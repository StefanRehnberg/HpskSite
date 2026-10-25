using System.Globalization;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Steg 6a (Expertgruppen, Stefan B11): <b>självanmälan för hela veckan</b>. En funktionär ser en huvudplanerings alla delar på
    /// en sida (Gemensamt, Fält, Precision …), kryssar det hen kan i flera delar och sparar en gång. Lagringen är oförändrad —
    /// en <c>StaffHelpSignup</c> per del — så den sammankallande i varje del ser anmälan i sin egen Hitta funktionärer.
    /// </summary>
    public partial class MinaUppdragController
    {
        /// <summary>Delarna i huvudplaneringen <paramref name="planningId"/> med det som behövs i var och en. Bara för en huvudplanering med delar.</summary>
        [HttpGet("week")]
        public async Task<IActionResult> GetWeekParts(int planningId, bool preview = false)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var family = HttpContext.RequestServices.GetRequiredService<PlanningService>().Family(planningId);
            if (family.Count < 2 || family[0].Id != planningId)
                return Json(new { success = false, message = "Det här är ingen huvudplanering med delar." });

            var parts = new List<object>();
            foreach (var row in family)
            {
                var owner = _owners.ForPlanning(row.Id);
                if (owner == null) continue;
                parts.Add(new
                {
                    planningId = row.Id,
                    name = row.Name,
                    isShared = row.Id == planningId,
                    needDays = _help.NeedDays(owner),
                    slots = _help.GetSlots(owner),
                    roleChoice = _owners.SignupRoleChoice(owner),
                    mySignup = preview ? null : _help.GetMySignup(owner, memberId.Value),
                });
            }
            return Json(new { success = true, parts });
        }

        /// <summary>Sparar anmälan i flera delar på en gång. Varje del måste höra till huvudplaneringens familj.</summary>
        [HttpPost("help-signup-week")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveWeekSignup([FromBody] SaveWeekSignupRequest request)
        {
            if (request == null || request.PlanningId <= 0 || request.Parts == null) return Json(new { success = false, message = "Ogiltig förfrågan" });
            var memberId = await CurrentMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Inte inloggad" });
            var planning = HttpContext.RequestServices.GetRequiredService<PlanningService>();
            var family = planning.Family(request.PlanningId);
            if (family.Count < 2 || family[0].Id != request.PlanningId) return Json(new { success = false, message = "Det här är ingen huvudplanering med delar." });
            var ids = family.Select(f => f.Id).ToHashSet();
            if (request.Parts.Any(p => !ids.Contains(p.PlanningId))) return Json(new { success = false, message = "En av delarna hör inte till planeringen." });

            var md = _memberService.GetByEmail((await _memberManager.GetCurrentMemberAsync())?.Email ?? "");
            var name = md == null ? $"Medlem {memberId}"
                : ($"{md.GetValue<string>("firstName")} {md.GetValue<string>("lastName")}".Trim() is { Length: > 0 } n ? n : (md.Name ?? $"Medlem {memberId}"));

            // Alla delar skrivs — också de utan val, så att ett avbockat val tas bort. Kommentaren följer med där personen har valt något
            // (och i huvudplaneringen); annars skulle varje del få en tom anmälan bara för en kommentar.
            var byPart = request.Parts.GroupBy(p => p.PlanningId).ToDictionary(g => g.Key, g => g.SelectMany(p => p.Slots ?? new()).ToList());
            foreach (var f in family)
            {
                var owner = _owners.ForPlanning(f.Id);
                if (owner == null) continue;
                var slots = byPart.TryGetValue(f.Id, out var s) ? s : new List<HelpSlotChoice>();
                var comment = slots.Count > 0 || f.Id == request.PlanningId ? request.Comment : null;
                _help.SaveMySignup(owner, memberId.Value, name, comment, slots);
            }

            if (request.ShootIntents != null)
            {
                var err = HttpContext.RequestServices.GetRequiredService<ShootIntentService>().Save(request.PlanningId, "m:" + memberId.Value, name,
                    request.ShootIntents.Select(i => (i.TargetPlanningId, i.Classes)), Models.Planning.ShootIntentSource.Self, memberId.Value);
                if (err != null) return Json(new { success = false, message = err });
            }
            return Json(new { success = true });
        }
    }

    public class SaveWeekSignupRequest
    {
        /// <summary>Huvudplaneringen.</summary>
        public int PlanningId { get; set; }
        public string? Comment { get; set; }
        public List<WeekSignupPart> Parts { get; set; } = new();
        public List<ShootIntentChoice>? ShootIntents { get; set; }
    }

    public class WeekSignupPart
    {
        public int PlanningId { get; set; }
        public List<HelpSlotChoice> Slots { get; set; } = new();
    }
}
