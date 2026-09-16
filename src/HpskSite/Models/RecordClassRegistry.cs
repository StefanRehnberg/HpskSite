using System.Text.Json;
using System.Text.Json.Serialization;

namespace HpskSite.Models
{
    /// <summary>
    /// Klass- och grenkatalogen för rekord (<see cref="CompetitionRecord"/>) och mästartitlar
    /// (<see cref="CompetitionChampion"/>). THE one place the per-gren facts below live:
    /// vilka klasser som finns, hur många serier grenen skjuts på, vilken ENHET resultatet
    /// mäts i, och — viktigast — om grenen kan ha rekord, mästare eller båda.
    ///
    /// ⚠️ Rekord och mästare är INTE samma sak, och skillnaden är en regel, inte en smaksak:
    ///   • En MÄSTARE koras varje år. Det räcker att tävlingen är ett mästerskap.
    ///   • Ett REKORD står sig tills någon slår det, och kräver därför att grenen fortlöpande
    ///     skjuts i exakt samma form och under samma förutsättningar. Fältskytte gör inte det
    ///     — banan är ny varje gång, avstånd och figurer varierar — så fältskytte kan ha
    ///     mästare men aldrig rekord. Det är vad <see cref="RecordDisciplineDefinition.SupportsRecords"/>
    ///     bär, och båda tjänsterna grindar på det.
    ///
    /// ⚠️ Vyerna renderar sina dropdowns och tabellrader ur <see cref="RecordCombosJson"/> /
    ///     <see cref="ChampionCombosJson"/>. Handkopiera ALDRIG klasslistorna till en .cshtml
    ///     igen: det var precis så disciplin-dropdownen kom att erbjuda Standardpistol och
    ///     Sportpistol medan registret inte kände till dem — användaren fick en tom klasslista
    ///     och ett nekat sparande, utan att något sa varför.
    /// </summary>
    public static class RecordClassRegistry
    {
        // ── Klasslistor ──────────────────────────────────────────────────────────────
        // A Opt (vapengrupp A optisk, se ShootingClasses) finns överallt där A finns.
        // Lagklasserna slår ihop veteranklasserna till ett gemensamt C Vet.

        private static readonly string[] PrecisionFamilyIndividual =
            { "A", "A_Opt", "B", "C", "C_Dam", "C_Jun", "C_VetY", "C_VetA" };

        private static readonly string[] PrecisionFamilyTeam =
            { "A", "A_Opt", "B", "C", "C_Dam", "C_Jun", "C_Vet" };

        // Milsnabb och fältskytte skjuts även i vapengrupp R (grovkalibrig revolver).
        private static readonly string[] WithRevolverIndividual =
            { "A", "A_Opt", "B", "C", "C_Dam", "C_Jun", "C_VetY", "C_VetA", "R" };

        private static readonly string[] WithRevolverTeam =
            { "A", "A_Opt", "B", "C", "C_Dam", "C_Jun", "C_Vet", "R" };

        // M1–M9 enligt vapenklasskatalogen i ShootingClasses (M8 Revolver 38-45, M9 Vapenklass A).
        private static readonly string[] MagnumClasses =
            { "M1", "M2", "M3", "M4", "M5", "M6", "M7", "M8", "M9" };

        // ── Grenkatalogen ────────────────────────────────────────────────────────────

        private static readonly List<RecordDisciplineDefinition> Disciplines = new()
        {
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.Precision,
                DisplayName = "Precisionsskjutning",
                Icon = "bi-bullseye",
                IndividualSeries = 10,
                TeamSeries = 7,
                IndividualClasses = PrecisionFamilyIndividual,
                TeamClasses = PrecisionFamilyTeam
            },
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.Standardpistol,
                DisplayName = "Standardpistol",
                Icon = "bi-stopwatch",
                // 12 serier om 5 skott (150 s / 20 s / 10 s). Samma seriemodell som
                // precisionsfamiljen i övrigt — se PrecisionFamily.DefaultSeriesCount.
                IndividualSeries = 12,
                TeamSeries = 12,
                IndividualClasses = PrecisionFamilyIndividual,
                TeamClasses = PrecisionFamilyTeam
            },
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.Sportpistol,
                DisplayName = "Sportpistol",
                Icon = "bi-stopwatch",
                // Precisionshalva + duellhalva, 12 serier om 5 skott totalt.
                IndividualSeries = 12,
                TeamSeries = 12,
                IndividualClasses = PrecisionFamilyIndividual,
                TeamClasses = PrecisionFamilyTeam
            },
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.MagnumPrecision,
                DisplayName = "Magnumprecision",
                Icon = "bi-bullseye",
                IndividualSeries = 6,
                TeamSeries = 6,
                IndividualClasses = MagnumClasses,
                TeamClasses = MagnumClasses
            },
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.Milsnabb,
                DisplayName = "Militär snabbmatch",
                Icon = "bi-stopwatch",
                IndividualSeries = 12,
                TeamSeries = 12,
                IndividualClasses = WithRevolverIndividual,
                TeamClasses = WithRevolverTeam
            },
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.Faltskytte,
                DisplayName = "Fältskytte",
                Icon = "bi-signpost-split",
                // ⚠️ Mästare men INGA rekord: banan byggs om varje tävling, så två resultat
                // är aldrig skjutna under samma förutsättningar. Se klassens doc-kommentar.
                SupportsRecords = false,
                SupportsChampions = true,
                // Ingen seriemodell och inget fast tak — antalet stationer varierar.
                // Resultatet är träff, med figurer som särskiljare.
                IndividualSeries = 0,
                TeamSeries = 0,
                ScoreLabel = "Antal träff",
                ScoreUnit = "träff",
                ScoreUnitPlural = "träff",
                SecondaryLabel = "figurer",
                OpenScoreCap = 120,
                IndividualClasses = WithRevolverIndividual,
                TeamClasses = WithRevolverTeam
            },
            new RecordDisciplineDefinition
            {
                Id = RecordDisciplines.Poangfalt,
                DisplayName = "Poängfält",
                Icon = "bi-signpost-split",
                // Samma skäl som fältskyttet: banan är ny varje gång, alltså mästare men inga rekord.
                SupportsRecords = false,
                SupportsChampions = true,
                IndividualSeries = 0,
                TeamSeries = 0,
                // ⚠️ Poängfältet har ETT tal — poäng — och ingen särskiljare. Normalfältet har två
                // (träff med figurer som särskiljare). Det är hela skälet att de är skilda grenar
                // här: ett tal ur den ena under den andras etikett är en tyst lögn.
                ScoreLabel = "Poäng",
                ScoreUnit = "poäng",
                ScoreUnitPlural = "poäng",
                SecondaryLabel = null,
                OpenScoreCap = 240,
                IndividualClasses = WithRevolverIndividual,
                TeamClasses = WithRevolverTeam
            }
        };

        private static readonly Dictionary<string, RecordDisciplineDefinition> ById =
            Disciplines.ToDictionary(d => d.Id, StringComparer.OrdinalIgnoreCase);

        public static RecordDisciplineDefinition? Get(string? discipline)
            => discipline != null && ById.TryGetValue(discipline.Trim(), out var d) ? d : null;

        /// <summary>Grenarna i visningsordning. Filtrera med <c>SupportsRecords</c>/<c>SupportsChampions</c>.</summary>
        public static IReadOnlyList<RecordDisciplineDefinition> All => Disciplines;

        public static bool SupportsRecords(string? discipline) => Get(discipline)?.SupportsRecords ?? false;

        public static bool SupportsChampions(string? discipline) => Get(discipline)?.SupportsChampions ?? false;

        public static IReadOnlyList<string> GetClasses(string discipline, string recordType)
        {
            var d = Get(discipline);
            if (d == null) return Array.Empty<string>();
            return recordType == RecordTypes.Team ? d.TeamClasses : d.IndividualClasses;
        }

        public static int GetSeriesCount(string discipline, string recordType)
        {
            var d = Get(discipline);
            if (d == null) return 0;
            return recordType == RecordTypes.Team ? d.TeamSeries : d.IndividualSeries;
        }

        /// <summary>
        /// Högsta möjliga resultat = 50 × antal serier (5 skott × 10p × serier).
        /// <b>0 betyder "inget fast tak"</b> — grenen har ingen seriemodell (fältskytte).
        /// Använd <see cref="GetScoreCeiling"/> när du behöver en siffra att validera mot.
        /// </summary>
        public static int GetMaxScore(string discipline, string recordType)
            => 50 * GetSeriesCount(discipline, recordType);

        /// <summary>
        /// Den övre gräns ett inmatat resultat valideras mot. Lika med <see cref="GetMaxScore"/>
        /// när grenen har en seriemodell, annars grenens öppna tak (<c>OpenScoreCap</c>).
        /// </summary>
        public static int GetScoreCeiling(string discipline, string recordType)
        {
            var max = GetMaxScore(discipline, recordType);
            if (max > 0) return max;
            return Get(discipline)?.OpenScoreCap ?? 0;
        }

        /// <summary>Enheten resultatet mäts i — "poäng" eller "träff". Måste följa med talet överallt.</summary>
        public static string GetScoreUnit(string? discipline) => Get(discipline)?.ScoreUnitPlural ?? "poäng";

        /// <summary>Etikett för det andra talet ("figurer"), eller null när grenen bara har ett.</summary>
        public static string? GetSecondaryLabel(string? discipline) => Get(discipline)?.SecondaryLabel;

        /// <summary>Fältetiketten för resultatet — "Total poäng" / "Antal träff" / "Poäng".</summary>
        public static string GetScoreLabel(string? discipline) => Get(discipline)?.ScoreLabel ?? "Total poäng";

        /// <summary>
        /// Resultatet som en färdig sträng med enheten på — "285 / 300" för en seriegren,
        /// "42 träff (25 figurer)" för fältskytte. Vyerna ska visa DENNA, aldrig sätta ihop
        /// tal och etikett själva: ett tal ur databasen med en etikett ur nuläget är en tyst lögn.
        /// </summary>
        public static string FormatScore(string? discipline, string recordType, int totalScore, int? secondaryScore)
        {
            var max = GetMaxScore(discipline ?? "", recordType);
            if (max > 0) return $"{totalScore} / {max}";

            var text = $"{totalScore} {GetScoreUnit(discipline)}";
            var secondaryLabel = GetSecondaryLabel(discipline);
            if (secondaryLabel != null && secondaryScore.HasValue)
                text += $" ({secondaryScore.Value} {secondaryLabel})";
            return text;
        }

        public static bool IsValid(string discipline, string recordType, string classCode)
        {
            var classes = GetClasses(discipline, recordType);
            return classes.Any(c => string.Equals(c, classCode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Visningsnamn för en klasskod (t.ex. C_VetY → "C Vet Y", A_Opt → "A Opt").
        /// </summary>
        public static string GetClassDisplayName(string classCode) => classCode switch
        {
            "A_Opt" => "A Opt",
            "C_Dam" => "C Dam",
            "C_Jun" => "C Jun",
            "C_VetY" => "C Vet Y",
            "C_VetA" => "C Vet Ä",
            "C_Vet" => "C Vet",
            _ => classCode
        };

        /// <summary>
        /// Alla gren + typ-kombinationer som kan bära REKORD, i visningsordning.
        /// </summary>
        public static IEnumerable<(string Discipline, string RecordType)> RecordCombos()
            => CombosFor(d => d.SupportsRecords);

        /// <summary>
        /// Alla gren + typ-kombinationer som kan bära MÄSTARTITLAR, i visningsordning.
        /// </summary>
        public static IEnumerable<(string Discipline, string RecordType)> ChampionCombos()
            => CombosFor(d => d.SupportsChampions);

        private static IEnumerable<(string Discipline, string RecordType)> CombosFor(
            Func<RecordDisciplineDefinition, bool> predicate)
        {
            foreach (var d in Disciplines.Where(predicate))
            {
                yield return (d.Id, RecordTypes.Individual);
                yield return (d.Id, RecordTypes.Team);
            }
        }

        // ── JSON för vyerna ──────────────────────────────────────────────────────────

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never
        };

        /// <summary>Grenkatalogen för rekordytorna, som JSON. Åäö escapas till \uXXXX av serialiseraren.</summary>
        public static string RecordCombosJson => Serialize(d => d.SupportsRecords);

        /// <summary>Grenkatalogen för mästarytorna, som JSON.</summary>
        public static string ChampionCombosJson => Serialize(d => d.SupportsChampions);

        private static string Serialize(Func<RecordDisciplineDefinition, bool> predicate)
        {
            var payload = Disciplines.Where(predicate).Select(d => new
            {
                id = d.Id,
                label = d.DisplayName,
                icon = d.Icon,
                scoreLabel = d.ScoreLabel,
                scoreUnit = d.ScoreUnitPlural,
                secondaryLabel = d.SecondaryLabel,
                types = new[] { RecordTypes.Individual, RecordTypes.Team }.Select(t => new
                {
                    type = t,
                    series = GetSeriesCount(d.Id, t),
                    max = GetMaxScore(d.Id, t),
                    ceiling = GetScoreCeiling(d.Id, t),
                    classes = GetClasses(d.Id, t)
                        .Select(c => new { code = c, label = GetClassDisplayName(c) })
                        .ToArray()
                }).ToArray()
            });
            return JsonSerializer.Serialize(payload, JsonOptions);
        }
    }

    /// <summary>
    /// En gren i rekord-/mästarkatalogen. Se <see cref="RecordClassRegistry"/> för varför
    /// <see cref="SupportsRecords"/> och <see cref="SupportsChampions"/> är två olika svar.
    /// </summary>
    public sealed class RecordDisciplineDefinition
    {
        public required string Id { get; init; }

        public required string DisplayName { get; init; }

        /// <summary>Bootstrap-ikonklass för rubrikraden.</summary>
        public string Icon { get; init; } = "bi-bullseye";

        /// <summary>Kan grenen bära rekord? Falskt när banan inte är densamma två gånger.</summary>
        public bool SupportsRecords { get; init; } = true;

        /// <summary>Kan grenen bära mästartitlar? Sant för alla grenar vi känner till.</summary>
        public bool SupportsChampions { get; init; } = true;

        /// <summary>Antal serier individuellt. 0 = grenen har ingen seriemodell.</summary>
        public int IndividualSeries { get; init; }

        /// <summary>Antal serier för lag. 0 = grenen har ingen seriemodell.</summary>
        public int TeamSeries { get; init; }

        /// <summary>Fältetiketten för resultatet i inmatningsformulären.</summary>
        public string ScoreLabel { get; init; } = "Total poäng";

        /// <summary>Enheten i singular, för hjälptexter.</summary>
        public string ScoreUnit { get; init; } = "poäng";

        /// <summary>Enheten som skrivs efter talet.</summary>
        public string ScoreUnitPlural { get; init; } = "poäng";

        /// <summary>Etikett för grenens andra tal, t.ex. "figurer". Null när grenen bara har ett.</summary>
        public string? SecondaryLabel { get; init; }

        /// <summary>Övre valideringsgräns när grenen saknar seriemodell (inget fast tak finns).</summary>
        public int OpenScoreCap { get; init; }

        public required IReadOnlyList<string> IndividualClasses { get; init; }

        public required IReadOnlyList<string> TeamClasses { get; init; }
    }
}
