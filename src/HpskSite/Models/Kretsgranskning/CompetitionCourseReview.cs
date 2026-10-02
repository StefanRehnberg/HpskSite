using NPoco;

namespace HpskSite.Models.Kretsgranskning
{
    /// <summary>
    /// Kretsens granskning av banan i fältskytte (fas 3, SHB C.3.5.2.2). En rad per tävling.
    ///
    /// <para><b>Samma modell som resultatgranskningen:</b> en stämpel, frivillig för fälttävlingar
    /// på kretsnivå och högre. Nationell fältskjutning kräver den (SHB); en krets kan själv kräva den
    /// för sina kretstävlingar. SM och landsdelsmästerskap granskas av Förbundet
    /// (<see cref="CourseReviewRoute.Forbundet"/>) — där registrerar arrangören bara när underlaget
    /// skickades och vad Förbundet svarade.</para>
    ///
    /// <para><b>⚠️ Granskningen gäller TÄVLINGENS stationConfig, inte den sparade konfigurationen.</b>
    /// En konfiguration kopieras in vid "Anslut" och kopian kan ändras efteråt, så ett godkännande av
    /// konfigurationen hade inte sagt något om vad som faktiskt skjuts. Banläggarens godkännande av
    /// konfigurationen visas i checklistan, men är inget krav.</para>
    /// </summary>
    [TableName("CompetitionCourseReview")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionCourseReview
    {
        public int Id { get; set; }
        public int CompetitionId { get; set; }
        public int RegionId { get; set; }
        public string Route { get; set; } = CourseReviewRoute.Krets;
        public string Status { get; set; } = CourseReviewStatus.Inskickad;
        public bool Required { get; set; }
        public string ReviewerKind { get; set; } = CourseReviewerKind.Bangranskare;
        public string Checksum { get; set; } = "";
        public string? Snapshot { get; set; }
        public string? ApprovedCustomTargets { get; set; }
        public string? ArrangerNote { get; set; }
        public DateTime SubmittedAt { get; set; }
        public int SubmittedByMemberId { get; set; }
        public string? SubmittedByName { get; set; }
        public DateTime? DecidedAt { get; set; }
        public int? DecidedByMemberId { get; set; }
        public string? DeciderName { get; set; }
        public string? DeciderRole { get; set; }
        public bool DeciderIsCompetitor { get; set; }
        public string? Comment { get; set; }
        public string? Channel { get; set; }
        public string? LinkSentTo { get; set; }
        public DateTime? SentToForbundetAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        [Ignore] public bool IsApproved => Status == CourseReviewStatus.Godkand;
        [Ignore] public bool IsForbundet => Route == CourseReviewRoute.Forbundet;
    }

    [TableName("CompetitionCourseReviewEvent")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionCourseReviewEvent
    {
        public int Id { get; set; }
        public int ReviewId { get; set; }
        public DateTime At { get; set; }
        public int ByMemberId { get; set; }
        public string? ByName { get; set; }
        public string Kind { get; set; } = "";
        public string? Text { get; set; }
    }

    public static class CourseReviewStatus
    {
        public const string Inskickad = "Inskickad";
        public const string Godkand = "Godkand";
        public const string Atersand = "Atersand";

        public static string Label(string? s, bool forbundet = false) => s switch
        {
            Inskickad => forbundet ? "Skickad till Förbundet" : "Inskickad till kretsen",
            Godkand => forbundet ? "Godkänd av Förbundet" : "Godkänd av kretsen",
            Atersand => forbundet ? "Förbundet begär ändringar" : "Återsänd till arrangören",
            _ => s ?? ""
        };
    }

    public static class CourseReviewRoute
    {
        public const string Krets = "Krets";
        /// <summary>SM och landsdelsmästerskap: underlaget går till Förbundet minst 12 veckor före.</summary>
        public const string Forbundet = "Forbundet";
    }

    public static class CourseReviewerKind
    {
        public const string Bangranskare = "Bangranskare";
        public const string Kretsinstruktor = "Kretsinstruktor";

        public static string Normalize(string? v) =>
            string.Equals(v, Kretsinstruktor, StringComparison.OrdinalIgnoreCase) ? Kretsinstruktor : Bangranskare;

        public static string Label(string? v) => Normalize(v) == Kretsinstruktor ? "Kretsinstruktörerna" : "Bangranskaren";
    }

    public static class CourseReviewEventKind
    {
        public const string Submitted = "Submitted";
        public const string Resubmitted = "Resubmitted";
        public const string Approved = "Approved";
        public const string Returned = "Returned";
        public const string SentToForbundet = "SentToForbundet";
        public const string ForbundetAnswer = "ForbundetAnswer";
    }

    /// <summary>Reglerna som rena funktioner (enhetstestade).</summary>
    public static class CourseReviewRules
    {
        /// <summary>Förbundets framförhållning för SM och landsdelsmästerskap i fält (SHB C.3.5.2.2).</summary>
        public const int ForbundetLeadWeeks = 12;

        /// <summary>Fält eller magnumfält.</summary>
        public static bool IsFieldType(string? competitionType) =>
            string.Equals(competitionType, "Faltskytte", StringComparison.OrdinalIgnoreCase)
            || string.Equals(competitionType, "MagnumFalt", StringComparison.OrdinalIgnoreCase)
            || string.Equals(competitionType, "Magnum Fält", StringComparison.OrdinalIgnoreCase)
            || string.Equals(competitionType, "Fältskytte", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Erbjuds granskning: en fälttävling som inte är klubbintern och som är en kretstävling
        /// eller högre — en bekräftad kategori, eller (för en obekräftad) standardmedaljer.
        /// </summary>
        public static bool Offered(string? competitionType, bool isClubOnly, bool kretsOrAbove, bool awardsStandardMedals) =>
            IsFieldType(competitionType) && !isClubOnly && (kretsOrAbove || awardsStandardMedals);

        /// <summary>SM och landsdelsmästerskap granskas av Förbundet, inte av kretsen.</summary>
        public static string RouteFor(bool isSmOrLandsdel) => isSmOrLandsdel ? CourseReviewRoute.Forbundet : CourseReviewRoute.Krets;

        /// <summary>
        /// Krävs granskningen? Nationell fältskjutning: alltid (SHB). SM/LDM: alltid, men av Förbundet.
        /// En kretstävling: bara om kretsen kräver det och tävlingen är från den dag kravet slogs på.
        /// </summary>
        public static bool Required(bool isNationalOrHigher, bool isSmOrLandsdel, bool kretsRequires, DateTime? since, DateTime? competitionDate)
        {
            if (isSmOrLandsdel || isNationalOrHigher) return true;
            if (!kretsRequires) return false;
            return since == null || competitionDate == null || competitionDate.Value.Date >= since.Value.Date;
        }

        /// <summary>Sista dag att skicka in: Förbundets 12 veckor, annars kretsens framförhållning (null = ingen).</summary>
        public static DateTime? Deadline(DateTime? competitionDate, bool forbundet, int? kretsWeeks)
        {
            if (competitionDate == null) return null;
            if (forbundet) return competitionDate.Value.Date.AddDays(-7 * ForbundetLeadWeeks);
            return kretsWeeks is > 0 ? competitionDate.Value.Date.AddDays(-7 * kretsWeeks.Value) : null;
        }

        public static bool KretsCanAct(CompetitionCourseReview r) => !r.IsForbundet && r.Status == CourseReviewStatus.Inskickad;

        /// <summary>
        /// Har stationsbeskrivningen ändrats sedan inskicket/godkännandet? ⚠️ Avgörs vid LÄSNING mot
        /// tävlingens nuvarande stationConfig, inte genom krokar i skrivvägarna: stationConfig skrivs
        /// av guiden, redigeringsdialogen, konfiguratorn och konfigurationsväljaren, och en krok som
        /// glöms i en av dem hade låtit en ändrad bana fortsätta bära "Godkänd".
        /// </summary>
        public static bool Changed(CompetitionCourseReview r, string currentChecksum) =>
            !string.Equals(r.Checksum, currentChecksum, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Meta-nycklar som inte ändrar vad som skjuts: vilken sparad konfiguration kopian kom ifrån
        /// och länkade stationers källa. Mörker (<c>_morker</c>) och tävlingstyp (<c>_scoringMode</c>)
        /// ändrar däremot skjuttiden och poängen och räknas med.
        /// </summary>
        public static bool IsVolatileKey(string key) =>
            key == "_attachedConfigId" || key.StartsWith("_linkedFrom", StringComparison.Ordinal);

        /// <summary>Kontrollsumma över stationConfig, utan de flyktiga nycklarna. 32 hex-tecken.</summary>
        public static string Checksum(string? stationConfig)
        {
            var text = stationConfig ?? "";
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
                    foreach (var k in o.Select(p => p.Key).Where(IsVolatileKey).ToList()) o.Remove(k);
                    foreach (var p in o) Strip(p.Value);
                    break;
                case System.Text.Json.Nodes.JsonArray a:
                    foreach (var item in a) Strip(item);
                    break;
            }
        }
    }

    /// <summary>
    /// Bangranskningens påminnelser som ren funktion — bara för en granskning som KRÄVS (en frivillig
    /// stämpel påminns inte, samma beslut som resultatgranskningen).
    /// </summary>
    public static class CourseReviewReminders
    {
        public const string KindArranger = "cr-arranger";
        public const string KindKrets = "cr-krets";
        /// <summary>Kretsen: en inskickad bana som legat 5 dagar.</summary>
        public const int KretsDays = 5;

        public record Candidate(int CompetitionId, int RegionId, DateTime Deadline);
        public record Due(string Key, string Kind, int CompetitionId, int RegionId, CompetitionCourseReview? Review);

        /// <param name="notSubmitted">Tävlingar där granskningen krävs, med sista dag, och som inte skickats in.</param>
        public static List<Due> Compute(IEnumerable<Candidate> notSubmitted, IEnumerable<CompetitionCourseReview> pending, DateTime today)
        {
            var list = new List<Due>();
            foreach (var c in notSubmitted)
            {
                var left = (c.Deadline.Date - today.Date).TotalDays;
                // Två veckor före sista dagen, och på sista dagen. Bara det senaste steget som passerats.
                if (left > 14 || left < -7) continue;
                var step = left <= 0 ? 0 : 14;
                list.Add(new Due($"cr-arr{step}-{c.CompetitionId}", KindArranger, c.CompetitionId, c.RegionId, null));
            }
            foreach (var r in pending)
            {
                if (r.IsForbundet || r.Status != CourseReviewStatus.Inskickad) continue;
                if ((today.Date - r.UpdatedAt.Date).TotalDays >= KretsDays)
                    list.Add(new Due($"cr-krets{KretsDays}-{r.Id}-{r.UpdatedAt:yyyyMMdd}", KindKrets, r.CompetitionId, r.RegionId, r));
            }
            return list;
        }
    }
}
