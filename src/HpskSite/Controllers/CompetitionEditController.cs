using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.CompetitionTypes.Common.Interfaces;
using HpskSite.CompetitionTypes.Precision.Services;
using Umbraco.Cms.Core;
using Newtonsoft.Json;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for competition data editing across all competition types.
    /// Routes edit requests to type-specific services for saving to Umbraco.
    /// </summary>
    public class CompetitionEditController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IPublishedContentQuery _publishedContentQuery;

        public CompetitionEditController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IPublishedContentQuery publishedContentQuery)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _publishedContentQuery = publishedContentQuery;
        }

        /// <summary>
        /// Get competition data for editing.
        /// </summary>
        [HttpGet]
        public IActionResult GetCompetitionData(int competitionId)
        {
            Console.WriteLine($"GetCompetitionData called with competitionId: {competitionId}");
            try
            {
                var content = _contentService.GetById(competitionId);
                Console.WriteLine($"Content found: {content != null}");
                if (content != null)
                {
                    Console.WriteLine($"Content name: {content.Name}");
                }
                if (content == null)
                {
                    Console.WriteLine($"Competition with ID {competitionId} not found");
                    return NotFound(new { success = false, message = $"Competition with ID {competitionId} not found" });
                }

                // Get field values from Umbraco content
                var contactEmailValue = content.GetValue<string>("contactEmail") ?? "";
                var contactPhoneValue = content.GetValue<string>("contactPhone") ?? "";

                // Get competitionManagers - JSON array of member IDs
                var competitionManagersJson = content.GetValue<string>("competitionManagers") ?? "[]";
                int[] competitionManagerIds;

                try
                {
                    competitionManagerIds = JsonConvert.DeserializeObject<int[]>(competitionManagersJson) ?? Array.Empty<int>();
                }
                catch
                {
                    competitionManagerIds = Array.Empty<int>();
                }

                var allowDualCClassValue = content.GetValue<bool>("allowDualCClassRegistration");

                // Extract competition data - ensure all values are properly serialized
                // Check if parent is a series
                var parent = content.ParentId > 0 ? _contentService.GetById(content.ParentId) : null;
                var isInSeries = parent != null && parent.ContentType.Alias == "competitionSeries";

                var competitionData = new
                {
                    id = content.Id,
                    competitionName = content.GetValue<string>("competitionName") ?? "",
                    description = content.GetValue<string>("description") ?? "",
                    venue = content.GetValue<string>("venue") ?? "",
                    competitionDate = content.GetValue<DateTime>("competitionDate"),
                    competitionEndDate = content.GetValue<DateTime?>("competitionEndDate"),
                    registrationOpenDate = content.GetValue<DateTime?>("registrationOpenDate"),
                    registrationCloseDate = content.GetValue<DateTime?>("registrationCloseDate"),
                    maxParticipants = content.GetValue<int>("maxParticipants"),
                    registrationFee = content.GetValue<decimal>("registrationFee"),
                    competitionDirector = content.GetValue<string>("competitionDirector") ?? "",
                    contactEmail = contactEmailValue,
                    contactPhone = contactPhoneValue,
                    numberOfSeriesOrStations = content.GetValue<int>("numberOfSeriesOrStations"),
                    numberOfFinalSeries = content.GetValue<int>("numberOfFinalSeries"),
                    allowDualCClass = allowDualCClassValue,
                    showLiveResults = content.GetValue<bool>("showLiveResults"),
                    addToMenu = content.GetValue<bool>("addToMenu"),
                    isActive = content.GetValue<bool>("isActive"),
                    isClubOnly = content.GetValue<bool>("isClubOnly"),
                    clubId = content.GetValue<int?>("clubId") ?? 0,
                    swishNumber = content.GetValue<string>("swishNumber") ?? "",
                    competitionManagers = competitionManagerIds,
                    shootingClassIds = GetShootingClassIdsString(content),
                    competitionScope = content.GetValue<string>("competitionScope") ?? "",
                    isAwardingStandardMedals = content.GetValue<bool>("isAwardingStandardMedals"),
                    seriesId = isInSeries ? parent!.Id : (int?)null,
                    seriesName = isInSeries ? parent!.Name : null
                };

                Console.WriteLine($"Returning competition data for: {content.Name}");
                return Ok(new { success = true, data = competitionData });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Exception in GetCompetitionData: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return StatusCode(500, new { success = false, message = $"Error loading competition data: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get valid shooting classes for a competition based on its type.
        /// Works for both creating new competitions (competitionId=0) and editing existing ones.
        /// </summary>
        [HttpGet]
        public IActionResult GetShootingClasses(int? competitionId)
        {
            try
            {
                // If competitionId is provided and > 0, validate it exists
                if (competitionId.HasValue && competitionId.Value > 0)
                {
                    var content = _contentService.GetById(competitionId.Value);
                    if (content == null)
                    {
                        return NotFound(new { success = false, message = "Competition not found" });
                    }
                }

                // Get all shooting classes - same for all competitions
                var classes = HpskSite.Models.ShootingClasses.All;
                var classOptions = classes.Select(c => new { id = c.Id, name = c.Name, description = c.Description }).ToList();

                return Ok(new { success = true, data = classOptions });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Save competition data to Umbraco.
        /// Routes to type-specific service based on competition type.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCompetition(
            [FromBody] CompetitionEditRequest request)
        {
            if (!ModelState.IsValid)
            {
                return BadRequest(new
                {
                    success = false,
                    message = "Invalid request data",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            try
            {
                if (!request.IsValid())
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Request validation failed"
                    });
                }

                // Validate competition exists
                var content = _contentService.GetById(request.CompetitionId);
                if (content == null)
                {
                    return NotFound(new
                    {
                        success = false,
                        message = "Competition not found"
                    });
                }

                // Validate competition type
                if (string.IsNullOrEmpty(request.CompetitionType))
                {
                    return BadRequest(new
                    {
                        success = false,
                        message = "Competition type is required"
                    });
                }

                // Route to type-specific save logic
                var result = await RouteToTypeSpecificSave(request, content);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return StatusCode(500, new
                {
                    success = false,
                    message = "An error occurred while saving the competition",
                    error = ex.Message
                });
            }
        }

        /// <summary>
        /// Extract shooting class IDs as comma-separated string from competition content.
        /// </summary>
        private string GetShootingClassIdsString(Umbraco.Cms.Core.Models.IContent content)
        {
            var classIdsObj = content.GetValue("shootingClassIds");

            if (classIdsObj is string[] classArray && classArray.Length > 0)
            {
                return string.Join(",", classArray);
            }
            else if (classIdsObj is IEnumerable<string> enumerable)
            {
                var classIds = enumerable.Where(s => !string.IsNullOrEmpty(s)).ToList();
                if (classIds.Any())
                {
                    return string.Join(",", classIds);
                }
            }
            else if (classIdsObj is string classStr && !string.IsNullOrEmpty(classStr))
            {
                // Could be JSON array or comma-separated
                if (classStr.StartsWith("[") && classStr.EndsWith("]"))
                {
                    // Parse JSON array to comma-separated
                    try
                    {
                        var jsonContent = classStr.Substring(1, classStr.Length - 2);
                        var ids = jsonContent.Split(',')
                            .Select(s => s.Trim().Trim('"').Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToList();
                        return string.Join(",", ids);
                    }
                    catch
                    {
                        return classStr;
                    }
                }
                else
                {
                    return classStr;
                }
            }

            return "";
        }

        /// <summary>
        /// Route edit request to appropriate competition type service.
        /// </summary>
        private async Task<object> RouteToTypeSpecificSave(
            CompetitionEditRequest request,
            Umbraco.Cms.Core.Models.IContent content)
        {
            return request.CompetitionType.ToLower() switch
            {
                "precision" => await SavePrecisionCompetition(request, content),
                _ => new
                {
                    success = false,
                    message = $"Unknown competition type: {request.CompetitionType}"
                }
            };
        }

        /// <summary>
        /// Handle Precision competition type saves.
        /// </summary>
        private async Task<object> SavePrecisionCompetition(
            CompetitionEditRequest request,
            Umbraco.Cms.Core.Models.IContent content)
        {
            try
            {
                var service = new PrecisionCompetitionEditService(_contentService);
                var result = await service.SaveCompetitionAsync(request.CompetitionId, request.Fields);
                
                return new
                {
                    success = result.Success,
                    message = result.Message,
                    errors = result.Errors,
                    data = result.Data
                };
            }
            catch (Exception ex)
            {
                return new
                {
                    success = false,
                    message = "Error saving Precision competition",
                    error = ex.Message
                };
            }
        }
    }

    /// <summary>
    /// Request model for competition data editing.
    /// </summary>
    public class CompetitionEditRequest
    {
        public int CompetitionId { get; set; }
        public string CompetitionType { get; set; }
        
        /// <summary>
        /// Dictionary of field names to values for updating.
        /// Example: { "competitionName": "New Name", "maxParticipants": 100 }
        /// </summary>
        public Dictionary<string, object> Fields { get; set; } = new Dictionary<string, object>();

        public bool IsValid()
        {
            return CompetitionId > 0 && 
                   !string.IsNullOrEmpty(CompetitionType) && 
                   Fields != null && 
                   Fields.Any();
        }
    }
}
