using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>En medlem med just de egenskaper anroparen bad om — aldrig ett helt <c>IMember</c>.</summary>
    public sealed class MemberPropertyRow
    {
        public int Id { get; init; }
        /// <summary>Medlemsnodens namn (<c>IMember.Name</c>).</summary>
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
        public bool IsApproved { get; init; }
        /// <summary>Kontots skapandedag i lokal tid (<c>umbracoNode.createDate</c> är UTC i Umbraco 17).</summary>
        public DateTime CreateDate { get; init; }
        internal Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Egenskapens värde som text, som <c>GetValue&lt;string&gt;</c> ger det: heltal som "123",
        /// datum som "yyyy-MM-dd HH:mm:ss". Tom sträng när värdet saknas — aldrig null.
        /// </summary>
        public string Get(string alias) => Values.TryGetValue(alias, out var v) ? v : "";

        /// <summary><c>primaryClubId</c> som tal, eller 0. ⚠️ Egenskapen är en STRÄNG i medlemstypen.</summary>
        public int PrimaryClubId => int.TryParse(Get("primaryClubId").Trim(), out var id) && id > 0 ? id : 0;

        /// <summary>Förnamn + efternamn, annars nodens namn.</summary>
        public string DisplayName
        {
            get
            {
                var full = $"{Get("firstName")} {Get("lastName")}".Trim();
                return full.Length > 0 ? full : Name;
            }
        }
    }

    /// <summary>
    /// Läser medlemmar med ett valt urval egenskaper i två SQL-frågor (skalplanen B, 2026-10-19).
    ///
    /// <para><b>Varför:</b> <c>_memberService.GetAll(0, int.MaxValue)</c> bygger varje medlem som ett
    /// fullständigt <c>IMember</c> med alla ~30 egenskaper, ~600 000 egenskapsrader vid 20 000
    /// medlemmar. Statistik och listor över hela registret behöver två till fyra av dem. Här läses
    /// bara de efterfrågade egenskaperna, och inga objekt byggs utöver raden.</para>
    ///
    /// <para>Klubbar lagrade som medlemmar (<c>hpskClub</c>, gammal modell) är alltid bortfiltrerade.
    /// Gäller det en klubbs medlemmar: använd <see cref="ClubRosterService"/>. Gäller det en sökning
    /// på namn: <see cref="MemberDirectoryService.Search"/>.</para>
    /// </summary>
    public class MemberPropertyReader
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public MemberPropertyReader(IUmbracoDatabaseFactory databaseFactory) => _databaseFactory = databaseFactory;

        /// <summary>Hela registret (eller de angivna medlemmarna) med egenskaperna <paramref name="aliases"/>.</summary>
        /// <param name="approvedOnly">Bara godkända medlemmar.</param>
        /// <param name="unapprovedOnly">Bara medlemmar som väntar på godkännande.</param>
        public List<MemberPropertyRow> Read(IEnumerable<string> aliases, IEnumerable<int>? memberIds = null,
            bool approvedOnly = false, bool unapprovedOnly = false)
        {
            var aliasList = aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var idList = memberIds?.Distinct().ToList();
            if (idList is { Count: 0 }) return new List<MemberPropertyRow>();

            using var db = _databaseFactory.CreateDatabase();
            var rows = new Dictionary<int, MemberPropertyRow>();
            var filter = (approvedOnly ? " AND m.isApproved = 1" : "") + (unapprovedOnly ? " AND m.isApproved = 0" : "");

            // ⚠️ IN (@0) tar slut kring 2 100 parametrar och gör det tyst — därför bitar om 1 000.
            var bases = new List<BaseRow>();
            if (idList == null) bases.AddRange(db.Fetch<BaseRow>(BaseSql + filter + " ORDER BY m.nodeId"));
            else foreach (var chunk in idList.Chunk(1000)) bases.AddRange(db.Fetch<BaseRow>(BaseSql + filter + " AND m.nodeId IN (@0) ORDER BY m.nodeId", chunk));

            foreach (var b in bases)
                rows[b.MemberId] = new MemberPropertyRow
                {
                    Id = b.MemberId,
                    Name = b.Name ?? "",
                    Email = b.Email ?? "",
                    IsApproved = b.IsApproved,
                    // Kind = Local, som IMember.CreateDate genom UmbracoTime.Local — JSON får då samma offset som förut.
                    CreateDate = DateTime.SpecifyKind(UmbracoTime.FromDbUtc(b.CreateDate), DateTimeKind.Local)
                };
            if (aliasList.Count == 0 || rows.Count == 0) return rows.Values.ToList();

            // Få medlemmar: värdena för just dem. Många (hela registret): en genomläsning av egenskapen.
            var values = new List<ValueRow>();
            if (rows.Count > 5000) values.AddRange(db.Fetch<ValueRow>(ValueSql, aliasList));
            else foreach (var chunk in rows.Keys.Chunk(1000)) values.AddRange(db.Fetch<ValueRow>(ValueSql + " AND cv.nodeId IN (@1)", aliasList, chunk));
            foreach (var v in values)
                if (v.Value != null && rows.TryGetValue(v.MemberId, out var row))
                    row.Values[v.Alias] = v.Value;

            return rows.Values.ToList();
        }

        /// <summary>
        /// Medlemmar där egenskapen <paramref name="alias"/> har ett värde — t.ex. de som börjat
        /// Skyttetrappan (<c>trainingStartDate</c>). För ytor som sedan behöver hela <c>IMember</c> för
        /// just dem: hämta med <c>GetAllMembers(ids)</c> i bitar, aldrig hela registret.
        /// </summary>
        public List<int> IdsWithValue(string alias, bool approvedOnly = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<int>(@"
SELECT DISTINCT m.nodeId
  FROM cmsMember m
  JOIN umbracoNode n     ON n.id = m.nodeId AND n.trashed = 0
  JOIN umbracoContent c  ON c.nodeId = m.nodeId
  JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias <> 'hpskClub'
  JOIN umbracoContentVersion cv ON cv.nodeId = m.nodeId AND cv.[current] = 1
  JOIN umbracoPropertyData pd   ON pd.versionId = cv.id
  JOIN cmsPropertyType pt       ON pt.id = pd.propertyTypeId AND pt.alias = @0
 WHERE (pd.intValue IS NOT NULL OR pd.dateValue IS NOT NULL OR pd.decimalValue IS NOT NULL
        OR NULLIF(pd.varcharValue, '') IS NOT NULL OR pd.textValue IS NOT NULL)" + (approvedOnly ? " AND m.isApproved = 1" : ""), alias);
        }

        private sealed class BaseRow
        {
            public int MemberId { get; set; }
            public string? Name { get; set; }
            public string? Email { get; set; }
            public bool IsApproved { get; set; }
            public DateTime CreateDate { get; set; }
        }

        private sealed class ValueRow
        {
            public int MemberId { get; set; }
            public string Alias { get; set; } = "";
            public string? Value { get; set; }
        }

        private const string BaseSql = @"
SELECT m.nodeId AS MemberId, n.text AS Name, ISNULL(m.Email, '') AS Email,
       CAST(m.isApproved AS BIT) AS IsApproved, n.createDate AS CreateDate
  FROM cmsMember m
  JOIN umbracoNode n     ON n.id = m.nodeId AND n.trashed = 0
  JOIN umbracoContent c  ON c.nodeId = m.nodeId
  JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId AND ct.alias <> 'hpskClub'
 WHERE 1 = 1";

        // ⚠️ Värdet ur den kolumn egenskapen faktiskt lagras i: primaryClubId ligger i intValue i prod,
        // datum i dateValue. En läsning av bara varcharValue gav NULL och lästes som "saknas".
        private const string ValueSql = @"
SELECT cv.nodeId AS MemberId, pt.alias AS Alias,
       COALESCE(CAST(pd.intValue AS NVARCHAR(20)),
                pd.varcharValue,
                CAST(pd.textValue AS NVARCHAR(MAX)),
                CONVERT(NVARCHAR(30), pd.dateValue, 120),
                CAST(pd.decimalValue AS NVARCHAR(40))) AS Value
  FROM umbracoContentVersion cv
  JOIN cmsMember mm           ON mm.nodeId = cv.nodeId
  JOIN umbracoPropertyData pd ON pd.versionId = cv.id
  JOIN cmsPropertyType pt     ON pt.id = pd.propertyTypeId
 WHERE cv.[current] = 1 AND pt.alias IN (@0)";
    }
}
