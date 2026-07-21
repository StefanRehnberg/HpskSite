namespace HpskSite.Models.Staffing
{
    /// <summary>
    /// Scope vocabulary a staff assignment binds to. Superset of the messaging scope
    /// (<see cref="HpskSite.Models.Messaging.MessageScopeType"/>) — adds Patrull/Bana for the
    /// Fältskytte/Springskytte roles. Person/Role are addressing-only (messaging) and never a
    /// StaffAssignment.ScopeType, so they are not listed here.
    /// </summary>
    public static class StaffScopeType
    {
        public const string Skjutlag = "Skjutlag";
        public const string Station = "Station";
        public const string Klass = "Klass";
        public const string Patrull = "Patrull";
        public const string Bana = "Bana";
        public const string All = "All";
    }

    public static class StaffAssignmentStatus
    {
        public const string Planned = "Planned";
        public const string Invited = "Invited";
        public const string Accepted = "Accepted";
        public const string Declined = "Declined";
        public const string Confirmed = "Confirmed";
    }

    /// <summary>
    /// A functionary role definition. Data-driven so adding/renaming a role is a one-line change,
    /// not a schema migration (mirrors BoardRoleDefinitions). The available roles + their default
    /// scope differ by discipline — the "Lägg till funktionär" dialog offers only the roles valid
    /// for the competition's type, and the roster renders only role groups that discipline uses.
    /// </summary>
    public class FunctionaryRole
    {
        public string Key { get; set; } = "";
        public string DisplayName { get; set; } = "";      // singular
        public string PluralName { get; set; } = "";       // plural (falls back to DisplayName)
        public string DefaultScopeType { get; set; } = ""; // StaffScopeType.* or "" (unscoped)
        public bool SupportsTargetRange { get; set; }      // Markör-style tavlor from–to
        public bool SupportsFunctionTitle { get; set; }    // Tävlingsledning-style function title
        public bool IsResponsibleByDefault { get; set; }
        public string Description { get; set; } = "";
        /// <summary>"Vad behövs" — the kit / briefing a person in this role needs (P2 item 7).
        /// Static per-role checklist shown in the roster + add-dialog; data-driven, no schema.</summary>
        public string[] Needs { get; set; } = Array.Empty<string>();

        public string Plural => string.IsNullOrEmpty(PluralName) ? DisplayName : PluralName;
    }

    /// <summary>
    /// The per-discipline role catalog. Cross-discipline roles (Tävlingsledning/Kassa/Sekretariat)
    /// prepend every discipline's list.
    ///
    /// NOTE (spec §5, §8): the Precision-family roles are confirmed; the Springskytte and Fältskytte
    /// role sets are a FIRST PASS pending arrangör sign-off. They are included so the framework is
    /// complete, but treat them as provisional until confirmed against SHB / a real crew.
    /// </summary>
    public static class FunctionaryRoles
    {
        // --- discipline groups ---
        public static readonly string[] PrecisionFamily =
        {
            "Precision", "MagnumPrecision", "Milsnabb", "Duell",
            "NationellHelmatch", "Standardpistol", "Sportpistol"
        };
        public static readonly string[] FaltFamily = { "Faltskytte", "MagnumFalt" };

        // --- cross-discipline (all types) ---
        private static readonly FunctionaryRole[] Cross =
        {
            new() { Key = "tavlingsledning", DisplayName = "Tävlingsledning", PluralName = "Tävlingsledning",
                    DefaultScopeType = StaffScopeType.All, SupportsFunctionTitle = true, IsResponsibleByDefault = true,
                    Description = "Tävlingsledare / Bitr. tävlingsledare / Säkerhetschef / Sekreterare. Kan ges appbehörighet.",
                    Needs = new[] { "TävlingsPM och tidsplan", "Säkerhetsplan", "Kontaktlista funktionärer", "Sanktionsbevis" } },
            new() { Key = "kassa", DisplayName = "Kassa", PluralName = "Kassa",
                    DefaultScopeType = StaffScopeType.All,
                    Needs = new[] { "Kontantkassa / Swish-nummer", "Anmälnings- och avgiftslista", "Kvittoblock" } },
            new() { Key = "sekretariat", DisplayName = "Sekretariat", PluralName = "Sekretariat",
                    DefaultScopeType = StaffScopeType.All,
                    Needs = new[] { "Startlistor", "Resultatinmatning / dator", "Skrivare", "Priser och medaljer" } },
            new() { Key = "materielansvarig", DisplayName = "Materielansvarig", PluralName = "Materielansvariga",
                    DefaultScopeType = StaffScopeType.All, IsResponsibleByDefault = true,
                    Description = "Ansvarar för materiel — beställning, transport och återlämning.",
                    Needs = new[] { "Materiellista / beställningslista", "Transport", "Nyckel till förråd" } },
            new() { Key = "banansvarig", DisplayName = "Banansvarig", PluralName = "Banansvariga",
                    DefaultScopeType = StaffScopeType.All, IsResponsibleByDefault = true,
                    Description = "Ansvarar för att banan byggs, skyltas och besiktas.",
                    Needs = new[] { "Ban-ritning / stationskonfiguration", "Skyltar och avspärrning", "Besiktningsprotokoll" } },
            new() { Key = "sjukvardsansvarig", DisplayName = "Sjukvårdsansvarig", PluralName = "Sjukvårdsansvariga",
                    DefaultScopeType = StaffScopeType.All, IsResponsibleByDefault = true,
                    Description = "Ansvarar för sjukvård och första hjälpen under tävlingen.",
                    Needs = new[] { "Första hjälpen-väska", "Hjärtstartare", "Larmrutin och vägbeskrivning", "Vätska" } },
        };

        // --- Precision-family ---
        private static readonly FunctionaryRole[] Precision =
        {
            new() { Key = "skjutledare", DisplayName = "Skjutledare", PluralName = "Skjutledare",
                    DefaultScopeType = StaffScopeType.Skjutlag, IsResponsibleByDefault = true,
                    Description = "Leder eldlinjen för ett skjutlag.",
                    Needs = new[] { "Skjutledarbehörighet", "Kommandon / ljudanläggning", "Tidtagning", "Säkerhetsgenomgång" } },
            new() { Key = "markor", DisplayName = "Markör", PluralName = "Markörer",
                    DefaultScopeType = StaffScopeType.Skjutlag, SupportsTargetRange = true,
                    Description = "Flera per skjutlag, var och en täcker ett tavelintervall (t.ex. tavlor 1–8).",
                    Needs = new[] { "Markeringsutrustning", "Kikare", "Protokoll och penna", "Tavlor / klister" } },
        };

        // --- Springskytte (first pass) ---
        private static readonly FunctionaryRole[] Springskytte =
        {
            new() { Key = "startledare", DisplayName = "Startledare", PluralName = "Startledare",
                    DefaultScopeType = StaffScopeType.Klass, IsResponsibleByDefault = true,
                    Description = "Sköter startlinjen.",
                    Needs = new[] { "Startlista", "Nummervästar", "Kommunikationsradio" } },
            new() { Key = "tidtagare", DisplayName = "Tidtagare", PluralName = "Tidtagare",
                    DefaultScopeType = StaffScopeType.Klass, Description = "Registrerar sluttid.",
                    Needs = new[] { "Tidtagningsutrustning (löpande klocka)", "Reservtidtagning", "Protokoll" } },
            new() { Key = "bomkontrollant", DisplayName = "Bomkontrollant", PluralName = "Bomkontrollanter",
                    DefaultScopeType = StaffScopeType.Klass, Description = "Kontrollerar träff/bom på figurerna.",
                    Needs = new[] { "Kikare", "Protokoll och penna" } },
            new() { Key = "varvraknare", DisplayName = "Varvräknare", PluralName = "Varvräknare",
                    DefaultScopeType = StaffScopeType.Klass, Description = "Räknar varv.",
                    Needs = new[] { "Varvräknarblad", "Penna" } },
            new() { Key = "maldomare", DisplayName = "Måldomare", PluralName = "Måldomare",
                    DefaultScopeType = StaffScopeType.All, Description = "Domare vid mållinjen.",
                    Needs = new[] { "SHB / regelverk", "Mållinjemarkering", "Protokoll" } },
        };

        // --- Fältskytte / MagnumFält (first pass) ---
        private static readonly FunctionaryRole[] Falt =
        {
            new() { Key = "stationschef", DisplayName = "Stationschef", PluralName = "Stationschefer",
                    DefaultScopeType = StaffScopeType.Station, IsResponsibleByDefault = true,
                    Description = "Ansvarar för en station (tilldelas idag via Stationer-fliken; konvergerar hit).",
                    Needs = new[] { "Stationskort / QR", "Tidur", "Figurer och markering", "Säkerhetsvinklar" } },
            new() { Key = "stationsmarkor", DisplayName = "Stationsmarkör", PluralName = "Stationsmarkörer",
                    DefaultScopeType = StaffScopeType.Station, SupportsTargetRange = true,
                    Description = "Flera per station vid behov; markerar figurer.",
                    Needs = new[] { "Markeringsutrustning", "Protokoll", "Skärmar" } },
            new() { Key = "startledare", DisplayName = "Startledare (utsläpp)", PluralName = "Startledare (utsläpp)",
                    DefaultScopeType = StaffScopeType.All, Description = "Släpper ut patruller från starten.",
                    Needs = new[] { "Patrullista", "Utsläppsschema", "Kommunikationsradio" } },
            new() { Key = "patrulledare", DisplayName = "Patrulledare", PluralName = "Patrulledare",
                    DefaultScopeType = StaffScopeType.Patrull, Description = "Leder en patrull runt banan.",
                    Needs = new[] { "Patrullkort", "Poängkort", "Penna" } },
            new() { Key = "bandomare", DisplayName = "Bandomare", PluralName = "Bandomare",
                    DefaultScopeType = StaffScopeType.Bana, Description = "Avgör träff/tvister.",
                    Needs = new[] { "SHB / regelverk", "Måttband", "Protokoll" } },
        };

        /// <summary>Ordered role list for a competition type: cross-discipline first, then the discipline set.</summary>
        public static IReadOnlyList<FunctionaryRole> ForDiscipline(string? competitionType)
        {
            var list = new List<FunctionaryRole>(Cross);
            var t = competitionType ?? "";
            if (Array.Exists(FaltFamily, x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
                list.AddRange(Falt);
            else if (string.Equals(t, "Springskytte", StringComparison.OrdinalIgnoreCase))
                list.AddRange(Springskytte);
            else // Precision-family + unknown default to the confirmed precision set
                list.AddRange(Precision);
            return list;
        }

        /// <summary>Resolve a single role for a stored assignment (by key within the comp's discipline).</summary>
        public static FunctionaryRole? Resolve(string? competitionType, string? roleKey)
        {
            if (string.IsNullOrWhiteSpace(roleKey)) return null;
            foreach (var r in ForDiscipline(competitionType))
                if (string.Equals(r.Key, roleKey, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }
    }
}
