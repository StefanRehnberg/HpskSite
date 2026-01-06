using HpskSite.CompetitionTypes.Common.Interfaces;
using HpskSite.CompetitionTypes.Common.Utilities;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Scoring service for Precision competition type.
    /// Implements IScoringService to calculate scores for precision shooting competitions.
    /// Uses shared ScoringUtilities for core scoring logic.
    /// </summary>
    public class PrecisionScoringService : IScoringService
    {
        /// <summary>
        /// Calculate total points for a series of shots.
        /// Each shot: X = 10, 10 = 10, 9-0 = face value
        /// Uses shared ScoringUtilities for calculation.
        /// </summary>
        public decimal CalculateSeriesTotal(List<string> shots)
        {
            return ScoringUtilities.CalculateTotal(shots);
        }

        /// <summary>
        /// Calculate inner tens (X-shots) from a series.
        /// X-shots are shots in the inner 10-ring.
        /// Uses shared ScoringUtilities for calculation.
        /// </summary>
        public int CalculateInnerTens(List<string> shots)
        {
            return ScoringUtilities.CountInnerTens(shots);
        }

        /// <summary>
        /// Calculate tens count (10 and X) from a series.
        /// Includes both regular 10-shots and X-shots.
        /// Uses shared ScoringUtilities for calculation.
        /// </summary>
        public int CalculateTens(List<string> shots)
        {
            return ScoringUtilities.CountAllTens(shots);
        }

        /// <summary>
        /// Validate if a shot value is valid for precision competition.
        /// Valid values: 0-10, X
        /// Uses shared ScoringUtilities for validation.
        /// </summary>
        public bool IsValidShotValue(string shotValue)
        {
            return ScoringUtilities.IsValidShotValue(shotValue);
        }

        /// <summary>
        /// Convert shot value to points.
        /// X = 10 points, numeric values = face value
        /// Uses shared ScoringUtilities for conversion.
        /// </summary>
        public decimal ShotValueToPoints(string shotValue)
        {
            return ScoringUtilities.ShotToPoints(shotValue);
        }

        /// <summary>
        /// Get maximum possible score for a single series.
        /// Precision: 5 shots × 10 points = 50 points max
        /// </summary>
        public decimal GetMaxSeriesScore()
        {
            return 50; // 5 shots × 10 points each
        }

        /// <summary>
        /// Get maximum possible score for entire competition.
        /// Precision: max per series × number of series
        /// </summary>
        public decimal GetMaxCompetitionScore(int numberOfSeries)
        {
            if (numberOfSeries <= 0)
                return 0;

            return GetMaxSeriesScore() * numberOfSeries;
        }

        /// <summary>
        /// Calculate score from JSON-serialized shots array.
        /// Helper method for working with persisted shot data.
        /// </summary>
        public int CalculateTotalFromShotsJson(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<string[]>(shotsJson) ?? new string[0];
                if (shots.Length == 0)
                    return 0;

                return (int)CalculateSeriesTotal(shots.ToList());
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Calculate X-count from JSON-serialized shots array.
        /// Helper method for working with persisted shot data.
        /// </summary>
        public int CalculateXCountFromShotsJson(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<string[]>(shotsJson) ?? new string[0];
                if (shots.Length == 0)
                    return 0;

                return CalculateInnerTens(shots.ToList());
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Calculate 10-count from JSON-serialized shots array.
        /// Helper method for working with persisted shot data.
        /// </summary>
        public int CalculateTensCountFromShotsJson(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<string[]>(shotsJson) ?? new string[0];
                if (shots.Length == 0)
                    return 0;

                return CalculateTens(shots.ToList());
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Get scoring breakdown/explanation for a series.
        /// Useful for displaying score details to users.
        /// </summary>
        public dynamic GetScoringBreakdown(List<string> shots)
        {
            if (shots == null || shots.Count == 0)
                return new { total = 0, innerTens = 0, tens = 0, breakdown = new string[0] };

            var breakdown = shots.Select((shot, index) => new
            {
                shotNumber = index + 1,
                value = shot,
                points = ShotValueToPoints(shot),
                isValid = IsValidShotValue(shot)
            }).ToList();

            return new
            {
                total = (int)CalculateSeriesTotal(shots),
                innerTens = CalculateInnerTens(shots),
                tens = CalculateTens(shots),
                breakdown = breakdown,
                maxPossible = (int)GetMaxSeriesScore()
            };
        }
    }
}
