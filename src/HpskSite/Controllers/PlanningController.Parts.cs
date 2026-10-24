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
    /// Omgång 2 steg 4 (2026-10-24): OMRÅDENA ersätter delarna. Ett område grupperar uppgifter och meddelanden och har en
    /// ansvarig, en värd (klubb, krets eller fri text) och en beskrivning. EN form (<see cref="AreasJson"/>) för planeringens
    /// svar och uppgifternas, så att de inte kan säga olika saker.
    /// </summary>
    public partial class PlanningController
    {
        private object AreasJson(int planningId, IReadOnlyDictionary<int, string> names, List<PlanningTaskRow> tasks) =>
            _planning.Areas(planningId).Select(a => new
            {
                id = a.Id, name = a.Name, description = a.Description,
                responsibleId = a.ResponsibleParticipantId,
                responsible = a.ResponsibleParticipantId.HasValue ? names.GetValueOrDefault(a.ResponsibleParticipantId.Value, "Har lämnat planeringen") : a.ResponsibleName,
                responsibleName = a.ResponsibleName,
                hostClubId = a.HostClubId, hostRegionId = a.HostRegionId, hostText = a.HostText,
                host = a.HostClubId > 0 ? OrgName(DocumentOwnerType.Club, a.HostClubId) : a.HostRegionId > 0 ? OrgName(DocumentOwnerType.Region, a.HostRegionId) : a.HostText,
                openTasks = tasks.Count(t => t.AreaId == a.Id && PlanningTaskStatus.IsOpen(t.Status)),
                tasks = tasks.Count(t => t.AreaId == a.Id),
            }).ToList();

        /// <summary>Värdarna att välja bland: de klubbar och kretsar som någon i planeringen företräder (annars fri text).</summary>
        [HttpGet]
        public async Task<IActionResult> AreaHostOptions(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManageAreas(x.Caller)) return No("Det är den som kan ändra planeringen som lägger upp områden.");
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
