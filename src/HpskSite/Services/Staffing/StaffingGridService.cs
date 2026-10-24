using System.Globalization;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Builds the Bemanning grid: roles as rows, the plan's DAYS as columns, people in the cells.
    ///
    /// <para>Lives in a service rather than the controller because the SCREEN and the PRINTOUT must be the
    /// same grid. The printable sheet used to render its own role-grouped list, which is why one person
    /// showed up on row after row and the day axis was missing entirely — the thing the organiser reads on
    /// paper looked nothing like the thing they built.</para>
    /// </summary>
    public class StaffingGridService
    {
        private readonly StaffingService _staffing;
        private readonly RoleCatalogService _roles;
        private readonly StaffDayService _days;
        private readonly StaffPassService _pass;
        private readonly CompetitionPeopleService _people;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<StaffingGridService> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.MemberPropertyReader _memberProps;

        public StaffingGridService(
            StaffingService staffing,
            RoleCatalogService roles,
            StaffDayService days,
            StaffPassService pass,
            CompetitionPeopleService people,
            IMemberService memberService,
            ClubService clubService,
            IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<StaffingGridService> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.MemberPropertyReader memberProps)
        {
            _memberProps = memberProps;
            _competitions = competitions;
            _staffing = staffing;
            _roles = roles;
            _days = days;
            _pass = pass;
            _people = people;
            _memberService = memberService;
            _clubService = clubService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
        }

        private DateTime? GetCompetitionDate(int competitionId)
        {
            try
            {
                return _competitions.Get(competitionId)?.CompetitionDate;
            }
            catch { return null; }
        }

        private static (TimeSpan start, TimeSpan end)? ParseShiftRange(string? label) => StaffTimeCoverage.ParseRange(label);

        /// <summary>Ett pass klockslag som ett f\u00f6nster; null utan b\u00e5da tiderna.</summary>
        private static (TimeSpan Start, TimeSpan End)? PassWindow(StaffPassView p) =>
            StaffTimeCoverage.ParseRange(string.IsNullOrEmpty(p.StartTime) || string.IsNullOrEmpty(p.EndTime) ? null : p.StartTime + "-" + p.EndTime);

        /// <summary>
        /// Omg\u00e5ng 3 (3.2): banans stationer \u2014 en kopplad f\u00e4ltt\u00e4vlings antal stationer, annars omr\u00e5denas stationer i hela
        /// planeringsfamiljen. Tom lista = inga stationsrader.
        /// </summary>
        private List<int> PlanStations(StaffOwner owner, string? discipline)
        {
            try
            {
                if (owner.Comp > 0 && FunctionaryRoles.FaltFamily.Contains(discipline ?? "", StringComparer.OrdinalIgnoreCase)
                    && _competitions.Get(owner.Comp)?.NumberOfSeriesOrStations is int n && n > 0)
                    return Enumerable.Range(1, Math.Min(n, HpskSite.Models.Planning.PlanningStations.Max)).ToList();
                return _pass.FamilyAreaStations(owner);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: station list failed for {Owner}", owner.MemoKey);
                return new List<int>();
            }
        }

        private static bool ShiftsOverlap(StaffAssignmentView a, StaffAssignmentView b)
        {
            if (a.DateKey != null && b.DateKey != null && a.DateKey != b.DateKey) return false;
            if (a.PassId != null && b.PassId != null) return a.PassId == b.PassId;
            var ra = ParseShiftRange(a.ShiftLabel);
            var rb = ParseShiftRange(b.ShiftLabel);
            if (ra == null || rb == null) return false;
            return ra.Value.start < rb.Value.end && rb.Value.start < ra.Value.end;
        }

        private static string PersonKeyOf(StaffAssignmentView a)
            => a.MemberId is int m && m > 0 ? "m:" + m : "n:" + (a.DisplayName ?? "").Trim().ToLowerInvariant();


        // ======================= Bemanning grid (roll × dag) =======================

        /// <summary>
        /// Projects the roster into the grid shape. Built ON TOP of <c>BuildRoster</c> rather than reading
        /// StaffAssignment directly, so role names, scope labels, shift labels and the Fält station-chief
        /// mirror stay identical to every other surface — the grid is a second VIEW, never a second truth.
        /// </summary>
        public GridResponse BuildGrid(StaffOwner owner, string? discipline, int viewerId)
        {
            var competitionId = owner.Comp;
            var roster = _staffing.BuildRoster(owner, discipline, canEdit: true);
            var resp = new GridResponse { Discipline = discipline ?? "", CanEdit = true };

            var all = roster.Groups.SelectMany(g => g.Assignments).ToList();

            // ---- columns: the DAYS OF THE PLAN --------------------------------------------------
            // Owned by the arrangör (StaffDay), seeded once from the competition span. The days you STAFF
            // are not the days you COMPETE — build-up, materiel runs and teardown carry crew and sit
            // outside the span — so the span may only seed, never constrain.
            // The competition date is deliberately NOT unioned in here: doing that produced a phantom
            // column the organiser never asked for and could not remove. Dates that actually CARRY crew
            // are unioned in, because work is proof that the day is part of the plan.
            var days = _days.EnsureSeeded(owner, viewerId);
            var withWork = new HashSet<string>(
                all.Where(a => !string.IsNullOrEmpty(a.DateKey)).Select(a => a.DateKey!), StringComparer.Ordinal);

            var passes = SafeGetPasses(owner);
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            var byDate = new SortedDictionary<string, GridColumn>(StringComparer.Ordinal);

            void AddColumn(string key, StaffDay? day)
            {
                if (byDate.ContainsKey(key)) return;
                var parsed = DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)
                    ? dt : (DateTime?)null;
                var dayPasses = passes.Where(p => p.Date == key).ToList();
                var kind = day?.Kind ?? StaffDayKind.Competition;
                byDate[key] = new GridColumn
                {
                    Key = key,
                    Label = parsed?.ToString("ddd d MMM", sv) ?? key,
                    DayLabel = string.IsNullOrWhiteSpace(day?.Label) ? null : day!.Label,
                    Kind = kind,
                    KindLabel = StaffDayKind.Label(kind),
                    IsPlanned = day != null,
                    DayId = day?.Id ?? 0,
                    HasAssignments = withWork.Contains(key),
                    TimeLabel = DayTimeLabel(dayPasses),
                    PassIds = dayPasses.Select(p => p.Id).ToList(),
                    Passes = dayPasses.Where(p => PassWindow(p) != null).Select(p => new GridPassCover
                    {
                        PassId = p.Id,
                        Label = string.IsNullOrWhiteSpace(p.Label) ? $"{p.StartTime}–{p.EndTime}" : $"{p.Label} {p.StartTime}–{p.EndTime}",
                    }).ToList(),
                };
            }

            foreach (var d in days) AddColumn(d.DayDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d);
            foreach (var p in passes) if (!string.IsNullOrEmpty(p.Date)) AddColumn(p.Date!, null);
            // Work on a day nobody planned still has to be visible — never drop people on the floor.
            foreach (var key in withWork) AddColumn(key, null);

            resp.Columns.AddRange(byDate.Values);

            var single = resp.Columns.Count == 1 ? resp.Columns[0].Key : null;
            // The undated bucket appears ONLY when something is genuinely undated. On a single-day plan
            // those rows belong to that day, so no bucket is needed at all.
            if (all.Any(a => string.IsNullOrEmpty(a.DateKey)) && single == null)
                resp.Columns.Add(new GridColumn
                {
                    Key = "",
                    Label = "Utan datum",
                    Kind = StaffDayKind.Competition,
                    KindLabel = "",
                    IsPlanned = false,
                });

            // ---- rows: one per role, split by scope where the role is scoped ---------------------
            var catalog = _roles.For(owner, discipline);
            var clubs = ResolveClubNames(all);

            // Per-role need drives the number inside each cell. Coverage stopped being a screen of its
            // own precisely because nobody ever found that screen — on every real competition it was zero.
            // Behovet per dag (Stefan 2026-10-23): rollens antal för alla dagar, och dagar som avviker.
            var needRows = new List<StaffCrewNeed>();
            try { needRows = _pass.GetAllCrewNeeds(owner); }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: crew-need read failed for {Comp}", competitionId); }
            var datedKeys = resp.Columns.Where(c => !string.IsNullOrEmpty(c.Key)).Select(c => c.Key).ToList();
            // Omgång 3 (3.2): stationerna (en rad per station för stationsrollerna) och passens fönster.
            var stations = PlanStations(owner, discipline);
            var passWindows = passes.ToDictionary(p => p.Id, PassWindow);
            static DateTime Day(string key) => DateTime.ParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture);

            foreach (var role in catalog)
            {
                var mine = all.Where(a => string.Equals(a.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                // NULL and "All" both mean "hela tävlingen" — grouping on the raw value split one function
                // into two identical-looking rows depending on which surface created the assignment.
                static string? NormScope(string? t)
                    => string.IsNullOrEmpty(t) || string.Equals(t, StaffScopeType.All, StringComparison.OrdinalIgnoreCase)
                        ? null : t;

                var scopeKeys = mine
                    .Select(a => (ScopeType: NormScope(a.ScopeType), a.ScopeKey, a.ScopeLabel))
                    .Distinct()
                    .ToList();
                // Omgång 3 (3.2): en stationsroll får en rad per station, även tom — "station 5, markörer" ska gå att se
                // och fylla. Raden för hela tävlingen står kvar bara om någon står där.
                var stationRole = stations.Count > 0 && string.Equals(role.DefaultScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase);
                if (stationRole)
                    foreach (var n in stations)
                    {
                        var k = n.ToString(CultureInfo.InvariantCulture);
                        if (!scopeKeys.Any(x => string.Equals(x.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase) && x.ScopeKey == k))
                            scopeKeys.Add((StaffScopeType.Station, k, $"Station {k}"));
                    }
                scopeKeys = scopeKeys
                    .OrderBy(x => x.ScopeType == null ? 0 : 1)
                    .ThenBy(x => int.TryParse(x.ScopeKey, out var sn) ? sn : int.MaxValue)
                    .ThenBy(x => x.ScopeKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (scopeKeys.Count == 0) scopeKeys.Add((null, null, ""));
                // En stationsrad läser behovet "per station" (varje station, eller just den här) — och, när inget sådant
                // gäller stationen, rollens antal (som förut, när varje stationsrad visade samma tal).
                bool StationNeedFor(string? key) => needRows.Any(n => string.Equals(n.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(n.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase)
                    && (string.IsNullOrEmpty(n.ScopeKey) || string.Equals(n.ScopeKey, key, StringComparison.OrdinalIgnoreCase)));

                foreach (var (scopeType, scopeKey, scopeLabel) in scopeKeys)
                {
                    var isStation = string.Equals(scopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase);
                    var needKind = isStation && StationNeedFor(scopeKey) ? CrewNeedScope.Station : CrewNeedScope.All;
                    var needKey = needKind == CrewNeedScope.Station ? scopeKey : null;
                    var row = new GridRow
                    {
                        RoleKey = role.Key,
                        RoleName = role.DisplayName,
                        ScopeType = scopeType,
                        ScopeKey = scopeKey,
                        ScopeLabel = string.Equals(scopeLabel, "Hela tävlingen", StringComparison.OrdinalIgnoreCase) ? null : scopeLabel,
                        IsCustom = _roles.IsCustom(owner, role.Key),
                        SupportsTargetRange = role.SupportsTargetRange,
                        SupportsFunctionTitle = role.SupportsFunctionTitle,
                        DefaultScopeType = role.DefaultScopeType,
                        Needed = CrewNeedRules.For(needRows, needKind, role.Key, null, needKey, null),
                        SortOrder = role.SortOrder,
                        HasOwnStationNeed = isStation && needRows.Any(n => string.Equals(n.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase)
                            && string.Equals(n.RoleKey, role.Key, StringComparison.OrdinalIgnoreCase) && string.Equals(n.ScopeKey, scopeKey, StringComparison.OrdinalIgnoreCase)),
                    };
                    foreach (var k in datedKeys)
                        row.NeededByDay[k] = CrewNeedRules.For(needRows, needKind, role.Key, Day(k), needKey, null);
                    foreach (var p in passes.Where(p => !string.IsNullOrEmpty(p.Date) && datedKeys.Contains(p.Date!)))
                        row.NeededByPass[p.Id] = CrewNeedRules.For(needRows, needKind, role.Key, Day(p.Date!), needKey, p.Id);
                    if (!row.HasNeed) { row.NeededByDay.Clear(); row.NeededByPass.Clear(); }

                    foreach (var a in mine.Where(a => NormScope(a.ScopeType) == scopeType && a.ScopeKey == scopeKey))
                    {
                        var colKey = a.DateKey ?? single ?? "";
                        if (!row.Cells.TryGetValue(colKey, out var list))
                            row.Cells[colKey] = list = new List<GridEntry>();
                        list.Add(new GridEntry
                        {
                            Id = a.Id,
                            MemberId = a.MemberId,
                            DisplayName = a.DisplayName,
                            ClubName = a.MemberId is int mid && clubs.TryGetValue(mid, out var cn) ? cn : null,
                            // A whole-day person renders as a bare name; a chip appears only where a real
                            // time exists. Five identical chips would read as five people.
                            TimeLabel = a.ShiftLabel ?? a.PassLabel,
                            ScopeLabel = row.ScopeLabel,
                            Status = a.Status,
                            IsResponsible = a.IsResponsible,
                            IsExternal = a.MemberId is not > 0,
                            OriginalName = a.OriginalName,
                            Email = a.Email,
                            HasContact = a.MemberId is > 0 || !string.IsNullOrWhiteSpace(a.Email),
                            ReadOnly = a.ReadOnly,
                            Note = a.Note,
                            PassId = a.PassId,
                        });
                        row.Filled++;
                    }

                    // Omgång 3 (3.2): tiden är sanningen — flest samtidigt per dag, och per pass när dagen har flera pass.
                    (TimeSpan Start, TimeSpan End)? SpanOf(GridEntry e) =>
                        e.PassId is int pid && passWindows.TryGetValue(pid, out var w) && w != null ? w : StaffTimeCoverage.ParseRange(e.TimeLabel);
                    foreach (var k in datedKeys)
                    {
                        var list = row.Cells.TryGetValue(k, out var l) ? l : new List<GridEntry>();
                        if (list.Count > 0) row.FilledByDay[k] = StaffTimeCoverage.Peak(list.Select(SpanOf));
                        var dayPasses = passes.Where(p => p.Date == k && passWindows.TryGetValue(p.Id, out var w) && w != null).ToList();
                        if (dayPasses.Count < 2 || (list.Count == 0 && !row.HasNeed)) continue;
                        row.PassCover[k] = dayPasses.Select(p => new GridPassCover
                        {
                            PassId = p.Id,
                            Label = string.IsNullOrWhiteSpace(p.Label) ? $"{p.StartTime}–{p.EndTime}" : p.Label,
                            Filled = StaffTimeCoverage.Peak(list.Where(e => e.PassId == null || e.PassId == p.Id).Select(SpanOf), passWindows[p.Id]),
                            Needed = row.NeededByPass.TryGetValue(p.Id, out var pn) ? pn : row.NeedFor(k),
                        }).ToList();
                    }
                    resp.Rows.Add(row);
                }
            }

            resp.TotalAssigned = all.Count;
            resp.ExternalCount = all.Count(a => a.MemberId is not > 0);
            // Who can't be reached at all: no account AND no e-mail. These are the rows that silently
            // deliver nothing, and the organiser needs them as a list, not as a count.
            resp.Unreachable = all
                .Where(a => a.MemberId is not > 0 && string.IsNullOrWhiteSpace(a.Email))
                .Select(a => a.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            // Volunteers with no assignment. They are, by definition, absent from every cell — so the
            // grid has to be told about them explicitly or they are invisible until someone goes looking.
            // Same predicate as the people layer's UnassignedVolunteerCount; sharing it is what keeps the
            // strip, the badge and the filtered list from disagreeing.
            // The ARRANGÖR'S order, not alphabetical. Alphabetical is tidy and predictable but it splits
            // the groups a staffing plan is actually read in — Start/Mål together, Skjutplats together —
            // which is how the source sheet is laid out and how the organiser scans it. So the rows sort
            // on a SortOrder they set by dragging; roles they have never placed trail behind, in catalog
            // order, and a Swedish comparer only breaks ties (å/ä/ö sort after z).
            var svCmp = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), ignoreCase: true);
            resp.Rows = resp.Rows
                .OrderBy(r => r.SortOrder)
                .ThenBy(r => r.RoleName, svCmp)
                // Station 2 före station 10.
                .ThenBy(r => r.ScopeType == null ? 0 : 1)
                .ThenBy(r => int.TryParse(r.ScopeKey, out var sn) ? sn : int.MaxValue)
                .ThenBy(r => r.ScopeLabel ?? "", svCmp)
                .ToList();

            // Double-booking, surfaced where the organiser is standing. It used to be computed only for
            // MEMBERS and rendered only on the retired Roller view — so on a plan that is 90% free text it
            // checked almost nobody, and showed the result to nobody.
            foreach (var grp in all.Where(a => !a.ReadOnly && !string.IsNullOrWhiteSpace(a.DisplayName))
                                   .GroupBy(PersonKeyOf))
            {
                var list = grp.ToList();
                for (var i = 0; i < list.Count; i++)
                    for (var j = i + 1; j < list.Count; j++)
                    {
                        if (!ShiftsOverlap(list[i], list[j])) continue;
                        resp.Clashes.Add(new GridClash
                        {
                            PersonKey = grp.Key,
                            Name = list[i].DisplayName,
                            DateKey = list[i].DateKey,
                            A = $"{list[i].RoleName} ({list[i].ShiftLabel ?? list[i].PassLabel ?? "heldag"})",
                            B = $"{list[j].RoleName} ({list[j].ShiftLabel ?? list[j].PassLabel ?? "heldag"})",
                        });
                    }
            }

            // Time gaps INSIDE a cell: "han gick hem kl 09 och ingen tog över". Deliberately measured only
            // between the cell's own first and last shift — flagging the rest of the day would invent a
            // requirement nobody stated and cry wolf on every plan.
            foreach (var row in resp.Rows)
            {
                foreach (var kv in row.Cells)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    var spans = kv.Value
                        .Select(e => ParseShiftRange(e.TimeLabel))
                        .Where(x => x != null).Select(x => x!.Value)
                        .OrderBy(x => x.start).ToList();
                    // An untimed row is present all day, so it can't leave a hole.
                    if (spans.Count < 2 || spans.Count != kv.Value.Count) continue;

                    var cursor = spans[0].end;
                    foreach (var s in spans.Skip(1))
                    {
                        if (s.start > cursor)
                            row.Gaps.Add(new GridGap
                            {
                                DateKey = kv.Key,
                                From = $"{cursor:hh\\:mm}",
                                To = $"{s.start:hh\\:mm}",
                            });
                        if (s.end > cursor) cursor = s.end;
                    }
                }
            }

            try
            {
                // includePrep:false — the strip only needs who volunteered, and the prep breakdown is the
                // expensive half of the people build.
                var people = _people.Build(owner, discipline, canEdit: true, includePrep: false);
                resp.Volunteers = people.People
                    .Where(p => p.Volunteer != null && p.Assignments.Count == 0)
                    .Select(p => new GridVolunteer { Key = p.Key, Name = p.DisplayName, Summary = p.Volunteer?.SlotsSummary })
                    .ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: volunteer strip failed for {Comp}", competitionId); }

            return resp;
        }

        private List<StaffPassView> SafeGetPasses(StaffOwner owner)
        {
            try { return _pass.GetPasses(owner); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: pass lookup failed for {Owner}", owner.MemoKey);
                return new List<StaffPassView>();
            }
        }

        private static string? DayTimeLabel(List<StaffPassView> dayPasses)
        {
            var starts = dayPasses.Select(p => p.StartTime).Where(t => !string.IsNullOrEmpty(t)).OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault();
            var ends = dayPasses.Select(p => p.EndTime).Where(t => !string.IsNullOrEmpty(t)).OrderByDescending(t => t, StringComparer.Ordinal).FirstOrDefault();
            if (starts == null && ends == null) return null;
            return $"{starts ?? "?"}–{ends ?? "?"}";
        }

        /// <summary>
        /// Club per person, batched. Club is not decoration here: some competitions split any surplus
        /// between the clubs that staffed the event, so it is the basis for that split — which is also why
        /// it belongs to the CELL and not the row (the same function is often held by different clubs on
        /// different days). Best-effort; a missing club just renders nothing.
        /// </summary>
        private Dictionary<int, string> ResolveClubNames(List<StaffAssignmentView> rows)
        {
            var result = new Dictionary<int, string>();
            var memberIds = rows.Where(r => r.MemberId is > 0).Select(r => r.MemberId!.Value).Distinct().ToList();
            if (memberIds.Count == 0) return result;

            var clubNameById = new Dictionary<int, string>();
            // Omgång 3 steg 10: EN läsning för hela rutnätet i stället för ett GetById per person.
            List<HpskSite.Services.MemberPropertyRow> members;
            try { members = _memberProps.Read(new[] { "primaryClubId" }, memberIds); }
            catch (Exception ex) { _logger.LogWarning(ex, "Rutnätet: klubbarna kunde inte läsas."); return result; }
            foreach (var m in members)
            {
                var mid = m.Id;
                try
                {
                    // primaryClubId is stored as a STRING on the member type — PrimaryClubId parses it.
                    var clubId = m.PrimaryClubId;
                    if (clubId <= 0) continue;
                    if (!clubNameById.TryGetValue(clubId, out var name))
                    {
                        name = _clubService.GetClubNameById(clubId) ?? "";
                        clubNameById[clubId] = name;
                    }
                    // "Varbergs Pistolklubb" -> "Varbergs PK". A grid cell is a name plus a club plus a
                    // time on one line; the unabbreviated club is what pushes it onto two.
                    if (!string.IsNullOrEmpty(name)) result[mid] = HpskSite.Helpers.ClubNameHelper.Shorten(name);
                }
                catch { /* one unreadable member must not blank the whole grid */ }
            }
            return result;
        }
    }
}
