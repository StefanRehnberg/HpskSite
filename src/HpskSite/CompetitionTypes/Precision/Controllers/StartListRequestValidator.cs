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
                // Site administrators can manage everything
                var member = _memberService.GetById(memberId);
                if (member == null) return false;

                var memberGroups = _memberService.GetAllRoles(member.Username)?.ToArray() ?? new string[0];
                if (memberGroups.Contains("Administrators"))
                    return true;

                // Competition managers can manage their competitions
                var competition = _contentService.GetById(competitionId);
                if (competition == null) return false;

                var json = competition.GetValue<string>("competitionManagers") ?? "[]";
                var managerIds = JsonConvert.DeserializeObject<int[]>(json) ?? Array.Empty<int>();

                return managerIds.Contains(memberId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking competition management permissions for member {MemberId}, competition {CompetitionId}", memberId, competitionId);
                return false;
            }
        }

    }
}
