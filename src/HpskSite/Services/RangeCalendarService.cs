using System.Globalization;
using System.Text;
using HpskSite.Helpers;
using HpskSite.Models;
using HpskSite.Models.Training;
using HpskSite.Services.Schedule;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Banans kalender (2026-10-11, efter Michaels önskemål): vad som händer på en skjutbana, från
    /// ALLA klubbar som använder den. Fem källor, en form (<see cref="RangeCalendarItem"/>):
    /// <list type="bullet">
    /// <item>tävlingar — <c>competition.rangeId</c> (doctype-egenskap, läses ur ett cachat index),</item>
    /// <item>träningar — <c>ClubTraining.RangeId</c> (+ <c>RangeSectionId</c>),</item>
    /// <item>händelser — <see cref="ClubEventRange"/> (egen tabell, se dess kommentar),</item>
    /// <item>klubbarnas fasta tider — <c>ClubRangeAllocation</c>, utlagda per dag,</item>
    /// <item>förvaltarens externa bokningar — <see cref="RangeExternalBooking"/>.</item>
    /// </list>
    ///
    /// <para><b>Synlighet:</b> inloggade medlemmar, som resten av skjutbanedatabasen. Kalendern visar
    /// vad, när, var på banan och vilken klubb — ALDRIG vilka som är anmälda.</para>
    ///
    /// <para><b>⚠️ Krock är en varning, aldrig en spärr</b> — se <see cref="RangeCalendarRules"/>.</para>
    /// </summary>
    public class RangeCalendarService
    {
        public const string CompetitionIndexKey = "rangecal_competition_index";
        /// <summary>En post som löper över flera dagar läggs ut per dag — men aldrig fler än så här.</summary>
        private const int MaxDaysPerItem = 31;

        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly AppCaches _caches;
        private readonly ClubService _clubs;
        private readonly ILogger<RangeCalendarService> _logger;

        public RangeCalendarService(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextFactory contextFactory,
            AppCaches caches,
            ClubService clubs,
            ILogger<RangeCalendarService> logger)
        {
            _databaseFactory = databaseFactory;
            _contextFactory = contextFactory;
            _caches = caches;
            _clubs = clubs;
            _logger = logger;
        }

        /// <summary>
        /// Det som saknas av <c>create-range-calendar-tables.sql</c>. Läses av startkontrollen. Kastar
        /// om frågan inte går att ställa — "kunde inte fråga" får aldrig bli ett svar.
        /// </summary>
        public List<string> MissingSchema()
        {
            using var db = _databaseFactory.CreateDatabase();
            var missing = new List<string>();
            if (db.ExecuteScalar<int>("SELECT CASE WHEN COL_LENGTH('dbo.ClubTraining', 'RangeSectionId') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("ClubTraining.RangeSectionId");
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.ClubEventRange', 'U') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("ClubEventRange");
            if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID('dbo.RangeExternalBooking', 'U') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("RangeExternalBooking");
            return missing;
        }

        // ── Bana och vall ─────────────────────────────────────────────────────

        /// <summary>
        /// Finns banan, och hör vallen till den? Ett färdigt felmeddelande, eller null. Anropas av
        /// varje skrivväg — en vall från en annan bana hade annars lagt posten på fel ställe i båda
        /// banornas kalender.
        /// </summary>
        public string? Validate(int rangeId, int? sectionId)
        {
            if (rangeId <= 0) return null;
            using var db = _databaseFactory.CreateDatabase();
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.ShootingRange WHERE Id = @0", rangeId) == 0)
                return "Skjutbanan finns inte längre. Välj en annan bana.";
            if (sectionId is > 0 &&
                db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.RangeSection WHERE Id = @0 AND RangeId = @1", sectionId, rangeId) == 0)
                return "Banan/vallen hör inte till den valda skjutbanan.";
            return null;
        }

        /// <summary>Banans namn och vallar — för väljaren och för kalendersidans rubrik.</summary>
        public (ShootingRange? Range, List<RangeSection> Sections) GetRangeWithSections(int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var range = db.SingleOrDefault<ShootingRange>("WHERE Id = @0", rangeId);
            if (range == null) return (null, new());
            var sections = db.Fetch<RangeSection>("WHERE RangeId = @0 ORDER BY SortOrder, Label", rangeId);
            return (range, sections);
        }

        /// <summary>
        /// Banan klubbens träningar oftast ligger på — förvalet när klubben inte har angett någon ägd
        /// bana eller huvudbana. 0 när klubben inte kopplat någon träning till en bana än.
        /// </summary>
        public int MostUsedRange(int clubId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                return db.ExecuteScalar<int?>(@"SELECT TOP 1 RangeId FROM dbo.ClubTraining
WHERE ClubId = @0 AND RangeId IS NOT NULL AND [Date] >= DATEADD(year, -1, CAST(GETDATE() AS date))
GROUP BY RangeId ORDER BY COUNT(*) DESC, MAX([Date]) DESC", clubId) ?? 0;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Klubbens vanligaste bana kunde inte läsas");
                return 0;
            }
        }

        /// <summary>Vallarna för flera banor på en gång (väljaren för klubbens banor).</summary>
        public Dictionary<int, List<RangeSection>> SectionsFor(IEnumerable<int> rangeIds)
        {
            var ids = rangeIds.Distinct().ToList();
            if (ids.Count == 0) return new();
            using var db = _databaseFactory.CreateDatabase();
            return db.Fetch<RangeSection>("WHERE RangeId IN (@0) ORDER BY SortOrder, Label", ids)
                .GroupBy(s => s.RangeId).ToDictionary(g => g.Key, g => g.ToList());
        }

        // ── Händelsens bana ───────────────────────────────────────────────────

        /// <summary>Banorna för flera händelser — EN fråga för en hel lista. Fel ger tomt.</summary>
        public Dictionary<int, ClubEventRange> GetEventRanges(IEnumerable<int> eventIds)
        {
            var ids = eventIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return new();
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var result = new Dictionary<int, ClubEventRange>();
                foreach (var chunk in ids.Chunk(1000)) // IN (@0) tar slut kring 2100 parametrar
                    foreach (var r in db.Fetch<ClubEventRange>("WHERE EventId IN (@0)", chunk))
                        result[r.EventId] = r;
                return result;
            }
            catch (Exception ex)
            {
                // En saknad tabell får inte ta ner händelselistan — bara bankopplingen saknas då.
                _logger.LogWarning(ex, "Händelsernas skjutbanor kunde inte läsas (ClubEventRange)");
                return new();
            }
        }

        public ClubEventRange? GetEventRange(int eventId)
            => GetEventRanges(new[] { eventId }).GetValueOrDefault(eventId);

        /// <summary>
        /// Sätter händelsens bana. <paramref name="rangeId"/> null = RÖR INTE (en äldre klient som inte
        /// skickar fältet får aldrig nolla ett val); 0 = ingen bana; &gt; 0 = den banan.
        /// Returnerar ett färdigt felmeddelande, eller null.
        /// </summary>
        public string? SetEventRange(int eventId, int? rangeId, int? sectionId, int? byMemberId)
        {
            if (eventId <= 0 || rangeId == null) return null;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                if (rangeId <= 0)
                {
                    db.Execute("DELETE FROM dbo.ClubEventRange WHERE EventId = @0", eventId);
                    return null;
                }
                var error = Validate(rangeId.Value, sectionId);
                if (error != null) return error;
                int? section = sectionId is > 0 ? sectionId : null;
                var n = db.Execute(
                    "UPDATE dbo.ClubEventRange SET RangeId = @1, RangeSectionId = @2, UpdatedByMemberId = @3, UpdatedAt = GETDATE() WHERE EventId = @0",
                    eventId, rangeId, section, byMemberId);
                if (n == 0)
                    db.Insert(new ClubEventRange
                    {
                        EventId = eventId, RangeId = rangeId.Value, RangeSectionId = section,
                        UpdatedByMemberId = byMemberId, UpdatedAt = DateTime.Now
                    });
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Händelsens skjutbana kunde inte sparas (händelse {EventId})", eventId);
                return "Händelsen sparades, men skjutbanan kunde inte kopplas. Försök igen om en stund.";
            }
        }

        /// <summary>"Kopiera händelse" ska ta med banan — kopian är samma sak ett annat datum.</summary>
        public void CopyEventRange(int fromEventId, int toEventId)
        {
            var src = GetEventRange(fromEventId);
            if (src != null) SetEventRange(toEventId, src.RangeId, src.RangeSectionId, src.UpdatedByMemberId);
        }

        /// <summary>En borttagen händelse ska inte stå kvar i banans kalender.</summary>
        public void DeleteEventRange(int eventId)
        {
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                db.Execute("DELETE FROM dbo.ClubEventRange WHERE EventId = @0", eventId);
            }
            catch (Exception ex)
            {
                // Ofarligt: kalendern hoppar över händelser som inte längre finns publicerade.
                _logger.LogDebug(ex, "ClubEventRange kunde inte städas för händelse {EventId}", eventId);
            }
        }

        // ── Kalendern ─────────────────────────────────────────────────────────

        /// <summary>
        /// Allt som händer på banan i [from, to], äldst först. En källa som fallerar loggas och hoppas
        /// över — en kalender utan tävlingarna är fortfarande användbar, en tom sida är det inte.
        /// </summary>
        public List<RangeCalendarItem> Build(int rangeId, DateTime from, DateTime to)
        {
            from = from.Date; to = to.Date;
            var items = new List<RangeCalendarItem>();
            Dictionary<int, string> sectionLabels;
            using (var db = _databaseFactory.CreateDatabase())
                sectionLabels = db.Fetch<RangeSection>("WHERE RangeId = @0", rangeId).ToDictionary(s => s.Id, s => s.Label);

            void Try(string what, Action a)
            {
                try { a(); }
                catch (Exception ex) { _logger.LogWarning(ex, "Banans kalender: {What} kunde inte läsas för bana {RangeId}", what, rangeId); }
            }

            Try("träningarna", () => items.AddRange(Trainings(rangeId, from, to)));
            Try("de fasta tiderna", () => items.AddRange(Allocations(rangeId, from, to)));
            Try("de externa bokningarna", () => items.AddRange(External(rangeId, from, to)));
            Try("händelserna", () => items.AddRange(Events(rangeId, from, to)));
            Try("tävlingarna", () => items.AddRange(Competitions(rangeId, from, to)));

            foreach (var i in items)
                if (i.SectionId is int sid && sectionLabels.TryGetValue(sid, out var lbl)) i.SectionLabel = lbl;

            return items
                .OrderBy(i => i.Date, StringComparer.Ordinal)
                .ThenBy(i => i.Start ?? "", StringComparer.Ordinal)
                .ThenBy(i => KindOrder(i.Kind))
                .ThenBy(i => i.Title, StringComparer.Create(new CultureInfo("sv-SE"), true))
                .ToList();
        }

        private static int KindOrder(string kind) => kind switch
        {
            RangeCalendarKind.Competition => 0,
            RangeCalendarKind.Event => 1,
            RangeCalendarKind.Training => 2,
            RangeCalendarKind.External => 3,
            _ => 4
        };

        private string ClubShort(int clubId)
        {
            if (clubId <= 0) return "";
            var name = _clubs.GetClubNameById(clubId);
            return string.IsNullOrWhiteSpace(name) ? $"Klubb {clubId}" : ClubNameHelper.Shorten(name);
        }

        private static string Ymd(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private IEnumerable<RangeCalendarItem> Trainings(int rangeId, DateTime from, DateTime to)
        {
            using var db = _databaseFactory.CreateDatabase();
            var rows = db.Fetch<ClubTraining>(
                "WHERE RangeId = @0 AND [Date] >= @1 AND [Date] <= @2", rangeId, from, to);
            var names = new Dictionary<int, string>();
            foreach (var t in rows)
            {
                if (!names.TryGetValue(t.ClubId, out var club)) names[t.ClubId] = club = ClubShort(t.ClubId);
                yield return new RangeCalendarItem
                {
                    Kind = RangeCalendarKind.Training,
                    SourceId = t.Id,
                    Title = string.IsNullOrWhiteSpace(t.Name) ? "Träning" : t.Name,
                    ClubId = t.ClubId,
                    OwnerName = club,
                    Date = Ymd(t.Date),
                    Start = t.StartTime,
                    End = t.EndTime,
                    SectionId = t.RangeSectionId,
                    IsCancelled = t.IsCancelled
                };
            }
        }

        private IEnumerable<RangeCalendarItem> Allocations(int rangeId, DateTime from, DateTime to)
        {
            using var db = _databaseFactory.CreateDatabase();
            var links = db.Fetch<ClubRangeLink>("WHERE RangeId = @0", rangeId);
            if (links.Count == 0) yield break;
            var clubByLink = links.ToDictionary(l => l.Id, l => l.ClubId);
            var slots = db.Fetch<ClubRangeAllocation>("WHERE ClubRangeLinkId IN (@0)", links.Select(l => l.Id).ToList());
            var names = new Dictionary<int, string>();
            foreach (var (date, s) in RangeCalendarRules.Expand(slots, from, to))
            {
                var clubId = clubByLink.GetValueOrDefault(s.ClubRangeLinkId);
                if (!names.TryGetValue(clubId, out var club)) names[clubId] = club = ClubShort(clubId);
                yield return new RangeCalendarItem
                {
                    Kind = RangeCalendarKind.Allocation,
                    SourceId = s.Id,
                    Title = "Fast tid",
                    ClubId = clubId,
                    OwnerName = club,
                    Date = Ymd(date),
                    Start = RangeCalendarRules.Hhmm(s.StartTime),
                    End = RangeCalendarRules.Hhmm(s.EndTime),
                    SectionId = s.RangeSectionId,
                    Note = s.Note
                };
            }
        }

        private IEnumerable<RangeCalendarItem> External(int rangeId, DateTime from, DateTime to)
        {
            using var db = _databaseFactory.CreateDatabase();
            foreach (var b in db.Fetch<RangeExternalBooking>(
                "WHERE RangeId = @0 AND IsActive = 1 AND [Date] >= @1 AND [Date] <= @2", rangeId, from, to))
            {
                yield return new RangeCalendarItem
                {
                    Kind = RangeCalendarKind.External,
                    SourceId = b.Id,
                    Title = b.Title,
                    OwnerName = "Extern bokning",
                    Date = Ymd(b.Date),
                    Start = b.StartTime,
                    End = b.EndTime,
                    SectionId = b.RangeSectionId,
                    Note = b.Note
                };
            }
        }

        private List<RangeCalendarItem> Events(int rangeId, DateTime from, DateTime to)
        {
            var result = new List<RangeCalendarItem>();
            List<ClubEventRange> links;
            using (var db = _databaseFactory.CreateDatabase())
                links = db.Fetch<ClubEventRange>("WHERE RangeId = @0", rangeId);
            if (links.Count == 0) return result;

            using var cref = _contextFactory.EnsureUmbracoContext();
            var content = cref.UmbracoContext.Content;
            if (content == null) return result;

            foreach (var link in links)
            {
                var ev = content.GetById(link.EventId);
                // Opublicerad, borttagen eller avstängd händelse syns inte — i kalendern som överallt annars.
                if (ev == null || ev.ContentType.Alias != "clubSimpleEvent") continue;
                if (ev.HasProperty("isActive") && !ev.Value<bool>("isActive")) continue;
                var start = ClubEvents.RealDate(ev.Value<DateTime?>("eventDate"));
                if (start == null) continue;
                var end = ClubEvents.RealDate(ev.Value<DateTime?>("eventEndDate"));

                var (clubId, owner) = OwnerOf(ev.Parent);
                var startTime = start.Value.TimeOfDay == TimeSpan.Zero ? null : RangeCalendarRules.Hhmm(start.Value.TimeOfDay);
                foreach (var day in Days(start.Value.Date, end?.Date, from, to))
                {
                    result.Add(new RangeCalendarItem
                    {
                        Kind = RangeCalendarKind.Event,
                        SourceId = ev.Id,
                        Title = ev.Value<string>("eventName") ?? ev.Name ?? "Händelse",
                        ClubId = clubId,
                        OwnerName = owner,
                        Date = Ymd(day),
                        // Klockslaget hör till första dagen; följande dagar av en flerdagars händelse saknar det.
                        Start = day == start.Value.Date ? startTime : null,
                        SectionId = link.RangeSectionId,
                        Url = ev.Url()
                    });
                }
            }
            return result;
        }

        private List<RangeCalendarItem> Competitions(int rangeId, DateTime from, DateTime to)
        {
            var result = new List<RangeCalendarItem>();
            var index = _caches.RuntimeCache.GetCacheItem(CompetitionIndexKey, BuildCompetitionIndex, TimeSpan.FromMinutes(2))
                        ?? new Dictionary<int, List<CompetitionRow>>();
            if (!index.TryGetValue(rangeId, out var comps)) return result;
            foreach (var c in comps)
                foreach (var day in Days(c.Date, c.End, from, to))
                    result.Add(new RangeCalendarItem
                    {
                        Kind = RangeCalendarKind.Competition,
                        SourceId = c.Id,
                        Title = c.Name,
                        ClubId = c.ClubId,
                        OwnerName = c.Owner,
                        Date = Ymd(day),
                        Url = c.Url
                    });
            return result;
        }

        /// <summary>Efter att en tävlings bana ändrats ska kalendern visa det direkt.</summary>
        public void InvalidateCompetitions() => _caches.RuntimeCache.Clear(CompetitionIndexKey);

        private sealed record CompetitionRow(int Id, string Name, DateTime Date, DateTime? End, int ClubId, string Owner, string Url);

        /// <summary>
        /// rangeId → tävlingarna på banan, ur den PUBLICERADE cachen. Byggs högst varannan minut —
        /// en genomgång av alla tävlingar per sidvisning hade varit samma fel som gjorde fakturasidan
        /// tolv sekunder lång.
        /// </summary>
        private Dictionary<int, List<CompetitionRow>> BuildCompetitionIndex()
        {
            var index = new Dictionary<int, List<CompetitionRow>>();
            try
            {
                using var cref = _contextFactory.EnsureUmbracoContext();
                var root = cref.UmbracoContext.Content?.GetAtRoot().FirstOrDefault();
                if (root == null) return index;

                var regionNames = root.Children()
                    .Where(c => c.ContentType.Alias == "regionalPage")
                    .GroupBy(r => r.Value<string>("regionCode") ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().Value<string>("regionName") ?? g.First().Name ?? g.Key,
                        StringComparer.OrdinalIgnoreCase);

                foreach (var c in root.Descendants().Where(d => d.ContentType.Alias == "competition"))
                {
                    var rangeId = c.HasProperty("rangeId") ? c.Value<int>("rangeId") : 0;
                    if (rangeId <= 0) continue;
                    var date = ClubEvents.RealDate(c.Value<DateTime?>("competitionDate"));
                    if (date == null) continue;
                    var end = ClubEvents.RealDate(c.Value<DateTime?>("competitionEndDate"));
                    var clubId = c.Value<int>("clubId");
                    string owner;
                    if (clubId > 0) owner = ClubShort(clubId);
                    else
                    {
                        var code = c.Value<string>("regionalFederation") ?? "";
                        owner = regionNames.TryGetValue(code, out var rn) ? rn : code;
                    }
                    var row = new CompetitionRow(c.Id, c.Value<string>("competitionName") ?? c.Name ?? "Tävling",
                        date.Value.Date, end?.Date, clubId, owner, c.Url());
                    if (!index.TryGetValue(rangeId, out var list)) index[rangeId] = list = new();
                    list.Add(row);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Banans kalender: tävlingsindexet kunde inte byggas");
            }
            return index;
        }

        private (int ClubId, string Name) OwnerOf(IPublishedContent? parent)
        {
            if (parent == null) return (0, "");
            if (parent.ContentType.Alias == "club") return (parent.Id, ClubShort(parent.Id));
            if (parent.ContentType.Alias == "regionalPage")
                return (0, parent.Value<string>("regionName") ?? parent.Name ?? "Kretsen");
            return (0, parent.Name ?? "");
        }

        /// <summary>Dagarna en post täcker, avgränsade till fönstret och till <see cref="MaxDaysPerItem"/>.</summary>
        private static IEnumerable<DateTime> Days(DateTime start, DateTime? end, DateTime from, DateTime to)
        {
            var last = end is { } e && e > start ? e : start;
            if (last > start.AddDays(MaxDaysPerItem - 1)) last = start.AddDays(MaxDaysPerItem - 1);
            for (var d = start < from ? from : start; d <= last && d <= to; d = d.AddDays(1))
                yield return d;
        }

        // ── Dagens läge på banan: underlaget för krockvarningen ──────────────

        public class DayItem
        {
            public RangeCalendarItem Item { get; set; } = new();
            /// <summary>"overlap", "sameDay" eller "otherSection".</summary>
            public string Relation { get; set; } = "sameDay";
            public bool OwnClub { get; set; }
        }

        /// <summary>
        /// Allt annat på banan samma dag som en post som håller på att läggas in, med hur det
        /// förhåller sig till den: krockar i tid och vall, samma dag men oklar tid, eller en annan vall.
        /// <paramref name="excludeKind"/>/<paramref name="excludeId"/> = posten själv (vid redigering).
        /// </summary>
        public List<DayItem> Day(int rangeId, int? sectionId, DateTime date, string? start, string? end,
            string? excludeKind, int excludeId, int ownClubId)
        {
            var result = new List<DayItem>();
            foreach (var i in Build(rangeId, date, date))
            {
                if (i.IsCancelled) continue;
                if (excludeId > 0 && string.Equals(i.Kind, excludeKind, StringComparison.OrdinalIgnoreCase) && i.SourceId == excludeId)
                    continue;
                var own = ownClubId > 0 && i.ClubId == ownClubId;
                string relation;
                if (!RangeCalendarRules.SectionsCollide(sectionId, i.SectionId)) relation = "otherSection";
                else relation = RangeCalendarRules.Relate(start, end, i.Start, i.End) switch
                {
                    TimeRelation.Overlap => "overlap",
                    TimeRelation.None => "none",
                    _ => "sameDay"
                };
                // En tävling saknar klockslag men upptar banan hela dagen — den är en krock, inte en notis.
                if (i.Kind == RangeCalendarKind.Competition && relation == "sameDay") relation = "overlap";
                result.Add(new DayItem { Item = i, Relation = relation, OwnClub = own });
            }
            return result;
        }

        // ── Externa bokningar ─────────────────────────────────────────────────

        public (int Id, string? Error) SaveExternal(int id, int rangeId, int? sectionId, string? date,
            string? startTime, string? endTime, string? title, string? note, int byMemberId)
        {
            var t = (title ?? "").Trim();
            if (t.Length == 0) return (0, "Skriv vem som bokat banan, till exempel Jaktskytteklubben.");
            if (t.Length > 200) t = t[..200];
            if (!DateTime.TryParseExact((date ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return (0, "Ange ett giltigt datum.");
            var s = string.IsNullOrWhiteSpace(startTime) ? null : startTime.Trim();
            var e = string.IsNullOrWhiteSpace(endTime) ? null : endTime.Trim();
            if (s != null && RangeCalendarRules.Minutes(s) == null) return (0, "Starttiden ska skrivas som tt:mm.");
            if (e != null && RangeCalendarRules.Minutes(e) == null) return (0, "Sluttiden ska skrivas som tt:mm.");
            if (s == null && e != null) return (0, "Ange en starttid när du anger en sluttid.");
            if (s != null && e != null && RangeCalendarRules.Minutes(e) <= RangeCalendarRules.Minutes(s))
                return (0, "Sluttiden måste vara efter starttiden.");
            var error = Validate(rangeId, sectionId);
            if (error != null) return (0, error);

            using var db = _databaseFactory.CreateDatabase();
            RangeExternalBooking b;
            if (id > 0)
            {
                b = db.SingleOrDefault<RangeExternalBooking>("WHERE Id = @0", id)!;
                if (b == null || !b.IsActive) return (0, "Bokningen finns inte längre.");
                if (b.RangeId != rangeId) return (0, "Bokningen hör till en annan skjutbana.");
            }
            else b = new RangeExternalBooking { RangeId = rangeId, CreatedByMemberId = byMemberId, CreatedAt = DateTime.Now };

            b.RangeSectionId = sectionId is > 0 ? sectionId : null;
            b.Date = day.Date;
            b.StartTime = s; b.EndTime = e;
            b.Title = t;
            b.Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(500, note.Trim().Length)];
            b.IsActive = true;
            if (b.Id > 0) db.Update(b); else db.Insert(b);
            return (b.Id, null);
        }

        /// <summary>Tar bort (gömmer) en extern bokning. Returnerar ett fel, eller null.</summary>
        public string? DeleteExternal(int id, int rangeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var n = db.Execute("UPDATE dbo.RangeExternalBooking SET IsActive = 0 WHERE Id = @0 AND RangeId = @1", id, rangeId);
            return n == 1 ? null : "Bokningen finns inte.";
        }

        public int? RangeOfExternal(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            var r = db.ExecuteScalar<int>("SELECT ISNULL(MAX(RangeId), 0) FROM dbo.RangeExternalBooking WHERE Id = @0", id);
            return r > 0 ? r : null;
        }

        // ── Prenumeration (ICS) ───────────────────────────────────────────────

        /// <summary>
        /// Banans kalender som prenumerationsfil. Fasta tider tas INTE med — de är återkommande och
        /// hade fyllt varje vardag i medlemmens kalender; den som vill se dem öppnar sidan.
        /// Inställda träningar skrivs som CANCELLED, så de försvinner i stället för att bli kvar.
        /// </summary>
        public string BuildIcs(int rangeId, string rangeName, IEnumerable<RangeCalendarItem> items, string siteBaseUrl)
        {
            var sb = new StringBuilder();
            var stamp = DateTime.UtcNow;
            sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n");
            sb.Append("PRODID:-//pistol.nu//Banans kalender//SV\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n");
            ScheduleIcsBuilder.AppendLine(sb, "X-WR-CALNAME", rangeName);
            sb.Append("REFRESH-INTERVAL;VALUE=DURATION:PT6H\r\nX-PUBLISHED-TTL:PT6H\r\n");
            var page = $"{siteBaseUrl.TrimEnd('/')}/skjutbana/{rangeId}#vy=kalender";

            foreach (var i in items)
            {
                if (i.Kind == RangeCalendarKind.Allocation) continue;
                if (!DateTime.TryParseExact(i.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
                sb.Append("BEGIN:VEVENT\r\n");
                ScheduleIcsBuilder.AppendLine(sb, "UID", $"range-{rangeId}-{i.Kind}-{i.SourceId}-{day:yyyyMMdd}@pistol.nu");
                ScheduleIcsBuilder.AppendLine(sb, "DTSTAMP", ScheduleIcsBuilder.Utc(stamp));
                var st = RangeCalendarRules.Minutes(i.Start);
                if (st != null)
                {
                    var start = day.AddMinutes(st.Value);
                    var en = RangeCalendarRules.Minutes(i.End);
                    var end = en != null && en > st ? day.AddMinutes(en.Value) : start.AddHours(2);
                    ScheduleIcsBuilder.AppendLine(sb, "DTSTART", ScheduleIcsBuilder.Local(start));
                    ScheduleIcsBuilder.AppendLine(sb, "DTEND", ScheduleIcsBuilder.Local(end));
                }
                else
                {
                    sb.Append($"DTSTART;VALUE=DATE:{day:yyyyMMdd}\r\n");
                    sb.Append($"DTEND;VALUE=DATE:{day.AddDays(1):yyyyMMdd}\r\n");
                }
                var who = string.IsNullOrWhiteSpace(i.OwnerName) ? "" : $" ({i.OwnerName})";
                ScheduleIcsBuilder.AppendLine(sb, "SUMMARY", i.Title + who);
                ScheduleIcsBuilder.AppendLine(sb, "LOCATION", string.IsNullOrWhiteSpace(i.SectionLabel) ? rangeName : $"{rangeName}, {i.SectionLabel}");
                if (i.IsCancelled) sb.Append("STATUS:CANCELLED\r\n");
                ScheduleIcsBuilder.AppendLine(sb, "URL", page);
                sb.Append("END:VEVENT\r\n");
            }
            sb.Append("END:VCALENDAR\r\n");
            return sb.ToString();
        }
    }
}
