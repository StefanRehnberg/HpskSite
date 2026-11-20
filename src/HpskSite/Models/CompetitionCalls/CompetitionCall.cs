using NPoco;

namespace HpskSite.Models.CompetitionCalls
{
    /// <summary>
    /// En utlysning: kretsens fasta tävlingstillfälle som en klubb sedan får arrangera (kretsutlysningen F2).
    ///
    /// <para><b>⚠️ EN RAD, INTE EN TÄVLINGSNOD</b> och inte en vändning av <c>CompetitionApplication</c> (som är klubb → krets,
    /// har datum NOT NULL och ClubId 0 = kretsen själv). Tävlingen skapas först när den utsedda klubben trycker
    /// <i>Skapa tävlingen</i> (F4) och kopplas då via <see cref="CompetitionId"/>.</para>
    ///
    /// <para>Orden i gränssnittet: <b>utlysa · önska · utse</b> — aldrig "ansök" (det är klubb → krets) eller "tilldela".</para>
    /// </summary>
    [TableName("CompetitionCall")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionCall
    {
        public int Id { get; set; }
        public int RegionId { get; set; }
        public int? SeriesId { get; set; }
        public int Year { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string? Discipline { get; set; }
        public string Level { get; set; } = "Krets";
        public string? ChampionshipScope { get; set; }
        /// <summary>Datumet kan saknas: kretsen bestämmer senare, eller klubben föreslår.</summary>
        public DateTime? CompetitionDate { get; set; }
        /// <summary>"Datumet är fastställt – klubben kan inte ändra det." Bara kretsnivå.</summary>
        public bool LockedByKrets { get; set; }
        public DateTime? WishDeadline { get; set; }
        public bool WishesVisibleToClubs { get; set; } = true;
        public string Status { get; set; } = CompetitionCallStatus.Planering;
        public int? AwardedClubId { get; set; }
        public DateTime? AwardedAt { get; set; }
        public int? AwardedByMemberId { get; set; }
        public int? CompetitionId { get; set; }
        public DateTime? CreatingAt { get; set; }
        public int Version { get; set; } = 1;
        public DateTime? DateChangedAt { get; set; }
        public int? DateChangedByMemberId { get; set; }
        public DateTime CreatedAt { get; set; }
        public int? CreatedByMemberId { get; set; }
        public DateTime UpdatedAt { get; set; }

        [Ignore] public bool HasDate => CompetitionDate.HasValue;
    }

    /// <summary>En klubbs önskemål att få arrangera (byggs i F3; tabellen finns sedan F2).</summary>
    [TableName("CompetitionCallWish")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionCallWish
    {
        public int Id { get; set; }
        public int CallId { get; set; }
        public int ClubId { get; set; }
        public DateTime? ProposedDate { get; set; }
        public string? Note { get; set; }
        public string Status { get; set; } = "Aktiv";
        public int? ContactMemberId { get; set; }
        public int? RegisteredByMemberId { get; set; }
        public bool OnBehalf { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    [TableName("CompetitionCallEvent")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CompetitionCallEvent
    {
        public int Id { get; set; }
        public int CallId { get; set; }
        public DateTime At { get; set; }
        /// <summary>NULL = systemet eller en gallrad medlem.</summary>
        public int? ByMemberId { get; set; }
        public string? ByName { get; set; }
        public string Kind { get; set; } = "";
        public string? Text { get; set; }
    }

    /// <summary>Utlysningens läge. Strängarna lagras (CHECK-regeln i databasen) — byt aldrig en befintlig, lägg till.</summary>
    public static class CompetitionCallStatus
    {
        /// <summary>Bara kretsen ser den.</summary>
        public const string Planering = "Planering";
        /// <summary>Klubbarna ser den och kan önska.</summary>
        public const string Utlyst = "Utlyst";
        /// <summary>Kretsen har utsett en klubb.</summary>
        public const string Tilldelad = "Tilldelad";
        /// <summary>Klubben har skapat tävlingen ur utlysningen.</summary>
        public const string Skapad = "Skapad";
        public const string Installd = "Installd";

        public static readonly string[] All = { Planering, Utlyst, Tilldelad, Skapad, Installd };

        public static string Label(string? status) => status switch
        {
            Planering => "Under planering",
            Utlyst => "Utlyst",
            Tilldelad => "Utsedd",
            Skapad => "Tävlingen är skapad",
            Installd => "Inställd",
            _ => status ?? ""
        };
    }

    public static class CompetitionCallEventKind
    {
        public const string Created = "Created";
        public const string Edited = "Edited";
        public const string DateChanged = "DateChanged";
        public const string Published = "Published";
        public const string Withdrawn = "Withdrawn";
        public const string Awarded = "Awarded";
        public const string Unawarded = "Unawarded";
        public const string Cancelled = "Cancelled";
        public const string Wished = "Wished";
        public const string WishWithdrawn = "WishWithdrawn";
        /// <summary>Utseendet är registrerat; Text = hur många mejl som gick iväg.</summary>
        public const string AwardMailed = "AwardMailed";

        public static string Label(string kind) => kind switch
        {
            Wished => "Klubb önskade att arrangera",
            WishWithdrawn => "Klubb ångrade sitt önskemål",
            AwardMailed => "Utseendet aviserades",
            Created => "Utlysningen lades till",
            Edited => "Uppgifterna ändrades",
            DateChanged => "Datumet ändrades",
            Published => "Utlystes – klubbarna kan önska",
            Withdrawn => "Drogs tillbaka till planering",
            Awarded => "Klubb utsedd",
            Unawarded => "Utnämningen togs tillbaka",
            Cancelled => "Ställdes in",
            _ => kind
        };
    }
}
