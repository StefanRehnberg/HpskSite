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
        private readonly HpskSite.Services.AdminAuthorizationService _authorizationService;
        // Anmalningar sparas OPUBLICERADE, sa de maste raknas i SQL — den publicerade
        // cachen ser dem inte. Basklassen tar emot fabriken men exponerar den inte.
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly Microsoft.Extensions.Logging.ILogger<CompetitionEditController> _logger;

        public CompetitionEditController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IPublishedContentQuery publishedContentQuery,
            HpskSite.Services.AdminAuthorizationService authorizationService,
            Microsoft.Extensions.Logging.ILogger<CompetitionEditController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _publishedContentQuery = publishedContentQuery;
            _authorizationService = authorizationService;
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>
        /// May the caller read/change this competition's definition?
        ///
        /// This controller had NO authorization of any kind — verified 2026-08-05 that a fully
        /// ANONYMOUS caller could read a competition's whole configuration (including swishNumber and
        /// the organiser's contact details) and POST SaveCompetition to change fees, the Swish number,
        /// dates and classes on ANY competition. Changing swishNumber redirects payments, so this was
        /// the most serious hole found in the Springskytte test-plan run.
        ///
        /// Tiers match the rest of the per-competition surface: site admin, a named competition
        /// manager (or Bemanning roster app access), a club admin of the organising club, or the
        /// regional admin of the hosting krets on a region-hosted competition. Skjutledare are
        /// deliberately NOT included — running the firing line does not include redefining the
        /// competition's fees and classes.
        /// </summary>
        private async Task<bool> CanEditCompetitionAsync(int competitionId)
        {
            if (competitionId <= 0) return false;
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;
            if (await _authorizationService.IsCompetitionManager(competitionId)) return true;

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return false;

            var clubId = competition.GetValue<int>("clubId");
            if (clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId)) return true;

            return await _authorizationService.IsRegionHostAdminAsync(
                clubId, competition.GetValue<string>("regionalFederation"));
        }

        /// <summary>
        /// Get competition data for editing.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCompetitionData(int competitionId)
        {
            if (!await CanEditCompetitionAsync(competitionId))
                return Json(new { success = false, message = "Åtkomst nekad." });

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
                    teamResultSeriesCount = content.GetValue<int>("teamResultSeriesCount"),
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

            if (!await CanEditCompetitionAsync(request.CompetitionId))
                return Ok(new { success = false, message = "Åtkomst nekad." });

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

                // HOST PRESERVATION — must run before the guard below and before the per-type save
                // service applies the fields.
                //
                // A competition is hosted by a club (clubId) or by a krets (regionalFederation), and
                // that value decides the URL shape, who may administer it, and who the invoice payee
                // is. The edit modals post EVERY field, and their club/region dropdowns are populated
                // from GetClubsForCompetitionAdmin — which correctly refuses a plain competition
                // manager, since they may not re-host the competition. The result was that the two
                // selects rendered EMPTY and the save then posted regionalFederation="" and wiped the
                // hosting krets: verified 2026-08-05, a competition manager saving the Springskytte
                // edit modal turned region "Halland" into "". The URL guard below did not catch it
                // because competitionScope was still set. The competition was left with no host, so
                // even the krets's own admins — and the regional admin who created it — lost access
                // to it entirely.
                //
                // Rule: an EMPTY incoming host value never clears a host that exists. Switching hosts
                // still works, because a switch sets the other side (club -> krets or krets -> club),
                // and that non-empty value is honoured.
                bool clubIncomingEmpty = FieldPresentButEmpty(request.Fields, "clubId");
                bool regionIncomingEmpty = FieldPresentButEmpty(request.Fields, "regionalFederation");
                int existingClubId = content.GetValue<int?>("clubId") ?? 0;
                string existingRegion = (content.GetValue<string>("regionalFederation") ?? "").Trim();
                bool wouldClearBothHosts =
                    (clubIncomingEmpty || existingClubId <= 0) && (regionIncomingEmpty || string.IsNullOrWhiteSpace(existingRegion));
                if (wouldClearBothHosts && (existingClubId > 0 || !string.IsNullOrWhiteSpace(existingRegion)))
                {
                    if (clubIncomingEmpty && existingClubId > 0) request.Fields?.Remove("clubId");
                    if (regionIncomingEmpty && !string.IsNullOrWhiteSpace(existingRegion)) request.Fields?.Remove("regionalFederation");
                }

                // Soft URL-correctness guard: at least one of clubId / regionalFederation /
                // competitionScope must be set so CompetitionUrlProvider can produce a clean URL.
                // For fields not present in the request, fall back to the existing content value
                // (a partial-update client must not be able to leave the node in a no-URL state).
                int hostClubId = ReadFieldOrContentAsInt(request.Fields, "clubId", content);
                string hostRegFed = ReadFieldOrContentAsString(request.Fields, "regionalFederation", content);
                string hostScope = ReadFieldOrContentAsString(request.Fields, "competitionScope", content);
                if (hostClubId <= 0 && string.IsNullOrWhiteSpace(hostRegFed) && string.IsNullOrWhiteSpace(hostScope))
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Välj antingen ansvarig klubb, krets eller mästerskapstyp — annars går det inte att skapa en lättläst URL för tävlingen."
                    });
                }

                // Persist the shooting-range link here (type-agnostic): the per-type save
                // services map only their own fields and would drop "rangeId". Only act when
                // the field is actually present so a partial-update client can't clear it by
                // omission. The type-specific service re-loads + publishes the node afterwards,
                // so this saved value gets published with the rest. SetValue is a no-op if the
                // doctype lacks the (optional) rangeId property.
                if (request.Fields != null && request.Fields.ContainsKey("rangeId"))
                {
                    int newRangeId = ReadFieldOrContentAsInt(request.Fields, "rangeId", content);
                    content.SetValue("rangeId", newRangeId > 0 ? newRangeId : 0);
                    _contentService.Save(content);
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
        /// Read a field from the request Fields dict as an int; falls back to the content
        /// node's stored value when the key isn't in the request (partial-update safety).
        /// Handles JsonElement (System.Text.Json shape), boxed int, and string forms.
        /// </summary>
        /// <summary>
        /// True when the key IS in the request but carries nothing usable (null, empty, whitespace,
        /// or 0 for a numeric id). Distinguishes "the client deliberately cleared this" from "the
        /// client never sent it" — the two must not be treated alike for host fields.
        /// </summary>
        private static bool FieldPresentButEmpty(Dictionary<string, object>? fields, string key)
        {
            if (fields == null || !fields.TryGetValue(key, out var obj)) return false;   // absent
            if (obj == null) return true;
            if (obj is System.Text.Json.JsonElement je)
            {
                switch (je.ValueKind)
                {
                    case System.Text.Json.JsonValueKind.Null:
                    case System.Text.Json.JsonValueKind.Undefined:
                        return true;
                    case System.Text.Json.JsonValueKind.String:
                        var s = je.GetString() ?? "";
                        return string.IsNullOrWhiteSpace(s) || s.Trim() == "0";
                    case System.Text.Json.JsonValueKind.Number:
                        return je.TryGetInt32(out var n) && n == 0;
                    default:
                        return false;
                }
            }
            var raw = (obj.ToString() ?? "").Trim();
            return string.IsNullOrWhiteSpace(raw) || raw == "0";
        }

        private static int ReadFieldOrContentAsInt(Dictionary<string, object>? fields, string key, Umbraco.Cms.Core.Models.IContent content)
        {
            if (fields != null && fields.TryGetValue(key, out var obj) && obj != null)
            {
                if (obj is System.Text.Json.JsonElement je)
                {
                    if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
                    if (je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out var s)) return s;
                    // Field is present but null/empty → treat as cleared (return 0, do not fall back).
                    return 0;
                }
                if (obj is int direct) return direct;
                return int.TryParse(obj.ToString(), out var parsed) ? parsed : 0;
            }
            // Field absent from request → keep existing content value.
            return content.GetValue<int?>(key) ?? 0;
        }

        /// <summary>
        /// Read a field from the request Fields dict as a trimmed string; falls back to the
        /// content node's stored value when the key isn't in the request.
        /// </summary>
        private static string ReadFieldOrContentAsString(Dictionary<string, object>? fields, string key, Umbraco.Cms.Core.Models.IContent content)
        {
            if (fields != null && fields.TryGetValue(key, out var obj) && obj != null)
            {
                if (obj is System.Text.Json.JsonElement je)
                {
                    if (je.ValueKind == System.Text.Json.JsonValueKind.Null || je.ValueKind == System.Text.Json.JsonValueKind.Undefined) return string.Empty;
                    if (je.ValueKind == System.Text.Json.JsonValueKind.String) return (je.GetString() ?? string.Empty).Trim();
                    return je.ToString().Trim();
                }
                return (obj.ToString() ?? string.Empty).Trim();
            }
            return (content.GetValue<string>(key) ?? string.Empty).Trim();
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

        // ─────────────────────────────────────────────────────────────────────
        // Byt disciplin pa en befintlig tavling
        //
        // Varfor: competitionType satts i tavlingsguiden och gick INTE att andra
        // efterat — den lastes bara av redigeringsmodalen for att visa/dolja
        // avsnitt. Valde arrangoren fel gren var radera-och-skapa-om enda vagen,
        // vilket ar precis klagomalet posten handlar om.
        //
        // ⚠ NAR det ar sakert ar sjalva regeln. Varje disciplin har sin EGEN
        // resultattabell (se CompetitionResultTables) och sina egna klasser, sa ett
        // byte efter att folk anmalt sig eller efter att resultat matats in skulle
        // foraldralosa bada. Darfor: bara nar tavlingen ar orord.
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Far den har tavlingens disciplin bytas, och i sa fall varfor/varfor inte?
        /// Read-only. Klienten anvander svaret for att aktivera eller forklara.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCompetitionTypeChangeability(int competitionId)
        {
            try
            {
                if (!await CanEditCompetitionAsync(competitionId))
                    return Json(new { success = false, message = "Behorighet saknas." });

                var competition = _contentService.GetById(competitionId);
                if (competition == null || competition.ContentType.Alias != "competition")
                    return Json(new { success = false, message = "Tavlingen hittades inte." });

                var state = InspectTypeChange(competitionId, competition);
                return Json(new
                {
                    success = true,
                    currentType = state.CurrentType,
                    canChange = state.CanChange,
                    reason = state.Reason,
                    registrationCount = state.RegistrationCount,
                    resultCount = state.ResultCount,
                    options = HpskSite.Models.CompetitionTypes.All
                        .Select(t => new { id = t.Id, name = t.Name })
                        .ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetCompetitionTypeChangeability failed for {Id}", competitionId);
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        /// <summary>
        /// Byter disciplin. Kontrollerar sakerheten SERVERSIDAN igen — klientens
        /// bild kan vara sekunder gammal, och en anmalan som kom emellan far inte
        /// bli foraldralos av ett knapptryck.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangeCompetitionType([FromBody] ChangeCompetitionTypeRequest request)
        {
            try
            {
                if (request == null || request.CompetitionId <= 0)
                    return Json(new { success = false, message = "Ogiltig begaran." });

                if (!await CanEditCompetitionAsync(request.CompetitionId))
                    return Json(new { success = false, message = "Behorighet saknas." });

                var newType = (request.NewType ?? "").Trim();
                var known = HpskSite.Models.CompetitionTypes.All.FirstOrDefault(t =>
                    string.Equals(t.Id, newType, StringComparison.OrdinalIgnoreCase));
                if (known == null)
                    return Json(new { success = false, message = $"Okand gren: \"{newType}\"." });

                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null || competition.ContentType.Alias != "competition")
                    return Json(new { success = false, message = "Tavlingen hittades inte." });

                var state = InspectTypeChange(request.CompetitionId, competition);
                if (!state.CanChange)
                    return Json(new { success = false, message = state.Reason });

                if (string.Equals(state.CurrentType, known.Id, StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = true, message = "Grenen var redan " + known.Name + ".", changed = false });

                competition.SetValue("competitionType", known.Id);

                // ⚠ Klasserna tillhor grenen. Springskytte anvander vapen- och
                // aldersklasser, precisionsfamiljen sina egna — att lamna kvar den
                // gamla uppsattningen ger en tavling vars klasser inte finns i dess
                // gren. Nollstall dem, sa arrangoren far valja om i samma modal.
                competition.SetValue("shootingClassIds", "");

                var result = _contentService.Save(competition);
                if (!result.Success)
                    return Json(new { success = false, message = "Kunde inte spara grenbytet." });

                // Bara redan publicerade tavlingar publiceras om. Att publicera ett
                // utkast som sidoeffekt av ett grenbyte vore fel.
                if (competition.Published)
                {
                    _contentService.Publish(competition, Array.Empty<string>());
                }

                _logger.LogInformation(
                    "Competition {Id} type changed {From} -> {To} (registrations {Regs}, results {Res})",
                    request.CompetitionId, state.CurrentType, known.Id, state.RegistrationCount, state.ResultCount);

                return Json(new
                {
                    success = true,
                    changed = true,
                    newType = known.Id,
                    newTypeName = known.Name,
                    message = $"Grenen andrad till {known.Name}. Valj klasser for den nya grenen och spara."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ChangeCompetitionType failed");
                return Json(new { success = false, message = "Fel: " + ex.Message });
            }
        }

        private sealed class TypeChangeState
        {
            public string CurrentType { get; set; } = "";
            public bool CanChange { get; set; }
            public string Reason { get; set; } = "";
            public int RegistrationCount { get; set; }
            public int ResultCount { get; set; }
        }

        /// <summary>
        /// Sakerhetsregeln pa ETT stalle, last av bade lasningen och skrivningen.
        /// </summary>
        private TypeChangeState InspectTypeChange(int competitionId, Umbraco.Cms.Core.Models.IContent competition)
        {
            var state = new TypeChangeState
            {
                CurrentType = competition.GetValue<string>("competitionType") ?? ""
            };

            using var db = _databaseFactory.CreateDatabase();

            // ⚠ Anmalningar ar Umbraco-NODER som sparas OPUBLICERADE, sa den
            // publicerade cachen ser dem inte — de maste raknas i SQL. Samma sak som
            // gjorde att kolumnen "Anmalningar" visade 0 pa varje rad fore 2026-08-18.
            // De ligger under en registrationsHub som i sin tur ligger under tavlingen.
            state.RegistrationCount = db.ExecuteScalar<int>(@"
                SELECT COUNT(*)
                FROM umbracoNode reg
                JOIN umbracoContent c ON c.nodeId = reg.id
                JOIN umbracoNode hub ON hub.id = reg.parentId
                WHERE c.contentTypeId IN (SELECT nodeId FROM cmsContentType WHERE alias = 'competitionRegistration')
                  AND (reg.parentId = @0 OR hub.parentId = @0)", competitionId);

            // ⚠ Rakna i ALLA disciplintabeller, inte bara den nuvarande grenens. En
            // tavling som redan bytt gren en gang, eller som skapades som fel gren och
            // fick nagra rader, har kvar dem i en annan tabell — och de skulle bli
            // osynliga foraldralosa rader efter ytterligare ett byte.
            var tables = new[]
            {
                "PrecisionResultEntry", "MilsnabbResultEntry", "DuellResultEntry",
                "NationellHelmatchResultEntry", "MagnumPrecisionResultEntry",
                "SpringskytteResultEntry", "FaltskytteResultEntry",
                "StandardpistolResultEntry", "SportpistolResultEntry"
            };
            var total = 0;
            foreach (var t in tables)
            {
                try
                {
                    // Tabellen kan saknas i en miljo som inte kort alla migreringar.
                    // Da finns inga rader dar heller — hoppa over, avbryt inte.
                    var exists = db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM sys.tables WHERE name = @0", t);
                    if (exists == 0) continue;
                    total += db.ExecuteScalar<int>(
                        $"SELECT COUNT(*) FROM [{t}] WHERE CompetitionId = @0", competitionId);
                }
                catch (Exception ex)
                {
                    // En tabell vi inte kan lasa far inte tolkas som "inga resultat" —
                    // det ar precis det svaret som skulle tillata ett farligt byte.
                    _logger.LogWarning(ex, "Could not count results in {Table} for competition {Id}", t, competitionId);
                    state.CanChange = false;
                    state.Reason = "Kunde inte kontrollera om tavlingen har resultat. Grenbytet stoppas.";
                    return state;
                }
            }
            state.ResultCount = total;

            if (state.RegistrationCount > 0 && state.ResultCount > 0)
            {
                state.Reason = $"Tavlingen har {state.RegistrationCount} anmalningar och {state.ResultCount} inmatade resultat. "
                             + "Grenen kan bara bytas sa lange tavlingen ar orord — varje gren har egen resultattabell och egna klasser.";
            }
            else if (state.RegistrationCount > 0)
            {
                state.Reason = $"Tavlingen har {state.RegistrationCount} anmalningar. Ta bort dem forst, "
                             + "eller skapa en ny tavling — klasserna tillhor grenen och foljer inte med.";
            }
            else if (state.ResultCount > 0)
            {
                state.Reason = $"Tavlingen har {state.ResultCount} inmatade resultat. Grenen kan inte bytas, "
                             + "eftersom varje gren lagrar sina resultat i en egen tabell.";
            }
            else
            {
                state.CanChange = true;
                state.Reason = "Tavlingen ar orord — grenen kan bytas.";
            }

            return state;
        }

    }

    /// <summary>Begaran om att byta disciplin pa en befintlig tavling.</summary>
    public class ChangeCompetitionTypeRequest
    {
        public int CompetitionId { get; set; }
        public string NewType { get; set; } = "";
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
