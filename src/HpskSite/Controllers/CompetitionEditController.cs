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

        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
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
            Microsoft.Extensions.Logging.ILogger<CompetitionEditController> logger,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations,
            HpskSite.Services.Competitions.ICompetitionStore competitions)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _competitions = competitions;
            _registrations = registrations;
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
        private Task<bool> CanEditCompetitionAsync(int competitionId) =>
            _authorizationService.CanEditCompetitionAsync(competitionId, HpskSite.Services.AdminAuthorizationService.CompetitionRead.Saved);

        public class PreviewDirektplaceringRequest
        {
            public int CompetitionId { get; set; }
            /// <summary>The Egenbokning config as the modal would save it (JSON string).</summary>
            public string? Config { get; set; }
        }

        /// <summary>
        /// What switching Egenbokning on (with the config in the modal) would do to existing
        /// registrations and an existing start list. Read-only — the save applies it only when
        /// the client sends ApplyDirektplaceringAssignments after the organiser confirmed.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> PreviewDirektplacering([FromBody] PreviewDirektplaceringRequest request)
        {
            if (request == null || !await CanEditCompetitionAsync(request.CompetitionId))
                return Json(new { success = false, message = "Åtkomst nekad." });

            try
            {
                var competition = _competitions.GetSaved(request.CompetitionId);
                if (competition == null) return Json(new { success = false, message = "Tävlingen hittades inte." });

                var config = HpskSite.Models.DirektplaceringConfig.Parse(request.Config);
                if (config == null) return Json(new { success = true, needsConfirmation = false });

                var dp = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.DirektplaceringStartListService))
                    as HpskSite.Services.DirektplaceringStartListService;
                if (dp == null) return Json(new { success = false, message = "Tjänsten saknas." });

                var plan = dp.Plan(competition, config);
                return Json(new
                {
                    success = true,
                    needsConfirmation = plan.NeedsConfirmation,
                    hasStartList = plan.HasStartList,
                    startListIsManual = plan.StartListIsManual,
                    startListIsPublished = plan.StartListIsPublished,
                    droppedFromList = plan.DroppedFromList,
                    rows = plan.Rows.Select(r => new
                    {
                        name = r.MemberName,
                        shootingClass = HpskSite.Models.ShootingClasses.DisplayName(r.ShootingClass),
                        teamNumber = r.TeamNumber,
                        fromExistingList = r.FromExistingList
                    })
                });
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
            {
                _logger.LogError(ex, "PreviewDirektplacering failed for competition {CompetitionId}", request.CompetitionId);
                return Json(new { success = false, message = "Kunde inte förhandsgranska egenbokningen: " + ex.Message });
            }
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
                // Det sparade läget (redigeringen); tidpunkterna helst ur det publicerade (C3-planen S3).
                var content = _competitions.GetSaved(competitionId);
                if (content == null)
                {
                    return NotFound(new { success = false, message = $"Competition with ID {competitionId} not found" });
                }
                var published = _competitions.Get(competitionId);
                string? DateString(Func<HpskSite.Models.Competitions.CompetitionInfo, DateTime?> read, bool includeTime)
                {
                    var format = includeTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";
                    var pub = published == null ? null : read(published);
                    if (pub.HasValue && pub.Value != DateTime.MinValue) return pub.Value.ToString(format);
                    var raw = read(content);
                    return raw.HasValue && raw.Value != DateTime.MinValue ? raw.Value.ToString(format) : null;
                }

                // Check if parent is a series
                var isInSeries = content.SeriesId != null;
                var parent = isInSeries ? _contentService.GetById(content.SeriesId!.Value) : null;
                isInSeries = parent != null;

                var description = HpskSite.Extensions.RteHelper.ExtractMarkup(content.Description);

                var competitionData = new
                {
                    id = content.Id,
                    competitionName = content.CompetitionName ?? "",
                    description = description,
                    venue = content.Venue ?? "",
                    competitionDate = DateString(c => c.CompetitionDate, true),
                    competitionEndDate = DateString(c => c.CompetitionEndDate, false),
                    registrationOpenDate = DateString(c => c.RegistrationOpenDate, true),
                    registrationCloseDate = DateString(c => c.RegistrationCloseDate, true),
                    maxParticipants = content.MaxParticipants,
                    registrationFee = content.Fee(HpskSite.Models.Competitions.CompetitionFeeKind.Registration) ?? 0m,
                    juniorRegistrationFee = content.JuniorRegistrationFee ?? "0",
                    subCompetitionFee = content.SubCompetitionFee ?? "0",
                    subCompetitionFeeMode = content.SubCompetitionFeeMode ?? "perClass",
                    competitionDirector = content.CompetitionDirector ?? "",
                    contactEmail = content.ContactEmail ?? "",
                    contactPhone = content.ContactPhone ?? "",
                    numberOfSeriesOrStations = content.NumberOfSeriesOrStations,
                    numberOfFinalSeries = content.NumberOfFinalSeries,
                    allowDualCClass = content.AllowDualCClassRegistration,
                    showLiveResults = content.ShowLiveResults,
                    addToMenu = content.AddToMenu,
                    isActive = content.IsVisible,
                    isClubOnly = content.IsClubOnly,
                    clubId = content.ClubId,
                    regionalFederation = content.RegionalFederation ?? "",
                    swishNumber = content.SwishNumber ?? "",
                    competitionManagers = content.ManagerIds.ToArray(),
                    shootingClassIds = ShootingClassIdsString(content.ShootingClassIds),
                    competitionScope = content.CompetitionScope ?? "",
                    // Tom = ej bekräftad; dialogen föreslår då en nivå (_CompetitionLevelField).
                    competitionLevel = HpskSite.CompetitionTypes.Common.CompetitionLevel.Normalize(content.CompetitionLevel),
                    isAwardingStandardMedals = content.IsAwardingStandardMedals,
                    allowSelfReporting = content.AllowSelfReporting,
                    isExternal = content.IsExternal,
                    externalUrl = content.ExternalUrl ?? "",
                    externalRegistrationEmail = content.ExternalRegistrationEmail ?? "",
                    allowTeams = content.AllowTeams,
                    teamRegistrationFee = content.TeamRegistrationFee ?? "0",
                    // teamResultSeriesCount, patrolSize och patrolIntervalMinutes har aldrig funnits på dokumenttypen.
                    teamResultSeriesCount = 0,
                    allowStafett = content.AllowStafett,
                    stafettRegistrationFee = content.StafettRegistrationFee ?? "0",
                    seriesId = isInSeries ? parent!.Id : (int?)null,
                    seriesName = isInSeries ? parent!.Name : null,
                    competitionType = content.CompetitionType ?? "Precision",
                    // Fältskytte-specific fields
                    scoringMode = content.ScoringMode ?? "Normal",
                    stationConfig = content.StationConfig ?? "",
                    patrolSize = 0,
                    patrolIntervalMinutes = 0,
                    maxReshoots = content.MaxReshoots,
                    rollingStart = content.RollingStart ?? "",
                    faltskytteSelfServiceResults = content.FaltskytteSelfServiceResults,
                    subCompetitionName = content.SubCompetitionName ?? "",
                    // Direktplacering
                    direktplaceringConfig = content.DirektplaceringConfig ?? ""
                };

                Console.WriteLine($"Returning competition data for: {content.Name}");
                return Ok(new { success = true, data = competitionData });
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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
                    if (_competitions.GetSaved(competitionId.Value) == null) // C3-planen §5d
                    {
                        return NotFound(new { success = false, message = "Competition not found" });
                    }
                }

                // Get all shooting classes - same for all competitions
                var classes = HpskSite.Models.ShootingClasses.All;
                var classOptions = classes.Select(c => new { id = c.Id, name = c.Name, description = c.Description }).ToList();

                return Ok(new { success = true, data = classOptions });
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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
            // Bara sajtadmin lägger en tävling i den nationella menyn (Stefan 2026-10-13); för andra står värdet kvar.
            if (!await _authorizationService.IsCurrentUserAdminAsync()) request.Fields?.Remove("addToMenu");

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

                // Validate competition exists — det sparade läget ur lagringen (C3-planen §5d), inte noden.
                var content = _competitions.GetSaved(request.CompetitionId);
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
                int existingClubId = content.ClubId;
                string existingRegion = (content.RegionalFederation ?? "").Trim();
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

                // Tävlingens kategori (CompetitionLevel): ett okänt värde vägras, och kategorin får
                // inte säga emot "endast klubbens egna medlemmar", kräva en klubb som saknas eller
                // vara föreningstävling med standardmedaljer. Bara när fältet skickas — en äldre
                // klient som inte känner till kategorin ska kunna spara som förut.
                if (request.Fields != null && request.Fields.ContainsKey(HpskSite.CompetitionTypes.Common.CompetitionLevel.PropertyAlias))
                {
                    var levelRaw = ReadFieldOrContentAsString(request.Fields, HpskSite.CompetitionTypes.Common.CompetitionLevel.PropertyAlias, content);
                    if (!HpskSite.CompetitionTypes.Common.CompetitionLevel.IsValid(levelRaw))
                        return Ok(new { success = false, message = $"Okänd tävlingsnivå: \"{levelRaw}\"." });
                    var levelError = HpskSite.CompetitionTypes.Common.CompetitionLevel.ConsistencyError(
                        levelRaw,
                        ReadFieldOrContentAsBool(request.Fields, "isClubOnly", content),
                        hostClubId > 0,
                        ReadFieldOrContentAsBool(request.Fields, "isAwardingStandardMedals", content));
                    if (levelError != null)
                        return Ok(new { success = false, message = levelError });
                }

                // Finalomgång finns bara i precisionsskjutning (SHB C.3.6.1.1). Prövas mot det
                // SPARADE värdet när fältet inte skickas, så en tävling som redan bär felaktiga
                // finalserier inte kan sparas vidare utan att rättas.
                var finalsRefusal = HpskSite.CompetitionTypes.Common.CompetitionFinals.Refusal(
                    content.CompetitionType,
                    ReadFieldOrContentAsInt(request.Fields, "numberOfFinalSeries", content));
                if (finalsRefusal != null)
                    return Ok(new { success = false, message = finalsRefusal });

                // Banan (rangeId) sparas med resten genom fältpåsen (C3-planen S5b) — förut en egen
                // sparning före grenvalet. Bara när fältet skickas, som förut.

                // Route to type-specific save logic
                var result = await RouteToTypeSpecificSave(request, content);

                // Egenbokning switched on (or re-saved) over registrations that never booked a
                // skjutlag: the organiser has seen PreviewDirektplacering and confirmed, so seat
                // them now. After the save, because Apply reads the config the save just wrote.
                var saved = result?.GetType().GetProperty("success")?.GetValue(result) is true;
                if (request.ApplyDirektplaceringAssignments && saved)
                {
                    var er = new CompetitionEditResult
                    {
                        Success = true,
                        Message = result!.GetType().GetProperty("message")?.GetValue(result) as string ?? "",
                        Data = result.GetType().GetProperty("data")?.GetValue(result)
                    };
                    try
                    {
                        var dp = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.DirektplaceringStartListService))
                            as HpskSite.Services.DirektplaceringStartListService;
                        var applied = dp?.Apply(request.CompetitionId);
                        if (applied != null)
                        {
                            var placed = applied.Rows.Count(r => r.TeamNumber.HasValue);
                            var unplaced = applied.Rows.Count(r => !r.TeamNumber.HasValue);
                            var note = $" Egenbokning: {placed} anmälning{(placed == 1 ? "" : "ar")} fick skjutlag och startlistan byggdes om.";
                            if (unplaced > 0) note += $" {unplaced} fick inget skjutlag — det fanns ingen passande plats.";
                            er.Message = (er.Message ?? "") + note;
                        }
                    }
                    catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
                    {
                        // The competition IS saved; say that the seating did not happen instead of
                        // reporting the whole save as failed.
                        _logger.LogError(ex, "Egenbokning assignment failed on competition {CompetitionId}", request.CompetitionId);
                        er.Message = (er.Message ?? "") + " Tävlingen sparades, men skjutlagen kunde inte tilldelas — kontrollera Startlistor-fliken.";
                    }
                    var baseMessage = result!.GetType().GetProperty("message")?.GetValue(result) as string ?? "";
                    result = new { success = true, message = er.Message, data = er.Data, dpNote = (er.Message ?? "").Substring(Math.Min(baseMessage.Length, (er.Message ?? "").Length)).Trim() };
                }

                // Invalidate admin competition/series list caches so edits are reflected
                AppCaches.RuntimeCache.ClearByKey("admin_series_list");
                AppCaches.RuntimeCache.ClearByRegex("^admin_competitions_list_");
                HpskSite.Services.CompetitionMenuService.Invalidate(AppCaches);

                return Ok(result);
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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

        /// <summary>Det sparade värdet för ett fält (nodens alias) ur posten.</summary>
        private static object? ExistingValue(HpskSite.Models.Competitions.CompetitionInfo c, string alias) =>
            HpskSite.Models.Competitions.CompetitionFieldTable.Fields
                .FirstOrDefault(f => string.Equals(f.Alias, alias, StringComparison.OrdinalIgnoreCase))?.Get(c);

        private static int ReadFieldOrContentAsInt(Dictionary<string, object>? fields, string key, HpskSite.Models.Competitions.CompetitionInfo content)
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
            return HpskSite.Models.Competitions.CompetitionFieldValues.ReadInt(ExistingValue(content, key)) ?? 0;
        }

        /// <summary>
        /// Read a field from the request Fields dict as a trimmed string; falls back to the
        /// content node's stored value when the key isn't in the request.
        /// </summary>
        /// <summary>
        /// Read a field from the request Fields dict as a bool; falls back to the content node's
        /// stored value when the key isn't in the request. Accepts true/false and "true"/"false".
        /// </summary>
        private static bool ReadFieldOrContentAsBool(Dictionary<string, object>? fields, string key, HpskSite.Models.Competitions.CompetitionInfo content)
        {
            if (fields != null && fields.TryGetValue(key, out var obj) && obj != null)
            {
                if (obj is System.Text.Json.JsonElement je)
                {
                    if (je.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                    if (je.ValueKind == System.Text.Json.JsonValueKind.False) return false;
                    if (je.ValueKind == System.Text.Json.JsonValueKind.String) return bool.TryParse(je.GetString(), out var sb) && sb;
                    return false;
                }
                if (obj is bool b) return b;
                return bool.TryParse(obj.ToString(), out var pb) && pb;
            }
            return HpskSite.Models.Competitions.CompetitionFieldValues.ReadBool(ExistingValue(content, key)) ?? false;
        }

        private static string ReadFieldOrContentAsString(Dictionary<string, object>? fields, string key, HpskSite.Models.Competitions.CompetitionInfo content)
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
            return (ExistingValue(content, key)?.ToString() ?? string.Empty).Trim();
        }

        /// <summary>
        /// Extract shooting class IDs as comma-separated string from competition content.
        /// </summary>
        private static string ShootingClassIdsString(string? stored)
        {
            object? classIdsObj = stored;

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
            HpskSite.Models.Competitions.CompetitionInfo content)
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
            HpskSite.Models.Competitions.CompetitionInfo content)
        {
            try
            {
                var service = new PrecisionCompetitionEditService(_competitions, _logger);
                var result = await service.SaveCompetitionAsync(request.CompetitionId, request.Fields);
                
                return new
                {
                    success = result.Success,
                    message = result.Message,
                    errors = result.Errors,
                    data = result.Data
                };
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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

                // Läser bara — det sparade läget ur tävlingslagringen (C3-planen S3).
                var competition = _competitions.GetSaved(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tavlingen hittades inte." });

                var state = InspectTypeChange(competitionId, competition.CompetitionType ?? "");
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
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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

                var competition = _competitions.GetSaved(request.CompetitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tavlingen hittades inte." });

                var state = InspectTypeChange(request.CompetitionId, competition.CompetitionType ?? "");
                if (!state.CanChange)
                    return Json(new { success = false, message = state.Reason });

                if (string.Equals(state.CurrentType, known.Id, StringComparison.OrdinalIgnoreCase))
                    return Json(new { success = true, message = "Grenen var redan " + known.Name + ".", changed = false });

                // Genom lagringen (C3-planen S5b), som bara publicerar om en redan publicerad tävling.
                var result = _competitions.Update(request.CompetitionId, null, c =>
                {
                    c.CompetitionType = known.Id;

                    // Den nya grenen har kanske ingen finalomgång (SHB C.3.6.1.1). Nollställ då
                    // finalserierna i samma handling — kvar hade de gjort tävlingen osparbar.
                    if (!HpskSite.CompetitionTypes.Common.CompetitionFinals.Supports(known.Id) && c.NumberOfFinalSeries > 0)
                        c.NumberOfFinalSeries = 0;

                    // ⚠ Klasserna tillhor grenen. Springskytte anvander vapen- och
                    // aldersklasser, precisionsfamiljen sina egna — att lamna kvar den
                    // gamla uppsattningen ger en tavling vars klasser inte finns i dess
                    // gren. Nollstall dem, sa arrangoren far valja om i samma modal.
                    c.ShootingClassIds = null;
                });
                if (!result.Ok)
                    return Json(new { success = false, message = "Kunde inte spara grenbytet." });
                if (result.PublishProblem != null)
                    _logger.LogWarning("Competition {Id}: grenbytet sparades men publicerades inte: {Problem}", request.CompetitionId, result.PublishProblem);

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
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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
        private TypeChangeState InspectTypeChange(int competitionId, string currentType)
        {
            var state = new TypeChangeState
            {
                CurrentType = currentType
            };

            using var db = _databaseFactory.CreateDatabase();

            // Antalet via anmälningslagringen (sajtstrukturen fas 1a), som räknar alla anmälningar,
            // publicerade eller inte.
            state.RegistrationCount = _registrations.CountByCompetition(new[] { competitionId })
                .GetValueOrDefault(competitionId);

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
                catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
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

        /// <summary>
        /// The organiser confirmed PreviewDirektplacering: seat registrations without a skjutlag
        /// and rebuild the Egenbokning list after saving. Never set without that confirmation.
        /// </summary>
        public bool ApplyDirektplaceringAssignments { get; set; }

        public bool IsValid()
        {
            return CompetitionId > 0 && 
                   !string.IsNullOrEmpty(CompetitionType) && 
                   Fields != null && 
                   Fields.Any();
        }
    }
}
