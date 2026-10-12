using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>En rad i medlemskatalogen — det listor och väljare behöver, aldrig ett helt <c>IMember</c>.</summary>
    public sealed class MemberDirectoryEntry
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Email { get; set; } = "";
        public string FirstName { get; set; } = "";
        public string LastName { get; set; } = "";
        public string Phone { get; set; } = "";
        public string ProfilePictureUrl { get; set; } = "";
        public string PrimaryClubIdRaw { get; set; } = "";
        public string MemberClubIdsRaw { get; set; } = "";
        public bool IsApproved { get; set; }
        public bool IsLockedOut { get; set; }

        public int? PrimaryClubId => int.TryParse(PrimaryClubIdRaw.Trim(), out var id) && id > 0 ? id : null;

        /// <summary>Huvudklubben och övriga klubbar, utan dubbletter.</summary>
        public IReadOnlyList<int> ClubIds => ClubMembershipSyncHandler.ClubIds(PrimaryClubIdRaw, MemberClubIdsRaw);
    }

    /// <summary>
    /// Hela medlemsregistret som en lätt projektion i EN SQL-fråga (skalplanen, 2026-10-12).
    ///
    /// <para><b>Varför:</b> sajtadmins medlemslista och "kan bli admin" söker i HELA registret, så de
    /// kan inte gå via ett klubbregister. De läste förut alla medlemmar som fullständiga <c>IMember</c>
    /// (<c>GetAll(0, int.MaxValue)</c>) — 4 s vid 20 000 medlemmar, även när svaret var 50 rader. Här
    /// läses bara de sju egenskaper listorna visar. Filtrering, sortering och sidindelning sker på
    /// projektionen; ingen <c>IMember</c> laddas alls.</para>
    ///
    /// <para>Klubbar som är lagrade som medlemmar (<c>hpskClub</c>, gammal modell) är bortfiltrerade.</para>
    /// </summary>
    public class MemberDirectoryService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public MemberDirectoryService(IUmbracoDatabaseFactory databaseFactory) => _databaseFactory = databaseFactory;

        public List<MemberDirectoryEntry> All()
        {
            using var db = _databaseFactory.CreateDatabase();
            // ⚠️ Till en ren radklass först: NPoco genererar IL för mappningen och gav "Common Language
            // Runtime detected an invalid program" på klassen med beräknade egenskaper (mätt 2026-10-12).
            return db.Fetch<Row>(Sql).Select(r => new MemberDirectoryEntry
            {
                MemberId = r.MemberId,
                Name = r.Name ?? "",
                Email = r.Email ?? "",
                FirstName = r.FirstName ?? "",
                LastName = r.LastName ?? "",
                Phone = r.Phone ?? "",
                ProfilePictureUrl = r.ProfilePictureUrl ?? "",
                PrimaryClubIdRaw = r.PrimaryClubIdRaw ?? "",
                MemberClubIdsRaw = r.MemberClubIdsRaw ?? "",
                IsApproved = r.IsApproved,
                IsLockedOut = r.IsLockedOut
            }).ToList();
        }

        private sealed class Row
        {
            public int MemberId { get; set; }
            public string? Name { get; set; }
            public string? Email { get; set; }
            public string? FirstName { get; set; }
            public string? LastName { get; set; }
            public string? Phone { get; set; }
            public string? ProfilePictureUrl { get; set; }
            public string? PrimaryClubIdRaw { get; set; }
            public string? MemberClubIdsRaw { get; set; }
            public bool IsApproved { get; set; }
            public bool IsLockedOut { get; set; }
        }

        private const string Sql = @"
SELECT m.nodeId AS MemberId,
       n.text AS Name,
       ISNULL(m.Email, '') AS Email,
       CAST(m.isApproved AS BIT) AS IsApproved,
       CAST(m.isLockedOut AS BIT) AS IsLockedOut,
       ISNULL(MAX(CASE WHEN pt.alias = 'firstName' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS FirstName,
       ISNULL(MAX(CASE WHEN pt.alias = 'lastName' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS LastName,
       ISNULL(MAX(CASE WHEN pt.alias = 'phoneNumber' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS Phone,
       ISNULL(MAX(CASE WHEN pt.alias = 'profilePictureUrl' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) END), '') AS ProfilePictureUrl,
       ISNULL(MAX(CASE WHEN pt.alias = 'primaryClubId' THEN COALESCE(CAST(pd.intValue AS NVARCHAR(20)), pd.varcharValue, CAST(pd.textValue AS NVARCHAR(40))) END), '') AS PrimaryClubIdRaw,
       ISNULL(MAX(CASE WHEN pt.alias = 'memberClubIds' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) END), '') AS MemberClubIdsRaw
  FROM cmsMember m
  JOIN umbracoNode n     ON n.id = m.nodeId AND n.trashed = 0
  JOIN umbracoContent c  ON c.nodeId = m.nodeId
  JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias <> 'hpskClub'
  LEFT JOIN umbracoContentVersion cv ON cv.nodeId = m.nodeId AND cv.[current] = 1
  LEFT JOIN umbracoPropertyData pd   ON pd.versionId = cv.id
  LEFT JOIN cmsPropertyType pt       ON pt.id = pd.propertyTypeId
       AND pt.alias IN ('firstName', 'lastName', 'phoneNumber', 'profilePictureUrl', 'primaryClubId', 'memberClubIds')
 GROUP BY m.nodeId, n.text, m.Email, m.isApproved, m.isLockedOut";
    }
}
