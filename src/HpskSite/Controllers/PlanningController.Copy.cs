using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models.Planning;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HpskSite.Controllers
{
    /// <summary>Kopiera planeringen och lämna över ledningen (P3 steg C, 2026-10-23). Bara den sammankallande.</summary>
    public partial class PlanningController
    {
        /// <summary>
        /// En ny planering med samma form (områden, uppgifter, milstolpar, delar, bemanningens roller, dagar och
        /// behov). Mottagaren — jag själv eller en annan medlem — blir sammankallande och får ett mejl.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyPlanning(int id, string? name, string? targetStart, string? targetEnd,
            int? recipientMemberId, string? includeMe, string? includeSubs = null)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller) || x.MemberId <= 0) return No("Det är den sammankallande som kopierar planeringen.");
            DateTime? start = ParseDate(targetStart), end = ParseDate(targetEnd);
            if (!string.IsNullOrWhiteSpace(targetStart) && start == null) return No("Skriv första dagen som ÅÅÅÅ-MM-DD.");
            if (!string.IsNullOrWhiteSpace(targetEnd) && end == null) return No("Skriv sista dagen som ÅÅÅÅ-MM-DD.");

            var me = _memberService.GetById(x.MemberId);
            var recipientId = recipientMemberId > 0 ? recipientMemberId.Value : x.MemberId;
            var recipient = recipientId == x.MemberId ? me : _memberService.GetById(recipientId);
            if (recipient == null || !recipient.IsApproved) return No("Medlemmen finns inte.");
            int? Club(Umbraco.Cms.Core.Models.IMember? m) => m != null && _memberClubs.GetPrimaryClubId(m) is var c && c > 0 ? c : null;

            var comp = LinkedCompetition(id);
            // Stefan 2026-10-25 (B7): delplaneringarna följer med (förval) — var och en med sin egen dag att räkna från.
            Dictionary<int, (DateTime?, int?)>? subs = null;
            if (includeSubs != "0" && !(x.Planning.ParentPlanningId > 0))
            {
                subs = new();
                foreach (var sub in _planning.Family(id).Where(f => f.ParentPlanningId == id))
                {
                    var sc = LinkedCompetition(sub.Id);
                    subs[sub.Id] = (AnchorFor(sub, sc), sc?.Id);
                }
            }
            var (report, err) = _planning.Copy(id, new PlanningService.CopyInput(
                string.IsNullOrWhiteSpace(name) ? PlanningCopyRules.DefaultName(x.Planning.Name) : name!.Trim(), start, end,
                recipientId, MemberDisplayName(recipient), Club(recipient),
                x.MemberId, MemberDisplayName(me), Club(me), includeMe != "0",
                AnchorFor(x.Planning, comp), comp?.Id, subs));
            if (err != null || report == null) return No(err ?? "Planeringen kunde inte kopieras.");

            var mailed = false;
            if (recipientId != x.MemberId)
            {
                try
                {
                    var pid = _planning.ParticipantForMember(report.PlanningId, recipientId)?.Id ?? 0;
                    if (pid > 0) mailed = await _notify.InviteAsync(report.PlanningId, pid, null, false);
                }
                catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: mottagaren av kopian {Copy} kunde inte aviseras.", id, report.PlanningId); }
            }
            return Ok(new
            {
                success = true, planningId = report.PlanningId, url = $"/tavlingsplanering/{report.PlanningId}",
                areas = report.Areas, tasks = report.Tasks, roles = report.Roles, days = report.Days, needs = report.Needs,
                datesDropped = report.DatesDropped, daysDropped = report.DaysDropped, subPlannings = report.SubPlannings,
                toSomeoneElse = recipientId != x.MemberId, recipient = MemberDisplayName(recipient), mailed,
            });
        }

        /// <summary>Gör en annan deltagare till sammankallande och den som lämnar till Kan ändra, i ett steg.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> HandOver(int id, int participantId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanManagePlanning(x.Caller)) return No("Det är den sammankallande som lämnar över.");
            var err = _planning.HandOver(id, x.Caller.ParticipantId, participantId);
            return err == null ? Ok() : No(err);
        }
    }
}
