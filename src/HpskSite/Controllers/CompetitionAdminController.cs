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
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        /// <summary>Row shape for the registration-count query in GetCompetitionsList.</summary>
        private class RegistrationParentCountRow
        {
            public int ParentId { get; set; }
            public int GrandParentId { get; set; }
            public int Cnt { get; set; }
        }

        // Cache configuration
        private const string SeriesListCacheKey = "admin_series_list";
        private const string CompetitionsListCacheKey = "admin_competitions_list_{0}_{1}"; // year, includeCompleted
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);
        // Deliberately under the admin_competitions_list_ prefix — see the comment at the query.
        private const string RegistrationCountsCacheKey = "admin_competitions_list_regcounts";
        private static readonly TimeSpan RegistrationCountsCacheDuration = TimeSpan.FromSeconds(60);

        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;
        private readonly HpskSite.Services.Results.ICompetitionResultStore _results;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.ICompetitionSeriesStore _seriesStore;
        private readonly HpskSite.Services.Competitions.CompetitionTreePlacement _placement;
        private readonly HpskSite.Services.Competitions.CompetitionDeleteGuard _deleteGuard;
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
            SeriesCalculationService seriesCalculationService,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations,
            HpskSite.Services.Results.ICompetitionResultStore results,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Competitions.ICompetitionSeriesStore seriesStore,
            HpskSite.Services.Competitions.CompetitionTreePlacement placement,
            HpskSite.Services.Competitions.CompetitionDeleteGuard deleteGuard)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _registrations = registrations;
            _results = results;
            _competitions = competitions;
            _seriesStore = seriesStore;
            _placement = placement;
            _deleteGuard = deleteGuard;
            _contentService = contentService;
            _memberManager = memberManager;
            _memberService = memberService;
            _authorizationService = authorizationService;
            _mediaService = mediaService;
            _mediaFileManager = mediaFileManager;
            _seriesCalculationService = seriesCalculationService;
            _appCaches = appCaches;
            _umbracoContextAccessor = umbracoContextAccessor;
            _databaseFactory = databaseFactory;
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
        /// <summary>
        /// Members pickable as Tävlingsansvariga, for the shared ManagerSelectionModal.
        ///
        /// The picker used to call MemberAdmin/GetMembers, which is the site-admin member-administration
        /// endpoint — so a plain COMPETITION MANAGER editing their own competition got
        /// "Kunde inte ladda medlemmar: Access denied" and an empty picker (reported 2026-08-05,
        /// kallekula on an SM). Appointing helpers is a normal part of running a competition, so this
        /// endpoint answers to whoever may manage the competition, while returning only what the picker
        /// renders — id, name, club — and none of the administrative fields (groups, lockout, activity)
        /// that MemberAdmin/GetMembers exposes.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMembersForManagerPicker(int? competitionId = null, int? clubId = null)
        {
            try
            {
                bool authorized = await _authorizationService.IsCurrentUserAdminAsync()
                    || (await _authorizationService.GetManagedClubIds()).Any()
                    || (await _authorizationService.GetManagedRegions()).Any();

                // Editing an existing competition: whoever manages THAT competition qualifies.
                if (!authorized && competitionId.HasValue && competitionId.Value > 0)
                {
                    authorized = await _authorizationService.IsCompetitionManager(competitionId.Value)
                              || await _authorizationService.IsRegionHostAdminAsync(competitionId.Value);
                }

                if (!authorized)
                    return Ok(new { success = false, message = "Du har inte behörighet att välja tävlingsansvariga." });

                // Club names for the picker's club column + filter. Same traversal the sibling
                // endpoints in this controller use (clubsPage under a root, and under each region).
                var clubNames = new Dictionary<int, string>();
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    foreach (var root in ctx.Content.GetAtRoot())
                    {
                        var hubs = root.Children.Where(c => c.ContentType.Alias == "clubsPage").ToList();
                        foreach (var region in root.Children.Where(c => c.ContentType.Alias == "regionalPage"))
                            hubs.AddRange(region.Children.Where(c => c.ContentType.Alias == "clubsPage"));
                        foreach (var hub in hubs)
                            foreach (var club in hub.Children.Where(c => c.ContentType.Alias == "club"))
                                clubNames[club.Id] = club.Value<string>("clubName") ?? club.Name;
                    }
                }

                var members = _memberService.GetAll(0, int.MaxValue, out _)
                    .Where(m => m.ContentType.Alias != "hpskClub" && m.IsApproved)
                    .Select(m =>
                    {
                        int primary = 0;
                        int.TryParse(m.GetValue<string>("primaryClubId") ?? "", out primary);
                        return new
                        {
                            id = m.Id,
                            firstName = m.GetValue<string>("firstName") ?? "",
                            lastName = m.GetValue<string>("lastName") ?? "",
                            email = m.Email,
                            primaryClubId = primary,
                            primaryClubName = primary > 0 && clubNames.TryGetValue(primary, out var cn) ? cn : "No Club"
                        };
                    })
                    .Where(m => !clubId.HasValue || clubId.Value <= 0 || m.primaryClubId == clubId.Value)
                    .OrderBy(m => (m.firstName + " " + m.lastName).Trim(),
                             StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), ignoreCase: true))
                    .ToList();

                return Ok(new { success = true, data = new { items = members } });
            }
            catch (Exception)
            {
                return Ok(new { success = false, message = "Kunde inte hämta medlemslistan." });
            }
        }

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
        /// Supports server-side filtering by year, completed status, type, region and club scope
        /// </summary>
        /// <param name="clubScope">
        /// "all" (default) = show everything, "noInternal" = hide klubbinterna (isClubOnly) comps,
        /// "noClub" = hide every club-hosted comp so only krets/nationella remain.
        /// </param>
        [HttpGet]
        public async Task<IActionResult> GetCompetitionsList(int? year = null, bool includeCompleted = false, string? type = null, string? region = null, string? clubScope = null)
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
                var cacheKeyClubScope = string.IsNullOrEmpty(clubScope) ? "all" : clubScope;
                if (isSiteAdmin)
                {
                    cacheKey = string.Format(CompetitionsListCacheKey, filterYear, includeCompleted) + $"_{cacheKeyType}_{cacheKeyRegion}_{cacheKeyClubScope}";
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

                // Alla publicerade tävlingar ur tävlingslagringen (C3-planen S3) — de ligger under navet.
                var allCompetitions = _competitions.List();

                // Build club -> region lookup from published cache (only if needed for region filtering)
                // Resolved once, not per competition — the filter runs over every node in the hub.
                var wantedType = string.IsNullOrEmpty(type) ? null : Models.CompetitionTypes.GetFuzzy(type);

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
                    .Where(comp => isSiteAdmin || isRegionalAdmin || managedClubIds.Contains(comp.ClubId) || skjutledareClubIds.Contains(comp.ClubId))
                    .Where(comp =>
                    {
                        var compDate = comp.CompetitionDate;

                        // Year filter
                        if (year.HasValue && compDate.HasValue && compDate.Value.Year != filterYear)
                            return false;

                        // Status filter - exclude completed unless requested
                        if (!includeCompleted && compDate.HasValue)
                        {
                            var compEndDate = comp.CompetitionEndDate;
                            var effectiveEnd = (compEndDate.HasValue && compEndDate.Value.Year > 1900) ? compEndDate.Value.Date : compDate.Value.Date;
                            var isCompleted = effectiveEnd < today;
                            if (isCompleted) return false;
                        }

                        // Type filter. The dropdown sends a canonical CompetitionTypes id, but the
                        // stored property has historically held both ids ("MagnumFalt") and display
                        // names, so match through GetFuzzy and only fall back to a literal compare
                        // when the value is not a known type at all.
                        if (!string.IsNullOrEmpty(type))
                        {
                            var compType = comp.CompetitionType ?? "";
                            var actualType = wantedType == null ? null : Models.CompetitionTypes.GetFuzzy(compType);
                            if (wantedType != null && actualType != null)
                            {
                                if (!wantedType.Id.Equals(actualType.Id, StringComparison.OrdinalIgnoreCase))
                                    return false;
                            }
                            else if (!compType.Equals(type, StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }
                        }

                        // Club-scope filter - the list is dominated by club competitions, so an
                        // admin needs to be able to fold them away without losing krets/nationella.
                        if (!string.IsNullOrEmpty(clubScope) && !clubScope.Equals("all", StringComparison.OrdinalIgnoreCase))
                        {
                            var compClubIdScope = comp.ClubId;
                            if (clubScope.Equals("noClub", StringComparison.OrdinalIgnoreCase) && compClubIdScope > 0)
                                return false;
                            if (clubScope.Equals("noInternal", StringComparison.OrdinalIgnoreCase)
                                && compClubIdScope > 0 && comp.IsClubOnly)
                                return false;
                        }

                        // Region filter
                        if (!string.IsNullOrEmpty(effectiveRegion))
                        {
                            var compClubId = comp.ClubId;
                            var compRegion = comp.RegionalFederation ?? "";

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
                            var compClubId = comp.ClubId;
                            var compRegion = comp.RegionalFederation ?? "";

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

                // Registration counts come from the DB, not the published cache: public
                // registrations are Save()d but never Publish()ed, so comp.Children never sees
                // them and the column read 0 for every row. Both layouts exist in the tree
                // (registration directly under the competition, or under a registrations hub),
                // so resolve the owning competition from parent AND grandparent.
                // The row set is the same for every caller, filter combination and page, so it is
                // cached independently of the response cache above (which only site admins get, and
                // only on an exact filter repeat). The key sits under the admin_competitions_list_
                // prefix on purpose: InvalidateCompetitionCaches() already drops that whole prefix,
                // so there are no new invalidation call sites to remember.
                var regCountByCompetition = new Dictionary<int, int>();
                try
                {
                    // Antalet per tävling via anmälningslagringen (sajtstrukturen fas 1a).
                    var allCounts = _appCaches.RuntimeCache.GetCacheItem(
                        RegistrationCountsCacheKey,
                        () => new Dictionary<int, int>(_registrations.CountByCompetition()),
                        RegistrationCountsCacheDuration) as Dictionary<int, int>;

                    if (allCounts != null)
                    {
                        foreach (var c in filteredCompetitions)
                            if (allCounts.TryGetValue(c.Id, out var cnt)) regCountByCompetition[c.Id] = cnt;
                    }
                }
                catch
                {
                    // A failed count must not take the whole admin list down - fall back to 0.
                }

                // Build competition list — parent lookup via .Parent (no DB calls needed)
                var competitions = filteredCompetitions
                    .Select(comp =>
                    {
                        // Noden bara för adressen och beskrivningen (RTE); serien ur serielagringen.
                        var node = umbracoContext.Content.GetById(comp.Id);
                        var series = comp.SeriesId is int sid ? _seriesStore.Get(sid) : null;
                        var isInSeries = series != null;

                        var isActive = comp.IsVisible;
                        var compDate = comp.CompetitionDate;
                        var compEndDate = comp.CompetitionEndDate;
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
                            else
                            {
                                // Finished the day after the end date — same rule the public club page
                                // (ClubCompetitionsList) and the "visa även avslutade" filter use. No grace
                                // window: it previously kept a just-ended comp "Aktiv" for 7 days, which
                                // contradicted the filter (already counting it completed) and every other view.
                                status = "Completed";
                            }
                        }
                        else
                        {
                            status = "Scheduled";
                        }

                        // Count registrations from the SQL lookup built above (see comment there)
                        var regCount = regCountByCompetition.GetValueOrDefault(comp.Id, 0);

                        return new
                        {
                            id = comp.Id,
                            url = node == null ? "" : HpskSite.Extensions.CompetitionUrlExtensions.CompetitionUrl(node),
                            name = comp.CompetitionName ?? comp.Name,
                            description = node?.Value<string>("description") ?? "",
                            type = comp.CompetitionType ?? "Unknown",
                            startDate = compDate,
                            endDate = compEndDate,
                            registrationOpenDate = comp.RegistrationOpenDate,
                            registrationCloseDate = comp.RegistrationCloseDate,
                            isActive = isActive,
                            isClubOnly = comp.IsClubOnly,
                            // Bekräftad nivå eller tomt; listan faller då tillbaka på värdbrickan.
                            competitionLevel = HpskSite.CompetitionTypes.Common.CompetitionLevel.Normalize(comp.CompetitionLevel),
                            isExternal = comp.IsExternal,
                            clubId = comp.ClubId,
                            // Surfaced so the list can tag each row Klubb / Krets / Nationell — lets a
                            // regional admin who mis-scoped a comp recognise where it actually landed.
                            regionalFederation = comp.RegionalFederation ?? "",
                            registrationCount = regCount,
                            allowSelfReporting = comp.AllowSelfReporting,
                            seriesId = isInSeries ? series!.Id : (int?)null,
                            seriesName = isInSeries ? (series!.SeriesName ?? series.Name) : null,
                            status = status
                        };
                    })
                    .OrderByDescending(c => c.startDate ?? DateTime.MinValue)
                    .ToList();

                var result = new
                {
                    success = true,
                    isSiteAdmin = isSiteAdmin,
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
            // Personalregeln (C3-planen S2, Stefans beslut 2026-10-14). Förut saknades kretsens väg: en
            // kretsadmin nådde inte en kretsvärdad tävling i redigeringsdialogen.
            if (!await _authorizationService.HasCompetitionStaffAccessAsync(id, AdminAuthorizationService.CompetitionRead.Saved))
            {
                return Ok(new { success = false, message = "You don't have permission to view this competition" });
            }

            try
            {
                // Det sparade läget (redigeringen), tidpunkterna helst ur det publicerade (C3-planen S3).
                var competition = _competitions.GetSaved(id);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }
                var publishedComp = _competitions.Get(id);

                // shootingClassIds: JSON-array eller äldre CSV.
                string[] shootingClassIds = Array.Empty<string>();
                var shootingClassIdsValue = competition.ShootingClassIds;
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

                // Serien tävlingen ingår i (föräldern när den är en serie).
                int? seriesId = competition.SeriesId;

                // Format dates for Flatpickr (Y-m-d H:i or Y-m-d)
                string FormatDate(Func<HpskSite.Models.Competitions.CompetitionInfo, DateTime?> read, bool includeTime = true)
                {
                    var fmt = includeTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd";
                    var dt = publishedComp == null ? null : read(publishedComp);
                    if (dt.HasValue && dt.Value != DateTime.MinValue)
                        return dt.Value.ToString(fmt);
                    var raw = read(competition);
                    if (!raw.HasValue || raw.Value == DateTime.MinValue) return "";
                    return raw.Value.ToString(fmt);
                }

                var descriptionValue = HpskSite.Extensions.RteHelper.ExtractMarkup(competition.Description);

                var competitionData = new
                {
                    id = competition.Id,
                    competitionName = competition.CompetitionName ?? "",
                    competitionType = competition.CompetitionType ?? "Precision",
                    description = descriptionValue,
                    venue = competition.Venue ?? "",
                    competitionDate = FormatDate(c => c.CompetitionDate, true),
                    competitionEndDate = FormatDate(c => c.CompetitionEndDate, false),
                    registrationOpenDate = FormatDate(c => c.RegistrationOpenDate, true),
                    registrationCloseDate = FormatDate(c => c.RegistrationCloseDate, true),
                    numberOfSeriesOrStations = competition.NumberOfSeriesOrStations,
                    numberOfFinalSeries = competition.NumberOfFinalSeries,
                    shootingClassIds = shootingClassIds,
                    externalUrl = competition.ExternalUrl ?? "",
                    externalRegistrationEmail = competition.ExternalRegistrationEmail ?? "",
                    isExternal = competition.IsExternal,
                    allowSelfReporting = competition.AllowSelfReporting,
                    showLiveResults = competition.ShowLiveResults,
                    isActive = competition.IsVisible,
                    allowDualCClass = competition.AllowDualCClassRegistration,
                    addToMenu = competition.AddToMenu,
                    isAwardingStandardMedals = competition.IsAwardingStandardMedals,
                    isClubOnly = competition.IsClubOnly,
                    maxParticipants = competition.MaxParticipants,
                    registrationFee = competition.Fee(HpskSite.Models.Competitions.CompetitionFeeKind.Registration) ?? 0m,
                    juniorRegistrationFee = competition.JuniorRegistrationFee ?? "0",
                    subCompetitionFee = competition.SubCompetitionFee ?? "0",
                    subCompetitionFeeMode = competition.SubCompetitionFeeMode ?? "perClass",
                    competitionDirector = competition.CompetitionDirector ?? "",
                    contactEmail = competition.ContactEmail ?? "",
                    contactPhone = competition.ContactPhone ?? "",
                    swishNumber = competition.SwishNumber ?? "",
                    allowTeams = competition.AllowTeams,
                    teamRegistrationFee = competition.TeamRegistrationFee ?? "0",
                    // Aldrig på dokumenttypen — standardvärdet.
                    teamResultSeriesCount = 0,
                    allowStafett = competition.AllowStafett,
                    stafettRegistrationFee = competition.StafettRegistrationFee ?? "0",
                    competitionManagers = competition.ManagerIds.ToArray(),
                    competitionScope = competition.CompetitionScope ?? "",
                    seriesId = seriesId,
                    clubId = competition.ClubId,
                    regionalFederation = competition.RegionalFederation ?? ""
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

            // Check Regional Admin — för den krets tävlingen skapas i (rättat 2026-10-14, C3-planen S2).
            // Förut räckte det att vara kretsadmin någonstans, så en kretsadmin kunde skapa en tävling
            // åt vilken klubb eller krets som helst. Samma regel som serierna (RegionalAdminOwnsSeriesHost):
            // en klubbvärd via IsClubAdminForClub (som viker in klubbens kretsadmin), en kretsvärd måste
            // vara en av den inloggades kretsar. En tävling utan värd hör inte till någon krets.
            bool isRegionalAdmin = false;
            if (!isSiteAdmin && !isClubAdmin && !isSkjutledare)
            {
                isRegionalAdmin = await RegionalAdminOwnsSeriesHost(
                    ReadFieldAsInt(request.Fields, "clubId"), ReadFieldAsString(request.Fields, "regionalFederation"));
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

                // Soft URL-correctness guard: at least one of clubId / regionalFederation /
                // competitionScope must be set so CompetitionUrlProvider can produce a clean
                // URL. Mirrors the wizard's client-side check; a buggy/old client cannot bypass it.
                int _hostClubId = ReadFieldAsInt(request.Fields, "clubId");
                string _hostRegFed = ReadFieldAsString(request.Fields, "regionalFederation");
                string _hostScope = ReadFieldAsString(request.Fields, "competitionScope");
                if (_hostClubId <= 0 && string.IsNullOrWhiteSpace(_hostRegFed) && string.IsNullOrWhiteSpace(_hostScope))
                {
                    return Ok(new { success = false, message = "Välj antingen ansvarig klubb, krets eller mästerskapstyp — annars går det inte att skapa en lättläst URL för tävlingen." });
                }

                // Tävlingens kategori — samma regel som redigeringen (CompetitionEditController.SaveCompetition).
                var _level = ReadFieldAsString(request.Fields, HpskSite.CompetitionTypes.Common.CompetitionLevel.PropertyAlias);
                if (!HpskSite.CompetitionTypes.Common.CompetitionLevel.IsValid(_level))
                {
                    return Ok(new { success = false, message = $"Okänd tävlingsnivå: \"{_level}\"." });
                }
                var _levelError = HpskSite.CompetitionTypes.Common.CompetitionLevel.ConsistencyError(
                    _level, ReadFieldAsBool(request.Fields, "isClubOnly"), _hostClubId > 0,
                    ReadFieldAsBool(request.Fields, "isAwardingStandardMedals"));
                if (_levelError != null)
                {
                    return Ok(new { success = false, message = _levelError });
                }

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

                // Finalomgång finns bara i precisionsskjutning (SHB C.3.6.1.1) — se CompetitionFinals.
                var _finalsRefusal = HpskSite.CompetitionTypes.Common.CompetitionFinals.Refusal(
                    competitionType.Id, ReadFieldAsInt(request.Fields, "numberOfFinalSeries"));
                if (_finalsRefusal != null)
                {
                    return Ok(new { success = false, message = _finalsRefusal });
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
                    // Årsmappen under tävlingsnavet (CompetitionTreePlacement, C3-planen S5c).
                    try { parentId = _placement.YearFolderId(competitionDate.Year); }
                    catch (InvalidOperationException ex) { return Ok(new { success = false, message = ex.Message }); }
                }

                // Bara sajtadmin lägger en tävling i den nationella menyn (Stefan 2026-10-13).
                if (!isSiteAdmin) request.Fields?.Remove("addToMenu");

                // Tävlingen skapas genom lagringen och fältpåsen genom den gemensamma regeln (C3-planen S5b).
                // Synlig om inte formuläret säger annat; namn och gren sätts sist, ur de prövade värdena.
                var created = _competitions.Create(parentId, competitionName, c =>
                {
                    c.IsVisible = true;
                    HpskSite.Services.Competitions.CompetitionFieldBag.Apply(c, request.Fields, CreateHandledElsewhere);
                    c.CompetitionType = competitionTypeId;
                    c.CompetitionName = competitionName;
                });
                var newCompetition = created.Record!;
                if (created.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Competition saved but failed to publish: " + created.PublishProblem
                    });
                }

                // Fältskytte/MagnumFält: eagerly create the "Resultat" child page so the
                // admin Resultat tab can embed /competitions/.../resultat/ immediately —
                // no need to click Officiell/Preliminär first just to see live results.
                // Public "Visa resultat"-button stays hidden until isOfficial=true.
                // (CompetitionManagement.cshtml has a fallback that creates this lazily on
                // first tab open for legacy comps that pre-date this change.)
                EnsureCompetitionResultPage(newCompetition, competitionTypeId);

                // Kretsgranskning fas 4: skapades tävlingen ur en ansökan kopplas de HÄR, på
                // servern, i samma begäran. Ett separat klientanrop efteråt hade kunnat utebli, och
                // då hade kalendern visat både ansökan och tävlingen. Värdens klubb måste vara
                // ansökans — en ansökan kan inte kopplas till en annan klubbs tävling.
                var applicationNote = LinkApplicationIfAny(request.Fields, newCompetition.Id, _hostClubId);

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
                        startDate = newCompetition.CompetitionDate,
                        status = GetCompetitionStatus(newCompetition),
                        registrationCount = 0 // New competition has no registrations
                    },
                    applicationNote
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error creating competition: " + ex.Message });
            }
        }

        /// <summary>
        /// Kopplar en nyskapad tävling till ansökan den skapades ur (fältet <c>applicationId</c>,
        /// som annars släpps av fältkatalogen). Returnerar ett besked när kopplingen inte gick —
        /// tävlingen är då skapad ändå, och det ska sägas i stället för att tigas om.
        /// </summary>
        private string? LinkApplicationIfAny(Dictionary<string, object>? fields, int competitionId, int hostClubId)
        {
            var appId = ReadFieldAsInt(fields, "applicationId");
            if (appId <= 0) return null;
            try
            {
                // Hämtas ur RequestServices i stället för konstruktorn: den här kontrollern har redan
                // ett tjugotal beroenden, och kopplingen är en sidoväg i ett enda anrop.
                var apps = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.Kretsgranskning.CompetitionApplicationService))
                    as HpskSite.Services.Kretsgranskning.CompetitionApplicationService;
                var cal = HttpContext.RequestServices.GetService(typeof(HpskSite.Services.Kretsgranskning.KretsCalendarService))
                    as HpskSite.Services.Kretsgranskning.KretsCalendarService;
                if (apps == null) return null;
                var a = apps.Get(appId);
                if (a == null) return "Ansökan som tävlingen skulle kopplas till finns inte.";
                if (a.ClubId != Math.Max(0, hostClubId)) return "Tävlingen har en annan arrangör än ansökan och kopplades inte till den.";

                var me = _memberManager.GetCurrentMemberAsync().GetAwaiter().GetResult();
                var data = me?.Email == null ? null : _memberService.GetByEmail(me.Email);
                var (linked, err) = apps.LinkCompetition(appId, competitionId, data?.Id ?? 0, data?.Name ?? "");
                cal?.Invalidate();
                return linked == null ? err : null;
            }
            catch (Exception ex)
            {
                (HttpContext.RequestServices.GetService(typeof(Microsoft.Extensions.Logging.ILogger<CompetitionAdminController>)) as Microsoft.Extensions.Logging.ILogger)?.LogError(ex, "Competition {Comp}: linking to application {App} failed.", competitionId, appId);
                return "Tävlingen är skapad, men kopplingen till ansökan misslyckades.";
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
                // Bara sajtadmin lägger en tävling i den nationella menyn (Stefan 2026-10-13).
                if (fields != null && !await _authorizationService.IsCurrentUserAdminAsync()) fields.Remove("addToMenu");
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
                    // Årsmappen under tävlingsnavet (CompetitionTreePlacement, C3-planen S5c).
                    try { parentId = _placement.YearFolderId(competitionDate.Year); }
                    catch (InvalidOperationException ex) { return Ok(new { success = false, message = ex.Message }); }
                }

                // Annonsen skapas genom lagringen och fältpåsen genom den gemensamma regeln (C3-planen S5b).
                // En annons är alltid extern, synlig och öppen; serieantalet har sina standardvärden, och
                // klubb och bana sätts bara när de är valda.
                var created = _competitions.Create(parentId, competitionName, c =>
                {
                    HpskSite.Services.Competitions.CompetitionFieldBag.Apply(c, fields, AdvertisementHandledElsewhere);
                    c.CompetitionType = competitionTypeId;
                    c.CompetitionName = competitionName;
                    c.IsExternal = true;
                    c.IsVisible = true;
                    c.IsClubOnly = false;
                    c.NumberOfSeriesOrStations = AdvertisementSeriesCount(fields);
                    c.NumberOfFinalSeries = AdvertisementFinalSeriesCount(fields);
                    var club = ReadFieldAsInt(fields, "clubId");
                    if (club > 0) c.ClubId = club;
                    var range = ReadFieldAsInt(fields, "rangeId");
                    if (range > 0) c.RangeId = range;
                });
                var newCompetition = created.Record!;

                // TODO: Handle file upload for invitation
                // File uploads for new competitions via this endpoint are not supported yet
                // Users can add invitation files via the Edit modal after creation
                // This keeps the CreateAdvertisement endpoint simpler and avoids media API complexity

                if (created.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Advertisement saved but failed to publish: " + created.PublishProblem
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
                        startDate = newCompetition.CompetitionDate,
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
                // Bara sajtadmin lägger en tävling i den nationella menyn (Stefan 2026-10-13).
                if (fields != null && !await _authorizationService.IsCurrentUserAdminAsync()) fields.Remove("addToMenu");
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
                var competition = _competitions.GetSaved(competitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // Validate isExternal flag (ensure we're only editing external competitions)
                if (!competition.IsExternal)
                {
                    return Ok(new { success = false, message = "This competition is not external. Use internal edit endpoint." });
                }

                // AUTHORIZATION: personalregeln (C3-planen S2) — sajtadmin, tävlingsansvarig, klubbadmin eller
                // skjutledare för värdklubben, eller kretsens admin för en kretsvärdad tävling. Sparat läge.
                if (!await _authorizationService.HasCompetitionStaffAccessAsync(competitionId, AdminAuthorizationService.CompetitionRead.Saved))
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

                // Genom lagringen och den gemensamma fältregeln (C3-planen S5b). Förut skrevs den råa
                // formulärnyckeln som alias, så t.ex. allowDualCClass gick aldrig att spara här.
                // ⚠️ Lagringen publicerar bara en annons som redan var publicerad (förut alltid).
                string competitionName = nameObj.ToString()!;
                var result = _competitions.Update(competitionId, null, c =>
                {
                    HpskSite.Services.Competitions.CompetitionFieldBag.Apply(c, fields, AdvertisementHandledElsewhere);
                    c.Name = competitionName;
                    c.CompetitionName = competitionName;
                    c.IsExternal = true;
                    var type = ReadFieldAsString(fields, "competitionType");
                    if (!string.IsNullOrWhiteSpace(type)) c.CompetitionType = type;
                    c.NumberOfSeriesOrStations = AdvertisementSeriesCount(fields);
                    c.NumberOfFinalSeries = AdvertisementFinalSeriesCount(fields);
                    // Klubben sätts när fältet skickas (0 tar bort klubben); banan likaså (0 = ingen bana).
                    if (fields.TryGetValue("clubId", out var clubRaw) && clubRaw != null) c.ClubId = Math.Max(0, ReadFieldAsInt(fields, "clubId"));
                    if (fields.TryGetValue("rangeId", out var rangeRaw) && rangeRaw != null) c.RangeId = Math.Max(0, ReadFieldAsInt(fields, "rangeId"));
                });
                if (!result.Ok || result.Record == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }
                if (result.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Advertisement saved but failed to publish: " + result.PublishProblem
                    });
                }
                var saved = result.Record;

                // Invalidate cache so updates show up immediately
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Competition advertisement updated successfully",
                    data = new
                    {
                        id = saved.Id,
                        name = saved.CompetitionName ?? saved.Name,
                        type = saved.CompetitionType,
                        startDate = saved.CompetitionDate,
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
                var sourceCompetition = _competitions.GetSaved(request.SourceCompetitionId);
                if (sourceCompetition == null)
                {
                    return Ok(new { success = false, message = "Source competition not found" });
                }

                // AUTHORIZATION: redigeringsregeln (C3-planen S2, Stefans beslut 2026-10-14) — tävlingsansvarig
                // med, skjutledare inte; kretsens admin bara för en kretsvärdad tävling.
                if (!await _authorizationService.CanEditCompetitionAsync(request.SourceCompetitionId, AdminAuthorizationService.CompetitionRead.Saved))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Get parent container
                var parentId = sourceCompetition.ParentId;
                if (parentId <= 0)
                {
                    return Ok(new { success = false, message = "Cannot determine competition container" });
                }

                // ⚠️ BARA DEFINITIONEN följer med (C3-planen S5b, CompetitionFieldTable.CopyDefinitionInto):
                // publicerade patruller, officiella resultat, stationschefer, sammanslagning, medaljindelning
                // och de uppladdade filerna hör till den gamla tävlingen. Förut kopierades varje egenskap, så en
                // kopia kunde födas med "resultaten är officiella" och förra årets inbjudan.
                // Datumen flyttas ett år; kopian är synlig, och bara sajtadmin får lägga den i menyn.
                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var newName = $"{sourceCompetition.Name} {DateTime.Now.Year + 1}";
                var created = _competitions.Create(parentId, newName, c =>
                {
                    HpskSite.Models.Competitions.CompetitionFieldTable.CopyDefinitionInto(sourceCompetition, c, shiftYears: 1);
                    c.IsVisible = true;
                    c.AddToMenu = isSiteAdmin && sourceCompetition.AddToMenu;
                });
                var newCompetition = created.Record!;
                if (created.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Competition copy saved but failed to publish: " + created.PublishProblem
                    });
                }

                // Eager Resultat page for Fältskytte/MagnumFält copies — no-op for other types.
                EnsureCompetitionResultPage(newCompetition, newCompetition.CompetitionType);

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
                        type = newCompetition.CompetitionType ?? "Unknown",
                        startDate = newCompetition.CompetitionDate,
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
                var competition = _competitions.GetSaved(request.CompetitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // AUTHORIZATION: borttagningsregeln (C3-planen S2, Stefans beslut 2026-10-14) — klubbadmin för
                // värdklubben eller kretsens admin för en kretsvärdad tävling. Varken tävlingsansvarig eller
                // skjutledare.
                if (!await _authorizationService.CanDeleteCompetitionAsync(request.CompetitionId, AdminAuthorizationService.CompetitionRead.Saved))
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

                // Allt annat som hänger på tävlingen (C3-planen S5c): lag, resultat, betalningar, gamla fakturor,
                // medaljer, märkesserier, bemanning, evenemangskoppling, ansökan.
                var refusal = _deleteGuard.Refusal(competition.Id);
                if (refusal != null)
                {
                    return Ok(new { success = false, message = refusal });
                }

                // ⚠️ MJUK RADERING: papperskorgen, inte en permanent radering. Den permanenta tog med sig allt
                // under tävlingen (anmälningsnav, gamla fakturor, resultatnoden) och gick inte att ångra.
                var trashed = _competitions.Trash(competition.Id);
                if (!trashed.Ok)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // Invalidate caches
                InvalidateCompetitionCaches();

                return Ok(new
                {
                    success = true,
                    message = "Tävlingen ligger nu i papperskorgen och kan läggas tillbaka av en sajtadministratör."
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
                if (_competitions.GetSaved(request.CompetitionId) == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // AUTHORIZATION: redigeringsregeln (C3-planen S2, Stefans beslut 2026-10-14) — samma som
                // redigeringsdialogen som anropar flytten när serien byts, så en tävlingsansvarig som får
                // redigera också får byta serie. Förut släpptes vilken kretsadmin som helst in
                // (GetManagedRegions().Any()), och tävlingsansvariga nekades.
                if (!await _authorizationService.CanEditCompetitionAsync(request.CompetitionId, AdminAuthorizationService.CompetitionRead.Saved))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Genom lagringen (C3-planen S5c): serien, eller årsmappen för tävlingens datum.
                try
                {
                    var moved = _competitions.MoveToSeries(request.CompetitionId,
                        request.SeriesId is > 0 ? request.SeriesId : null);
                    if (!moved.Ok) return Ok(new { success = false, message = "Competition not found" });
                }
                catch (InvalidOperationException ex)
                {
                    return Ok(new { success = false, message = ex.Message });
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

                // ⚠️ GetRootContent() returnerar noder på ROTNIVÅ (barn till -1), alltså bara Home.
                // competitionsHub är ett BARN till Home (path -1,<home>,<hub>), så det gamla
                // uppslaget hittade den aldrig och den här endpointen har svarat
                // "Competitions hub not found" sedan den skrevs — den har alltså aldrig migrerat
                // något. Samma uppslag som CreateCompetition använder, av samma skäl.
                var competitionsHub = _placement.Hub();
                if (competitionsHub == null)
                {
                    return Ok(new { success = false, message = "Competitions hub not found" });
                }

                // Trädet (även opublicerade) avgör urvalet; tävlingen läses och skrivs genom lagringen (C3-planen S5).
                var allCompetitions = GetFlatDescendants(competitionsHub)
                    .Where(c => c.ContentType.Alias == "competition") // competitionstore-ok: underhållsverktyget går trädet, även opublicerade
                    .Select(c => _competitions.GetSaved(c.Id))
                    .Where(c => c != null)
                    .Cast<HpskSite.Models.Competitions.CompetitionInfo>()
                    .ToList();

                foreach (var competition in allCompetitions)
                {
                    try
                    {
                        var shootingClassIds = competition.ShootingClassIds;

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

                        // Samma normaliserare som skrivvägen, så migreringen och skapandet inte
                        // kan producera olika former.
                        var jsonArray = HpskSite.Models.ShootingClassIdsValue.FromText(shootingClassIds);
                        if (string.IsNullOrEmpty(jsonArray))
                        {
                            continue; // inget att skriva
                        }

                        // Update the competition
                        var result = _competitions.Update(competition.Id, null, c => c.ShootingClassIds = jsonArray);

                        if (result.Ok && result.PublishProblem == null)
                        {
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
        /// Ensures the "Resultat" child page exists for a Fältskytte/MagnumFält
        /// competition. No-op for other competition types and for comps that already
        /// have the child. Safe to call from any create or copy path. Failures are
        /// logged and swallowed — CompetitionManagement.cshtml has a lazy fallback
        /// that retries on first view.
        /// </summary>
        /// <summary>
        /// Skapar tävlingens "Resultat"-barnnod direkt när tävlingen skapas eller kopieras.
        ///
        /// ⚠️ GÄLLER ALLA grenar sedan 2026-09-07, inte bara fältfamiljen. Skälet: admins
        /// Resultat-flik bäddar in <c>{tävlingens url}/resultat/</c>, och den URL:en byggs
        /// DETERMINISTISKT av <c>CompetitionResultsController</c> — den frågar inte om noden
        /// finns. Saknades noden fick organisatören en "Page Not Found" mitt i fliken, vilket
        /// är precis vad precisionsfamiljen gjorde ända till nu.
        ///
        /// En typlista här hade drivit isär från listan i CompetitionManagement.cshtml (samma
        /// sorts drift som CompetitionFieldCatalog finns för att stoppa), så regeln är
        /// grenoberoende.
        ///
        /// Säkert publikt: noden skapas med <c>isOfficial=false</c>, och Competition.cshtml
        /// kräver <c>hasResultPage &amp;&amp; isResultsOfficial</c> för "Visa resultat"-knappen.
        /// </summary>
        private void EnsureCompetitionResultPage(HpskSite.Models.Competitions.CompetitionInfo? competition, string? competitionType)
        {
            if (competition == null) return;
            try
            {
                _results.Ensure(competition.Id);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Could not create Resultat page for competition {competition.Id} (type {competitionType}): {ex.Message}");
            }
        }

        /// <summary>Nycklar som skapandet sköter själv och som fältpåsen inte ska skriva (C3-planen S5b).</summary>
        private static readonly HashSet<string> CreateHandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
        {
            "competitionName", "competitionType", "seriesId", "applicationId", "competitionManagerIds"
        };

        /// <summary>Nycklar som annonsdialogerna sköter själva (C3-planen S5b) — filerna har egna uppladdningar.</summary>
        private static readonly HashSet<string> AdvertisementHandledElsewhere = new(StringComparer.OrdinalIgnoreCase)
        {
            "competitionId", "competitionName", "competitionType", "seriesId", "isExternal", "isActive", "isClubOnly",
            "clubId", "rangeId", "numberOfSeriesOrStations", "numberOfFinalSeries", "invitationFile", "resultListFile"
        };

        /// <summary>Annonsens antal serier: det angivna när det är större än noll, annars 6 (som förut).</summary>
        private static int AdvertisementSeriesCount(Dictionary<string, object>? fields)
        {
            var n = ReadFieldAsInt(fields, "numberOfSeriesOrStations");
            return n > 0 ? n : 6;
        }

        /// <summary>Annonsens antal finalserier: det angivna när det inte är negativt, annars 0.</summary>
        private static int AdvertisementFinalSeriesCount(Dictionary<string, object>? fields) =>
            Math.Max(0, ReadFieldAsInt(fields, "numberOfFinalSeries"));

        /// <summary>
        /// Invalidate all competition and series caches
        /// Called after any CRUD operation on competitions or series
        /// </summary>
        private void InvalidateCompetitionCaches()
        {
            _appCaches.RuntimeCache.ClearByKey(SeriesListCacheKey);
            _appCaches.RuntimeCache.ClearByRegex("^admin_competitions_list_");
            HpskSite.Services.CompetitionMenuService.Invalidate(_appCaches);
        }

        // IsRegionalAdminForCompetition BORTTAGEN 2026-10-14 (C3-planen S2): kopiera och flytta följer
        // redigeringsregeln, ta bort borttagningsregeln — båda i AdminAuthorizationService.

        /// <summary>
        /// Count registrations for a specific competition
        /// </summary>
        private int CountRegistrationsForCompetition(int competitionId)
        {
            // Via anmälningslagringen (sajtstrukturen fas 1a) — förut en vandring genom hela
            // innehållsträdet för att räkna en tävlings anmälningar.
            // ⚠️ Inget try/catch: ett läsfel är INTE noll anmälningar. Förut svarade den 0 vid fel, och
            // raderingen släppte då igenom en tävling med anmälningar (genomgången 2026-10-13, A7).
            // Felet bubblar till DeleteCompetitions catch, som vägrar.
            return _registrations.CountByCompetition(new[] { competitionId }).GetValueOrDefault(competitionId);
        }

        /// <summary>
        /// Determine competition status based on dates
        /// </summary>
        private string GetCompetitionStatus(Umbraco.Cms.Core.Models.IContent competition) =>
            GetCompetitionStatus(competition.GetValue<DateTime?>("competitionDate"), competition.GetValue<DateTime?>("competitionEndDate"));

        private static string GetCompetitionStatus(HpskSite.Models.Competitions.CompetitionInfo competition) =>
            GetCompetitionStatus(competition.CompetitionDate, competition.CompetitionEndDate);

        private static string GetCompetitionStatus(DateTime? startDate, DateTime? endDate)
        {
            try
            {

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
            // Auth context drives filtering, not access. Series metadata (name/dates) is
            // already public on /competitions/<series>/, and competition managers (who can
            // open the edit modal) need this list to assign their competition to a series.
            var (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds) = await GetSeriesAuthContext();

            // Require an authenticated member, but allow any role (competition managers too).
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
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
                    var managedRegions = await _authorizationService.GetManagedRegions();
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

                // Check cache first (include effective region or club IDs in cache key)
                string cacheKeySuffix;
                if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin)
                    cacheKeySuffix = "clubs_" + string.Join("_", managedClubIds.OrderBy(x => x));
                else
                    cacheKeySuffix = effectiveRegion ?? (effectiveRegions != null ? string.Join("_", effectiveRegions) : "all");
                var cacheKey = $"{SeriesListCacheKey}_{cacheKeySuffix}";
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
                // Club admins only see series belonging to their managed clubs
                var now = DateTime.Now;
                var seriesData = allSeries
                    .Where(series =>
                    {
                        // Club admins: only show series belonging to their clubs
                        if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin)
                        {
                            var seriesClubId = series.GetValue<int>("clubId");
                            return seriesClubId > 0 && managedClubIds.Contains(seriesClubId);
                        }

                        if (!needsRegionFilter)
                            return true; // No filter — show all

                        var seriesClubIdForRegion = series.GetValue<int>("clubId");
                        var seriesRegion = series.GetValue<string>("regionalFederation") ?? "";

                        // Series with a club — look up the club's region
                        if (seriesClubIdForRegion > 0)
                        {
                            if (clubRegionLookup.TryGetValue(seriesClubIdForRegion, out var clubRegion))
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
            var (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds) = await GetSeriesAuthContext();
            if (!isSiteAdmin && !isRegionalAdmin && !isClubAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var series = _seriesStore.GetSaved(seriesId);
                if (series == null)
                {
                    return Ok(new { success = false, message = "Series not found" });
                }

                // Club admins can only view competitions for their club's series
                if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin && !IsSeriesOwnedByClubs(series, managedClubIds))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }
                if (isRegionalAdmin && !isSiteAdmin && !await RegionalAdminOwnsSeries(series))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Seriens barn ur trädet (även opublicerade), fälten ur tävlingens sparade läge (C3-planen S3).
                var competitions = _contentService.GetPagedChildren(seriesId, 0, int.MaxValue, out _)
                    .Select(c => _competitions.GetSaved(c.Id))
                    .Where(c => c != null)
                    .Select(c => new
                    {
                        id = c!.Id,
                        name = c.CompetitionName ?? c.Name,
                        competitionDate = c.CompetitionDate ?? DateTime.MinValue,
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
            // AUTHORIZATION: Site Admin OR Regional Admin OR Club Admin
            var (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds) = await GetSeriesAuthContext();
            if (!isSiteAdmin && !isRegionalAdmin && !isClubAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            // Club admins can only create series for their own club
            if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin)
            {
                if (string.IsNullOrEmpty(request.ClubId) || !int.TryParse(request.ClubId, out var reqClubId) || !managedClubIds.Contains(reqClubId))
                {
                    return Ok(new { success = false, message = "Access denied: you can only create series for your own club" });
                }
            }

            // Kretsadministratörer bara för en klubb eller krets i den egna kretsen.
            if (isRegionalAdmin && !isSiteAdmin && !await RegionalAdminOwnsSeriesHost(ParseClubId(request.ClubId), request.RegionalFederation))
            {
                return Ok(new { success = false, message = "Access denied: du kan bara skapa serier för klubbar och kretsar i din egen krets" });
            }

            if (string.IsNullOrEmpty(request.SeriesName))
            {
                return BadRequest(new { success = false, message = "Series name is required" });
            }

            try
            {
                // Årsmappen under tävlingsnavet (CompetitionTreePlacement, C3-planen S5c).
                int yearFolderId;
                try { yearFolderId = _placement.YearFolderId((request.SeriesStartDate?.Year ?? DateTime.Now.Year)); }
                catch (InvalidOperationException ex) { return Ok(new { success = false, message = ex.Message }); }

                // Genom serielagringen (C3-planen S5c). Samma värden som förut; ett tomt fält skrivs inte.
                var created = _seriesStore.Create(yearFolderId, request.SeriesName, s =>
                {
                    s.SeriesName = request.SeriesName;
                    if (!string.IsNullOrEmpty(request.SeriesShortDescription)) s.SeriesShortDescription = request.SeriesShortDescription;
                    if (!string.IsNullOrEmpty(request.SeriesDescription)) s.SeriesDescription = request.SeriesDescription;
                    s.SeriesStartDate = request.SeriesStartDate;
                    s.SeriesEndDate = request.SeriesEndDate;
                    // Bara sajtadmin lägger serier i den nationella menyn (Stefan 2026-10-13).
                    s.ShowInMenu = isSiteAdmin && request.ShowInMenu;
                    s.IsVisible = request.IsActive;
                    var club = ParseClubId(request.ClubId);
                    if (club > 0) s.ClubId = club;
                    if (!string.IsNullOrEmpty(request.RegionalFederation)) s.RegionalFederation = request.RegionalFederation;
                    s.SeriesCalculationStrategy = string.IsNullOrEmpty(request.SeriesCalculationStrategy) ? null : request.SeriesCalculationStrategy;
                    s.SeriesCalculationConfig = string.IsNullOrEmpty(request.SeriesCalculationConfig) ? null : request.SeriesCalculationConfig;
                });
                var newSeries = created.Record!;
                if (created.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Series saved but failed to publish: " + created.PublishProblem
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
            // AUTHORIZATION: Site Admin OR Regional Admin OR Club Admin
            var (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds) = await GetSeriesAuthContext();
            if (!isSiteAdmin && !isRegionalAdmin && !isClubAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var series = _seriesStore.GetSaved(request.SeriesId);
                if (series == null)
                {
                    return Ok(new { success = false, message = "Series not found" });
                }

                // Club admins can only update their own club's series and cannot change the club
                if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin)
                {
                    if (!IsSeriesOwnedByClubs(series, managedClubIds))
                    {
                        return Ok(new { success = false, message = "Access denied" });
                    }
                    // Prevent club admin from changing clubId to a different club
                    if (!string.IsNullOrEmpty(request.ClubId) && int.TryParse(request.ClubId, out var reqClubId) && !managedClubIds.Contains(reqClubId))
                    {
                        return Ok(new { success = false, message = "Access denied: cannot change series to a different club" });
                    }
                }

                // Kretsadministratörer: serien OCH den nya värden ska ligga i den egna kretsen.
                if (isRegionalAdmin && !isSiteAdmin
                    && (!await RegionalAdminOwnsSeries(series)
                        || !await RegionalAdminOwnsSeriesHost(ParseClubId(request.ClubId), request.RegionalFederation)))
                {
                    return Ok(new { success = false, message = "Access denied: serien och dess värd måste ligga i din egen krets" });
                }

                // Genom serielagringen (C3-planen S5c), samma regler som förut: beskrivningarna och datumen
                // ändras bara när de skickas, värden är klubben ELLER kretsen. ⚠️ Lagringen publicerar bara en
                // serie som redan var publicerad (förut alltid).
                var updated = _seriesStore.Update(request.SeriesId, null, s =>
                {
                    s.SeriesName = string.IsNullOrEmpty(request.SeriesName) ? null : request.SeriesName;
                    if (!string.IsNullOrEmpty(request.SeriesShortDescription)) s.SeriesShortDescription = request.SeriesShortDescription;
                    if (!string.IsNullOrEmpty(request.SeriesDescription)) s.SeriesDescription = request.SeriesDescription;
                    if (request.SeriesStartDate.HasValue) s.SeriesStartDate = request.SeriesStartDate.Value;
                    if (request.SeriesEndDate.HasValue) s.SeriesEndDate = request.SeriesEndDate.Value;
                    // Bara sajtadmin lägger serier i den nationella menyn (Stefan 2026-10-13); för andra står värdet kvar.
                    if (isSiteAdmin) s.ShowInMenu = request.ShowInMenu;
                    s.IsVisible = request.IsActive;
                    var club = ParseClubId(request.ClubId);
                    s.ClubId = club > 0 ? club : 0;
                    s.RegionalFederation = club <= 0 && !string.IsNullOrEmpty(request.RegionalFederation) ? request.RegionalFederation : null;
                    s.SeriesCalculationStrategy = string.IsNullOrEmpty(request.SeriesCalculationStrategy) ? null : request.SeriesCalculationStrategy;
                    s.SeriesCalculationConfig = string.IsNullOrEmpty(request.SeriesCalculationConfig) ? null : request.SeriesCalculationConfig;
                });
                if (!updated.Ok)
                {
                    return Ok(new { success = false, message = "Series not found" });
                }
                if (updated.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Series changes saved but failed to publish: " + updated.PublishProblem
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
            // AUTHORIZATION: Site Admin OR Regional Admin OR Club Admin
            var (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds) = await GetSeriesAuthContext();
            if (!isSiteAdmin && !isRegionalAdmin && !isClubAdmin)
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var series = _seriesStore.GetSaved(request.SeriesId);
                if (series == null)
                {
                    return Ok(new { success = false, message = "Series not found" });
                }

                // Club admins can only delete their own club's series
                if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin && !IsSeriesOwnedByClubs(series, managedClubIds))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }
                if (isRegionalAdmin && !isSiteAdmin && !await RegionalAdminOwnsSeries(series))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Check if series has competitions
                var competitionCount = _contentService.GetPagedChildren(series.Id, 0, int.MaxValue, out _)
                    .Count(c => c.ContentType.Alias == "competition"); // competitionstore-ok: spärren räknar trädets barn, även opublicerade och i papperskorgen-läge

                if (competitionCount > 0)
                {
                    return Ok(new
                    {
                        success = false,
                        message = $"Cannot delete series with {competitionCount} competition(s). Remove competitions first."
                    });
                }

                // Mjuk radering (C3-planen S5c): papperskorgen, inte en permanent radering.
                var trashed = _seriesStore.Trash(series.Id);
                if (!trashed.Ok)
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
            // AUTHORIZATION: Site Admin OR Regional Admin OR Club Admin
            var (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds) = await GetSeriesAuthContext();
            if (!isSiteAdmin && !isRegionalAdmin && !isClubAdmin)
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
                var sourceSeries = _seriesStore.GetSaved(request.SourceSeriesId);
                if (sourceSeries == null)
                {
                    return Ok(new { success = false, message = "Source series not found" });
                }

                // Club admins can only copy their own club's series
                if (isClubAdmin && !isSiteAdmin && !isRegionalAdmin && !IsSeriesOwnedByClubs(sourceSeries, managedClubIds))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }
                if (isRegionalAdmin && !isSiteAdmin && !await RegionalAdminOwnsSeries(sourceSeries))
                {
                    return Ok(new { success = false, message = "Access denied" });
                }

                // Årsmappen under tävlingsnavet (CompetitionTreePlacement, C3-planen S5c).
                int parentId;
                try { parentId = _placement.YearFolderId(request.StartDate.Value.Year); }
                catch (InvalidOperationException ex) { return Ok(new { success = false, message = ex.Message }); }

                var newSeriesName = sourceSeries.SeriesName ?? sourceSeries.Name;
                if (!newSeriesName.Contains(" - Copy"))
                {
                    newSeriesName += " - Copy";
                }

                // Genom serielagringen (C3-planen S5c). Samma fält som förut: namn, beskrivningar, värd och
                // beräkningen följer med; datumen kommer ur begäran; menyn bara för sajtadmin. ⚠️ Synligheten
                // läses nu med §9a-regeln (saknat värde = synlig) — förut blev en serie utan isActive dold.
                var createdSeries = _seriesStore.Create(parentId, newSeriesName, s =>
                {
                    s.SeriesName = newSeriesName;
                    s.SeriesShortDescription = sourceSeries.SeriesShortDescription;
                    s.SeriesDescription = sourceSeries.SeriesDescription;
                    s.SeriesStartDate = request.StartDate.Value;
                    s.SeriesEndDate = request.EndDate;
                    s.ShowInMenu = isSiteAdmin && sourceSeries.ShowInMenu;
                    s.IsVisible = sourceSeries.IsVisible;
                    if (sourceSeries.ClubId > 0) s.ClubId = sourceSeries.ClubId;
                    s.RegionalFederation = sourceSeries.RegionalFederation;
                    s.SeriesCalculationStrategy = sourceSeries.SeriesCalculationStrategy;
                    s.SeriesCalculationConfig = sourceSeries.SeriesCalculationConfig;
                });
                var newSeries = createdSeries.Record!;
                if (createdSeries.PublishProblem != null)
                {
                    return Ok(new
                    {
                        success = false,
                        message = "Series copy saved but failed to publish: " + createdSeries.PublishProblem
                    });
                }

                // Copy selected competitions if any
                // ⚠️ Bara definitionen följer med (C3-planen S5b), utan datum — omgångarna får sina datum när
                // de planeras. Kopiorna är dolda tills arrangören visar dem, och bara sajtadmin får lägga dem i menyn.
                int copiedCompetitionCount = 0;
                if (request.CompetitionIdsToCopy != null && request.CompetitionIdsToCopy.Any())
                {
                    foreach (var compId in request.CompetitionIdsToCopy)
                    {
                        var sourceComp = _competitions.GetSaved(compId);
                        if (sourceComp == null) continue;

                        var compName = sourceComp.CompetitionName ?? sourceComp.Name;
                        try
                        {
                            var created = _competitions.Create(newSeries.Id, compName, c =>
                            {
                                HpskSite.Models.Competitions.CompetitionFieldTable.CopyDefinitionInto(sourceComp, c, shiftYears: null);
                                c.IsVisible = false;
                                c.AddToMenu = isSiteAdmin && sourceComp.AddToMenu;
                            });
                            if (created.PublishProblem == null)
                            {
                                copiedCompetitionCount++;
                                // Eager Resultat page for Fältskytte/MagnumFält — no-op otherwise.
                                EnsureCompetitionResultPage(created.Record, created.Record?.CompetitionType);
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Could not copy competition {compId} into series {newSeries.Id}: {ex.Message}");
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
                        isActive = newSeries.IsVisible
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
        /// Get authorization context for series operations (site admin, regional admin, or club admin)
        /// </summary>
        /// <summary>
        /// Why a series publish failed, in words. Umbraco hands back an EMPTY EventMessages list
        /// when the content is merely invalid — a mandatory property left blank — which produced a
        /// bare "failed to publish: " with nothing after the colon, and an unpublished ghost series
        /// nobody could see. The publish status is the only thing that says anything in that case.
        /// </summary>
        private static string DescribeSeriesPublishFailure(Umbraco.Cms.Core.Services.PublishResult publishResult)
        {
            var messages = publishResult.EventMessages?.GetAll().Select(e => e.Message).Where(m => !string.IsNullOrWhiteSpace(m)).ToList()
                           ?? new List<string>();
            if (messages.Any())
            {
                return string.Join(", ", messages);
            }

            if (publishResult.Result == Umbraco.Cms.Core.Services.PublishResultType.FailedPublishContentInvalid)
            {
                return "obligatoriska fält saknas — kontrollera att startdatum är ifyllt.";
            }

            return publishResult.Result.ToString();
        }
        private async Task<(bool isSiteAdmin, bool isRegionalAdmin, bool isClubAdmin, HashSet<int> managedClubIds)> GetSeriesAuthContext()
        {
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isRegionalAdmin = false;
            bool isClubAdmin = false;
            var managedClubIds = new HashSet<int>();

            if (!isSiteAdmin)
            {
                var managedRegions = await _authorizationService.GetManagedRegions();
                isRegionalAdmin = managedRegions.Any();
            }

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                var clubIds = await _authorizationService.GetManagedClubIds();
                managedClubIds = clubIds.ToHashSet();
                isClubAdmin = managedClubIds.Any();
            }

            return (isSiteAdmin, isRegionalAdmin, isClubAdmin, managedClubIds);
        }

        /// <summary>
        /// Får en KRETSadministratör hantera en serie med den här värden (klubb eller krets)?
        ///
        /// <para>C3-planen S0, seriesviten 2026-10-14: serieendpointsen frågade bara "är du kretsadmin
        /// någonstans" (<c>GetManagedRegions().Any()</c>), så en kretsadmin i Blekinge kunde skapa, ändra,
        /// kopiera och ta bort Hallands serier. Samma fel som Fältskyttes <c>IsAuthorizedForCompetition</c>
        /// hade 2026-08-25.</para>
        ///
        /// <para>En klubbserie: <see cref="AdminAuthorizationService.IsClubAdminForClub"/>, som viker in
        /// kretsadministratören för klubbens krets. En kretsserie: kretsen måste vara en av den inloggades.
        /// En serie utan värd (nationell) hör inte till någon krets.</para>
        /// </summary>
        private async Task<bool> RegionalAdminOwnsSeriesHost(int clubId, string? region)
        {
            if (clubId > 0) return await _authorizationService.IsClubAdminForClub(clubId);
            var code = (region ?? "").Trim();
            if (code.Length == 0) return false;
            var managed = await _authorizationService.GetManagedRegions();
            return managed.Any(m => string.Equals(m, code, StringComparison.OrdinalIgnoreCase));
        }

        private Task<bool> RegionalAdminOwnsSeries(HpskSite.Models.Competitions.CompetitionSeriesInfo series) =>
            RegionalAdminOwnsSeriesHost(series.ClubId, series.RegionalFederation);

        private static int ParseClubId(string? clubId) => int.TryParse(clubId, out var id) ? id : 0;

        /// <summary>
        /// Check if a series belongs to one of the given club IDs
        /// </summary>
        private static bool IsSeriesOwnedByClubs(HpskSite.Models.Competitions.CompetitionSeriesInfo series, HashSet<int> managedClubIds)
        {
            var seriesClubId = series.ClubId;
            return seriesClubId > 0 && managedClubIds.Contains(seriesClubId);
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

        // MigrateRegistrationClubIds BORTTAGEN 2026-10-09 (sajtstrukturen fas 1a): en äldre
        // engångsflytt av memberClub-text till clubId som vandrade genom hela innehållsträdet.
        // Samma omräkning görs i anmälningsflytten till tabellen (fas 1d), där en klubb som bara
        // finns som memberClub-id blir ClubId på raden.

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
                // Trädet (även opublicerade) avgör urvalet; skrivningen går genom lagringen (C3-planen S5).
                var allIds = _contentService.GetRootContent()
                    .SelectMany(root => GetFlatDescendants(root))
                    .Where(c => c.ContentType.Alias == "competition") // competitionstore-ok: underhållsverktyget går trädet, även opublicerade
                    .Select(c => c.Id)
                    .ToList();

                static bool Invalid(string? v) => !string.IsNullOrEmpty(v) && !v.StartsWith("umb://");
                foreach (var id in allIds)
                {
                    var competition = _competitions.GetSaved(id);
                    if (competition == null || (!Invalid(competition.InvitationFile) && !Invalid(competition.ResultListFile))) continue;
                    var result = _competitions.Update(id, null, c =>
                    {
                        if (Invalid(c.InvitationFile)) c.InvitationFile = null;
                        if (Invalid(c.ResultListFile)) c.ResultListFile = null;
                    });
                    if (result.Ok) clearedCount++;
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
            // Personalregeln (C3-planen S2, Stefans beslut 2026-10-14). Förut saknades kretsens väg för en
            // kretsvärdad tävling.
            if (!await _authorizationService.HasCompetitionStaffAccessAsync(competitionId, AdminAuthorizationService.CompetitionRead.Saved))
            {
                return Ok(new { success = false, message = "You don't have permission to upload files for this competition" });
            }

            try
            {
                // Get competition
                var competition = _competitions.GetSaved(competitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                // Verify it's an external competition
                bool isExternal = competition.IsExternal;
                if (!isExternal)
                {
                    return Ok(new { success = false, message = "File upload only available for external competitions" });
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
                // Genom tävlingslagringen (C3-planen S5): publicerar bara en tävling som redan var publicerad.
                var udi = mediaItem.GetUdi().ToString();
                var linked = _competitions.Update(competitionId, null, c => c.InvitationFile = udi);
                if (!linked.Ok)
                {
                    return Ok(new { success = false, message = "Failed to link file to competition" });
                }
                if (linked.PublishProblem != null)
                {
                    return Ok(new { success = false, message = linked.PublishProblem });
                }

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

        /// <summary>
        /// Upload result list file for external competition
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadResultListFile(int competitionId, IFormFile resultListFile)
        {
            // AUTHORIZATION: Site Admin OR Competition Manager OR Club Admin OR Skjutledare
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);

            var managedClubIds = await _authorizationService.GetManagedClubIds();
            var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();

            if (!isSiteAdmin && !isCompetitionManager && !managedClubIds.Any() && !skjutledareClubIds.Any())
            {
                return Ok(new { success = false, message = "Access denied" });
            }

            try
            {
                var competition = _competitions.GetSaved(competitionId);
                if (competition == null)
                {
                    return Ok(new { success = false, message = "Competition not found" });
                }

                bool isExternal = competition.IsExternal;
                if (!isExternal)
                {
                    return Ok(new { success = false, message = "File upload only available for external competitions" });
                }

                var competitionClubId = competition.ClubId;
                bool isClubAdmin = competitionClubId > 0 && managedClubIds.Contains(competitionClubId);
                bool isSkjutledare = competitionClubId > 0 && skjutledareClubIds.Contains(competitionClubId);

                if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin && !isSkjutledare)
                {
                    return Ok(new { success = false, message = "You don't have permission to upload files for this competition" });
                }

                if (resultListFile == null || resultListFile.Length == 0)
                {
                    return Ok(new { success = false, message = "No file uploaded" });
                }

                if (resultListFile.Length > 10 * 1024 * 1024)
                {
                    return Ok(new { success = false, message = "File too large. Maximum 10 MB allowed." });
                }

                var extension = Path.GetExtension(resultListFile.FileName).ToLowerInvariant();
                var allowedExtensions = new[] { ".pdf", ".doc", ".docx" };
                if (!allowedExtensions.Contains(extension))
                {
                    return Ok(new { success = false, message = "Invalid file type. Only PDF and Word documents allowed." });
                }

                // Find or create "Competition Results" folder in Media Library
                var resultsFolder = _mediaService.GetRootMedia()
                    .FirstOrDefault(m => m.Name == "Competition Results");

                if (resultsFolder == null)
                {
                    resultsFolder = _mediaService.CreateMedia("Competition Results", -1, "Folder");
                    _mediaService.Save(resultsFolder);
                }

                string fileName = Path.GetFileName(resultListFile.FileName);
                string fileExtension = Path.GetExtension(fileName);
                string uniqueFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid()}{fileExtension}";

                var mediaItem = _mediaService.CreateMedia(fileName, resultsFolder.Id, "File");

                var tempFilePath = Path.Combine(Path.GetTempPath(), uniqueFileName);
                using (var fileStream = new FileStream(tempFilePath, FileMode.Create))
                {
                    await resultListFile.CopyToAsync(fileStream);
                }

                try
                {
                    var mediaFolderPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "media", "competition-results");

                    if (!Directory.Exists(mediaFolderPath))
                    {
                        Directory.CreateDirectory(mediaFolderPath);
                    }

                    var physicalFilePath = Path.Combine(mediaFolderPath, uniqueFileName);
                    System.IO.File.Copy(tempFilePath, physicalFilePath, true);

                    var relativePath = $"/media/competition-results/{uniqueFileName}";
                    mediaItem.SetValue("umbracoFile", relativePath);
                    mediaItem.SetValue("umbracoExtension", fileExtension.TrimStart('.'));
                    mediaItem.SetValue("umbracoBytes", resultListFile.Length.ToString());

                    var mediaSaveResult = _mediaService.Save(mediaItem);
                    if (!mediaSaveResult.Success)
                    {
                        if (System.IO.File.Exists(physicalFilePath))
                        {
                            System.IO.File.Delete(physicalFilePath);
                        }
                        return Ok(new { success = false, message = "Failed to save file to media library" });
                    }
                }
                finally
                {
                    if (System.IO.File.Exists(tempFilePath))
                    {
                        System.IO.File.Delete(tempFilePath);
                    }
                }

                // Genom tävlingslagringen (C3-planen S5): publicerar bara en tävling som redan var publicerad.
                var udi = mediaItem.GetUdi().ToString();
                var linked = _competitions.Update(competitionId, null, c => c.ResultListFile = udi);
                if (!linked.Ok)
                {
                    return Ok(new { success = false, message = "Failed to link file to competition" });
                }
                if (linked.PublishProblem != null)
                {
                    return Ok(new { success = false, message = linked.PublishProblem });
                }

                return Ok(new
                {
                    success = true,
                    message = "Result list file uploaded successfully",
                    fileName = fileName
                });
            }
            catch (Exception ex)
            {
                return Ok(new { success = false, message = "Error uploading file: " + ex.Message });
            }
        }

        /// <summary>
        /// Read a field from the request Fields dict as an int. Handles JsonElement
        /// (the System.Text.Json deserialization shape), boxed int, and string forms.
        /// Returns 0 when missing/unparseable.
        /// </summary>
        private static int ReadFieldAsInt(Dictionary<string, object>? fields, string key)
        {
            if (fields == null || !fields.TryGetValue(key, out var obj) || obj == null) return 0;
            if (obj is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Number && je.TryGetInt32(out var n)) return n;
                if (je.ValueKind == System.Text.Json.JsonValueKind.String && int.TryParse(je.GetString(), out var s)) return s;
                return 0;
            }
            if (obj is int direct) return direct;
            return int.TryParse(obj.ToString(), out var parsed) ? parsed : 0;
        }

        /// <summary>
        /// Read a field from the request Fields dict as a bool. Accepts JSON true/false and the
        /// strings "true"/"false"; anything else, or a missing field, is false.
        /// </summary>
        private static bool ReadFieldAsBool(Dictionary<string, object>? fields, string key)
        {
            if (fields == null || !fields.TryGetValue(key, out var obj) || obj == null) return false;
            if (obj is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.True) return true;
                if (je.ValueKind == System.Text.Json.JsonValueKind.String) return bool.TryParse(je.GetString(), out var sb) && sb;
                return false;
            }
            if (obj is bool b) return b;
            return bool.TryParse(obj.ToString(), out var pb) && pb;
        }

        /// <summary>
        /// Read a field from the request Fields dict as a trimmed string. Returns empty
        /// when missing/null/JsonElement.Null.
        /// </summary>
        private static string ReadFieldAsString(Dictionary<string, object>? fields, string key)
        {
            if (fields == null || !fields.TryGetValue(key, out var obj) || obj == null) return string.Empty;
            if (obj is System.Text.Json.JsonElement je)
            {
                if (je.ValueKind == System.Text.Json.JsonValueKind.Null || je.ValueKind == System.Text.Json.JsonValueKind.Undefined) return string.Empty;
                if (je.ValueKind == System.Text.Json.JsonValueKind.String) return (je.GetString() ?? string.Empty).Trim();
                return je.ToString().Trim();
            }
            return (obj.ToString() ?? string.Empty).Trim();
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
