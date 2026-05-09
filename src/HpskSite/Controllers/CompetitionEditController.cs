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

                var description = HpskSite.Extensions.RteHelper.ExtractMarkup(content.GetValue<string>("description"));

                var competitionData = new
                {
                    id = content.Id,
                    competitionName = content.GetValue<string>("competitionName") ?? "",
                    description = description,
                    venue = content.GetValue<string>("venue") ?? "",
                    competitionDate = GetDateTimeString(content, competitionId, "competitionDate", true),
                    competitionEndDate = GetDateTimeString(content, competitionId, "competitionEndDate", false),
                    registrationOpenDate = GetDateTimeString(content, competitionId, "registrationOpenDate", true),
                    registrationCloseDate = GetDateTimeString(content, competitionId, "registrationCloseDate", true),
                    maxParticipants = content.GetValue<int>("maxParticipants"),
                    registrationFee = content.GetValue<decimal>("registrationFee"),
                    juniorRegistrationFee = content.GetValue<string>("juniorRegistrationFee") ?? "0",
                    subCompetitionFee = content.GetValue<string>("subCompetitionFee") ?? "0",
                    subCompetitionFeeMode = content.GetValue<string>("subCompetitionFeeMode") ?? "perClass",
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
                    regionalFederation = content.GetValue<string>("regionalFederation") ?? "",
                    swishNumber = content.GetValue<string>("swishNumber") ?? "",
                    competitionManagers = competitionManagerIds,
                    shootingClassIds = GetShootingClassIdsString(content),
                    competitionScope = content.GetValue<string>("competitionScope") ?? "",
                    isAwardingStandardMedals = content.GetValue<bool>("isAwardingStandardMedals"),
                    allowSelfReporting = content.GetValue<bool>("allowSelfReporting"),
                    isExternal = content.GetValue<bool>("isExternal"),
                    externalUrl = content.GetValue<string>("externalUrl") ?? "",
                    externalRegistrationEmail = content.GetValue<string>("externalRegistrationEmail") ?? "",
                    allowTeams = content.GetValue<bool>("allowTeams"),
                    teamRegistrationFee = content.GetValue<string>("teamRegistrationFee") ?? "0",
                    allowStafett = content.GetValue<bool>("allowStafett"),
                    stafettRegistrationFee = content.GetValue<string>("stafettRegistrationFee") ?? "0",
                    seriesId = isInSeries ? parent!.Id : (int?)null,
                    seriesName = isInSeries ? parent!.Name : null,
                    competitionType = content.GetValue<string>("competitionType") ?? "Precision",
                    // Fältskytte-specific fields
                    scoringMode = content.GetValue<string>("scoringMode") ?? "Normal",
                    stationConfig = content.GetValue<string>("stationConfig") ?? "",
                    patrolSize = content.GetValue<int>("patrolSize"),
                    patrolIntervalMinutes = content.GetValue<int>("patrolIntervalMinutes"),
                    maxReshoots = content.GetValue<int>("maxReshoots"),
                    rollingStart = content.HasProperty("rollingStart") ? content.GetValue<string>("rollingStart") ?? "" : "",
                    faltskytteSelfServiceResults = content.HasProperty("faltskytteSelfServiceResults") && content.GetValue<bool>("faltskytteSelfServiceResults"),
                    subCompetitionName = content.HasProperty("subCompetitionName") ? content.GetValue<string>("subCompetitionName") ?? "" : "",
                    // Direktplacering
                    direktplaceringConfig = content.HasProperty("direktplaceringConfig") ? content.GetValue<string>("direktplaceringConfig") ?? "" : ""
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

                // Invalidate admin competition/series list caches so edits are reflected
                AppCaches.RuntimeCache.ClearByKey("admin_series_list");
                AppCaches.RuntimeCache.ClearByRegex("^admin_competitions_list_");

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
                "springskytte" => await SavePrecisionCompetition(request, content),
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

        /// <summary>
        /// Get a date/time string for edit forms. Tries the published content cache first
        /// (which correctly preserves time via value converters), then falls back to IContent raw values.
        /// </summary>
        private string? GetDateTimeString(Umbraco.Cms.Core.Models.IContent content, int contentId, string propertyAlias, bool includeTime)
        {
            var format = includeTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";

            // Try published cache first — it correctly preserves date+time
            var published = UmbracoContext.Content?.GetById(contentId);
            if (published != null)
            {
                var pubDate = published.Value<DateTime?>(propertyAlias);
                if (pubDate.HasValue && pubDate.Value != DateTime.MinValue)
                    return pubDate.Value.ToString(format);
            }

            // Fallback: IContent raw value
            var raw = content.GetValue(propertyAlias);
            if (raw == null) return null;

            if (raw is DateTime dt)
            {
                if (dt == DateTime.MinValue) return null;
                return dt.ToString(format);
            }

            var str = raw.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(str)) return null;

            if (DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var parsed))
            {
                if (parsed == DateTime.MinValue) return null;
                return parsed.ToString(format);
            }

            return null;
        }

        /// <summary>
        /// Extract HTML markup from an Umbraco RTE value.
        /// RTE stores as JSON: {"markup":"<p>text</p>","blocks":{...}}
        /// Returns the markup string, or the original value if it's not JSON.
        /// </summary>
        private static string ExtractRteMarkup(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (!value.TrimStart().StartsWith("{")) return value;

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(value);
                if (doc.RootElement.TryGetProperty("markup", out var markup))
                    return markup.GetString() ?? "";
            }
            catch { }

            return value;
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
