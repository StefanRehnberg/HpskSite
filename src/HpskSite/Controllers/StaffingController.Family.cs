using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    public partial class StaffingController
    {
        /// <summary>
        /// Steg 2 (Expertgruppen): familjens rutnät för huvudplaneringen — det gemensamma och varje del, var och en som sitt
        /// eget, oförändrade rutnät (<see cref="StaffingGridService.BuildGrid"/>), på en gemensam dagaxel. Varje rad och post
        /// bär sin planerings id (K6), och klienten skickar den med varje ändring; servern prövar då delen på nytt
        /// (<c>OwnerAsync</c> → <c>CanEditStaffingIn</c>). En del läsaren inte får ändra visas inte — den räknas bara.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetFamilyGrid(int competitionId)
        {
            var rootOwner = await OwnerAsync(competitionId);
            if (rootOwner is not { HasPlanning: true })
                return Json(new { success = false, message = "Ingen behörighet" });

            try
            {
                var viewer = await ResolveViewerAsync();
                var root = _owners.Planning(rootOwner.PlanningId);
                if (root == null || root.ParentPlanningId > 0)
                    return Json(new { success = false, message = "Familjens rutnät visas i huvudplaneringen." });

                var resp = new FamilyGridResponse();
                foreach (var row in _planning.Family(root.Id))
                {
                    var isRoot = row.Id == root.Id;
                    if (viewer == null || !_planning.CanEditStaffingIn(row.Id, viewer.Id))
                    {
                        if (!isRoot) resp.HiddenParts++;
                        continue;
                    }
                    var owner = _owners.ForPlanning(row.Id);
                    if (owner == null) continue;
                    var discipline = _owners.Discipline(owner);
                    var grid = _grid.BuildGrid(owner, discipline, viewer.Id);
                    try { ApplyRoleChoice(grid, owner, discipline, row.Id); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Staffing: role choice failed for planning {Planning}", row.Id); }
                    resp.Parts.Add(new FamilyGridPart
                    {
                        PlanningId = row.Id,
                        Name = row.Name,
                        IsShared = isRoot,
                        HostLabel = string.IsNullOrWhiteSpace(row.HostText) ? null : row.HostText.Trim(),
                        DayKeys = FamilyGridRules.DayKeys(grid.Columns),
                        Grid = grid,
                    });
                }

                resp.Columns = FamilyGridRules.UnionColumns(resp.Parts.Select(p => p.Grid.Columns));
                try
                {
                    var intents = HttpContext.RequestServices.GetRequiredService<ShootIntentService>();
                    resp.FamilyClashes = intents.Clashes(root.Id).Select(c => new GridClash
                    {
                        PersonKey = c.PersonKey, Name = c.Name, DateKey = c.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                        A = c.A, B = c.B, Kind = c.Kind,
                    }).ToList();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "Staffing: family clashes failed for planning {Planning}", root.Id); }
                return Json(resp);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetFamilyGrid failed for planning {Planning}", rootOwner.PlanningId);
                return Json(new { success = false, message = "Kunde inte läsa familjens rutnät." });
            }
        }

        // ------------------------------------------------------------------ steg 3: personens vecka

        private static object WeekItemJson(PersonWeekItem i) => new
        {
            sourceKey = i.Entry.SourceKey,
            kind = i.Entry.Kind.ToString(),
            status = i.Entry.Status,
            label = i.Entry.Label,
            time = i.Entry.Interval is { } iv ? (iv.AllDay ? "hela dagen" : $"{iv.Start:HH\\:mm}–{iv.End:HH\\:mm}") : null,
            place = i.Entry.Place,
            planningId = i.Entry.PlanningId,
            endsNextDay = i.EndsNextDay,
            hasClash = i.HasClash,
            clashes = i.Clashes.Select(c => new { with = c.WithLabel, kind = c.Kind.ToString(), hard = c.IsHard }),
        };

        private static object WeekDayJson(PersonWeekDay d) => new
        {
            date = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            label = d.Date.ToString("dddd d MMM", CultureInfo.GetCultureInfo("sv-SE")),
            items = d.Items.Select(WeekItemJson),
            carriedIn = d.CarriedIn.Select(e => new { label = e.Label, time = e.Interval is { } iv ? $"{iv.Start:HH\\:mm}–{iv.End:HH\\:mm}" : null }),
        };

        /// <summary>Huvudplaneringens ägare för anropet, eller null (med svaret som ska ges). Bara huvudplaneringens redaktörer.</summary>
        private async Task<(StaffOwner? Root, IActionResult? Error)> FamilyRootAsync(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            // Steg 4–6 M1: veckan visar alla personer i alla delar — den som bara har tävlingens behörighet (OwnerAsync släpper in
            // en GET via tävlingen) ska inte se den. Kräver rätt att ändra bemanningen i huvudplaneringen.
            if (owner is not { HasPlanning: true } || !await CanEditPlanningStaffAsync(owner.PlanningId))
                return (null, Json(new { success = false, message = "Ingen behörighet" }));
            var root = _owners.Planning(owner.PlanningId);
            if (root == null || root.ParentPlanningId > 0)
                return (null, Json(new { success = false, message = "Veckan visas i huvudplaneringen." }));
            return (owner, null);
        }

        /// <summary>Steg 3: alla personer i familjen, med uppdrag och krockar (utan egna starter — de läses per person).</summary>
        [HttpGet]
        public async Task<IActionResult> GetFamilyWeek(int competitionId)
        {
            var (root, err) = await FamilyRootAsync(competitionId);
            if (root == null) return err!;
            try
            {
                var people = HttpContext.RequestServices.GetRequiredService<FamilyWeekService>().Build(root.PlanningId, null, withStarts: true);
                return Json(new
                {
                    success = true,
                    startsIncluded = FamilyWeekService.StartsFitInList(people.Count),
                    people = people.Select(p => new
                    {
                        key = p.Key, name = p.Name, memberId = p.MemberId, isExternal = p.IsExternal,
                        assignments = p.Assignments, clashes = p.Clashes, warnings = p.Warnings,
                        days = p.Days.Select(d => new { date = d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), label = d.Date.ToString("ddd d MMM", CultureInfo.GetCultureInfo("sv-SE")), count = d.Items.Count }),
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetFamilyWeek failed for planning {Planning}", root.PlanningId);
                return Json(new { success = false, message = "Kunde inte läsa veckan." });
            }
        }

        /// <summary>
        /// Steg 6c (B12): när personen har tid att skjuta själv, per tävling i veckan och dag — luckorna mellan uppdrag och egna starter
        /// (<see cref="FreeWindowRules"/>). <c>declared</c> = hen har sagt att hen tänker tävla där. Tom lista när veckan inte har tävlingar med datum.
        /// </summary>
        private object ShootWindows(int rootPlanningId, FamilyWeekService.Person p)
        {
            try
            {
                var intents = HttpContext.RequestServices.GetRequiredService<ShootIntentService>();
                var declared = intents.ForFamily(rootPlanningId).Where(i => i.PersonKey == p.Key).Select(i => i.TargetPlanningId).ToHashSet();
                var busy = p.Days.SelectMany(d => d.Items)
                    .Where(i => i.Entry.Kind != PersonWeekKind.Intent && i.Entry.Interval is not null)
                    .Select(i => i.Entry.Interval!.Value).ToList();
                var sv = CultureInfo.GetCultureInfo("sv-SE");
                return intents.Targets(rootPlanningId).Where(t => t.From != null).Select(t =>
                {
                    var last = (t.To ?? t.From)!.Value.Date;
                    if ((last - t.From!.Value.Date).TotalDays > 14) last = t.From.Value.Date;
                    var days = new List<object>();
                    for (var d = t.From.Value.Date; d <= last; d = d.AddDays(1))
                        days.Add(new
                        {
                            date = d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            label = d.ToString("dddd d MMM", sv),
                            windows = FreeWindowRules.Windows(busy, d).Select(w => new { from = w.Start.ToString("HH:mm", CultureInfo.InvariantCulture), to = w.End.ToString("HH:mm", CultureInfo.InvariantCulture) }),
                        });
                    return (object)new { planningId = t.PlanningId, name = t.Name, declared = declared.Contains(t.PlanningId), days };
                }).ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: lediga fönster för {Key} i planering {Planning} gick inte att räkna.", p.Key, rootPlanningId);
                return new List<object>();
            }
        }

        /// <summary>Steg 3: en persons vecka dag för dag, med egna starter, krockar, platsbyten och förslag på vem hen kan vara.</summary>
        [HttpGet]
        public async Task<IActionResult> GetFamilyPersonWeek(int competitionId, string personKey)
        {
            var (root, err) = await FamilyRootAsync(competitionId);
            if (root == null) return err!;
            if (string.IsNullOrWhiteSpace(personKey)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            try
            {
                var p = HttpContext.RequestServices.GetRequiredService<FamilyWeekService>().Build(root.PlanningId, personKey).FirstOrDefault();
                if (p == null) return Json(new { success = false, message = "Personen har inget uppdrag i veckan." });
                object? match = null;
                if (p.IsExternal)
                {
                    var owners = new List<StaffOwner>();
                    foreach (var row in _planning.Family(root.PlanningId))
                        if (_owners.ForPlanning(row.Id) is { } o) owners.Add(o);
                    var m = _match.Suggest(owners).FirstOrDefault(x => x.Key == personKey);
                    if (m != null) match = new
                    {
                        confident = m.Confident, looksLikeTwoPeople = m.LooksLikeTwoPeople,
                        candidates = m.Candidates.Select(c => new { memberId = c.MemberId, name = c.Name, clubName = c.ClubName, reason = c.Reason }),
                        inPlan = m.InPlan.Select(x => new { name = x.Name, memberId = x.MemberId, rowCount = x.RowCount, reason = x.Reason }),
                    };
                }
                return Json(new
                {
                    success = true, key = p.Key, name = p.Name, memberId = p.MemberId, isExternal = p.IsExternal,
                    assignments = p.Assignments, clashes = p.Clashes, warnings = p.Warnings,
                    days = p.Days.Select(WeekDayJson), match,
                    shootWindows = ShootWindows(root.PlanningId, p),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetFamilyPersonWeek failed for planning {Planning}", root.PlanningId);
                return Json(new { success = false, message = "Kunde inte läsa personens vecka." });
            }
        }

        /// <summary>
        /// Steg 3: koppla en person (eller byt namn) i HELA familjen — samma person kan stå i flera delar. Varje del som
        /// läsaren får ändra tas; resten rörs inte och räknas i svaret.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyFamilyPersonIdentity([FromBody] PersonActionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.PersonKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (request.MemberId <= 0 && string.IsNullOrWhiteSpace(request.NewName))
                return Json(new { success = false, message = "Inget att ändra." });
            var (root, err) = await FamilyRootAsync(request.CompetitionId);
            if (root == null) return err!;
            var viewer = await ResolveViewerAsync();
            try
            {
                int affected = 0, skipped = 0; string? last = null;
                foreach (var row in _planning.Family(root.PlanningId))
                {
                    var owner = _owners.ForPlanning(row.Id);
                    if (owner == null) continue;
                    if (viewer == null || !_planning.CanEditStaffingIn(row.Id, viewer.Id)) { skipped++; continue; }
                    var r = _staffing.ApplyPersonIdentity(request, owner);
                    if (r.Success) affected += r.Affected; else if (r.Message != null && !r.Message.StartsWith("Hittade inga")) last = r.Message;
                }
                return affected > 0 ? Json(new { success = true, affected, skipped }) : Json(new { success = false, message = last ?? "Hittade inga uppdrag för personen." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ApplyFamilyPersonIdentity failed for planning {Planning}", root.PlanningId);
                return Json(new { success = false, message = "Kunde inte spara ändringen." });
            }
        }

        /// <summary>Steg 3: ångra en koppling i hela familjen — det som skrevs över får sitt ursprungliga namn tillbaka.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UndoFamilyPersonIdentity([FromBody] PersonActionRequest request)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.PersonKey))
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var (root, err) = await FamilyRootAsync(request.CompetitionId);
            if (root == null) return err!;
            var viewer = await ResolveViewerAsync();
            try
            {
                var affected = 0;
                foreach (var row in _planning.Family(root.PlanningId))
                {
                    var owner = _owners.ForPlanning(row.Id);
                    if (owner == null || viewer == null || !_planning.CanEditStaffingIn(row.Id, viewer.Id)) continue;
                    var r = _staffing.UndoPersonIdentity(owner, request.PersonKey);
                    if (r.Success) affected += r.Affected;
                }
                return affected > 0 ? Json(new { success = true, affected }) : Json(new { success = false, message = "Det finns inget tidigare namn att gå tillbaka till." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: UndoFamilyPersonIdentity failed for planning {Planning}", root.PlanningId);
                return Json(new { success = false, message = "Kunde inte ångra." });
            }
        }

        /// <summary>
        /// Steg 2 (Stefan B13): "Flytta raden till en annan del…". Radens id består. Rätten prövas i BÅDA delarna
        /// (<c>CanEditStaffingIn</c>), delarna ska höra till samma familj (B14: aldrig över familjegränsen) och rollen ska
        /// finnas i målet — en egen roll följer inte med av sig själv.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MoveAssignmentToPlanning([FromBody] MoveAssignmentToPlanningRequest request)
        {
            if (request == null || request.Id <= 0 || request.TargetPlanningId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            var from = await RowOwnerAsync(_staffing.OwnerOf(request.Id));
            var to = await RowOwnerAsync(_owners.ForPlanning(request.TargetPlanningId));
            if (from == null || to == null || !from.HasPlanning || !to.HasPlanning)
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                if (!_planning.Family(from.PlanningId).Any(p => p.Id == to.PlanningId))
                    return Json(new { success = false, message = "Uppdraget kan bara flyttas inom samma huvudplanering." });
                var row = _staffing.GetById(request.Id);
                if (row == null) return Json(new { success = false, message = "Uppdraget hittades inte." });
                if (!_roleCatalog.For(to, _owners.Discipline(to)).Any(r => string.Equals(r.Key, row.RoleKey, StringComparison.OrdinalIgnoreCase)))
                    return Json(new { success = false, message = "Rollen finns inte i den delen. Skriv rollen där först." });
                var (ok, msg, revoked) = _staffing.MoveToPlanning(request.Id, from, to);
                if (ok && revoked)
                    _logger.LogInformation("Staffing: assignment {Id} moved from planning {From} to {To}; app access in the competition ended", request.Id, from.PlanningId, to.PlanningId);
                // Steg 4–6 H3: en Tävlingsledning-rad som flyttar lämnar eller når en tävling — speglingen till tävlingsansvariga ska följa med.
                if (ok)
                    foreach (var c in new[] { from.Comp, to.Comp }.Where(c => c > 0).Distinct())
                        try { _staffing.SyncCompetitionMirrors(c); } catch (Exception ex) { _logger.LogWarning(ex, "Staffing: spegling efter flytt misslyckades för tävling {Comp}", c); }
                return Json(new { success = ok, message = msg, accessEnded = ok && revoked });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: MoveAssignmentToPlanning failed for {Id}", request.Id);
                return Json(new { success = false, message = "Kunde inte flytta uppdraget." });
            }
        }

        // ------------------------------------------------------------------ steg 6b: Hitta ersättare

        /// <summary>
        /// Vem kan ta uppdraget? De som anmält sig till dagen i någon del av familjen, lediga och med rätt roll först. Bara den som får
        /// ändra uppdragets del (<c>RowOwnerAsync</c>) ser listan — den visar personers anmälningar och när de är upptagna.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetReplacementCandidates(int assignmentId)
        {
            var (owner, byLeader) = await ReplaceOwnerAsync(assignmentId, null);
            if (owner == null) return Json(new { success = false, message = byLeader.Refusal ?? "Ingen behörighet" });
            try
            {
                var viewer = await ResolveViewerAsync();
                // En ledare ser bara det den leder: i andra delar visas "uppdrag i en annan del" i stället för vad personen gör där.
                var (t, list, err) = HttpContext.RequestServices.GetRequiredService<StaffReplacementService>()
                    .Candidates(assignmentId, pid => viewer != null && (byLeader.IsLeader ? pid == owner.PlanningId : _planning.CanEditStaffingIn(pid, viewer.Id)));
                if (err != null || t == null) return Json(new { success = false, message = err ?? "Uppdraget hittades inte." });
                return Json(new
                {
                    success = true,
                    target = new { id = t.AssignmentId, holder = t.HolderName, hasHolder = t.HolderMemberId is > 0, role = t.RoleName, when = t.When, hadAccess = t.HadAccess },
                    candidates = list.Select(c => new { memberId = c.MemberId, name = c.Name, roleMatch = c.RoleMatch, free = c.Free, busyWith = c.BusyWith, comment = c.Comment, phone = c.Phone }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetReplacementCandidates failed for {Id}", assignmentId);
                return Json(new { success = false, message = "Kunde inte leta efter någon som kan ta uppdraget." });
            }
        }

        /// <summary>Lämnar ett uppdrag över till en annan medlem och aviserar hen. Samma rad, ny person (<see cref="StaffingService.ReplaceHolder"/>).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReplaceAssignmentHolder([FromBody] ReplaceHolderRequest request)
        {
            if (request == null || request.Id <= 0 || request.MemberId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var (owner, byLeader) = await ReplaceOwnerAsync(request.Id, request.MemberId);
            var viewer = await ResolveViewerAsync();
            if (owner == null || viewer == null) return Json(new { success = false, message = byLeader.Refusal ?? "Ingen behörighet" });
            try
            {
                var r = _staffing.ReplaceHolder(request.Id, owner, request.MemberId);
                if (!r.Ok) return Json(new { success = false, message = r.Message });
                var notified = await HttpContext.RequestServices.GetRequiredService<StaffReplacementService>().NotifyAsync(r.Row!, r.OldName, r.OldMemberId, viewer.Id);
                return Json(new { success = true, notified, name = r.Row!.DisplayName });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ReplaceAssignmentHolder failed for {Id}", request.Id);
                return Json(new { success = false, message = "Kunde inte lämna över uppdraget." });
            }
        }
    }

    public partial class StaffingController
    {
        /// <summary>
        /// Mitt schema, "Din del": uppdragen i en del som den som leder den kan lämna över. Bara den som har en ledarroll (skjutledare,
        /// stationschef) i just den planeringen får listan — den visar vem som har vilket uppdrag och om hen tackat nej.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyPartAssignments(int partId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null || partId <= 0 || !_staffing.LedPlanningIds(viewer.Id).Contains(partId))
                return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var rows = HttpContext.RequestServices.GetRequiredService<StaffReplacementService>().PartAssignments(partId, viewer.Id);
                return Json(new { success = true, rows = rows.Select(r => new { id = r.AssignmentId, holder = r.Holder, role = r.RoleName, when = r.When, status = r.Status, open = r.Open }) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetMyPartAssignments failed for part {Part}", partId);
                return Json(new { success = false, message = "Kunde inte läsa uppdragen i din del." });
            }
        }

        /// <summary>Hur rätten att lämna över uppdraget kom till: av en ledare (skjutledare/stationschef i delen), eller av den som bemannar.</summary>
        private readonly record struct ReplaceAccess(bool IsLeader, string? Refusal);

        /// <summary>
        /// Den som bemannar delen (<c>RowOwnerAsync</c>) får som förut. Annars kan en skjutledare eller stationschef i just den delen
        /// (<see cref="ReplacementAccessRules"/>). <paramref name="newMemberId"/> är null vid läsning av listan; vid överlämning prövas att
        /// den nya personen finns i poolen — en ledare ska inte kunna lägga en uppdrag på vilken medlem som helst.
        /// </summary>
        private async Task<(StaffOwner? Owner, ReplaceAccess Access)> ReplaceOwnerAsync(int assignmentId, int? newMemberId)
        {
            var raw = _staffing.OwnerOf(assignmentId);
            var editor = await RowOwnerAsync(raw);
            if (editor != null) return (editor, new ReplaceAccess(false, null));
            if (raw is not { HasPlanning: true }) return (null, new ReplaceAccess(false, null));
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return (null, new ReplaceAccess(false, null));
            var row = _staffing.GetById(assignmentId);
            if (row == null) return (null, new ReplaceAccess(false, null));

            var leads = _staffing.LedPlanningIds(viewer.Id).Contains(raw.PlanningId);
            bool? inPool = null;
            if (leads && newMemberId is > 0)
            {
                var (_, list, _) = HttpContext.RequestServices.GetRequiredService<StaffReplacementService>().Candidates(assignmentId);
                inPool = list.Any(c => c.MemberId == newMemberId.Value);
            }
            var refusal = ReplacementAccessRules.Refusal(leads, row.RoleKey, row.HasAdminAccess, row.MemberId == viewer.Id, inPool);
            return refusal == null ? (raw, new ReplaceAccess(true, null)) : (null, new ReplaceAccess(false, leads ? refusal : null));
        }
    }

    public class ReplaceHolderRequest
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
    }
}
