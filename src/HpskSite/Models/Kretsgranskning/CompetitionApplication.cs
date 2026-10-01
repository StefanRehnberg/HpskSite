using NPoco;
using HpskSite.CompetitionTypes.Common;

namespace HpskSite.Models.Kretsgranskning
{
    /// <summary>
    /// En ansökan om att få arrangera en tävling (kretsgranskning fas 4).
    ///
    /// <para><b>⚠️ EN RAD, INTE EN TÄVLINGSNOD.</b> En tävlingsnod syns på ett tjugotal ställen
    /// (tävlingssidan, tre tävlingslistor, utvalda tävlingar, statistik, serier, sök, adresserna);
    /// var och en hade behövt lära sig att en ansökan inte är en tävling, och den som glömdes hade
    /// visat en avslagen ansökan publikt. Tävlingen skapas först när arrangören väljer det, efter
    /// kretsens ja, och kopplas via <see cref="CompetitionId"/>.</para>
    /// </summary>
    [TableName("CompetitionApplication")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionApplication
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        /// <summary>0 = kretsen arrangerar själv.</summary>
        public int ClubId { get; set; }
        public string Level { get; set; } = "";
        public string Discipline { get; set; } = "";
        public string Name { get; set; } = "";
        public DateTime CompetitionDate { get; set; }
        public DateTime? EndDate { get; set; }
        public DateTime? ReserveDate { get; set; }
        public int? RangeId { get; set; }
        public string? Place { get; set; }
        public int? ContactMemberId { get; set; }
        public string? ContactName { get; set; }
        public string? ContactEmail { get; set; }
        public string? ContactPhone { get; set; }
        public string? Classes { get; set; }
        public string? Note { get; set; }
        public string Status { get; set; } = CompetitionApplicationStatus.Utkast;
        public string? KretsOpinion { get; set; }
        public string? KretsOpinionText { get; set; }
        public string? CompletionRequest { get; set; }
        public string? CompletionReply { get; set; }
        public string? DecisionText { get; set; }
        public DateTime? DecidedAt { get; set; }
        public int? DecidedByMemberId { get; set; }
        public DateTime? GrantedDate { get; set; }
        public DateTime? ForbundetDecisionDate { get; set; }
        public DateTime? SentToForbundetAt { get; set; }
        public int? CompetitionId { get; set; }
        public string? Channel { get; set; }
        public string? LinkSentTo { get; set; }
        public string? DeciderName { get; set; }
        public string? DeciderRole { get; set; }
        public DateTime CreatedAt { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime? SubmittedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        /// <summary>Kräver Förbundets medgivande (via kretsen) — nationell och landsdel.</summary>
        [Ignore] public bool NeedsForbundet => CompetitionApplicationRules.NeedsForbundet(Level);

        /// <summary>Kretsen arrangerar själv.</summary>
        [Ignore] public bool IsRegionOwn => ClubId <= 0;

        /// <summary>Datumet tävlingen faktiskt får: Förbundets beviljade datum om det ändrats.</summary>
        [Ignore] public DateTime EffectiveDate => GrantedDate ?? CompetitionDate;
    }

    [TableName("CompetitionApplicationEvent")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionApplicationEvent
    {
        public int Id { get; set; }
        public int ApplicationId { get; set; }
        public DateTime At { get; set; }
        public int ByMemberId { get; set; }
        public string? ByName { get; set; }
        public string Kind { get; set; } = "";
        public string? Text { get; set; }
    }

    [TableName("RegionApplicationDeadline")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RegionApplicationDeadline
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public int Year { get; set; }
        public string Level { get; set; } = "";
        public DateTime LastDate { get; set; }
    }

    [TableName("RegionCalendarSettings")]
    [PrimaryKey("RegionId", AutoIncrement = false)]
    public class RegionCalendarSettings
    {
        public int RegionId { get; set; }
        public int? FieldPrereqWeeks { get; set; }
        public string? CourseReviewer { get; set; }
        public string? NeighbourOverrides { get; set; }
        public DateTime UpdatedAt { get; set; }
        public int UpdatedByMemberId { get; set; }
    }

    /// <summary>⚠️ Lagras som text. Byt aldrig en befintlig sträng — lägg till.</summary>
    public static class CompetitionApplicationStatus
    {
        public const string Utkast = "Utkast";
        public const string Inskickad = "Inskickad";
        /// <summary>Kretsen har bett klubben komplettera. Ligger kvar som öppen hos kretsen.</summary>
        public const string Komplettering = "Komplettering";
        /// <summary>Kretsens yttrande klart och mejlat till Förbundet; kretsen registrerar beslutet.</summary>
        public const string HosForbundet = "HosForbundet";
        public const string Beviljad = "Beviljad";
        public const string Avslagen = "Avslagen";
        /// <summary>Klubben drog tillbaka ansökan. EGEN status, inte Avslagen — ingen prövade den.</summary>
        public const string Aterkallad = "Aterkallad";

        public static readonly string[] All = { Utkast, Inskickad, Komplettering, HosForbundet, Beviljad, Avslagen, Aterkallad };

        /// <summary>Väntar på kretsen (inkorgen).</summary>
        public static readonly string[] AtKrets = { Inskickad, Komplettering };

        /// <summary>Visas i kalendern som preliminär.</summary>
        public static readonly string[] Preliminary = { Inskickad, Komplettering, HosForbundet };

        public static string Label(string? s) => s switch
        {
            Utkast => "Utkast",
            Inskickad => "Inskickad",
            Komplettering => "Komplettering begärd",
            HosForbundet => "Hos Förbundet",
            Beviljad => "Godkänd",
            Avslagen => "Avslagen",
            Aterkallad => "Återkallad",
            _ => s ?? ""
        };
    }

    public static class CompetitionApplicationOpinion
    {
        public const string Tillstyrker = "Tillstyrker";
        public const string Avstyrker = "Avstyrker";
        public static bool IsValid(string? v) => v == Tillstyrker || v == Avstyrker;
    }

    public static class CompetitionApplicationEventKind
    {
        public const string Created = "Created";
        public const string Edited = "Edited";
        public const string Submitted = "Submitted";
        public const string CompletionRequested = "CompletionRequested";
        public const string Replied = "Replied";
        public const string Opinion = "Opinion";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
        public const string SentToForbundet = "SentToForbundet";
        public const string ForbundetDecision = "ForbundetDecision";
        public const string Withdrawn = "Withdrawn";
        public const string Linked = "Linked";
    }

    /// <summary>
    /// Ansökans regler som rena funktioner — ingen databas, inget "nu" utom där det skickas in.
    /// Tjänsten frågar här; testerna prövar här.
    /// </summary>
    public static class CompetitionApplicationRules
    {
        /// <summary>
        /// Nivåer som söks. En föreningstävling söks inte — den har per definition inget
        /// godkännande (SHB C.3.1). En rikstävling är Förbundets uppdrag, inte en ansökan.
        /// </summary>
        public static readonly string[] ApplicableLevels =
            { CompetitionLevel.Krets, CompetitionLevel.Landsdel, CompetitionLevel.Nationell };

        public static bool IsApplicableLevel(string? level) => ApplicableLevels.Contains(level ?? "");

        /// <summary>Landsdel och nationell beslutas av Förbundet via kretsen.</summary>
        public static bool NeedsForbundet(string? level) =>
            level == CompetitionLevel.Landsdel || level == CompetitionLevel.Nationell;

        /// <summary>Får klubben ändra ansökans innehåll? Före beslut, och när kretsen bett om mer.</summary>
        public static bool ClubCanEdit(string status) =>
            status == CompetitionApplicationStatus.Utkast
            || status == CompetitionApplicationStatus.Inskickad
            || status == CompetitionApplicationStatus.Komplettering;

        public static bool ClubCanSubmit(string status) => status == CompetitionApplicationStatus.Utkast;

        public static bool ClubCanWithdraw(string status) =>
            status != CompetitionApplicationStatus.Avslagen && status != CompetitionApplicationStatus.Aterkallad;

        /// <summary>Kan kretsen besluta, yttra sig eller begära komplettering?</summary>
        public static bool KretsCanAct(string status) => CompetitionApplicationStatus.AtKrets.Contains(status);

        /// <summary>
        /// Får arrangören skapa tävlingen ur ansökan? Efter kretsens ja — och för nationell och
        /// landsdel redan efter kretsens tillstyrkan, så att inbjudan kan börja medan Förbundets
        /// besked dröjer. En avstyrkt ansökan får det inte.
        /// </summary>
        public static bool CanCreateCompetition(CompetitionApplication a) =>
            a.CompetitionId is null or <= 0 && (
                a.Status == CompetitionApplicationStatus.Beviljad
                || (a.Status == CompetitionApplicationStatus.HosForbundet && a.KretsOpinion == CompetitionApplicationOpinion.Tillstyrker));

        /// <summary>Validering av ansökans innehåll. Null när den duger.</summary>
        public static string? ValidateContent(CompetitionApplication a, DateTime today)
        {
            if (string.IsNullOrWhiteSpace(a.Name)) return "Ange tävlingens namn.";
            if (!IsApplicableLevel(a.Level)) return "Välj kretstävling, landsdelstävling eller nationell tävling. En föreningstävling söks inte.";
            if (!CompetitionTypes.All.Any(t => t.Id == a.Discipline)) return "Välj gren.";
            if (a.CompetitionDate == default) return "Ange datum.";
            if (a.CompetitionDate.Date < today.Date) return "Datumet har redan passerat.";
            if (a.EndDate.HasValue && a.EndDate.Value.Date < a.CompetitionDate.Date) return "Slutdatumet ligger före startdatumet.";
            if (a.ReserveDate.HasValue && a.ReserveDate.Value.Date < today.Date) return "Reservdatumet har redan passerat.";
            if (a.Name.Length > 200) return "Namnet är för långt.";
            return null;
        }

        /// <summary>
        /// Förbundets gräns: 1 oktober året före tävlingsåret enligt SHB och handboken, men
        /// Förbundets instruktion säger 30 september — vi visar den tidigare (designbeslut).
        /// </summary>
        public static DateTime ForbundetDeadline(int competitionYear) => new(competitionYear - 1, 9, 30);

        /// <summary>Är ansökan sen i förhållande till en sista dag? Inskickningsdagen räknas med.</summary>
        public static bool IsLate(DateTime submittedOrToday, DateTime lastDate) => submittedOrToday.Date > lastDate.Date;
    }
}
