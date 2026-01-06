using System.Text.Json.Serialization;

namespace HpskSite.Models.ViewModels.TrainingScoring
{
    /// <summary>
    /// Represents a single shooting series in a training session
    /// </summary>
    public class TrainingSeries
    {
        /// <summary>
        /// Series number within the training session (1, 2, 3, etc.)
        /// </summary>
        [JsonPropertyName("seriesNumber")]
        public int SeriesNumber { get; set; }

        /// <summary>
        /// Individual shot values (0-10 or "X"). Null for simplified entry methods.
        /// </summary>
        [JsonPropertyName("shots")]
        public List<string>? Shots { get; set; }

        /// <summary>
        /// Total points for this series
        /// </summary>
        [JsonPropertyName("total")]
        public int Total { get; set; }

        /// <summary>
        /// Number of X shots (10.9 in precision scoring). 0 if unknown.
        /// </summary>
        [JsonPropertyName("xCount")]
        public int XCount { get; set; }

        /// <summary>
        /// Entry method used: "ShotByShot", "SeriesTotal", or "TotalOnly"
        /// </summary>
        [JsonPropertyName("entryMethod")]
        public string EntryMethod { get; set; } = "ShotByShot";

        /// <summary>
        /// Number of series (only used for TotalOnly method)
        /// </summary>
        [JsonPropertyName("seriesCount")]
        public int? SeriesCount { get; set; }

        /// <summary>
        /// Calculate total and X-count from shots array (only for ShotByShot method)
        /// </summary>
        public void CalculateScore()
        {
            // Only calculate from shots if using ShotByShot method
            if (Shots == null || Shots.Count == 0)
                return;

            Total = 0;
            XCount = 0;

            foreach (var shot in Shots)
            {
                if (string.IsNullOrWhiteSpace(shot))
                    continue;

                var shotValue = shot.Trim().ToUpper();

                if (shotValue == "X")
                {
                    Total += 10;
                    XCount++;
                }
                else if (int.TryParse(shotValue, out int value))
                {
                    Total += value;
                }
            }
        }

        /// <summary>
        /// Validate series based on entry method
        /// </summary>
        public bool IsValid()
        {
            // Default to ShotByShot for backward compatibility
            if (string.IsNullOrEmpty(EntryMethod))
                EntryMethod = "ShotByShot";

            return EntryMethod switch
            {
                "ShotByShot" => ValidateShotByShot(),
                "SeriesTotal" => ValidateSeriesTotal(),
                "TotalOnly" => ValidateTotalOnly(),
                _ => false
            };
        }

        /// <summary>
        /// Validate shot-by-shot entry (requires exactly 5 shots)
        /// </summary>
        private bool ValidateShotByShot()
        {
            return Shots != null && Shots.Count == 5 && Shots.All(s => IsValidShot(s));
        }

        /// <summary>
        /// Validate series-total entry (requires total, shots must be null)
        /// </summary>
        private bool ValidateSeriesTotal()
        {
            return Shots == null && Total > 0 && Total <= 50 && XCount >= 0 && XCount <= 5;
        }

        /// <summary>
        /// Validate total-only entry (requires total, shots must be null)
        /// </summary>
        private bool ValidateTotalOnly()
        {
            return Shots == null && Total > 0 && XCount >= 0;
        }

        /// <summary>
        /// Validate a single shot value
        /// </summary>
        private bool IsValidShot(string shot)
        {
            if (string.IsNullOrWhiteSpace(shot))
                return false;

            var shotValue = shot.Trim().ToUpper();

            if (shotValue == "X")
                return true;

            if (int.TryParse(shotValue, out int value))
            {
                return value >= 0 && value <= 10;
            }

            return false;
        }
    }
}
