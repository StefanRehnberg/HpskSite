namespace HpskSite.CompetitionTypes.Common.Interfaces
{
    /// <summary>
    /// Service interface for generating and managing start lists.
    /// Each competition type implements this to define start list generation rules.
    /// </summary>
    public interface IStartListService
    {
        /// <summary>
        /// Generate start list for a competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>Generated start list configuration</returns>
        Task<dynamic> GenerateStartList(int competitionId);

        /// <summary>
        /// Generate start list with specific grouping algorithm.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <param name="groupingStrategy">Strategy for grouping shooters (e.g., "byClass", "random", "byClub")</param>
        /// <returns>Generated start list with specified grouping</returns>
        Task<dynamic> GenerateStartListWithStrategy(int competitionId, string groupingStrategy);

        /// <summary>
        /// Validate start list before publishing.
        /// </summary>
        /// <param name="startListId">ID of the start list</param>
        /// <returns>Validation result with any errors or warnings</returns>
        Task<dynamic> ValidateStartList(int startListId);

        /// <summary>
        /// Publish start list (make it official).
        /// </summary>
        /// <param name="startListId">ID of the start list</param>
        /// <returns>Success indicator</returns>
        Task<bool> PublishStartList(int startListId);

        /// <summary>
        /// Get current start list for a competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>Current start list or null if not generated</returns>
        Task<dynamic> GetCurrentStartList(int competitionId);

        /// <summary>
        /// Update start list (e.g., after registration changes).
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>Updated start list</returns>
        Task<dynamic> UpdateStartList(int competitionId);
    }
}
