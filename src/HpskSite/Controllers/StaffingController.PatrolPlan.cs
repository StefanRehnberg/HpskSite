using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Planera dagen steg 3b: <b>Bygg startlistan runt funktionärerna</b> (fält). Förhandsvisningen skriver ingenting; tillämpningen tar en backup, skriver
    /// i EN transaktion och kan ångras. Rätten: den som får ändra bemanningen <i>och</i> har tävlingens personalbehörighet (startlistans ägare) — ingen av dem ensam.
    /// </summary>
    public partial class StaffingController
    {
        private async Task<StaffOwner?> PatrolOwnerAsync(int competitionId)
        {
            var owner = await OwnerAsync(competitionId);
            if (owner == null || !owner.HasCompetition) return null;
            return await HasCompetitionAccessAsync(owner.Comp) ? owner : null;
        }

        private static string Hm(DateTime? t) => t?.ToString("HH\\:mm") ?? "";

        /// <summary>Räknar ut en ny startlista och visar vad som skulle ändras. Ingenting sparas.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPatrolPlan(int competitionId, int patrolSize = 0, int intervalMinutes = 0, string? firstStart = null, string? grouping = null, int gap = 0, bool keep = true)
        {
            var owner = await PatrolOwnerAsync(competitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var svc = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>();
                var first = DateTime.TryParse(firstStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f) ? f : (DateTime?)null;
                var settings = svc.Normalize(owner.Comp, patrolSize, intervalMinutes, first, grouping, gap, keep);
                var pv = await svc.PreviewAsync(owner, settings);
                if (pv.BlockedReason != null && pv.Plan.Patrols.Count == 0 && pv.Fingerprint == "")
                    return Json(new { success = false, message = pv.BlockedReason });
                var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
                return Json(new
                {
                    success = true, fingerprint = pv.Fingerprint, blocked = pv.BlockedReason,
                    settings = new { patrolSize = settings.PatrolSize, intervalMinutes = settings.IntervalMinutes, firstStart = settings.FirstStart?.ToString("yyyy-MM-ddTHH:mm"), grouping = settings.WeaponGrouping, gap = settings.MultiClassGapMinutes, keep = settings.KeepFitting },
                    keptFitting = pv.KeptFitting,
                    existing = new { patrols = pv.ExistingPatrols, shooters = pv.ExistingShooters },
                    change = new { moved = pv.Moved, unchanged = pv.Unchanged, added = pv.Added, removed = pv.Removed },
                    patrols = pv.Plan.Patrols.Select(p => new
                    {
                        number = p.PatrolNumber, time = Hm(p.StartTime), group = p.WeaponGroup,
                        members = p.Members.Select(m => new { memberId = m.MemberId, name = m.Name, club = m.Club, shootingClass = m.ShootingClass }),
                    }),
                    unavoidable = pv.Plan.Unavoidable.Select(u => new { memberId = u.MemberId, name = u.Name, time = Hm(u.StartTime) }),
                    current = (pv.Current ?? new()).Select(c => new { patrolMemberId = c.PatrolMemberId, memberId = c.MemberId, name = c.Name, shootingClass = c.ShootingClass, number = c.PatrolNumber, time = Hm(c.StartTime), locked = c.Locked }),
                    moves = (pv.Moves ?? new()).Select(m => new { kind = m.Kind, memberId = m.MemberId, name = m.Name, shootingClass = m.ShootingClass, fromPatrol = m.FromPatrol, fromTime = Hm(m.FromTime), toPatrol = m.ToPatrol, toTime = Hm(m.ToTime), why = m.Why }),
                    published = pv.BlockedReason != null,
                    groups = (pv.Groups ?? new()).Select(g => new { label = g.Label, firstStart = g.FirstStart.ToString("HH\\:mm"), interval = g.IntervalMinutes, patrolSize = g.PatrolSize, patrols = g.PatrolCount }),
                    ungrouped = pv.Plan.Ungrouped,
                    reservesLeftOut = pv.Plan.ReservesLeftOut,
                    lockedKept = pv.Plan.LockedKept, lockedDropped = pv.Plan.LockedDropped, lockedOverflow = pv.Plan.LockedOverflow,
                    undoable = svc.LatestUndoable(owner),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: GetPatrolPlan failed for competition {Comp}", owner.Comp);
                return Json(new { success = false, message = "Kunde inte räkna ut en startlista." });
            }
        }

        /// <summary>Bygger om startlistan. Fingeravtrycket från förhandsvisningen måste stämma; en backup tas först.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApplyPatrolPlan([FromBody] ApplyPatrolPlanRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId)) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await PatrolOwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var svc = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>();
                var first = DateTime.TryParse(request.FirstStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var f) ? f : (DateTime?)null;
                var settings = svc.Normalize(owner.Comp, request.PatrolSize, request.IntervalMinutes, first, request.Grouping, request.Gap, request.Keep);
                var o = await svc.ApplyAsync(owner, settings, request.Fingerprint, request.Force, viewer.Id);
                return Json(new { success = o.Ok, message = o.Message, backupId = o.BackupId, patrols = o.Patrols, shooters = o.Shooters });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: ApplyPatrolPlan failed for competition {Comp}", owner.Comp);
                return Json(new { success = false, message = "Kunde inte bygga om startlistan. Inget ändrades." });
            }
        }

        /// <summary>Återställer startlistan som den var före en ombyggnad. Vägras om någon ändrat den sedan (om inte <c>Force</c>).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UndoPatrolPlan([FromBody] UndoPatrolPlanRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.BackupId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var owner = await PatrolOwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            try
            {
                var (ok, msg) = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>().Undo(owner, request.BackupId, request.Force);
                return Json(new { success = ok, message = msg });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Staffing: UndoPatrolPlan failed for competition {Comp}", owner.Comp);
                return Json(new { success = false, message = "Kunde inte återställa. Inget ändrades." });
            }
        }

        /// <summary>Låser eller låser upp en skytts plats i startlistan. Loggas med vem som gjorde det.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetPatrolLock([FromBody] SetPatrolLockRequest request)
        {
            if (request == null || !HasOwnerKey(request.CompetitionId) || request.PatrolMemberId <= 0) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            var owner = await PatrolOwnerAsync(request.CompetitionId);
            if (owner == null) return Json(new { success = false, message = "Ingen behörighet" });
            var n = HttpContext.RequestServices.GetRequiredService<PatrolPlanService>().SetLocked(owner, request.PatrolMemberId, request.Locked);
            if (n == 0) return Json(new { success = false, message = "Platsen finns inte i den här tävlingen." });
            _logger.LogInformation("Startlistan: medlem {By} {Action} plats {PatrolMember} i tävling {Comp}", viewer.Id, request.Locked ? "låste" : "låste upp", request.PatrolMemberId, owner.Comp);
            return Json(new { success = true });
        }
    }

    public class ApplyPatrolPlanRequest
    {
        public int CompetitionId { get; set; }
        public string? Fingerprint { get; set; }
        public int PatrolSize { get; set; }
        public int IntervalMinutes { get; set; }
        public string? FirstStart { get; set; }
        public string? Grouping { get; set; }
        public int Gap { get; set; }
        /// <summary>Behåll de som redan har en start som passar.</summary>
        public bool Keep { get; set; } = true;
        /// <summary>Bygg om även när startlistan är publicerad eller en patrull gått ut.</summary>
        public bool Force { get; set; }
    }

    public class UndoPatrolPlanRequest
    {
        public int CompetitionId { get; set; }
        public int BackupId { get; set; }
        public bool Force { get; set; }
    }

    public class SetPatrolLockRequest
    {
        public int CompetitionId { get; set; }
        public int PatrolMemberId { get; set; }
        public bool Locked { get; set; }
    }
}
