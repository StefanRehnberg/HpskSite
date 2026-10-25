using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// OMRÅDENA: ansvarsområden (Servering, Materiel, Banor …) med en ansvarig. Stefan 2026-10-25: ett område är bara det —
    /// ingen värd, ingen beskrivning, aldrig en gren eller förstadiet till en delplanering. EN form (<see cref="AreasJson"/>)
    /// för planeringens svar och uppgifternas, så att de inte kan säga olika saker.
    /// </summary>
    public partial class PlanningController
    {
        private object AreasJson(int planningId, IReadOnlyDictionary<int, string> names, List<PlanningTaskRow> tasks) =>
            _planning.Areas(planningId).Select(a => new
            {
                id = a.Id, name = a.Name,
                responsibleId = a.ResponsibleParticipantId,
                responsible = a.ResponsibleParticipantId.HasValue ? names.GetValueOrDefault(a.ResponsibleParticipantId.Value, "Har lämnat planeringen") : a.ResponsibleName,
                responsibleName = a.ResponsibleName,
                openTasks = tasks.Count(t => t.AreaId == a.Id && PlanningTaskStatus.IsOpen(t.Status)),
                tasks = tasks.Count(t => t.AreaId == a.Id),
            }).ToList();

        /// <summary>
        /// Vem som kan SKÖTA en delplanering (Stefan B11): de klubbar och kretsar som någon i planeringen företräder (annars
        /// fri text). Används av <i>Ny delplanering</i>, delplaneringens Inställningar och stationernas ansvariga — därför
        /// räcker Kan ändra; själva sparandet har sin egen grind.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> HostOptions(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (x.Caller.ViaLink || !PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som väljer klubb eller krets.");
            return Json(new
            {
                success = true,
                hosts = BoardTargets(id).Select(t => new { ownerType = t.OwnerType, ownerId = t.OwnerId, name = t.Name }),
            });
        }

        /// <summary>Märk en tråd med ett område: den som skrev den, eller den som kan ändra planeringen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMessageArea(int id, int messageId, int? areaId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var m = _planning.Message(id, messageId);
            if (m == null || m.Kind != "Message") return No("Meddelandet finns inte.");
            if (!(m.ParticipantId == x.Caller.ParticipantId && PlanningAccess.CanWriteMessages(x.Caller)) && !PlanningAccess.CanEditPlanning(x.Caller))
                return No("Det är den som skrev meddelandet, eller den som kan ändra planeringen, som väljer område.");
            var err = _planning.SetMessageArea(id, messageId, areaId);
            return err == null ? Ok() : No(err);
        }
    }
}
