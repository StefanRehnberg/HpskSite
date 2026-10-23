using System;
using System.Collections.Generic;
using System.Linq;
using NPoco;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Förslag och beslut (P3 steg B, 2026-10-23). Ett FÖRSLAG läggs fram i planeringen, deltagarna svarar
    /// <i>Vi kan</i> eller <i>Vi kan inte</i> med en kommentar, och beslutet registreras på förslaget — som
    /// arbetsgruppens beslut eller som beslutat av en styrelse. Ett förslag kan skickas som ärende till
    /// styrelsen i en klubb eller krets som någon i planeringen företräder (samma kö som styrelsens egna ärenden).
    ///
    /// <para><b>⚠️ Svaren raderas inte när förslaget ändras.</b> Den som svarade före en ändring märks ut
    /// (<see cref="PlanningDecisionRules.AnsweredBeforeEdit"/>) — att tyst kasta ett svar är värre än att visa
    /// att det gavs på en äldre text.</para>
    ///
    /// <para><b>⚠️ Styrelsens läge lagras inte här.</b> Ärendets placering och styrelsens beslut läses ur
    /// styrelsens ärendekö vid varje läsning (som ärendet självt gör). Det som lagras är vart och när det skickades.</para>
    /// </summary>
    [TableName("PlanningDecision")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningDecisionRow
    {
        public int Id { get; set; }
        public int PlanningId { get; set; }
        public string Title { get; set; } = "";
        public string? Body { get; set; }
        public int ProposedBy { get; set; }
        public DateTime ProposedAt { get; set; }
        public DateTime? EditedAt { get; set; }
        public string Status { get; set; } = PlanningDecisionStatus.Open;
        public string? Outcome { get; set; }
        public string? DecidedByKind { get; set; }
        public string? DecidedBoardName { get; set; }
        public string? DecisionNote { get; set; }
        public DateTime? DecidedAt { get; set; }
        public int? DecidedBy { get; set; }
        public int? BoardIssueId { get; set; }
        public int? BoardOwnerType { get; set; }
        public int? BoardOwnerId { get; set; }
        public DateTime? SentToBoardAt { get; set; }
        public int? SentToBoardBy { get; set; }
        public DateTime UpdatedAt { get; set; }
        public DateTime? DeletedAt { get; set; }

        [Ignore] public bool IsOpen => Status == PlanningDecisionStatus.Open;
    }

    [TableName("PlanningDecisionResponse")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningDecisionResponseRow
    {
        public int Id { get; set; }
        public int DecisionId { get; set; }
        public int PlanningId { get; set; }
        public int ParticipantId { get; set; }
        public string Answer { get; set; } = PlanningDecisionAnswer.Can;
        public string? Comment { get; set; }
        public DateTime AnsweredAt { get; set; }
        public bool ViaEmail { get; set; }
    }

    public static class PlanningDecisionStatus
    {
        public const string Open = "Open";
        public const string Decided = "Decided";
        public const string Withdrawn = "Withdrawn";
    }

    public static class PlanningDecisionAnswer
    {
        public const string Can = "Can";
        public const string Cannot = "Cannot";
        public static string? Normalize(string? a) =>
            string.Equals(a, Can, StringComparison.OrdinalIgnoreCase) ? Can
            : string.Equals(a, Cannot, StringComparison.OrdinalIgnoreCase) ? Cannot : null;
        public static string Label(string? a) => a == Cannot ? "Vi kan inte" : "Vi kan";
    }

    public static class PlanningDecisionOutcome
    {
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public static string? Normalize(string? o) =>
            string.Equals(o, Approved, StringComparison.OrdinalIgnoreCase) ? Approved
            : string.Equals(o, Rejected, StringComparison.OrdinalIgnoreCase) ? Rejected : null;
        public static string Label(string? o) => o == Rejected ? "Avslaget" : "Beslutat";
    }

    public static class PlanningDecisionMaker
    {
        /// <summary>Arbetsgruppen (planeringen) beslutade själv.</summary>
        public const string Group = "Group";
        /// <summary>En styrelse beslutade (namnet i <see cref="PlanningDecisionRow.DecidedBoardName"/>).</summary>
        public const string Board = "Board";
        public static string? Normalize(string? k) =>
            string.Equals(k, Group, StringComparison.OrdinalIgnoreCase) ? Group
            : string.Equals(k, Board, StringComparison.OrdinalIgnoreCase) ? Board : null;
    }

    /// <summary>Förslagens regler som rena funktioner, så att de går att pröva utan databas.</summary>
    public static class PlanningDecisionRules
    {
        public const int MaxTitle = 300;
        public const int MaxBody = 4000;
        public const int MaxComment = 1000;

        public static string? ValidateTitle(string? title)
        {
            var t = (title ?? "").Trim();
            if (t.Length == 0) return "Skriv vad förslaget gäller.";
            if (t.Length > MaxTitle) return $"Rubriken är för lång (högst {MaxTitle} tecken).";
            return null;
        }

        /// <summary>Gavs svaret före den senaste ändringen av förslaget? Då gäller det en äldre text.</summary>
        public static bool AnsweredBeforeEdit(PlanningDecisionRow d, PlanningDecisionResponseRow r) =>
            d.EditedAt.HasValue && r.AnsweredAt < d.EditedAt.Value;

        /// <summary>
        /// Får förslaget skickas till en styrelse nu? Bara ett öppet förslag, och inte en gång till medan
        /// ett tidigare ärende ligger i styrelsens kö eller på ett möte som inte är justerat
        /// (<paramref name="atBoard"/>). Avvisat, återkallat eller behandlat ärende = får skickas igen.
        /// </summary>
        public static string? SendRefusal(PlanningDecisionRow d, bool atBoard)
        {
            if (!d.IsOpen) return "Förslaget är redan avgjort.";
            if (atBoard) return "Förslaget ligger redan hos styrelsen. Vänta på deras besked.";
            return null;
        }

        /// <summary>
        /// Vilka som ännu inte svarat: de som deltar och uppåt (inte de som följer) och som inte har ett svar.
        /// Samma krets som kallas till mötena.
        /// </summary>
        public static List<PlanningMemberRow> Waiting(IEnumerable<PlanningMemberRow> participants,
            IEnumerable<PlanningDecisionResponseRow> responses)
        {
            var answered = new HashSet<int>(responses.Select(r => r.ParticipantId));
            return participants
                .Where(p => p.IsActive && PlanningAccessLevel.Rank(p.Access) >= PlanningAccessLevel.Rank(PlanningAccessLevel.Participant)
                            && !answered.Contains(p.Id))
                .ToList();
        }
    }
}
