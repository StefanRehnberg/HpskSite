using HpskSite.Models.ViewModels.Competition;
using Umbraco.Cms.Core.Services;
using HpskSite.Services;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class StartListRequestValidator
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ILogger<StartListRequestValidator> _logger;
        private readonly AdminAuthorizationService _authorizationService;

        public StartListRequestValidator(IMemberService memberService, IContentService contentService, ILogger<StartListRequestValidator> logger, AdminAuthorizationService authorizationService)
        {
            _memberService = memberService;
            _contentService = contentService;
            _logger = logger;
            _authorizationService = authorizationService;
        }

        public (bool IsValid, string? ErrorMessage) ValidateCompetitionId(int competitionId)
        {
            if (competitionId <= 0)
                return (false, "Ogiltigt tävlings-ID.");
            
            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return (false, "Tävlingen hittades inte.");
            
            return (true, null);
        }

        public (bool IsValid, string? ErrorMessage) ValidateGenerationRequest(int competitionId, List<CompetitionRegistration>? registrations)
        {
            var competitionValidation = ValidateCompetitionId(competitionId);
            if (!competitionValidation.IsValid)
                return competitionValidation;
            
            if (registrations == null || !registrations.Any())
                return (false, "Inga anmälningar hittades för denna tävling.");
            
            return (true, null);
        }

        public async Task<bool> CanManageCompetition(int memberId, int competitionId)
        {
            try
            {
                // Mirror the canonical three-tier competition check used everywhere else
                // (e.g. CompetitionResultsController.CanManageCompetitionResults): site admin,
                // competition manager, OR club admin for the competition's club. The club-admin
                // check folds in regional admins (AdminAuthorizationService.IsClubAdminForClub
                // also matches RegionalAdmin_{region} for the club's region). Previously this
                // only accepted site admins + the competitionManagers list, so a club/regional
                // admin who hosts the competition could not create or publish its start list.
                //
                // C3-planen S2 (Stefans beslut 2026-10-14): redigeringsregeln. Kretsens admin bara för en
                // kretsvärdad tävling — förut släpptes kretsen i regionalFederation in även för en klubbvärdad.
                return await _authorizationService.CanEditCompetitionAsync(competitionId, HpskSite.Services.AdminAuthorizationService.CompetitionRead.Saved);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking competition management permissions for member {MemberId}, competition {CompetitionId}", memberId, competitionId);
                return false;
            }
        }

    }
}
