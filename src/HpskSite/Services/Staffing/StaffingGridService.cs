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

        public StaffingGridService(
            StaffingService staffing,
            RoleCatalogService roles,
            StaffDayService days,
            StaffPassService pass,
            CompetitionPeopleService people,
            IMemberService memberService,
            ClubService clubService,
            IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<StaffingGridService> logger)
        {
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
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return null;
                var d = ctx.Content.GetById(competitionId)?.Value<DateTime>("competitionDate");
                return d == default ? null : d;
            }
            catch { return null; }
        }

        private static (TimeSpan start, TimeSpan end)? ParseShiftRange(string? label)
        {
            if (string.IsNullOrWhiteSpace(label)) return null;
            var parts = label.Split('\u2013', '-');
            if (parts.Length != 2) return null;
            if (!TimeSpan.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var s)) return null;
            if (!TimeSpan.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var e)) return null;
            return e <= s ? null : (s, e);
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
        public GridResponse BuildGrid(int competitionId, string? discipline, int viewerId)
        {
            var roster = _staffing.BuildRoster(competitionId, discipline, canEdit: true);
            var resp = new GridResponse { Discipline = discipline ?? "", CanEdit = true };

            var all = roster.Groups.SelectMany(g => g.Assignments).ToList();

            // ---- columns: the DAYS OF THE PLAN --------------------------------------------------
            // Owned by the arrangör (StaffDay), seeded once from the competition span. The days you STAFF
            // are not the days you COMPETE — build-up, materiel runs and teardown carry crew and sit
            // outside the span — so the span may only seed, never constrain.
            // The competition date is deliberately NOT unioned in here: doing that produced a phantom
            // column the organiser never asked for and could not remove. Dates that actually CARRY crew
            // are unioned in, because work is proof that the day is part of the plan.
            var days = _days.EnsureSeeded(competitionId, viewerId);
            var withWork = new HashSet<string>(
                all.Where(a => !string.IsNullOrEmpty(a.DateKey)).Select(a => a.DateKey!), StringComparer.Ordinal);

            var passes = SafeGetPasses(competitionId);
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
            var catalog = _roles.ForCompetition(competitionId, discipline);
            var clubs = ResolveClubNames(all);

            // Per-role need drives the number inside each cell. Coverage stopped being a screen of its
            // own precisely because nobody ever found that screen — on every real competition it was zero.
            var needByRole = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var n in _pass.GetCrewNeeds(competitionId, discipline))
                    if (n.Count > 0) needByRole[n.RoleKey] = n.Count;
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: crew-need read failed for {Comp}", competitionId); }

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
                    .OrderBy(x => x.ScopeKey, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (scopeKeys.Count == 0) scopeKeys.Add((null, null, ""));

                foreach (var (scopeType, scopeKey, scopeLabel) in scopeKeys)
                {
                    var row = new GridRow
                    {
                        RoleKey = role.Key,
                        RoleName = role.DisplayName,
                        ScopeType = scopeType,
                        ScopeKey = scopeKey,
                        ScopeLabel = string.Equals(scopeLabel, "Hela tävlingen", StringComparison.OrdinalIgnoreCase) ? null : scopeLabel,
                        IsCustom = _roles.IsCustom(competitionId, role.Key),
                        SupportsTargetRange = role.SupportsTargetRange,
                        SupportsFunctionTitle = role.SupportsFunctionTitle,
                        DefaultScopeType = role.DefaultScopeType,
                        Needed = needByRole.TryGetValue(role.Key, out var nd) ? nd : 0,
                        SortOrder = role.SortOrder,
                    };

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
                        });
                        row.Filled++;
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
                var people = _people.Build(competitionId, discipline, canEdit: true, includePrep: false);
                resp.Volunteers = people.People
                    .Where(p => p.Volunteer != null && p.Assignments.Count == 0)
                    .Select(p => new GridVolunteer { Key = p.Key, Name = p.DisplayName, Summary = p.Volunteer?.SlotsSummary })
                    .ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: volunteer strip failed for {Comp}", competitionId); }

            return resp;
        }

        private List<StaffPassView> SafeGetPasses(int competitionId)
        {
            try { return _pass.GetPasses(competitionId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: pass lookup failed for competition {CompetitionId}", competitionId);
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
            foreach (var mid in memberIds)
            {
                try
                {
                    var m = _memberService.GetById(mid);
                    // primaryClubId is stored as a STRING on the member type — GetValue<int> returns 0.
                    // Same read as SearchMembers, which is the proven one.
                    var raw = m?.GetValue<string>("primaryClubId");
                    if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out var clubId) || clubId <= 0) continue;
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
