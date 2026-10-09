using HpskSite.Models;
using HpskSite.Models.Registrations;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// Anmälningarna som Umbraco-noder: <c>competition › competitionRegistrationsHub ›
    /// competitionRegistration</c>. Samlar det som låg i fyra kopior av
    /// <c>GetOrCreateRegistrationsHub</c> och i ett fyrtiotal handskrivna trädvandringar.
    ///
    /// <para><b>Tre medvetna beslut:</b></para>
    /// <list type="number">
    /// <item><b>Läser innehållstjänsten, aldrig den publicerade cachen</b> — se gränssnittets huvud.</item>
    /// <item><b>Publicerar aldrig en anmälan.</b> Publiceringen fyllde ingen funktion när alla läsare
    /// går hit, den gjorde anmälningarna nåbara via sin trädadress (fas S), och varje publicering
    /// tog Umbracos globala skrivlås en gång till. Befintliga publicerade noder avpubliceras vid
    /// migreringen.</item>
    /// <item><b>Skriver bara de egenskaper som faktiskt ändrats.</b> En anmälan bär egenskaper som
    /// ingen kod skriver (memberEmail, notes, shooterNotes); att skriva alla vid varje sparning hade
    /// riskerat att nolla dem.</item>
    /// </list>
    ///
    /// <para><b>Hubben skapas opublicerad</b> (Stefans beslut 2026-09-02): dess mall listade
    /// anmälningarna utan behörighetskontroll.</para>
    /// </summary>
    public class NodeRegistrationStore : ICompetitionRegistrationStore
    {
        public const string RegistrationAlias = "competitionRegistration";
        public const string HubAlias = "competitionRegistrationsHub";

        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<NodeRegistrationStore> _logger;

        public NodeRegistrationStore(
            IContentService contentService,
            IContentTypeService contentTypeService,
            IScopeProvider scopeProvider,
            ILogger<NodeRegistrationStore> logger)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<RegistrationRecord> List(int competitionId)
        {
            if (competitionId <= 0) return Array.Empty<RegistrationRecord>();
            var competition = _contentService.GetById(competitionId);
            if (competition is null) return Array.Empty<RegistrationRecord>();

            return RegistrationNodes(competition)
                .Select(n => Map(n, competitionId))
                .ToList();
        }

        public RegistrationRecord? Get(int competitionId, int registrationId)
        {
            var record = GetById(registrationId);
            return record is not null && record.CompetitionId == competitionId ? record : null;
        }

        public RegistrationRecord? GetById(int registrationId)
        {
            if (registrationId <= 0) return null;
            var node = _contentService.GetById(registrationId);
            if (node is null || node.Trashed || node.ContentType.Alias != RegistrationAlias) return null;
            return Map(node, CompetitionIdOf(node));
        }

        public RegistrationRecord? FindForMember(int competitionId, int memberId)
            => ListForMemberInCompetition(competitionId, memberId).FirstOrDefault();

        public IReadOnlyList<RegistrationRecord> ListForMemberInCompetition(int competitionId, int memberId)
        {
            if (memberId <= 0) return Array.Empty<RegistrationRecord>();
            return List(competitionId).Where(r => r.MemberId == memberId).ToList();
        }

        public IReadOnlyList<RegistrationRecord> ListForMember(int memberId)
        {
            if (memberId <= 0) return Array.Empty<RegistrationRecord>();

            // Nod-id:na ur egenskapstabellen, i EN fråga — att vandra varje tävlings hubb för en
            // enda medlem vore en trädvandring per tävling på sajten.
            List<int> ids;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                ids = db.Fetch<int>(@"
SELECT DISTINCT n.id
FROM umbracoNode n
JOIN umbracoContent c        ON c.nodeId = n.id
JOIN cmsContentType ct       ON ct.nodeId = c.contentTypeId
JOIN umbracoContentVersion v ON v.nodeId = n.id AND v.[current] = 1
JOIN umbracoPropertyData pd  ON pd.versionId = v.id
JOIN cmsPropertyType pt      ON pt.id = pd.propertyTypeId
WHERE ct.alias = @0 AND n.trashed = 0 AND pt.Alias = 'memberId'
  AND (pd.intValue = @1 OR LTRIM(RTRIM(pd.varcharValue)) = @2)", RegistrationAlias, memberId, memberId.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlem {MemberId}s anmälningar kunde inte läsas", memberId);
                return Array.Empty<RegistrationRecord>();
            }

            return ids.Count == 0
                ? Array.Empty<RegistrationRecord>()
                : _contentService.GetByIds(ids)
                    .Where(n => !n.Trashed && n.ContentType.Alias == RegistrationAlias)
                    .Select(n => Map(n, CompetitionIdOf(n)))
                    .Where(r => r.MemberId == memberId)
                    .ToList();
        }

        public IReadOnlyList<(int MemberId, int CompetitionId)> CompetitionsForMembers(IReadOnlyCollection<int> memberIds)
        {
            var result = new List<(int, int)>();
            var ids = memberIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return result;
            try
            {
                // I portioner: IN (@0) blir en parameter per id, och SQL Server tar slut vid ~2100.
                foreach (var batch in ids.Chunk(1000))
                {
                    var inList = string.Join(",", batch.Select((_, i) => "@" + (i + 1)));
                    var args = new object[] { RegistrationAlias }.Concat(batch.Cast<object>()).ToArray();
                    using var scope = _scopeProvider.CreateScope(autoComplete: true);
                    var rows = scope.Database.Fetch<PairRow>($@"
SELECT DISTINCT
       COALESCE(pd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20))) AS MemberId,
       COALESCE(cd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(cd.varcharValue, cd.textValue), 20))) AS CompetitionId
FROM umbracoNode n
JOIN umbracoContent c          ON c.nodeId = n.id
JOIN cmsContentType ct         ON ct.nodeId = c.contentTypeId AND ct.alias = @0
JOIN umbracoContentVersion cv  ON cv.nodeId = n.id AND cv.[current] = 1
JOIN cmsPropertyType pt        ON pt.contentTypeId = ct.nodeId AND pt.Alias = 'memberId'
JOIN umbracoPropertyData pd    ON pd.versionId = cv.id AND pd.propertyTypeId = pt.id
JOIN cmsPropertyType ctp       ON ctp.contentTypeId = ct.nodeId AND ctp.Alias = 'competitionId'
JOIN umbracoPropertyData cd    ON cd.versionId = cv.id AND cd.propertyTypeId = ctp.id
WHERE n.trashed = 0
  AND COALESCE(pd.intValue, TRY_CONVERT(INT, LEFT(COALESCE(pd.varcharValue, pd.textValue), 20))) IN ({inList})", args);
                    result.AddRange(rows.Where(r => r.CompetitionId > 0).Select(r => (r.MemberId, r.CompetitionId)));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlemmarnas anmälningar kunde inte läsas");
            }
            return result;
        }

        private sealed class PairRow
        {
            public int MemberId { get; set; }
            public int CompetitionId { get; set; }
        }

        public IReadOnlyDictionary<int, int> CountByCompetition(IReadOnlyCollection<int>? competitionIds = null)
        {
            var result = new Dictionary<int, int>();
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                // Föräldern till anmälans förälder är tävlingen (hubben ligger emellan). En anmälan som
                // ligger DIREKT under tävlingen (äldre reservväg när hubben inte gick att skapa) räknas
                // via sin egen förälder.
                var rows = db.Fetch<CountRow>(@"
SELECT CASE WHEN hubType.alias = @1 OR hub.text LIKE N'%Anm%lningar%' OR hub.text LIKE N'%Registration%'
            THEN hub.parentId ELSE hub.id END AS CompetitionId,
       COUNT(*) AS Cnt
FROM umbracoNode reg
JOIN umbracoContent c   ON c.nodeId = reg.id
JOIN cmsContentType ct  ON ct.nodeId = c.contentTypeId
JOIN umbracoNode hub    ON hub.id = reg.parentId
JOIN umbracoContent hc  ON hc.nodeId = hub.id
JOIN cmsContentType hubType ON hubType.nodeId = hc.contentTypeId
WHERE ct.alias = @0 AND reg.trashed = 0
GROUP BY CASE WHEN hubType.alias = @1 OR hub.text LIKE N'%Anm%lningar%' OR hub.text LIKE N'%Registration%'
              THEN hub.parentId ELSE hub.id END", RegistrationAlias, HubAlias);
                foreach (var r in rows)
                    if (competitionIds is null || competitionIds.Contains(r.CompetitionId))
                        result[r.CompetitionId] = r.Cnt;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Antalet anmälningar per tävling kunde inte läsas");
            }
            return result;
        }

        private sealed class CountRow
        {
            public int CompetitionId { get; set; }
            public int Cnt { get; set; }
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public int Save(RegistrationRecord r)
        {
            ArgumentNullException.ThrowIfNull(r);
            IContent node;
            bool isNew = r.Id <= 0;

            if (isNew)
            {
                var competition = _contentService.GetById(r.CompetitionId)
                    ?? throw new InvalidOperationException($"Tävling {r.CompetitionId} finns inte.");
                var parent = GetOrCreateHub(competition);
                var date = r.RegistrationDate == default ? DateTime.Now : r.RegistrationDate;
                node = _contentService.Create($"{r.MemberName} - {date:yyyy-MM-dd}", parent, RegistrationAlias);

                // Det som bara sätts när anmälan skapas.
                node.SetValue("competitionId", r.CompetitionId);
                node.SetValue("memberId", r.MemberId);
                node.SetValue("memberName", r.MemberName);
                node.SetValue("isActive", r.IsActive);
                if (r.ClubId > 0) node.SetValue("clubId", r.ClubId);
                WriteClasses(node, r);
                node.SetValue("registrationDate", date);
                node.SetValue("registeredBy", r.RegisteredBy ?? "");
                SetIfExists(node, "isSubCompetition", r.IsSubCompetition);
                if (r.IsCheckedIn) SetIfExists(node, "isCheckedIn", true);
                if (r.InvoiceId is int inv && inv > 0) SetIfExists(node, "invoiceId", inv);
            }
            else
            {
                node = _contentService.GetById(r.Id)
                    ?? throw new InvalidOperationException($"Anmälan {r.Id} finns inte.");
                if (node.ContentType.Alias != RegistrationAlias || CompetitionIdOf(node) != r.CompetitionId)
                    throw new InvalidOperationException($"Anmälan {r.Id} hör inte till tävling {r.CompetitionId}.");

                ApplyChanges(node, r);
                try
                {
                    var saved = _contentService.Save(node);
                    if (!saved.Success)
                        throw new InvalidOperationException($"Anmälan för {r.MemberName} kunde inte sparas.");
                }
                catch (Exception ex) when (ex.Message.Contains("non-current", StringComparison.OrdinalIgnoreCase))
                {
                    // ⚠️ GetById kan lämna ut en cachad instans vars version inte längre är aktuell
                    // (en anmälan som den ivriga fakturavägen nyss rört), och Save kastar då
                    // "Cannot save a non-current version" — uppmätt i dev 2026-09-30. Den sidade
                    // frågan under föräldern ger alltid den aktuella versionen; läs om och försök en gång.
                    var fresh = _contentService.GetPagedChildren(node.ParentId, 0, int.MaxValue, out _)
                        .FirstOrDefault(c => c.Id == r.Id)
                        ?? throw new InvalidOperationException($"Anmälan {r.Id} finns inte.");
                    ApplyChanges(fresh, r);
                    if (!_contentService.Save(fresh).Success)
                        throw new InvalidOperationException($"Anmälan för {r.MemberName} kunde inte sparas.");
                }
                return r.Id;
            }

            var result = _contentService.Save(node);
            if (!result.Success)
                throw new InvalidOperationException($"Anmälan för {r.MemberName} kunde inte sparas.");

            r.Id = node.Id;
            return node.Id;
        }

        /// <summary>Skriver bara de egenskaper som skiljer sig från nodens nuvarande värden.</summary>
        private static void ApplyChanges(IContent node, RegistrationRecord r)
        {
            {
                var current = Map(node, r.CompetitionId);
                if (current.MemberId != r.MemberId) node.SetValue("memberId", r.MemberId);
                if (current.MemberName != r.MemberName) node.SetValue("memberName", r.MemberName);
                // 0 betyder "inte satt" — en klubb tas aldrig bort av en sparning som bara inte bar den.
                if (r.ClubId > 0 && current.ClubId != r.ClubId) node.SetValue("clubId", r.ClubId);
                if (!SameClasses(current, r)) WriteClasses(node, r);
                if (r.RegistrationDate != default && current.RegistrationDate != r.RegistrationDate)
                    node.SetValue("registrationDate", r.RegistrationDate);
                if ((current.RegisteredBy ?? "") != (r.RegisteredBy ?? "")) node.SetValue("registeredBy", r.RegisteredBy ?? "");
                if (current.IsActive != r.IsActive) node.SetValue("isActive", r.IsActive);
                if (current.IsSubCompetition != r.IsSubCompetition) SetIfExists(node, "isSubCompetition", r.IsSubCompetition);
                if (current.IsCheckedIn != r.IsCheckedIn) SetIfExists(node, "isCheckedIn", r.IsCheckedIn);
                if (current.InvoiceId != r.InvoiceId && r.InvoiceId is int inv) SetIfExists(node, "invoiceId", inv);
            }
        }

        public bool Delete(int competitionId, int registrationId)
        {
            var node = registrationId > 0 ? _contentService.GetById(registrationId) : null;
            if (node is null || node.ContentType.Alias != RegistrationAlias || CompetitionIdOf(node) != competitionId)
                return false;
            _contentService.Delete(node);
            return true;
        }

        // ── Nodformen ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Anmälningsnoderna under tävlingen: i hubben (eller hubbarna — äldre data kan ha en
        /// contentPage-hubb), och de som av en äldre reservväg hamnade direkt under tävlingen.
        /// </summary>
        private IEnumerable<IContent> RegistrationNodes(IContent competition)
        {
            foreach (var child in _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _))
            {
                if (child.ContentType.Alias == RegistrationAlias)
                {
                    yield return child;
                }
                else if (IsHub(child))
                {
                    foreach (var reg in _contentService.GetPagedChildren(child.Id, 0, int.MaxValue, out _))
                        if (reg.ContentType.Alias == RegistrationAlias && !reg.Trashed)
                            yield return reg;
                }
            }
        }

        /// <summary>Samma igenkänning som de fyra gamla kopiorna: hubbens doctype, eller ett namn
        /// som säger att det är hubben (äldre data skapad som contentPage).</summary>
        private static bool IsHub(IContent node)
            => node.ContentType.Alias == HubAlias
               || (node.Name?.Contains("Anmälningar") ?? false)
               || (node.Name?.Contains("Registration") ?? false);

        /// <summary>
        /// Hubben under tävlingen; skapas (opublicerad) när den saknas. Saknas hubbens doctype används
        /// contentPage, och går inte heller det returneras tävlingen själv — samma reservväg som förut.
        /// </summary>
        public IContent GetOrCreateHub(IContent competition)
        {
            var existing = _contentService.GetPagedChildren(competition.Id, 0, int.MaxValue, out _)
                .FirstOrDefault(IsHub);
            if (existing is not null) return existing;

            var type = _contentTypeService.Get(HubAlias) ?? _contentTypeService.Get("contentPage");
            if (type is null)
            {
                _logger.LogError("Ingen doctype för anmälningshubben — anmälan läggs direkt under tävling {Id}", competition.Id);
                return competition;
            }

            var hub = _contentService.Create("Anmälningar", competition, type.Alias);
            if (type.Alias == "contentPage")
            {
                hub.SetValue("pageTitle", "Anmälningar");
                hub.SetValue("bodyText", "<p>Alla anmälningar för denna tävling.</p>");
            }
            _contentService.Save(hub);
            return hub;
        }

        /// <summary>Tävlingens id: egenskapen, annars hubbens förälder.</summary>
        private int CompetitionIdOf(IContent node)
        {
            var fromProperty = ReadInt(node, "competitionId");
            if (fromProperty > 0) return fromProperty;
            var parent = node.ParentId > 0 ? _contentService.GetById(node.ParentId) : null;
            if (parent is null) return 0;
            return parent.ContentType.Alias == "competition" ? parent.Id : parent.ParentId;
        }

        private static RegistrationRecord Map(IContent n, int competitionId)
        {
            var record = new RegistrationRecord
            {
                Id = n.Id,
                CompetitionId = competitionId,
                MemberId = ReadInt(n, "memberId"),
                MemberName = n.GetValue<string>("memberName") ?? "",
                ClubId = ReadInt(n, "clubId"),
                LegacyClubText = n.GetValue<string>("memberClub") ?? "",
                RegistrationDate = n.GetValue<DateTime>("registrationDate"),
                CreatedDate = n.CreateDate,
                RegisteredBy = n.GetValue<string>("registeredBy") ?? "",
                // Saknat värde = aktiv, samma regel som CompetitionRegistrationDocument.IsActive.
                IsActive = n.GetValue("isActive") is null || n.GetValue<bool>("isActive"),
                IsSubCompetition = n.HasProperty("isSubCompetition") && n.GetValue<bool>("isSubCompetition"),
                IsCheckedIn = n.HasProperty("isCheckedIn") && n.GetValue<bool>("isCheckedIn"),
                InvoiceId = ReadInt(n, "invoiceId") is int inv && inv > 0 ? inv : null,
                MemberEmail = n.GetValue<string>("memberEmail") ?? "",
                Notes = n.GetValue<string>("notes") ?? "",
                ShooterNotes = n.GetValue<string>("shooterNotes") ?? ""
            };

            var classesJson = n.GetValue<string>("shootingClasses") ?? "";
            var classes = CompetitionRegistrationDocument.DeserializeShootingClasses(classesJson);
            if (classes.Count == 0 && !string.IsNullOrWhiteSpace(classesJson))
            {
                // ⚠️ Springskyttets äldre form är en array av STRÄNGAR (["C-H 50"]), som
                // DeserializeShootingClasses inte kan läsa och tyst svarar tomt på. ReadClassIds läser
                // båda formerna och alla skiftlägen av "class" — utan reserven försvann de skyttarna.
                classes = CompetitionRegistrationDocument.ReadClassIds(classesJson)
                    .Select(id => new ShootingClassEntry { Class = id })
                    .ToList();
            }
            if (classes.Count == 0)
            {
                var legacy = n.GetValue<string>("shootingClass");
                if (!string.IsNullOrWhiteSpace(legacy))
                {
                    classes.Add(new ShootingClassEntry
                    {
                        Class = legacy,
                        StartPreference = n.GetValue<string>("startPreference") ?? ""
                    });
                    record.ClassesFromLegacyScalar = true;
                }
            }
            record.Classes = classes;
            return record;
        }

        private static void WriteClasses(IContent node, RegistrationRecord r)
        {
            // En äldre anmälan med EN klass i de skalära egenskaperna skrivs tillbaka i samma form.
            if (r.ClassesFromLegacyScalar && r.Classes.Count == 1)
            {
                node.SetValue("shootingClass", r.Classes[0].Class);
                SetIfExists(node, "startPreference", r.Classes[0].StartPreference ?? "");
                return;
            }
            node.SetValue("shootingClasses", CompetitionRegistrationDocument.SerializeShootingClasses(r.Classes));
        }

        private static bool SameClasses(RegistrationRecord a, RegistrationRecord b)
            => a.ClassesFromLegacyScalar == b.ClassesFromLegacyScalar
               && a.Classes.Count == b.Classes.Count
               && a.Classes.Zip(b.Classes).All(p =>
                   p.First.Class == p.Second.Class
                   && (p.First.StartPreference ?? "") == (p.Second.StartPreference ?? "")
                   && p.First.TeamNumber == p.Second.TeamNumber);

        private static void SetIfExists(IContent node, string alias, object? value)
        {
            if (node.HasProperty(alias)) node.SetValue(alias, value);
        }

        /// <summary>Ett heltal ur en egenskap som kan vara lagrad som tal eller text.</summary>
        private static int ReadInt(IContent node, string alias)
        {
            var raw = node.GetValue(alias);
            return raw switch
            {
                null => 0,
                int i => i,
                long l => (int)l,
                decimal d => (int)d,
                _ => int.TryParse(raw.ToString()?.Trim(), out var parsed) ? parsed : 0
            };
        }
    }
}
