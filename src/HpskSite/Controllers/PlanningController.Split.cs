using System;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Omgång 2 steg 5 (Stefan 2026-10-24): dela ut ett område till en egen planering. Se
    /// <see cref="PlanningService.SplitArea"/> för vad som flyttar. Bara den sammankallande, bara i en planering som inte
    /// själv är en delplanering.
    /// </summary>
    public partial class PlanningController
    {
        /// <summary>Det som flyttar och förslagen till dialogen. Skriver ingenting.</summary>
        [HttpGet]
        public async Task<IActionResult> SplitPreview(int id, int areaId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanSplitArea(x.Caller, x.Planning)) return No(SplitRefusal(x.Planning));
            var r = _planning.SplitPreview(id, areaId, x.Caller.ParticipantId);
            if (r.Error != null) return No(r.Error);
            return Json(new
            {
                success = true,
                tasks = r.Tasks, openTasks = r.OpenTasks, threads = r.Threads, messages = r.Messages,
                suggestedParticipants = r.SuggestedParticipantIds, suggestedDocuments = r.SuggestedDocumentIds,
                suggestedConvener = r.SuggestedConvenerId,
                disciplineKey = x.Planning.DisciplineKey ?? "",
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SplitArea(int id, int areaId, string name, string? discipline, int convenerParticipantId,
            string? participants, string? documents)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanSplitArea(x.Caller, x.Planning)) return No(SplitRefusal(x.Planning));
            var key = string.IsNullOrWhiteSpace(discipline) ? null : HpskSite.Models.CompetitionTypes.GetById(discipline.Trim())?.Id;
            if (!string.IsNullOrWhiteSpace(discipline) && key == null) return No("Grenen finns inte.");
            var (report, err) = _planning.SplitArea(id, new PlanningService.SplitInput(areaId, name, key, convenerParticipantId,
                Ids(participants), Ids(documents), x.MemberId, x.Caller.ParticipantId));
            if (err != null) return No(err);
            return Ok(new
            {
                success = true, id = report!.PlanningId, name = report.Name, url = $"/tavlingsplanering/{report.PlanningId}",
                tasks = report.Tasks, messages = report.Messages, participants = report.Participants,
                documents = report.Documents, detached = report.Detached,
            });
        }

        private const string ParentReaderLabel = "Läser (sammankallande i huvudplaneringen)";

        private static string SplitRefusal(PlanningRow p) => p.ParentPlanningId > 0
            ? "En delplanering kan inte delas upp i sin tur — dela ut området från huvudplaneringen."
            : "Det är den sammankallande som delar ut ett område till en egen planering.";

        /// <summary>Huvudplaneringen, för en delplanering: namnet, och om den som frågar kan öppna den.</summary>
        private object? ParentJson(PlanningRow p, int memberId)
        {
            if (!(p.ParentPlanningId > 0)) return null;
            var parent = _planning.Get(p.ParentPlanningId!.Value);
            if (parent == null) return null;
            var canOpen = memberId > 0 && _planning.ParticipantForMember(parent.Id, memberId) is { IsActive: true };
            return new { id = parent.Id, name = parent.Name, url = canOpen ? $"/tavlingsplanering/{parent.Id}" : null };
        }

        /// <summary>Delplaneringarnas läge, för huvudplaneringens Översikt. Länken bara för den som kan läsa delplaneringen.</summary>
        private object SubPlanningsJson(PlanningRow p, PlanningCaller c, int memberId)
        {
            if (p.ParentPlanningId > 0 || !c.IsParticipant) return Array.Empty<object>();
            var convener = c.Access == PlanningAccessLevel.Owner && !c.ViaLink;
            return _planning.SubPlannings(p.Id, c.ViaLink ? 0 : memberId).Select(s => new
            {
                id = s.Id, name = s.Name, status = s.Status, conveners = s.Conveners, openTasks = s.OpenTasks, tasks = s.Tasks,
                overdue = s.OverdueTasks, nextMilestone = s.NextMilestoneTitle, nextMilestoneDue = D(s.NextMilestoneDue),
                nextMeeting = s.NextMeetingTitle, nextMeetingAt = DT(s.NextMeetingAt), changedAt = DT(s.ChangedAt),
                url = convener || s.MemberIsParticipant ? $"/tavlingsplanering/{s.Id}" : null,
            }).ToList();
        }
    }
}
