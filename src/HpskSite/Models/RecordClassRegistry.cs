namespace HpskSite.Models
{
    /// <summary>
    /// Hardcoded class lists per (Discipline, RecordType). Source of truth for what
    /// records can be entered for a given combination, the series count, and the max
    /// possible score. Used by the controller for validation and by the UI to populate
    /// the class dropdown after the user selects a discipline + record type.
    ///
    /// Series counts and class lists are SPSF rules (per the user's spec):
    ///
    ///   Precision         Individual: 10 series  Classes: A, B, C, C_Dam, C_Jun, C_VetY, C_VetA
    ///                     Team:        7 series  Classes: A, B, C, C_Dam, C_Jun, C_Vet
    ///   MagnumPrecision   Individual:  6 series  Classes: M1..M7
    ///                     Team:        6 series  Classes: M1..M7
    ///   Milsnabb          Individual: 12 series  Classes: A, B, C, C_Dam, C_Jun, C_VetY, C_VetA, R
    ///                     Team:       12 series  Classes: A, B, C, C_Dam, C_Jun, C_Vet, R
    ///
    /// Score validation: total score must be in [0, 50 * SeriesCount]. (5 shots × 10p × series.)
    /// </summary>
    public static class RecordClassRegistry
    {
        private static readonly string[] PrecisionIndividual = { "A", "B", "C", "C_Dam", "C_Jun", "C_VetY", "C_VetA" };
        private static readonly string[] PrecisionTeam = { "A", "B", "C", "C_Dam", "C_Jun", "C_Vet" };
        private static readonly string[] MagnumIndividual = { "M1", "M2", "M3", "M4", "M5", "M6", "M7" };
        private static readonly string[] MagnumTeam = { "M1", "M2", "M3", "M4", "M5", "M6", "M7" };
        private static readonly string[] MilsnabbIndividual = { "A", "B", "C", "C_Dam", "C_Jun", "C_VetY", "C_VetA", "R" };
        private static readonly string[] MilsnabbTeam = { "A", "B", "C", "C_Dam", "C_Jun", "C_Vet", "R" };

        public static IReadOnlyList<string> GetClasses(string discipline, string recordType)
        {
            return (discipline, recordType) switch
            {
                (RecordDisciplines.Precision, RecordTypes.Individual) => PrecisionIndividual,
                (RecordDisciplines.Precision, RecordTypes.Team) => PrecisionTeam,
                (RecordDisciplines.MagnumPrecision, RecordTypes.Individual) => MagnumIndividual,
                (RecordDisciplines.MagnumPrecision, RecordTypes.Team) => MagnumTeam,
                (RecordDisciplines.Milsnabb, RecordTypes.Individual) => MilsnabbIndividual,
                (RecordDisciplines.Milsnabb, RecordTypes.Team) => MilsnabbTeam,
                _ => Array.Empty<string>()
            };
        }

        public static int GetSeriesCount(string discipline, string recordType)
        {
            return (discipline, recordType) switch
            {
                (RecordDisciplines.Precision, RecordTypes.Individual) => 10,
                (RecordDisciplines.Precision, RecordTypes.Team) => 7,
                (RecordDisciplines.MagnumPrecision, _) => 6,
                (RecordDisciplines.Milsnabb, _) => 12,
                _ => 0
            };
        }

        /// <summary>Max possible score = 50 × series count (5 shots × 10p × series).</summary>
        public static int GetMaxScore(string discipline, string recordType)
            => 50 * GetSeriesCount(discipline, recordType);

        public static bool IsValid(string discipline, string recordType, string classCode)
        {
            var classes = GetClasses(discipline, recordType);
            return classes.Any(c => string.Equals(c, classCode, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Display label for a class code (e.g. C_VetY → "C Vet Y", C_Dam → "C Dam").
        /// </summary>
        public static string GetClassDisplayName(string classCode) => classCode switch
        {
            "C_Dam" => "C Dam",
            "C_Jun" => "C Jun",
            "C_VetY" => "C Vet Y",
            "C_VetA" => "C Vet Ä",
            "C_Vet" => "C Vet",
            _ => classCode
        };

        /// <summary>
        /// All discipline + record-type combos in display order. Used by the UI to
        /// iterate through and render every section.
        /// </summary>
        public static IEnumerable<(string Discipline, string RecordType)> AllCombos()
        {
            yield return (RecordDisciplines.Precision, RecordTypes.Individual);
            yield return (RecordDisciplines.Precision, RecordTypes.Team);
            yield return (RecordDisciplines.MagnumPrecision, RecordTypes.Individual);
            yield return (RecordDisciplines.MagnumPrecision, RecordTypes.Team);
            yield return (RecordDisciplines.Milsnabb, RecordTypes.Individual);
            yield return (RecordDisciplines.Milsnabb, RecordTypes.Team);
        }
    }
}
