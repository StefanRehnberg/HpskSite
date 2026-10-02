using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Extensions;
using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Kretsgranskning
{
    /// <summary>
    /// Kretskalendern (fas 4): EN tjänst läser alla källor — tävlingar, ansökningar som ännu saknar
    /// tävling och kretsens händelser — och översätter dem till <see cref="KretsCalendarEntry"/>.
    ///
    /// <para><b>⚠️ Samma tävling får aldrig stå två gånger.</b> En ansökan som fått sin tävling
    /// visas inte; tävlingen visas i stället. En godkänd ansökan utan tävling visas som godkänd —
    /// för en skytt är den lika verklig som en tävling.</para>
    ///
    /// <para><b>Vad som visas för vem:</b> kretsens egna preliminära ansökningar syns för alla (det
    /// hjälper skyttar och klubbar att planera). En grannkrets preliminära ansökningar syns bara för
    /// inloggade klubb- och kretsadministratörer. Klubbinterna tävlingar visas aldrig.</para>
    ///
    /// <para><b>Indexet över innehållsträdet cachas i 60 s</b> — ett varv genom tävlingarna per
    /// sidladdning hade gjort en öppen sida dyr. Ansökningarna läses direkt (SQL, billigt), så ett
    /// kretsbeslut syns genast.</para>
    /// </summary>
    public class KretsCalendarService
    {
        private const string CacheKey = "kretskalender_index_v1";
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly AppCaches _caches;
        private readonly CompetitionApplicationService _applications;
        private readonly ClubService _clubs;
        private readonly StomprogramService _stomprogram;
        private readonly ILogger<KretsCalendarService> _logger;

        public KretsCalendarService(IUmbracoContextFactory contextFactory, AppCaches caches,
            CompetitionApplicationService applications, ClubService clubs, StomprogramService stomprogram, ILogger<KretsCalendarService> logger)
        {
            _contextFactory = contextFactory;
            _caches = caches;
            _applications = applications;
            _clubs = clubs;
            _stomprogram = stomprogram;
            _logger = logger;
        }

        public record RegionInfo(int Id, string Code, string Name, string Url);

        private record CompRow(int Id, string Name, DateTime Date, DateTime? EndDate, string Discipline, string Level,
            string Scope, int ClubId, string RegionCode, bool IsClubOnly, string Url, string Venue);

        private record EventRow(int RegionId, int Id, string Name, DateTime Date, DateTime? EndDate, string Venue, string Url);

        private class Index
        {
            public Dictionary<int, RegionInfo> Regions { get; } = new();
            public Dictionary<int, string> ClubRegion { get; } = new();
            public Dictionary<int, string> ClubName { get; } = new();
            public List<CompRow> Competitions { get; } = new();
            public List<EventRow> Events { get; } = new();
            public Dictionary<int, CompetitionRegionInfo> CompetitionRegion { get; } = new();
        }

        public record CompetitionRegionInfo(int CompetitionId, string RegionCode, DateTime? Date, DateTime? EndDate, string Name);

        /// <summary>
        /// The hosting krets of a competition (its own regionalFederation, else the club's krets,
        /// else the parent series'), with its node id. Null when no krets can be resolved.
        /// </summary>
        public (RegionInfo Region, CompetitionRegionInfo Competition)? CompetitionRegion(int competitionId)
        {
            var idx = GetIndex();
            if (!idx.CompetitionRegion.TryGetValue(competitionId, out var c)) return null;
            var r = idx.Regions.Values.FirstOrDefault(x => x.Code.Equals(c.RegionCode, StringComparison.OrdinalIgnoreCase));
            return r == null ? null : (r, c);
        }

        public RegionInfo? Region(int regionId) => GetIndex().Regions.GetValueOrDefault(regionId);

        /// <summary>Alla kretsar, i namnordning.</summary>
        public List<RegionInfo> AllRegions() => GetIndex().Regions.Values.OrderBy(r => r.Name).ToList();

        /// <summary>Alla tävlingar i indexet med värdkrets och datum.</summary>
        public List<CompetitionRegionInfo> AllCompetitions() => GetIndex().CompetitionRegion.Values.ToList();

        /// <summary>Alla tävlingar (även inaktiva och odaterade) vars värdkrets är <paramref name="regionCode"/>.</summary>
        public List<int> CompetitionIdsInRegion(string regionCode) =>
            GetIndex().CompetitionRegion.Values
                .Where(c => c.RegionCode.Equals(regionCode ?? "", StringComparison.OrdinalIgnoreCase))
                .Select(c => c.CompetitionId).ToList();

        public RegionInfo? RegionByCode(string code) =>
            GetIndex().Regions.Values.FirstOrDefault(r => r.Code.Equals(code ?? "", StringComparison.OrdinalIgnoreCase));

        /// <summary>Kretsnodens id för en klubb (trädet: regionalPage &gt; clubsPage &gt; club).</summary>
        public int RegionIdForClub(int clubId)
        {
            var idx = GetIndex();
            return idx.ClubRegion.TryGetValue(clubId, out var code)
                ? idx.Regions.Values.FirstOrDefault(r => r.Code.Equals(code, StringComparison.OrdinalIgnoreCase))?.Id ?? 0
                : 0;
        }

        /// <summary>Klubbarna i kretsen (id → namn).</summary>
        public Dictionary<int, string> ClubsInRegion(int regionId)
        {
            var idx = GetIndex();
            var r = idx.Regions.GetValueOrDefault(regionId);
            if (r == null) return new();
            return idx.ClubRegion.Where(kv => kv.Value.Equals(r.Code, StringComparison.OrdinalIgnoreCase))
                .ToDictionary(kv => kv.Key, kv => idx.ClubName.GetValueOrDefault(kv.Key, ""));
        }

        /// <summary>
        /// Kretsens grannar, som noder: kretsens eget val om det finns
        /// (<c>RegionCalendarSettings.NeighbourOverrides</c>), annars <see cref="RegionAdjacency"/>.
        /// </summary>
        public List<RegionInfo> Neighbours(int regionId)
        {
            var idx = GetIndex();
            var r = idx.Regions.GetValueOrDefault(regionId);
            if (r == null) return new();
            string? overrides = null;
            try { overrides = _applications.Settings(regionId)?.NeighbourOverrides; }
            catch (Exception ex) { _logger.LogWarning(ex, "Kretsens grannkretsval kunde inte läsas för {Region}.", regionId); }
            var codes = RegionNeighbourSetting.Resolve(r.Code, overrides);
            return idx.Regions.Values.Where(x => codes.Contains(x.Code, StringComparer.OrdinalIgnoreCase))
                .OrderBy(x => x.Name).ToList();
        }

        /// <summary>
        /// Kalenderns poster för en krets i ett intervall.
        /// </summary>
        /// <param name="withNeighbours">Lägg in grannkretsarnas tävlingar och ansökningar.</param>
        /// <param name="viewerIsAdmin">Inloggad klubb- eller kretsadministratör: ser grannarnas preliminära ansökningar.</param>
        /// <param name="planning">
        /// Planeringskalendern (adminpanelerna): preliminära ansökningar, Förbundets stomprogram och
        /// krockvarningar. Den PUBLIKA kalendern (kretssidan) visar bara det kretsen publicerat —
        /// tävlingar, godkända ansökningar och kretsens händelser — utan krockar: en skytt har ingen
        /// nytta av "samma gren samma dag i grannkretsen", och en preliminär ansökan är inget löfte.
        /// </param>
        public List<KretsCalendarEntry> Build(int regionId, DateTime from, DateTime to, bool withNeighbours, bool viewerIsAdmin, bool planning = true)
        {
            var idx = GetIndex();
            var region = idx.Regions.GetValueOrDefault(regionId);
            if (region == null) return new();

            var neighbours = withNeighbours ? Neighbours(regionId) : new List<RegionInfo>();
            var regionByCode = idx.Regions.Values.GroupBy(r => r.Code, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
            var list = new List<KretsCalendarEntry>();

            // Tävlingar. SM i hela landet tas med som krockbakgrund även utan grannar.
            foreach (var c in idx.Competitions)
            {
                if (c.IsClubOnly) continue;
                var end = (c.EndDate ?? c.Date).Date;
                if (end < from.Date || c.Date.Date > to.Date) continue;

                var own = c.RegionCode.Equals(region.Code, StringComparison.OrdinalIgnoreCase);
                var isNeighbour = !own && neighbours.Any(n => n.Code.Equals(c.RegionCode, StringComparison.OrdinalIgnoreCase));
                var isSm = c.Scope == CompetitionScopeHelper.SvensktMasterskap;
                if (!own && !isNeighbour && !isSm) continue;

                var rinfo = regionByCode.GetValueOrDefault(c.RegionCode);
                list.Add(new KretsCalendarEntry
                {
                    Kind = "competition", Id = c.Id, Name = c.Name, Date = c.Date, EndDate = c.EndDate,
                    Discipline = c.Discipline, DisciplineLabel = ActivityDiscipline.Label(c.Discipline),
                    Level = c.Level,
                    Organiser = c.ClubId > 0 ? idx.ClubName.GetValueOrDefault(c.ClubId, "") : (rinfo?.Name ?? ""),
                    Place = c.Venue, Url = c.Url,
                    Status = KretsCalendarStatus.Tavling, StatusLabel = KretsCalendarStatus.Label(KretsCalendarStatus.Tavling),
                    RegionCode = c.RegionCode, RegionName = rinfo?.Name ?? c.RegionCode,
                    IsNeighbour = isNeighbour || (!own && isSm), IsSm = isSm
                });
            }

            // Ansökningar som ännu saknar tävling.
            var regionIds = new List<int> { regionId };
            regionIds.AddRange(neighbours.Select(n => n.Id));
            List<CompetitionApplication> apps;
            try { apps = _applications.ForRegions(regionIds, from.Date, to.Date); }
            catch (Exception ex)
            {
                // En saknad tabell får inte ta ner hela kalendern — tävlingarna visas ändå.
                _logger.LogWarning(ex, "Kretskalendern kunde inte läsa ansökningarna för krets {Region}.", regionId);
                apps = new();
            }
            foreach (var a in apps)
            {
                if (a.CompetitionId is > 0) continue;
                var prelim = CompetitionApplicationStatus.Preliminary.Contains(a.Status);
                var granted = a.Status == CompetitionApplicationStatus.Beviljad;
                if (!prelim && !granted) continue;
                var isNeighbour = a.RegionId != regionId;
                if (prelim && !planning) continue;
                if (isNeighbour && prelim && !viewerIsAdmin) continue;

                var rinfo = idx.Regions.GetValueOrDefault(a.RegionId);
                var status = granted ? KretsCalendarStatus.Godkand : KretsCalendarStatus.Preliminar;
                list.Add(new KretsCalendarEntry
                {
                    Kind = "application", Id = a.Id, Name = a.Name, Date = a.EffectiveDate, EndDate = a.EndDate,
                    Discipline = a.Discipline, DisciplineLabel = ActivityDiscipline.Label(a.Discipline),
                    Level = a.Level,
                    Organiser = a.ClubId > 0 ? idx.ClubName.GetValueOrDefault(a.ClubId, "") : (rinfo?.Name ?? ""),
                    Place = a.Place ?? "",
                    Status = status, StatusLabel = KretsCalendarStatus.Label(status),
                    RegionCode = rinfo?.Code ?? "", RegionName = rinfo?.Name ?? "",
                    IsNeighbour = isNeighbour
                });
            }

            // Kretsens egna händelser (möten, utbildningar).
            foreach (var e in idx.Events.Where(e => e.RegionId == regionId))
            {
                var end = (e.EndDate ?? e.Date).Date;
                if (end < from.Date || e.Date.Date > to.Date) continue;
                list.Add(new KretsCalendarEntry
                {
                    Kind = "event", Id = e.Id, Name = e.Name, Date = e.Date, EndDate = e.EndDate,
                    Place = e.Venue, Url = e.Url, Organiser = region.Name,
                    Status = KretsCalendarStatus.Handelse, StatusLabel = KretsCalendarStatus.Label(KretsCalendarStatus.Handelse),
                    RegionCode = region.Code, RegionName = region.Name
                });
            }

            // Förbundets stomprogram — bakgrund och krockkälla, för alla kretsar.
            List<StomprogramItem> fixedDates;
            try { fixedDates = planning ? _stomprogram.Range(from.Date, to.Date) : new(); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kretskalendern kunde inte läsa stomprogrammet.");
                fixedDates = new();
            }
            foreach (var s in fixedDates)
            {
                list.Add(new KretsCalendarEntry
                {
                    Kind = "stomprogram", Id = s.Id, Name = s.Name, Date = s.StartDate, EndDate = s.EndDate,
                    Discipline = s.Discipline ?? "", DisciplineLabel = StomprogramDisciplines.Label(s.Discipline), IsPeriod = s.IsPeriod,
                    Organiser = "Förbundet", Place = s.Note ?? "",
                    Status = KretsCalendarStatus.Stomprogram, StatusLabel = KretsCalendarStatus.Label(KretsCalendarStatus.Stomprogram),
                    RegionCode = "", RegionName = "Förbundet"
                });
            }

            KretsCalendarConflicts.Mark(list);
            if (!planning) foreach (var e in list) e.Conflicts.Clear();
            return list.OrderBy(e => e.Date).ThenBy(e => e.IsNeighbour).ThenBy(e => e.Name).ToList();
        }

        public record ChampionshipRow(int Id, string Name, DateTime Date, string DisciplineLabel, string Venue, string Organiser);

        /// <summary>
        /// Kretsens kretsmästerskap ett år — Förbundets instruktion säger att kretsen för in dem på
        /// sammanställningen, så att Förbundet kan undvika krockar i rikskalendern.
        /// </summary>
        public List<ChampionshipRow> Kretsmasterskap(int regionId, int year)
        {
            var idx = GetIndex();
            var r = idx.Regions.GetValueOrDefault(regionId);
            if (r == null) return new();
            return idx.Competitions
                .Where(c => c.Scope == CompetitionScopeHelper.Kretsmasterskap && c.Date.Year == year
                            && c.RegionCode.Equals(r.Code, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Date)
                .Select(c => new ChampionshipRow(c.Id, c.Name, c.Date, ActivityDiscipline.Label(c.Discipline), c.Venue,
                    c.ClubId > 0 ? idx.ClubName.GetValueOrDefault(c.ClubId, "") : r.Name))
                .ToList();
        }

        /// <summary>Töm indexet — efter att en tävling skapats ur en ansökan ska kalendern visa den genast.</summary>
        public void Invalidate() => _caches.RuntimeCache.Clear(CacheKey);

        // ── Indexet ─────────────────────────────────────────────────────────────────────────

        private Index GetIndex()
            => _caches.RuntimeCache.GetCacheItem(CacheKey, BuildIndex, TimeSpan.FromSeconds(60)) ?? new Index();

        private Index BuildIndex()
        {
            var idx = new Index();
            try
            {
                using var cref = _contextFactory.EnsureUmbracoContext();
                var content = cref.UmbracoContext.Content;
                var root = content?.GetAtRoot().FirstOrDefault();
                if (root == null) return idx;

                foreach (var r in root.Children().Where(c => c.ContentType.Alias == "regionalPage"))
                {
                    var code = r.Value<string>("regionCode") ?? "";
                    idx.Regions[r.Id] = new RegionInfo(r.Id, code, r.Value<string>("regionName") ?? r.Name ?? code, r.Url());

                    foreach (var club in r.Descendants().Where(d => d.ContentType.Alias == "club"))
                    {
                        idx.ClubRegion[club.Id] = code;
                        idx.ClubName[club.Id] = club.Value<string>("clubName") ?? club.Name ?? "";
                    }
                    foreach (var ev in r.Children().Where(c => c.ContentType.Alias == "clubSimpleEvent"))
                    {
                        var d = ev.Value<DateTime?>("eventDate");
                        if (d is null || d.Value == DateTime.MinValue) continue;
                        var endRaw = ev.Value<DateTime?>("eventEndDate");
                        idx.Events.Add(new EventRow(r.Id, ev.Id, ev.Value<string>("eventName") ?? ev.Name ?? "",
                            d.Value, endRaw is { } x && x != DateTime.MinValue ? x : null,
                            ev.Value<string>("venue") ?? "", ev.Url()));
                    }
                }

                // Klubbar som inte ligger under en krets i trädet men bär regionalFederation.
                foreach (var club in root.Descendants().Where(d => d.ContentType.Alias == "club" && !idx.ClubRegion.ContainsKey(d.Id)))
                {
                    var code = club.Value<string>("regionalFederation") ?? "";
                    if (string.IsNullOrEmpty(code)) continue;
                    idx.ClubRegion[club.Id] = code;
                    idx.ClubName[club.Id] = club.Value<string>("clubName") ?? club.Name ?? "";
                }

                foreach (var c in root.Descendants().Where(d => d.ContentType.Alias == "competition"))
                {
                    var date = c.Value<DateTime?>("competitionDate");
                    var clubId = c.Value<int>("clubId");
                    var regionCode = c.Value<string>("regionalFederation") ?? "";
                    if (string.IsNullOrEmpty(regionCode) && clubId > 0) idx.ClubRegion.TryGetValue(clubId, out regionCode);
                    // ⚠️ En serieomgång utan egen värd ÄRVER seriens krets (som i kretsavgiften).
                    if (string.IsNullOrEmpty(regionCode) && c.Parent?.ContentType.Alias == "competitionSeries")
                        regionCode = c.Parent.Value<string>("regionalFederation") ?? "";
                    if (string.IsNullOrEmpty(regionCode)) continue;

                    var endRaw = c.Value<DateTime?>("competitionEndDate");
                    var realDate = date is { } dd && dd != DateTime.MinValue ? dd : (DateTime?)null;
                    var realEnd = endRaw is { } ee && ee != DateTime.MinValue ? ee : (DateTime?)null;
                    // Every competition, active or not, for CompetitionRegion (the review gate).
                    idx.CompetitionRegion[c.Id] = new CompetitionRegionInfo(c.Id, regionCode ?? "", realDate, realEnd,
                        c.Value<string>("competitionName") ?? c.Name ?? "");

                    if (realDate is null) continue;
                    if (!c.Value<bool>("isActive")) continue;
                    date = realDate;
                    idx.Competitions.Add(new CompRow(
                        c.Id,
                        c.Value<string>("competitionName") ?? c.Name ?? "",
                        date.Value,
                        endRaw is { } e && e != DateTime.MinValue ? e : null,
                        ActivityDiscipline.Canonical(c.Value("competitionType")?.ToString()),
                        CompetitionLevel.Read(c),
                        CompetitionScopeHelper.ReadScope(c),
                        clubId,
                        regionCode ?? "",
                        c.Value<bool>("isClubOnly"),
                        SafeUrl(c),
                        c.Value<string>("venue") ?? ""));
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kretskalenderns index kunde inte byggas.");
            }
            return idx;
        }

        private static string SafeUrl(IPublishedContent c)
        {
            try { return c.CompetitionUrl(); } catch { return c.Url(); }
        }
    }
}
