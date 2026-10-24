using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Förslag och beslut (P3 steg B). Samma regler som resten av tjänsten: behörigheten prövas av anroparen
    /// med <see cref="PlanningAccess"/>, här prövas att varje id hör till RÄTT planering, och inga notiser
    /// skickas härifrån. Varje ändring ger en systemrad i meddelandena, så att gruppen ser vad som hänt.
    /// </summary>
    public partial class PlanningService
    {
        public List<PlanningDecisionRow> Decisions(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningDecisionRow>(
                "SELECT * FROM PlanningDecision WHERE PlanningId = @0 AND DeletedAt IS NULL ORDER BY Id", planningId);
        }

        public PlanningDecisionRow? GetDecision(int planningId, int decisionId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.SingleOrDefault<PlanningDecisionRow>(
                "SELECT * FROM PlanningDecision WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", decisionId, planningId);
        }

        public List<PlanningDecisionResponseRow> DecisionResponses(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<PlanningDecisionResponseRow>(
                "SELECT * FROM PlanningDecisionResponse WHERE PlanningId = @0 ORDER BY AnsweredAt", planningId);
        }

        /// <summary>Hur många öppna förslag deltagaren inte har svarat på — siffran i rälsen.</summary>
        public int UnansweredDecisionCount(int planningId, int participantId)
        {
            if (participantId <= 0) return 0;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int>(
                @"SELECT COUNT(*) FROM PlanningDecision d WHERE d.PlanningId = @0 AND d.DeletedAt IS NULL AND d.Status = N'Open' AND d.DeciderKind = N'Group'
                  AND NOT EXISTS (SELECT 1 FROM PlanningDecisionResponse r WHERE r.DecisionId = d.Id AND r.ParticipantId = @1)",
                planningId, participantId);
        }

        private static PlanningDecisionRow? LockDecision(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db, int planningId, int decisionId) =>
            db.SingleOrDefault<PlanningDecisionRow>(
                "SELECT * FROM PlanningDecision WITH (UPDLOCK) WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", decisionId, planningId);

        /// <summary>
        /// Lägger fram ett förslag. <paramref name="deciderKind"/> = Group (gruppen svarar) eller Board (en styrelse
        /// beslutar; anroparen skickar det sedan med <see cref="MarkDecisionSentToBoard"/>).
        /// </summary>
        public (int DecisionId, string? Error) CreateDecision(int planningId, int byParticipantId, string? title, string? body, string? deciderKind = null)
        {
            var err = PlanningDecisionRules.ValidateTitle(title);
            if (err != null) return (0, err);
            var kind = PlanningDecisionMaker.Normalize(deciderKind) ?? PlanningDecisionMaker.Group;
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = new PlanningDecisionRow
            {
                PlanningId = planningId, Title = title!.Trim(), Body = Clean(body, PlanningDecisionRules.MaxBody),
                ProposedBy = byParticipantId, ProposedAt = now, Status = PlanningDecisionStatus.Open, UpdatedAt = now,
                DeciderKind = kind,
            };
            db.Insert(d);
            // Ett förslag till en styrelse får sin systemrad när det skickas (MarkDecisionSentToBoard).
            if (kind == PlanningDecisionMaker.Group)
                InsertSystem(db, planningId, "decision-new", $"Nytt förslag: {d.Title}. Svara Ja eller Nej under Förslag och beslut.", byParticipantId, null, ("decision", d.Id), now);
            Bump(db, planningId);
            return (d.Id, null);
        }

        /// <summary>
        /// Ändrar förslagets text. Svaren står kvar; de som svarade före ändringen märks ut. Anroparen prövar
        /// att förslaget inte ligger hos en styrelse.
        /// </summary>
        public string? UpdateDecision(int planningId, int decisionId, string? title, string? body)
        {
            var err = PlanningDecisionRules.ValidateTitle(title);
            if (err != null) return err;
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = LockDecision(db, planningId, decisionId);
            if (d == null) return "Förslaget finns inte.";
            if (!d.IsOpen) return "Förslaget är redan avgjort och kan inte ändras.";
            var newTitle = title!.Trim();
            var newBody = Clean(body, PlanningDecisionRules.MaxBody);
            if (newTitle == d.Title && newBody == d.Body) return null;
            d.Title = newTitle; d.Body = newBody; d.EditedAt = now; d.UpdatedAt = now;
            db.Update(d);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Ett svar per deltagare och förslag; ett nytt svar ersätter det förra.</summary>
        public string? RespondToDecision(int planningId, int decisionId, int participantId, string? answer, string? comment, bool viaEmail)
        {
            var a = PlanningDecisionAnswer.Normalize(answer);
            if (a == null) return "Välj Ja eller Nej.";
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = LockDecision(db, planningId, decisionId);
            if (d == null) return "Förslaget finns inte.";
            if (PlanningDecisionRules.RespondRefusal(d) is { } refused) return refused;
            var r = db.SingleOrDefault<PlanningDecisionResponseRow>(
                "SELECT * FROM PlanningDecisionResponse WITH (UPDLOCK, HOLDLOCK) WHERE DecisionId = @0 AND ParticipantId = @1", decisionId, participantId);
            var c = Clean(comment, PlanningDecisionRules.MaxComment);
            if (r == null)
                db.Insert(new PlanningDecisionResponseRow
                {
                    DecisionId = decisionId, PlanningId = planningId, ParticipantId = participantId, Answer = a, Comment = c,
                    AnsweredAt = now, ViaEmail = viaEmail,
                });
            else
            {
                r.Answer = a; r.Comment = c; r.AnsweredAt = now; r.ViaEmail = viaEmail;
                db.Update(r);
            }
            Bump(db, planningId);
            return null;
        }

        /// <summary>
        /// Registrerar gruppens beslut. Ett förslag till en styrelse registreras inte här — styrelsens beslut läses
        /// ur deras ärendekö.
        /// </summary>
        public string? DecideDecision(int planningId, int decisionId, int byParticipantId, string? outcome, string? note)
        {
            var o = PlanningDecisionOutcome.Normalize(outcome);
            if (o == null) return "Välj om förslaget blev beslutat eller avslaget.";
            const string k = PlanningDecisionMaker.Group;
            string? bn = null;
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = LockDecision(db, planningId, decisionId);
            if (d == null) return "Förslaget finns inte.";
            if (d.ForBoard) return "Förslaget beslutas av styrelsen. Deras beslut syns här när det är fattat.";
            if (!d.IsOpen) return "Förslaget är redan avgjort.";
            d.Status = PlanningDecisionStatus.Decided; d.Outcome = o; d.DecidedByKind = k;
            d.DecidedBoardName = k == PlanningDecisionMaker.Board ? bn : null;
            d.DecisionNote = Clean(note, PlanningDecisionRules.MaxBody);
            d.DecidedAt = now; d.DecidedBy = byParticipantId; d.UpdatedAt = now;
            db.Update(d);
            var who = k == PlanningDecisionMaker.Board ? $"beslutat av {d.DecidedBoardName}" : "arbetsgruppens beslut";
            InsertSystem(db, planningId, "decision-decided", $"{d.Title}: {PlanningDecisionOutcome.Label(o).ToLowerInvariant()} ({who}).",
                byParticipantId, null, ("decision", d.Id), now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Ångra beslutet: förslaget är öppet igen, svaren står kvar.</summary>
        public string? ReopenDecision(int planningId, int decisionId, int byParticipantId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = LockDecision(db, planningId, decisionId);
            if (d == null) return "Förslaget finns inte.";
            if (d.Status != PlanningDecisionStatus.Decided) return "Förslaget är inte avgjort.";
            d.Status = PlanningDecisionStatus.Open; d.Outcome = null; d.DecidedByKind = null; d.DecidedBoardName = null;
            d.DecisionNote = null; d.DecidedAt = null; d.DecidedBy = null; d.UpdatedAt = now;
            db.Update(d);
            InsertSystem(db, planningId, "decision-reopened", $"{d.Title}: beslutet är ångrat, förslaget är öppet igen.", byParticipantId, null, ("decision", d.Id), now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Drar tillbaka ett öppet förslag. Det står kvar i listan som tillbakadraget.</summary>
        public string? WithdrawDecision(int planningId, int decisionId, int byParticipantId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = LockDecision(db, planningId, decisionId);
            if (d == null) return "Förslaget finns inte.";
            if (!d.IsOpen) return "Förslaget är redan avgjort.";
            d.Status = PlanningDecisionStatus.Withdrawn; d.UpdatedAt = now;
            db.Update(d);
            InsertSystem(db, planningId, "decision-withdrawn", $"{d.Title}: förslaget är tillbakadraget.", byParticipantId, null, ("decision", d.Id), now);
            Bump(db, planningId);
            return null;
        }

        /// <summary>Styrelsen ett förslag till en styrelse ska till — sparas när förslaget läggs fram, före utskicket.</summary>
        public void SetDecisionBoardTarget(int planningId, int decisionId, int ownerType, int ownerId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("UPDATE PlanningDecision SET BoardOwnerType = @2, BoardOwnerId = @3 WHERE Id = @0 AND PlanningId = @1",
                decisionId, planningId, ownerType, ownerId);
        }

        /// <summary>Förslaget har lagts i styrelsens ärendekö: vart, när och av vem.</summary>
        public string? MarkDecisionSentToBoard(int planningId, int decisionId, int byParticipantId, int issueId, int ownerType, int ownerId, string orgName)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var d = LockDecision(db, planningId, decisionId);
            if (d == null) return "Förslaget finns inte.";
            d.BoardIssueId = issueId; d.BoardOwnerType = ownerType; d.BoardOwnerId = ownerId;
            d.SentToBoardAt = now; d.SentToBoardBy = byParticipantId; d.UpdatedAt = now;
            db.Update(d);
            InsertSystem(db, planningId, "decision-board", $"{d.Title}: skickat till styrelsen i {orgName}.", byParticipantId, null, ("decision", d.Id), now);
            Bump(db, planningId);
            return null;
        }
    }
}
