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
