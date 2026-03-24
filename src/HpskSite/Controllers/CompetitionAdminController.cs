using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Cms.Core.Security;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Common.SeriesCalculation;
using Umbraco.Cms.Core.IO;
using System.IO;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for competition administration operations.
    /// Handles CRUD operations for the admin competition list.
    /// </summary>
    public class CompetitionAdminController : SurfaceController
    {
        private readonly IContentService _contentService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMediaService _mediaService;
        private readonly MediaFileManager _mediaFileManager;
        private readonly AppCaches _appCaches;
        private readonly SeriesCalculationService _seriesCalculationService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        // Cache configuration
        private const string SeriesListCacheKey = "admin_series_list";
        private const string CompetitionsListCacheKey = "admin_competitions_list_{0}_{1}"; // year, includeCompleted
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        public CompetitionAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IContentService contentService,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService authorizationService,
            IMediaService mediaService,
            MediaFileManager mediaFileManager,
            SeriesCalculationService seriesCalculationService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _contentService = contentService;
            _memberManager = memberManager;
            _memberService = memberService;
            _authorizationService = authorizationService;
            _mediaService = mediaService;
            _mediaFileManager = mediaFileManager;
            _seriesCalculationService = seriesCalculationService;
            _appCaches = appCaches;
            _umbracoContextAccessor = umbracoContextAccessor;
        }


        /// <summary>
        /// Get all clubs for the club selector dropdown
        /// </summary>
        [HttpGet]
        public IActionResult GetClubsList()
        {
            try
            {
                var clubs = new List<object>();

                // Get all club content nodes from clubsPage
                if (UmbracoContext.Content != null)
                {
                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root != null)
                    {
                        var clubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (clubsHub != null)
                        {
                            var clubNodes = clubsHub.Children
                                .Where(c => c.ContentType.Alias == "club")
                                .OrderBy(c => c.Name)
                                .Select(c => new
                                {
                                    id = c.Id,
                                    name = c.Name,
                                    description = c.Value<string>("description") ?? "",
                                    city = c.Value<string>("city") ?? "",
                                    email = c.Value<string>("contactEmail") ?? ""
                                })
                                .ToList();

                            clubs.AddRange(clubNodes);
                        }
                    }
                }

                return Ok(new
                {
                    success = true,
                    data = clubs
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        /// <summary>
        /// Get clubs filtered by the current user's role for competition organizer selection.
        /// Site admins see all clubs, regional admins see clubs in their regions,
        /// club admins see their managed clubs, skjutledare see their clubs.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubsForCompetitionAdmin()
        {
            try
            {
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var managedClubIds = await _authorizationService.GetManagedClubIds();
                var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();

                // Determine role
                string role;
                if (isSiteAdmin)
                {
                    role = "admin";
                }
                else if ((await _authorizationService.GetManagedRegions()).Any() && !managedClubIds.Any(id => skjutledareClubIds.Contains(id) == false))
                {
                    // Check if user has regional admin groups (not just club admin groups that happen to include regional clubs)
                    role = "regionalAdmin";
                }
                else if (managedClubIds.Any())
                {
                    role = "clubAdmin";
                }
                else if (skjutledareClubIds.Any())
                {
                    role = "skjutledare";
                }
                else
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Refine role detection: check actual member roles for regional admin
                if (!isSiteAdmin)
                {
                    var managedRegions = await _authorizationService.GetManagedRegions();
                    if (managedRegions.Any())
                    {
                        role = "regionalAdmin";
                    }
                }

                // Union of managedClubIds + skjutledareClubIds = clubs where the user won't lose visibility
                var allManagedIds = new HashSet<int>(managedClubIds);
                foreach (var id in skjutledareClubIds)
                {
                    allManagedIds.Add(id);
                }

                // Get club details from published content
                var clubs = new List<object>();
                if (UmbracoContext.Content != null)
                {
                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root != null)
                    {
                        var clubNodes = new List<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();

                        // NEW STRUCTURE: Find clubs under regional pages
                        var regionalPages = root.Children.Where(c => c.ContentType.Alias == "regionalPage").ToList();
                        foreach (var regionalPage in regionalPages)
                        {
                            var clubsPage = regionalPage.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                            if (clubsPage != null)
                            {
                                clubNodes.AddRange(clubsPage.Children.Where(c => c.ContentType.Alias == "club"));
                            }
                        }

                        // BACKWARDS COMPATIBILITY: Also check root-level clubsPage
                        var rootClubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (rootClubsHub != null)
                        {
                            clubNodes.AddRange(rootClubsHub.Children.Where(c => c.ContentType.Alias == "club"));
                        }

                        // Filter to only clubs the user can manage (site admins get all)
                        var filteredClubs = isSiteAdmin
                            ? clubNodes
                            : clubNodes.Where(c => allManagedIds.Contains(c.Id)).ToList();

                        clubs = filteredClubs
                            .Select(c => new
                            {
                                id = c.Id,
                                name = c.Name ?? ""
                            })
                            .OrderBy(c => c.name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("sv-SE"), false))
                            .Cast<object>()
                            .ToList();
                    }
                }

                return Ok(new
                {
                    success = true,
                    role = role,
                    managedClubIds = allManagedIds.ToList(),
                    clubs = clubs
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        /// <summary>
        /// Get all competition types (hardcoded) - No auth required as it's just static data
        /// </summary>
        [HttpGet]
        public IActionResult GetCompetitionTypes()
        {
            try
            {
                var competitionTypes = Models.CompetitionTypes.All.Select(t => new
                {
                    id = t.Id,
                    name = t.Name,
                    description = t.Description
                }).ToList();

                return Ok(new
                {
                    success = true,
                    data = competitionTypes
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading competition types: " + ex.Message });
            }
        }

        /// <summary>
        /// Get all competitions with basic info for the admin list (OPTIMIZED)
        /// Supports server-side filtering by year, completed status, type, and region
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCompetitionsList(int? year = null, bool includeCompleted = false, string? type = null, string? region = null)
        {
            // --- AUTH: fetch member + roles ONCE (instead of ~12 redundant DB calls) ---
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
                return Ok(new { success = false, message = "Access denied" });

            var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (memberData == null)
                return Ok(new { success = false, message = "Access denied" });

            var memberRoles = _memberService.GetAllRoles(memberData.Id);
            bool isSiteAdmin = memberRoles.Contains("Administrators");

            var managedClubIds = new HashSet<int>();
            var managedRegions = new List<string>();
            var skjutledareClubIds = new HashSet<int>();

            if (!isSiteAdmin)
            {
                // Extract ClubAdmin club IDs from roles
                foreach (var role in memberRoles.Where(r => r.StartsWith("ClubAdmin_")))
                {
                    if (int.TryParse(role.Replace("ClubAdmin_", ""), out int clubId))
                        managedClubIds.Add(clubId);
                }

                // Extract RegionalAdmin regions from roles
                foreach (var role in memberRoles.Where(r => r.StartsWith("RegionalAdmin_")))
                {
                    managedRegions.Add(role.Replace("RegionalAdmin_", ""));
                }

                // If regional admin, add clubs in managed regions
                if (managedRegions.Any() && _umbracoContextAccessor.TryGetUmbracoContext(out var regionCtx) && regionCtx.Content != null)
                {
                    var regionRoot = regionCtx.Content.GetAtRoot().FirstOrDefault();
                    if (regionRoot != null)
                    {
                        foreach (var rp in regionRoot.Children.Where(c => c.ContentType.Alias == "regionalPage"))
                        {
                            var regionCode = rp.Value<string>("regionCode") ?? "";
                            if (managedRegions.Contains(regionCode))
                            {
                                var clubsPage = rp.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                                if (clubsPage != null)
                                {
                                    foreach (var club in clubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                                        managedClubIds.Add(club.Id);
                                }
                            }
                        }

                        var rootClubsPage = regionRoot.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (rootClubsPage != null)
                        {
                            foreach (var club in rootClubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                            {
                                var clubRegion = club.Value<string>("regionalFederation") ?? "";
                                if (managedRegions.Contains(clubRegion))
                                    managedClubIds.Add(club.Id);
                            }
                        }
                    }
                }

                // Extract Skjutledare club IDs from roles
                foreach (var role in memberRoles.Where(r => r.StartsWith("Skjutledare_")))
                {
                    if (int.TryParse(role.Replace("Skjutledare_", ""), out int clubId))
                        skjutledareClubIds.Add(clubId);
                }
            }

            bool isClubAdmin = managedClubIds.Any();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();
            bool isSkjutledare = skjutledareClubIds.Any();

            if (!isSiteAdmin && !isClubAdmin && !isRegionalAdmin && !isSkjutledare)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            // Determine effective region(s) for regional admin filtering
            string? effectiveRegion = region;
            List<string>? effectiveRegions = null;

            if (isRegionalAdmin && !isSiteAdmin)
            {
                if (!string.IsNullOrEmpty(region) && managedRegions.Contains(region))
                {
                    effectiveRegion = region;
                }
                else if (managedRegions.Count == 1)
                {
                    effectiveRegion = managedRegions.First();
                }
                else
                {
                    effectiveRegion = null;
                    effectiveRegions = managedRegions;
                }
            }

            try
            {
                var today = DateTime.Today;
                var filterYear = year ?? today.Year;

                // Check cache (include type and region in cache key)
                string? cacheKey = null;
                var cacheKeyType = type ?? "all";
                var cacheKeyRegion = region ?? "all";
                if (isSiteAdmin)
                {
                    cacheKey = string.Format(CompetitionsListCacheKey, filterYear, includeCompleted) + $"_{cacheKeyType}_{cacheKeyRegion}";
                    var cachedResult = _appCaches.RuntimeCache.Get(cacheKey);
                    if (cachedResult != null)
                    {
                        return Ok(cachedResult);
                    }
                }

                // --- Use published content cache (in-memory) instead of _contentService.GetPagedDescendants (DB) ---
                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext) || umbracoContext.Content == null)
                {
                    return Ok(new { success = false, message = "Content cache not available" });
                }

                var root = umbracoContext.Content.GetAtRoot().FirstOrDefault();
                if (root == null)
                {
                    return Ok(new { success = true, data = new List<object>() });
                }

                // Find competitions hub
                var competitionsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                if (competitionsHub == null)
                {
                    return Ok(new { success = true, data = new List<object>() });
                }

                // Collect all competitions from competitionsHub subtree (published cache)
                // Structure: competitionsHub → year folder (contentPage) or competitionSeries → competition
                var allCompetitions = competitionsHub.Descendants()
                    .Where(c => c.ContentType.Alias == "competition")
                    .ToList();

                // Build club -> region lookup from published cache (only if needed for region filtering)
                var clubRegionLookup = new Dictionary<int, string>();
                if (!string.IsNullOrEmpty(effectiveRegion) || (effectiveRegions != null && effectiveRegions.Any()))
                {
                    foreach (var rp in root.Children.Where(c => c.ContentType.Alias == "regionalPage"))
                    {
                        var clubsPage = rp.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (clubsPage != null)
                        {
                            foreach (var club in clubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                                clubRegionLookup[club.Id] = club.Value<string>("regionalFederation") ?? "";
                        }
                    }

                    var rootClubsPage = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    if (rootClubsPage != null)
                    {
                        foreach (var club in rootClubsPage.Children.Where(c => c.ContentType.Alias == "club"))
                        {
                            if (!clubRegionLookup.ContainsKey(club.Id))
                                clubRegionLookup[club.Id] = club.Value<string>("regionalFederation") ?? "";
                        }
                    }
                }

                // Apply server-side filters
                var filteredCompetitions = allCompetitions
                    .Where(comp => isSiteAdmin || isRegionalAdmin || managedClubIds.Contains(comp.Value<int?>("clubId") ?? 0) || skjutledareClubIds.Contains(comp.Value<int?>("clubId") ?? 0))
                    .Where(comp =>
                    {
                        var compDate = comp.Value<DateTime?>("competitionDate");

                        // Year filter
                        if (year.HasValue && compDate.HasValue && compDate.Value.Year != filterYear)
                            return false;

                        // Status filter - exclude completed unless requested
                        if (!includeCompleted && compDate.HasValue)
                        {
                            var compEndDate = comp.Value<DateTime?>("competitionEndDate");
                            var effectiveEnd = (compEndDate.HasValue && compEndDate.Value.Year > 1900) ? compEndDate.Value.Date : compDate.Value.Date;
                            var isCompleted = effectiveEnd < today;
                            if (isCompleted) return false;
                        }

                        // Type filter
                        if (!string.IsNullOrEmpty(type))
                        {
                            var compType = comp.Value<string>("competitionType") ?? "";
                            if (!compType.Equals(type, StringComparison.OrdinalIgnoreCase))
                                return false;
                        }

                        // Region filter
                        if (!string.IsNullOrEmpty(effectiveRegion))
                        {
                            var compClubId = comp.Value<int?>("clubId") ?? 0;
                            var compRegion = comp.Value<string>("regionalFederation") ?? "";

                            if (compClubId > 0)
                            {
                                if (!clubRegionLookup.TryGetValue(compClubId, out var clubRegion) ||
                                    !clubRegion.Equals(effectiveRegion, StringComparison.OrdinalIgnoreCase))
                                    return false;
                            }
                            else if (!string.IsNullOrEmpty(compRegion))
                            {
                                if (!compRegion.Equals(effectiveRegion, StringComparison.OrdinalIgnoreCase))
                                    return false;
                            }
                        }
                        else if (effectiveRegions != null && effectiveRegions.Any())
                        {
                            var compClubId = comp.Value<int?>("clubId") ?? 0;
                            var compRegion = comp.Value<string>("regionalFederation") ?? "";

                            if (compClubId > 0)
                            {
                                if (!clubRegionLookup.TryGetValue(compClubId, out var clubRegion) ||
                                    !effectiveRegions.Contains(clubRegion))
                                    return false;
                            }
                            else if (!string.IsNullOrEmpty(compRegion))
                            {
                                if (!effectiveRegions.Contains(compRegion))
                                    return false;
                            }
                        }

                        return true;
                    })
                    .ToList();

                // Build competition list — parent lookup via .Parent (no DB calls needed)
                var competitions = filteredCompetitions
                    .Select(comp =>
                    {
                        var parent = comp.Parent;
                        var isInSeries = parent != null && parent.ContentType.Alias == "competitionSeries";

                        var isActive = comp.Value<bool>("isActive");
                        var compDate = comp.Value<DateTime?>("competitionDate");
                        var compEndDate = comp.Value<DateTime?>("competitionEndDate");
                        var effectiveEndDate = (compEndDate.HasValue && compEndDate.Value.Year > 1900) ? compEndDate.Value.Date : compDate?.Date;

                        // Calculate status
                        string status;
                        if (!isActive)
                        {
                            status = "Draft";
                        }
                        else if (compDate.HasValue)
                        {
                            if (compDate.Value.Date > today)
                            {
                                status = "Scheduled";
                            }
                            else if (effectiveEndDate.HasValue && effectiveEndDate.Value >= today)
                            {
                                status = "Active";
                            }
                            else if (compDate.Value.Date >= today.AddDays(-7))
                            {
                                status = "Active";
                            }
                            else
                            {
                                status = "Completed";
                            }
                        }
                        else
                        {
                            status = "Scheduled";
                        }

                        // Count registrations from children (published cache)
                        var regCount = comp.Children.Count(c => c.ContentType.Alias == "competitionRegistration");

                        return new
                        {
                            id = comp.Id,
                            name = comp.Value<string>("competitionName") ?? comp.Name,
                            description = comp.Value<string>("description") ?? "",
                            type = comp.Value<string>("competitionType") ?? "Unknown",
                            startDate = compDate,
                            endDate = compEndDate,
                            registrationOpenDate = comp.Value<DateTime?>("registrationOpenDate"),
                            registrationCloseDate = comp.Value<DateTime?>("registrationCloseDate"),
                            isActive = isActive,
                            isClubOnly = comp.Value<bool>("isClubOnly"),
                            isExternal = comp.Value<bool>("isExternal"),
                            clubId = comp.Value<int?>("clubId") ?? 0,
                            registrationCount = regCount,
                            allowSelfReporting = comp.Value<bool>("allowSelfReporting"),
                            seriesId = isInSeries ? parent!.Id : (int?)null,
                            seriesName = isInSeries ? (parent!.Value<string>("seriesName") ?? parent.Name) : null,
                            status = status
                        };
                    })
                    .OrderByDescending(c => c.startDate ?? DateTime.MinValue)
                    .ToList();

                var result = new
                {
                    success = true,
                    data = competitions
                };

                // Cache the result for site admins
                if (isSiteAdmin && cacheKey != null)
                {
                    _appCaches.RuntimeCache.Insert(cacheKey, () => result, CacheDuration);
                }

                return Ok(result);
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading competitions: " + ex.Message });
            }
        }

        /// <summary>
        /// Get a single competition by ID for editing
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCompetition(int id)
        {
            // Check if user is site admin OR club admin OR skjutledare OR competition manager
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var managedClubIds = await _authorizationService.GetManagedClubIds();
            bool isCompetitionManager = await _authorizationService.IsCompetitionManager(id);
            var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();

            if (!isSiteAdmin && !managedClubIds.Any() && !isCompetitionManager && !skjutledareClubIds.Any())
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var competition = _contentService.GetById(id);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // Check authorization for this specific competition
                var competitionClubId = competition.GetValue<int?>("clubId") ?? 0;
                bool isClubAdmin = competitionClubId > 0 && managedClubIds.Contains(competitionClubId);
                bool isSkjutledare = competitionClubId > 0 && skjutledareClubIds.Contains(competitionClubId);

                if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Ok(new { success = false, message = "You don't have permission to view this competition" });
                }

                // Parse shootingClassIds from JSON array to regular array
                string[] shootingClassIds = Array.Empty<string>();
                var shootingClassIdsValue = competition.GetValue<string>("shootingClassIds");
                if (!string.IsNullOrEmpty(shootingClassIdsValue))
                {
                    try
                    {
                        if (shootingClassIdsValue.TrimStart().StartsWith("["))
                        {
                            shootingClassIds = System.Text.Json.JsonSerializer.Deserialize<string[]>(shootingClassIdsValue) ?? Array.Empty<string>();
                        }
                        else
                        {
                            // Fallback for CSV format
                            shootingClassIds = shootingClassIdsValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                        }
                    }
                    catch
                    {
                        shootingClassIds = Array.Empty<string>();
                    }
                }

                // Get parent series ID if competition is in a series
                int? seriesId = null;
                if (competition.ParentId > 0)
                {
                    var parent = _contentService.GetById(competition.ParentId);
                    if (parent != null && parent.ContentType.Alias == "competitionSeries")
                    {
                        seriesId = parent.Id;
                    }
                }

                // Format dates for Flatpickr (Y-m-d H:i or Y-m-d)
                string FormatDate(DateTime? date, bool includeTime = true)
                {
                    if (!date.HasValue) return "";
                    return includeTime ? date.Value.ToString("yyyy-MM-dd HH:mm") : date.Value.ToString("yyyy-MM-dd");
                }

                var competitionData = new
                {
                    id = competition.Id,
                    competitionName = competition.GetValue<string>("competitionName") ?? "",
                    competitionType = competition.GetValue<string>("competitionType") ?? "Precision",
                    description = competition.GetValue<string>("description") ?? "",
                    venue = competition.GetValue<string>("venue") ?? "",
                    competitionDate = FormatDate(competition.GetValue<DateTime?>("competitionDate"), true),
                    competitionEndDate = FormatDate(competition.GetValue<DateTime?>("competitionEndDate"), false),
                    registrationOpenDate = FormatDate(competition.GetValue<DateTime?>("registrationOpenDate"), true),
                    registrationCloseDate = FormatDate(competition.GetValue<DateTime?>("registrationCloseDate"), true),
                    numberOfSeriesOrStations = competition.GetValue<int>("numberOfSeriesOrStations"),
                    numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries"),
                    shootingClassIds = shootingClassIds,
                    externalUrl = competition.GetValue<string>("externalUrl") ?? "",
                    externalRegistrationEmail = competition.GetValue<string>("externalRegistrationEmail") ?? "",
                    isExternal = competition.GetValue<bool>("isExternal"),
                    allowSelfReporting = competition.GetValue<bool>("allowSelfReporting"),
                    showLiveResults = competition.GetValue<bool>("showLiveResults"),
                    isActive = competition.GetValue<bool>("isActive"),
                    allowDualCClass = competition.GetValue<bool>("allowDualCClass"),
                    addToMenu = competition.GetValue<bool>("addToMenu"),
                    isAwardingStandardMedals = competition.GetValue<bool>("isAwardingStandardMedals"),
                    isClubOnly = competition.GetValue<bool>("isClubOnly"),
                    maxParticipants = competition.GetValue<int>("maxParticipants"),
                    registrationFee = competition.GetValue<decimal>("registrationFee"),
                    competitionDirector = competition.GetValue<string>("competitionDirector") ?? "",
                    contactEmail = competition.GetValue<string>("contactEmail") ?? "",
                    contactPhone = competition.GetValue<string>("contactPhone") ?? "",
                    swishNumber = competition.GetValue<string>("swishNumber") ?? "",
                    allowTeams = competition.GetValue<bool>("allowTeams"),
                    teamRegistrationFee = competition.GetValue<string>("teamRegistrationFee") ?? "0",
                    allowStafett = competition.GetValue<bool>("allowStafett"),
                    stafettRegistrationFee = competition.GetValue<string>("stafettRegistrationFee") ?? "0",
                    competitionManagers = GetCompetitionManagerIds(competition),
                    competitionScope = competition.GetValue<string>("competitionScope") ?? "",
                    seriesId = seriesId,
                    clubId = competitionClubId,
                    regionalFederation = competition.GetValue<string>("regionalFederation") ?? ""
                };

                return Ok(new
                {
                    success = true,
                    competition = competitionData
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading competition: " + ex.Message });
            }
        }

        /// <summary>
        /// Create a new competition
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateCompetition([FromBody] CreateCompetitionRequest request)
        {
            // AUTHORIZATION: Site Admin OR Club Admin OR Skjutledare (for their club)
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

            // Check Club Admin / Skjutledare (if creating club competition)
            bool isClubAdmin = false;
            bool isSkjutledare = false;
            if (request.Fields != null && request.Fields.TryGetValue("clubId", out var clubIdObj))
            {
                int clubId = 0;

                // Handle JsonElement (from JSON deserialization)
                if (clubIdObj is System.Text.Json.JsonElement jsonElement)
                {
                    if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        clubId = jsonElement.GetInt32();
                    }
                    else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        int.TryParse(jsonElement.GetString(), out clubId);
                    }
                }
                // Handle direct int
                else if (clubIdObj is int directInt)
                {
                    clubId = directInt;
                }
                // Handle string
                else if (int.TryParse(clubIdObj?.ToString(), out int parsedClubId))
                {
                    clubId = parsedClubId;
                }

                if (clubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(clubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(clubId);
                }
            }

            // Check Regional Admin
            bool isRegionalAdmin = false;
            if (!isSiteAdmin && !isClubAdmin && !isSkjutledare)
            {
                var managedRegions = await _authorizationService.GetManagedRegions();
                isRegionalAdmin = managedRegions.Any();
            }

            // Allow if Site Admin OR Club Admin OR Skjutledare OR Regional Admin
            if (!isSiteAdmin && !isClubAdmin && !isSkjutledare && !isRegionalAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            if (!ModelState.IsValid)
            {
                return Ok(new
                {
                    success = false,
                    message = "Invalid request data",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            try
            {
                if (request.Fields == null || request.Fields.Count == 0)
                {
                    return Ok(new { success = false, message = "No competition data provided" });
                }

                // Extract competition name from fields
                if (!request.Fields.TryGetValue("competitionName", out var nameObj) || nameObj == null || string.IsNullOrEmpty(nameObj.ToString()))
                {
                    return Ok(new { success = false, message = "Competition name is required" });
                }

                string competitionName = nameObj.ToString()!;

                if (!request.Fields.TryGetValue("competitionType", out var typeIdObj) || typeIdObj == null)
                {
                    return Ok(new { success = false, message = "Competition type is required" });
                }

                // Get the competition type ID (now a string) and validate it exists
                string competitionTypeId = typeIdObj.ToString()!;

                var competitionType = Models.CompetitionTypes.GetById(competitionTypeId);
                if (competitionType == null)
                {
                    return Ok(new { success = false, message = $"Competition type '{competitionTypeId}' not found" });
                }

                // Extract competition date to determine year folder
                DateTime competitionDate = DateTime.Now;
                if (request.Fields.TryGetValue("competitionDate", out var dateObj) && dateObj != null)
                {
                    if (DateTime.TryParse(dateObj.ToString(), out DateTime parsedDate))
                    {
                        competitionDate = parsedDate;
                    }
                }

                // Check if seriesId is provided
                int? seriesId = null;
                if (request.Fields.TryGetValue("seriesId", out var seriesIdObj) && seriesIdObj != null)
                {
                    var seriesIdStr = seriesIdObj.ToString();
                    if (!string.IsNullOrWhiteSpace(seriesIdStr) && int.TryParse(seriesIdStr, out int parsedSeriesId))
                    {
                        seriesId = parsedSeriesId;
                    }
                }

                int parentId;

                // If seriesId is provided, use series as parent
                if (seriesId.HasValue)
                {
                    var series = _contentService.GetById(seriesId.Value);
                    if (series == null)
                    {
                        return Ok(new { success = false, message = "Selected series not found" });
                    }
                    parentId = series.Id;
                }
                else
                {
                    // Otherwise, find or create year folder (original logic)
                    var rootContent = _contentService.GetRootContent().FirstOrDefault();
                    if (rootContent == null)
                    {
                        return Ok(new { success = false, message = "Root content not found" });
                    }

                    // Find "Competitions" folder under root (homepage)
                    var competitionsFolder = GetFlatDescendants(rootContent)
                        .FirstOrDefault(c => c.Name.Equals("Competitions", StringComparison.OrdinalIgnoreCase)
                                          || c.ContentType.Alias == "competitionsHub");

                    if (competitionsFolder == null)
                    {
                        return Ok(new { success = false, message = "Competitions folder not found. Please create it in Umbraco at /homepage/competitions/" });
                    }

                    // Find or create year folder
                    string yearFolderName = competitionDate.Year.ToString();
                    var yearFolder = _contentService.GetPagedChildren(competitionsFolder.Id, 0, int.MaxValue, out var totalRecords)
                        .FirstOrDefault(c => c.Name == yearFolderName);

                    if (yearFolder == null)
                    {
                        // Create year folder (use contentPage or similar basic document type)
                        yearFolder = _contentService.Create(yearFolderName, competitionsFolder.Id, "contentPage");
                        var saveYearResult = _contentService.Save(yearFolder);
                        if (!saveYearResult.Success)
                        {
                            return Ok(new { success = false, message = "Failed to create year folder: " + yearFolderName });
                        }
                        _contentService.Publish(yearFolder, Array.Empty<string>());
                    }

                    parentId = yearFolder.Id;
                }

                // Create new competition under the determined parent (series or year folder)
                var newCompetition = _contentService.Create(competitionName, parentId, "competition");

                if (newCompetition == null)
                {
                    return Ok(new { success = false, message = "Failed to create competition content" });
                }

                // Set competition type as a string property
                newCompetition.SetValue("competitionType", competitionTypeId);

                // Set competitionName as a property as well (in addition to the content name)
                newCompetition.SetValue("competitionName", competitionName);

                // Set all other properties from fields
                foreach (var field in request.Fields)
                {
                    try
                    {
                        // Skip fields already handled or not content properties
                        if (field.Key == "competitionName" || field.Key == "competitionType" || field.Key == "seriesId")
                            continue;

                        var value = field.Value;

                        // Convert value to appropriate type based on field name
                        if (field.Key.Contains("Date") && value != null)
                        {
                            if (DateTime.TryParse(value.ToString(), out DateTime dateValue))
                            {
                                value = dateValue;
                            }
                        }
                        else if ((field.Key == "registrationFee" || field.Key == "teamRegistrationFee" || field.Key == "stafettRegistrationFee") && value != null)
                        {
                            // registrationFee must be stored as decimal (not int) for Model.Value<decimal?> to work
                            if (value is System.Text.Json.JsonElement jsonElementDec)
                            {
                                if (jsonElementDec.ValueKind == System.Text.Json.JsonValueKind.Number)
                                {
                                    value = jsonElementDec.GetDecimal();
                                }
                                else if (jsonElementDec.ValueKind == System.Text.Json.JsonValueKind.String &&
                                         decimal.TryParse(jsonElementDec.GetString(), out decimal parsedDec))
                                {
                                    value = parsedDec;
                                }
                            }
                            else if (decimal.TryParse(value.ToString(), out decimal decValue))
                            {
                                value = decValue;
                            }
                        }
                        else if ((field.Key == "maxParticipants" || field.Key == "numberOfSeriesOrStations" ||
                                  field.Key == "numberOfFinalSeries" || field.Key == "clubId") && value != null)
                        {
                            // Handle JsonElement numbers
                            if (value is System.Text.Json.JsonElement jsonElement)
                            {
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                                {
                                    value = jsonElement.GetInt32();
                                }
                                else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                                         int.TryParse(jsonElement.GetString(), out int parsedValue))
                                {
                                    value = parsedValue;
                                }
                            }
                            else if (int.TryParse(value.ToString(), out int intValue))
                            {
                                value = intValue;
                            }
                        }
                        else if ((field.Key == "showLiveResults" || field.Key == "addToMenu" ||
                                  field.Key == "allowDualCClass" || field.Key == "isActive" || field.Key == "isClubOnly" ||
                                  field.Key == "allowSelfReporting" || field.Key == "allowTeams" || field.Key == "allowStafett" ||
                                  field.Key == "isAwardingStandardMedals") && value != null)
                        {
                            // Handle JsonElement booleans
                            if (value is System.Text.Json.JsonElement jsonElement)
                            {
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.True)
                                {
                                    value = true;
                                }
                                else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.False)
                                {
                                    value = false;
                                }
                                else if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                                         bool.TryParse(jsonElement.GetString(), out bool parsedValue))
                                {
                                    value = parsedValue;
                                }
                            }
                            else if (bool.TryParse(value.ToString(), out bool boolValue))
                            {
                                value = boolValue;
                            }
                        }
                        else if (field.Key == "shootingClassIds" && value != null)
                        {
                            // Convert to JSON array string for storage
                            if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
                            {
                                // Split comma-separated values and serialize to JSON array
                                var classIds = stringValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                                value = System.Text.Json.JsonSerializer.Serialize(classIds);
                            }
                            else if (value is System.Text.Json.JsonElement jsonElement)
                            {
                                // Handle JSON array from frontend
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    var classIds = jsonElement.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                                    value = System.Text.Json.JsonSerializer.Serialize(classIds);
                                }
                            }
                        }

                        if (value != null)
                        {
                            newCompetition.SetValue(field.Key, value);
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log but continue with other properties
                        Console.WriteLine($"Error setting property {field.Key}: {ex.Message}");
                    }
                }

                // Ensure new competitions are active by default
                if (!request.Fields.ContainsKey("isActive"))
                {
                    newCompetition.SetValue("isActive", true);
                }

                var saveResult = _contentService.Save(newCompetition);
                if (!saveResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to save competition: " + string.Join(", ", saveResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Publish the competition ("*" = publish invariant content)
                var publishResult = _contentService.Publish(newCompetition, new[] { "*" });
                if (!publishResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Competition saved but failed to publish: " + string.Join(", ", publishResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                // Return full competition data so frontend can add to table without reload
                return Ok(new
                {
                    success = true,
                    message = "Competition created successfully",
                    data = new
                    {
                        id = newCompetition.Id,
                        name = newCompetition.Name,
                        type = competitionTypeId,
                        startDate = newCompetition.GetValue<DateTime?>("competitionDate"),
                        status = GetCompetitionStatus(newCompetition),
                        registrationCount = 0 // New competition has no registrations
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating competition: " + ex.Message });
            }
        }

        /// <summary>
        /// Create a new external competition advertisement
        /// Simplified endpoint for external competitions (sets isExternal=true automatically)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateAdvertisement()
        {
            // Authorization check - site admins, club admins, and skjutledare can create advertisements
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var managedClubIds = await _authorizationService.GetManagedClubIds();
            bool isClubAdmin = managedClubIds.Any();
            var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();
            bool isSkjutledare = skjutledareClubIds.Any();

            if (!isSiteAdmin && !isClubAdmin && !isSkjutledare)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                // Parse fields from form data (supports file upload)
                var fieldsJson = Request.Form["fields"];
                if (string.IsNullOrEmpty(fieldsJson))
                {
                    return Ok(new { success = false, message = "No competition data provided" });
                }

                var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(fieldsJson.ToString());
                if (fields == null || fields.Count == 0)
                {
                    return Ok(new { success = false, message = "Invalid competition data format" });
                }

                // Extract competition name
                if (!fields.TryGetValue("competitionName", out var nameObj) || nameObj == null || string.IsNullOrEmpty(nameObj.ToString()))
                {
                    return Ok(new { success = false, message = "Competition name is required" });
                }

                string competitionName = nameObj.ToString()!;

                // Extract competition type (default to "Precision" if not provided)
                string competitionTypeId = "Precision";
                if (fields.TryGetValue("competitionType", out var typeObj) && typeObj != null && !string.IsNullOrEmpty(typeObj.ToString()))
                {
                    competitionTypeId = typeObj.ToString()!;
                }

                // Extract competition date to determine year folder
                DateTime competitionDate = DateTime.Now;
                if (fields.TryGetValue("competitionDate", out var dateObj) && dateObj != null)
                {
                    if (DateTime.TryParse(dateObj.ToString(), out DateTime parsedDate))
                    {
                        competitionDate = parsedDate;
                    }
                }

                // Check if seriesId is provided
                int? seriesId = null;
                if (fields.TryGetValue("seriesId", out var seriesIdObj) && seriesIdObj != null)
                {
                    var seriesIdStr = seriesIdObj.ToString();
                    if (!string.IsNullOrWhiteSpace(seriesIdStr) && int.TryParse(seriesIdStr, out int parsedSeriesId))
                    {
                        seriesId = parsedSeriesId;
                    }
                }

                int parentId;

                // Determine parent (series or year folder)
                if (seriesId.HasValue)
                {
                    var series = _contentService.GetById(seriesId.Value);
                    if (series == null)
                    {
                        return Ok(new { success = false, message = "Selected series not found" });
                    }
                    parentId = series.Id;
                }
                else
                {
                    // Find or create year folder
                    var rootContent = _contentService.GetRootContent().FirstOrDefault();
                    if (rootContent == null)
                    {
                        return Ok(new { success = false, message = "Root content not found" });
                    }

                    var competitionsFolder = GetFlatDescendants(rootContent)
                        .FirstOrDefault(c => c.Name.Equals("Competitions", StringComparison.OrdinalIgnoreCase)
                                          || c.ContentType.Alias == "competitionsHub");

                    if (competitionsFolder == null)
                    {
                        return Ok(new { success = false, message = "Competitions folder not found" });
                    }

                    string yearFolderName = competitionDate.Year.ToString();
                    var yearFolder = _contentService.GetPagedChildren(competitionsFolder.Id, 0, int.MaxValue, out var totalRecords)
                        .FirstOrDefault(c => c.Name == yearFolderName);

                    if (yearFolder == null)
                    {
                        yearFolder = _contentService.Create(yearFolderName, competitionsFolder.Id, "contentPage");
                        var saveYearResult = _contentService.Save(yearFolder);
                        if (!saveYearResult.Success)
                        {
                            return Ok(new { success = false, message = "Failed to create year folder: " + yearFolderName });
                        }
                        _contentService.Publish(yearFolder, Array.Empty<string>());
                    }

                    parentId = yearFolder.Id;
                }

                // Create new competition
                var newCompetition = _contentService.Create(competitionName, parentId, "competition");
                if (newCompetition == null)
                {
                    return Ok(new { success = false, message = "Failed to create competition content" });
                }

                // Set competition type
                newCompetition.SetValue("competitionType", competitionTypeId);
                newCompetition.SetValue("competitionName", competitionName);

                // CRITICAL: Set external competition flags
                newCompetition.SetValue("isExternal", true);
                newCompetition.SetValue("isActive", true);
                newCompetition.SetValue("isClubOnly", false);

                // Set series/final series fields with defaults
                int numberOfSeries = 6; // Default
                if (fields.TryGetValue("numberOfSeriesOrStations", out var seriesObj) && seriesObj != null)
                {
                    if (int.TryParse(seriesObj.ToString(), out int parsedSeries) && parsedSeries > 0)
                    {
                        numberOfSeries = parsedSeries;
                    }
                }
                newCompetition.SetValue("numberOfSeriesOrStations", numberOfSeries);

                int numberOfFinalSeries = 0; // Default
                if (fields.TryGetValue("numberOfFinalSeries", out var finalObj) && finalObj != null)
                {
                    if (int.TryParse(finalObj.ToString(), out int parsedFinal) && parsedFinal >= 0)
                    {
                        numberOfFinalSeries = parsedFinal;
                    }
                }
                newCompetition.SetValue("numberOfFinalSeries", numberOfFinalSeries);

                // Handle clubId with proper type conversion
                if (fields.TryGetValue("clubId", out var clubIdObj) && clubIdObj != null)
                {
                    int clubId = 0;
                    if (clubIdObj is System.Text.Json.JsonElement jsonClubElement)
                    {
                        if (jsonClubElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            clubId = jsonClubElement.GetInt32();
                        }
                        else if (jsonClubElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                                 int.TryParse(jsonClubElement.GetString(), out int parsed))
                        {
                            clubId = parsed;
                        }
                    }
                    else if (int.TryParse(clubIdObj.ToString(), out int parsedClubId))
                    {
                        clubId = parsedClubId;
                    }

                    if (clubId > 0)
                    {
                        newCompetition.SetValue("clubId", clubId);
                    }
                }

                // Set all other properties from fields
                foreach (var field in fields)
                {
                    try
                    {
                        // Skip fields already handled or special fields
                        if (field.Key == "competitionName" || field.Key == "competitionType" ||
                            field.Key == "isExternal" || field.Key == "isActive" || field.Key == "isClubOnly" ||
                            field.Key == "clubId" || field.Key == "invitationFile") // Skip file upload field - handle separately if needed
                            continue;

                        var value = field.Value;

                        // Convert value to appropriate type
                        if (field.Key.Contains("Date") && value != null)
                        {
                            if (DateTime.TryParse(value.ToString(), out DateTime dateValue))
                            {
                                value = dateValue;
                            }
                        }
                        else if (field.Key == "shootingClassIds" && value != null)
                        {
                            // Convert to JSON array string for storage
                            if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
                            {
                                var classIds = stringValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                                value = System.Text.Json.JsonSerializer.Serialize(classIds);
                            }
                            else if (value is System.Text.Json.JsonElement jsonElement)
                            {
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    var classIds = jsonElement.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                                    value = System.Text.Json.JsonSerializer.Serialize(classIds);
                                }
                            }
                        }

                        if (value != null)
                        {
                            newCompetition.SetValue(field.Key, value);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error setting property {field.Key}: {ex.Message}");
                    }
                }

                // TODO: Handle file upload for invitation
                // File uploads for new competitions via this endpoint are not supported yet
                // Users can add invitation files via the Edit modal after creation
                // This keeps the CreateAdvertisement endpoint simpler and avoids media API complexity

                // Save and publish
                var saveResult = _contentService.Save(newCompetition);
                if (!saveResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to save advertisement: " + string.Join(", ", saveResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                var publishResult = _contentService.Publish(newCompetition, Array.Empty<string>());
                if (!publishResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Advertisement saved but failed to publish: " + string.Join(", ", publishResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate cache so the new competition shows up immediately
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Competition advertisement created successfully",
                    data = new
                    {
                        id = newCompetition.Id,
                        name = newCompetition.Name,
                        type = competitionTypeId,
                        startDate = newCompetition.GetValue<DateTime?>("competitionDate"),
                        isExternal = true
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating advertisement: " + ex.Message });
            }
        }

        /// <summary>
        /// Save/update external competition advertisement
        /// Simplified save for external competitions only
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAdvertisement()
        {
            try
            {
                // Parse fields from form data
                var fieldsJson = Request.Form["fields"];
                if (string.IsNullOrEmpty(fieldsJson))
                {
                    return Ok(new { success = false, message = "No competition data provided" });
                }

                var fields = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(fieldsJson.ToString());
                if (fields == null || fields.Count == 0)
                {
                    return Ok(new { success = false, message = "Invalid competition data format" });
                }

                // Extract competition ID
                if (!fields.TryGetValue("competitionId", out var idObj) || !int.TryParse(idObj?.ToString(), out int competitionId))
                {
                    return Ok(new { success = false, message = "Competition ID is required" });
                }

                // Get existing competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // Validate isExternal flag (ensure we're only editing external competitions)
                bool isExternal = competition.GetValue<bool>("isExternal");
                if (!isExternal)
                {
                    return Ok(new { success = false, message = "This competition is not external. Use internal edit endpoint." });
                }

                // AUTHORIZATION: Site Admin OR Competition Manager OR Club Admin OR Skjutledare
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);

                // Check Club Admin / Skjutledare (if competition belongs to a club)
                bool isClubAdmin = false;
                bool isSkjutledare = false;
                var competitionClubId = competition.GetValue<int?>("clubId") ?? 0;
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(competitionClubId);
                }

                if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Ok(new { success = false, message = "You don't have permission to edit this competition" });
                }

                // Validate required fields
                if (!fields.TryGetValue("competitionName", out var nameObj) || string.IsNullOrWhiteSpace(nameObj?.ToString()))
                {
                    return Ok(new { success = false, message = "Competition name is required" });
                }

                if (!fields.TryGetValue("venue", out var venueObj) || string.IsNullOrWhiteSpace(venueObj?.ToString()))
                {
                    return Ok(new { success = false, message = "Venue is required" });
                }

                if (!fields.TryGetValue("competitionDate", out var compDateObj) || compDateObj == null)
                {
                    return Ok(new { success = false, message = "Competition date is required" });
                }

                if (!fields.TryGetValue("registrationOpenDate", out var regOpenObj) || regOpenObj == null)
                {
                    return Ok(new { success = false, message = "Registration open date is required" });
                }

                if (!fields.TryGetValue("registrationCloseDate", out var regCloseObj) || regCloseObj == null)
                {
                    return Ok(new { success = false, message = "Registration close date is required" });
                }

                // Extract external fields (optional)
                string externalUrl = fields.TryGetValue("externalUrl", out var urlObj) ? urlObj?.ToString() ?? "" : "";
                string externalEmail = fields.TryGetValue("externalRegistrationEmail", out var emailObj) ? emailObj?.ToString() ?? "" : "";

                // Validate series count
                if (fields.TryGetValue("numberOfSeriesOrStations", out var seriesObj))
                {
                    if (int.TryParse(seriesObj?.ToString(), out int seriesCount) && seriesCount < 1)
                    {
                        return Ok(new { success = false, message = "Number of series must be at least 1" });
                    }
                }

                // Update competition name
                string competitionName = nameObj.ToString()!;
                competition.Name = competitionName;
                competition.SetValue("competitionName", competitionName);

                // Explicitly handle numberOfSeriesOrStations and numberOfFinalSeries with proper type conversion
                int numberOfSeries = 6; // Default
                if (fields.TryGetValue("numberOfSeriesOrStations", out var seriesObjValue) && seriesObjValue != null)
                {
                    // Handle both direct int and JsonElement
                    if (seriesObjValue is System.Text.Json.JsonElement jsonSeriesElement)
                    {
                        if (jsonSeriesElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            numberOfSeries = jsonSeriesElement.GetInt32();
                        }
                        else if (jsonSeriesElement.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(jsonSeriesElement.GetString(), out int parsed))
                        {
                            numberOfSeries = parsed;
                        }
                    }
                    else if (int.TryParse(seriesObjValue.ToString(), out int parsedSeries) && parsedSeries > 0)
                    {
                        numberOfSeries = parsedSeries;
                    }
                }
                competition.SetValue("numberOfSeriesOrStations", numberOfSeries);

                int numberOfFinalSeries = 0; // Default
                if (fields.TryGetValue("numberOfFinalSeries", out var finalObjValue) && finalObjValue != null)
                {
                    // Handle both direct int and JsonElement
                    if (finalObjValue is System.Text.Json.JsonElement jsonFinalElement)
                    {
                        if (jsonFinalElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            numberOfFinalSeries = jsonFinalElement.GetInt32();
                        }
                        else if (jsonFinalElement.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(jsonFinalElement.GetString(), out int parsed))
                        {
                            numberOfFinalSeries = parsed;
                        }
                    }
                    else if (int.TryParse(finalObjValue.ToString(), out int parsedFinal) && parsedFinal >= 0)
                    {
                        numberOfFinalSeries = parsedFinal;
                    }
                }
                competition.SetValue("numberOfFinalSeries", numberOfFinalSeries);

                // Handle clubId with proper type conversion
                if (fields.TryGetValue("clubId", out var clubIdObjValue) && clubIdObjValue != null)
                {
                    int clubIdValue = 0;
                    if (clubIdObjValue is System.Text.Json.JsonElement jsonClubElement)
                    {
                        if (jsonClubElement.ValueKind == System.Text.Json.JsonValueKind.Number)
                        {
                            clubIdValue = jsonClubElement.GetInt32();
                        }
                        else if (jsonClubElement.ValueKind == System.Text.Json.JsonValueKind.String &&
                                 int.TryParse(jsonClubElement.GetString(), out int parsed))
                        {
                            clubIdValue = parsed;
                        }
                    }
                    else if (int.TryParse(clubIdObjValue.ToString(), out int parsedClubId))
                    {
                        clubIdValue = parsedClubId;
                    }

                    // Set clubId (allow 0 to clear the club)
                    competition.SetValue("clubId", clubIdValue);
                }

                // Update all properties from fields
                foreach (var field in fields)
                {
                    try
                    {
                        // Skip special fields already handled
                        if (field.Key == "competitionId" || field.Key == "invitationFile" ||
                            field.Key == "competitionName" || field.Key == "numberOfSeriesOrStations" ||
                            field.Key == "numberOfFinalSeries" || field.Key == "clubId")
                            continue;

                        var value = field.Value;

                        // Convert dates
                        if (field.Key.Contains("Date") && value != null)
                        {
                            if (DateTime.TryParse(value.ToString(), out DateTime dateValue))
                            {
                                value = dateValue;
                            }
                        }
                        // Convert shooting class IDs to JSON array
                        else if (field.Key == "shootingClassIds" && value != null)
                        {
                            if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
                            {
                                var classIds = stringValue.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                                value = System.Text.Json.JsonSerializer.Serialize(classIds);
                            }
                            else if (value is System.Text.Json.JsonElement jsonElement)
                            {
                                if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
                                {
                                    var classIds = jsonElement.EnumerateArray().Select(e => e.GetString()).Where(s => !string.IsNullOrEmpty(s)).ToArray();
                                    value = System.Text.Json.JsonSerializer.Serialize(classIds);
                                }
                            }
                        }

                        if (value != null)
                        {
                            competition.SetValue(field.Key, value);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error setting property {field.Key}: {ex.Message}");
                    }
                }

                // Save and publish
                var saveResult = _contentService.Save(competition);
                if (!saveResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to save advertisement: " + string.Join(", ", saveResult.EventMessages?.GetAll().Select(e => e.Message) ?? new List<string>())
                    });
                }

                var publishResult = _contentService.Publish(competition, Array.Empty<string>());
                if (!publishResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Advertisement saved but failed to publish: " + string.Join(", ", publishResult.EventMessages?.GetAll().Select(e => e.Message) ?? new List<string>())
                    });
                }

                // Invalidate cache so updates show up immediately
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Competition advertisement updated successfully",
                    data = new
                    {
                        id = competition.Id,
                        name = competition.GetValue<string>("competitionName") ?? competition.Name,
                        type = competition.GetValue<string>("competitionType"),
                        startDate = competition.GetValue<DateTime?>("competitionDate"),
                        isExternal = true
                    }
                });
            }
            catch (Exception ex)
            {
                //_logger.LogError(ex, "Error saving advertisement");
                return Ok(new { success = false, message = "Error saving advertisement: " + ex.Message });
            }
        }

        /// <summary>
        /// Copy an existing competition with +1 year offset on dates
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopyCompetition([FromBody] CopyCompetitionRequest request)
        {
            if (!ModelState.IsValid)
            {
                return Ok(new
                {
                    success = false,
                    message = "Invalid request data",
                    errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)
                });
            }

            try
            {
                // Get source competition
                var sourceCompetition = _contentService.GetById(request.SourceCompetitionId);
                if (sourceCompetition == null)
                {
                    return Ok(new { success = false, message = "Source competition not found" });
                }

                if (sourceCompetition.ContentType.Alias != "competition")
                {
                    return Ok(new { success = false, message = "Invalid competition content type" });
                }

                // AUTHORIZATION: Site Admin OR Club Admin OR Skjutledare (for competition's club)
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                // Check Club Admin / Skjutledare (based on source competition's clubId)
                bool isClubAdmin = false;
                bool isSkjutledare = false;
                var competitionClubId = sourceCompetition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(competitionClubId);
                }

                // Allow if Site Admin OR Club Admin OR Skjutledare for this club
                if (!isSiteAdmin && !isClubAdmin && !isSkjutledare)
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Get parent container
                var parentId = sourceCompetition.ParentId;
                if (parentId <= 0)
                {
                    return Ok(new { success = false, message = "Cannot determine competition container" });
                }

                // Create new competition with incremented name and dates
                var newName = $"{sourceCompetition.Name} {DateTime.Now.Year + 1}";
                var newCompetition = _contentService.Create(newName, parentId, "competition");

                if (newCompetition == null)
                {
                    return Ok(new { success = false, message = "Failed to create competition copy" });
                }

                // Copy all properties from source to new, incrementing dates by 1 year
                foreach (var property in sourceCompetition.Properties)
                {
                    try
                    {
                        var value = sourceCompetition.GetValue(property.Alias);

                        // Special handling for date properties - add 1 year
                        if (property.Alias.Contains("Date") || property.Alias.Contains("date"))
                        {
                            if (value is DateTime dateValue)
                            {
                                value = dateValue.AddYears(1);
                            }
                        }

                        if (value != null)
                        {
                            newCompetition.SetValue(property.Alias, value);
                        }
                    }
                    catch
                    {
                        // Skip properties that can't be copied
                    }
                }

                // Ensure copied competition is active (boolean properties may not copy correctly)
                newCompetition.SetValue("isActive", true);

                var saveResult = _contentService.Save(newCompetition);
                if (!saveResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to save competition copy: " + string.Join(", ", saveResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Publish the competition copy ("*" = publish invariant content)
                var publishResult = _contentService.Publish(newCompetition, new[] { "*" });
                if (!publishResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Competition copy saved but failed to publish: " + string.Join(", ", publishResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                // Return full competition data so frontend can add to table without reload
                return Ok(new
                {
                    success = true,
                    message = "Competition copied successfully with dates advanced by 1 year",
                    data = new
                    {
                        id = newCompetition.Id,
                        name = newCompetition.Name,
                        type = newCompetition.GetValue<string>("competitionType") ?? "Unknown",
                        startDate = newCompetition.GetValue<DateTime?>("competitionDate"),
                        status = GetCompetitionStatus(newCompetition),
                        registrationCount = 0, // Copied competition has no registrations
                        sourceId = sourceCompetition.Id
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error copying competition: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a competition
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCompetition([FromBody] DeleteCompetitionRequest request)
        {
            try
            {
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                if (competition.ContentType.Alias != "competition")
                {
                    return Ok(new { success = false, message = "Invalid competition content type" });
                }

                // AUTHORIZATION: Site Admin OR Club Admin OR Skjutledare (for competition's club)
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                // Check Club Admin / Skjutledare (based on competition's clubId)
                bool isClubAdmin = false;
                bool isSkjutledare = false;
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(competitionClubId);
                }

                // Allow if Site Admin OR Club Admin OR Skjutledare for this club
                if (!isSiteAdmin && !isClubAdmin && !isSkjutledare)
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Check for registrations
                var registrationCount = CountRegistrationsForCompetition(competition.Id);
                if (registrationCount > 0)
                {
                    return Ok(new
                    {
                        success = false,
                        message = $"Cannot delete competition with {registrationCount} registration(s). Please remove registrations first."
                    });
                }

                // Unpublish first
                _contentService.Unpublish(competition, null);

                // Delete the content
                var deleteResult = _contentService.Delete(competition);
                if (!deleteResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to delete competition: " + string.Join(", ", deleteResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Competition deleted successfully"
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error deleting competition: " + ex.Message });
            }
        }

        /// <summary>
        /// Move a competition to a series or back to year folder
        /// POST: /umbraco/surface/CompetitionAdmin/MoveCompetitionToSeries
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> MoveCompetitionToSeries([FromBody] MoveCompetitionRequest request)
        {
            try
            {
                var competition = _contentService.GetById(request.CompetitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                if (competition.ContentType.Alias != "competition")
                {
                    return Ok(new { success = false, message = "Invalid competition content type" });
                }

                // AUTHORIZATION: Site Admin OR Club Admin OR Skjutledare OR Regional Admin
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                // Check Club Admin / Skjutledare (based on competition's clubId)
                bool isClubAdmin = false;
                bool isSkjutledare = false;
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
                    if (!isClubAdmin)
                        isSkjutledare = await _authorizationService.IsSkjutledareForClub(competitionClubId);
                }

                // Check Regional Admin
                bool isRegionalAdmin = false;
                if (!isSiteAdmin && !isClubAdmin && !isSkjutledare)
                {
                    var managedRegions = await _authorizationService.GetManagedRegions();
                    isRegionalAdmin = managedRegions.Any();
                }

                if (!isSiteAdmin && !isClubAdmin && !isSkjutledare && !isRegionalAdmin)
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                int newParentId;

                // If seriesId is provided, move to series
                if (request.SeriesId.HasValue && request.SeriesId.Value > 0)
                {
                    var series = _contentService.GetById(request.SeriesId.Value);
                    if (series == null)
                    {
                        return Ok(new { success = false, message = "Series not found" });
                    }

                    if (series.ContentType.Alias != "competitionSeries")
                    {
                        return Ok(new { success = false, message = "Invalid series content type" });
                    }

                    newParentId = series.Id;
                }
                else
                {
                    // Move to year folder
                    var competitionDate = competition.GetValue<DateTime>("competitionDate");
                    if (competitionDate == default)
                    {
                        competitionDate = DateTime.Now;
                    }

                    // Find competitions folder
                    var rootContent = _contentService.GetRootContent().FirstOrDefault();
                    if (rootContent == null)
                    {
                        return Ok(new { success = false, message = "Root content not found" });
                    }

                    var competitionsFolder = GetAllDescendants(rootContent)
                        .FirstOrDefault(c => c.Name.Equals("Competitions", StringComparison.OrdinalIgnoreCase)
                                          || c.ContentType.Alias == "competitionsHub");

                    if (competitionsFolder == null)
                    {
                        return Ok(new { success = false, message = "Competitions folder not found" });
                    }

                    // Find or create year folder
                    string yearFolderName = competitionDate.Year.ToString();
                    var yearFolder = _contentService.GetPagedChildren(competitionsFolder.Id, 0, int.MaxValue, out var totalRecords)
                        .FirstOrDefault(c => c.Name == yearFolderName);

                    if (yearFolder == null)
                    {
                        yearFolder = _contentService.Create(yearFolderName, competitionsFolder.Id, "contentPage");
                        var saveYearResult = _contentService.Save(yearFolder);
                        if (!saveYearResult.Success)
                        {
                            return Ok(new { success = false, message = "Failed to create year folder: " + yearFolderName });
                        }
                        _contentService.Publish(yearFolder, Array.Empty<string>());
                    }

                    newParentId = yearFolder.Id;
                }

                // Move the competition
                var moveResult = _contentService.Move(competition, newParentId);
                if (!moveResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to move competition: " + string.Join(", ", moveResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate caches so competition list and series list reflect the move
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Competition moved successfully"
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error moving competition: " + ex.Message });
            }
        }

        /// <summary>
        /// Migrate shooting class IDs from CSV format to JSON array format
        /// GET: /umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> FixShootingClassIdsFormat()
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                int fixedCount = 0;
                int alreadyCorrectCount = 0;
                int errorCount = 0;
                var errors = new List<string>();

                // Get all competitions
                var competitionsHub = _contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                if (competitionsHub == null)
                {
                    return Ok(new { success = false, message = "Competitions hub not found" });
                }

                var allCompetitions = GetFlatDescendants(competitionsHub)
                    .Where(c => c.ContentType.Alias == "competition")
                    .ToList();

                foreach (var competition in allCompetitions)
                {
                    try
                    {
                        var shootingClassIds = competition.GetValue<string>("shootingClassIds");

                        if (string.IsNullOrEmpty(shootingClassIds))
                        {
                            continue; // No shooting classes set
                        }

                        // Check if already in JSON format
                        if (shootingClassIds.TrimStart().StartsWith("["))
                        {
                            alreadyCorrectCount++;
                            continue; // Already correct format
                        }

                        // Convert CSV to JSON array
                        var classIds = shootingClassIds.Split(',')
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .ToArray();

                        var jsonArray = System.Text.Json.JsonSerializer.Serialize(classIds);

                        // Update the competition
                        competition.SetValue("shootingClassIds", jsonArray);
                        var result = _contentService.Save(competition);

                        if (result.Success)
                        {
                            _contentService.Publish(competition, Array.Empty<string>());
                            fixedCount++;
                        }
                        else
                        {
                            errorCount++;
                            errors.Add($"Competition {competition.Id} ({competition.Name}): Failed to save");
                        }
                    }
                    catch (Exception ex)
                    {
                        errorCount++;
                        errors.Add($"Competition {competition.Id} ({competition.Name}): {ex.Message}");
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = $"Migration completed. Fixed: {fixedCount}, Already correct: {alreadyCorrectCount}, Errors: {errorCount}",
                    fixedCount,
                    alreadyCorrectCount,
                    errorCount,
                    totalCompetitions = allCompetitions.Count,
                    errors
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error during migration: " + ex.Message });
            }
        }

        /// <summary>
        /// Get all descendants of a content item (recursive - use GetFlatDescendants for better performance)
        /// </summary>
        private IEnumerable<Umbraco.Cms.Core.Models.IContent> GetAllDescendants(Umbraco.Cms.Core.Models.IContent content)
        {
            yield return content;

            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out var totalRecords);
            foreach (var child in children)
            {
                foreach (var descendant in GetAllDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Get all descendants of a content item in flat list (OPTIMIZED)
        /// Uses breadth-first iteration instead of recursion for better performance
        /// </summary>
        private int[] GetCompetitionManagerIds(Umbraco.Cms.Core.Models.IContent competition)
        {
            var json = competition.GetValue<string>("competitionManagers") ?? "[]";
            try
            {
                return Newtonsoft.Json.JsonConvert.DeserializeObject<int[]>(json) ?? Array.Empty<int>();
            }
            catch
            {
                return Array.Empty<int>();
            }
        }

        private List<Umbraco.Cms.Core.Models.IContent> GetFlatDescendants(Umbraco.Cms.Core.Models.IContent root)
        {
            var result = new List<Umbraco.Cms.Core.Models.IContent>();
            var queue = new Queue<Umbraco.Cms.Core.Models.IContent>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                var children = _contentService.GetPagedChildren(current.Id, 0, int.MaxValue, out _);
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        /// <summary>
        /// Invalidate all competition and series caches
        /// Called after any CRUD operation on competitions or series
        /// </summary>
        private void InvalidateCompetitionCaches()
        {
            _appCaches.RuntimeCache.ClearByKey(SeriesListCacheKey);
            _appCaches.RuntimeCache.ClearByRegex("^admin_competitions_list_");
        }

        /// <summary>
        /// Count registrations for a specific competition
        /// </summary>
        private int CountRegistrationsForCompetition(int competitionId)
        {
            try
            {
                var rootContent = _contentService.GetRootContent();
                var count = 0;

                foreach (var root in rootContent)
                {
                    count += GetAllDescendants(root)
                        .Where(c => c.ContentType.Alias == "competitionRegistration")
                        .AsEnumerable()
                        .Count(c => c.GetValue<int>("competitionId") == competitionId);
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Determine competition status based on dates
        /// </summary>
        private string GetCompetitionStatus(Umbraco.Cms.Core.Models.IContent competition)
        {
            try
            {
                var startDate = competition.GetValue<DateTime?>("competitionDate");
                var endDate = competition.GetValue<DateTime?>("competitionEndDate");

                if (!startDate.HasValue)
                    return "Draft";

                var now = DateTime.Now;

                if (startDate.Value > now)
                    return "Scheduled";

                if (endDate.HasValue && endDate.Value < now)
                    return "Completed";

                return "Active";
            }
            catch
            {
                return "Unknown";
            }
        }

        // ==================== SERIES ENDPOINTS ====================

        /// <summary>
        /// Get all competition series with basic info for the admin list
        /// Supports optional region filter - shows series containing competitions from clubs in the selected region
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSeriesList(string? region = null)
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var managedRegions = await _authorizationService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                // Determine effective region(s) for filtering first (needed for cache key)
                string? effectiveRegion = region;
                List<string>? effectiveRegions = null;

                if (isRegionalAdmin && !isSiteAdmin)
                {
                    if (!string.IsNullOrEmpty(region) && managedRegions.Contains(region))
                    {
                        effectiveRegion = region;
                    }
                    else if (managedRegions.Count == 1)
                    {
                        effectiveRegion = managedRegions.First();
                    }
                    else
                    {
                        effectiveRegion = null;
                        effectiveRegions = managedRegions;
                    }
                }

                // Check cache first (include effective region in cache key)
                var cacheKeyRegion = effectiveRegion ?? (effectiveRegions != null ? string.Join("_", effectiveRegions) : "all");
                var cacheKey = $"{SeriesListCacheKey}_{cacheKeyRegion}";
                var cachedResult = _appCaches.RuntimeCache.Get(cacheKey);
                if (cachedResult != null)
                {
                    return Ok(cachedResult);
                }

                // Use GetPagedDescendants for a single efficient query (same approach as GetCompetitionsList)
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Ok(new { success = true, data = new List<object>() });
                }

                var allSeries = new List<Umbraco.Cms.Core.Models.IContent>();
                var allCompetitions = new List<Umbraco.Cms.Core.Models.IContent>();
                var clubRegionLookup = new Dictionary<int, string>();
                bool needsRegionFilter = !string.IsNullOrEmpty(effectiveRegion) || (effectiveRegions != null && effectiveRegions.Any());

                var descendants = _contentService.GetPagedDescendants(rootContent.Id, 0, int.MaxValue, out _);
                foreach (var item in descendants)
                {
                    if (item.ContentType.Alias == "competitionSeries")
                    {
                        allSeries.Add(item);
                    }
                    else if (item.ContentType.Alias == "competition")
                    {
                        allCompetitions.Add(item);
                    }
                    else if (item.ContentType.Alias == "club" && needsRegionFilter)
                    {
                        // Collect club region info for series that have a clubId
                        if (!clubRegionLookup.ContainsKey(item.Id))
                        {
                            clubRegionLookup[item.Id] = item.GetValue<string>("regionalFederation") ?? "";
                        }
                    }
                }

                // Pre-calculate competition counts per series
                var competitionCountsByParent = allCompetitions
                    .GroupBy(x => x.ParentId)
                    .ToDictionary(g => g.Key, g => g.Count());

                // Filter series by their OWN organizer properties (clubId / regionalFederation)
                var now = DateTime.Now;
                var seriesData = allSeries
                    .Where(series =>
                    {
                        if (!needsRegionFilter)
                            return true; // No filter — show all

                        var seriesClubId = series.GetValue<int>("clubId");
                        var seriesRegion = series.GetValue<string>("regionalFederation") ?? "";

                        // Series with a club — look up the club's region
                        if (seriesClubId > 0)
                        {
                            if (clubRegionLookup.TryGetValue(seriesClubId, out var clubRegion))
                            {
                                if (!string.IsNullOrEmpty(effectiveRegion))
                                    return clubRegion.Equals(effectiveRegion, StringComparison.OrdinalIgnoreCase);
                                if (effectiveRegions != null)
                                    return effectiveRegions.Contains(clubRegion);
                            }
                            return false;
                        }
                        // Series with direct region
                        if (!string.IsNullOrEmpty(seriesRegion))
                        {
                            if (!string.IsNullOrEmpty(effectiveRegion))
                                return seriesRegion.Equals(effectiveRegion, StringComparison.OrdinalIgnoreCase);
                            if (effectiveRegions != null)
                                return effectiveRegions.Contains(seriesRegion);
                        }
                        // National series (no club, no region) — show to all
                        return true;
                    })
                    .Select(series => new
                    {
                        id = series.Id,
                        name = series.GetValue<string>("seriesName") ?? series.Name,
                        shortDescription = series.GetValue<string>("seriesShortDescription") ?? "",
                        description = series.GetValue<string>("seriesDescription") ?? "",
                        startDate = series.GetValue<DateTime?>("seriesStartDate"),
                        endDate = series.GetValue<DateTime?>("seriesEndDate"),
                        showInMenu = series.GetValue<bool>("showInMenu"),
                        isActive = series.GetValue<bool>("isActive"),
                        clubId = series.GetValue<int>("clubId"),
                        regionalFederation = series.GetValue<string>("regionalFederation") ?? "",
                        seriesCalculationStrategy = series.GetValue<string>("seriesCalculationStrategy") ?? "",
                        seriesCalculationConfig = series.GetValue<string>("seriesCalculationConfig") ?? "",
                        // Use pre-calculated count instead of per-series query
                        competitionCount = competitionCountsByParent.TryGetValue(series.Id, out var count) ? count : 0,
                        status = GetSeriesStatus(series, now)
                    })
                    .OrderByDescending(s => s.startDate ?? DateTime.MinValue)
                    .ToList();

                // Cache the result
                var result = new { success = true, data = seriesData };
                _appCaches.RuntimeCache.Insert(cacheKey, () => result, CacheDuration);

                return Ok(result);
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading series: " + ex.Message });
            }
        }

        /// <summary>
        /// Get competitions in a specific series
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSeriesCompetitions(int seriesId)
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var series = _contentService.GetById(seriesId);
                if (series == null || series.ContentType.Alias != "competitionSeries")
                {
                    return Ok(new { success = false, message = "Series not found" });
                }

                var competitions = _contentService.GetPagedChildren(seriesId, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "competition")
                    .Select(c => new
                    {
                        id = c.Id,
                        name = c.GetValue<string>("competitionName") ?? c.Name,
                        competitionDate = c.GetValue<DateTime>("competitionDate"),
                        status = GetCompetitionStatus(c)
                    })
                    .OrderBy(c => c.competitionDate)
                    .ToList();

                return Ok(new { success = true, data = competitions });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error loading competitions: " + ex.Message });
            }
        }

        /// <summary>
        /// Create a new competition series
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSeries([FromBody] CreateSeriesRequest request)
        {
            // AUTHORIZATION: Site Admin OR Regional Admin
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isRegionalAdmin = false;
            if (!isSiteAdmin)
            {
                var managedRegions = await _authorizationService.GetManagedRegions();
                isRegionalAdmin = managedRegions.Any();
            }
            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            if (string.IsNullOrEmpty(request.SeriesName))
            {
                return BadRequest(new { success = false, message = "Series name is required" });
            }

            try
            {
                // Find or create the competitions folder
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Ok(new { success = false, message = "Root content not found" });
                }

                var competitionsFolder = GetAllDescendants(rootContent)
                    .FirstOrDefault(c => c.Name.Equals("Competitions", StringComparison.OrdinalIgnoreCase)
                                      || c.ContentType.Alias == "competitionsHub");

                if (competitionsFolder == null)
                {
                    return Ok(new { success = false, message = "Competitions folder not found" });
                }

                // Find or create year folder based on series start date
                string yearFolderName = (request.SeriesStartDate?.Year ?? DateTime.Now.Year).ToString();
                var yearFolder = _contentService.GetPagedChildren(competitionsFolder.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.Name == yearFolderName);

                if (yearFolder == null)
                {
                    yearFolder = _contentService.Create(yearFolderName, competitionsFolder.Id, "contentPage");
                    var saveYearResult = _contentService.Save(yearFolder);
                    if (!saveYearResult.Success)
                    {
                        return Ok(new { success = false, message = "Failed to create year folder" });
                    }
                    _contentService.Publish(yearFolder, Array.Empty<string>());
                }

                // Create new series
                var newSeries = _contentService.Create(request.SeriesName, yearFolder.Id, "competitionSeries");
                if (newSeries == null)
                {
                    return Ok(new { success = false, message = "Failed to create series content" });
                }

                // Set all properties
                newSeries.SetValue("seriesName", request.SeriesName);
                if (!string.IsNullOrEmpty(request.SeriesShortDescription))
                    newSeries.SetValue("seriesShortDescription", request.SeriesShortDescription);
                if (!string.IsNullOrEmpty(request.SeriesDescription))
                    newSeries.SetValue("seriesDescription", request.SeriesDescription);
                if (request.SeriesStartDate.HasValue)
                    newSeries.SetValue("seriesStartDate", request.SeriesStartDate.Value);
                if (request.SeriesEndDate.HasValue)
                    newSeries.SetValue("seriesEndDate", request.SeriesEndDate.Value);
                newSeries.SetValue("showInMenu", request.ShowInMenu);
                newSeries.SetValue("isActive", request.IsActive);
                if (!string.IsNullOrEmpty(request.ClubId) && request.ClubId != "0")
                    newSeries.SetValue("clubId", int.Parse(request.ClubId));
                if (!string.IsNullOrEmpty(request.RegionalFederation))
                    newSeries.SetValue("regionalFederation", request.RegionalFederation);
                newSeries.SetValue("seriesCalculationStrategy", request.SeriesCalculationStrategy ?? "");
                newSeries.SetValue("seriesCalculationConfig", request.SeriesCalculationConfig ?? "");

                var saveResult = _contentService.Save(newSeries);
                if (!saveResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Failed to save series: " + string.Join(", ", saveResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Publish the series
                var publishResult = _contentService.Publish(newSeries, Array.Empty<string>());
                if (!publishResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Series saved but failed to publish: " + string.Join(", ", publishResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Series created successfully",
                    data = new
                    {
                        id = newSeries.Id,
                        name = request.SeriesName,
                        startDate = request.SeriesStartDate,
                        endDate = request.SeriesEndDate,
                        competitionCount = 0,
                        isActive = request.IsActive
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating series: " + ex.Message });
            }
        }

        /// <summary>
        /// Update an existing competition series
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSeries([FromBody] UpdateSeriesRequest request)
        {
            // AUTHORIZATION: Site Admin OR Regional Admin
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isRegionalAdmin = false;
            if (!isSiteAdmin)
            {
                var managedRegions = await _authorizationService.GetManagedRegions();
                isRegionalAdmin = managedRegions.Any();
            }
            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var series = _contentService.GetById(request.SeriesId);
                if (series == null || series.ContentType.Alias != "competitionSeries")
                {
                    return Ok(new { success = false, message = "Series not found" });
                }

                // Update properties
                series.SetValue("seriesName", request.SeriesName ?? "");
                if (!string.IsNullOrEmpty(request.SeriesShortDescription))
                    series.SetValue("seriesShortDescription", request.SeriesShortDescription);
                if (!string.IsNullOrEmpty(request.SeriesDescription))
                    series.SetValue("seriesDescription", request.SeriesDescription);
                if (request.SeriesStartDate.HasValue)
                    series.SetValue("seriesStartDate", request.SeriesStartDate.Value);
                if (request.SeriesEndDate.HasValue)
                    series.SetValue("seriesEndDate", request.SeriesEndDate.Value);
                series.SetValue("showInMenu", request.ShowInMenu);
                series.SetValue("isActive", request.IsActive);

                // Clear both organizer fields first, then set the relevant one
                series.SetValue("clubId", 0);
                series.SetValue("regionalFederation", "");
                if (!string.IsNullOrEmpty(request.ClubId) && request.ClubId != "0")
                    series.SetValue("clubId", int.Parse(request.ClubId));
                else if (!string.IsNullOrEmpty(request.RegionalFederation))
                    series.SetValue("regionalFederation", request.RegionalFederation);
                series.SetValue("seriesCalculationStrategy", request.SeriesCalculationStrategy ?? "");
                series.SetValue("seriesCalculationConfig", request.SeriesCalculationConfig ?? "");

                var saveResult = _contentService.Save(series);
                if (!saveResult.Success)
                {
                    return Ok(new { success = false, message = "Failed to save series changes" });
                }

                // Publish the series changes
                var publishResult = _contentService.Publish(series, Array.Empty<string>());
                if (!publishResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Series changes saved but failed to publish: " + string.Join(", ", publishResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();
                _seriesCalculationService.InvalidateCacheForSeries(request.SeriesId);

                return Ok(new { success = true, message = "Series updated successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error updating series: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a competition series (only if it has no competitions)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSeries([FromBody] DeleteSeriesRequest request)
        {
            // AUTHORIZATION: Site Admin OR Regional Admin
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isRegionalAdmin = false;
            if (!isSiteAdmin)
            {
                var managedRegions = await _authorizationService.GetManagedRegions();
                isRegionalAdmin = managedRegions.Any();
            }
            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var series = _contentService.GetById(request.SeriesId);
                if (series == null || series.ContentType.Alias != "competitionSeries")
                {
                    return Ok(new { success = false, message = "Series not found" });
                }

                // Check if series has competitions
                var competitionCount = _contentService.GetPagedChildren(series.Id, 0, int.MaxValue, out _)
                    .Count(c => c.ContentType.Alias == "competition");

                if (competitionCount > 0)
                {
                    return Ok(new
                    {
                        success = false,
                        message = $"Cannot delete series with {competitionCount} competition(s). Remove competitions first."
                    });
                }

                // Unpublish first
                _contentService.Unpublish(series);

                // Then delete
                var deleteResult = _contentService.Delete(series);
                if (!deleteResult.Success)
                {
                    return Ok(new { success = false, message = "Failed to delete series" });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                return Ok(new { success = true, message = "Series deleted successfully" });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error deleting series: " + ex.Message });
            }
        }

        /// <summary>
        /// Copy a series and optionally copy selected competitions
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CopySeriesWithCompetitions([FromBody] CopySeriesRequest request)
        {
            // AUTHORIZATION: Site Admin OR Regional Admin
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isRegionalAdmin = false;
            if (!isSiteAdmin)
            {
                var managedRegions = await _authorizationService.GetManagedRegions();
                isRegionalAdmin = managedRegions.Any();
            }
            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            // Validate start date is present
            if (!request.StartDate.HasValue)
            {
                return Ok(new { success = false, message = "Start date is required to copy series" });
            }

            try
            {
                var sourceSeries = _contentService.GetById(request.SourceSeriesId);
                if (sourceSeries == null || sourceSeries.ContentType.Alias != "competitionSeries")
                {
                    return Ok(new { success = false, message = "Source series not found" });
                }

                // Determine year folder based on StartDate
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Ok(new { success = false, message = "Root content not found" });
                }

                var competitionsFolder = GetAllDescendants(rootContent)
                    .FirstOrDefault(c => c.Name.Equals("Competitions", StringComparison.OrdinalIgnoreCase)
                                      || c.ContentType.Alias == "competitionsHub");
                if (competitionsFolder == null)
                {
                    return Ok(new { success = false, message = "Competitions folder not found" });
                }

                // Find or create year folder based on StartDate
                string yearFolderName = request.StartDate.Value.Year.ToString();
                var yearFolder = _contentService.GetPagedChildren(competitionsFolder.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.Name == yearFolderName);

                if (yearFolder == null)
                {
                    yearFolder = _contentService.Create(yearFolderName, competitionsFolder.Id, "contentPage");
                    var saveYearResult = _contentService.Save(yearFolder);
                    if (!saveYearResult.Success)
                    {
                        return Ok(new { success = false, message = "Failed to create year folder" });
                    }
                    _contentService.Publish(yearFolder, Array.Empty<string>());
                }

                var parentId = yearFolder.Id;

                var newSeriesName = (sourceSeries.GetValue<string>("seriesName") ?? sourceSeries.Name);
                if (!newSeriesName.Contains(" - Copy"))
                {
                    newSeriesName += " - Copy";
                }

                var newSeries = _contentService.Create(newSeriesName, parentId, "competitionSeries");

                // Copy properties with new dates
                newSeries.SetValue("seriesName", newSeriesName);
                newSeries.SetValue("seriesShortDescription", sourceSeries.GetValue<string>("seriesShortDescription") ?? "");
                newSeries.SetValue("seriesDescription", sourceSeries.GetValue<string>("seriesDescription") ?? "");
                // Set dates from request
                newSeries.SetValue("seriesStartDate", request.StartDate.Value);
                if (request.EndDate.HasValue)
                {
                    newSeries.SetValue("seriesEndDate", request.EndDate.Value);
                }
                newSeries.SetValue("showInMenu", sourceSeries.GetValue<bool>("showInMenu"));
                newSeries.SetValue("isActive", sourceSeries.GetValue<bool>("isActive"));

                // Copy organizer properties
                var sourceClubId = sourceSeries.GetValue<int>("clubId");
                if (sourceClubId > 0)
                    newSeries.SetValue("clubId", sourceClubId);
                var sourceRegion = sourceSeries.GetValue<string>("regionalFederation");
                if (!string.IsNullOrEmpty(sourceRegion))
                    newSeries.SetValue("regionalFederation", sourceRegion);

                // Copy series calculation properties
                var sourceStrategy = sourceSeries.GetValue<string>("seriesCalculationStrategy");
                if (!string.IsNullOrEmpty(sourceStrategy))
                    newSeries.SetValue("seriesCalculationStrategy", sourceStrategy);
                var sourceConfig = sourceSeries.GetValue<string>("seriesCalculationConfig");
                if (!string.IsNullOrEmpty(sourceConfig))
                    newSeries.SetValue("seriesCalculationConfig", sourceConfig);

                var saveSeriesResult = _contentService.Save(newSeries);
                if (!saveSeriesResult.Success)
                {
                    return Ok(new { success = false, message = "Failed to create copied series" });
                }

                // Publish the copied series
                var publishSeriesResult = _contentService.Publish(newSeries, Array.Empty<string>());
                if (!publishSeriesResult.Success)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Series copy saved but failed to publish: " + string.Join(", ", publishSeriesResult.EventMessages?.GetAll().Select(e => e.Message))
                    });
                }

                // Copy selected competitions if any
                int copiedCompetitionCount = 0;
                if (request.CompetitionIdsToCopy != null && request.CompetitionIdsToCopy.Any())
                {
                    foreach (var compId in request.CompetitionIdsToCopy)
                    {
                        var sourceComp = _contentService.GetById(compId);
                        if (sourceComp != null && sourceComp.ContentType.Alias == "competition")
                        {
                            // Clone competition
                            var compName = sourceComp.GetValue<string>("competitionName") ?? sourceComp.Name;
                            var newComp = _contentService.Create(compName, newSeries.Id, "competition");

                            // Copy all properties except dates and isActive
                            var allProperties = sourceComp.Properties;
                            foreach (var prop in allProperties)
                            {
                                try
                                {
                                    var value = sourceComp.GetValue(prop.Alias);
                                    // Skip date fields - keep them from new competition
                                    // Skip isActive - we'll set it to false explicitly
                                    if (!prop.Alias.Contains("Date") && prop.Alias != "isActive")
                                    {
                                        newComp.SetValue(prop.Alias, value);
                                    }
                                }
                                catch { /* Skip properties that can't be copied */ }
                            }

                            // Set isActive to false for copied competition
                            newComp.SetValue("isActive", false);

                            var saveCompResult = _contentService.Save(newComp);
                            if (saveCompResult.Success)
                            {
                                // Publish the copied competition
                                var publishCompResult = _contentService.Publish(newComp, Array.Empty<string>());
                                if (publishCompResult.Success)
                                {
                                    copiedCompetitionCount++;
                                }
                            }
                        }
                    }
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = $"Series copied successfully with {copiedCompetitionCount} competition(s)",
                    data = new
                    {
                        id = newSeries.Id,
                        name = newSeriesName,
                        startDate = request.StartDate,
                        endDate = request.EndDate,
                        competitionCount = copiedCompetitionCount,
                        isActive = newSeries.GetValue<bool>("isActive")
                    }
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error copying series: " + ex.Message });
            }
        }

        // ==================== SERIES CALCULATION ENDPOINTS ====================

        /// <summary>
        /// Get available series calculation strategies with parameter definitions.
        /// Used by the series edit modal to populate the strategy dropdown.
        /// </summary>
        [HttpGet]
        public IActionResult GetSeriesCalculationStrategies()
        {
            var strategies = SeriesCalculationRegistry.GetAll().Select(s => new
            {
                id = s.Id,
                name = s.Name,
                description = s.Description,
                parameters = s.GetParameters().Select(p => new
                {
                    key = p.Key,
                    label = p.Label,
                    type = p.Type,
                    defaultValue = p.DefaultValue,
                    placeholder = p.Placeholder,
                    dependsOn = p.DependsOn,
                    dependsOnValue = p.DependsOnValue,
                    options = p.Options?.Select(o => new
                    {
                        value = o.Value,
                        label = o.Label
                    })
                })
            });

            return Ok(new { success = true, data = strategies });
        }

        /// <summary>
        /// Calculate and return series results for a given series.
        /// Called by the series page to display standings.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSeriesResults(int seriesId, bool forceRefresh = false)
        {
            try
            {
                if (forceRefresh)
                {
                    _seriesCalculationService.InvalidateCacheForSeries(seriesId);
                }

                var result = await _seriesCalculationService.CalculateSeriesResults(seriesId);
                if (result == null)
                {
                    return Ok(new { success = false, message = "No calculation strategy configured or series not found" });
                }

                return Ok(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error calculating series results: " + ex.Message });
            }
        }

        /// <summary>
        /// Determine series status (Draft, Scheduled, Active, Completed)
        /// </summary>
        private string GetSeriesStatus(Umbraco.Cms.Core.Models.IContent series, DateTime now)
        {
            var startDate = series.GetValue<DateTime?>("seriesStartDate");
            var endDate = series.GetValue<DateTime?>("seriesEndDate");

            if (!startDate.HasValue)
                return "Draft";

            if (startDate.Value > now)
                return "Scheduled";

            if (endDate.HasValue && endDate.Value < now)
                return "Completed";

            return "Active";
        }

        /// <summary>
        /// Migrate legacy memberClub (string) data to new clubId (numeric) property - BATCHED VERSION
        /// Usage:
        /// - Preview: /umbraco/surface/CompetitionAdmin/MigrateRegistrationClubIds
        /// - Migrate: /umbraco/surface/CompetitionAdmin/MigrateRegistrationClubIds?confirm=true&batchSize=50
        /// Run multiple times until complete
        /// </summary>
        [HttpGet]
        public IActionResult MigrateRegistrationClubIds(bool confirm = false, int batchSize = 50)
        {
            try
            {
                // Get all competitionRegistration nodes
                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
                var allRegistrations = allContent
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .ToList();

                // Find registrations that need migration
                var needMigration = allRegistrations
                    .Where(reg =>
                    {
                        var existingClubId = reg.GetValue<int>("clubId");
                        var memberClub = reg.GetValue<string>("memberClub");
                        return existingClubId == 0 && !string.IsNullOrEmpty(memberClub) && int.TryParse(memberClub, out _);
                    })
                    .ToList();

                var alreadyMigrated = allRegistrations.Count - needMigration.Count;

                if (!confirm)
                {
                    // Preview mode - just show status
                    return Json(new
                    {
                        success = true,
                        preview = true,
                        totalRegistrations = allRegistrations.Count,
                        alreadyMigrated = alreadyMigrated,
                        needMigration = needMigration.Count,
                        message = $"Status: {alreadyMigrated} already migrated, {needMigration.Count} remaining. Add ?confirm=true&batchSize=50 to migrate next batch."
                    });
                }

                // Process in batches to avoid timeout
                var batch = needMigration.Take(batchSize).ToList();

                int migratedCount = 0;
                var errors = new List<string>();

                foreach (var reg in batch)
                {
                    try
                    {
                        var memberClub = reg.GetValue<string>("memberClub");
                        if (!string.IsNullOrEmpty(memberClub) && int.TryParse(memberClub, out var clubId))
                        {
                            reg.SetValue("clubId", clubId);
                            var result = _contentService.Save(reg);
                            if (result.Success)
                            {
                                migratedCount++;
                            }
                            else
                            {
                                errors.Add($"Failed to save registration {reg.Id}: {string.Join(", ", result.EventMessages)}");
                            }
                        }
                    }
                    catch (Exception regEx)
                    {
                        errors.Add($"Error processing registration {reg.Id}: {regEx.Message}");
                    }
                }

                var remaining = needMigration.Count - batch.Count;

                return Json(new
                {
                    success = true,
                    batchMigrated = migratedCount,
                    batchSize = batch.Count,
                    totalAlreadyMigrated = alreadyMigrated + migratedCount,
                    totalRemaining = remaining,
                    totalRegistrations = allRegistrations.Count,
                    isComplete = remaining == 0,
                    errors = errors,
                    message = remaining > 0
                        ? $"Migrated {migratedCount} registrations. {remaining} remaining. Run again to continue."
                        : $"Migration complete! All {allRegistrations.Count} registrations processed."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Clear invalid invitation file references
        /// GET: /umbraco/surface/CompetitionAdmin/ClearInvalidInvitationFiles
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ClearInvalidInvitationFiles()
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                int clearedCount = 0;
                var allCompetitions = _contentService.GetRootContent()
                    .SelectMany(root => GetFlatDescendants(root))
                    .Where(c => c.ContentType.Alias == "competition")
                    .ToList();

                foreach (var competition in allCompetitions)
                {
                    var invitationFileValue = competition.GetValue<string>("invitationFile");
                    if (!string.IsNullOrEmpty(invitationFileValue))
                    {
                        // Check if it's a valid UDI
                        if (!invitationFileValue.StartsWith("umb://"))
                        {
                            // Invalid value - clear it
                            competition.SetValue("invitationFile", null);
                            _contentService.Save(competition);
                            _contentService.Publish(competition, Array.Empty<string>());
                            clearedCount++;
                        }
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = $"Cleared {clearedCount} invalid invitation file reference(s)",
                    clearedCount = clearedCount
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error clearing invalid data: " + ex.Message });
            }
        }

        /// <summary>
        /// Upload invitation file for external competition
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadInvitationFile(int competitionId, IFormFile invitationFile)
        {
            // AUTHORIZATION: Site Admin OR Competition Manager OR Club Admin OR Skjutledare
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);

            // Get managed clubs and skjutledare clubs for authorization check
            var managedClubIds = await _authorizationService.GetManagedClubIds();
            var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();

            if (!isSiteAdmin && !isCompetitionManager && !managedClubIds.Any() && !skjutledareClubIds.Any())
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get competition
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // Verify it's an external competition
                bool isExternal = competition.GetValue<bool>("isExternal");
                if (!isExternal)
                {
                    return Ok(new { success = false, message = "File upload only available for external competitions" });
                }

                // Check club admin / skjutledare authorization
                var competitionClubId = competition.GetValue<int?>("clubId") ?? 0;
                bool isClubAdmin = competitionClubId > 0 && managedClubIds.Contains(competitionClubId);
                bool isSkjutledare = competitionClubId > 0 && skjutledareClubIds.Contains(competitionClubId);

                if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Ok(new { success = false, message = "You don't have permission to upload files for this competition" });
                }

                // Validate file
                if (invitationFile == null || invitationFile.Length == 0)
                {
                    return Ok(new { success = false, message = "No file uploaded" });
                }

                // Validate file size (10 MB max)
                if (invitationFile.Length > 10 * 1024 * 1024)
                {
                    return Ok(new { success = false, message = "File too large. Maximum 10 MB allowed." });
                }

                // Validate file extension
                var extension = Path.GetExtension(invitationFile.FileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".pdf", ".doc", ".docx" };
                if (!allowedExtensions.Contains(extension))
                {
                    return Ok(new { success = false, message = "Invalid file type. Only PDF and Word documents allowed." });
                }

                // Find or create "Competition Invitations" folder in Media Library
                var invitationsFolder = _mediaService.GetRootMedia()
                    .FirstOrDefault(m => m.Name == "Competition Invitations");

                if (invitationsFolder == null)
                {
                    invitationsFolder = _mediaService.CreateMedia("Competition Invitations", -1, "Folder");
                    _mediaService.Save(invitationsFolder);
                }

                // Create media item with unique name to avoid conflicts
                string fileName = Path.GetFileName(invitationFile.FileName);
                string fileExtension = Path.GetExtension(fileName);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid()}{fileExtension}";

                var mediaItem = _mediaService.CreateMedia(fileName, invitationsFolder.Id, "File");

                // Save the file to a temporary location first
                var tempFilePath = Path.Combine(Path.GetTempPath(), uniqueFileName);
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await invitationFile.CopyToAsync(fileStream);
                }

                try
                {
                    // Get the physical media folder path
                    var mediaFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media", "competition-invitations");

                    // Create the directory if it doesn't exist
                    if (!Directory.Exists(mediaFolderPath))
                    {
                        Directory.CreateDirectory(mediaFolderPath);
                    }

                    // Copy file to media folder
                    var physicalFilePath = Path.Combine(mediaFolderPath, uniqueFileName);
                    System.IO.File.Copy(tempFilePath, physicalFilePath, true);

                    // Set the media file path (relative to wwwroot)
                    var relativePath = $"/media/competition-invitations/{uniqueFileName}";
                    mediaItem.SetValue("umbracoFile", relativePath);

                    // Set additional properties
                    mediaItem.SetValue("umbracoExtension", fileExtension.TrimStart('.'));
                    mediaItem.SetValue("umbracoBytes", invitationFile.Length.ToString());

                    // Save media item with file reference
                    var mediaSaveResult = _mediaService.Save(mediaItem);
                    if (!mediaSaveResult.Success)
                    {
                        // Clean up physical file if save failed
                        if (System.IO.File.Exists(physicalFilePath))
                        {
                            System.IO.File.Delete(physicalFilePath);
                        }
                        return Ok(new { success = false, message = "Failed to save file to media library" });
                    }
                }
                finally
                {
                    // Clean up temp file
                    if (System.IO.File.Exists(tempFilePath))
                    {
                        System.IO.File.Delete(tempFilePath);
                    }
                }

                // Link media item to competition
                competition.SetValue("invitationFile", mediaItem.GetUdi().ToString());

                // Save competition
                var competitionSaveResult = _contentService.Save(competition);
                if (!competitionSaveResult.Success)
                {
                    return Ok(new { success = false, message = "Failed to link file to competition" });
                }

                // Publish competition
                _contentService.Publish(competition, Array.Empty<string>());

                return Ok(new
                {
                    success = true,
                    message = "Invitation file uploaded successfully",
                    fileName = fileName
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error uploading file: " + ex.Message });
            }
        }
    }

    /// <summary>
    /// Request model for creating a new competition
    /// </summary>
    public class CreateCompetitionRequest
    {
        public string? CompetitionType { get; set; }
        public Dictionary<string, object>? Fields { get; set; }
    }

    /// <summary>
    /// Request model for copying a competition
    /// </summary>
    public class CopyCompetitionRequest
    {
        public int SourceCompetitionId { get; set; }
    }

    /// <summary>
    /// Request model for deleting a competition
    /// </summary>
    public class DeleteCompetitionRequest
    {
        public int CompetitionId { get; set; }
    }

    /// <summary>
    /// Request model for moving a competition to a series
    /// </summary>
    public class MoveCompetitionRequest
    {
        public int CompetitionId { get; set; }
        public int? SeriesId { get; set; } // Null to move to year folder
    }

    /// <summary>
    /// Request model for creating a series
    /// </summary>
    public class CreateSeriesRequest
    {
        public string SeriesName { get; set; }
        public string SeriesShortDescription { get; set; }
        public string SeriesDescription { get; set; }
        public DateTime? SeriesStartDate { get; set; }
        public DateTime? SeriesEndDate { get; set; }
        public bool ShowInMenu { get; set; } = false;
        public bool IsActive { get; set; } = true;
        public string ClubId { get; set; }
        public string RegionalFederation { get; set; }
        public string SeriesCalculationStrategy { get; set; }
        public string SeriesCalculationConfig { get; set; }
    }

    /// <summary>
    /// Request model for updating a series
    /// </summary>
    public class UpdateSeriesRequest
    {
        public int SeriesId { get; set; }
        public string SeriesName { get; set; }
        public string SeriesShortDescription { get; set; }
        public string SeriesDescription { get; set; }
        public DateTime? SeriesStartDate { get; set; }
        public DateTime? SeriesEndDate { get; set; }
        public bool ShowInMenu { get; set; }
        public bool IsActive { get; set; }
        public string ClubId { get; set; }
        public string RegionalFederation { get; set; }
        public string SeriesCalculationStrategy { get; set; }
        public string SeriesCalculationConfig { get; set; }
    }

    /// <summary>
    /// Request model for deleting a series
    /// </summary>
    public class DeleteSeriesRequest
    {
        public int SeriesId { get; set; }
    }
          

    /// <summary>
    /// Request model for copying a series with selected competitions
    /// </summary>
    public class CopySeriesRequest
    {
        public int SourceSeriesId { get; set; }
        public int[] CompetitionIdsToCopy { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
    }
}
