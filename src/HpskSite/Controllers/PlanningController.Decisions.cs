using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Models;
using HpskSite.Models.Planning;
using HpskSite.Services;
using HpskSite.Services.Hosting;
using HpskSite.Services.Planning;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Förslag och beslut (P3 steg B, 2026-10-23). Ett förslag läggs fram, deltagarna svarar Vi kan / Vi kan
    /// inte, och beslutet registreras — av arbetsgruppen eller av en styrelse. Ett förslag kan skickas som
    /// ärende till styrelsen i en klubb eller krets som någon i planeringen företräder; ärendet hamnar i samma
    /// kö som styrelsens egna ärenden (källan "Planering"), och styrelsens läge läses därifrån vid varje läsning.
    /// </summary>
    public partial class PlanningController
    {
        /// <summary>
        /// Styrelserna förslaget kan skickas till: de klubbar och kretsar som någon i planeringen företräder.
        /// Fri text ("Förbundets SM-kommitté") är ingen styrelse på sajten och kommer inte med.
        /// </summary>
        private List<(int OwnerType, int OwnerId, string Name)> BoardTargets(int planningId)
        {
            var list = new List<(int, int, string)>();
            foreach (var p in _planning.Participants(planningId))
            {
                if (p.RepresentsClubId > 0) list.Add((DocumentOwnerType.Club, p.RepresentsClubId!.Value, OrgName(DocumentOwnerType.Club, p.RepresentsClubId)));
                else if (p.RepresentsRegionId > 0) list.Add((DocumentOwnerType.Region, p.RepresentsRegionId!.Value, OrgName(DocumentOwnerType.Region, p.RepresentsRegionId)));
            }
            return list.Where(t => t.Item3.Length > 0).Distinct().OrderBy(t => t.Item3, StringComparer.Create(Sv, true)).ToList();
        }

        /// <summary>Styrelsens läge för ett skickat förslag, läst ur ärendekön (lagras inte på förslaget).</summary>
        private object? BoardState(PlanningDecisionRow d, IReadOnlyDictionary<int, BoardWorkService.Placement> placements,
            IReadOnlyDictionary<int, string> names, out bool issueOpen)
        {
            issueOpen = false;
            if (!d.BoardIssueId.HasValue) return null;
            var issue = _boardWork.GetIssue(d.BoardIssueId.Value);
            var org = OrgName(d.BoardOwnerType, d.BoardOwnerId);
            placements.TryGetValue(d.BoardIssueId.Value, out var pl);
            string state, label; string? decision = null;
            if (issue == null) { state = "gone"; label = "Ärendet finns inte längre hos styrelsen."; }
            else if (pl != null && pl.MeetingStatus == "Justerat")
            {
                state = "handled"; label = $"Behandlat på {pl.MeetingTitle} {pl.MeetingDate.ToString("d MMMM yyyy", Sv)} ({pl.Paragraph}).";
                decision = pl.Decision;
            }
            else if (pl != null)
            {
                state = "placed"; label = $"Ligger på dagordningen för {pl.MeetingTitle} {pl.MeetingDate.ToString("d MMMM yyyy", Sv)} ({pl.Paragraph}).";
                issueOpen = true;
            }
            else if (issue.ClosedStatus == BoardIssueClosed.Rejected)
            { state = "rejected"; label = "Styrelsen tar inte upp ärendet." + (string.IsNullOrWhiteSpace(issue.ClosedReason) ? "" : " Skäl: " + issue.ClosedReason); }
            else if (issue.ClosedStatus == BoardIssueClosed.Withdrawn) { state = "withdrawn"; label = "Ärendet är återkallat."; }
            else { state = "waiting"; label = "Väntar på att styrelsen placerar ärendet på ett möte."; issueOpen = true; }
            return new
            {
                org, state, label, decision,
                sentAt = DT(d.SentToBoardAt),
                sentBy = d.SentToBoardBy.HasValue ? names.GetValueOrDefault(d.SentToBoardBy.Value, "Någon") : null,
            };
        }

        [HttpGet]
        public async Task<IActionResult> Decisions(int id, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            var c = x.Caller;
            var people = _planning.Participants(id, includeRemoved: true);
            var names = people.ToDictionary(p => p.Id, p => p.DisplayName);
            var active = people.Where(p => p.IsActive).ToList();
            var decisions = _planning.Decisions(id);
            var responses = _planning.DecisionResponses(id).GroupBy(r => r.DecisionId).ToDictionary(g => g.Key, g => g.ToList());
            var issueIds = decisions.Where(d => d.BoardIssueId.HasValue).Select(d => d.BoardIssueId!.Value).ToList();
            var placements = issueIds.Count == 0 ? new Dictionary<int, BoardWorkService.Placement>() : _boardWork.GetIssuePlacements(issueIds);
            var canDecide = PlanningAccess.CanDecideDecision(c);
            var targets = canDecide ? BoardTargets(id) : new List<(int, int, string)>();
            var canSend = canDecide && !c.ViaLink && x.MemberId > 0;
            var byId = active.ToDictionary(p => p.Id);
            return Json(new
            {
                success = true,
                canPropose = PlanningAccess.CanProposeDecision(c),
                canDecide,
                boardTargets = targets.Select(tg => new { ownerType = tg.Item1, ownerId = tg.Item2, name = tg.Item3 }),
                decisions = decisions.OrderByDescending(d => d.IsOpen).ThenByDescending(d => d.Id).Select(d =>
                {
                    var rs = responses.GetValueOrDefault(d.Id) ?? new List<PlanningDecisionResponseRow>();
                    var board = BoardState(d, placements, names, out var issueOpen);
                    var mine = rs.FirstOrDefault(r => r.ParticipantId == c.ParticipantId && c.IsParticipant);
                    return new
                    {
                        id = d.Id, title = d.Title, body = d.Body,
                        proposedBy = names.GetValueOrDefault(d.ProposedBy, "Någon"), proposedAt = DT(d.ProposedAt), editedAt = DT(d.EditedAt),
                        status = d.Status, outcome = d.Outcome, outcomeLabel = d.Outcome == null ? null : PlanningDecisionOutcome.Label(d.Outcome),
                        decidedByKind = d.DecidedByKind, decidedBoardName = d.DecidedBoardName, decisionNote = d.DecisionNote,
                        decidedAt = DT(d.DecidedAt), decidedBy = d.DecidedBy.HasValue ? names.GetValueOrDefault(d.DecidedBy.Value, "Någon") : null,
                        responses = rs.Select(r => new
                        {
                            participantId = r.ParticipantId,
                            name = names.GetValueOrDefault(r.ParticipantId, "Har lämnat planeringen"),
                            represents = byId.TryGetValue(r.ParticipantId, out var rp) ? Represents(rp) : "",
                            answer = r.Answer, answerLabel = PlanningDecisionAnswer.Label(r.Answer), comment = r.Comment,
                            at = DT(r.AnsweredAt), beforeEdit = PlanningDecisionRules.AnsweredBeforeEdit(d, r),
                        }),
                        waiting = d.IsOpen ? PlanningDecisionRules.Waiting(active, rs).Select(p => p.DisplayName) : Enumerable.Empty<string>(),
                        mine = mine == null ? null : new { answer = mine.Answer, comment = mine.Comment, beforeEdit = PlanningDecisionRules.AnsweredBeforeEdit(d, mine) },
                        board,
                        canRespond = d.IsOpen && PlanningAccess.CanRespondToDecision(c),
                        canEdit = PlanningAccess.CanEditDecision(c, d) && !issueOpen,
                        canWithdraw = PlanningAccess.CanEditDecision(c, d) && !issueOpen,
                        canDecide = d.IsOpen && canDecide,
                        canReopen = d.Status == PlanningDecisionStatus.Decided && canDecide,
                        canSendToBoard = canSend && targets.Count > 0 && d.IsOpen && !issueOpen,
                    };
                }),
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateDecision(int id, string? title, string? body)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanProposeDecision(x.Caller)) return No("Bara den som deltar lägger fram förslag.");
            var (did, err) = _planning.CreateDecision(id, x.Caller.ParticipantId, title, body);
            return err == null ? Ok(new { success = true, decisionId = did }) : No(err);
        }

        /// <summary>Ett förslag som ligger hos en styrelse (i kön eller på ett möte) får inte ändras här.</summary>
        private bool IsAtBoard(PlanningDecisionRow d)
        {
            if (!d.BoardIssueId.HasValue) return false;
            var issue = _boardWork.GetIssue(d.BoardIssueId.Value);
            if (issue == null) return false;
            var pl = _boardWork.GetIssuePlacement(issue.Id);
            if (pl != null) return pl.MeetingStatus != "Justerat";
            return issue.ClosedStatus == null;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateDecision(int id, int decisionId, string? title, string? body)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var d = _planning.GetDecision(id, decisionId);
            if (d == null) return No("Förslaget finns inte.");
            if (!PlanningAccess.CanEditDecision(x.Caller, d)) return No("Det är den som lade fram förslaget, eller den som kan ändra planeringen, som ändrar det.");
            if (IsAtBoard(d)) return No("Förslaget ligger hos styrelsen och kan inte ändras här.");
            var err = _planning.UpdateDecision(id, decisionId, title, body);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RespondDecision(int id, int decisionId, string? answer, string? comment, string? t)
        {
            var x = await ResolveAsync(id, t);
            if (x == null) return Missing();
            if (!PlanningAccess.CanRespondToDecision(x.Caller)) return No("Bara den som deltar svarar på förslag. Du som följer planeringen läser, men svarar inte.");
            var err = _planning.RespondToDecision(id, decisionId, x.Caller.ParticipantId, answer, comment, x.Caller.ViaLink);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DecideDecision(int id, int decisionId, string? outcome, string? maker, string? boardName, string? note)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanDecideDecision(x.Caller)) return No("Det är den som kan ändra planeringen som registrerar beslutet.");
            var err = _planning.DecideDecision(id, decisionId, x.Caller.ParticipantId, outcome, maker, boardName, note);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReopenDecision(int id, int decisionId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanDecideDecision(x.Caller)) return No("Det är den som kan ändra planeringen som ångrar ett beslut.");
            var err = _planning.ReopenDecision(id, decisionId, x.Caller.ParticipantId);
            return err == null ? Ok() : No(err);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WithdrawDecision(int id, int decisionId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            var d = _planning.GetDecision(id, decisionId);
            if (d == null) return No("Förslaget finns inte.");
            if (!PlanningAccess.CanEditDecision(x.Caller, d)) return No("Det är den som lade fram förslaget, eller den som kan ändra planeringen, som drar tillbaka det.");
            if (IsAtBoard(d)) return No("Förslaget ligger hos styrelsen. Be styrelsen ta bort ärendet först.");
            var err = _planning.WithdrawDecision(id, decisionId, x.Caller.ParticipantId);
            return err == null ? Ok() : No(err);
        }

        /// <summary>
        /// Lägger förslaget som ärende i styrelsens kö — för en klubb eller krets som någon i planeringen
        /// företräder. Ärendet anmäls av den som skickar (hen får besked om placering och beslut), och
        /// sekreteraren och ordföranden aviseras som för styrelsens egna ärenden.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendDecisionToBoard(int id, int decisionId, int ownerType, int ownerId, string? kind)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanDecideDecision(x.Caller) || x.MemberId <= 0) return No("Det är den som kan ändra planeringen, inloggad, som skickar ett förslag till en styrelse.");
            var d = _planning.GetDecision(id, decisionId);
            if (d == null) return No("Förslaget finns inte.");
            var target = BoardTargets(id).FirstOrDefault(tg => tg.OwnerType == ownerType && tg.OwnerId == ownerId);
            if (target.Name == null) return No("Ingen i planeringen företräder den föreningen. Välj en klubb eller krets som någon här företräder.");
            var refusal = PlanningDecisionRules.SendRefusal(d, IsAtBoard(d));
            if (refusal != null) return No(refusal);

            var rs = _planning.DecisionResponses(id).Where(r => r.DecisionId == d.Id).ToList();
            var people = _planning.Participants(id, includeRemoved: true).ToDictionary(p => p.Id);
            string Who(PlanningDecisionResponseRow r) => people.TryGetValue(r.ParticipantId, out var p)
                ? p.DisplayName + (Represents(p) is { Length: > 0 } rep ? $" ({rep})" : "") : "Någon";
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.Body)) lines.Add(d.Body!.Trim());
            lines.Add($"Från tävlingsplaneringen {x.Planning.Name}: {Request.PublicBase()}/tavlingsplanering/{id}#vy=beslut");
            var can = rs.Where(r => r.Answer == PlanningDecisionAnswer.Can).Select(Who).ToList();
            var cannot = rs.Where(r => r.Answer == PlanningDecisionAnswer.Cannot).Select(r => Who(r) + (string.IsNullOrWhiteSpace(r.Comment) ? "" : $": {r.Comment}")).ToList();
            if (can.Count > 0) lines.Add("Vi kan: " + string.Join(", ", can) + ".");
            if (cannot.Count > 0) lines.Add("Vi kan inte: " + string.Join("; ", cannot) + ".");

            BoardIssue issue;
            try
            {
                issue = _boardWork.CreateIssue(ownerType, ownerId, d.Title, string.Join("\n\n", lines), kind ?? BoardIssueKinds.Decision,
                    null, x.MemberId, BoardIssueSources.Planning, d.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planering {Id}: förslag {Decision} kunde inte läggas i styrelsens kö ({Type}/{Owner}).", id, decisionId, ownerType, ownerId);
                return No("Ärendet kunde inte läggas i styrelsens kö. Försök igen om en stund.");
            }
            var err = _planning.MarkDecisionSentToBoard(id, decisionId, x.Caller.ParticipantId, issue.Id, ownerType, ownerId, target.Name);
            int notified = 0;
            try { notified = await _boardNotify.IssueSubmittedAsync(issue, Request.PublicBase()); }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: styrelsen kunde inte aviseras om ärende {Issue}.", id, issue.Id); }
            return err == null ? Ok(new { success = true, issueId = issue.Id, notified, org = target.Name }) : No(err);
        }
    }
}
