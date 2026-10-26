using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// "Planera dagen" (Stefan 2026-10-26, Expertgruppen): bemanning och starttider i ett verktyg. Den här filen har ändpunkterna
    /// som hör till det — skjuttiden per gren, kandidatlistan, förslagen. Rätten är densamma som för resten av bemanningen
    /// (<c>OwnerAsync</c>, planeringens <i>Kan ändra</i>).
    /// </summary>
    public partial class StaffingController
    {
        /// <summary>Skjuttiden som gäller (grenens förval med planeringens egna värden över), och förvalet — så att dialogen kan visa båda.</summary>
        [HttpGet]
        public async Task<IActionResult> GetShootTime(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            if (!owner.HasPlanning) return Json(new { success = false, message = "Skjuttiden hör till planeringen." });
            var p = _owners.Planning(owner.PlanningId);
            var discipline = _owners.Discipline(owner);
            var def = ShootTimeRules.Default(discipline);
            var cur = _owners.ShootTime(owner.PlanningId);
            return Json(new
            {
                success = true,
                before = cur.BeforeMinutes, duration = cur.DurationMinutes, after = cur.AfterMinutes,
                isOwn = p?.ShootBeforeMin != null || p?.ShootDurationMin != null || p?.ShootAfterMin != null,
                defaults = new { before = def.BeforeMinutes, duration = def.DurationMinutes, after = def.AfterMinutes },
            });
        }

        /// <summary>
        /// Sparar skjuttiden. <c>Reset</c> = tillbaka till grenens förval. Annars tre tal i minuter (tomt = förvalet för det talet).
        /// Krockarna i veckan, i bemanningen och i förslagen räknas om av sig själva — skjuttiden läses vid varje anrop.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetShootTime([FromBody] SetShootTimeRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            if (!owner.HasPlanning) return Json(new { success = false, message = "Skjuttiden hör till planeringen." });
            int? before = request.Reset ? null : request.Before, duration = request.Reset ? null : request.Duration, after = request.Reset ? null : request.After;
            var err = ShootTimeRules.Validate(before, duration, after);
            if (err != null) return Json(new { success = false, message = err });
            _planning.SetShootTime(owner.PlanningId, before, duration, after, viewer.Id);
            return Json(new { success = true });
        }
    }

    public partial class StaffingController
    {
        private static object CandidateJson(StaffCandidate c) => new
        {
            memberId = c.MemberId, name = c.Name, club = c.Club, shooterClass = c.ShooterClass, isShooter = c.IsShooter,
            starts = c.Starts.Select(s => new { title = s.Title, where = s.Where, time = s.Start.ToString("HH\\:mm"), end = s.End?.ToString("HH\\:mm") }),
            volunteer = c.Volunteer, roleMatch = c.RoleMatch, comment = c.Comment, free = c.Free, tier = (int)c.Tier,
            assignedToday = c.AssignedToday, alreadyHere = c.AlreadyHere,
            conflict = c.Conflict == null ? null : new { kind = c.Conflict.Kind.ToString(), label = c.Conflict.Label, hard = c.Conflict.IsHard, gap = c.Conflict.GapMinutes },
        };

        private static CandidateQuery CandidateQueryOf(StaffOwner owner, string? roleKey, string? date, string? from, string? to, string? scopeKey,
            string? q, bool onlyVolunteers, int take, IReadOnlyCollection<int>? only = null)
        {
            DateTime? d = DateTime.TryParseExact((date ?? "").Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dd) ? dd : null;
            return new CandidateQuery
            {
                Owner = owner, RoleKey = string.IsNullOrWhiteSpace(roleKey) ? null : roleKey, Date = d, ScopeKey = scopeKey,
                From = StaffingRules.ParseTime(from), To = StaffingRules.ParseTime(to), Search = q, OnlyVolunteers = onlyVolunteers,
                Take = take is > 0 and <= 200 ? take : 40, OnlyMembers = only,
            };
        }

        /// <summary>
        /// Planera dagen steg 1: kandidatlistan för en plats (roll, dag, tid, station). Poolen är de som står i tävlingens anmälningar eller har
        /// anmält sig som funktionärer; varje kandidat bär sina starter, sin anmälan och om hen är ledig. Bara den som får ändra bemanningen.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCandidates(int competitionId, string? roleKey, string? date, string? from, string? to, string? scopeKey,
            string? q, bool onlyVolunteers = false, int take = 40)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var r = HttpContext.RequestServices.GetRequiredService<StaffCandidateService>().Find(CandidateQueryOf(owner, roleKey, date, from, to, scopeKey, q, onlyVolunteers, take));
                return Json(new { success = true, candidates = r.Candidates.Select(CandidateJson), total = r.Total, shooters = r.Shooters, volunteers = r.Volunteers, volunteersWithoutAssignment = r.VolunteersWithoutAssignment });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetCandidates failed for planning {Planning}", owner.PlanningIdOrNull);
                return Json(new { success = false, message = "Kunde inte läsa kandidaterna." });
            }
        }

        /// <summary>
        /// "Lägg in flera": samma roll, station, dag och tid för flera personer på en gång. Varje person prövas för sig på servern: den som är
        /// upptagen (hårt hinder) hoppas över om inte <c>Force</c> är satt, och den som redan står där läggs inte in en gång till. Svaret säger exakt
        /// vilka som lades in och vilka som hoppades över, med skäl. Planerat läge (<c>Planned</c>) — ingen får något mejl av det här.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignMany([FromBody] AssignManyRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey) || request.MemberIds == null || request.MemberIds.Count == 0)
                return Json(new { success = false, message = "Välj roll och minst en person." });
            if (request.MemberIds.Count > 200) return Json(new { success = false, message = "Högst 200 personer åt gången." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            var discipline = _owners.Discipline(owner);
            if (_roleCatalog.Resolve(owner, discipline, request.RoleKey) == null) return Json(new { success = false, message = "Rollen finns inte. Skapa den först i rutnätet." });
            var from = StaffingRules.ParseTime(request.From); var to = StaffingRules.ParseTime(request.To);
            if ((!string.IsNullOrWhiteSpace(request.From) && from == null) || (!string.IsNullOrWhiteSpace(request.To) && to == null)) return Json(new { success = false, message = "Skriv klockslagen som 8:00." });
            if ((from != null || to != null) && string.IsNullOrWhiteSpace(request.Date)) return Json(new { success = false, message = "Tider kräver en dag." });
            if (from != null && to != null && to <= from) return Json(new { success = false, message = "Sluttiden ligger före starttiden." });

            var ids = request.MemberIds.Where(i => i > 0).Distinct().ToList();
            var eval = HttpContext.RequestServices.GetRequiredService<StaffCandidateService>()
                .Find(CandidateQueryOf(owner, request.RoleKey, request.Date, request.From, request.To, request.ScopeKey, null, false, 200, ids));
            var byId = eval.Candidates.ToDictionary(c => c.MemberId);

            var added = new List<object>(); var skipped = new List<object>();
            foreach (var mid in ids)
            {
                if (!byId.TryGetValue(mid, out var c)) { skipped.Add(new { memberId = mid, name = MemberDisplayName(mid) ?? $"Medlem {mid}", reason = "Personen står inte i anmälningarna eller bland funktionärerna." }); continue; }
                if (c.AlreadyHere) { skipped.Add(new { memberId = mid, name = c.Name, reason = "Står redan där." }); continue; }
                if (c.Conflict is { IsHard: true } && !request.Force) { skipped.Add(new { memberId = mid, name = c.Name, reason = c.Conflict.Label, clash = true }); continue; }
                try
                {
                    var day = request.Date;
                    var req = new SaveStaffAssignmentRequest
                    {
                        Id = 0, CompetitionId = request.CompetitionId, MemberId = mid, DisplayName = c.Name, RoleKey = request.RoleKey,
                        ScopeType = string.IsNullOrWhiteSpace(request.ScopeType) ? null : request.ScopeType, ScopeKey = string.IsNullOrWhiteSpace(request.ScopeKey) ? null : request.ScopeKey,
                        StartsAt = from != null && day != null ? $"{day} {from:hh\\:mm}" : null, EndsAt = to != null && day != null ? $"{day} {to:hh\\:mm}" : null,
                        DayDate = day, Status = StaffAssignmentStatus.Planned,
                    };
                    var id = _staffing.Save(req, owner, viewer.Id);
                    if (id == 0) { skipped.Add(new { memberId = mid, name = c.Name, reason = NotInCompetitionMessage }); continue; }
                    added.Add(new { memberId = mid, name = c.Name, id, warning = c.Conflict is { IsHard: false } ? c.Conflict.Label : null, forced = c.Conflict is { IsHard: true } });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Staffing: AssignMany failed for member {Member}", mid);
                    skipped.Add(new { memberId = mid, name = c.Name, reason = "Kunde inte spara." });
                }
            }
            return Json(new { success = true, added, skipped });
        }
    }

    public partial class StaffingController
    {
        /// <summary>
        /// Bemanningen SKRIVSKYDDAD för den som är med i planeringen men inte får ändra (Stefans svar efter persontestet: "Deltar ska kunna se
        /// bemanningen"). Bara det en kollega behöver veta: roll, station, dag, tid, namn och förening — aldrig telefon, e-post, anteckningar,
        /// status (en som tackat nej syns inte), behörighet eller kollegers egna starter. Den som får ändra läser det vanliga rutnätet.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetStaffingOverview(int competitionId)
        {
            var pid = PlanningIdFromRequest();
            var viewer = await ResolveViewerAsync();
            if (pid <= 0 || viewer == null) return Json(new { success = false, message = "Ingen behörighet" });
            var me = _planning.ParticipantForMember(pid, viewer.Id);
            if (me is not { IsActive: true }) return Json(new { success = false, message = "Ingen behörighet" });
            var owner = _owners.ForPlanning(pid);
            if (owner == null) return Json(new { success = false, message = "Planeringen finns inte." });
            try
            {
                var g = _grid.BuildGrid(owner, _owners.Discipline(owner), viewer.Id);
                var cols = g.Columns.Where(c => !string.IsNullOrEmpty(c.Key)).ToList();
                var rows = g.Rows.Where(r => r.Cells.Values.Any(l => l.Any(e => !string.Equals(e.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase))) || r.HasNeed)
                    .Select(r => new
                    {
                        roleName = r.RoleName, scopeLabel = r.ScopeLabel,
                        cells = r.Cells.ToDictionary(kv => kv.Key, kv => kv.Value
                            .Where(e => !string.Equals(e.Status, StaffAssignmentStatus.Declined, StringComparison.OrdinalIgnoreCase))
                            .Select(e => new { name = e.DisplayName, club = e.ClubName, time = e.TimeLabel, responsible = e.IsResponsible }).ToList()),
                        needed = cols.ToDictionary(c => c.Key, c => r.NeedFor(c.Key)),
                        filled = cols.ToDictionary(c => c.Key, c => r.FilledByDay.TryGetValue(c.Key, out var f) ? f : 0),
                    }).ToList();
                var missing = g.Rows.Sum(r => cols.Sum(c => Math.Max(0, r.NeedFor(c.Key) - (r.FilledByDay.TryGetValue(c.Key, out var f) ? f : 0))));
                return Json(new
                {
                    success = true,
                    columns = cols.Select(c => new { key = c.Key, label = c.Label, dayLabel = c.DayLabel, timeLabel = c.TimeLabel }),
                    rows, missing,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetStaffingOverview failed for planning {Planning}", pid);
                return Json(new { success = false, message = "Kunde inte läsa bemanningen." });
            }
        }
    }

    public class AssignManyRequest
    {
        public int CompetitionId { get; set; }
        public string RoleKey { get; set; } = "";
        public string? ScopeType { get; set; }
        public string? ScopeKey { get; set; }
        public string? Date { get; set; }
        public string? From { get; set; }
        public string? To { get; set; }
        public List<int>? MemberIds { get; set; }
        /// <summary>Lägg in även den som är upptagen (ett hårt hinder). Förvalet är att hoppa över.</summary>
        public bool Force { get; set; }
    }

    /// <summary>Minuter före, skjuttid och minuter efter. Null = förvalet. <c>Reset</c> nollar alla tre.</summary>
    public class SetShootTimeRequest
    {
        public int CompetitionId { get; set; }
        public int? Before { get; set; }
        public int? Duration { get; set; }
        public int? After { get; set; }
        public bool Reset { get; set; }
    }
}
