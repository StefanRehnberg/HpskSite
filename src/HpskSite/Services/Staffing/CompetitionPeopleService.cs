using System.Globalization;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// THE PEOPLE LAYER for a competition — one row per human, unioned across every store that holds
    /// people. See <see cref="CompetitionPersonRow"/> for why this exists.
    ///
    /// Deliberately built by composing the EXISTING services (<see cref="StaffingService.BuildRoster"/>,
    /// <see cref="StaffHelpService"/>, <see cref="WorkBreakdownService.Build"/>) rather than re-querying
    /// the tables: consistency between surfaces is the entire point, so there must be exactly one code
    /// path that decides what a roster row, a sign-up and a prep assignment mean. Per-competition volume
    /// is tiny (tens of rows), so the extra reads are cheaper than a second interpretation of the data.
    /// </summary>
    public class CompetitionPeopleService
    {
        private readonly StaffingService _staffing;
        private readonly StaffHelpService _help;
        private readonly WorkBreakdownService _work;
        private readonly StaffPassService _pass;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly ILogger<CompetitionPeopleService> _logger;
        private readonly MemberClubService _memberClubs;
        private readonly Umbraco.Cms.Core.Web.IUmbracoContextAccessor _umbraco;
        private readonly HpskSite.Services.MemberPropertyReader _memberProps;

        public CompetitionPeopleService(
            StaffingService staffing,
            StaffHelpService help,
            WorkBreakdownService work,
            StaffPassService pass,
            IMemberService memberService,
            ClubService clubService,
            ILogger<CompetitionPeopleService> logger,
            MemberClubService memberClubs,
            Umbraco.Cms.Core.Web.IUmbracoContextAccessor umbraco,
            HpskSite.Services.MemberPropertyReader memberProps)
        {
            _memberProps = memberProps;
            _memberClubs = memberClubs;
            _umbraco = umbraco;
            _staffing = staffing;
            _help = help;
            _work = work;
            _pass = pass;
            _memberService = memberService;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>Stable cross-store identity. Members key on id; free-text helpers on their name, so the
        /// same typed-in helper doesn't split into two people.</summary>
        public static string KeyFor(int? memberId, string? displayName) =>
            StaffPersonKey.For(memberId, displayName);

        public CompetitionPeopleResponse Build(StaffOwner owner, string? discipline, bool canEdit,
            bool includePrep = true)
        {
            var competitionId = owner.Comp;
            var resp = new CompetitionPeopleResponse { Discipline = discipline ?? "", CanEdit = canEdit };
            var byKey = new Dictionary<string, CompetitionPersonRow>(StringComparer.Ordinal);

            CompetitionPersonRow Row(int? memberId, string displayName, string source)
            {
                var key = KeyFor(memberId, displayName);
                if (!byKey.TryGetValue(key, out var row))
                {
                    row = new CompetitionPersonRow
                    {
                        Key = key,
                        MemberId = memberId is > 0 ? memberId : null,
                        DisplayName = string.IsNullOrWhiteSpace(displayName) ? "(namn saknas)" : displayName.Trim(),
                        IsExternal = memberId is not > 0,
                    };
                    byKey[key] = row;
                }
                // A later store may know the name better than the first one did (e.g. a roster row typed as
                // "Anna" then a sign-up carrying the full member name) — prefer the longer, non-placeholder one.
                if (!string.IsNullOrWhiteSpace(displayName) && displayName.Trim().Length > row.DisplayName.Length
                    && !row.DisplayName.StartsWith("(", StringComparison.Ordinal))
                    row.DisplayName = displayName.Trim();
                if (!row.Sources.Contains(source)) row.Sources.Add(source);
                return row;
            }

            // ---------- 1. the roster (StaffAssignment + Fält station-chief mirrors + pass labels) ----------
            var roster = _staffing.BuildRoster(owner, discipline, canEdit);
            foreach (var g in roster.Groups)
            {
                foreach (var a in g.Assignments)
                {
                    var row = Row(a.MemberId, a.DisplayName,
                        a.ReadOnly ? PersonSource.Stationer : PersonSource.Roster);
                    if (row.Phone == null && !string.IsNullOrWhiteSpace(a.Phone)) row.Phone = a.Phone;
                    if (row.Email == null && !string.IsNullOrWhiteSpace(a.Email)) row.Email = a.Email;

                    var flat = new CompetitionPersonAssignment
                    {
                        Id = a.Id,
                        PersonName = row.DisplayName,
                        RoleKey = a.RoleKey,
                        RoleName = a.RoleName,
                        FunctionTitle = a.FunctionTitle,
                        ScopeType = a.ScopeType,
                        ScopeKey = a.ScopeKey,
                        ScopeLabel = a.ScopeLabel,
                        ShiftLabel = a.ShiftLabel,
                        PassId = a.PassId,
                        PassLabel = a.PassLabel,
                        // WHICH DAY. Without it a person on three days shows three identical
                        // "Sekretariat · Heldag" rows that cannot be told apart — or deleted with any
                        // confidence about which one you are removing.
                        DateKey = a.DateKey,
                        DayLabel = FormatDay(a.DateKey),
                        Status = a.Status,
                        NotifiedAt = a.NotifiedAt,
                        IsResponsible = a.IsResponsible,
                        HasAdminAccess = a.HasAdminAccess,
                        CheckedIn = a.CheckedIn,
                        ReadOnly = a.ReadOnly,
                    };
                    flat.Label = BuildAssignmentLabel(flat);
                    row.Assignments.Add(flat);
                    row.RoleLabels.Add(flat.Label);
                    if (a.HasAdminAccess) row.HasAdminAccess = true;
                    if (a.IsResponsible) row.IsResponsible = true;
                    // P3 steg E: ett uttryckligt "ställer upp för" på någon av personens rader gäller personen.
                    if (row.RepresentsId == null && a.RepresentsClubId > 0) { row.RepresentsType = "club"; row.RepresentsId = a.RepresentsClubId; }
                    else if (row.RepresentsId == null && a.RepresentsRegionId > 0) { row.RepresentsType = "region"; row.RepresentsId = a.RepresentsRegionId; }
                    if (a.AvailabilityLabels.Count > 0 && row.AvailabilityLabels.Count == 0)
                        row.AvailabilityLabels = a.AvailabilityLabels.ToList();
                    if (a.ReadOnly && row.SourceLabel == null) row.SourceLabel = a.SourceLabel;

                    // Tävlingsledning read-through for Förberedelser: the roster is the single source.
                    if (string.Equals(a.RoleKey, "tavlingsledning", StringComparison.OrdinalIgnoreCase))
                        resp.Leadership.Add(flat);
                }
            }

            // ---------- 2. self-sign-ups (/bemanna) — the link that was missing entirely ----------
            var passes = SafePasses(owner);
            var slots = SafeSlots(owner);
            foreach (var v in SafeReview(owner))
            {
                var row = Row(v.MemberId, v.MemberName, PersonSource.Volunteer);
                var vol = new CompetitionPersonVolunteer
                {
                    Comment = v.Comment,
                    Updated = v.Updated,
                    Slots = v.Slots.Select(s =>
                    {
                        var slot = slots.FirstOrDefault(x => x.Id == s.SlotId);
                        return new CompetitionPersonVolunteerSlot
                        {
                            SlotId = s.SlotId,
                            Label = s.Label,
                            TimesText = s.TimesText,
                            SuggestedPassId = slot == null ? null : MatchPass(slot, passes),
                        };
                    }).ToList(),
                };
                vol.SlotsSummary = string.Join(" · ", vol.Slots.Select(s =>
                    ShortSlotLabel(slots.FirstOrDefault(x => x.Id == s.SlotId), s)));
                row.Volunteer = vol;
            }

            // ---------- 3. declared availability (members who said when they can work) ----------
            foreach (var grp in _staffing.GetAvailabilityForOwner(owner).GroupBy(a => a.MemberId))
            {
                var name = ResolveMemberName(grp.Key);
                var row = Row(grp.Key, name, PersonSource.Volunteer);
                if (row.AvailabilityLabels.Count == 0)
                    row.AvailabilityLabels = grp
                        .OrderBy(a => a.AvailableFrom ?? DateTime.MinValue)
                        .Select(a => AvailabilityLabel(a.AvailableFrom, a.AvailableTo))
                        .ToList();
            }

            // ---------- 4. prep ownership (Förberedelser) — områdesansvarig + uppgiftsansvarig ----------
            if (includePrep)
            {
                try
                {
                    var wb = _work.BuildForPlanning(owner.PlanningId, canEdit: false);
                    foreach (var area in wb.Areas)
                    {
                        if (area.ResponsibleMemberId is > 0)
                        {
                            var row = Row(area.ResponsibleMemberId, area.ResponsibleName ?? ResolveMemberName(area.ResponsibleMemberId.Value), PersonSource.Prep);
                            row.IsResponsible = true;
                            row.Prep.Add(new CompetitionPersonPrepRef
                            {
                                AreaId = area.Id,
                                AreaName = area.Name,
                                IsAreaLead = true,
                                Status = area.DoneCount >= area.TotalCount && area.TotalCount > 0 ? WorkItemStatus.Klar : WorkItemStatus.Pagar,
                                IsOverdue = area.OverdueCount > 0,
                            });
                        }
                        foreach (var item in area.Items.Where(i => i.AssignedMemberId is > 0))
                        {
                            var row = Row(item.AssignedMemberId, item.AssignedName ?? ResolveMemberName(item.AssignedMemberId!.Value), PersonSource.Prep);
                            row.Prep.Add(new CompetitionPersonPrepRef
                            {
                                AreaId = area.Id,
                                ItemId = item.Id,
                                AreaName = area.Name,
                                ItemTitle = item.Title,
                                DueDate = item.DueDate,
                                Status = item.Status,
                                IsOverdue = item.IsOverdue,
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Prep is enrichment — a failure here must never blank the people list.
                    _logger.LogWarning(ex, "People: prep enrichment failed for competition {CompetitionId}", competitionId);
                }
            }

            // ---------- 5. finalise: contact details, club, state rollup ----------
            // Omgång 3 steg 10: medlemmarnas kontakt och klubbar i EN läsning, inte ett GetById per person.
            var members = ReadMembers(byKey.Values.Where(r => r.MemberId is > 0).Select(r => r.MemberId!.Value));
            foreach (var row in byKey.Values)
            {
                row.PrepOpenCount = row.Prep.Count(p => !string.Equals(p.Status, WorkItemStatus.Klar, StringComparison.OrdinalIgnoreCase));
                row.PrepOverdueCount = row.Prep.Count(p => p.IsOverdue);
                row.ReadOnly = row.Assignments.Count > 0 && row.Assignments.All(a => a.ReadOnly);

                if (row.MemberId is > 0 && members.TryGetValue(row.MemberId.Value, out var m)) EnrichFromMember(row, m);
                (row.State, row.StatePriority) = RollUpState(row);
            }
            FinishRepresents(byKey.Values.ToList(), resp, members);

            resp.People = byKey.Values
                .OrderBy(p => p.StatePriority)
                .ThenBy(p => p.DisplayName, StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true))
                .ToList();

            resp.TotalPeople = resp.People.Count;
            resp.AssignedCount = resp.People.Count(p => p.Assignments.Count > 0);
            // The queue = "offered to help but holds no uppdrag". Someone who only declared availability
            // windows (without ticking pass) has still offered, so they belong here — and they are already
            // labelled Anmäld by RollUpState, so counting them keeps the badge, the banner and the
            // "Att tilldela" filter in agreement. Keep this predicate and the client filter in step.
            resp.UnassignedVolunteerCount = resp.People.Count(p =>
                (p.Volunteer != null || p.AvailabilityLabels.Count > 0) && p.Assignments.Count == 0);
            resp.UnnotifiedCount = resp.People.Count(p => p.Assignments.Any(a => !a.ReadOnly && !StaffAssignmentStatus.IsTold(a.Status, a.NotifiedAt)));
            resp.ExternalCount = resp.People.Count(p => p.IsExternal);
            // ONE ROW PER PERSON. Leadership is collected per ASSIGNMENT, and since tävlingsledning is
            // staffed per day a three-day SM produced three identical "Hans Reschke · Tävlingsledare"
            // rows. This card answers "who leads this competition" — a question that has no day in it.
            // Star and behörighet are merged across the days so a person who has either on any day
            // keeps it here.
            resp.Leadership = resp.Leadership
                .GroupBy(a => (a.PersonName ?? "").Trim().ToLowerInvariant()
                            + "|" + (a.FunctionTitle ?? a.RoleName ?? "").Trim().ToLowerInvariant())
                .Select(g =>
                {
                    var first = g.First();
                    first.IsResponsible = g.Any(x => x.IsResponsible);
                    first.HasAdminAccess = g.Any(x => x.HasAdminAccess);
                    return first;
                })
                .OrderByDescending(a => a.IsResponsible)
                .ThenBy(a => a.FunctionTitle, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return resp;
        }

        /// <summary>
        /// Everyone who could reasonably be picked to own prep work or a role on THIS competition — the crew
        /// first, so prep owners stop being strangers to the roster. Used by the "föreslagna" block in the
        /// member pickers before falling back to a site-wide member search.
        /// </summary>
        public List<CompetitionPersonRow> Candidates(StaffOwner owner, string? discipline)
            => Build(owner, discipline, canEdit: false).People
                .Where(p => p.MemberId is > 0)
                .ToList();

        /// <summary>
        /// Does this person already hold an equivalent role on the same scope + pass? Prevents the silent
        /// duplicate the old dialog allowed (assign the same person to Skjutlag 2 twice).
        ///
        /// FunctionTitle is part of the identity on purpose: one person legitimately holds *Tävlingsledare*
        /// AND *Säkerhetschef* — both are role=tavlingsledning, scope=All, no pass — so ignoring the title
        /// would block a normal appointment. Only an exact repeat of all five is treated as a duplicate.
        /// </summary>
        /// <summary>
        /// Same person, same role, same scope, same pass — <b>and the same DAY</b>. The day used to be
        /// missing from the key, so putting someone on vapenkontroll on Saturday warned that they already
        /// had it because they were on it on Friday. Working the same post on consecutive days is the
        /// normal case in a staffing plan, not a mistake, and a warning that cries wolf gets clicked past.
        /// </summary>
        public CompetitionPersonAssignment? FindDuplicate(StaffOwner owner, string? discipline,
            int? memberId, string? displayName, string roleKey, string? functionTitle,
            string? scopeType, string? scopeKey, int? passId, int excludeId, string? dateKey = null)
        {
            var key = KeyFor(memberId, displayName);
            var person = Build(owner, discipline, canEdit: false, includePrep: false)
                .People.FirstOrDefault(p => p.Key == key);
            if (person == null) return null;
            static string N(string? s) => (s ?? "").Trim().ToLowerInvariant();
            return person.Assignments.FirstOrDefault(a =>
                a.Id != excludeId
                && !a.ReadOnly
                && N(a.RoleKey) == N(roleKey)
                && N(a.FunctionTitle) == N(functionTitle)
                && N(a.ScopeType) == N(scopeType)
                && N(a.ScopeKey) == N(scopeKey)
                && (a.PassId ?? 0) == (passId ?? 0)
                && N(a.DateKey) == N(dateKey));
        }

        // ---- helpers ----

        /// <summary>"2026-08-22" → "lör 22 aug". Null/unparseable → null (never a guessed day).</summary>
        private static string? FormatDay(string? dateKey)
        {
            if (string.IsNullOrWhiteSpace(dateKey)) return null;
            return DateTime.TryParseExact(dateKey, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)
                ? d.ToString("ddd d MMM", System.Globalization.CultureInfo.GetCultureInfo("sv-SE"))
                : null;
        }

        private static string BuildAssignmentLabel(CompetitionPersonAssignment a)
        {
            var parts = new List<string> { a.RoleName };
            if (!string.IsNullOrWhiteSpace(a.FunctionTitle)) parts.Add(a.FunctionTitle!);
            if (!string.IsNullOrWhiteSpace(a.ScopeLabel) && a.ScopeLabel != "Hela tävlingen") parts.Add(a.ScopeLabel);
            var time = a.PassLabel ?? a.ShiftLabel;
            if (!string.IsNullOrWhiteSpace(time)) parts.Add(time!);
            return string.Join(" · ", parts);
        }

        /// <summary>"Lör 06–13 · Kansliet" — short enough for a chip, unlike the review label.</summary>
        private static string ShortSlotLabel(HelpSlotView? slot, CompetitionPersonVolunteerSlot s)
        {
            if (slot == null) return s.Label;
            var day = "";
            if (DateTime.TryParse(slot.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                day = d.ToString("ddd d/M", CultureInfo.GetCultureInfo("sv-SE"));
            var time = !string.IsNullOrEmpty(s.TimesText) ? s.TimesText
                : ((!string.IsNullOrEmpty(slot.StartTime) || !string.IsNullOrEmpty(slot.EndTime)) ? $"{slot.StartTime}–{slot.EndTime}" : "");
            return string.Join(" ", new[] { day, time, slot.Headline }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }

        /// <summary>Line a help-slot up with a StaffPass (same date, overlapping time) so "Tilldela" can
        /// prefill the pass. Null when there is no unambiguous match — never guess a time onto a person.</summary>
        private static int? MatchPass(HelpSlotView slot, List<StaffPassView> passes)
        {
            var same = passes.Where(p => string.Equals(p.Date, slot.Date, StringComparison.Ordinal)).ToList();
            if (same.Count == 0) return null;
            if (same.Count == 1) return same[0].Id;

            var s = ParseTime(slot.StartTime); var e = ParseTime(slot.EndTime);
            if (s == null && e == null) return null;
            var hits = same.Where(p =>
            {
                var ps = ParseTime(p.StartTime); var pe = ParseTime(p.EndTime);
                if (ps == null || pe == null) return false;
                var ss = s ?? ps.Value; var ee = e ?? pe.Value;
                return ss < pe.Value && ps.Value < ee;
            }).ToList();
            return hits.Count == 1 ? hits[0].Id : null;
        }

        private static TimeSpan? ParseTime(string? s) =>
            TimeSpan.TryParse((s ?? "").Trim(), CultureInfo.InvariantCulture, out var t) ? t : null;

        /// <summary>
        /// P3 steg E (beslut 9.6): vilken förening var och en ställer upp för, valen och summeringen per förening.
        /// Förvalet är medlemmens huvudklubb; en person utan konto har inget förval men kan väljas till de föreningar
        /// som andra i bemanningen ställer upp för. Ett fel här får aldrig fälla personlistan.
        /// </summary>
        private void FinishRepresents(List<CompetitionPersonRow> rows, CompetitionPeopleResponse resp,
            Dictionary<int, HpskSite.Services.MemberPropertyRow> members)
        {
            try
            {
                var names = new Dictionary<string, string>();
                string Name(string type, int id)
                {
                    var k = type + ":" + id;
                    if (names.TryGetValue(k, out var n)) return n;
                    n = type == "club" ? HpskSite.Helpers.ClubNameHelper.Shorten(_clubService.GetClubNameById(id) ?? "") : RegionName(id);
                    names[k] = n;
                    return n;
                }
                foreach (var row in rows)
                {
                    if (row.MemberId is > 0 && members.TryGetValue(row.MemberId.Value, out var m))
                    {
                        var clubs = m.AllClubIds;
                        foreach (var c in clubs) row.RepresentChoices.Add(new RepresentChoice { Type = "club", Id = c, Name = Name("club", c) });
                        foreach (var reg in clubs.Select(RegionIdOfClub).Where(r => r > 0).Distinct())
                            row.RepresentChoices.Add(new RepresentChoice { Type = "region", Id = reg, Name = Name("region", reg) });
                        if (row.RepresentsId == null && m.PrimaryClubId is var pc && pc > 0)
                        { row.RepresentsType = "club"; row.RepresentsId = pc; row.RepresentsIsDefault = true; }
                    }
                    if (row.RepresentsId > 0) row.RepresentsName = Name(row.RepresentsType!, row.RepresentsId.Value);
                    row.RepresentChoices = row.RepresentChoices.Where(c => c.Name.Length > 0).GroupBy(c => c.Type + c.Id).Select(g => g.First()).ToList();
                }
                // Den utan konto: de föreningar som någon annan i bemanningen ställer upp för eller kan välja.
                var pool = rows.SelectMany(r => r.RepresentChoices).GroupBy(c => c.Type + c.Id).Select(g => g.First())
                    .OrderBy(c => c.Type).ThenBy(c => c.Name, StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true)).ToList();
                foreach (var row in rows.Where(r => !(r.MemberId > 0))) row.RepresentChoices = pool.ToList();

                resp.ByOrganisation = rows.Where(r => r.Assignments.Count > 0)
                    .GroupBy(r => string.IsNullOrEmpty(r.RepresentsName) ? "" : r.RepresentsName!)
                    .Select(g => new RepresentSummary { Name = g.Key.Length == 0 ? "Ingen förening" : g.Key, People = g.Count(), Assignments = g.Sum(r => r.Assignments.Count) })
                    .OrderBy(s => s.Name == "Ingen förening" ? 1 : 0).ThenByDescending(s => s.People).ThenBy(s => s.Name)
                    .ToList();
            }
            catch (Exception ex) { _logger.LogWarning(ex, "People: ställer upp för kunde inte läsas."); }
        }

        /// <summary>Klubbens krets ur trädet (krets &gt; klubbar &gt; klubb) — samma källa som adressen.</summary>
        private int RegionIdOfClub(int clubId)
        {
            if (!_umbraco.TryGetUmbracoContext(out var ctx)) return 0;
            var r = ctx.Content?.GetById(clubId)?.Parent?.Parent;
            return r != null && r.ContentType.Alias == "regionalPage" ? r.Id : 0;
        }

        private string RegionName(int regionId)
        {
            if (!_umbraco.TryGetUmbracoContext(out var ctx)) return "";
            var r = ctx.Content?.GetById(regionId);
            if (r == null || r.ContentType.Alias != "regionalPage") return "";
            return r.Value<string>("regionName") is { Length: > 0 } n ? n : r.Name ?? "";
        }

        /// <summary>Kontakt och klubbar för personlistans medlemmar, i en läsning. Ett fel ger en tom karta.</summary>
        private Dictionary<int, HpskSite.Services.MemberPropertyRow> ReadMembers(IEnumerable<int> ids)
        {
            try
            {
                return _memberProps.Read(new[] { "phoneNumber", "primaryClubId", "memberClubIds" }, ids)
                    .ToDictionary(m => m.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "People: medlemmarnas kontaktuppgifter kunde inte läsas.");
                return new Dictionary<int, HpskSite.Services.MemberPropertyRow>();
            }
        }

        private void EnrichFromMember(CompetitionPersonRow row, HpskSite.Services.MemberPropertyRow m)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(row.Email)) row.Email = m.Email;
                if (string.IsNullOrWhiteSpace(row.Phone) && m.Get("phoneNumber") is { Length: > 0 } phone)
                    row.Phone = phone;
                if (m.PrimaryClubId is var clubId && clubId > 0)
                    // Same abbreviation as the grid — a person's club must read identically in the cell,
                    // the person panel and the printout.
                    row.ClubName = HpskSite.Helpers.ClubNameHelper.Shorten(_clubService.GetClubNameById(clubId) ?? "");
            }
            catch { /* contact enrichment is cosmetic */ }
        }

        /// <summary>
        /// The one word that says where this person is in the process. A volunteer with no role is the
        /// organiser's to-do (priority 0); a placement nobody has been told about is the next.
        /// </summary>
        private static (string State, int Priority) RollUpState(CompetitionPersonRow row)
        {
            if (row.Assignments.Count == 0)
            {
                var state = row.Volunteer != null ? PersonState.Anmald
                    : row.Prep.Count > 0 ? PersonState.PrepOnly
                    : row.AvailabilityLabels.Count > 0 ? PersonState.Anmald
                    : PersonState.Unplaced;
                return (state, PersonState.Priority(state));
            }
            // Worst-first across their rows: what the organiser must act on (not told yet) wins over what is settled.
            var label = PersonState.OfRows(row.Assignments.Select(a => (a.Status, a.NotifiedAt)));
            return (label, PersonState.Priority(label));
        }

        private string ResolveMemberName(int memberId)
        {
            try
            {
                var m = _memberService.GetById(memberId);
                if (m == null) return $"Medlem {memberId}";
                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                return string.IsNullOrEmpty(name) ? (m.Name ?? $"Medlem {memberId}") : name;
            }
            catch { return $"Medlem {memberId}"; }
        }

        private static string AvailabilityLabel(DateTime? from, DateTime? to)
        {
            var ci = CultureInfo.GetCultureInfo("sv-SE");
            if (from == null && to == null) return "Heldag";
            string Day(DateTime d) => d.ToString("ddd d MMM", ci);
            string T(DateTime d) => d.ToString("HH:mm", ci);
            if (from != null && to != null)
                return from.Value.Date == to.Value.Date
                    ? $"{Day(from.Value)} {T(from.Value)}–{T(to.Value)}"
                    : $"{Day(from.Value)} {T(from.Value)} – {Day(to.Value)} {T(to.Value)}";
            return from != null ? $"från {Day(from.Value)} {T(from.Value)}" : $"till {Day(to!.Value)} {T(to.Value)}";
        }

        private List<HelpSignupReviewView> SafeReview(StaffOwner owner)
        {
            try { return _help.GetReview(owner); }
            catch (Exception ex) { _logger.LogWarning(ex, "People: sign-up read failed for {Owner}", owner.MemoKey); return new(); }
        }

        private List<HelpSlotView> SafeSlots(StaffOwner owner)
        {
            try { return _help.GetSlots(owner); }
            catch { return new(); }
        }

        private List<StaffPassView> SafePasses(StaffOwner owner)
        {
            try { return _pass.GetPasses(owner); }
            catch { return new(); }
        }
    }
}
