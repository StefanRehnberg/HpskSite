namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Precisionsfamiljen: de grenar som delar seriemodellen (serier om 5 skott, poängsumma) och
    /// därmed <c>CompetitionResultsController</c>, startlistorna och resultatvyerna. THE one place
    /// the per-discipline facts below live.
    ///
    /// Varje fakta här låg tidigare som ett handkopierat switch-uttryck: **skytteklass-egenskapen
    /// fanns i 19 kopior över 6 filer** (MatchApiController ×5, MemberController ×6,
    /// TrainingMatchController ×6, MemberMergeService, RankingSnapshotService) plus 11 i
    /// UserProfile.cshtml, och seriekartan i 2. Att lägga till en gren betydde alltså ~30
    /// switch-uttryck att hitta, och en missad kopia faller **tyst** tillbaka på Precision — vilket
    /// är exakt buggen som beskrivs i `competition-type-implementation`: en gren läste
    /// Precisions handikappindex i stället för sitt eget, och ingenting sa till.
    ///
    /// Samma skäl som <see cref="CompetitionResultTables"/> finns, och samma lärdom som
    /// RoleCatalogService: en karta med "keep the two in sync" i kommentaren är lukten, inte skyddet.
    /// </summary>
    public static class PrecisionFamily
    {
        private sealed class DisciplineFacts
        {
            /// <summary>Medlemsegenskapen som bär skyttens kompetensklass för grenen.</summary>
            public string ShooterClassProperty { get; init; } = "";

            /// <summary>
            /// Antal serier grenen normalt skjuts på. Används som FÖRVAL i tävlingsguiden, aldrig som
            /// en regel — arrangören får ändra. 0 = inget förval (Precision varierar).
            /// </summary>
            public int DefaultSeriesCount { get; init; }
        }

        // Nyckeln är tävlingstypens Id (se Models/CompetitionType.cs).
        private static readonly Dictionary<string, DisciplineFacts> Facts =
            new(StringComparer.OrdinalIgnoreCase)
            {
                ["Precision"] = new() { ShooterClassProperty = "precisionShooterClass", DefaultSeriesCount = 0 },
                ["Milsnabb"] = new() { ShooterClassProperty = "milsnabbShooterClass", DefaultSeriesCount = 12 },
                ["Duell"] = new() { ShooterClassProperty = "duellShooterClass", DefaultSeriesCount = 0 },
                ["NationellHelmatch"] = new() { ShooterClassProperty = "nationellHelmatchShooterClass", DefaultSeriesCount = 12 },
                ["MagnumPrecision"] = new() { ShooterClassProperty = "magnumPrecisionShooterClass", DefaultSeriesCount = 6 },

                // ── Nya 2026-08-26 ──────────────────────────────────────────────────────────────
                // Båda skjuts som 12 serier om 5 skott (60 skott, 600 poäng) på precisionstavla, och
                // ryms därför i den delade seriemodellen utan egen motor.
                //
                // ⚠️ Serieantalet är ett FÖRVAL grundat på det gängse formatet, inte på en SPSF-regel
                // jag kunnat läsa i repot. Backloggen bekräftade (2026-06-30) strängarna respektive
                // halvorna, inte antalet serier per sträng. Arrangören kan ändra i guiden, och
                // stämmer 12 inte för svenskt bruk är det en siffra att rätta här.
                //
                // Standardpistol: 150 s / 20 s / 10 s — tiden växlar mellan strängarna, men varje
                // serie är fortfarande 5 skott poängsatta likadant, så inget i beräkningen skiljer.
                ["Standardpistol"] = new() { ShooterClassProperty = "standardpistolShooterClass", DefaultSeriesCount = 12 },

                // Sportpistol: precisionshalva + duellhalva (snabb). Samma form som
                // NationellHelmatch, som också är en flerdelad match utan egen motor: halvorna är en
                // konvention över serieordningen, inte två datamodeller.
                ["Sportpistol"] = new() { ShooterClassProperty = "sportpistolShooterClass", DefaultSeriesCount = 12 },
            };

        /// <summary>
        /// Medlemsegenskapen som bär skyttens klass för grenen.
        ///
        /// ⚠️ Faller tillbaka på <c>precisionShooterClass</c> för okänd eller tom gren, vilket är
        /// EXAKT det gamla `_ =>`-beteendet och måste förbli så: Springskytte och Fältskytte har
        /// ingen egen klassegenskap och läser precisionens.
        ///
        /// Anropande kod ska fortsätta skydda med <c>member.HasProperty(...)</c> — egenskapen skapas
        /// för hand i Umbracos backoffice, så en nyss tillagd gren har den inte förrän någon gjort
        /// det. Utan skyddet blir en saknad egenskap ett fel i stället för en tom klass.
        /// </summary>
        public static string ShooterClassProperty(string? typeId) =>
            Facts.TryGetValue((typeId ?? "").Trim(), out var f) && f.ShooterClassProperty.Length > 0
                ? f.ShooterClassProperty
                : "precisionShooterClass";

        /// <summary>
        /// Förvalt antal serier i tävlingsguiden; 0 betyder "inget förval, låt arrangören välja".
        /// Bevarar det gamla beteendet: Milsnabb och NationellHelmatch 12, MagnumPrecision 6,
        /// allt annat 0.
        /// </summary>
        public static int DefaultSeriesCount(string? typeId) =>
            Facts.TryGetValue((typeId ?? "").Trim(), out var f) ? f.DefaultSeriesCount : 0;

        /// <summary>
        /// Hör grenen till precisionsfamiljen — alltså delar den seriemodell, resultatendpoints och
        /// startlistor? Falskt för Springskytte och Fältskytte/MagnumFält, som äger sina egna.
        ///
        /// ⚠️ Använd INTE detta som ersättning för fallbacken i <see cref="ShooterClassProperty"/>:
        /// en Fältskytte-tävling läser fortfarande precisionens klassegenskap.
        /// </summary>
        public static bool IsMember(string? typeId) =>
            Facts.ContainsKey((typeId ?? "").Trim());

        /// <summary>Familjens grenar, i registrets ordning. För dropdowns och tester.</summary>
        public static IReadOnlyCollection<string> All => Facts.Keys.ToList();
    }
}
