using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// C3-planen F5: seriens värd skrivs in i de omgångar som saknar BÅDE klubb och krets, en gång,
    /// efter en differenslista som Stefan godkänt (2026-10-13). Därefter gäller tävlingens egna värden —
    /// ingen ärvning vid körning.
    ///
    /// Regler:
    /// <list type="bullet">
    /// <item>Bara en omgång där <c>clubId</c> &lt;= 0 OCH <c>regionalFederation</c> är tom. En omgång med
    /// en egen värd rörs aldrig.</item>
    /// <item>Seriens klubb och krets skrivs som EN enhet (klubb 0 + krets X ger krets X).</item>
    /// <item>En tävling med liggarrader eller gamla fakturor hoppas över: utställaren fastställs vid första
    /// avgiften och flyttas aldrig.</item>
    /// <item>Bara de id:n anroparen skickar, och bara om de fortfarande står i förhandsgranskningen.</item>
    /// </list>
    /// Adressen byts (regionvärd ger kretsformen); Umbracos adresshistorik vidarekopplar den gamla.
    /// </summary>
    public sealed class CompetitionHostBackfillService
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IPublishedUrlProvider _urlProvider;
        private readonly IScopeProvider _scopeProvider;
        private readonly LegacyInvoiceHubs _legacyInvoices;
        private readonly ILogger<CompetitionHostBackfillService> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;

        public CompetitionHostBackfillService(IContentService contentService, IUmbracoContextAccessor umbracoContextAccessor,
            IPublishedUrlProvider urlProvider, IScopeProvider scopeProvider, LegacyInvoiceHubs legacyInvoices,
            ILogger<CompetitionHostBackfillService> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions)
        {
            _competitions = competitions;
            _contentService = contentService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _urlProvider = urlProvider;
            _scopeProvider = scopeProvider;
            _legacyInvoices = legacyInvoices;
            _logger = logger;
        }

        public sealed class Row
        {
            public int CompetitionId { get; init; }
            public string Name { get; init; } = "";
            public DateTime? Date { get; init; }
            public int SeriesId { get; init; }
            public string SeriesName { get; init; } = "";
            public int NewClubId { get; init; }
            public string NewRegion { get; init; } = "";
            public string OldUrl { get; init; } = "";
            /// <summary>Tomt = kan skrivas. Annars skälet att den hoppas över.</summary>
            public string Blocked { get; init; } = "";
        }

        /// <summary>Läser bara: omgångar utan egen värd i en serie som har en värd.</summary>
        public List<Row> Preview()
        {
            var rows = new List<Row>();
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return rows;
            // Seriernas id ur databasen — ingen trädgenomgång (C3-planens spärr mot GetAtRoot i ny kod).
            List<int> seriesIds;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                seriesIds = scope.Database.Fetch<int>(@"SELECT n.id FROM umbracoNode n
JOIN umbracoContent c ON c.nodeId = n.id JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
WHERE ct.alias = 'competitionSeries' AND n.trashed = 0");
            var seriesNodes = seriesIds.Select(id => ctx.Content.GetById(id)).Where(n => n != null).Cast<IPublishedContent>();
            foreach (var series in seriesNodes)
            {
                var seriesClub = series.Value<int>("clubId");
                var seriesRegion = (series.Value<string>("regionalFederation") ?? "").Trim();
                if (seriesClub <= 0 && seriesRegion.Length == 0) continue;
                // Seriens publicerade omgångar ur tävlingslagringen (C3-planen S3).
                foreach (var comp in _competitions.ListBySeries(series.Id))
                {
                    if (comp.ClubId > 0) continue;
                    if (!string.IsNullOrWhiteSpace(comp.RegionalFederation)) continue;
                    var date = comp.CompetitionDate;
                    rows.Add(new Row
                    {
                        CompetitionId = comp.Id,
                        Name = comp.CompetitionName ?? comp.Name ?? "",
                        Date = date.HasValue && date.Value.Year > 1900 ? date : null,
                        SeriesId = series.Id,
                        SeriesName = series.Name ?? "",
                        NewClubId = Math.Max(0, seriesClub),
                        NewRegion = seriesRegion,
                        OldUrl = _urlProvider.GetUrl(comp.Id),
                        Blocked = BlockedReason(comp.Id),
                    });
                }
            }
            return rows.OrderBy(r => r.SeriesName).ThenBy(r => r.Date).ToList();
        }

        private string BlockedReason(int competitionId)
        {
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var payments = scope.Database.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM dbo.LedgerPayment WHERE SourceId = @0 AND SourceType IN ('competition-registration','team-fee')",
                    competitionId);
                if (payments > 0) return $"{payments} avgifter i liggaren — utställaren flyttas inte.";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Värdrättelsen: liggaren kunde inte läsas för tävling {Id}", competitionId);
                return "Liggaren kunde inte läsas.";
            }
            if (_legacyInvoices.HasInvoices(competitionId)) return "Har fakturor — utställaren flyttas inte.";
            return "";
        }

        /// <summary>Skriver värden i de valda omgångarna. Returnerar per tävling vad som hände.</summary>
        public List<(int Id, bool Ok, string Message, string NewUrl)> Apply(IReadOnlyCollection<int> ids, int? actorMemberId)
        {
            var result = new List<(int, bool, string, string)>();
            var preview = Preview().ToDictionary(r => r.CompetitionId);
            foreach (var id in ids.Distinct())
            {
                if (!preview.TryGetValue(id, out var row)) { result.Add((id, false, "Står inte i listan längre.", "")); continue; }
                if (row.Blocked.Length > 0) { result.Add((id, false, row.Blocked, "")); continue; }
                try
                {
                    var content = _contentService.GetById(id);
                    if (content == null) { result.Add((id, false, "Tävlingen finns inte.", "")); continue; }
                    if (row.NewClubId > 0) content.SetValue("clubId", row.NewClubId);
                    if (row.NewRegion.Length > 0) content.SetValue("regionalFederation", row.NewRegion);
                    var published = content.Published;
                    _contentService.Save(content);
                    // Umbracos användar-id, inte medlemmens: en medlem är ingen backoffice-användare
                    // (FK_umbracoContentVersion_umbracoUser_id). Medlemmen står i loggraden.
                    if (published)
                    {
                        var pub = _contentService.Publish(content, new[] { "*" }, -1);
                        if (!pub.Success) { result.Add((id, false, "Sparad men inte publicerad: " + pub.Result, "")); continue; }
                    }
                    _logger.LogWarning("Värdrättelsen (F5): tävling {Id} \"{Name}\" fick klubb {Club} och krets {Region} från serien {Series} (medlem {Actor}). Gammal adress {Url}",
                        id, row.Name, row.NewClubId, row.NewRegion, row.SeriesId, actorMemberId, row.OldUrl);
                    result.Add((id, true, "", _urlProvider.GetUrl(id)));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Värdrättelsen: tävling {Id} kunde inte sparas", id);
                    result.Add((id, false, "Kunde inte sparas — se loggen.", ""));
                }
            }
            return result;
        }
    }
}
