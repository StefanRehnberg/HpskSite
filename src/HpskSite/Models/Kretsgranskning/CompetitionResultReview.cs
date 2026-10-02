using NPoco;

namespace HpskSite.Models.Kretsgranskning
{
    /// <summary>
    /// Kretsens granskning av en resultatlista (fas 2). En rad per (tävling, huvudlista/deltävling).
    ///
    /// <para><b>Beslut 2026-10-02: en STÄMPEL, inte en grind.</b> Standardmedaljerna verifieras som
    /// i dag vid publicering; en godkänd lista får märkningen "Granskad av …krets". Bara när kretsen
    /// själv kräver godkännande (<see cref="Gated"/>, fryst vid inskick) väntar medaljerna.</para>
    /// </summary>
    [TableName("CompetitionResultReview")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionResultReview
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public bool IsSubCompetition { get; set; }
        public int RegionId { get; set; }
        public string Status { get; set; } = ResultReviewStatus.Inskickad;
        public bool Gated { get; set; }
        public bool RequiresTwo { get; set; }
        public string Checksum { get; set; } = "";
        public string? Snapshot { get; set; }
        public bool WeaponCheckAttested { get; set; }
        public string? ArrangerNote { get; set; }
        public DateTime SubmittedAt { get; set; }
        public int SubmittedByMemberId { get; set; }
        public string? SubmittedByName { get; set; }
        public DateTime? FirstApprovedAt { get; set; }
        public int? FirstApproverMemberId { get; set; }
        public string? FirstApproverName { get; set; }
        public DateTime? DecidedAt { get; set; }
        public int? DecidedByMemberId { get; set; }
        public string? DeciderName { get; set; }
        public string? DeciderRole { get; set; }
        public string? Comment { get; set; }
        public string? Channel { get; set; }
        public string? LinkSentTo { get; set; }
        public DateTime UpdatedAt { get; set; }

        [Ignore] public bool IsApproved => Status == ResultReviewStatus.Godkand;

        /// <summary>Standardmedaljerna väntar på kretsen: bara i en krets med grinden, och bara tills godkänt.</summary>
        [Ignore] public bool MedalsPending => Gated && !IsApproved;
    }

    [TableName("CompetitionResultReviewEvent")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionResultReviewEvent
    {
        public int Id { get; set; }
        public int ReviewId { get; set; }
        public DateTime At { get; set; }
        public int ByMemberId { get; set; }
        public string? ByName { get; set; }
        public string Kind { get; set; } = "";
        public string? Text { get; set; }
    }

    /// <summary>⚠️ Lagras som text. Byt aldrig en befintlig sträng.</summary>
    public static class ResultReviewStatus
    {
        public const string Inskickad = "Inskickad";
        public const string Godkand = "Godkand";
        public const string Atersand = "Atersand";

        public static string Label(string? s) => s switch
        {
            Inskickad => "Inskickad till kretsen",
            Godkand => "Granskad av kretsen",
            Atersand => "Återsänd av kretsen",
            _ => s ?? ""
        };
    }

    public static class ResultReviewEventKind
    {
        public const string Submitted = "Submitted";
        public const string FirstApproval = "FirstApproval";
        public const string Approved = "Approved";
        public const string Returned = "Returned";
        public const string ChangedAfterApproval = "ChangedAfterApproval";
        public const string Resubmitted = "Resubmitted";
    }

    /// <summary>Granskningens regler som rena funktioner.</summary>
    public static class ResultReviewRules
    {
        /// <summary>
        /// Vilka tävlingar erbjuds granskning: de som ger standardmedaljer. En föreningstävling
        /// ger inga (fas 0) och har därmed inget att granska.
        /// </summary>
        public static bool Offered(bool awardsStandardMedals, bool isClubOnly) => awardsStandardMedals && !isClubOnly;

        /// <summary>SHB C.4.3.1.10: resultatlistan till kretsen inom 14 dagar efter tävlingen.</summary>
        public static DateTime SendDeadline(DateTime competitionEnd) => competitionEnd.Date.AddDays(14);

        /// <summary>Kan kretsen besluta nu?</summary>
        public static bool KretsCanAct(CompetitionResultReview r) => r.Status == ResultReviewStatus.Inskickad;

        public enum ApproveOutcome { Approved, FirstOfTwo, SameReviewerTwice, NotPending }

        /// <summary>
        /// Ett godkännande. Med två granskare blir det första "1 av 2" och det andra — av en ANNAN
        /// person — gör listan godkänd. Via länk (memberId 0) jämförs namnet.
        /// </summary>
        public static ApproveOutcome Approve(CompetitionResultReview r, int memberId, string name)
        {
            if (!KretsCanAct(r)) return ApproveOutcome.NotPending;
            if (!r.RequiresTwo) return ApproveOutcome.Approved;
            if (r.FirstApproverMemberId is null && r.FirstApproverName is null) return ApproveOutcome.FirstOfTwo;
            var same = memberId > 0
                ? r.FirstApproverMemberId == memberId
                : string.Equals((r.FirstApproverName ?? "").Trim(), (name ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            return same ? ApproveOutcome.SameReviewerTwice : ApproveOutcome.Approved;
        }

        /// <summary>
        /// Har listan ändrats sedan den skickades in eller godkändes? Då gäller inte godkännandet
        /// längre — en godkänd lista som ändras tyst är precis det granskningen ska förhindra.
        /// </summary>
        public static bool Changed(CompetitionResultReview r, string currentChecksum) =>
            !string.Equals(r.Checksum, currentChecksum, StringComparison.Ordinal);

        /// <summary>
        /// Fält som ändras utan att listans INNEHÅLL ändras: omräkningens tidsstämpel, publiceringsläget
        /// och radernas inmatningsstämplar. Utan att plocka bort dem hade en omräkning med exakt samma
        /// resultat — eller ett byte mellan preliminär och publicerad — skickat tillbaka en godkänd lista.
        /// </summary>
        public static readonly HashSet<string> VolatileKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "UpdatedAt", "CreatedAt", "GeneratedAt", "CalculatedAt", "PublishedAt",
            "LastModified", "EnteredAt", "EnteredBy", "IsOfficial", "OfficialWeaponClasses"
        };

        /// <summary>
        /// Kontrollsumma över resultData — den lagrade artefakten är det som granskas — med de
        /// flyktiga fälten bortplockade (<see cref="VolatileKeys"/>). Ogiltig JSON hashas som den är.
        /// </summary>
        public static string Checksum(string? resultData)
        {
            var text = resultData ?? "";
            try
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(text);
                Strip(node);
                text = node?.ToJsonString() ?? "";
            }
            catch (System.Text.Json.JsonException) { /* hashas rått */ }
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..32];
        }

        private static void Strip(System.Text.Json.Nodes.JsonNode? node)
        {
            switch (node)
            {
                case System.Text.Json.Nodes.JsonObject o:
                    foreach (var k in o.Select(p => p.Key).Where(VolatileKeys.Contains).ToList()) o.Remove(k);
                    foreach (var p in o) Strip(p.Value);
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var item in a) Strip(item);
                    break;
            }
        }
    }

    /// <summary>
    /// Resultatgranskningens påminnelser som ren funktion. Claim-then-send i
    /// <c>KretsgranskningReminder</c>; nyckeln är det som gör att varje steg skickas en gång.
    /// </summary>
    public static class ResultReviewReminders
    {
        /// <summary>Arrangören, första påminnelsen: 3 dagar efter tävlingen.</summary>
        public const int ArrangerFirstDays = 3;
        /// <summary>Arrangören, andra påminnelsen: 10 dagar efter (fyra dagar före 14-dagarsgränsen).</summary>
        public const int ArrangerSecondDays = 10;
        /// <summary>Äldre tävlingar påminns inte — annars får varje gammal tävling ett mejl när funktionen slås på.</summary>
        public const int ArrangerWindowDays = 30;
        /// <summary>Kretsen: en inskickad lista som legat 5 dagar.</summary>
        public const int KretsDays = 5;

        public const string KindArranger = "rr-arranger";
        public const string KindKrets = "rr-krets";

        public record Candidate(int CompetitionId, int RegionId, DateTime End);
        public record Due(string Key, string Kind, int CompetitionId, int RegionId, CompetitionResultReview? Review);

        /// <param name="notSubmitted">Tävlingar som erbjuds granskning men inte skickats in.</param>
        /// <param name="pending">Granskningar som ligger hos kretsen.</param>
        public static List<Due> Compute(IEnumerable<Candidate> notSubmitted, IEnumerable<CompetitionResultReview> pending, DateTime today)
        {
            var list = new List<Due>();
            foreach (var c in notSubmitted)
            {
                var days = (today.Date - c.End.Date).TotalDays;
                if (days < ArrangerFirstDays || days > ArrangerWindowDays) continue;
                // Bara det senaste steget som passerats: körs svepet första gången dag 12 ska
                // arrangören få ETT mejl, inte två på samma gång.
                var step = days >= ArrangerSecondDays ? ArrangerSecondDays : ArrangerFirstDays;
                list.Add(new Due($"rr-arr{step}-{c.CompetitionId}", KindArranger, c.CompetitionId, c.RegionId, null));
            }
            foreach (var r in pending)
            {
                if (r.Status != ResultReviewStatus.Inskickad) continue;
                // Nyckeln bär dagen listan senast ändrades/skickades, så ett nytt inskick beväpnar om.
                if ((today.Date - r.UpdatedAt.Date).TotalDays >= KretsDays)
                    list.Add(new Due($"rr-krets{KretsDays}-{r.Id}-{r.UpdatedAt:yyyyMMdd}", KindKrets, r.CompetitionId, r.RegionId, r));
            }
            return list;
        }
    }
}
