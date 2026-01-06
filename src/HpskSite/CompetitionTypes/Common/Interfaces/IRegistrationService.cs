namespace HpskSite.CompetitionTypes.Common.Interfaces
{
    /// <summary>
    /// Service interface for handling competition registration.
    /// Each competition type implements this to define registration rules and validation.
    /// </summary>
    public interface IRegistrationService
    {
        /// <summary>
        /// Validate if a member can register for a competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <param name="memberId">ID of the member</param>
        /// <returns>Validation result indicating if registration is allowed</returns>
        Task<dynamic> ValidateRegistration(int competitionId, int memberId);

        /// <summary>
        /// Register a member for a competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <param name="memberId">ID of the member</param>
        /// <param name="shootingClass">Shooting class for registration</param>
        /// <returns>Registration ID if successful</returns>
        Task<int> RegisterMember(int competitionId, int memberId, string shootingClass);

        /// <summary>
        /// Cancel a registration.
        /// </summary>
        /// <param name="registrationId">ID of the registration to cancel</param>
        /// <returns>Success indicator</returns>
        Task<bool> CancelRegistration(int registrationId);

        /// <summary>
        /// Get registration status for a member.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <param name="memberId">ID of the member</param>
        /// <returns>Registration details or null if not registered</returns>
        Task<dynamic> GetRegistrationStatus(int competitionId, int memberId);

        /// <summary>
        /// Get all registrations for a competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>List of all registrations for the competition</returns>
        Task<List<dynamic>> GetCompetitionRegistrations(int competitionId);

        /// <summary>
        /// Check if competition has reached maximum participants.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>True if at max capacity</returns>
        Task<bool> IsAtMaxCapacity(int competitionId);

        /// <summary>
        /// Get available shooting classes for a competition.
        /// </summary>
        /// <param name="competitionId">ID of the competition</param>
        /// <returns>List of available shooting classes</returns>
        Task<List<dynamic>> GetAvailableClasses(int competitionId);
    }
}
