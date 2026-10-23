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
    /// Delarna som organisation (P3 steg D, 2026-10-23): grenar och ansvar med en ansvarig och en värd. Uppgifter och
    /// meddelanden kan märkas med en del och filtreras. Bemanning och koppling per del finns inte än (P4).
    /// </summary>
    public partial class PlanningController
    {
        private object PartsJson(int planningId, List<PlanningMemberRow> people, List<PlanningTaskRow> tasks)
        {
            var names = people.ToDictionary(p => p.Id, p => p.DisplayName);
            return _planning.Parts(planningId).Select(p => new
            {
                id = p.Id, name = p.Name, kind = p.Kind, kindLabel = PlanningPartKind.Label(p.Kind), description = p.Description,
                disciplineKey = p.DisciplineKey,
                discipline = string.IsNullOrEmpty(p.DisciplineKey) ? null : HpskSite.Models.CompetitionTypes.GetById(p.DisciplineKey)?.Name,
                responsibleId = p.ResponsibleParticipantId,
                responsible = p.ResponsibleParticipantId.HasValue ? names.GetValueOrDefault(p.ResponsibleParticipantId.Value, "Har lämnat planeringen") : p.ResponsibleName,
                responsibleName = p.ResponsibleName,
                hostClubId = p.HostClubId, hostRegionId = p.HostRegionId, hostText = p.HostText,
                host = p.HostClubId > 0 ? OrgName(DocumentOwnerType.Club, p.HostClubId) : p.HostRegionId > 0 ? OrgName(DocumentOwnerType.Region, p.HostRegionId) : p.HostText,
                openTasks = tasks.Count(t => t.PartId == p.Id && PlanningTaskStatus.IsOpen(t.Status)),
                tasks = tasks.Count(t => t.PartId == p.Id),
            }).ToList();
        }

        /// <summary>Värdarna att välja bland: de klubbar och kretsar som någon i planeringen företräder (annars fri text).</summary>
        [HttpGet]
        public async Task<IActionResult> PartHostOptions(int id)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som lägger upp delar.");
            return Json(new
            {
                success = true,
                hosts = BoardTargets(id).Select(t => new { ownerType = t.OwnerType, ownerId = t.OwnerId, name = t.Name }),
                disciplines = HpskSite.Models.CompetitionTypes.All.Select(d => new { id = d.Id, name = d.Name }),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePart(int id, int? partId, string? name, string? kind, string? description, string? disciplineKey,
            int? responsibleId, string? responsibleName, int? hostType, int? hostId, string? hostText)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som lägger upp delar.");
            if (!string.IsNullOrWhiteSpace(disciplineKey) && HpskSite.Models.CompetitionTypes.GetById(disciplineKey.Trim()) == null) return No("Okänd gren.");
            int? club = null, region = null;
            if (hostId > 0)
            {
                // Bara en klubb eller krets som någon i planeringen företräder — samma urval som förslagens styrelser.
                if (!BoardTargets(id).Any(t => t.OwnerType == hostType && t.OwnerId == hostId)) return No("Välj en klubb eller krets som någon i planeringen företräder, eller skriv värden som text.");
                if (hostType == DocumentOwnerType.Club) club = hostId; else region = hostId;
            }
            var (pid, err) = _planning.SavePart(id, partId ?? 0, x.Caller.ParticipantId, new PlanningService.PartInput(name, kind, description,
                disciplineKey?.Trim(), responsibleId, responsibleName, club, region, hostText));
            return err == null ? Ok(new { success = true, partId = pid }) : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemovePart(int id, int partId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanEditPlanning(x.Caller)) return No("Det är den som kan ändra planeringen som tar bort delar.");
            var (err, tasks, messages) = _planning.RemovePart(id, partId, x.Caller.ParticipantId);
            return err == null ? Ok(new { success = true, tasks, messages }) : No(err);
        }

        /// <summary>Märk en tråd med en del: den som skrev den, eller den som kan ändra planeringen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMessagePart(int id, int messageId, int? partId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var m = _planning.Message(id, messageId);
            if (m == null || m.Kind != "Message") return No("Meddelandet finns inte.");
            if (!(m.ParticipantId == x.Caller.ParticipantId && PlanningAccess.CanWriteMessages(x.Caller)) && !PlanningAccess.CanEditPlanning(x.Caller))
                return No("Det är den som skrev meddelandet, eller den som kan ändra planeringen, som väljer del.");
            var err = _planning.SetMessagePart(id, messageId, partId);
            return err == null ? Ok() : No(err);
        }

        /// <summary>"Ingen ändring" (null), "hela planeringen" (0) eller en del.</summary>
        private static int? PartArg(string? partId, int? keep) =>
            partId == null ? keep : int.TryParse(partId, out var n) && n > 0 ? n : null;
    }
}
