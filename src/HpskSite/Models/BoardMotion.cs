using System.Text.Json;
using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// En motion till ett årsmöte (2026-10-08).
    ///
    /// <para><b>Samma modell åt båda hållen.</b> En medlem motionerar till sin klubb, och en
    /// klubbstyrelse motionerar till sin krets. Bara motionären skiljer sig
    /// (<see cref="MotionerKind"/>): en medlem eller en klubb. Mottagaren är
    /// <see cref="OwnerType"/>/<see cref="OwnerId"/> (0 klubb, 1 krets).</para>
    ///
    /// <para><b>⚠️ En motion är en HANDLING.</b> <see cref="Snapshot"/> bär texten som den lämnades
    /// in, och motionären kan inte ändra den efteråt — bara återkalla den. Samma regel som
    /// föreningsintyget: det årsmötet behandlar ska vara det som skickades in.</para>
    ///
    /// <para><b>Läget härleds</b> (<see cref="BoardMotionRules.StateOf"/>): inkommen → yttrande
    /// klart → på dagordningen → behandlad. Årsmötets beslut läses från den underpunkt på årsmötet
    /// som bär <c>MotionId</c>, när protokollet är justerat — det lagras inte en gång till här.</para>
    /// </summary>
    [TableName("BoardMotions")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMotion
    {
        public int Id { get; set; }
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int Year { get; set; }
        public int Number { get; set; }
        public string MotionerKind { get; set; } = BoardMotionerKinds.Member;
        public int? MotionerMemberId { get; set; }
        public int? MotionerClubId { get; set; }
        /// <summary>Snapshot: medlemmens namn, eller klubbens namn.</summary>
        public string MotionerName { get; set; } = string.Empty;
        /// <summary>Klubbmotion: ordföranden som undertecknar för styrelsen (snapshot).</summary>
        public string? SignedByName { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Background { get; set; }
        /// <summary>JSON-lista med förslagen till beslut ("att …").</summary>
        public string? Proposals { get; set; }
        /// <summary>Årsmötet motionen ska behandlas på. null = nästa årsmöte, inte inlagt än.</summary>
        public int? MeetingId { get; set; }
        /// <summary>Klubbmotion: styrelsemötet och punkten där klubben beslutade att motionera.</summary>
        public int? SourceMeetingId { get; set; }
        public int? SourceAgendaItemId { get; set; }
        /// <summary>Hänvisningen i klartext, t.ex. "Styrelsemöte 2026-09-16, §8".</summary>
        public string? SourceReference { get; set; }
        public int SubmittedByMemberId { get; set; }
        public DateTime SubmittedDate { get; set; }
        public string? Snapshot { get; set; }
        public string? BoardOpinion { get; set; }
        /// <summary><see cref="BoardMotionProposals"/>.</summary>
        public string? BoardProposal { get; set; }
        public DateTime? OpinionDate { get; set; }
        public int? OpinionByMemberId { get; set; }
        public DateTime? WithdrawnDate { get; set; }
        public DateTime? DecisionNotifiedDate { get; set; }
        public bool IsActive { get; set; } = true;

        [Ignore]
        public string NumberLabel => BoardMotionRules.NumberLabel(OwnerType, Year, Number);

        [Ignore]
        public List<string> ProposalList => BoardMotionRules.ParseProposals(Proposals);
    }

    [TableName("BoardMotionSupport")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMotionSupport
    {
        public int Id { get; set; }
        public int MotionId { get; set; }
        public int MemberId { get; set; }
        /// <summary><see cref="BoardMotionSupportKinds"/>.</summary>
        public string Kind { get; set; } = BoardMotionSupportKinds.Up;
        public DateTime CreatedDate { get; set; }
    }

    /// <summary>
    /// En bilaga till en motion (2026-10-08) — ett underlag motionären eller styrelsen bifogar,
    /// t.ex. en offert eller en ritning. Filen ligger i dokumentarkivets lagring
    /// (<c>DocumentService.SaveFileAsync</c>); raden säger vilken motion den hör till. Tas bort
    /// genom <see cref="IsActive"/> = false — en bilaga till en behandlad motion är en del av
    /// årsmöteshandlingarna och raderas aldrig.
    /// </summary>
    [TableName("BoardMotionAttachments")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class BoardMotionAttachment
    {
        public int Id { get; set; }
        public int MotionId { get; set; }
        public string FileName { get; set; } = string.Empty;
        public string StoredFileName { get; set; } = string.Empty;
        public long FileSize { get; set; }
        public int UploadedByMemberId { get; set; }
        public DateTime UploadedDate { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public static class BoardMotionerKinds
    {
        public const string Member = "Member";
        public const string Club = "Club";
    }

    public static class BoardMotionSupportKinds
    {
        /// <summary>Medlemmen sätter sitt namn på motionen. Skrivs ut på motionen och i handlingarna.</summary>
        public const string CoSigner = "Medmotionar";
        public const string Up = "Upp";
        public const string Down = "Ner";
        public static bool IsValid(string? k) => k is CoSigner or Up or Down;
    }

    public static class BoardMotionProposals
    {
        public const string Approve = "Bifall";
        public const string Reject = "Avslag";
        public const string Answered = "Besvarad";
        public const string Partly = "DelvisBifall";
        public static readonly string[] All = { Approve, Reject, Answered, Partly };

        public static bool IsValid(string? p) => p != null && All.Contains(p);

        public static string Label(string? p) => p switch
        {
            Approve => "Bifall",
            Reject => "Avslag",
            Answered => "Anse motionen besvarad",
            Partly => "Delvis bifall",
            _ => ""
        };
    }

    public enum BoardMotionState
    {
        /// <summary>Inkommen, styrelsen har inte yttrat sig.</summary>
        Received,
        /// <summary>Styrelsens yttrande är skrivet.</summary>
        OpinionReady,
        /// <summary>Ligger som underpunkt på ett årsmöte som inte är justerat.</summary>
        OnAgenda,
        /// <summary>Årsmötets protokoll är justerat.</summary>
        Decided,
        Withdrawn
    }

    public static class BoardMotionRules
    {
        /// <summary>
        /// Motionsnumret i klartext: "M27-3" till en klubb, "K27-3" till en krets. Året är de två
        /// sista siffrorna i inlämningsåret, så numreringen börjar om varje år och inte kan krocka
        /// mellan åren (det unika indexet är på mottagare + år + nummer).
        /// </summary>
        public static string NumberLabel(int ownerType, int year, int number) =>
            $"{(ownerType == DocumentOwnerType.Region ? "K" : "M")}{year % 100:00}-{number}";

        /// <summary>
        /// Förslagen till beslut. Varje rad blir en att-sats; tomma rader tas bort och "att " sätts
        /// framför om motionären inte skrev det själv — det är så en motion läses upp.
        /// </summary>
        public static List<string> NormalizeProposals(IEnumerable<string?>? raw)
        {
            var list = new List<string>();
            foreach (var r in raw ?? Enumerable.Empty<string?>())
            {
                var t = (r ?? "").Trim();
                if (t.Length == 0) continue;
                if (!t.StartsWith("att ", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(t, "att", StringComparison.OrdinalIgnoreCase))
                    t = "att " + char.ToLower(t[0]) + t.Substring(1);
                list.Add(t);
            }
            return list;
        }

        public static string SerializeProposals(List<string> list) => JsonSerializer.Serialize(list);

        public static List<string> ParseProposals(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<string>();
            try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
            catch { return new List<string> { json }; }
        }

        /// <summary>
        /// Kom motionen in efter sista dag? <b>Dagen räknas MED</b> — en motion som lämnas klockan
        /// 23 på sista dagen är i tid. En motion efter dagen STOPPAS INTE; den märks, och stadgarna
        /// och årsmötet avgör.
        /// </summary>
        public static bool IsLate(DateTime submitted, DateTime? deadline) =>
            deadline.HasValue && submitted.Date > deadline.Value.Date;

        public static BoardMotionState StateOf(bool withdrawn, bool hasOpinion, string? agendaMeetingStatus)
        {
            if (agendaMeetingStatus == "Justerat") return BoardMotionState.Decided;
            if (withdrawn) return BoardMotionState.Withdrawn;
            if (agendaMeetingStatus != null) return BoardMotionState.OnAgenda;
            return hasOpinion ? BoardMotionState.OpinionReady : BoardMotionState.Received;
        }

        public static string StateLabel(BoardMotionState s) => s switch
        {
            BoardMotionState.Received => "Inkommen",
            BoardMotionState.OpinionReady => "Yttrande klart",
            BoardMotionState.OnAgenda => "På årsmötets dagordning",
            BoardMotionState.Decided => "Behandlad",
            BoardMotionState.Withdrawn => "Återkallad",
            _ => ""
        };

        /// <summary>
        /// Får motionären återkalla? Fram till att årsmötet behandlat den. En motion på dagordningen
        /// kan dras tillbaka på mötet — men inte efter att protokollet är justerat.
        /// </summary>
        public static bool CanWithdraw(BoardMotionState s) =>
            s is BoardMotionState.Received or BoardMotionState.OpinionReady or BoardMotionState.OnAgenda;

        /// <summary>
        /// Får man tycka till eller bli medmotionär? Bara på en motion som ännu inte behandlats,
        /// och aldrig på sin egen (motionären står redan som motionär).
        /// </summary>
        public static bool CanSupport(BoardMotionState s, bool isMotioner) =>
            !isMotioner && s is BoardMotionState.Received or BoardMotionState.OpinionReady or BoardMotionState.OnAgenda;
    }
}
