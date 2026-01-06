namespace HpskSite.CompetitionTypes.Common.Interfaces
{
    /// <summary>
    /// Service interface for calculating competition scores.
    /// Each competition type implements this to define scoring rules.
    /// </summary>
    public interface IScoringService
    {
        /// <summary>
        /// Calculate total points for a series of shots.
        /// </summary>
        /// <param name="shots">List of shot values (e.g., "10", "X", "9")</param>
        /// <returns>Total points for the series</returns>
        decimal CalculateSeriesTotal(List<string> shots);

        /// <summary>
        /// Calculate inner tens (X-shots) count from a series.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Number of X-shots</returns>
        int CalculateInnerTens(List<string> shots);

        /// <summary>
        /// Calculate tens count (10 and X) from a series.
        /// </summary>
        /// <param name="shots">List of shot values</param>
        /// <returns>Number of 10-shots and X-shots</returns>
        int CalculateTens(List<string> shots);

        /// <summary>
        /// Validate if a shot value is valid for this competition type.
        /// </summary>
        /// <param name="shotValue">Shot value (e.g., "10", "X", "9")</param>
        /// <returns>True if valid, false otherwise</returns>
        bool IsValidShotValue(string shotValue);

        /// <summary>
        /// Convert shot value to points.
        /// </summary>
        /// <param name="shotValue">Shot value (e.g., "10", "X", "9")</param>
        /// <returns>Points for the shot</returns>
        decimal ShotValueToPoints(string shotValue);

        /// <summary>
        /// Get maximum possible score for a single series.
        /// </summary>
        /// <returns>Maximum points achievable in one series</returns>
        decimal GetMaxSeriesScore();

        /// <summary>
        /// Get maximum possible score for entire competition.
        /// </summary>
        /// <param name="numberOfSeries">Number of series in competition</param>
        /// <returns>Maximum points achievable for full competition</returns>
        decimal GetMaxCompetitionScore(int numberOfSeries);
    }
}
