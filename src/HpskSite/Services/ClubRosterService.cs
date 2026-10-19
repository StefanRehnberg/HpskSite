using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>En medlem i en klubbs register — en lätt projektion, aldrig ett helt <c>IMember</c>.</summary>
    public sealed class ClubRosterEntry
    {
        public int MemberId { get; set; }
        /// <summary>Klubben raden avser (en medlem i flera av klubbarna har en rad per klubb).</summary>
        public int ClubId { get; set; }
        public string Name { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Email { get; set; } = "";
        public string Phone { get; set; } = "";
        public string ShooterIdNumber { get; set; } = "";
        public string ProfilePictureUrl { get; set; } = "";
        public bool IsApproved { get; set; }
        /// <summary>Klubben är medlemmens huvudklubb (<c>primaryClubId</c>).</summary>
        public bool IsPrimary { get; set; }
        public string MembershipStatus { get; set; } = "";
        // umbracoNode.createDate är UTC i Umbraco 17; resten av sajten räknar i lokal tid.
        private DateTime _createDate;
        public DateTime CreateDate { get => _createDate; set => _createDate = UmbracoTime.FromDbUtc(value); }
        /// <summary>Råvärdet av <c>primaryClubId</c> (strängegenskap).</summary>
        public string PrimaryClubIdRaw { get; set; } = "";
        /// <summary>Råvärdet av <c>memberClubIds</c> (CSV).</summary>
        public string MemberClubIdsRaw { get; set; } = "";

        public int? PrimaryClubId => int.TryParse(PrimaryClubIdRaw.Trim(), out var id) && id > 0 ? id : null;

        public List<int> AdditionalClubIds => MemberClubIdsRaw
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var id) ? id : 0)
            .Where(id => id > 0)
            .ToList();
    }

    /// <summary>
    /// <b>EN</b> plats för "vilka är medlemmar i klubb X" (skalplanen B3, 2026-10-12).
    ///
    /// <para><b>⚠️⚠️ Läs aldrig hela medlemsregistret för att svara på en fråga om en klubb.</b>
    /// <c>_memberService.GetAll(0, int.MaxValue)</c> + filter på <c>primaryClubId</c>/<c>memberClubIds</c>
    /// tog 3,6–4,1 s per anrop vid 20 000 medlemmar (mätt 2026-10-12) och växer rakt med registret.
    /// Den här tjänsten frågar <c>ClubMembership</c> och läser bara klubbens egna medlemmars
    /// egenskaper, i en SQL-fråga.</para>
    ///
    /// <para><b>Vem som räknas:</b> en rad i <c>ClubMembership</c> för klubben med status annan än
    /// <c>Utträdd</c>/<c>Avliden</c>. Raderna hålls i takt med medlemmens klubbar av
    /// <see cref="ClubMembershipSyncHandler"/> (skapas vid sparning, Utträdd när klubben tas bort).
    /// En utträdd medlem syns alltså inte längre i klubbens listor — det är vad "Avsluta medlemskap"
    /// alltid har lovat.</para>
    /// </summary>
    public class ClubRosterService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public ClubRosterService(IUmbracoDatabaseFactory databaseFactory) => _databaseFactory = databaseFactory;

        /// <summary>Klubbens aktiva medlemmar (godkända och ej godkända), sorterade på namn.</summary>
        public List<ClubRosterEntry> Active(int clubId)
        {
            if (clubId <= 0) return new List<ClubRosterEntry>();
            return ActiveInClubs(new[] { clubId });
        }

        /// <summary>
        /// Aktiva medlemmar i flera klubbar på en gång (t.ex. kretsens klubbar), en rad per (medlem, klubb).
        /// Bara huvudklubben: filtrera på <see cref="ClubRosterEntry.IsPrimary"/>.
        /// </summary>
        public List<ClubRosterEntry> ActiveInClubs(IEnumerable<int> clubIds)
        {
            var ids = clubIds.Where(id => id > 0).Distinct().ToList();
            var result = new List<ClubRosterEntry>();
            if (ids.Count == 0) return result;
            using var db = _databaseFactory.CreateDatabase();
            foreach (var chunk in ids.Chunk(1000))
                result.AddRange(db.Fetch<ClubRosterEntry>(Sql, chunk, "Utträdd", "Avliden"));
            return result;
        }

        /// <summary>Bara medlems-id:na — för frågor som går vidare till andra tabeller.</summary>
        public List<int> ActiveMemberIds(int clubId)
        {
            if (clubId <= 0) return new List<int>();
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>(
                @"SELECT cm.MemberId FROM ClubMembership cm
                   WHERE cm.ClubId = @0 AND cm.MembershipStatus NOT IN (@1, @2)", clubId, "Utträdd", "Avliden");
        }

        /// <summary>
        /// Medlemmar utan någon aktiv klubb — den självregistrerade som aldrig valde klubb, eller den som
        /// gått ur alla sina. Klubbar lagrade som medlemmar (<c>hpskClub</c>) räknas inte.
        /// </summary>
        public List<int> ClublessMemberIds()
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>(
                @"SELECT m.nodeId FROM cmsMember m
                    JOIN umbracoNode n     ON n.id = m.nodeId AND n.trashed = 0
                    JOIN umbracoContent c  ON c.nodeId = m.nodeId
                    JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias <> 'hpskClub'
                   WHERE NOT EXISTS (SELECT 1 FROM ClubMembership cm
                                      WHERE cm.MemberId = m.nodeId AND cm.MembershipStatus NOT IN (@0, @1))",
                "Utträdd", "Avliden");
        }

        /// <summary>
        /// Klubbens aktiva medlemmar som fullständiga <c>IMember</c> — för ytor som behöver många egenskaper.
        /// Läser BARA klubbens medlemmar, i bitar om 1 000 (⚠️ <c>IN (@0)</c> tar slut kring 2 100
        /// parametrar, och gör det tyst).
        /// </summary>
        public List<Umbraco.Cms.Core.Models.IMember> LoadActiveMembers(Umbraco.Cms.Core.Services.IMemberService memberService, int clubId)
        {
            var ids = ActiveMemberIds(clubId);
            var result = new List<Umbraco.Cms.Core.Models.IMember>(ids.Count);
            foreach (var chunk in ids.Chunk(1000))
                result.AddRange(memberService.GetAllMembers(chunk));
            return result;
        }

        private const string Sql = @"
SELECT cm.MemberId,
       cm.ClubId,
       n.text AS Name,
       n.createDate AS CreateDate,
       ISNULL(m.Email, '') AS Email,
       CAST(m.isApproved AS BIT) AS IsApproved,
       cm.MembershipStatus,
       ISNULL(MAX(CASE WHEN pt.alias = 'firstName' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS FirstName,
       ISNULL(MAX(CASE WHEN pt.alias = 'lastName' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS LastName,
       ISNULL(MAX(CASE WHEN pt.alias = 'phoneNumber' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS Phone,
       ISNULL(MAX(CASE WHEN pt.alias = 'shooterIdNumber' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS ShooterIdNumber,
       ISNULL(MAX(CASE WHEN pt.alias = 'profilePictureUrl' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) END), '') AS ProfilePictureUrl,
       ISNULL(MAX(CASE WHEN pt.alias = 'primaryClubId' THEN COALESCE(CAST(pd.intValue AS NVARCHAR(20)), pd.varcharValue, CAST(pd.textValue AS NVARCHAR(40))) END), '') AS PrimaryClubIdRaw,
       ISNULL(MAX(CASE WHEN pt.alias = 'memberClubIds' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) END), '') AS MemberClubIdsRaw,
       CAST(MAX(CASE WHEN pt.alias = 'primaryClubId'
                      AND COALESCE(CAST(pd.intValue AS NVARCHAR(20)), pd.varcharValue, CAST(pd.textValue AS NVARCHAR(40))) = CAST(cm.ClubId AS NVARCHAR(20))
                     THEN 1 ELSE 0 END) AS BIT) AS IsPrimary
  FROM ClubMembership cm
  JOIN cmsMember m  ON m.nodeId = cm.MemberId
  JOIN umbracoNode n ON n.id = cm.MemberId AND n.trashed = 0
  LEFT JOIN umbracoContentVersion cv ON cv.nodeId = cm.MemberId AND cv.[current] = 1
  LEFT JOIN umbracoPropertyData pd ON pd.versionId = cv.id
  LEFT JOIN cmsPropertyType pt ON pt.id = pd.propertyTypeId
       AND pt.alias IN ('firstName', 'lastName', 'phoneNumber', 'shooterIdNumber', 'profilePictureUrl', 'primaryClubId', 'memberClubIds')
 WHERE cm.ClubId IN (@0) AND cm.MembershipStatus NOT IN (@1, @2)
 GROUP BY cm.MemberId, cm.ClubId, n.text, n.createDate, m.Email, m.isApproved, cm.MembershipStatus
 ORDER BY n.text";
    }
}
