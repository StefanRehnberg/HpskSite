using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for looking up club information from Umbraco Document Type nodes.
    /// Clubs are stored as Document Type 'club' under the 'clubsPage' node.
    /// PERFORMANCE: Uses MemoryCache to avoid repeated database lookups.
    /// </summary>
    public class ClubService
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IContentService _contentService;
        private readonly IMemoryCache _cache;
        private readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);

        public ClubService(
            IUmbracoContextAccessor umbracoContextAccessor,
            IContentService contentService,
            IMemoryCache cache)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _contentService = contentService;
            _cache = cache;
        }

        /// <summary>
        /// Gets the club name by club ID.
        /// PERFORMANCE: Results are cached for 5 minutes to avoid repeated database lookups.
        /// </summary>
        /// <param name="clubId">The ID of the club content node</param>
        /// <returns>The club name, or null if club not found</returns>
        public string? GetClubNameById(int clubId)
        {
            // PERFORMANCE FIX: Check cache first
            var cacheKey = $"club_name_{clubId}";
            if (_cache.TryGetValue(cacheKey, out string? cachedName))
            {
                return cachedName;
            }

            // Cache miss - load from database
            string? clubName = null;

            // Try published content first (fast lookup for frontend contexts)
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
            {
                var clubNode = umbracoContext.Content?.GetById(clubId);
                if (clubNode?.ContentType.Alias == "club")
                {
                    clubName = clubNode.Value<string>("clubName") ?? clubNode.Name;
                }
            }

            // Fallback to IContentService (works in background/non-web contexts)
            if (clubName == null)
            {
                var club = _contentService.GetById(clubId);
                if (club?.ContentType.Alias == "club")
                {
                    clubName = club.GetValue<string>("clubName") ?? club.Name;
                }
            }

            // Store in cache (even if null to prevent repeated lookups of non-existent clubs)
            _cache.Set(cacheKey, clubName, _cacheExpiration);

            return clubName;
        }

        /// <summary>
        /// Gets basic club information by club ID.
        /// PERFORMANCE: Results are cached for 5 minutes to avoid repeated database lookups.
        /// </summary>
        /// <param name="clubId">The ID of the club content node</param>
        /// <returns>ClubInfo object, or null if club not found</returns>
        public ClubInfo? GetClubById(int clubId)
        {
            // PERFORMANCE FIX: Check cache first
            var cacheKey = $"club_info_{clubId}";
            if (_cache.TryGetValue(cacheKey, out ClubInfo? cachedInfo))
            {
                return cachedInfo;
            }

            // Cache miss - load from database
            ClubInfo? clubInfo = null;

            // Try published content first
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext))
            {
                var clubNode = umbracoContext.Content?.GetById(clubId);
                if (clubNode?.ContentType.Alias == "club")
                {
                    clubInfo = new ClubInfo
                    {
                        Id = clubNode.Id,
                        Name = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "",
                        Description = clubNode.Value<string>("description") ?? "",
                        City = clubNode.Value<string>("city") ?? "",
                        ContactEmail = clubNode.Value<string>("contactEmail") ?? ""
                    };
                }
            }

            // Fallback to IContentService
            if (clubInfo == null)
            {
                var club = _contentService.GetById(clubId);
                if (club?.ContentType.Alias == "club")
                {
                    clubInfo = new ClubInfo
                    {
                        Id = club.Id,
                        Name = club.GetValue<string>("clubName") ?? club.Name ?? "",
                        Description = club.GetValue<string>("description") ?? "",
                        City = club.GetValue<string>("city") ?? "",
                        ContactEmail = club.GetValue<string>("contactEmail") ?? ""
                    };
                }
            }

            // Store in cache (even if null to prevent repeated lookups of non-existent clubs)
            _cache.Set(cacheKey, clubInfo, _cacheExpiration);

            return clubInfo;
        }
    }

    /// <summary>
    /// Basic club information DTO
    /// </summary>
    public class ClubInfo
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string ContactEmail { get; set; } = string.Empty;
    }
}
