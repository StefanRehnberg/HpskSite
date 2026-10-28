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
        /// <summary>
        /// Fyller "Dagens plan" och platsräkningen ur rutnätssvaret. Platser = behov minus bemannat per roll och dag (per pass när dagen har flera pass) — samma regel som sidans
        /// rubrik förut räknade i JS; nu EN räkning här som både bandet och rubriken läser.
        /// </summary>
        private static void DerivePlanSteps(GridResponse resp, StaffOwner owner, ISet<int>? membersWithStart = null, ISet<int>? membersRegistered = null)
        {
            // Vilka som har placeringar, hur många placeringar som ännu inte meddelats (NotifiedAt saknas; självanmälda och bekräftade räknas inte), och hur många av personerna som också skjuter.
            // Tävlingsledning bemannas av den som skapade planeringen och räknas inte: annars stod "klart" på startlistan och "Alla inlagda har fått besked" innan någon lagts in (persontest 12).
            var live = resp.Rows.Where(r => !string.Equals(r.RoleKey, "tavlingsledning", StringComparison.OrdinalIgnoreCase)).SelectMany(r => r.Cells.Values.SelectMany(c => c))
                .ToList();
            var assignedKeys = live.Select(e => e.MemberId is > 0 ? "m" + e.MemberId : "n" + (e.DisplayName ?? "").Trim().ToLowerInvariant()).Distinct().Count();
            var unlinkedNames = StaffingPlaces.Unlinked(resp);
            var withStart = membersWithStart == null ? 0 : live.Where(e => e.MemberId is > 0 && membersWithStart.Contains(e.MemberId.Value)).Select(e => e.MemberId!.Value).Distinct().Count();
            var registered = membersRegistered == null ? 0 : live.Where(e => e.MemberId is > 0 && membersRegistered.Contains(e.MemberId.Value)).Select(e => e.MemberId!.Value).Distinct().Count();
            var unsent = live.Count(e => PlanStepRules.IsUnnotified(e.Status, e.NotifiedAt) && !e.ReadOnly && e.HasContact);
            var unreachable = live.Count(e => PlanStepRules.IsUnnotified(e.Status, e.NotifiedAt) && !e.ReadOnly && !e.HasContact);
            var (needed, missing, perRole) = StaffingPlaces.Count(resp);
            resp.PlacesNeeded = needed; resp.PlacesMissing = missing;
            // Krockar: inom planeringen (samma person på två poster), och över familjen (uppdrag mot annan del, uppdrag mot egen start). En avsikt att tävla är en varning, ingen krock.
            var startClashes = resp.FamilyClashes.Count(c => c.Kind == FamilyClashRules.KindStart);
            var allClashes = resp.Clashes.Count + resp.FamilyClashes.Count(c => c.Kind != FamilyClashRules.KindIntent);
            var facts = new PlanFacts(
                HasRoles: resp.Rows.Any(r => !string.Equals(r.RoleKey, "tavlingsledning", StringComparison.OrdinalIgnoreCase)),
                HasNeeds: resp.Rows.Any(r => r.HasNeed),
                PlacesNeeded: needed, PlacesMissing: missing, Linked: owner.HasCompetition, VolunteersTotal: resp.VolunteersTotal,
                ShooterModeTitle: ShooterModeRules.IsValid(resp.ShooterMode) ? ShooterModeRules.Describe(resp.ShooterMode!).Title : null,
                PatrolPlanAvailable: resp.PatrolPlanAvailable, StartClashes: startClashes, Clashes: allClashes,
                ByRole: perRole.Where(kv => kv.Value.Needed > 0).Select(kv => new RolePlaces(kv.Key, kv.Value.Needed, kv.Value.Missing)).ToList(),
                AssignedPeople: assignedKeys, PeopleWithStart: withStart, AssignedTotal: live.Count, PlannedUnsent: unsent, PlannedUnreachable: unreachable, Unlinked: unlinkedNames, PeopleRegistered: registered);
            resp.PlanSteps = PlanStepRules.Derive(facts);
        }

        /// <summary>Skjuttiden som gäller (grenens förval med planeringens egna värden över), och förvalet — så att dialogen kan visa båda.</summary>
        [HttpGet]
        public async Task<IActionResult> GetShootTime(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
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
            assignedToday = c.AssignedToday, alreadyHere = c.AlreadyHere, mode = c.Mode, placements = c.Placements ?? new List<string>(),
            conflict = c.Conflict == null ? null : new { kind = c.Conflict.Kind.ToString(), label = c.Conflict.Label, hard = c.Conflict.IsHard, gap = c.Conflict.GapMinutes },
        };

        private static CandidateQuery CandidateQueryOf(StaffOwner owner, string? roleKey, string? date, string? from, string? to, string? scopeKey,
            string? q, bool onlyVolunteers, int take, IReadOnlyCollection<int>? only = null, int skip = 0, bool onlyUnplaced = false, bool onlyFree = false)
        {
            DateTime? d = DateTime.TryParseExact((date ?? "").Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dd) ? dd : null;
            return new CandidateQuery
            {
                Owner = owner, RoleKey = string.IsNullOrWhiteSpace(roleKey) ? null : roleKey, Date = d, ScopeKey = scopeKey,
                From = StaffingRules.ParseTime(from), To = StaffingRules.ParseTime(to), Search = q, OnlyVolunteers = onlyVolunteers,
                Take = take is > 0 and <= 200 ? take : 40, OnlyMembers = only,
                Skip = Math.Max(0, skip), OnlyUnplaced = onlyUnplaced, OnlyFree = onlyFree,
            };
        }

        /// <summary>
        /// Planera dagen steg 1: kandidatlistan för en plats (roll, dag, tid, station). Poolen är de som står i tävlingens anmälningar eller har
        /// anmält sig som funktionärer; varje kandidat bär sina starter, sin anmälan och om hen är ledig. Bara den som får ändra bemanningen.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCandidates(int competitionId, string? roleKey, string? date, string? from, string? to, string? scopeKey,
            string? q, bool onlyVolunteers = false, int take = 40, int skip = 0, bool onlyUnplaced = false, bool onlyFree = false)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var r = HttpContext.RequestServices.GetRequiredService<StaffCandidateService>().Find(CandidateQueryOf(owner, roleKey, date, from, to, scopeKey, q, onlyVolunteers, take, null, skip, onlyUnplaced, onlyFree));
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
        /// "Klistra in en namnlista" (Lägg in flera): en rad per person — namn, gärna med klass, patrull eller starttid efter ett | — matchas mot poolen
        /// (anmälda och funktionärer). Ren läsning: ingen läggs in; svaret säger per rad om personen hittades, om flera heter lika (med val) eller inte hittades.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MatchNameList([FromBody] MatchNameListRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.Text))
                return Json(new { success = false, message = "Klistra in minst ett namn." });
            if (request.Text.Length > 20000) return Json(new { success = false, message = "Listan är för lång. Högst 200 rader." });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var pool = HttpContext.RequestServices.GetRequiredService<StaffCandidateService>()
                    .Find(new CandidateQuery { Owner = owner, Take = 5000 }).Candidates
                    .Select(c => new MatchPerson(c.MemberId, c.Name, c.Club, c.ShooterClass, c.Starts.Select(s => s.Start.ToString("HH\\:mm")).Distinct().ToList(),
                        c.Starts.OrderBy(s => s.Start).Select(s => (s.Where ?? "").Split(" · ")[0].Trim()).FirstOrDefault(w => w.Length > 0)))
                    .ToList();
                var lines = request.Text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                var matches = NameListMatcher.Match(lines, pool);
                object P(MatchPerson p) => new
                {
                    memberId = p.MemberId, name = p.Name, club = p.Club, shooterClass = p.Class,
                    starts = string.Join(", ", p.StartTimes), where = p.Where,
                };
                return Json(new
                {
                    success = true,
                    truncated = lines.Count(l => !string.IsNullOrWhiteSpace(l)) > NameListMatcher.MaxLines,
                    items = matches.Select(m => new
                    {
                        line = m.Line, query = m.Query, status = m.Status.ToString(),
                        chosen = m.Chosen == null ? null : P(m.Chosen), options = m.Options.Select(P),
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: MatchNameList failed for planning {Planning}", owner.PlanningIdOrNull);
                return Json(new { success = false, message = "Kunde inte läsa namnlistan." });
            }
        }
    }

    public class MatchNameListRequest
    {
        public int CompetitionId { get; set; }
        public string? Text { get; set; }
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
                var rows = g.Rows.Where(r => r.Cells.Values.Any(l => l.Count > 0) || r.HasNeed)
                    .Select(r => new
                    {
                        roleName = r.RoleName, scopeLabel = r.ScopeLabel,
                        cells = r.Cells.ToDictionary(kv => kv.Key, kv => kv.Value
                            
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

    public partial class StaffingController
    {
        /// <summary>Planeringens förval för funktionärer som skjuter, de fyra lägena med text, och personernas undantag (namn + läge).</summary>
        [HttpGet]
        public async Task<IActionResult> GetShooterMode(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            var p = _owners.Planning(owner.PlanningId);
            var overrides = _planning.ShooterModeOverrides(owner.PlanningId);
            return Json(new
            {
                success = true,
                mode = ShooterModeRules.IsValid(p?.ShooterMode) ? p!.ShooterMode : null,
                chosen = ShooterModeRules.IsValid(p?.ShooterMode),
                modes = ShooterMode.All.Select(m => { var (t, h) = ShooterModeRules.Describe(m); return new { key = m, title = t, help = h }; }),
                overrides = overrides.Select(kv => new { memberId = kv.Key, name = MemberDisplayName(kv.Key) ?? $"Medlem {kv.Key}", mode = kv.Value }),
            });
        }

        /// <summary>Sätter planeringens förval. Tomt värde tar bort valet (frågan ställs om).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetShooterMode([FromBody] SetShooterModeRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            var mode = string.IsNullOrWhiteSpace(request.Mode) ? null : request.Mode;
            if (mode != null && !ShooterModeRules.IsValid(mode)) return Json(new { success = false, message = "Okänt läge." });
            if (request.MemberId is > 0)
            {
                // Ett undantag för en person: bara någon som finns i anmälningarna eller bland funktionärerna.
                var known = HttpContext.RequestServices.GetRequiredService<StaffCandidateService>()
                    .Find(CandidateQueryOf(owner, null, null, null, null, null, null, false, 200, new[] { request.MemberId.Value })).Candidates.Any();
                if (!known) return Json(new { success = false, message = "Personen står inte i anmälningarna eller bland funktionärerna." });
                _planning.SetPersonShooterMode(owner.PlanningId, request.MemberId.Value, mode, viewer.Id);
            }
            else _planning.SetShooterMode(owner.PlanningId, mode, viewer.Id);
            return Json(new { success = true });
        }
    }

    /// <summary><c>MemberId</c> satt = ett undantag för den personen (tomt läge tar bort det); annars planeringens förval.</summary>
    public class SetShooterModeRequest
    {
        public int CompetitionId { get; set; }
        public string? Mode { get; set; }
        public int? MemberId { get; set; }
    }

    public partial class StaffingController
    {
        /// <summary>
        /// "Fyll med förslag": räknar ut ett förslag på hur luckorna kan fyllas — ingenting sparas. Svaret har förslaget (varje rad med skäl), de luckor som
        /// inte gick att fylla (med skäl) och ett fingeravtryck som Tillämpa skickar tillbaka.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetProposal(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            // Förslag görs för EN planering (en del, eller en fristående) — luckorna är den planeringens, upptagen-tiden läses över hela familjen.
            try
            {
                using var cts = new CancellationTokenSource(StaffProposalService.MaxRun);
                var p = HttpContext.RequestServices.GetRequiredService<StaffProposalService>().Build(owner, cts.Token);
                if (p.Problems.Count > 0)
                {
                    _logger.LogError("Staffing: förslaget bröt de hårda villkoren för planering {Planning}: {Problems}", owner.PlanningId, string.Join(" | ", p.Problems));
                    return Json(new { success = false, message = "Förslaget kunde inte kontrolleras och visas därför inte. Det är ett fel hos oss — försök igen eller lägg in för hand." });
                }
                string Iv(StaffInterval i) => i.AllDay ? "hela dagen" : $"{i.Start:HH\\:mm}–{i.End:HH\\:mm}";
                var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
                return Json(new
                {
                    success = true, fingerprint = p.Fingerprint,
                    placed = p.Result.Placed,
                    stillMissing = p.Result.StillMissing,
                    needed = p.Slots.Sum(s => s.Missing),
                    volunteers = p.People.Count,
                    rows = p.Result.Proposed.Select(r => new
                    {
                        slotKey = r.SlotKey, memberId = r.MemberId, name = r.Name, role = p.Slots.First(s => s.Key == r.SlotKey).RoleName,
                        scope = p.Slots.First(s => s.Key == r.SlotKey).ScopeLabel, date = r.Date.ToString("yyyy-MM-dd"), dayLabel = r.Date.ToString("ddd d MMM", sv),
                        time = Iv(r.Interval), why = r.Why, warning = r.Warning,
                    }),
                    unfilled = p.Result.Unfilled.Select(u => new
                    {
                        slotKey = u.SlotKey, role = u.RoleName, scope = u.ScopeLabel, dayLabel = u.Date.ToString("ddd d MMM", sv), time = Iv(u.Interval),
                        missing = u.Missing, reason = u.Reason,
                    }),
                });
            }
            catch (OperationCanceledException)
            {
                return Json(new { success = false, message = "Det tog för lång tid att räkna ut ett förslag. Lägg in för hand, eller försök igen." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetProposal failed for planning {Planning}", owner.PlanningIdOrNull);
                return Json(new { success = false, message = "Kunde inte räkna ut ett förslag." });
            }
        }

        /// <summary>
        /// Tillämpar ett förslag (eller delar av det): varje rad prövas på nytt, allt skrivs i EN transaktion som Planerat — ingen får något mejl.
        /// Har underlaget ändrats sedan förslaget gjordes (<c>Fingerprint</c>) avvisas allt.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyProposal([FromBody] ApplyProposalRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.Rows == null || request.Rows.Count == 0)
                return Json(new { success = false, message = "Välj minst en rad." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                using var cts = new CancellationTokenSource(StaffProposalService.MaxRun);
                var o = HttpContext.RequestServices.GetRequiredService<StaffProposalService>()
                    .Apply(owner, request.Rows.Select(r => (r.SlotKey ?? "", r.MemberId)).ToList(), request.Fingerprint, viewer.Id, cts.Token);
                return Json(new
                {
                    success = o.Ok, message = o.Message, batchId = o.BatchId,
                    added = o.Added.Select(a => new { memberId = a.MemberId, name = a.Name, id = a.Id }),
                    skipped = o.Skipped.Select(s => new { memberId = s.MemberId, name = s.Name, reason = s.Reason }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ApplyProposal failed for planning {Planning}", owner.PlanningIdOrNull);
                return Json(new { success = false, message = "Kunde inte lägga in förslaget. Inget ändrades." });
            }
        }

        /// <summary>Ångrar en tillämpning: tar bort raderna som fortfarande är Planerade (de som hunnit få svar står kvar och räknas).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UndoProposal([FromBody] UndoProposalRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.BatchId == Guid.Empty) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            var (removed, kept) = HttpContext.RequestServices.GetRequiredService<StaffProposalService>().Undo(owner, request.BatchId);
            if (kept < 0) return Json(new { success = false, message = StaffProposalService.BusyMessage });
            return Json(new { success = true, removed, kept });
        }

        private static DistributionSpec SpecOf(DistributionRequest r) => new((r.RoleKey ?? "").Trim(), (r.MemberIds ?? new()).Where(i => i > 0).Distinct().Take(200).ToList(), r.BlockMinutes);

        /// <summary>
        /// "Fördela dem åt mig": de namngivna personerna (ur den inklistrade listan) sprids över rollens stationer och dagens block, med tider som inte krockar med deras egna starter
        /// eller uppdrag. Ingenting sparas. Svaret har varje rad med skäl, de som inte fick någon plats (med skäl), luckor som blev kvar och ett fingeravtryck som Tillämpa skickar tillbaka.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PreviewDistribution([FromBody] DistributionRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey) || request.MemberIds == null || request.MemberIds.Count == 0)
                return Json(new { success = false, message = "Välj en roll och minst en person." });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var spec = SpecOf(request);
                using var cts = new CancellationTokenSource(StaffProposalService.MaxRun);
                var svc = HttpContext.RequestServices.GetRequiredService<StaffProposalService>();
                var (p, chosenBlock) = svc.BuildDistributionBest(owner, spec, cts.Token);
                if (p.Problems.Count > 0)
                {
                    _logger.LogError("Staffing: fördelningen bröt de hårda villkoren för planering {Planning}: {Problems}", owner.PlanningId, string.Join(" | ", p.Problems));
                    return Json(new { success = false, message = "Fördelningen kunde inte kontrolleras och visas därför inte. Det är ett fel hos oss — lägg in för hand." });
                }
                if (p.Slots.Count == 0)
                    return Json(new { success = false, message = "Rollen har inget behov som saknar folk. Sätt hur många som behövs, eller välj en annan roll." });
                var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
                string Iv(StaffInterval i) => $"{i.Start:HH\\:mm}–{i.End:HH\\:mm}";
                var placedIds = p.Result.Proposed.Select(r => r.MemberId).ToHashSet();
                var found = p.People.Select(x => x.MemberId).ToHashSet();
                return Json(new
                {
                    success = true, fingerprint = p.Fingerprint, blockMinutes = chosenBlock, autoBlock = spec.BlockMinutes <= 0,
                    placed = p.Result.Placed, stillMissing = p.Result.StillMissing, needed = p.Slots.Sum(s => s.Missing),
                    rows = p.Result.Proposed.Select(r => new
                    {
                        slotKey = r.SlotKey, memberId = r.MemberId, name = r.Name, scope = p.Slots.First(s => s.Key == r.SlotKey).ScopeLabel, role = p.Slots.First(s => s.Key == r.SlotKey).RoleName,
                        date = r.Date.ToString("yyyy-MM-dd"), dayLabel = r.Date.ToString("ddd d MMM", sv), time = Iv(r.Interval), why = r.Why, warning = r.Warning,
                    }),
                    // Personerna som inte fick någon plats, och varför (upptagna hela tiden, eller för många för behovet).
                    unplaced = p.People.Where(x => !placedIds.Contains(x.MemberId)).Select(x =>
                    {
                        // Exakt skäl: kunde personen tagit något block alls? Nej = upptagen under alla (med den första egna starten), ja = blocken fylldes av andra.
                        var couldTake = p.Slots.Any(s => StaffingRules.FirstConflict(s.Interval, s.Place, x.Busy) is not { IsHard: true });
                        var own = x.Busy.FirstOrDefault(b => b.Kind == BusyKind.Start)?.Label;
                        return new { memberId = x.MemberId, name = x.Name,
                            reason = couldTake ? (p.Result.StillMissing == 0 ? "Alla platser är redan fyllda — hen behövs inte" : "De platser hen kunde ta fylldes av andra") : $"Är upptagen under alla block som saknar folk{(own != null ? $" ({own})" : "")}" };
                    }),
                    unknown = spec.MemberIds.Where(i => !found.Contains(i)).ToList(),
                    unfilled = p.Result.Unfilled.Select(u => new { slotKey = u.SlotKey, scope = u.ScopeLabel, dayLabel = u.Date.ToString("ddd d MMM", sv), time = Iv(u.Interval), missing = u.Missing, reason = u.Reason }),
                });
            }
            catch (OperationCanceledException) { return Json(new { success = false, message = "Det tog för lång tid att räkna ut en fördelning. Försök igen, eller lägg in för hand." }); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: PreviewDistribution failed for planning {Planning}", owner.PlanningIdOrNull);
                return Json(new { success = false, message = "Kunde inte räkna ut en fördelning." });
            }
        }

        /// <summary>Tillämpar en fördelning (eller delar av den): varje rad prövas på nytt, allt skrivs som Planerat i EN transaktion under ett lås. Ångra med <c>UndoProposal</c>.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyDistribution([FromBody] DistributionRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || string.IsNullOrWhiteSpace(request.RoleKey) || request.Rows == null || request.Rows.Count == 0)
                return Json(new { success = false, message = "Välj minst en rad." });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await OwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var spec = SpecOf(request);
                using var cts = new CancellationTokenSource(StaffProposalService.MaxRun);
                var svc = HttpContext.RequestServices.GetRequiredService<StaffProposalService>();
                var o = svc.Apply(owner, request.Rows.Select(r => (r.SlotKey ?? "", r.MemberId)).ToList(), request.Fingerprint, viewer.Id, cts.Token, () => svc.BuildDistribution(owner, spec, cts.Token));
                return Json(new
                {
                    success = o.Ok, message = o.Message, batchId = o.BatchId,
                    added = o.Added.Select(a => new { memberId = a.MemberId, name = a.Name, id = a.Id }),
                    skipped = o.Skipped.Select(s => new { memberId = s.MemberId, name = s.Name, reason = s.Reason }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ApplyDistribution failed for planning {Planning}", owner.PlanningIdOrNull);
                return Json(new { success = false, message = "Kunde inte lägga in fördelningen. Inget ändrades." });
            }
        }
    }

    public class DistributionRequest
    {
        public int CompetitionId { get; set; }
        public string? RoleKey { get; set; }
        public List<int>? MemberIds { get; set; }
        /// <summary>Hur långa block dagen delas i (60–480 minuter; 0 = välj åt användaren: 3, 2 eller 1,5 timme, det som placerar flest). Har dagen pass används passen.</summary>
        public int BlockMinutes { get; set; }
        public string? Fingerprint { get; set; }
        public List<ApplyProposalRow>? Rows { get; set; }
    }

    public class ApplyProposalRequest
    {
        public int CompetitionId { get; set; }
        public string? Fingerprint { get; set; }
        public List<ApplyProposalRow>? Rows { get; set; }
    }

    public class ApplyProposalRow
    {
        public string? SlotKey { get; set; }
        public int MemberId { get; set; }
    }

    public class UndoProposalRequest
    {
        public int CompetitionId { get; set; }
        public Guid BatchId { get; set; }
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
