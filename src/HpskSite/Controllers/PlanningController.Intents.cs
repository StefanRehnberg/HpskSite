using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Omgång 3 steg 9 (Stefan 2026-10-24): funktionärernas tävlingsavsikter — vilka av veckans tävlingar en funktionär
    /// tänker tävla i. Den som bemannar ser och ändrar alla; den som deltar ser hur många som tänker tävla per tävling.
    /// </summary>
    public partial class PlanningController
    {
        private ShootIntentService Intents => HttpContext.RequestServices.GetRequiredService<ShootIntentService>();

        private static string? Day(DateTime? d) => d?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        [HttpGet]
        public async Task<IActionResult> ShootIntents(int id, string? t)
        {
            var x = await ResolveAsync(id, t, allowParentReader: true);
            if (x == null) return Missing();
            var targets = Intents.Targets(id);
            var all = Intents.ForFamily(id);
            var full = PlanningAccess.CanEditPlanning(x.Caller) && !x.Caller.ViaLink;
            return Json(new
            {
                success = true,
                canEdit = full,
                targets = targets.Select(tg => new
                {
                    planningId = tg.PlanningId, name = tg.Name, planningName = tg.PlanningName, competitionId = tg.CompetitionId,
                    from = Day(tg.From), to = Day(tg.To), count = all.Count(i => i.TargetPlanningId == tg.PlanningId),
                }),
                intents = full
                    ? all.Select(i => new { personKey = i.PersonKey, name = i.DisplayName, targetPlanningId = i.TargetPlanningId, classes = i.Classes, source = i.Source })
                    : null,
            });
        }

        public sealed class ShootIntentPick
        {
            public int TargetPlanningId { get; set; }
            public string? Classes { get; set; }
        }

        /// <summary>Skriver om en persons avsikter. Den som bemannar för vem som helst; en deltagare bara för sig själv.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveShootIntents(int id, string personKey, string displayName, string? picks)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var self = x.MemberId > 0 && personKey == "m:" + x.MemberId;
            if (x.Caller.ViaLink || !(PlanningAccess.CanEditPlanning(x.Caller) || self))
                return No("Det är den som bemannar som ändrar någon annans tävlingar.");
            List<ShootIntentPick> list;
            try { list = string.IsNullOrWhiteSpace(picks) ? new() : System.Text.Json.JsonSerializer.Deserialize<List<ShootIntentPick>>(picks, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
            catch { return No("Valen gick inte att läsa."); }
            var err = Intents.Save(id, personKey ?? "", displayName ?? "", list.Select(p => (p.TargetPlanningId, p.Classes)),
                self && !PlanningAccess.CanEditPlanning(x.Caller) ? ShootIntentSource.Self : ShootIntentSource.Organiser, x.MemberId);
            return err == null ? Ok(new { success = true }) : No(err);
        }
    }
}
