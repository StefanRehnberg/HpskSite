namespace HpskSite.Models.ViewModels.Training
{
    /// <summary>
    /// Represents a single step within a training level
    /// </summary>
    public class TrainingStep
    {
        public int StepNumber { get; set; }
        public string Description { get; set; } = string.Empty;
        public string? Notes { get; set; }
        public bool IsCompetitionRequired { get; set; }
        public string? AdditionalRequirements { get; set; }

        /// <summary>
        /// Get the unique identifier for this step across all levels
        /// </summary>
        public string GetStepId(int levelId)
        {
            return $"{levelId}-{StepNumber}";
        }

        /// <summary>
        /// Check if this is a final step that completes the level
        /// </summary>
        public bool IsFinalStep(TrainingLevel level)
        {
            return StepNumber == level.Steps.Count;
        }

        /// <summary>
        /// Parse requirements to identify key criteria (points, shots, etc.)
        /// </summary>
        public TrainingStepRequirements ParseRequirements()
        {
            var requirements = new TrainingStepRequirements();

            // Extract points requirement (e.g., "minst 34 poäng")
            var pointsMatch = System.Text.RegularExpressions.Regex.Match(Description, @"(\d+)\s*poäng");
            if (pointsMatch.Success)
            {
                requirements.MinPoints = int.Parse(pointsMatch.Groups[1].Value);
            }

            // Extract series count (e.g., "tre serier")
            var seriesMatch = System.Text.RegularExpressions.Regex.Match(Description, @"(\d+|två|tre|fyra|fem|sex)\s*serier?");
            if (seriesMatch.Success)
            {
                var countText = seriesMatch.Groups[1].Value;
                requirements.SeriesCount = countText switch
                {
                    "två" => 2,
                    "tre" => 3,
                    "fyra" => 4,
                    "fem" => 5,
                    "sex" => 6,
                    _ => int.TryParse(countText, out int count) ? count : 1
                };
            }

            // Check for consecutive series requirement ("i följd")
            requirements.ConsecutiveRequired = Description.Contains("i följd");

            // Check for competition requirement ("tävling")
            requirements.CompetitionRequired = Description.Contains("tävling") || IsCompetitionRequired;

            // Extract shot requirements (e.g., "3 st 8:or", "minst ett skott är en 10:a")
            var shotMatch = System.Text.RegularExpressions.Regex.Match(Description, @"(\d+)\s*st\s*(\d+):or");
            if (shotMatch.Success)
            {
                requirements.MinSpecificShots = int.Parse(shotMatch.Groups[1].Value);
                requirements.ShotValue = int.Parse(shotMatch.Groups[2].Value);
            }

            // Check for X-shot requirements
            if (Description.Contains("X"))
            {
                var xMatch = System.Text.RegularExpressions.Regex.Match(Description, @"(\d+)\s*st\s*X");
                if (xMatch.Success)
                {
                    requirements.MinXShots = int.Parse(xMatch.Groups[1].Value);
                }
            }

            return requirements;
        }
    }

    /// <summary>
    /// Parsed requirements from a training step description
    /// </summary>
    public class TrainingStepRequirements
    {
        public int? MinPoints { get; set; }
        public int SeriesCount { get; set; } = 1;
        public bool ConsecutiveRequired { get; set; }
        public bool CompetitionRequired { get; set; }
        public int? MinSpecificShots { get; set; }
        public int? ShotValue { get; set; }
        public int? MinXShots { get; set; }
        public bool AllShotsBlackRequired { get; set; }
    }
}