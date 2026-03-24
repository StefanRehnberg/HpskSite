using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Models.ViewModels;
using HpskSite.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Core.Security;
using System.Text;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Admin controller for member management operations
    /// Handles CRUD operations for members, member groups, and approvals
    /// </summary>
    public class MemberAdminController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberGroupService _memberGroupService;
        private readonly AdminAuthorizationService _authService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly EmailService _emailService;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IMemberManager _memberManager;
        private readonly string _adminEmail;

        private const string ClubMemberTypeAlias = "hpskClub";

        public MemberAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberGroupService memberGroupService,
            AdminAuthorizationService authService,
            EmailService emailService,
            IWebHostEnvironment webHostEnvironment,
            IMemberManager memberManager,
            IConfiguration configuration)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberGroupService = memberGroupService;
            _authService = authService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _emailService = emailService;
            _webHostEnvironment = webHostEnvironment;
            _memberManager = memberManager;
            _adminEmail = configuration["Email:AdminEmail"] ?? "";
        }

        /// <summary>
        /// Gets members with pagination, search, and filtering
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMembers(
            int page = 1,
            int pageSize = 50,
            string? search = null,
            string? region = null,
            int? clubId = null,
            string? role = null,
            string sortBy = "lastActiveSortValue",
            bool sortDesc = true)
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get current valid clubs for validation and filtering
                var validClubs = GetClubsFromStorage().Where(c => c.IsActive).ToList();

                // Build lookup for club regions
                var clubRegionLookup = validClubs.ToDictionary(c => c.Id ?? 0, c => c.RegionalFederation);

                // Determine effective region(s) for filtering
                // For regional admins: use their managed regions if no specific region provided
                // For site admins: use provided region or no filter
                string? effectiveRegion = region;
                List<string>? effectiveRegions = null;

                if (isRegionalAdmin && !isSiteAdmin)
                {
                    if (!string.IsNullOrEmpty(region) && managedRegions.Contains(region))
                    {
                        // Specific region selected that's in their managed regions - use it
                        effectiveRegion = region;
                    }
                    else if (managedRegions.Count == 1)
                    {
                        // Single region admin - force to their region
                        effectiveRegion = managedRegions.First();
                    }
                    else
                    {
                        // Multi-region admin with no specific region - filter by all their regions
                        effectiveRegion = null;
                        effectiveRegions = managedRegions;
                    }
                }

                // Get clubs in the selected region(s) if region filter is active
                var clubsInRegion = new HashSet<int>();
                if (!string.IsNullOrEmpty(effectiveRegion))
                {
                    clubsInRegion = validClubs
                        .Where(c => c.RegionalFederation == effectiveRegion)
                        .Select(c => c.Id ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet();
                }
                else if (effectiveRegions != null && effectiveRegions.Any())
                {
                    // Multi-region filter
                    clubsInRegion = validClubs
                        .Where(c => effectiveRegions.Contains(c.RegionalFederation ?? ""))
                        .Select(c => c.Id ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet();
                }

                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                // Materialize filtered members to avoid lazy re-evaluation
                var filteredMembers = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .ToList();

                // Apply search filter
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var searchLower = search.ToLowerInvariant();
                    filteredMembers = filteredMembers.Where(m =>
                        (m.GetValue("firstName")?.ToString()?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                        (m.GetValue("lastName")?.ToString()?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                        (m.Email?.ToLowerInvariant().Contains(searchLower) ?? false)
                    ).ToList();
                }

                // Apply region filter (always enforced for regional admins)
                if (clubsInRegion.Any())
                {
                    filteredMembers = filteredMembers.Where(m =>
                    {
                        var memberClubIdStr = m.GetValue("primaryClubId")?.ToString();
                        if (int.TryParse(memberClubIdStr, out var memberClubId))
                        {
                            return clubsInRegion.Contains(memberClubId);
                        }
                        return false;
                    }).ToList();
                }

                // Apply club filter
                if (clubId.HasValue)
                {
                    filteredMembers = filteredMembers.Where(m =>
                    {
                        var memberClubIdStr = m.GetValue("primaryClubId")?.ToString();
                        if (int.TryParse(memberClubIdStr, out var memberClubId))
                        {
                            return memberClubId == clubId.Value;
                        }
                        return false;
                    }).ToList();
                }

                // Pre-load all roles once into a dictionary (avoids N+1: was called per-member
                // in role filter AND again per-member in view model creation)
                var memberRolesDict = filteredMembers.ToDictionary(
                    m => m.Id,
                    m => _memberService.GetAllRoles(m.Id).ToArray()
                );

                // Apply role filter using pre-loaded roles
                if (!string.IsNullOrEmpty(role))
                {
                    filteredMembers = filteredMembers.Where(m =>
                    {
                        var memberRoles = memberRolesDict[m.Id];

                        // Handle special cases for ClubAdmin and RegionalAdmin which have suffixes like ClubAdmin_1234
                        if (role.Equals("ClubAdmin", StringComparison.OrdinalIgnoreCase))
                        {
                            return memberRoles.Any(r => r.StartsWith("ClubAdmin_", StringComparison.OrdinalIgnoreCase));
                        }
                        if (role.Equals("RegionalAdmin", StringComparison.OrdinalIgnoreCase))
                        {
                            return memberRoles.Any(r => r.StartsWith("RegionalAdmin_", StringComparison.OrdinalIgnoreCase));
                        }

                        return memberRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
                    }).ToList();
                }

                // Transform to view models using pre-loaded roles
                var memberList = filteredMembers.Select(m => {
                    var primaryClubIdStr = m.GetValue("primaryClubId")?.ToString();
                    var primaryClubName = "No Club";
                    var hasInvalidClubReference = false;
                    int? memberPrimaryClubId = null;
                    var memberRegion = "";

                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var parsedClubId))
                    {
                        memberPrimaryClubId = parsedClubId;
                        var club = validClubs.FirstOrDefault(c => c.Id == parsedClubId);
                        if (club != null)
                        {
                            primaryClubName = club.Name;
                            memberRegion = club.RegionalFederation;
                        }
                        else
                        {
                            primaryClubName = $"⚠️ Invalid Club (ID: {parsedClubId})";
                            hasInvalidClubReference = true;
                        }
                    }

                    var lastActive = m.GetValue<DateTime?>("lastActiveDate");
                    var lastMobileActive = m.GetValue<DateTime?>("lastMobileActiveDate");
                    var phoneNumber = m.GetValue("phoneNumber")?.ToString() ?? "";
                    var memberRoles = memberRolesDict[m.Id];

                    return new MemberListItem
                    {
                        Id = m.Id,
                        Name = m.Name ?? "",
                        Email = m.Email ?? "",
                        FirstName = m.GetValue("firstName")?.ToString() ?? "",
                        LastName = m.GetValue("lastName")?.ToString() ?? "",
                        ProfilePictureUrl = m.GetValue<string>("profilePictureUrl") ?? "",
                        PrimaryClubName = primaryClubName,
                        PrimaryClubId = memberPrimaryClubId,
                        Region = memberRegion,
                        PhoneNumber = phoneNumber,
                        IsApproved = m.IsApproved,
                        Groups = memberRoles,
                        HasInvalidClubReference = hasInvalidClubReference,
                        LastActive = lastActive,
                        LastActiveDisplay = FormatLastActive(lastActive),
                        LastActiveSortValue = lastActive?.Ticks ?? 0,
                        LastMobileActive = lastMobileActive,
                        LastMobileActiveDisplay = FormatLastActive(lastMobileActive),
                        LastMobileActiveSortValue = lastMobileActive?.Ticks ?? 0
                    };
                }).ToList();

                // Apply sorting
                memberList = sortBy switch
                {
                    "name" => sortDesc
                        ? memberList.OrderByDescending(m => m.FirstName).ThenByDescending(m => m.LastName).ToList()
                        : memberList.OrderBy(m => m.FirstName).ThenBy(m => m.LastName).ToList(),
                    "primaryClubName" => sortDesc
                        ? memberList.OrderByDescending(m => m.PrimaryClubName).ToList()
                        : memberList.OrderBy(m => m.PrimaryClubName).ToList(),
                    "lastMobileActiveSortValue" => sortDesc
                        ? memberList.OrderByDescending(m => m.LastMobileActiveSortValue).ToList()
                        : memberList.OrderBy(m => m.LastMobileActiveSortValue).ToList(),
                    _ => sortDesc
                        ? memberList.OrderByDescending(m => m.LastActiveSortValue).ToList()
                        : memberList.OrderBy(m => m.LastActiveSortValue).ToList()
                };

                // Pagination
                var totalCount = memberList.Count;
                var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
                var pagedMembers = memberList
                    .Skip((page - 1) * pageSize)
                    .Take(pageSize)
                    .ToList();

                // Build club ID → name lookup for frontend badge display
                var clubLookup = validClubs
                    .Where(c => c.Id.HasValue)
                    .ToDictionary(c => c.Id.Value.ToString(), c => c.Name ?? "");

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        items = pagedMembers,
                        page = page,
                        pageSize = pageSize,
                        totalCount = totalCount,
                        totalPages = totalPages,
                        hasNextPage = page < totalPages,
                        hasPreviousPage = page > 1,
                        clubLookup = clubLookup
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading members: " + ex.Message });
            }
        }

        /// <summary>
        /// Export members to CSV with current filters
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportMembers(
            string? search = null,
            string? region = null,
            int? clubId = null,
            string? role = null)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get current valid clubs for filtering
                var validClubs = GetClubsFromStorage().Where(c => c.IsActive).ToList();

                // Get clubs in the selected region if region filter is active
                var clubsInRegion = new HashSet<int>();
                if (!string.IsNullOrEmpty(region))
                {
                    clubsInRegion = validClubs
                        .Where(c => c.RegionalFederation == region)
                        .Select(c => c.Id ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet();
                }

                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);

                // Apply search filter
                if (!string.IsNullOrWhiteSpace(search))
                {
                    var searchLower = search.ToLowerInvariant();
                    regularMembers = regularMembers.Where(m =>
                        (m.GetValue("firstName")?.ToString()?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                        (m.GetValue("lastName")?.ToString()?.ToLowerInvariant().Contains(searchLower) ?? false) ||
                        (m.Email?.ToLowerInvariant().Contains(searchLower) ?? false)
                    );
                }

                // Apply region filter
                if (!string.IsNullOrEmpty(region) && clubsInRegion.Any())
                {
                    regularMembers = regularMembers.Where(m =>
                    {
                        var memberClubIdStr = m.GetValue("primaryClubId")?.ToString();
                        if (int.TryParse(memberClubIdStr, out var memberClubId))
                        {
                            return clubsInRegion.Contains(memberClubId);
                        }
                        return false;
                    });
                }

                // Apply club filter
                if (clubId.HasValue)
                {
                    regularMembers = regularMembers.Where(m =>
                    {
                        var memberClubIdStr = m.GetValue("primaryClubId")?.ToString();
                        if (int.TryParse(memberClubIdStr, out var memberClubId))
                        {
                            return memberClubId == clubId.Value;
                        }
                        return false;
                    });
                }

                // Apply role filter
                if (!string.IsNullOrEmpty(role))
                {
                    regularMembers = regularMembers.Where(m =>
                    {
                        var memberRoles = _memberService.GetAllRoles(m.Id);

                        // Handle special cases for ClubAdmin and RegionalAdmin which have suffixes like ClubAdmin_1234
                        if (role.Equals("ClubAdmin", StringComparison.OrdinalIgnoreCase))
                        {
                            return memberRoles.Any(r => r.StartsWith("ClubAdmin_", StringComparison.OrdinalIgnoreCase));
                        }
                        if (role.Equals("RegionalAdmin", StringComparison.OrdinalIgnoreCase))
                        {
                            return memberRoles.Any(r => r.StartsWith("RegionalAdmin_", StringComparison.OrdinalIgnoreCase));
                        }

                        return memberRoles.Contains(role, StringComparer.OrdinalIgnoreCase);
                    });
                }

                // Build CSV
                var csv = new StringBuilder();
                csv.AppendLine("Namn,E-post,Telefon,Klubb,Krets,Grupper,Status,Senast aktiv (webb),Senast aktiv (app)");

                foreach (var m in regularMembers)
                {
                    var primaryClubIdStr = m.GetValue("primaryClubId")?.ToString();
                    var primaryClubName = "";
                    var memberRegion = "";

                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var parsedClubId))
                    {
                        var club = validClubs.FirstOrDefault(c => c.Id == parsedClubId);
                        if (club != null)
                        {
                            primaryClubName = club.Name;
                            memberRegion = GetRegionDescription(club.RegionalFederation);
                        }
                    }

                    var firstName = m.GetValue("firstName")?.ToString() ?? "";
                    var lastName = m.GetValue("lastName")?.ToString() ?? "";
                    var phoneNumber = m.GetValue("phoneNumber")?.ToString() ?? "";
                    var memberRoles = _memberService.GetAllRoles(m.Id)
                        .Where(g => !g.StartsWith("ClubAdmin_", StringComparison.OrdinalIgnoreCase));
                    var lastActive = m.GetValue<DateTime?>("lastActiveDate");
                    var lastMobileActive = m.GetValue<DateTime?>("lastMobileActiveDate");

                    csv.AppendLine(string.Join(",",
                        EscapeCsvField($"{firstName} {lastName}"),
                        EscapeCsvField(m.Email ?? ""),
                        EscapeCsvField(phoneNumber),
                        EscapeCsvField(primaryClubName),
                        EscapeCsvField(memberRegion),
                        EscapeCsvField(string.Join("; ", memberRoles)),
                        m.IsApproved ? "Godkänd" : "Väntande",
                        lastActive?.ToString("yyyy-MM-dd HH:mm") ?? "",
                        lastMobileActive?.ToString("yyyy-MM-dd HH:mm") ?? ""
                    ));
                }

                var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray();
                var fileName = $"medlemmar_{DateTime.Now:yyyy-MM-dd}.csv";
                return File(bytes, "text/csv; charset=utf-8", fileName);
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error exporting members: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets all regions for user management filter dropdown
        /// Returns ALL regions from the Federations enum, sorted in Swedish alphabetical order
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionsForUserManagement()
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var swedishComparer = StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false);

                // Get all regions from the enum
                var regions = Enum.GetValues(typeof(Federations.RegionalFederations))
                    .Cast<Federations.RegionalFederations>()
                    .Select(f => new
                    {
                        id = f.ToString(),
                        name = f.GetDescription()
                    })
                    .OrderBy(r => r.name, swedishComparer)
                    .ToList();

                return Json(new { success = true, regions = regions });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading regions: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets clubs for a specific region (for cascading dropdown in user management)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubsForRegion(string region)
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var clubs = GetClubsFromStorage()
                    .Where(c => c.IsActive);

                // Determine effective region(s) for filtering
                string? effectiveRegion = region;
                List<string>? effectiveRegions = null;

                if (isRegionalAdmin && !isSiteAdmin)
                {
                    if (!string.IsNullOrEmpty(region) && managedRegions.Contains(region))
                    {
                        // Specific region selected that's in their managed regions - use it
                        effectiveRegion = region;
                    }
                    else if (managedRegions.Count == 1)
                    {
                        // Single region admin - force to their region
                        effectiveRegion = managedRegions.First();
                    }
                    else
                    {
                        // Multi-region admin with no specific region - filter by all their regions
                        effectiveRegion = null;
                        effectiveRegions = managedRegions;
                    }
                }

                // Apply region filter
                if (!string.IsNullOrEmpty(effectiveRegion))
                {
                    clubs = clubs.Where(c => c.RegionalFederation == effectiveRegion);
                }
                else if (effectiveRegions != null && effectiveRegions.Any())
                {
                    // Multi-region filter
                    clubs = clubs.Where(c => effectiveRegions.Contains(c.RegionalFederation ?? ""));
                }

                var clubList = clubs
                    .OrderBy(c => c.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false))
                    .Select(c => new { id = c.Id, name = c.Name })
                    .ToList();

                return Json(new { success = true, clubs = clubList });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        private string EscapeCsvField(string field)
        {
            if (string.IsNullOrEmpty(field)) return "";
            if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            {
                return $"\"{field.Replace("\"", "\"\"")}\"";
            }
            return field;
        }

        private string GetRegionDescription(string regionCode)
        {
            if (string.IsNullOrEmpty(regionCode)) return "";
            if (Enum.TryParse<Federations.RegionalFederations>(regionCode, out var federation))
            {
                return federation.GetDescription();
            }
            return regionCode;
        }

        /// <summary>
        /// Internal class for member list items
        /// </summary>
        private class MemberListItem
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public string Email { get; set; } = "";
            public string FirstName { get; set; } = "";
            public string LastName { get; set; } = "";
            public string ProfilePictureUrl { get; set; } = "";
            public string PrimaryClubName { get; set; } = "";
            public int? PrimaryClubId { get; set; }
            public string Region { get; set; } = "";
            public string PhoneNumber { get; set; } = "";
            public bool IsApproved { get; set; }
            public string[] Groups { get; set; } = Array.Empty<string>();
            public bool HasInvalidClubReference { get; set; }
            public DateTime? LastActive { get; set; }
            public string LastActiveDisplay { get; set; } = "";
            public long LastActiveSortValue { get; set; }
            public DateTime? LastMobileActive { get; set; }
            public string LastMobileActiveDisplay { get; set; } = "";
            public long LastMobileActiveSortValue { get; set; }
        }

        /// <summary>
        /// Gets a single member by ID with all details
        /// Optimized: Clubs and groups are loaded separately by frontend on tab init
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMember(int id)
        {
            // Allow site admins or club admins for member's clubs
            if (!await _authService.CanEditMemberAsync(id))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Check if current user is site admin (for frontend to show/hide certain features)
                var isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
                var member = _memberService.GetById(id);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Parse additional club IDs
                var additionalClubIds = new List<int>();
                var memberClubIdsStr = member.GetValue("memberClubIds")?.ToString() ?? "";
                if (!string.IsNullOrEmpty(memberClubIdsStr))
                {
                    additionalClubIds = memberClubIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Where(s => int.TryParse(s.Trim(), out _))
                        .Select(s => int.Parse(s.Trim()))
                        .ToList();
                }

                // Parse primary club ID as int for frontend
                var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString() ?? "";
                int? primaryClubId = null;
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var parsedClubId))
                {
                    primaryClubId = parsedClubId;
                }

                var memberData = new
                {
                    id = member.Id,
                    firstName = member.GetValue("firstName")?.ToString() ?? "",
                    lastName = member.GetValue("lastName")?.ToString() ?? "",
                    email = member.Email,
                    phoneNumber = member.GetValue("phoneNumber")?.ToString() ?? "",
                    shooterIdNumber = member.GetValue("shooterIdNumber")?.ToString() ?? "",
                    address = member.GetValue("address")?.ToString() ?? "",
                    postalCode = member.GetValue("postalCode")?.ToString() ?? "",
                    city = member.GetValue("city")?.ToString() ?? "",
                    personNumber = member.GetValue("personNumber")?.ToString() ?? "",
                    memberSince = member.GetValue("memberSince")?.ToString() ?? "",
                    profilePictureUrl = member.GetValue<string>("profilePictureUrl") ?? "",
                    primaryClubId = primaryClubId,
                    additionalClubIds = additionalClubIds.ToArray(),
                    precisionShooterClass = member.GetValue("precisionShooterClass")?.ToString() ?? "",
                    isApproved = member.IsApproved,
                    // Filter out ClubAdmin_* groups (managed separately via club admin assignment)
                    groups = _memberService.GetAllRoles(member.Id)
                        .Where(g => !g.StartsWith("ClubAdmin_", StringComparison.OrdinalIgnoreCase))
                        .ToArray()
                };

                return Json(new { success = true, data = memberData, isSiteAdmin = isSiteAdmin });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading member: " + ex.Message });
            }
        }

        /// <summary>
        /// Creates or updates a member
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveMember(
            int? id,
            string firstName,
            string lastName,
            string email,
            string phoneNumber = "",
            string shooterIdNumber = "",
            string address = "",
            string postalCode = "",
            string city = "",
            string personNumber = "",
            string memberSince = "",
            int? primaryClubId = null,
            string additionalClubIds = "",
            string precisionShooterClass = "",
            bool isApproved = false,
            string[] groups = null)
        {
            try
            {
                // Check authorization: site admins can do anything, club admins can edit members of their clubs
                bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();

                if (id.HasValue && id.Value > 0)
                {
                    // Editing existing member - check if allowed
                    if (!await _authService.CanEditMemberAsync(id.Value))
                    {
                        return Json(new { success = false, message = "Access denied" });
                    }
                }
                else
                {
                    // Creating new member - require site admin (club admins use the Add Member flow in club admin panel)
                    if (!isSiteAdmin)
                    {
                        return Json(new { success = false, message = "Access denied" });
                    }
                }

                Console.WriteLine($"SaveMember - ID: {id}, Email: {email}, FirstName: {firstName}");
                Console.WriteLine($"PrimaryClubId: {primaryClubId}, AdditionalClubIds: {additionalClubIds}");
                Console.WriteLine($"Groups: {string.Join(", ", groups ?? new string[0])}");

                // Get current valid clubs for validation
                var clubs = GetClubsFromStorage();
                var validClubs = clubs.Where(c => c.IsActive).ToList();

                // Validate primary club ID if provided
                if (primaryClubId.HasValue)
                {
                    var primaryClub = validClubs.FirstOrDefault(c => c.Id == primaryClubId.Value);
                    if (primaryClub == null)
                    {
                        return Json(new { success = false, message = $"Invalid primary club ID: {primaryClubId}. Club does not exist or is inactive." });
                    }
                }

                // Validate additional club IDs if provided
                var additionalClubIdList = new List<int>();
                if (!string.IsNullOrEmpty(additionalClubIds))
                {
                    var additionalIds = additionalClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => int.TryParse(s, out _))
                        .Select(s => int.Parse(s))
                        .ToList();

                    foreach (var clubId in additionalIds)
                    {
                        var club = validClubs.FirstOrDefault(c => c.Id == clubId);
                        if (club == null)
                        {
                            return Json(new { success = false, message = $"Invalid additional club ID: {clubId}. Club does not exist or is inactive." });
                        }
                        additionalClubIdList.Add(clubId);
                    }
                }

                if (id.HasValue && id.Value > 0)
                {
                    // Edit existing member
                    var member = _memberService.GetById(id.Value);
                    if (member == null)
                    {
                        return Json(new { success = false, message = "Member not found" });
                    }

                    Console.WriteLine($"Updating member: {member.Name}");

                    // Track approval status change for email notification
                    bool wasApproved = member.IsApproved;
                    bool nowApproved = isApproved;

                    // Update member properties
                    member.SetValue("firstName", firstName ?? "");
                    member.SetValue("lastName", lastName ?? "");
                    member.Email = email ?? "";
                    member.Username = email ?? "";
                    member.SetValue("phoneNumber", phoneNumber ?? "");
                    member.SetValue("shooterIdNumber", shooterIdNumber ?? "");
                    member.SetValue("address", address ?? "");
                    member.SetValue("postalCode", postalCode ?? "");
                    member.SetValue("city", city ?? "");
                    member.SetValue("personNumber", personNumber ?? "");
                    member.SetValue("memberSince", memberSince ?? "");
                    member.SetValue("primaryClubId", primaryClubId);
                    member.SetValue("memberClubIds", string.Join(",", additionalClubIdList));
                    // Only update shooter class if explicitly provided (prevents clearing on general profile updates)
                    if (!string.IsNullOrEmpty(precisionShooterClass))
                    {
                        member.SetValue("precisionShooterClass", precisionShooterClass);
                    }
                    member.IsApproved = isApproved;
                    member.Name = $"{firstName} {lastName}";

                    _memberService.Save(member);
                    Console.WriteLine("Member saved successfully");

                    // Send email notification if approval status changed from false to true
                    if (!wasApproved && nowApproved)
                    {
                        try
                        {
                            // Generate auto-login token for newly approved member
                            var autoLoginToken = Guid.NewGuid().ToString("N");
                            var tokenExpiry = DateTime.UtcNow.AddDays(7);
                            member.SetValue("autoLoginToken", autoLoginToken);
                            member.SetValue("autoLoginTokenExpiry", tokenExpiry.ToString("o"));
                            _memberService.Save(member);

                            await _emailService.SendApprovalNotificationAsync(email, member.Name, autoLoginToken);
                            Console.WriteLine($"Approval email sent to {email}");
                        }
                        catch (Exception emailEx)
                        {
                            Console.WriteLine($"Failed to send approval email: {emailEx.Message}");
                            // Don't fail the operation if email fails
                        }
                    }

                    // Update groups - only site admins can modify member groups
                    if (isSiteAdmin)
                    {
                        var currentRoles = _memberService.GetAllRoles(member.Id).ToList();
                        var newGroups = groups ?? new string[0];
                        var rolesToRemove = currentRoles.Except(newGroups).ToList();
                        var rolesToAdd = newGroups.Except(currentRoles).ToList();

                        Console.WriteLine($"Current roles: {string.Join(", ", currentRoles)}");
                        Console.WriteLine($"New roles: {string.Join(", ", newGroups)}");

                        foreach (var role in rolesToRemove)
                        {
                            _memberService.DissociateRole(member.Id, role);
                        }
                        foreach (var role in rolesToAdd)
                        {
                            _memberService.AssignRole(member.Id, role);
                        }
                    }
                    else
                    {
                        Console.WriteLine("Skipping group update - user is not a site admin");
                    }

                    return Json(new { success = true, message = "Member updated successfully." });
                }
                else
                {
                    Console.WriteLine("Creating new member");

                    // Check if email already exists
                    var existingMember = _memberService.GetByEmail(email);
                    if (existingMember != null)
                    {
                        return Json(new { success = false, message = "A member with this email already exists" });
                    }

                    // Create member
                    var newMember = _memberService.CreateMember(
                        email,
                        email,
                        $"{firstName} {lastName}",
                        "hpskMember"
                    );

                    // Set custom properties
                    newMember.SetValue("firstName", firstName ?? "");
                    newMember.SetValue("lastName", lastName ?? "");
                    newMember.SetValue("phoneNumber", phoneNumber ?? "");
                    newMember.SetValue("shooterIdNumber", shooterIdNumber ?? "");
                    newMember.SetValue("address", address ?? "");
                    newMember.SetValue("postalCode", postalCode ?? "");
                    newMember.SetValue("city", city ?? "");
                    newMember.SetValue("personNumber", personNumber ?? "");
                    newMember.SetValue("memberSince", memberSince ?? "");
                    newMember.SetValue("primaryClubId", primaryClubId);
                    newMember.SetValue("memberClubIds", string.Join(",", additionalClubIdList));
                    newMember.SetValue("precisionShooterClass", precisionShooterClass ?? "");
                    newMember.IsApproved = isApproved;

                    _memberService.Save(newMember);
                    Console.WriteLine("New member saved successfully");

                    // Always assign to Users group first (default for all members)
                    _memberService.AssignRole(newMember.Id, "Users");

                    // Assign additional groups if provided
                    if (groups != null)
                    {
                        foreach (var role in groups.Where(g => g != "Users")) // Avoid duplicate Users assignment
                        {
                            _memberService.AssignRole(newMember.Id, role);
                        }
                    }

                    return Json(new { success = true, message = "Member created successfully. They will need to set their password via registration." });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SaveMember error: {ex.Message}");
                Console.WriteLine($"Stack trace: {ex.StackTrace}");
                return Json(new { success = false, message = "Error saving member: " + ex.Message });
            }
        }

        /// <summary>
        /// Deletes a member
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMember(int memberId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member != null)
                {
                    _memberService.Delete(member);
                    return Json(new { success = true, message = "Member deleted successfully" });
                }
                return Json(new { success = false, message = "Member not found" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error deleting member: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets security status for a member (lockout status, failed login attempts, etc.)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberSecurityStatus(int memberId)
        {
            if (!await _authService.CanEditMemberAsync(memberId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get identity user
                var identityUser = await _memberManager.FindByEmailAsync(member.Email ?? "");
                if (identityUser == null)
                {
                    return Json(new { success = false, message = "Identity user not found" });
                }

                var isLockedOut = await _memberManager.IsLockedOutAsync(identityUser);
                var lockoutEnd = identityUser.LockoutEnd;
                var accessFailedCount = identityUser.AccessFailedCount;
                var lastActive = member.GetValue<DateTime?>("lastActiveDate");
                var hasPassword = !string.IsNullOrEmpty(identityUser.PasswordHash);

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        memberId = memberId,
                        memberName = member.Name ?? $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
                        email = member.Email,
                        isLockedOut = isLockedOut,
                        lockoutEndUtc = lockoutEnd?.UtcDateTime,
                        lockoutEndLocal = lockoutEnd.HasValue ? lockoutEnd.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : null,
                        accessFailedCount = accessFailedCount,
                        lastActiveDate = lastActive,
                        lastActiveDisplay = FormatLastActive(lastActive),
                        hasPassword = hasPassword,
                        isApproved = member.IsApproved
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error getting security status: " + ex.Message });
            }
        }

        /// <summary>
        /// Unlocks a locked-out member
        /// Members get locked out after too many failed login attempts
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UnlockMember(int memberId)
        {
            if (!await _authService.CanEditMemberAsync(memberId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get identity user
                var identityUser = await _memberManager.FindByEmailAsync(member.Email ?? "");
                if (identityUser == null)
                {
                    return Json(new { success = false, message = "Identity user not found" });
                }

                // Check if actually locked out
                var isLockedOut = await _memberManager.IsLockedOutAsync(identityUser);
                if (!isLockedOut)
                {
                    return Json(new { success = true, message = "Member is not locked out" });
                }

                // Unlock the member
                await _memberManager.SetLockoutEndDateAsync(identityUser, null);
                await _memberManager.ResetAccessFailedCountAsync(identityUser);

                return Json(new { success = true, message = "Member unlocked successfully" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error unlocking member: " + ex.Message });
            }
        }

        /// <summary>
        /// Sets a new password for a member (admin only)
        /// This allows admins to directly set a password for members who can't receive emails
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetMemberPassword(int memberId, string newPassword)
        {
            if (!await _authService.CanEditMemberAsync(memberId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Validate password
                if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
                {
                    return Json(new { success = false, message = "Password must be at least 6 characters" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get identity user
                var identityUser = await _memberManager.FindByEmailAsync(member.Email ?? "");
                if (identityUser == null)
                {
                    return Json(new { success = false, message = "Identity user not found" });
                }

                // Generate a reset token and use it to set the new password
                var resetToken = await _memberManager.GeneratePasswordResetTokenAsync(identityUser);
                var result = await _memberManager.ResetPasswordAsync(identityUser, resetToken, newPassword);

                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    return Json(new { success = false, message = "Failed to set password: " + errors });
                }

                // Also unlock the account if it was locked
                if (await _memberManager.IsLockedOutAsync(identityUser))
                {
                    await _memberManager.SetLockoutEndDateAsync(identityUser, null);
                    await _memberManager.ResetAccessFailedCountAsync(identityUser);
                }

                Console.WriteLine($"[SetMemberPassword] Password set for member {member.Email} by admin");

                return Json(new { success = true, message = "Password set successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SetMemberPassword] Error: {ex.Message}");
                return Json(new { success = false, message = "Error setting password: " + ex.Message });
            }
        }

        /// <summary>
        /// Sends a password reset email to a member (admin-triggered)
        /// This allows admins to help members who have forgotten their password
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendPasswordResetAdmin(int memberId)
        {
            if (!await _authService.CanEditMemberAsync(memberId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                if (string.IsNullOrEmpty(member.Email))
                {
                    return Json(new { success = false, message = "Member has no email address" });
                }

                // Get identity user
                var identityUser = await _memberManager.FindByEmailAsync(member.Email);
                if (identityUser == null)
                {
                    return Json(new { success = false, message = "Identity user not found" });
                }

                // Generate password reset token
                var resetToken = await _memberManager.GeneratePasswordResetTokenAsync(identityUser);

                // Store token with expiry (24 hours)
                var tokenExpiry = DateTime.UtcNow.AddHours(24);
                member.SetValue("passwordResetToken", resetToken);
                member.SetValue("passwordResetTokenExpiry", tokenExpiry.ToString("o"));
                _memberService.Save(member);

                // Send password reset email
                var memberName = member.Name ?? $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();
                await _emailService.SendPasswordResetEmailAsync(
                    member.Email,
                    memberName,
                    resetToken
                );

                Console.WriteLine($"[SendPasswordResetAdmin] Password reset email sent to {member.Email} by admin");

                return Json(new { success = true, message = "Password reset email sent successfully" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SendPasswordResetAdmin] Error: {ex.Message}");
                return Json(new { success = false, message = "Error sending password reset: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets all available member groups
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberGroups()
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Filter out ClubAdmin_* groups to reduce response size and UI clutter
                var groups = (await _memberGroupService.GetAllAsync())
                    .Select(g => g.Name)
                    .Where(name => !name.StartsWith("ClubAdmin_", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                return Json(new { success = true, data = groups });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading groups: " + ex.Message });
            }
        }

        /// <summary>
        /// Approves a pending member and sends approval notification email
        /// Accessible by site admins or club admins for their club's members
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ApproveMember(int memberId)
        {
            Console.WriteLine($"[ApproveMember] Starting approval for memberId: {memberId}");

            // Check if current user can approve this specific member
            var canApprove = await _authService.CanApproveMemberAsync(memberId);
            Console.WriteLine($"[ApproveMember] Authorization check result: {canApprove}");

            if (!canApprove)
            {
                Console.WriteLine($"[ApproveMember] Access denied for memberId: {memberId}");
                return Json(new { success = false, message = "Access denied - you do not have permission to approve this member" });
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    Console.WriteLine($"[ApproveMember] Member not found: {memberId}");
                    return Json(new { success = false, message = "Member not found" });
                }

                Console.WriteLine($"[ApproveMember] Member found: {member.Email}, IsApproved: {member.IsApproved}");

                // Check if already approved
                if (member.IsApproved)
                {
                    Console.WriteLine($"[ApproveMember] Member already approved: {memberId}");
                    return Json(new { success = false, message = "Member is already approved" });
                }

                // Approve the member
                member.IsApproved = true;
                _memberService.Save(member);
                Console.WriteLine($"[ApproveMember] Member approved and saved: {memberId}");

                // Assign to Users group and remove from PendingApproval group
                _memberService.AssignRole(member.Id, "Users");
                _memberService.DissociateRole(member.Id, "PendingApproval");
                Console.WriteLine($"[ApproveMember] Member groups updated: {memberId}");

                // Generate auto-login token (single-use, 7-day validity)
                var autoLoginToken = Guid.NewGuid().ToString("N"); // 32-character hex string
                var tokenExpiry = DateTime.UtcNow.AddDays(7);
                member.SetValue("autoLoginToken", autoLoginToken);
                member.SetValue("autoLoginTokenExpiry", tokenExpiry.ToString("o")); // ISO 8601 format
                _memberService.Save(member);
                Console.WriteLine($"[ApproveMember] Auto-login token generated and saved for: {memberId}");

                // Resolve club info (needed for email and first-member check)
                var clubId = member.GetValue<int>("primaryClubId");
                var clubName = "Ingen klubb";
                if (clubId > 0)
                {
                    var clubNode = Services.ContentService?.GetById(clubId);
                    clubName = clubNode?.GetValue<string>("clubName") ?? clubNode?.Name ?? $"Klubb (ID: {clubId})";
                }

                // Send approval email - use firstName + lastName as fallback if Name is empty
                try
                {
                    var memberName = member.Name;
                    if (string.IsNullOrEmpty(memberName))
                    {
                        var firstName = member.GetValue<string>("firstName") ?? "";
                        var lastName = member.GetValue<string>("lastName") ?? "";
                        memberName = $"{firstName} {lastName}".Trim();
                        Console.WriteLine($"[ApproveMember] Using fallback name: '{memberName}' (original Name was empty)");
                    }
                    else
                    {
                        Console.WriteLine($"[ApproveMember] Using member.Name: '{memberName}'");
                    }

                    Console.WriteLine($"[ApproveMember] Sending approval email to: {member.Email}, Name: {memberName}");
                    await _emailService.SendApprovalNotificationAsync(
                        member.Email,
                        memberName,
                        autoLoginToken
                    );
                    Console.WriteLine($"[ApproveMember] Approval email sent successfully to: {member.Email}");

                    // Send confirmation to admin
                    var approvedBy = "Administrat\u00f6r (adminpanelen)";
                    var currentUser = await _memberManager.GetCurrentMemberAsync();
                    if (currentUser != null)
                        approvedBy = currentUser.Name ?? currentUser.Email ?? approvedBy;

                    await _emailService.SendApprovalConfirmationToAdminAsync(
                        memberName, member.Email, clubName, approvedBy);
                }
                catch (Exception emailEx)
                {
                    // Log email error but don't fail the approval
                    Console.WriteLine($"[ApproveMember] Failed to send approval email to {member.Email}: {emailEx.Message}");
                    Console.WriteLine($"[ApproveMember] Email exception stack trace: {emailEx.StackTrace}");
                }

                Console.WriteLine($"[ApproveMember] Approval completed successfully for: {memberId}");

                // Check if this is the first approved member in their club
                bool isFirstClubMember = false;
                string firstMemberClubName = "";
                int firstMemberClubId = 0;
                if (clubId > 0)
                {
                    var allMembers = _memberService.GetAll(0, int.MaxValue, out _);
                    var otherApprovedClubMembers = allMembers
                        .Where(m => m.Id != memberId && m.IsApproved &&
                                    m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
                        .Any();

                    if (!otherApprovedClubMembers)
                    {
                        isFirstClubMember = true;
                        firstMemberClubId = clubId;
                        firstMemberClubName = clubName;
                        Console.WriteLine($"[ApproveMember] First member in club {clubName} (ID: {clubId})");
                    }
                }

                return Json(new
                {
                    success = true,
                    message = "Member approved successfully",
                    isFirstClubMember,
                    firstMemberClubId,
                    firstMemberClubName,
                    memberName = member.Name ?? $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}".Trim()
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ApproveMember] Error approving member {memberId}: {ex.Message}");
                Console.WriteLine($"[ApproveMember] Exception stack trace: {ex.StackTrace}");
                return Json(new { success = false, message = "Error approving member: " + ex.Message });
            }
        }

        /// <summary>
        /// Rejects a pending member application and sends rejection notification email
        /// Accessible by site admins or club admins for their club's members
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> RejectMember(int memberId, string reason = "")
        {
            // Check if current user can reject this specific member
            if (!await _authService.CanApproveMemberAsync(memberId))
            {
                return Json(new { success = false, message = "Access denied - you do not have permission to reject this member" });
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Send rejection email before deleting
                try
                {
                    await _emailService.SendRejectionNotificationAsync(
                        member.Email,
                        member.Name,
                        reason
                    );
                }
                catch (Exception emailEx)
                {
                    // Log email error but continue with rejection
                    Console.WriteLine($"Failed to send rejection email: {emailEx.Message}");
                }

                // Delete the member account
                _memberService.Delete(member);

                return Json(new { success = true, message = "Member rejected and notification sent" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error rejecting member: " + ex.Message });
            }
        }

        /// <summary>
        /// Sends invitation email to a member to set their password
        /// Accessible by site admins or club admins for their club's members
        /// Sends confirmation emails to the admin who sent the invitation and the general admin email
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SendInvitation(int memberId)
        {
            Console.WriteLine($"[SendInvitation] Starting invitation for memberId: {memberId}");

            // Check if current user can manage this specific member
            var canApprove = await _authService.CanApproveMemberAsync(memberId);
            Console.WriteLine($"[SendInvitation] Authorization check result: {canApprove}");

            if (!canApprove)
            {
                Console.WriteLine($"[SendInvitation] Access denied for memberId: {memberId}");
                return Json(new { success = false, message = "Access denied - you do not have permission to send invitation to this member" });
            }

            // Get current admin info early for confirmation emails
            string? adminEmail = null;
            string senderName = "Admin";
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember != null)
                {
                    adminEmail = currentMember.Email;
                    var currentMemberData = _memberService.GetByEmail(currentMember.Email);
                    if (currentMemberData != null)
                    {
                        var firstName = currentMemberData.GetValue<string>("firstName") ?? "";
                        var lastName = currentMemberData.GetValue<string>("lastName") ?? "";
                        senderName = $"{firstName} {lastName}".Trim();
                        if (string.IsNullOrEmpty(senderName))
                        {
                            senderName = currentMemberData.Name ?? "Admin";
                        }
                    }
                }
                Console.WriteLine($"[SendInvitation] Admin sending invitation: {senderName} ({adminEmail})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SendInvitation] Could not get current admin info: {ex.Message}");
            }

            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    Console.WriteLine($"[SendInvitation] Member not found: {memberId}");
                    return Json(new { success = false, message = "Member not found" });
                }

                Console.WriteLine($"[SendInvitation] Member found: {member.Email}");

                // Generate invitation token (single-use, 7-day validity)
                var invitationToken = Guid.NewGuid().ToString("N");
                var tokenExpiry = DateTime.UtcNow.AddDays(7);
                member.SetValue("invitationToken", invitationToken);
                member.SetValue("invitationTokenExpiry", tokenExpiry.ToString("o"));
                _memberService.Save(member);
                Console.WriteLine($"[SendInvitation] Invitation token generated and saved for: {memberId}");

                // Get member name and club name for emails
                var memberName = member.Name;
                if (string.IsNullOrEmpty(memberName))
                {
                    var firstName = member.GetValue<string>("firstName") ?? "";
                    var lastName = member.GetValue<string>("lastName") ?? "";
                    memberName = $"{firstName} {lastName}".Trim();
                    Console.WriteLine($"[SendInvitation] Using fallback name: '{memberName}'");
                }

                // Get primary club name
                string clubName = "din klubb";
                var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                {
                    if (UmbracoContext?.Content != null)
                    {
                        var clubNode = UmbracoContext.Content.GetById(primaryClubId);
                        if (clubNode != null && clubNode.ContentType.Alias == "club")
                        {
                            clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "din klubb";
                            Console.WriteLine($"[SendInvitation] Using club name: '{clubName}'");
                        }
                    }
                }

                // Send invitation email to member
                bool invitationSuccess = false;
                string? failureReason = null;

                try
                {
                    Console.WriteLine($"[SendInvitation] Sending invitation email to: {member.Email}");
                    await _emailService.SendMemberInvitationAsync(
                        member.Email,
                        memberName,
                        invitationToken,
                        clubName
                    );
                    invitationSuccess = true;
                    Console.WriteLine($"[SendInvitation] Invitation email sent successfully to: {member.Email}");
                }
                catch (Exception emailEx)
                {
                    invitationSuccess = false;
                    failureReason = emailEx.Message;
                    Console.WriteLine($"[SendInvitation] Failed to send invitation email to {member.Email}: {emailEx.Message}");
                    Console.WriteLine($"[SendInvitation] Email exception stack trace: {emailEx.StackTrace}");
                }

                // Send confirmation email to the admin who sent the invitation
                if (!string.IsNullOrEmpty(adminEmail))
                {
                    try
                    {
                        Console.WriteLine($"[SendInvitation] Sending confirmation email to admin: {adminEmail}");
                        await _emailService.SendInvitationConfirmationAsync(
                            adminEmail,
                            senderName,
                            memberName,
                            member.Email,
                            clubName,
                            invitationSuccess,
                            failureReason
                        );
                        Console.WriteLine($"[SendInvitation] Confirmation email sent to admin: {adminEmail}");
                    }
                    catch (Exception confirmEx)
                    {
                        Console.WriteLine($"[SendInvitation] Failed to send confirmation to admin {adminEmail}: {confirmEx.Message}");
                    }
                }

                // Send confirmation email to general admin email (if different from sender)
                if (!string.IsNullOrEmpty(_adminEmail) && !string.Equals(_adminEmail, adminEmail, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Console.WriteLine($"[SendInvitation] Sending confirmation email to general admin: {_adminEmail}");
                        await _emailService.SendInvitationConfirmationAsync(
                            _adminEmail,
                            senderName,
                            memberName,
                            member.Email,
                            clubName,
                            invitationSuccess,
                            failureReason
                        );
                        Console.WriteLine($"[SendInvitation] Confirmation email sent to general admin: {_adminEmail}");
                    }
                    catch (Exception confirmEx)
                    {
                        Console.WriteLine($"[SendInvitation] Failed to send confirmation to general admin {_adminEmail}: {confirmEx.Message}");
                    }
                }

                // Return appropriate response based on invitation success
                if (invitationSuccess)
                {
                    Console.WriteLine($"[SendInvitation] Invitation sent successfully for: {memberId}");
                    return Json(new { success = true, message = "Invitation sent successfully" });
                }
                else
                {
                    Console.WriteLine($"[SendInvitation] Invitation failed for: {memberId}");
                    return Json(new { success = false, message = "Failed to send invitation email: " + failureReason });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SendInvitation] Error sending invitation for member {memberId}: {ex.Message}");
                Console.WriteLine($"[SendInvitation] Exception stack trace: {ex.StackTrace}");
                return Json(new { success = false, message = "Error sending invitation: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets all members pending approval
        /// Optionally filtered by region (filters by club's RegionalFederation)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPendingApprovals(string? region = null)
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                // Get clubs for club name lookup and region filtering
                var clubs = GetClubsFromStorage();

                // Determine effective region(s) for filtering
                string? effectiveRegion = region;
                List<string>? effectiveRegions = null;

                if (isRegionalAdmin && !isSiteAdmin)
                {
                    if (!string.IsNullOrEmpty(region) && managedRegions.Contains(region))
                    {
                        // Specific region selected that's in their managed regions - use it
                        effectiveRegion = region;
                    }
                    else if (managedRegions.Count == 1)
                    {
                        // Single region admin - force to their region
                        effectiveRegion = managedRegions.First();
                    }
                    else
                    {
                        // Multi-region admin with no specific region - filter by all their regions
                        effectiveRegion = null;
                        effectiveRegions = managedRegions;
                    }
                }

                // Build a set of club IDs in the selected region(s) (if region filter is active)
                var clubsInRegion = new HashSet<int>();
                if (!string.IsNullOrEmpty(effectiveRegion))
                {
                    clubsInRegion = clubs
                        .Where(c => c.IsActive && c.RegionalFederation == effectiveRegion)
                        .Select(c => c.Id ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet();
                }
                else if (effectiveRegions != null && effectiveRegions.Any())
                {
                    // Multi-region filter
                    clubsInRegion = clubs
                        .Where(c => c.IsActive && effectiveRegions.Contains(c.RegionalFederation ?? ""))
                        .Select(c => c.Id ?? 0)
                        .Where(id => id > 0)
                        .ToHashSet();
                }

                var pendingMembers = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
                    .Where(m => !m.IsApproved) // Only non-approved members
                    .Select(m => {
                        var primaryClubIdStr = m.GetValue("primaryClubId")?.ToString();
                        var clubName = "No Club";
                        int? parsedClubId = null;

                        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var clubId))
                        {
                            parsedClubId = clubId;
                            var club = clubs.FirstOrDefault(c => c.Id == clubId);
                            clubName = club?.Name ?? $"Unknown Club (ID: {clubId})";
                        }

                        return new {
                            Id = m.Id,
                            Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                            Email = m.Email,
                            ClubName = clubName,
                            ClubId = parsedClubId,
                            RegistrationDate = m.CreateDate
                        };
                    })
                    // Apply region filter (always enforced for regional admins)
                    .Where(m => !clubsInRegion.Any() || (m.ClubId.HasValue && clubsInRegion.Contains(m.ClubId.Value)))
                    .OrderBy(m => m.RegistrationDate)
                    .ToList();

                return Json(new { success = true, data = pendingMembers });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Assigns "Users" group to all members that don't have any groups
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> FixUsersWithoutGroups()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
                var membersFixed = 0;

                foreach (var member in regularMembers)
                {
                    var memberRoles = _memberService.GetAllRoles(member.Id);

                    // If member has no groups, assign them to Users group
                    if (!memberRoles.Any())
                    {
                        _memberService.AssignRole(member.Id, "Users");
                        membersFixed++;
                    }
                }

                return Json(new {
                    success = true,
                    message = $"Fixed {membersFixed} members without groups. All members now belong to at least the 'Users' group.",
                    membersFixed = membersFixed
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error fixing users without groups: " + ex.Message });
            }
        }

        #region Helper Methods

        /// <summary>
        /// Helper method to retrieve clubs from Umbraco content tree
        /// TODO: This should be refactored into a ClubDataService
        /// </summary>
        private List<ClubViewModel> GetClubsFromStorage()
        {
            try
            {
                var clubs = new List<ClubViewModel>();

                if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext) && umbracoContext.Content != null)
                {
                    var root = umbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root == null) return clubs;

                    // Collect all clubsPage nodes from multiple locations:
                    // 1. Direct child of root (legacy structure)
                    // 2. Under regional pages (new structure)
                    var clubsHubs = new List<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();

                    // Check for direct clubsPage under root (legacy)
                    var directClubsHub = root.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    if (directClubsHub != null)
                    {
                        clubsHubs.Add(directClubsHub);
                    }

                    // Check for clubsPage under regional pages
                    var regionalPages = root.Children().Where(c => c.ContentType.Alias == "regionalPage");
                    foreach (var region in regionalPages)
                    {
                        var regionClubsHub = region.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                        if (regionClubsHub != null)
                        {
                            clubsHubs.Add(regionClubsHub);
                        }
                    }

                    // Get all club nodes from all clubsPage hubs
                    foreach (var clubsHub in clubsHubs)
                    {
                        var clubNodes = clubsHub.Children().Where(c => c.ContentType.Alias == "club").ToList();

                        foreach (var clubNode in clubNodes)
                        {
                            var clubId = clubNode.Id;
                            var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";
                            var regionalFederation = clubNode.Value<string>("regionalFederation") ?? "";

                            var club = new ClubViewModel
                            {
                                Id = clubId,
                                Name = clubName,
                                IsActive = clubNode.IsPublished(),
                                RegionalFederation = regionalFederation
                            };

                            clubs.Add(club);
                        }
                    }
                }

                return clubs.OrderBy(c => c.Name).ToList();
            }
            catch
            {
                return new List<ClubViewModel>();
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Format last active timestamp in hybrid format
        /// Recent: "5 min ago", "2 hours ago"
        /// Older than 24h: "2025-11-16 14:30"
        /// Never: "Never"
        /// </summary>
        private string FormatLastActive(DateTime? lastActive)
        {
            if (!lastActive.HasValue)
            {
                return "Aldrig";
            }

            var elapsed = DateTime.UtcNow - lastActive.Value;

            // Less than 5 minutes
            if (elapsed.TotalMinutes < 5)
            {
                return "Just nu";
            }

            // Less than 1 hour
            if (elapsed.TotalMinutes < 60)
            {
                var minutes = (int)elapsed.TotalMinutes;
                return $"{minutes} min sedan";
            }

            // Less than 24 hours
            if (elapsed.TotalHours < 24)
            {
                var hours = (int)elapsed.TotalHours;
                return $"{hours}h sedan";
            }

            // Less than 7 days - show days
            if (elapsed.TotalDays < 7)
            {
                var days = (int)elapsed.TotalDays;
                return $"{days}d sedan";
            }

            // 7 days or more - show date only (no time)
            return lastActive.Value.ToLocalTime().ToString("yyyy-MM-dd");
        }

        #endregion

        #region Profile Picture Management

        /// <summary>
        /// Upload a profile picture for a member (admin only)
        /// POST: /umbraco/surface/MemberAdmin/UploadMemberProfilePicture
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadMemberProfilePicture(int memberId, IFormFile profilePicture)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Validate file
                if (profilePicture == null || profilePicture.Length == 0)
                {
                    return Json(new { success = false, message = "Ingen fil vald" });
                }

                // Validate file size (max 5MB)
                const long maxFileSize = 5 * 1024 * 1024; // 5MB
                if (profilePicture.Length > maxFileSize)
                {
                    return Json(new { success = false, message = "Bilden är för stor. Max storlek är 5MB." });
                }

                // Validate file type
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                var extension = Path.GetExtension(profilePicture.FileName).ToLowerInvariant();
                if (!allowedExtensions.Contains(extension))
                {
                    return Json(new { success = false, message = "Ogiltigt filformat. Tillåtna format: JPG, PNG, GIF, WebP" });
                }

                // Get member
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                // Create profile pictures directory if it doesn't exist
                var profilePicturesDir = Path.Combine(_webHostEnvironment.WebRootPath, "media", "profile-pictures");
                if (!Directory.Exists(profilePicturesDir))
                {
                    Directory.CreateDirectory(profilePicturesDir);
                }

                // Delete old profile picture if exists
                var oldPictureUrl = member.GetValue<string>("profilePictureUrl");
                if (!string.IsNullOrEmpty(oldPictureUrl))
                {
                    try
                    {
                        var oldFilePath = Path.Combine(_webHostEnvironment.WebRootPath, oldPictureUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                        if (System.IO.File.Exists(oldFilePath))
                        {
                            System.IO.File.Delete(oldFilePath);
                        }
                    }
                    catch
                    {
                        // Ignore errors deleting old file
                    }
                }

                // Generate unique filename
                var fileName = $"{member.Id}_{DateTime.UtcNow.Ticks}{extension}";
                var filePath = Path.Combine(profilePicturesDir, fileName);

                // Save file
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await profilePicture.CopyToAsync(stream);
                }

                // Update member with new profile picture URL
                var imageUrl = $"/media/profile-pictures/{fileName}";
                member.SetValue("profilePictureUrl", imageUrl);
                _memberService.Save(member);

                return Json(new { success = true, imageUrl = imageUrl, message = "Profilbild uppladdad" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error uploading profile picture: {ex.Message}");
                return Json(new { success = false, message = "Ett fel uppstod vid uppladdning: " + ex.Message });
            }
        }

        /// <summary>
        /// Remove profile picture for a member (admin only)
        /// POST: /umbraco/surface/MemberAdmin/RemoveMemberProfilePicture
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveMemberProfilePicture(int memberId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get member
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                // Get current profile picture URL
                var pictureUrl = member.GetValue<string>("profilePictureUrl");
                if (string.IsNullOrEmpty(pictureUrl))
                {
                    return Json(new { success = true, message = "Ingen profilbild att ta bort" });
                }

                // Delete file from disk
                try
                {
                    var filePath = Path.Combine(_webHostEnvironment.WebRootPath, pictureUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
                    if (System.IO.File.Exists(filePath))
                    {
                        System.IO.File.Delete(filePath);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Could not delete profile picture file: {ex.Message}");
                    // Continue anyway - we'll clear the URL
                }

                // Clear profile picture URL from member
                member.SetValue("profilePictureUrl", string.Empty);
                _memberService.Save(member);

                return Json(new { success = true, message = "Profilbild borttagen" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error removing profile picture: {ex.Message}");
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        #endregion
    }
}
