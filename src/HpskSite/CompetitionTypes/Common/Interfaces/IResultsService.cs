namespace HpskSite.CompetitionTypes.Common.Interfaces
{
    /// <summary>
    /// Service interface for generating and managing competition results.
    /// Each competition type implements this to define result generation logic.
    /// </summary>
    public interface IResultsService
    {
        /// <summary>
        /// Generate results for a completed competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>List of result entries with scores and rankings</returns>
        Task<List<dynamic>> GenerateCompetitionResults(int competitionId);

        /// <summary>
        /// Generate live leaderboard for ongoing competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>Current standings with partial scores</returns>
        Task<List<dynamic>> GetLiveLeaderboard(int competitionId);

        /// <summary>
        /// Get detailed results for a single participant.
        /// </summary>
        /// <param name="registrationId">ID of the registration</param>
        /// <returns>Detailed results including all series scores</returns>
        Task<dynamic> GetParticipantResults(int registrationId);

        /// <summary>
        /// Export results in specified format.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <param name="format">Export format (e.g., "CSV", "Excel", "PDF")</param>
        /// <returns>Exported data as bytes</returns>
        Task<byte[]> ExportResults(int competitionId, string format);

        /// <summary>
        /// Calculate final ranking for competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>Ranked list of participants</returns>
        Task<List<dynamic>> CalculateFinalRanking(int competitionId);
    }
}
