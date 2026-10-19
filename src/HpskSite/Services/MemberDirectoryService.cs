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

        /// <summary>Förnamn + efternamn, annars nodens namn.</summary>
        public string DisplayName
        {
            get
            {
                var full = $"{FirstName} {LastName}".Trim();
                return full.Length > 0 ? full : Name;
            }
        }

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
            return db.Fetch<Row>(Sql).Select(ToEntry).ToList();
        }

        /// <summary>
        /// Sökning på namn eller e-post i SQL (skalplanen B, 2026-10-19) — för väljare och sökrutor.
        /// Ersätter <c>GetAll(0, int.MaxValue).Where(namn/e-post innehåller).Take(n)</c>, som byggde hela
        /// registret för att svara med 20 rader. Sorterat på namn.
        /// </summary>
        /// <param name="clubId">Bara klubbens aktiva medlemmar (<c>ClubMembership</c>, samma regel som
        /// <see cref="ClubRosterService"/>).</param>
        /// <param name="searchNames">Sök även i förnamn och efternamn (inte bara nodens namn och e-post).</param>
        public List<MemberDirectoryEntry> Search(string? query, int take, int? clubId = null, bool approvedOnly = true, bool searchNames = false)
        {
            var q = (query ?? "").Trim();
            if (take <= 0) return new List<MemberDirectoryEntry>();
            var like = "%" + q.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]") + "%";

            // Tom fråga = de första <take> på namn (väljare som visar en lista innan något skrivits).
            var where = new List<string> { q.Length == 0 ? "1 = 1" : "(n.text LIKE @0 OR m.Email LIKE @0" + (searchNames ? NameMatch : "") + ")" };
            if (approvedOnly) where.Add("m.isApproved = 1");
            if (clubId.HasValue)
                where.Add("EXISTS (SELECT 1 FROM ClubMembership cm WHERE cm.MemberId = m.nodeId AND cm.ClubId = @1 AND cm.MembershipStatus NOT IN (@2, @3))");

            var sql = SelectTop + " " + From + " WHERE " + string.Join(" AND ", where) + " " + GroupBy + " ORDER BY n.text";
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<Row>(sql.Replace("@take", take.ToString()), like, clubId ?? 0, "Utträdd", "Avliden").Select(ToEntry).ToList();
        }

        private static MemberDirectoryEntry ToEntry(Row r) => new()
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
        };

        private const string NameMatch = @"
            OR EXISTS (SELECT 1 FROM umbracoContentVersion sv
                         JOIN umbracoPropertyData sd ON sd.versionId = sv.id
                         JOIN cmsPropertyType st ON st.id = sd.propertyTypeId AND st.alias IN ('firstName', 'lastName')
                        WHERE sv.nodeId = m.nodeId AND sv.[current] = 1 AND sd.varcharValue LIKE @0)";

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

        // ⚠️ Frågan MÅSTE börja med SELECT — annars lägger NPoco till "SELECT * FROM" själv.
        private const string Sql = "SELECT " + Columns + " " + From + " " + GroupBy;
        private const string SelectTop = "SELECT TOP (@take) " + Columns;
        private const string GroupBy = "GROUP BY m.nodeId, n.text, m.Email, m.isApproved, m.isLockedOut";

        private const string Columns = @"
       m.nodeId AS MemberId,
       n.text AS Name,
       ISNULL(m.Email, '') AS Email,
       CAST(m.isApproved AS BIT) AS IsApproved,
       CAST(m.isLockedOut AS BIT) AS IsLockedOut,
       ISNULL(MAX(CASE WHEN pt.alias = 'firstName' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS FirstName,
       ISNULL(MAX(CASE WHEN pt.alias = 'lastName' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS LastName,
       ISNULL(MAX(CASE WHEN pt.alias = 'phoneNumber' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(400))) END), '') AS Phone,
       ISNULL(MAX(CASE WHEN pt.alias = 'profilePictureUrl' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) END), '') AS ProfilePictureUrl,
       ISNULL(MAX(CASE WHEN pt.alias = 'primaryClubId' THEN COALESCE(CAST(pd.intValue AS NVARCHAR(20)), pd.varcharValue, CAST(pd.textValue AS NVARCHAR(40))) END), '') AS PrimaryClubIdRaw,
       ISNULL(MAX(CASE WHEN pt.alias = 'memberClubIds' THEN COALESCE(pd.varcharValue, CAST(pd.textValue AS NVARCHAR(1000))) END), '') AS MemberClubIdsRaw";

        private const string From = @"
  FROM cmsMember m
  JOIN umbracoNode n     ON n.id = m.nodeId AND n.trashed = 0
  JOIN umbracoContent c  ON c.nodeId = m.nodeId
  JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias <> 'hpskClub'
  LEFT JOIN umbracoContentVersion cv ON cv.nodeId = m.nodeId AND cv.[current] = 1
  LEFT JOIN umbracoPropertyData pd   ON pd.versionId = cv.id
  LEFT JOIN cmsPropertyType pt       ON pt.id = pd.propertyTypeId
       AND pt.alias IN ('firstName', 'lastName', 'phoneNumber', 'profilePictureUrl', 'primaryClubId', 'memberClubIds')";
    }
}
