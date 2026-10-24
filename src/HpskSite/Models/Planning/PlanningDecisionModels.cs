using System;
using System.Collections.Generic;
using System.Linq;
using NPoco;

namespace HpskSite.Models.Planning
{
    /// <summary>
    /// Förslag och beslut (P3 steg B 2026-10-23, omgjort 2026-10-24). Ett FÖRSLAG läggs fram i planeringen och
    /// den som lägger fram det väljer <b>vem som beslutar</b> (<see cref="DeciderKind"/>):
    /// <list type="bullet">
    /// <item><b>Gruppen</b> — de som deltar svarar <i>Ja</i> eller <i>Nej</i> med en kommentar, och den som kan
    /// ändra planeringen registrerar beslutet.</item>
    /// <item><b>En styrelse</b> — förslaget skickas som ärende till styrelsen i en klubb eller krets som någon i
    /// planeringen företräder. Gruppen svarar INTE (beslutet är styrelsens); den som vill säga något skriver i
    /// meddelandena. Styrelsens beslut — utan möte eller på ett möte — läses ur ärendekön.</item>
    /// </list>
    /// Aldrig båda (Stefans beslut 2026-10-24).
    ///
    /// <para><b>⚠️ Svaren raderas inte när förslaget ändras.</b> Den som svarade före en ändring märks ut
    /// (<see cref="PlanningDecisionRules.AnsweredBeforeEdit"/>).</para>
    ///
    /// <para><b>⚠️ Styrelsens läge lagras inte här.</b> Ärendets placering och styrelsens beslut läses ur
    /// styrelsens ärendekö vid varje läsning. Det som lagras är vart och när det skickades.</para>
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
        /// <summary><see cref="PlanningDecisionMaker"/>: Group (gruppen svarar Ja/Nej) eller Board (en styrelse beslutar).</summary>
        public string DeciderKind { get; set; } = PlanningDecisionMaker.Group;

        [Ignore] public bool IsOpen => Status == PlanningDecisionStatus.Open;
        [Ignore] public bool ForBoard => DeciderKind == PlanningDecisionMaker.Board;
    }

    [TableName("PlanningDecisionResponse")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class PlanningDecisionResponseRow
    {
        public int Id { get; set; }
        public int DecisionId { get; set; }
        public int PlanningId { get; set; }
        public int ParticipantId { get; set; }
        public string Answer { get; set; } = PlanningDecisionAnswer.Yes;
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

    /// <summary>Svaren: Ja och Nej (2026-10-24; hette "Vi kan / Vi kan inte", som passade bara vissa förslag).</summary>
    public static class PlanningDecisionAnswer
    {
        public const string Yes = "Yes";
        public const string No = "No";
        public static string? Normalize(string? a) =>
            string.Equals(a, Yes, StringComparison.OrdinalIgnoreCase) ? Yes
            : string.Equals(a, No, StringComparison.OrdinalIgnoreCase) ? No : null;
        public static string Label(string? a) => a == No ? "Nej" : "Ja";
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
        /// <summary>Arbetsgruppen (planeringen) beslutar: de som deltar svarar Ja/Nej.</summary>
        public const string Group = "Group";
        /// <summary>En styrelse beslutar (förslaget ligger i deras ärendekö).</summary>
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

        /// <summary>Får gruppen svara på förslaget? Bara ett öppet förslag som gruppen själv beslutar om.</summary>
        public static string? RespondRefusal(PlanningDecisionRow d)
        {
            if (d.ForBoard) return "Det här förslaget beslutas av en styrelse. Skriv i meddelandena om du vill säga något.";
            if (!d.IsOpen) return "Förslaget är redan avgjort.";
            return null;
        }

        /// <summary>
        /// Får förslaget skickas till en styrelse nu? Bara ett öppet förslag som en styrelse beslutar om, och
        /// inte en gång till medan ett tidigare ärende ligger hos styrelsen (<paramref name="atBoard"/>).
        /// Avvisat, återkallat eller borttaget ärende = får skickas igen.
        /// </summary>
        public static string? SendRefusal(PlanningDecisionRow d, bool atBoard)
        {
            if (!d.ForBoard) return "Förslaget beslutas av gruppen, inte av en styrelse.";
            if (!d.IsOpen) return "Förslaget är redan avgjort.";
            if (atBoard) return "Förslaget ligger redan hos styrelsen. Vänta på deras besked.";
            return null;
        }

        /// <summary>
        /// Vilka som ännu inte svarat: de som deltar och uppåt (inte de som följer) och som inte har ett svar.
        /// Ett förslag till en styrelse väntar inte på någon i gruppen.
        /// </summary>
        public static List<PlanningMemberRow> Waiting(PlanningDecisionRow d, IEnumerable<PlanningMemberRow> participants,
            IEnumerable<PlanningDecisionResponseRow> responses)
        {
            if (d.ForBoard || !d.IsOpen) return new List<PlanningMemberRow>();
            var answered = new HashSet<int>(responses.Select(r => r.ParticipantId));
            return participants
                .Where(p => p.IsActive && PlanningAccessLevel.Rank(p.Access) >= PlanningAccessLevel.Rank(PlanningAccessLevel.Participant)
                            && !answered.Contains(p.Id))
                .ToList();
        }
    }
}
