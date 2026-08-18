using System.Globalization;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Springskytte.Controllers;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services.Messaging;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Builds a member's personal competition itinerary — the single source of truth behind
    /// /mitt-schema, the "Ditt schema" card on the competition page, the home-page card, the .ics
    /// export and the start-time reminders. Every one of those surfaces renders what this returns;
    /// none of them read start lists themselves.
    ///
    /// It fans out over the places a start time can live, which differ per discipline:
    ///   - Precision-family + Direktplacering + Springskytte → a `precisionStartList` child node's
    ///     `configurationData` JSON. All three share the doctype; the JSON SHAPE is the discriminator
    ///     (precision `Teams` / springskytte `Starters` / stafett `Teams` tagged TeamFormat).
    ///   - Championship finals → a `finalsStartList` child node (same precision shape).
    ///   - Fältskytte / MagnumFält → the FaltskyttePatrol + FaltskyttePatrolMember SQL tables.
    ///   - Working, every discipline → StaffAssignment (+ StaffPass for structured shifts).
    ///   - Everyone's programme → CompetitionAgendaItem.
    ///
    /// Three rules that must not be relaxed:
    ///   1. Only PUBLISHED/official start lists count. A draft time that later moves is worse than no
    ///      time, so the member is told "startlistan är inte publicerad än" instead.
    ///   2. Station layouts stay secret. A funktionär row says "Station 3" and nothing more — never
    ///      figures, distances or timelines. Those are reachable only by scanning the station QR.
    ///   3. Absolute times are never invented. See ScheduleItem.StartsAt.
    /// </summary>
    public class MyScheduleService
    {
        private readonly IUmbracoContextFactory _ctxFactory;
        private readonly IScopeProvider _scopeProvider;
        private readonly ParticipantAudienceResolver _audience;
        private readonly AppCaches _appCaches;
        private readonly HpskSite.Services.Staffing.RoleCatalogService _roles;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MyScheduleService> _logger;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        /// <summary>
        /// Deserializing a big configurationData blob per home-page render is wasteful, and the home
        /// page is the most-hit page on the site. Same short TTL as ParticipantAudienceResolver: long
        /// enough to make navigation feel instant, short enough that a republished start list shows up
        /// while the member is still looking at it.
        /// </summary>
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        public MyScheduleService(
            IUmbracoContextFactory ctxFactory,
            IScopeProvider scopeProvider,
            ParticipantAudienceResolver audience,
            AppCaches appCaches,
            HpskSite.Services.Staffing.RoleCatalogService roles,
            IConfiguration configuration,
            ILogger<MyScheduleService> logger)
        {
            _ctxFactory = ctxFactory;
            _scopeProvider = scopeProvider;
            _audience = audience;
            _appCaches = appCaches;
            _roles = roles;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>
        /// Config path listing competitions whose start lists live OUTSIDE pistol.nu. Matched on URL
        /// SEGMENT, not id, so one setting works in dev and prod (ids differ per database). Lives in
        /// the tracked appsettings.json rather than the gitignored appsettings.Production.json, so it
        /// cannot be lost on a deploy.
        /// </summary>
        public const string ExternalStartListConfigKey = "Schedule:ExternalStartListCompetitions";

        /// <summary>
        /// For a competition whose start lists are published elsewhere, "Startlistan är inte publicerad
        /// än" is a lie that would stand for the whole event — the list is never coming. The warning is
        /// replaced by a pointer instead. Empty config = every competition behaves exactly as before.
        /// Pure + public so the matching is unit-testable without an Umbraco context.
        /// </summary>
        public static bool MatchesExternalStartListSegment(string[]? configuredSegments, string? urlSegment)
        {
            if (configuredSegments == null || configuredSegments.Length == 0) return false;
            if (string.IsNullOrWhiteSpace(urlSegment)) return false;

            return configuredSegments.Any(s => !string.IsNullOrWhiteSpace(s)
                                               && string.Equals(s.Trim(), urlSegment.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private bool HasExternalStartLists(IPublishedContent comp)
            => MatchesExternalStartListSegment(
                _configuration.GetSection(ExternalStartListConfigKey).Get<string[]>(),
                comp.UrlSegment);

        // ---------------------------------------------------------------- public API

        /// <summary>The member's itinerary for one competition. Never throws; returns an empty
        /// schedule when the competition is gone or nothing is scheduled for this member.</summary>
        public MySchedule GetSchedule(int memberId, int competitionId, bool useCache = true)
        {
            if (memberId <= 0 || competitionId <= 0) return new MySchedule { CompetitionId = competitionId };
            if (!useCache) return BuildSchedule(memberId, competitionId);

            var key = $"sched_{competitionId}_{memberId}";
            return _appCaches.RuntimeCache.GetCacheItem(key, () => BuildSchedule(memberId, competitionId), CacheTtl)
                   ?? new MySchedule { CompetitionId = competitionId };
        }

        /// <summary>
        /// Competition ids the member has something scheduled in, within the window. Cheap by design:
        /// the candidate set comes from the published cache (dates only), then ONE registration query
        /// and ONE staffing query narrow it. A member with nothing on pays two indexed lookups.
        /// </summary>
        public List<int> GetCompetitionIdsForMember(int memberId, DateTime fromDate, DateTime toDate)
        {
            if (memberId <= 0) return new List<int>();
            var result = new HashSet<int>();

            try
            {
                var candidates = GetCompetitionsInWindow(fromDate, toDate);
                if (candidates.Count > 0)
                {
                    // Shooter side: registrations are Save()-only (unpublished), so this has to be SQL.
                    foreach (var id in RegisteredCompetitionIds(memberId, candidates))
                        result.Add(id);
                }

                // Working side: one indexed query, no date filter needed (the table is small per member).
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var staffed = scope.Database.Fetch<int>(
                    "SELECT DISTINCT CompetitionId FROM StaffAssignment WHERE MemberId = @0", memberId);
                foreach (var id in staffed)
                {
                    // Keep the ones whose RUN overlaps the window — the dates live in the content tree.
                    // Overlap, not "start date inside window": a competition running 1–31 August starts
                    // inside a 7-day window but ends outside it, and one that started last week is still
                    // going on today. Undated competitions are kept (hiding a real commitment is worse).
                    var (start, end) = GetCompetitionSpan(id);
                    if (start == null || (start.Value.Date <= toDate.Date && (end ?? start).Value.Date >= fromDate.Date))
                        result.Add(id);
                }

                // ...AND the competitions where THIS MEMBER has a commitment inside the window, whatever the
                // competition's own date. A funktionär's work starts long before the shooting does: bygga
                // banan, hämta materiel, a pre-comp pass. Those carry their own date — StaffAssignment.StartsAt,
                // or the linked StaffPass.PassDate — and filtering on the competition date alone hid every one
                // of them. A competition on 11 October with a build day next Saturday belongs on a "next 7
                // days" card; asking "is the competition soon?" instead of "do I have something soon?" is what
                // made it disappear. Declined rows are excluded, exactly as BuildSchedule skips them.
                foreach (var id in scope.Database.Fetch<int>(@"
                    SELECT DISTINCT sa.CompetitionId
                    FROM StaffAssignment sa
                    LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
                    WHERE sa.MemberId = @0
                      AND (sa.Status IS NULL OR sa.Status <> @3)
                      AND ( (sa.StartsAt IS NOT NULL AND CAST(sa.StartsAt AS date) BETWEEN @1 AND @2)
                         OR (sa.StartsAt IS NULL AND sp.PassDate IS NOT NULL AND CAST(sp.PassDate AS date) BETWEEN @1 AND @2) )",
                    memberId, fromDate.Date, toDate.Date, StaffAssignmentStatus.Declined))
                {
                    result.Add(id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyScheduleService: competition lookup failed for member {Member}", memberId);
            }

            return result.ToList();
        }

        // ---------------------------------------------------------------- build

        private MySchedule BuildSchedule(int memberId, int competitionId)
        {
            var s = new MySchedule { CompetitionId = competitionId };
            try
            {
                using var ctxRef = _ctxFactory.EnsureUmbracoContext();
                var comp = ctxRef.UmbracoContext.Content?.GetById(competitionId);
                if (comp == null || comp.ContentType.Alias != "competition") return s;

                s.CompName = comp.Value<string>("competitionName") ?? comp.Name ?? "Tävling";
                s.Discipline = ReadCompetitionType(comp);
                s.CompDate = NullIfDefault(comp.Value<DateTime>("competitionDate"));
                s.CompEndDate = RealDate(comp.Value<DateTime?>("competitionEndDate"));
                s.CompetitionUrl = comp.Url();

                var items = new List<ScheduleItem>();

                // --- shooting ---
                var isFalt = s.Discipline is "Faltskytte" or "MagnumFalt";
                if (isFalt)
                {
                    items.AddRange(BuildFaltItems(comp, memberId, s));
                }
                else
                {
                    items.AddRange(BuildStartListItems(comp, memberId, s));
                }

                // --- working ---
                items.AddRange(BuildFunctionaryItems(competitionId, memberId, s));

                // --- everyone's programme ---
                items.AddRange(BuildAgendaItems(competitionId, s, hasStaffItems:
                    items.Any(i => i.Kind == ScheduleItemKind.Funktionar)));

                // --- registered but nothing to show yet? ---
                s.IsRegistered = SafeIsRegistered(competitionId, memberId);
                if (s.IsRegistered && !items.Any(i => i.Kind == ScheduleItemKind.Skytte))
                {
                    // StartListPending stays true either way — it's what makes the schedule card render
                    // at all for a shooter with no shooting rows. Only the wording changes.
                    s.StartListPending = true;
                    if (HasExternalStartLists(comp))
                    {
                        s.Warnings.Add("Startlistan publiceras inte i pistol.nu för den här tävlingen — din starttid hittar du hos arrangören.");
                    }
                    else
                    {
                        s.Warnings.Add(isFalt
                            ? "Patrullistan är inte publicerad än — dina starttider visas så snart arrangören publicerar den."
                            : "Startlistan är inte publicerad än — dina starttider visas så snart arrangören publicerar den.");
                    }
                }

                DetectConflicts(items);
                s.Days = GroupIntoDays(items);
                s.NextItem = items
                    .Where(i => i.StartsAt != null && i.StartsAt > DateTime.Now)
                    .OrderBy(i => i.StartsAt)
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyScheduleService: build failed for member {Member} comp {Comp}", memberId, competitionId);
            }
            return s;
        }

        // ---------------------------------------------------------------- shooting: start-list nodes

        /// <summary>
        /// Precision-family, Direktplacering, Springskytte (individual + stafett) and championship
        /// finals. All live on child nodes; only official/published ones are read.
        /// </summary>
        private List<ScheduleItem> BuildStartListItems(IPublishedContent comp, int memberId, MySchedule s)
        {
            var items = new List<ScheduleItem>();

            var nodes = comp.Children()
                .Where(c => c.ContentType.Alias is "precisionStartList" or "finalsStartList")
                .ToList();

            // Legacy layout: nested under a competitionStartListsHub.
            var hub = comp.Children().FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");
            if (hub != null) nodes.AddRange(hub.Children().Where(c => c.ContentType.Alias == "precisionStartList"));

            foreach (var node in nodes)
            {
                var isFinals = node.ContentType.Alias == "finalsStartList";
                var official = isFinals
                    ? node.Value<bool>("isOfficialFinalsStartList")
                    : node.Value<bool>("isOfficialStartList");
                if (!official) continue;

                var json = node.Value<string>("configurationData");
                if (string.IsNullOrWhiteSpace(json)) continue;

                // Springskytte stafett is tagged; individual lists carry Starters; everything else is
                // the precision Teams shape. Probe in that order — the shapes are not interchangeable.
                if (SafeIsStafett(json))
                {
                    items.AddRange(BuildStafettItems(json, memberId, s, node));
                    continue;
                }

                var springskytte = TryDeserialize<SpringskytteStartListConfig>(json);
                if (springskytte?.Starters is { Count: > 0 })
                {
                    items.AddRange(BuildSpringskytteItems(springskytte, memberId, s, node));
                    continue;
                }

                var precision = TryDeserialize<StartListConfiguration>(json);
                if (precision?.Teams is { Count: > 0 })
                {
                    items.AddRange(BuildPrecisionItems(precision, memberId, s, node, isFinals));
                }
            }

            return items;
        }

        private List<ScheduleItem> BuildPrecisionItems(
            StartListConfiguration cfg, int memberId, MySchedule s, IPublishedContent node, bool isFinals)
        {
            var items = new List<ScheduleItem>();
            var url = node.Url();

            foreach (var team in cfg.Teams!)
            {
                var mine = team.Shooters?.Where(sh => sh.MemberId == memberId).ToList();
                if (mine == null || mine.Count == 0) continue;

                // Team-level Date is the only reliable multi-day signal (added 2026-07). When it's
                // absent we fall back to the competition date, but ONLY for a single-day competition —
                // on a multi-day comp that would silently put Sunday's skjutlag on Saturday, so the
                // freeform label becomes the day heading instead and no absolute time is claimed.
                var teamDate = ParseDate(team.Date);
                var (dayKey, dayLabel, resolvedDate) = ResolveDay(teamDate, team.Label, s);

                var startTod = ParseTimeOfDay(team.StartTime);
                var endTod = ParseTimeOfDay(team.EndTime);
                var startsAt = Combine(resolvedDate, startTod);
                var endsAt = Combine(resolvedDate, endTod);

                foreach (var sh in mine)
                {
                    var where = $"Skjutlag {team.TeamNumber}";
                    if (!string.IsNullOrWhiteSpace(team.Label)) where += $" ({team.Label})";
                    if (sh.Position > 0) where += $" · plats {sh.Position}";

                    items.Add(new ScheduleItem
                    {
                        Kind = ScheduleItemKind.Skytte,
                        Title = isFinals
                            ? $"Final — klass {DisplayClass(sh.WeaponClass, sh.ChampionshipClass)}"
                            : $"Klass {DisplayClass(sh.WeaponClass, null)}",
                        Where = where,
                        Detail = sh.QualificationRank is int r && r > 0 ? $"Kvalplacering {r}" : null,
                        StartsAt = startsAt,
                        EndsAt = endsAt,
                        TimeLabel = BuildTimeLabel(team.StartTime, team.EndTime),
                        DayKey = dayKey,
                        DayLabel = dayLabel,
                        SortHint = startTod?.TotalMinutes is double m ? (int)m : team.TeamNumber,
                        Link = url,
                        LinkText = isFinals ? "Finalstartlista" : "Startlista",
                        Icon = "bi-bullseye",
                        SourceKey = $"{(isFinals ? "final" : "lag")}-{node.Id}-{team.TeamNumber}-{sh.WeaponClass}",
                    });
                }
            }
            return items;
        }

        private List<ScheduleItem> BuildSpringskytteItems(
            SpringskytteStartListConfig cfg, int memberId, MySchedule s, IPublishedContent node)
        {
            var items = new List<ScheduleItem>();
            // Springskytte already carries a per-list date (multi-day comps run the same clock time on
            // different days), so it needs no fallback gymnastics.
            var listDate = ParseDate(cfg.ListDate);

            foreach (var st in cfg.Starters.Where(x => x.MemberId == memberId))
            {
                var (dayKey, dayLabel, resolvedDate) = ResolveDay(listDate, cfg.ListName, s);
                var tod = ParseTimeOfDay(st.StartTime);

                var where = string.IsNullOrWhiteSpace(cfg.ListName) ? null : cfg.ListName;
                if (st.StartOrder > 0) where = string.IsNullOrWhiteSpace(where)
                    ? $"Startnummer {st.StartOrder}" : $"{where} · startnummer {st.StartOrder}";

                items.Add(new ScheduleItem
                {
                    Kind = ScheduleItemKind.Skytte,
                    Title = $"Start — klass {st.WeaponClass}{(string.IsNullOrWhiteSpace(st.AgeGenderClass) ? "" : " " + st.AgeGenderClass)}",
                    Where = where,
                    StartsAt = Combine(resolvedDate, tod),
                    TimeLabel = FormatTod(tod) ?? st.StartTime,
                    DayKey = dayKey,
                    DayLabel = dayLabel,
                    SortHint = tod?.TotalMinutes is double m ? (int)m : st.StartOrder,
                    Link = $"/startlista/{s.CompetitionId}",
                    LinkText = "Startlista",
                    Icon = "bi-stopwatch",
                    SourceKey = $"spring-{node.Id}-{st.StartOrder}-{st.WeaponClass}",
                });
            }
            return items;
        }

        private List<ScheduleItem> BuildStafettItems(string json, int memberId, MySchedule s, IPublishedContent node)
        {
            var items = new List<ScheduleItem>();
            var cfg = TryDeserialize<SpringskytteStafettStartListConfig>(json);
            if (cfg?.Teams == null) return items;

            var listDate = ParseDate(cfg.ListDate);

            foreach (var team in cfg.Teams)
            {
                var me = team.Members?.FirstOrDefault(m => m.MemberId == memberId);
                if (me == null) continue;

                var (dayKey, dayLabel, resolvedDate) = ResolveDay(listDate, cfg.ListName, s);
                var tod = ParseTimeOfDay(string.IsNullOrWhiteSpace(team.StartTime) ? cfg.CommonStartTime : team.StartTime);

                items.Add(new ScheduleItem
                {
                    Kind = ScheduleItemKind.Skytte,
                    Title = $"Stafett — {team.TeamName}",
                    Where = $"{team.StafettClass} · sträcka {me.LegNumber}{(me.IsSpare ? " (reserv)" : "")}",
                    Detail = "Gemensam start",
                    StartsAt = Combine(resolvedDate, tod),
                    TimeLabel = FormatTod(tod) ?? cfg.CommonStartTime,
                    DayKey = dayKey,
                    DayLabel = dayLabel,
                    SortHint = tod?.TotalMinutes is double m ? (int)m : team.StartOrder,
                    Link = $"/startlista/{s.CompetitionId}",
                    LinkText = "Startlista",
                    Icon = "bi-people",
                    SourceKey = $"stafett-{node.Id}-{team.TeamId}",
                });
            }
            return items;
        }

        // ---------------------------------------------------------------- shooting: Fältskytte patrols

        private sealed class PatrolRow
        {
            public int PatrolId { get; set; }
            public int PatrolNumber { get; set; }
            public DateTime? StartTime { get; set; }
            public string? WeaponGroup { get; set; }
            public string? Label { get; set; }
            public int Position { get; set; }
            public string ShootingClass { get; set; } = "";
            public string? Status { get; set; }
        }

        private List<ScheduleItem> BuildFaltItems(IPublishedContent comp, int memberId, MySchedule s)
        {
            var items = new List<ScheduleItem>();

            // Same gate as the public /patrullista wall screen — unpublished patrols stay invisible.
            if (!comp.Value<bool>("faltskyttePatrolsPublished")) return items;

            List<PatrolRow> rows;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                rows = scope.Database.Fetch<PatrolRow>(@"
SELECT p.Id AS PatrolId, p.PatrolNumber, p.StartTime, p.WeaponGroup, p.Label,
       m.Position, m.ShootingClass, m.Status
FROM FaltskyttePatrolMember m
JOIN FaltskyttePatrol p ON p.Id = m.PatrolId
WHERE p.CompetitionId = @0 AND m.MemberId = @1
ORDER BY p.PatrolNumber", s.CompetitionId, memberId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyScheduleService: patrol read failed for comp {Comp}", s.CompetitionId);
                return items;
            }

            foreach (var r in rows)
            {
                // Patrol StartTime is a full DateTime, so Fältskytte gets true multi-day for free.
                var (dayKey, dayLabel, _) = ResolveDay(r.StartTime?.Date, r.Label, s);
                var isDns = string.Equals(r.Status, "DNS", StringComparison.OrdinalIgnoreCase);

                var where = $"Patrull {r.PatrolNumber}";
                if (!string.IsNullOrWhiteSpace(r.Label)) where += $" ({r.Label})";
                if (r.Position > 0) where += $" · nr {r.Position}";

                items.Add(new ScheduleItem
                {
                    Kind = ScheduleItemKind.Skytte,
                    Title = $"Klass {r.ShootingClass}" + (isDns ? " — anmäld som ej startande" : ""),
                    Where = where,
                    Detail = string.IsNullOrWhiteSpace(r.WeaponGroup) ? null : $"Vapengrupp {r.WeaponGroup}",
                    StartsAt = isDns ? null : r.StartTime,
                    TimeLabel = r.StartTime?.ToString("HH:mm", Sv) ?? "Tid ej satt",
                    DayKey = dayKey,
                    DayLabel = dayLabel,
                    SortHint = r.StartTime?.TimeOfDay.TotalMinutes is double m ? (int)m : r.PatrolNumber,
                    Link = $"/patrullista/{s.CompetitionId}",
                    LinkText = "Patrullista",
                    Icon = "bi-bullseye",
                    SourceKey = $"patrull-{r.PatrolId}-{r.ShootingClass}",
                });
            }
            return items;
        }

        // ---------------------------------------------------------------- working

        private List<ScheduleItem> BuildFunctionaryItems(int competitionId, int memberId, MySchedule s)
        {
            var items = new List<ScheduleItem>();
            List<StaffAssignment> rows;
            List<StaffPass> passes;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                rows = scope.Database.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId);
                passes = rows.Count == 0 ? new List<StaffPass>() : scope.Database.Fetch<StaffPass>(
                    "SELECT * FROM StaffPass WHERE CompetitionId = @0", competitionId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyScheduleService: staffing read failed for comp {Comp}", competitionId);
                return items;
            }

            if (rows.Count == 0) return items;
            s.IsFunctionary = true;
            var passById = passes.ToDictionary(p => p.Id);

            foreach (var a in rows)
            {
                if (string.Equals(a.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase))
                    continue;   // said no — not part of their day

                DateTime? startsAt = a.StartsAt;
                DateTime? endsAt = a.EndsAt;
                string timeLabel;
                // StartsAt.Date → DayDate → pass date. DayDate is how "heldag på lördag" is expressed
                // without inventing a clock time (a fake 00:00 would fire a reminder at 23:30 the night
                // before), so it pins the DAY and leaves StartsAt null on purpose.
                DateTime? dayDate = a.StartsAt?.Date ?? a.DayDate?.Date;

                if (startsAt == null && a.PassId is int pid && passById.TryGetValue(pid, out var pass))
                {
                    // A structured pass gives a real date plus (usually) real clock times.
                    dayDate ??= pass.PassDate.Date;
                    var st = ParseTimeOfDay(pass.StartTime);
                    var en = ParseTimeOfDay(pass.EndTime);
                    startsAt = Combine(dayDate, st);
                    endsAt = Combine(dayDate, en);
                    var label = string.IsNullOrWhiteSpace(pass.Label) ? pass.PassDate.ToString("ddd d MMM", Sv) : pass.Label;
                    timeLabel = st == null && en == null ? label : $"{label} · {FormatTod(st)}–{FormatTod(en)}";
                }
                else if (startsAt != null)
                {
                    timeLabel = endsAt != null && endsAt.Value.Date == startsAt.Value.Date
                        ? $"{startsAt.Value:HH\\:mm}–{endsAt.Value:HH\\:mm}"
                        : startsAt.Value.ToString("HH:mm", Sv);
                }
                else
                {
                    // No shift and no pass. "Heldag" is the honest answer, not an invented 08:00.
                    timeLabel = "Heldag";
                }

                var (dayKey, dayLabel, _) = ResolveDay(dayDate, null, s);
                var isInvited = string.Equals(a.Status, StaffAssignmentStatus.Invited, StringComparison.OrdinalIgnoreCase);

                items.Add(new ScheduleItem
                {
                    Kind = ScheduleItemKind.Funktionar,
                    Title = ResolveRoleName(competitionId, s.Discipline, a.RoleKey, a.FunctionTitle),
                    Where = BuildScopeLabel(a),
                    Detail = a.Note,
                    StartsAt = startsAt,
                    EndsAt = endsAt,
                    TimeLabel = timeLabel,
                    DayKey = dayKey,
                    DayLabel = dayLabel,
                    Status = a.Status,
                    NeedsResponse = isInvited,
                    IsResponsible = a.IsResponsible,
                    SortHint = startsAt?.TimeOfDay.TotalMinutes is double m ? (int)m : 0,
                    Link = $"/bemanna?c={competitionId}",
                    LinkText = isInvited ? "Svara på inbjudan" : "Uppdraget",
                    Icon = "bi-person-badge",
                    SourceKey = $"uppdrag-{a.Id}",
                });
            }
            return items;
        }

        // ---------------------------------------------------------------- everyone's programme

        private List<ScheduleItem> BuildAgendaItems(int competitionId, MySchedule s, bool hasStaffItems)
        {
            var items = new List<ScheduleItem>();
            List<CompetitionAgendaItem> rows;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                rows = scope.Database.Fetch<CompetitionAgendaItem>(
                    "SELECT * FROM CompetitionAgendaItem WHERE CompetitionId = @0 ORDER BY ItemDate, StartTime", competitionId);
            }
            catch
            {
                return items;   // table not migrated yet → the feature is simply absent
            }

            // Crew-only DAYS (banbygge, återställning) are crew-only programmes. A shooter has no business
            // seeing "materielhämtning 07:00 onsdag", and the organiser shouldn't have to remember to set
            // the audience on every single row of a build day.
            var crewOnlyDays = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var days = scope.Database.Fetch<HpskSite.Models.Staffing.StaffDay>(
                    "SELECT * FROM StaffDay WHERE CompetitionId = @0", competitionId);
                foreach (var d in days)
                    if (!HpskSite.Models.Staffing.StaffDayKind.IsParticipantFacing(d.Kind))
                        crewOnlyDays.Add(d.DayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            }
            catch { /* no day list yet → nothing is crew-only */ }

            foreach (var r in rows)
            {
                // Audience gate: staff-only rows are hidden from pure shooters and vice versa.
                if (string.Equals(r.Audience, AgendaAudience.Staff, StringComparison.OrdinalIgnoreCase) && !hasStaffItems) continue;
                if (string.Equals(r.Audience, AgendaAudience.Shooters, StringComparison.OrdinalIgnoreCase) && !s.IsRegistered) continue;
                if (!hasStaffItems && r.ItemDate is { } id
                    && crewOnlyDays.Contains(id.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) continue;

                var st = ParseTimeOfDay(r.StartTime);
                var en = ParseTimeOfDay(r.EndTime);
                var (dayKey, dayLabel, resolvedDate) = ResolveDay(r.ItemDate?.Date, null, s);

                items.Add(new ScheduleItem
                {
                    Kind = ScheduleItemKind.Praktiskt,
                    Title = r.Title,
                    Where = r.Location,
                    Detail = r.Note,
                    StartsAt = Combine(resolvedDate, st),
                    EndsAt = Combine(resolvedDate, en),
                    TimeLabel = st == null ? "Under dagen" : (en == null ? FormatTod(st)! : $"{FormatTod(st)}–{FormatTod(en)}"),
                    DayKey = dayKey,
                    DayLabel = dayLabel,
                    SortHint = st?.TotalMinutes is double m ? (int)m : 0,
                    Icon = string.IsNullOrWhiteSpace(r.Icon) ? "bi-info-circle" : r.Icon,
                    SourceKey = $"program-{r.Id}",
                });
            }
            return items;
        }

        // ---------------------------------------------------------------- conflicts

        /// <summary>
        /// Flags genuinely overlapping commitments ("du är markör 13–16 men skjuter klass B 13:30").
        ///
        /// Deliberately conservative: it never invents a duration. A pair is only in conflict when one
        /// item has a real end and the other starts inside it, or when two open-ended items start at
        /// exactly the same minute. Praktiskt rows are context, not commitments, so they're excluded —
        /// otherwise a two-hour "Anmälan öppen" band would flag every morning start as a clash.
        /// </summary>
        private static void DetectConflicts(List<ScheduleItem> items)
        {
            var real = items
                .Where(i => i.StartsAt != null && i.Kind != ScheduleItemKind.Praktiskt)
                .ToList();

            for (var i = 0; i < real.Count; i++)
            {
                for (var j = i + 1; j < real.Count; j++)
                {
                    var a = real[i];
                    var b = real[j];
                    if (!Overlaps(a, b)) continue;
                    a.ConflictsWith.Add(Describe(b));
                    b.ConflictsWith.Add(Describe(a));
                }
            }

            static bool Overlaps(ScheduleItem a, ScheduleItem b)
            {
                var aStart = a.StartsAt!.Value;
                var bStart = b.StartsAt!.Value;
                if (a.EndsAt is DateTime aEnd && bStart >= aStart && bStart < aEnd) return true;
                if (b.EndsAt is DateTime bEnd && aStart >= bStart && aStart < bEnd) return true;
                return a.EndsAt == null && b.EndsAt == null && aStart == bStart;
            }

            static string Describe(ScheduleItem i)
            {
                var w = string.IsNullOrWhiteSpace(i.Where) ? "" : $" ({i.Where})";
                return $"{i.Title}{w}, {i.TimeLabel}";
            }
        }

        // ---------------------------------------------------------------- grouping

        private static List<ScheduleDay> GroupIntoDays(List<ScheduleItem> items)
        {
            return items
                .GroupBy(i => i.DayKey)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g =>
                {
                    var date = ParseDate(g.Key);
                    return new ScheduleDay
                    {
                        DayKey = g.Key,
                        DayLabel = g.First().DayLabel,
                        Date = date,
                        IsToday = date?.Date == DateTime.Today,
                        Items = g
                            .OrderBy(i => i.StartsAt ?? DateTime.MaxValue)
                            .ThenBy(i => i.SortHint)
                            .ThenBy(i => i.Title, StringComparer.Create(Sv, false))
                            .ToList(),
                    };
                })
                .ToList();
        }

        /// <summary>
        /// Decides which day a row belongs to. Dated rows sort naturally by "yyyy-MM-dd". Undated rows
        /// on a SINGLE-day competition safely inherit the competition date. Undated rows on a MULTI-day
        /// competition fall back to their freeform label as the heading and keep no absolute date —
        /// the "z:" prefix parks those groups after all the dated ones.
        /// </summary>
        private (string dayKey, string dayLabel, DateTime? date) ResolveDay(DateTime? date, string? label, MySchedule s)
        {
            if (date != null)
                return (date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FormatDayLabel(date.Value), date);

            if (!s.IsMultiDay && s.CompDate != null)
            {
                var d = s.CompDate.Value.Date;
                return (d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), FormatDayLabel(d), d);
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                if (s.IsMultiDay && s.Warnings.All(w => !w.StartsWith("Tävlingen pågår")))
                {
                    s.Warnings.Add("Tävlingen pågår flera dagar men startlistan saknar datum — kontrollera vilken dag som gäller med arrangören.");
                }
                return ("z:" + label, label!, null);
            }

            return ("zz", "Tid meddelas", null);
        }

        private static string FormatDayLabel(DateTime d)
        {
            var label = d.ToString("dddd d MMMM", Sv);
            if (label.Length > 0) label = char.ToUpper(label[0], Sv) + label.Substring(1);
            if (d.Date == DateTime.Today) return $"Idag — {label}";
            if (d.Date == DateTime.Today.AddDays(1)) return $"Imorgon — {label}";
            return label;
        }

        // ---------------------------------------------------------------- small helpers

        private static string DisplayClass(string weaponClass, string? championshipClass)
            => !string.IsNullOrWhiteSpace(championshipClass) ? championshipClass! : weaponClass;

        /// <summary>
        /// The role name the functionary sees in their own schedule. Resolved through the merged catalog,
        /// so an arrangör-named role ("Vapenkontroll", "Vakt på löpslingan") reads exactly as they typed it.
        /// Falls back to the raw key, never to an empty title.
        /// </summary>
        private string ResolveRoleName(int competitionId, string discipline, string roleKey, string? functionTitle)
        {
            var name = _roles.NameFor(competitionId, discipline, roleKey);
            if (string.IsNullOrWhiteSpace(name)) name = roleKey;
            return string.IsNullOrWhiteSpace(functionTitle) ? name : $"{name} — {functionTitle}";
        }

        /// <summary>Mirrors StaffingService.BuildScopeLabel — a label only. Never station detail.</summary>
        private static string BuildScopeLabel(StaffAssignment a)
        {
            if (string.IsNullOrEmpty(a.ScopeType) || string.Equals(a.ScopeType, StaffScopeType.All, StringComparison.OrdinalIgnoreCase))
                return "Hela tävlingen";
            var label = $"{a.ScopeType} {a.ScopeKey}".Trim();
            if (a.TargetFrom is > 0)
            {
                label += a.TargetTo is > 0 && a.TargetTo != a.TargetFrom
                    ? $" · tavlor {a.TargetFrom}–{a.TargetTo}"
                    : $" · tavla {a.TargetFrom}";
            }
            return label;
        }

        private static string BuildTimeLabel(string? start, string? end)
        {
            var s = ParseTimeOfDay(start);
            var e = ParseTimeOfDay(end);
            if (s == null && e == null) return "Tid ej satt";
            if (s != null && e != null) return $"{FormatTod(s)}–{FormatTod(e)}";
            return FormatTod(s ?? e)!;
        }

        private static string? FormatTod(TimeSpan? t)
            => t == null ? null : $"{(int)t.Value.TotalHours:00}:{t.Value.Minutes:00}";

        internal static TimeSpan? ParseTimeOfDay(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var txt = s.Trim();
            string[] formats = { @"hh\:mm", @"h\:mm", @"hh\:mm\:ss", @"h\:mm\:ss" };
            if (TimeSpan.TryParseExact(txt, formats, CultureInfo.InvariantCulture, out var ts)) return ts;
            if (TimeSpan.TryParse(txt, CultureInfo.InvariantCulture, out ts)) return ts;
            return null;
        }

        internal static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d.Date : null;
        }

        private static DateTime? Combine(DateTime? date, TimeSpan? tod)
            => date == null || tod == null ? null : date.Value.Date.Add(tod.Value);

        private static DateTime? NullIfDefault(DateTime d) => d == default ? null : d;

        /// <summary>
        /// An unset Umbraco date property does NOT read back as null — `Value&lt;DateTime?&gt;` yields
        /// DateTime.MinValue (or another pre-1900 sentinel). Taking that at face value made every
        /// competition look like it ended in year 1, which silently emptied the cross-competition
        /// lookup. Competition.cshtml guards the same way for the same reason.
        /// </summary>
        private static DateTime? RealDate(DateTime? d)
            => d.HasValue && d.Value > DateTime.MinValue && d.Value.Year > 1900 ? d : null;

        private static string ReadCompetitionType(IPublishedContent comp)
        {
            // competitionType can be a FlexibleDropdown whose typed read throws on legacy plain-string
            // values — same defensive pattern as CompetitionUrlProvider.ReadScopeValue.
            try
            {
                var raw = comp.Value("competitionType");
                return raw switch
                {
                    string str => str,
                    null => "",
                    _ => raw.ToString() ?? "",
                };
            }
            catch { return ""; }
        }

        private static T? TryDeserialize<T>(string json) where T : class
        {
            try { return JsonConvert.DeserializeObject<T>(json); }
            catch { return null; }
        }

        private static bool SafeIsStafett(string json)
        {
            try { return SpringskytteController.IsStafettConfig(json); }
            catch { return false; }
        }

        private bool SafeIsRegistered(int competitionId, int memberId)
        {
            try { return _audience.IsRegistered(competitionId, memberId); }
            catch { return false; }
        }

        // ---------------------------------------------------------------- cross-competition lookup

        private List<int> GetCompetitionsInWindow(DateTime fromDate, DateTime toDate)
        {
            var ids = new List<int>();
            try
            {
                using var ctxRef = _ctxFactory.EnsureUmbracoContext();
                var root = ctxRef.UmbracoContext.Content?.GetAtRoot().FirstOrDefault();
                var hub = root?.Children().FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                if (hub == null) return ids;

                foreach (var c in hub.Descendants().Where(x => x.ContentType.Alias == "competition"))
                {
                    var start = NullIfDefault(c.Value<DateTime>("competitionDate"));
                    if (start == null) continue;
                    var end = RealDate(c.Value<DateTime?>("competitionEndDate")) ?? start;
                    // Any overlap between [start,end] and [fromDate,toDate].
                    if (end.Value.Date >= fromDate.Date && start.Value.Date <= toDate.Date) ids.Add(c.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyScheduleService: competition window scan failed");
            }
            return ids;
        }

        /// <summary>The competition's run as (start, end); end is null for a single-day competition.</summary>
        private (DateTime? start, DateTime? end) GetCompetitionSpan(int competitionId)
        {
            try
            {
                using var ctxRef = _ctxFactory.EnsureUmbracoContext();
                var c = ctxRef.UmbracoContext.Content?.GetById(competitionId);
                if (c == null) return (null, null);
                return (NullIfDefault(c.Value<DateTime>("competitionDate")),
                        RealDate(c.Value<DateTime?>("competitionEndDate")));
            }
            catch { return (null, null); }
        }

        private sealed class RegRow
        {
            public int CompetitionId { get; set; }
        }

        /// <summary>
        /// Which of the candidate competitions this member is registered in. Registrations are
        /// Save()-only nodes, so the published cache would undercount — this is the inverse of
        /// ParticipantAudienceResolver's projection, filtered by member instead of by competition.
        /// </summary>
        private List<int> RegisteredCompetitionIds(int memberId, List<int> candidateIds)
        {
            if (candidateIds.Count == 0) return new List<int>();
            var inList = string.Join(",", candidateIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            var sql = $@"
SELECT DISTINCT comp.id AS CompetitionId
FROM umbracoNode comp
JOIN umbracoNode hub        ON hub.parentId = comp.id
JOIN umbracoContent hc      ON hc.nodeId = hub.id
JOIN cmsContentType hct     ON hct.nodeId = hc.contentTypeId AND hct.alias = 'competitionRegistrationsHub'
JOIN umbracoNode n          ON n.parentId = hub.id AND n.trashed = 0
JOIN umbracoContent rc      ON rc.nodeId = n.id
JOIN cmsContentType rct     ON rct.nodeId = rc.contentTypeId AND rct.alias = 'competitionRegistration'
JOIN umbracoContentVersion cv ON cv.nodeId = n.id AND cv.[current] = 1
JOIN umbracoPropertyData pd ON pd.versionId = cv.id
JOIN cmsPropertyType pt     ON pt.id = pd.propertyTypeId AND pt.Alias = 'memberId'
WHERE comp.id IN ({inList})
  AND COALESCE(CAST(pd.intValue AS nvarchar(50)), pd.varcharValue, pd.textValue) = @0";

            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.Fetch<RegRow>(sql, memberId.ToString(CultureInfo.InvariantCulture))
                    .Select(r => r.CompetitionId).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "MyScheduleService: registration lookup failed for member {Member}", memberId);
                return new List<int>();
            }
        }
    }
}
