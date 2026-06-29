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

        public (bool IsValid, string? ErrorMessage) ValidateStartListId(int startListId)
        {
            if (startListId <= 0)
                return (false, "Ogiltigt startlist-ID.");
            
            var startList = _contentService.GetById(startListId);
            if (startList == null)
                return (false, "Startlista hittades inte.");
            
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
                if (await _authorizationService.IsCurrentUserAdminAsync()) return true;
                if (await _authorizationService.IsCompetitionManager(competitionId)) return true;

                var competition = _contentService.GetById(competitionId);
                if (competition == null) return false;

                var clubId = competition.GetValue<int>("clubId");
                if (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId)) return true;

                // Region-hosted (clubless) competition: the regional admin of the competition's
                // region manages it. (Club-hosted comps already pass above — IsClubAdminForClub
                // matches the regional admin of the club's region too.)
                var region = competition.GetValue<string>("regionalFederation") ?? "";
                if (!string.IsNullOrEmpty(region) && await _authorizationService.IsRegionalAdminForRegion(region)) return true;

                return false;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking competition management permissions for member {MemberId}, competition {CompetitionId}", memberId, competitionId);
                return false;
            }
        }

    }
}
