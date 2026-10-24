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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Förslag och beslut (P3 steg B 2026-10-23, omgjort 2026-10-24). Den som lägger fram ett förslag väljer vem
    /// som beslutar: <b>gruppen</b> (de som deltar svarar Ja/Nej, och den som kan ändra planeringen registrerar
    /// beslutet) eller <b>en styrelse</b> i en klubb eller krets som någon i planeringen företräder (förslaget
    /// läggs i styrelsens ärendekö med sina bilagor; styrelsen beslutar utan möte eller på ett möte, och läget
    /// läses därifrån vid varje läsning). Aldrig båda.
    /// </summary>
    public partial class PlanningController
    {
        /// <summary>Högst så många bilagor följer med ett förslag till en styrelse.</summary>
        private const int MaxBoardAttachments = 10;

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

        /// <summary>Styrelsens läge, läst ur ärendekön.</summary>
        private sealed record BoardRead(object Json, string State, string? Outcome, bool AtBoard);

        /// <summary>
        /// Styrelsens läge för ett förslag till en styrelse (lagras inte på förslaget). Ett beslut utan möte vinner
        /// över placeringen — punkten på mötet är då bara anmälan av beslutet.
        /// </summary>
        private BoardRead? BoardState(PlanningDecisionRow d, IReadOnlyDictionary<int, BoardWorkService.Placement> placements,
            IReadOnlyDictionary<int, string> names)
        {
            if (!d.ForBoard) return null;
            var org = d.BoardOwnerType.HasValue && d.BoardOwnerId.HasValue ? OrgName(d.BoardOwnerType.Value, d.BoardOwnerId) : "";
            string state, label; string? decision = null, outcome = null; bool atBoard = false;
            var issue = d.BoardIssueId.HasValue ? _boardWork.GetIssue(d.BoardIssueId.Value) : null;
            BoardWorkService.Placement? pl = null;
            if (d.BoardIssueId.HasValue) placements.TryGetValue(d.BoardIssueId.Value, out pl);
            if (!d.BoardIssueId.HasValue) { state = "unsent"; label = "Förslaget är inte skickat till styrelsen än."; }
            else if (issue == null) { state = "gone"; label = "Ärendet finns inte längre hos styrelsen."; }
            else if (issue.IsDirectlyDecided)
            {
                state = "decided"; outcome = issue.DirectOutcome;
                label = $"Beslutat utan möte {issue.DirectDecidedDate?.ToString("d MMMM yyyy", Sv)}"
                        + (string.IsNullOrWhiteSpace(issue.DirectDeciders) ? "" : $" av {issue.DirectDeciders}") + ".";
                decision = issue.DirectDecision;
            }
            else if (pl != null && pl.MeetingStatus == "Justerat")
            {
                state = "handled"; label = $"Behandlat på {pl.MeetingTitle} {pl.MeetingDate.ToString("d MMMM yyyy", Sv)} ({pl.Paragraph}).";
                decision = pl.Decision;
            }
            else if (pl != null)
            {
                state = "placed"; atBoard = true;
                label = $"Ligger på dagordningen för {pl.MeetingTitle} {pl.MeetingDate.ToString("d MMMM yyyy", Sv)} ({pl.Paragraph}).";
            }
            else if (issue.ClosedStatus == BoardIssueClosed.Rejected)
            { state = "rejected"; label = "Styrelsen tar inte upp ärendet." + (string.IsNullOrWhiteSpace(issue.ClosedReason) ? "" : " Skäl: " + issue.ClosedReason); }
            else if (issue.ClosedStatus == BoardIssueClosed.Withdrawn) { state = "withdrawn"; label = "Ärendet är återkallat."; }
            else { state = "waiting"; atBoard = true; label = "Hos styrelsen. De beslutar direkt eller tar upp det på ett möte."; }
            var json = new
            {
                org, state, label, decision, outcome,
                sentAt = DT(d.SentToBoardAt),
                sentBy = d.SentToBoardBy.HasValue ? names.GetValueOrDefault(d.SentToBoardBy.Value, "Någon") : null,
            };
            return new BoardRead(json, state, outcome, atBoard);
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
            var canSend = canDecide && !c.ViaLink && x.MemberId > 0;
            var targets = canSend ? BoardTargets(id) : new List<(int, int, string)>();
            var byId = active.ToDictionary(p => p.Id);
            return Json(new
            {
                success = true,
                canPropose = PlanningAccess.CanProposeDecision(c),
                canDecide,
                canSendToBoard = canSend && targets.Count > 0,
                boardTargets = targets.Select(tg => new { ownerType = tg.Item1, ownerId = tg.Item2, name = tg.Item3 }),
                decisions = decisions.OrderByDescending(d => d.IsOpen).ThenByDescending(d => d.Id).Select(d =>
                {
                    var rs = responses.GetValueOrDefault(d.Id) ?? new List<PlanningDecisionResponseRow>();
                    var board = BoardState(d, placements, names);
                    var mine = rs.FirstOrDefault(r => r.ParticipantId == c.ParticipantId && c.IsParticipant);
                    // Ett förslag till en styrelse är avgjort när styrelsen beslutat (utan möte eller i ett justerat protokoll).
                    var boardDone = board != null && board.State is "decided" or "handled";
                    var status = d.ForBoard && d.IsOpen && boardDone ? PlanningDecisionStatus.Decided : d.Status;
                    string? outcomeLabel = d.ForBoard
                        ? (board?.Outcome == BoardDirectOutcome.Rejected ? "Avslaget" : board?.Outcome == BoardDirectOutcome.Approved ? "Beslutat" : boardDone ? "Behandlat" : null)
                        : (d.Outcome == null ? null : PlanningDecisionOutcome.Label(d.Outcome));
                    // Ett förslag till en styrelse kan ändras och skickas om när det inte ligger hos styrelsen.
                    var boardFree = board == null || board.State is "unsent" or "rejected" or "withdrawn" or "gone";
                    var canEdit = PlanningAccess.CanEditDecision(c, d) && d.IsOpen && boardFree;
                    return new
                    {
                        id = d.Id, title = d.Title, body = d.Body,
                        deciderKind = d.DeciderKind,
                        proposedBy = names.GetValueOrDefault(d.ProposedBy, "Någon"), proposedAt = DT(d.ProposedAt), editedAt = DT(d.EditedAt),
                        status, outcome = d.ForBoard ? (board?.Outcome == BoardDirectOutcome.Rejected ? PlanningDecisionOutcome.Rejected : boardDone ? PlanningDecisionOutcome.Approved : null) : d.Outcome,
                        outcomeLabel,
                        decisionNote = d.DecisionNote,
                        decidedAt = DT(d.DecidedAt), decidedBy = d.DecidedBy.HasValue ? names.GetValueOrDefault(d.DecidedBy.Value, "Någon") : null,
                        responses = d.ForBoard ? Enumerable.Empty<object>() : rs.Select(r => (object)new
                        {
                            participantId = r.ParticipantId,
                            name = names.GetValueOrDefault(r.ParticipantId, "Har lämnat planeringen"),
                            represents = byId.TryGetValue(r.ParticipantId, out var rp) ? Represents(rp) : "",
                            answer = r.Answer, answerLabel = PlanningDecisionAnswer.Label(r.Answer), comment = r.Comment,
                            at = DT(r.AnsweredAt), beforeEdit = PlanningDecisionRules.AnsweredBeforeEdit(d, r),
                        }),
                        waiting = PlanningDecisionRules.Waiting(d, active, rs).Select(p => p.DisplayName),
                        mine = mine == null || d.ForBoard ? null : new { answer = mine.Answer, comment = mine.Comment, beforeEdit = PlanningDecisionRules.AnsweredBeforeEdit(d, mine) },
                        board = board?.Json,
                        canRespond = !d.ForBoard && d.IsOpen && PlanningAccess.CanRespondToDecision(c),
                        canEdit,
                        canAttach = canEdit,
                        canWithdraw = PlanningAccess.CanEditDecision(c, d) && d.IsOpen && boardFree,
                        canDecide = !d.ForBoard && d.IsOpen && canDecide,
                        canReopen = !d.ForBoard && d.Status == PlanningDecisionStatus.Decided && canDecide,
                        canSendToBoard = d.ForBoard && canSend && d.IsOpen && boardFree,
                    };
                }),
            });
        }

        /// <summary>
        /// Lägger fram ett förslag. <paramref name="decider"/> = Group (gruppen svarar Ja/Nej) eller Board (en styrelse
        /// beslutar). Ett förslag till en styrelse skickas med <see cref="SendDecisionToBoard"/> när bilagorna är valda.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateDecision(int id, string? title, string? body, string? decider, int? ownerType, int? ownerId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanProposeDecision(x.Caller)) return No("Bara den som deltar lägger fram förslag.");
            var kind = PlanningDecisionMaker.Normalize(decider) ?? PlanningDecisionMaker.Group;
            (int OwnerType, int OwnerId, string Name) target = default;
            if (kind == PlanningDecisionMaker.Board)
            {
                if (!PlanningAccess.CanDecideDecision(x.Caller) || x.MemberId <= 0)
                    return No("Det är den som kan ändra planeringen, inloggad, som lägger fram ett förslag till en styrelse.");
                target = BoardTargets(id).FirstOrDefault(tg => tg.OwnerType == ownerType && tg.OwnerId == ownerId);
                if (target.Name == null) return No("Välj en klubb eller krets som någon i planeringen företräder.");
            }
            var (did, err) = _planning.CreateDecision(id, x.Caller.ParticipantId, title, body, kind);
            if (err != null) return No(err);
            if (kind == PlanningDecisionMaker.Board)
                _planning.SetDecisionBoardTarget(id, did, target.OwnerType, target.OwnerId);
            return Ok(new { success = true, decisionId = did });
        }

        /// <summary>Ett förslag som ligger hos en styrelse (i kön eller på ett möte) får inte ändras här.</summary>
        private bool IsAtBoard(PlanningDecisionRow d)
        {
            if (!d.BoardIssueId.HasValue) return false;
            var issue = _boardWork.GetIssue(d.BoardIssueId.Value);
            if (issue == null) return false;
            if (issue.IsDirectlyDecided) return true;
            var pl = _boardWork.GetIssuePlacement(issue.Id);
            if (pl != null) return true;
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
        public async Task<IActionResult> DecideDecision(int id, int decisionId, string? outcome, string? note)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanDecideDecision(x.Caller)) return No("Det är den som kan ändra planeringen som registrerar beslutet.");
            var err = _planning.DecideDecision(id, decisionId, x.Caller.ParticipantId, outcome, note);
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
        /// Lägger förslaget som ärende i styrelsens kö (den styrelse som valdes när förslaget lades fram), med
        /// bilagorna. Filerna KOPIERAS till dokumentarkivets lagring: styrelsen är inte med i planeringen och ska
        /// inte nå planeringens arkiv. Ärendet anmäls av den som skickar (hen får besked om beslutet), och
        /// sekreteraren och ordföranden aviseras som för styrelsens egna ärenden.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendDecisionToBoard(int id, int decisionId)
        {
            var x = await ResolveAsync(id, null);
            if (x == null) return Missing();
            if (!PlanningAccess.CanDecideDecision(x.Caller) || x.MemberId <= 0) return No("Det är den som kan ändra planeringen, inloggad, som skickar ett förslag till en styrelse.");
            var d = _planning.GetDecision(id, decisionId);
            if (d == null) return No("Förslaget finns inte.");
            var refusal = PlanningDecisionRules.SendRefusal(d, IsAtBoard(d));
            if (refusal != null) return No(refusal);
            var target = BoardTargets(id).FirstOrDefault(tg => tg.OwnerType == d.BoardOwnerType && tg.OwnerId == d.BoardOwnerId);
            if (target.Name == null) return No("Ingen i planeringen företräder längre den föreningen. Dra tillbaka förslaget och lägg fram det till en annan styrelse.");

            var who = _planning.Participants(id).FirstOrDefault(p => p.Id == x.Caller.ParticipantId);
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(d.Body)) lines.Add(d.Body!.Trim());
            lines.Add($"Från tävlingsplaneringen {x.Planning.Name}" + (who == null ? "." : $", skickat av {who.DisplayName}" + (Represents(who) is { Length: > 0 } rep ? $" ({rep})." : ".")));

            BoardIssue issue;
            try
            {
                issue = _boardWork.CreateIssue(target.OwnerType, target.OwnerId, d.Title, string.Join("\n\n", lines), BoardIssueKinds.Decision,
                    null, x.MemberId, BoardIssueSources.Planning, d.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Planering {Id}: förslag {Decision} kunde inte läggas i styrelsens kö ({Type}/{Owner}).", id, decisionId, target.OwnerType, target.OwnerId);
                return No("Ärendet kunde inte läggas i styrelsens kö. Försök igen om en stund.");
            }

            var (copied, skipped) = await CopyDecisionAttachmentsAsync(id, d.Id, issue.Id, x.MemberId);
            var err = _planning.MarkDecisionSentToBoard(id, decisionId, x.Caller.ParticipantId, issue.Id, target.OwnerType, target.OwnerId, target.Name);
            int notified = 0;
            try { notified = await _boardNotify.IssueSubmittedAsync(issue, Request.PublicBase()); }
            catch (Exception ex) { _logger.LogError(ex, "Planering {Id}: styrelsen kunde inte aviseras om ärende {Issue}.", id, issue.Id); }
            return err == null ? Ok(new { success = true, issueId = issue.Id, notified, org = target.Name, attachments = copied, skipped }) : No(err);
        }

        /// <summary>Kopierar förslagets bilagor till ärendet. Returnerar antalet och namnen på dem som inte gick.</summary>
        private async Task<(int Copied, List<string> Skipped)> CopyDecisionAttachmentsAsync(int planningId, int decisionId, int issueId, int memberId)
        {
            var skipped = new List<string>();
            var ids = _planning.DocumentRefs(planningId)
                .Where(r => r.TargetKind == PlanningDocTarget.Decision && r.TargetId == decisionId && r.RemovedAt == null)
                .Select(r => r.DocumentId).Distinct().ToList();
            if (ids.Count == 0) return (0, skipped);
            var docs = _planning.Documents(planningId).Where(doc => ids.Contains(doc.Id)).ToList();
            var archive = HttpContext.RequestServices.GetRequiredService<DocumentService>();
            int copied = 0;
            foreach (var doc in docs)
            {
                if (copied >= MaxBoardAttachments) { skipped.Add(doc.Title); continue; }
                try
                {
                    if (!doc.IsFile)
                    {
                        _boardWork.AddIssueAttachment(issueId, doc.Title, null, doc.Url, null, memberId);
                        copied++;
                        continue;
                    }
                    var name = string.IsNullOrWhiteSpace(doc.OriginalFileName) ? doc.StoredFileName! : doc.OriginalFileName!;
                    var (ok, _) = archive.ValidateFile(name, doc.FileSize ?? 0);
                    await using var src = await _docs.OpenReadAsync(doc.StoredFileName);
                    if (!ok || src == null) { skipped.Add(doc.Title); continue; }
                    var stored = await archive.SaveFileAsync(src, name);
                    _boardWork.AddIssueAttachment(issueId, name, stored, null, doc.FileSize, memberId);
                    copied++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Planering {Id}: bilagan {Doc} kunde inte kopieras till ärende {Issue}.", planningId, doc.Id, issueId);
                    skipped.Add(doc.Title);
                }
            }
            return (copied, skipped);
        }
    }
}
