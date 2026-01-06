using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models.ViewModels.Competition;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Common.Interfaces;
using System.Text.Json;
using HpskSite.Services;

namespace HpskSite.CompetitionTypes.Precision.Controllers
{
    public class PrecisionResultsController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<PrecisionResultsController> _logger;
        private readonly IScoringService _scoringService;
        private readonly IResultsService _resultsService;
        private readonly ClubService _clubService;

        public PrecisionResultsController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<PrecisionResultsController> logger,
            IScoringService scoringService,
            IResultsService resultsService,
            ClubService clubService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
            _scoringService = scoringService;
            _resultsService = resultsService;
            _clubService = clubService;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SavePrecisionResults(PrecisionResultsEntryViewModel model)
        {
            try
            {
                // Validate user authentication
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "You must be logged in to save results." });
                }

                // Validate model
                if (model.Shooters == null || !model.Shooters.Any())
                {
                    return Json(new { success = false, message = "No shooters to save results for." });
                }

                // Find the registration content
                var registration = _contentService.GetById(model.RegistrationId);
                if (registration == null)
                {
                    return Json(new { success = false, message = "Registration not found." });
                }

                // Validate shots for each shooter
                var validationErrors = new List<string>();
                foreach (var shooter in model.Shooters)
                {
                    if (shooter.Shots == null || shooter.Shots.Count != 5)
                    {
                        validationErrors.Add($"Shooter {shooter.Name}: Must have exactly 5 shots.");
                        continue;
                    }

                    foreach (var shot in shooter.Shots)
                    {
                        if (shot < 0 || shot > 10)
                        {
                            validationErrors.Add($"Shooter {shooter.Name}: Invalid shot value {shot}. Must be 0-10.");
                        }
                    }
                }

                if (validationErrors.Any())
                {
                    return Json(new { success = false, message = "Validation errors:", errors = validationErrors });
                }

                // Save results
                var resultsData = new
                {
                    SeriesId = model.SeriesId,
                    SeriesNumber = model.SeriesNumber,
                    SeriesName = model.SeriesName,
                    EntryDate = DateTime.Now,
                    EnteredBy = currentMember.Name ?? currentMember.Email,
                    Shooters = model.Shooters.Select(s => new
                    {
                        s.Id,
                        s.Name,
                        s.Club,
                        s.Class,
                        Shots = s.Shots,
                        Total = s.Total,
                        XCount = s.XCount
                    }).ToList()
                };

                // Convert to JSON and save
                var jsonData = JsonSerializer.Serialize(resultsData, new JsonSerializerOptions
                {
                    WriteIndented = true
                });

                // Store in the registration content
                registration.SetValue("resultsData", jsonData);
                registration.SetValue("lastResultsUpdate", DateTime.Now);
                registration.SetValue("resultsEnteredBy", currentMember.Name ?? currentMember.Email);

                var saveResult = _contentService.Save(registration);
                if (saveResult.Success)
                {
                    var publishResult = _contentService.Publish(registration, Array.Empty<string>());
                    if (publishResult.Success)
                    {
                        _logger.LogInformation("Results saved successfully for registration {RegistrationId} by user {UserId}", 
                            model.RegistrationId, currentMember.Id);
                        
                        return Json(new { success = true, message = "Results saved successfully!" });
                    }
                    else
                    {
                        _logger.LogWarning("Results saved but not published for registration {RegistrationId}", model.RegistrationId);
                        return Json(new { success = true, message = "Results saved but not published." });
                    }
                }
                else
                {
                    var errorCount = saveResult.EventMessages?.Count ?? 0;
                    _logger.LogError("Failed to save results for registration {RegistrationId}. Error count: {ErrorCount}", 
                        model.RegistrationId, errorCount);
                    return Json(new { success = false, message = "Failed to save results." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving precision results for registration {RegistrationId}", model.RegistrationId);
                return Json(new { success = false, message = "An error occurred while saving results." });
            }
        }

        [HttpGet]
        public IActionResult GetResultsEntryData(int registrationId, int seriesId)
        {
            try
            {
                // Get registration data
                var registration = _contentService.GetById(registrationId);
                if (registration == null)
                {
                    return Json(new { success = false, message = "Registration not found." });
                }

                // Get shooter data from registration
                var shooters = new List<ShooterEntryViewModel>();

                // Resolve club name from clubId
                var clubId = registration.GetValue<int>("clubId");
                var clubName = clubId > 0 ? _clubService.GetClubNameById(clubId) : null;

                // Fallback for legacy data
                if (string.IsNullOrEmpty(clubName))
                {
                    var legacyClub = registration.GetValue<string>("memberClub");
                    if (!string.IsNullOrEmpty(legacyClub) && int.TryParse(legacyClub, out var legacyId))
                    {
                        clubName = _clubService.GetClubNameById(legacyId);
                    }
                    else
                    {
                        clubName = legacyClub;
                    }
                }

                // This would need to be implemented based on your registration structure
                // For now, creating a sample structure
                var shooter = new ShooterEntryViewModel
                {
                    Id = 1,
                    Name = registration.GetValue<string>("memberName") ?? "Unknown",
                    Club = clubName ?? "Unknown",
                    Class = registration.GetValue<string>("shootingClass") ?? "Unknown",
                    Shots = new List<int> { 0, 0, 0, 0, 0 } // Initialize with empty shots
                };
                
                shooters.Add(shooter);

                var model = new PrecisionResultsEntryViewModel
                {
                    RegistrationId = registrationId,
                    SeriesId = seriesId,
                    SeriesNumber = 1, // This would come from your series data
                    SeriesName = $"Series {seriesId}",
                    Shooters = shooters
                };

                return Json(new { success = true, data = model });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting results entry data for registration {RegistrationId}", registrationId);
                return Json(new { success = false, message = "Error loading data." });
            }
        }
    }
}
