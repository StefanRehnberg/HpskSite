using NPoco;

namespace HpskSite.Models.Firearms
{
    /// <summary>
    /// Var vapnet är i sin livscykel.
    ///
    /// <para><b>⚠️ <see cref="Planerat"/> är inte ett kantfall.</b> Ett föreningsintyg gäller
    /// oftast ett vapen medlemmen ännu <i>inte</i> äger — man söker licens före köpet. Ett register
    /// som bara rymmer innehav hjälper alltså bara vid förnyelser, alltså minoriteten av
    /// intygen.</para>
    ///
    /// <para><b>⚠️ <see cref="Avvecklat"/> raderas aldrig.</b> Gamla intyg refererar vapnet, och
    /// "antal vapen sedan tidigare" räknar historik.</para>
    /// </summary>
    public static class FirearmAcquisitionStatus
    {
        public const string Planerat = "Planerat";
        public const string Innehas = "Innehas";
        public const string Avvecklat = "Avvecklat";

        public static readonly string[] All = { Planerat, Innehas, Avvecklat };

        public static bool IsValid(string? value) =>
            All.Contains((value ?? "").Trim(), StringComparer.Ordinal);

        public static string Label(string? value) => (value ?? "").Trim() switch
        {
            Planerat => "Planerat (söker licens)",
            Innehas => "Innehas",
            Avvecklat => "Avvecklat",
            _ => value ?? "",
        };
    }

    /// <summary>Klubbvapnets tillgänglighet. Bara meningsfull för <c>ScopeKind = 'Club'</c>.</summary>
    public static class FirearmStatus
    {
        public const string Tillgangligt = "Tillgängligt";
        public const string Utlanat = "Utlånat";
        public const string Service = "Service";
        public const string Utgallrat = "Utgallrat";

        public static readonly string[] All = { Tillgangligt, Utlanat, Service, Utgallrat };

        public static bool IsValid(string? value) =>
            All.Contains((value ?? "").Trim(), StringComparer.Ordinal);
    }

    /// <summary>
    /// De SKYDDADE uppgifterna — vapnets identitet. Serialiseras till JSON och krypteras in i
    /// <see cref="Firearm.EncryptedDetails"/>.
    ///
    /// <para><b>Innehållet är avgränsat till det utfärdaren av ett föreningsintyg behöver</b>
    /// (Stefans beslut 2026-09-02) plus de två fält medlemmen själv har nytta av i sitt eget
    /// register. 551.1:s övriga rutor — patronantal, pipinformation, omladdningsfunktion,
    /// SE-nummer, överlåtaren — är medvetet INTE med: intyget rör dem inte.</para>
    ///
    /// <para><b>⚠️ Förvaringssätt lagras inte.</b> Ingen funktion läser det, ingen intygsrad kräver
    /// det, och det är det mest inbrottskänsliga fält blanketten har. Data vi inte har kan inte
    /// läcka.</para>
    ///
    /// <para><b>⚠️ Lägg aldrig ett fält här som något måste kunna SÖKA, RÄKNA eller SVEPA över.</b>
    /// Krypterat betyder oräkningsbart — det är därför förfallodatumet ligger i en klartextkolumn.</para>
    /// </summary>
    public class FirearmDetails
    {
        // ── Det föreningsintyget behöver ─────────────────────────────────────────────────────────
        public string Fabrikat { get; set; } = "";
        public string Modell { get; set; } = "";

        /// <summary>Kaliber/patronbeteckning, fritext: ".22 LR", "6,5 × 55 mm".</summary>
        public string Kaliber { get; set; } = "";

        /// <summary>Piplängd. Blanketten vill ha centimeter, men fältet är fritext.</summary>
        public string Piplangd { get; set; } = "";

        // ── Medlemmens eget ─────────────────────────────────────────────────────────────────────
        //
        // Ingår inte i ett intyg, men det är de här uppgifterna löftet på /om-pistol-nu handlar om
        // ("licens- och vapenuppgifterna lagras krypterat") — och därmed skälet en medlem litar på
        // registret alls.

        /// <summary>Tillverkningsnummer (serienummer). Alfanumeriskt.</summary>
        public string Tillverkningsnummer { get; set; } = "";

        public string Licensnummer { get; set; } = "";

        /// <summary>Licensens utfärdandedatum. Förfallodatumet ligger i KLARTEXT på raden.</summary>
        public string Licensdatum { get; set; } = "";

        /// <summary>Medlemmens egen anteckning.</summary>
        public string Anteckning { get; set; } = "";

        /// <summary>True när inget skyddat fält är ifyllt — då finns inget att kryptera.</summary>
        public bool IsEmpty =>
            string.IsNullOrWhiteSpace(Fabrikat) && string.IsNullOrWhiteSpace(Modell)
            && string.IsNullOrWhiteSpace(Kaliber) && string.IsNullOrWhiteSpace(Piplangd)
            && string.IsNullOrWhiteSpace(Tillverkningsnummer) && string.IsNullOrWhiteSpace(Licensnummer)
            && string.IsNullOrWhiteSpace(Licensdatum) && string.IsNullOrWhiteSpace(Anteckning);
    }

    /// <summary>
    /// Ett vapen. Klartextkolumnerna är det som får synas eller måste gå att räkna på;
    /// <see cref="EncryptedDetails"/> bär identiteten.
    ///
    /// <para><b>⚠️ SKRIVNINGEN ÄR TVÅSTEGS.</b> AAD:n binder chiffret till <see cref="Id"/>, så
    /// id:t måste finnas innan uppgifterna kan krypteras: infoga raden → läs id:t → kryptera →
    /// uppdatera. <c>FirearmProtector</c> vägrar ett id som inte är satt, så felet blir högljutt i
    /// stället för en rad ingen kan läsa.</para>
    /// </summary>
    [TableName("Firearm")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class Firearm
    {
        public int Id { get; set; }

        public string ScopeKind { get; set; } = string.Empty;
        public int ScopeId { get; set; }

        /// <summary>Det medvetet ofarliga visningsnamnet. Visas på resultat och i lånevapenlistan.</summary>
        public string Alias { get; set; } = string.Empty;

        /// <summary>
        /// Vapengruppens KOD ur <see cref="WeaponClass"/>: A, A_Opt, A_M, A_P, A_G, B, C, R, M, L.
        /// ⚠️ Samma form som <c>MemberActivityEntry.WeaponGroups</c> — lagras klassNAMNET i stället
        /// missar kopplingen mellan "mitt vapen är klass C" och "aktivitet i grupp C", tyst.
        /// </summary>
        public string? WeaponClass { get; set; }

        /// <summary>Ur <c>ForeningsintygDocument.AllaVapentyper</c> — blankettens fem värden.</summary>
        public string? Vapentyp { get; set; }

        public string? AnnanVapentyp { get; set; }

        public string AcquisitionStatus { get; set; } = FirearmAcquisitionStatus.Innehas;

        /// <summary>Klartext, så påminnelsesvepet kan läsa den utan att öppna en enda blob.</summary>
        public DateTime? LicenseExpiresOn { get; set; }

        // ── Klubbvapen ──────────────────────────────────────────────────────────────────────────
        public int? ClubWeaponNumber { get; set; }
        public bool IsLoanable { get; set; }
        public string? Status { get; set; }

        // ── Det skyddade ────────────────────────────────────────────────────────────────────────
        /// <summary>Null = inga skyddade uppgifter har någonsin skrivits.</summary>
        public byte[]? EncryptedDetails { get; set; }

        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        // ── Härlett, inte kolumner ──────────────────────────────────────────────────────────────

        [Ignore]
        public FirearmScope Scope => Enum.TryParse<FirearmOwnerKind>(ScopeKind, out var kind)
            ? new FirearmScope(kind, ScopeId)
            : throw new InvalidOperationException(
                $"Firearm.Id={Id} har okänd ScopeKind '{ScopeKind}'.");

        [Ignore]
        public bool HasProtectedDetails => EncryptedDetails is { Length: > 0 };

        /// <summary>
        /// Dagar till licensen förfaller. Negativt = redan förfallen. Null = inget datum.
        /// </summary>
        [Ignore]
        public int? DaysUntilLicenseExpiry => LicenseExpiresOn.HasValue
            ? (int)(LicenseExpiresOn.Value.Date - DateTime.Today).TotalDays
            : null;

        /// <summary>Vapentypen som den ska stå på ett intyg — fritexten när typen är "Annat".</summary>
        [Ignore]
        public string VapentypDisplay =>
            string.Equals(Vapentyp, "Annat", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(AnnanVapentyp)
                ? AnnanVapentyp!
                : Vapentyp ?? "";

        // ── Relationerna, fyllda av tjänsten ────────────────────────────────────────────────────

        /// <summary>Förbund vapnet används i. Ur <c>ForeningsintygDocument.AllaForbund</c>.</summary>
        [Ignore]
        public List<string> Federations { get; set; } = new();

        /// <summary>Grenar vapnet används i. Kanoniska id:n ur <c>ActivityDiscipline</c>.</summary>
        [Ignore]
        public List<string> Disciplines { get; set; } = new();
    }
}
