using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
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
using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Admin controller for club management operations
    /// Handles CRUD operations for clubs, club admin assignments, and member management
    /// </summary>
    public class ClubAdminController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberGroupService _memberGroupService;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _authService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<ClubAdminController> _logger;

        private const string ClubMemberTypeAlias = "hpskClub";

        public ClubAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberGroupService memberGroupService,
            IContentService contentService,
            AdminAuthorizationService authService,
            ILogger<ClubAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberGroupService = memberGroupService;
            _contentService = contentService;
            _authService = authService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
        }

        #region Club CRUD Operations

        [HttpGet]
        public async Task<IActionResult> GetClubs()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Return clubs without member/admin counts for performance
                // Counts are calculated when editing a specific club (GetClub)
                var clubs = GetClubsFromStorage();
                return Json(new { success = true, data = clubs });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetClub(int id)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Try to get club from content first (new hierarchical structure)
                IPublishedContent clubNode = null;
                if (UmbracoContext.Content != null)
                {
                    clubNode = UmbracoContext.Content.GetById(id);
                    if (clubNode != null && clubNode.ContentType.Alias != "club")
                    {
                        clubNode = null; // Not a club node
                    }
                }

                // Fall back to member-based clubs if content not found
                if (clubNode == null)
                {
                    var clubs = GetClubsFromStorage();
                    var club = clubs.FirstOrDefault(c => c.Id == id);

                    if (club == null)
                    {
                        return Json(new { success = false, message = "Club not found" });
                    }

                    // Get club members for contact person dropdown (exclude club member types)
                    var allMembersForClub = _memberService.GetAll(0, int.MaxValue, out _);
                    var clubMembers = allMembersForClub.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                        .Where(m => m.GetValue("primaryClubId")?.ToString() == club.Id.ToString() ||
                                    (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                    .Contains(club.Id.ToString()))
                        .Select(m => new {
                            id = m.Id,
                            name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                            email = m.Email
                        }).ToArray();

                    var clubData = new
                    {
                        Id = club.Id,
                        Name = club.Name,
                        Description = club.Description,
                        ContactPerson = club.ContactPerson,
                        ContactEmail = club.ContactEmail,
                        ContactPhone = club.ContactPhone,
                        Address = club.Address,
                        City = club.City,
                        PostalCode = club.PostalCode,
                        IsActive = club.IsActive,
                        MemberCount = club.MemberCount,
                        ClubMembers = clubMembers
                    };

                    return Json(new { success = true, data = clubData });
                }

                // Get club data from content node
                var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";

                // Get all members for counting
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias).ToList();

                // Get club members for contact person dropdown
                var clubMembersFromContent = regularMembers
                    .Where(m => m.GetValue("primaryClubId")?.ToString() == id.ToString() ||
                                (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Contains(id.ToString()))
                    .Select(m => new {
                        id = m.Id,
                        name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                        email = m.Email
                    }).ToArray();

                // Count members for this club
                var memberCount = clubMembersFromContent.Length;

                var clubDataFromContent = new
                {
                    Id = clubNode.Id,
                    Name = clubName,
                    Description = clubNode.Value<string>("description") ?? "",
                    ContactPerson = clubNode.Value<string>("contactPerson") ?? "",
                    ContactEmail = clubNode.Value<string>("contactEmail") ?? "",
                    ContactPhone = clubNode.Value<string>("contactPhone") ?? "",
                    Address = clubNode.Value<string>("address") ?? "",
                    City = clubNode.Value<string>("city") ?? "",
                    PostalCode = clubNode.Value<string>("postalCode") ?? "",
                    WebSite = clubNode.Value<string>("clubUrl") ?? "",
                    IsActive = clubNode.IsPublished(),
                    MemberCount = memberCount,
                    ClubMembers = clubMembersFromContent,
                    ClubIdNumber = clubNode.Value<int?>("clubId"),
                    RegionalFederation = clubNode.Value<string>("regionalFederation") ?? ""
                };

                return Json(new { success = true, data = clubDataFromContent });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading club: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveClub(
            int? id,
            string name,
            string description,
            string contactPerson,
            string contactEmail,
            string contactPhone,
            string webSite,
            string address,
            string city,
            string postalCode,
            bool isActive,
            int? clubIdNumber,
            string regionalFederation)
        {
            _logger.LogInformation("=== SaveClub called === ID: {Id}, Name: {Name}", id, name);

            try
            {
                if (!await _authService.IsCurrentUserAdminAsync())
                {
                    _logger.LogWarning("SaveClub: Access denied");
                    return Json(new { success = false, message = "Access denied" });
                }

                if (string.IsNullOrEmpty(name))
                {
                    _logger.LogWarning("SaveClub: Name is required");
                    return Json(new { success = false, message = "Club name is required" });
                }

                _logger.LogInformation("SaveClub: Validation passed, proceeding with save");

                if (id.HasValue && id.Value > 0)
                {
                    _logger.LogInformation("SaveClub: Editing existing club ID {Id}", id.Value);

                    // Edit existing club - try content first
                    IPublishedContent clubNode = null;
                    if (UmbracoContext.Content != null)
                    {
                        clubNode = UmbracoContext.Content.GetById(id.Value);
                        _logger.LogInformation("SaveClub: clubNode from published cache: {IsNull}", clubNode == null ? "NULL" : "Found");
                        if (clubNode != null && clubNode.ContentType.Alias != "club")
                        {
                            _logger.LogWarning("SaveClub: clubNode contentType is {Type}, not 'club'. Setting to null.", clubNode.ContentType.Alias);
                            clubNode = null;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("SaveClub: UmbracoContext.Content is NULL");
                    }

                    if (clubNode != null)
                    {
                        _logger.LogInformation("SaveClub: Proceeding with content-based club update");
                        // Update content-based club
                        var clubContent = _contentService.GetById(id.Value);
                        if (clubContent == null)
                        {
                            return Json(new { success = false, message = "Club not found" });
                        }

                        _logger.LogInformation("=== Starting duplicate name check for club edit ===");
                        _logger.LogInformation("Club being edited - ID: {ClubId}, Name: {ClubName}", id.Value, name);

                        // Check for duplicate names using ContentService (gets current saved data, not stale published cache)
                        var rootContent = _contentService.GetRootContent().FirstOrDefault();
                        if (rootContent != null)
                        {
                            _logger.LogInformation("Found root content node: {RootId}", rootContent.Id);

                            // Find clubsPage node
                            var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                            var clubsHub = rootChildren.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");

                            if (clubsHub != null)
                            {
                                _logger.LogInformation("Found clubsPage hub: {HubId}", clubsHub.Id);

                                // Get all club nodes under clubsPage
                                var allClubs = _contentService.GetPagedChildren(clubsHub.Id, 0, int.MaxValue, out _)
                                    .Where(c => c.ContentType.Alias == "club")
                                    .ToList(); // Materialize to get count

                                _logger.LogInformation("Found {ClubCount} total clubs to check against", allClubs.Count);

                                foreach (var existingClub in allClubs)
                                {
                                    var existingName = existingClub.GetValue<string>("clubName") ?? existingClub.Name ?? "";

                                    _logger.LogInformation("Checking club - ID: {ExistingId}, Name: '{ExistingName}'",
                                        existingClub.Id, existingName);

                                    if (existingClub.Id == id.Value)
                                    {
                                        _logger.LogInformation("  → Skipping (this is the club being edited)");
                                        continue; // Skip the club being edited
                                    }

                                    _logger.LogInformation("  → Comparing names: '{ExistingName}' vs '{NewName}' (case-insensitive)",
                                        existingName, name);

                                    if (existingName.Equals(name, StringComparison.OrdinalIgnoreCase))
                                    {
                                        _logger.LogWarning("  → DUPLICATE FOUND! Existing club ID {ExistingId} has same name",
                                            existingClub.Id);
                                        return Json(new { success = false, message = "A club with this name already exists" });
                                    }
                                    else
                                    {
                                        _logger.LogInformation("  → Names do not match, continuing");
                                    }
                                }

                                _logger.LogInformation("No duplicate names found, proceeding with save");
                            }
                            else
                            {
                                _logger.LogWarning("clubsPage hub not found!");
                            }
                        }
                        else
                        {
                            _logger.LogWarning("Root content not found!");
                        }

                        _logger.LogInformation("=== Duplicate name check completed successfully ===");

                        // Update club properties
                        clubContent.Name = name;
                        clubContent.SetValue("clubName", name);
                        clubContent.SetValue("description", description ?? "");
                        clubContent.SetValue("contactPerson", contactPerson ?? "");
                        clubContent.SetValue("contactEmail", contactEmail ?? "");
                        clubContent.SetValue("contactPhone", contactPhone ?? "");
                        clubContent.SetValue("clubUrl", webSite ?? "");
                        clubContent.SetValue("address", address ?? "");
                        clubContent.SetValue("city", city ?? "");
                        clubContent.SetValue("postalCode", postalCode ?? "");
                        clubContent.SetValue("clubId", clubIdNumber);
                        clubContent.SetValue("regionalFederation", regionalFederation ?? "");

                        _contentService.Save(clubContent);

                        // Publish if active, unpublish if inactive
                        if (isActive)
                        {
                            _contentService.Publish(clubContent, Array.Empty<string>());
                        }
                        else
                        {
                            _contentService.Unpublish(clubContent);
                        }

                        return Json(new { success = true, message = "Club updated successfully" });
                    }
                    else
                    {
                        return Json(new { success = false, message = "Club not found in content hierarchy" });
                    }
                }
                else
                {
                    // Create new club in content hierarchy
                    var clubs = GetClubsFromStorage();
                    if (clubs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        return Json(new { success = false, message = "A club with this name already exists" });
                    }

                    // Try to create as content first if clubsPage exists
                    if (UmbracoContext.Content != null)
                    {
                        var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                        if (root != null)
                        {
                            var clubsHub = root.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                            if (clubsHub != null)
                            {
                                // Create new club content node
                                var newClubContent = _contentService.Create(
                                    name,
                                    clubsHub.Id,
                                    "club"
                                );

                                newClubContent.SetValue("clubName", name);
                                newClubContent.SetValue("description", description ?? "");
                                newClubContent.SetValue("contactPerson", contactPerson ?? "");
                                newClubContent.SetValue("contactEmail", contactEmail ?? "");
                                newClubContent.SetValue("contactPhone", contactPhone ?? "");
                                newClubContent.SetValue("clubUrl", webSite ?? "");
                                newClubContent.SetValue("address", address ?? "");
                                newClubContent.SetValue("city", city ?? "");
                                newClubContent.SetValue("postalCode", postalCode ?? "");
                                newClubContent.SetValue("clubId", clubIdNumber);
                                newClubContent.SetValue("regionalFederation", regionalFederation ?? "");

                                _contentService.Save(newClubContent);

                                if (isActive)
                                {
                                    _contentService.Publish(newClubContent, Array.Empty<string>());
                                }

                                // Create corresponding club admin group
                                await _authService.EnsureClubAdminGroup(newClubContent.Id, name);

                                return Json(new { success = true, message = "Club created successfully" });
                            }
                        }
                    }

                    return Json(new { success = false, message = "Unable to create club: clubsPage not found in content hierarchy" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error saving club: " + ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteClub(int clubId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var clubs = GetClubsFromStorage();
                var club = clubs.FirstOrDefault(c => c.Id == clubId);

                if (club == null)
                {
                    return Json(new { success = false, message = "Club not found" });
                }

                // Check if any members are assigned to this club (primary or additional)
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
                var membersWithThisClubAsPrimary = regularMembers
                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
                    .ToList();

                var membersWithThisClubAsAdditional = regularMembers
                    .Where(m => !string.IsNullOrEmpty(m.GetValue("memberClubIds")?.ToString()) &&
                               m.GetValue("memberClubIds")?.ToString()
                                ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                ?.Select(id => id.Trim())
                                ?.Contains(clubId.ToString()) == true)
                    .ToList();

                var totalMembersWithThisClub = membersWithThisClubAsPrimary.Count + membersWithThisClubAsAdditional.Count;

                if (totalMembersWithThisClub > 0)
                {
                    var primaryCount = membersWithThisClubAsPrimary.Count;
                    var additionalCount = membersWithThisClubAsAdditional.Count;
                    var message = $"Cannot delete club '{club.Name}'. {totalMembersWithThisClub} member(s) are still linked to this club";

                    if (primaryCount > 0 && additionalCount > 0)
                    {
                        message += $" ({primaryCount} as primary club, {additionalCount} as additional club)";
                    }
                    else if (primaryCount > 0)
                    {
                        message += $" (as primary club)";
                    }
                    else
                    {
                        message += $" (as additional club)";
                    }

                    message += ". Please unlink all members from this club before deletion.";

                    // Add member details for better user experience
                    var memberDetails = new List<object>();

                    foreach (var member in membersWithThisClubAsPrimary)
                    {
                        memberDetails.Add(new
                        {
                            Id = member.Id,
                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
                            Email = member.Email,
                            LinkType = "Primary Club"
                        });
                    }

                    foreach (var member in membersWithThisClubAsAdditional)
                    {
                        memberDetails.Add(new
                        {
                            Id = member.Id,
                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
                            Email = member.Email,
                            LinkType = "Additional Club"
                        });
                    }

                    return Json(new {
                        success = false,
                        message = message,
                        linkedMembers = memberDetails
                    });
                }

                // Try to delete as content first (new hierarchical structure)
                IPublishedContent clubNode = null;
                if (UmbracoContext.Content != null)
                {
                    clubNode = UmbracoContext.Content.GetById(clubId);
                    if (clubNode != null && clubNode.ContentType.Alias != "club")
                    {
                        clubNode = null;
                    }
                }

                if (clubNode != null)
                {
                    // Delete content-based club
                    var clubContent = _contentService.GetById(clubId);
                    if (clubContent != null)
                    {
                        // Unpublish first to remove from public site
                        _contentService.Unpublish(clubContent);
                        // Then delete
                        _contentService.Delete(clubContent);
                    }
                    return Json(new { success = true, message = "Club deleted successfully" });
                }
                else
                {
                    // Fall back to member-based club deletion for backward compatibility
                    var clubMember = _memberService.GetById(clubId);
                    if (clubMember != null)
                    {
                        _memberService.Delete(clubMember);
                    }
                    return Json(new { success = true, message = "Club deleted successfully" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error deleting club: " + ex.Message });
            }
        }

        #endregion

        #region Club Validation & Cleanup

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CleanupInvalidClubReferences()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get current valid clubs
                var validClubs = GetClubsFromStorage().Where(c => c.IsActive).ToList();
                var validClubIds = validClubs.Select(c => c.Id).ToHashSet();

                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
                var membersUpdated = 0;
                var membersWithInvalidRefs = new List<string>();

                foreach (var member in regularMembers)
                {
                    var memberUpdated = false;
                    var memberName = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();

                    // ALWAYS remove primaryClubName field (data normalization)
                    var primaryClubName = member.GetValue("primaryClubName")?.ToString();
                    if (!string.IsNullOrEmpty(primaryClubName))
                    {
                        member.SetValue("primaryClubName", "");
                        memberUpdated = true;
                    }

                    // Check and clean invalid primary club IDs
                    var primaryClubId = member.GetValue("primaryClubId")?.ToString();
                    if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out var clubId) && !validClubIds.Contains(clubId))
                    {
                        member.SetValue("primaryClubId", null);
                        memberUpdated = true;
                        membersWithInvalidRefs.Add($"{memberName} (Invalid Primary Club ID: {primaryClubId})");
                    }

                    // Check and clean additional club references
                    var memberClubIds = member.GetValue("memberClubIds")?.ToString();
                    if (!string.IsNullOrEmpty(memberClubIds))
                    {
                        var clubIds = memberClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => int.TryParse(s, out _))
                            .Select(s => int.Parse(s))
                            .ToList();

                        var validAdditionalClubIds = clubIds.Where(id => validClubIds.Contains(id)).ToList();

                        if (validAdditionalClubIds.Count != clubIds.Count)
                        {
                            var invalidIds = clubIds.Except(validAdditionalClubIds);
                            member.SetValue("memberClubIds", string.Join(",", validAdditionalClubIds));
                            memberUpdated = true;
                            membersWithInvalidRefs.Add($"{memberName} (Invalid Additional Club IDs: {string.Join(", ", invalidIds)})");
                        }
                    }

                    if (memberUpdated)
                    {
                        _memberService.Save(member);
                        membersUpdated++;
                    }
                }

                var message = membersUpdated > 0
                    ? $"Cleaned up {membersUpdated} member(s) - removed redundant club name fields and invalid club references."
                    : "No cleanup needed - all member data is valid.";

                return Json(new {
                    success = true,
                    message = message,
                    membersUpdated = membersUpdated,
                    invalidReferences = membersWithInvalidRefs
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error cleaning up club references: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> CheckClubCanBeDeleted(int clubId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var clubs = GetClubsFromStorage();
                var club = clubs.FirstOrDefault(c => c.Id == clubId);

                if (club == null)
                {
                    return Json(new { success = false, message = "Club not found" });
                }

                // Check if any members are assigned to this club (primary or additional)
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
                var membersWithThisClubAsPrimary = regularMembers
                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
                    .ToList();

                var membersWithThisClubAsAdditional = regularMembers
                    .Where(m => !string.IsNullOrEmpty(m.GetValue("memberClubIds")?.ToString()) &&
                               m.GetValue("memberClubIds")?.ToString()
                                ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                ?.Select(id => id.Trim())
                                ?.Contains(clubId.ToString()) == true)
                    .ToList();

                var totalMembersWithThisClub = membersWithThisClubAsPrimary.Count + membersWithThisClubAsAdditional.Count;

                if (totalMembersWithThisClub > 0)
                {
                    var primaryCount = membersWithThisClubAsPrimary.Count;
                    var additionalCount = membersWithThisClubAsAdditional.Count;
                    var message = $"Cannot delete club '{club.Name}'. {totalMembersWithThisClub} member(s) are still linked to this club";

                    if (primaryCount > 0 && additionalCount > 0)
                    {
                        message += $" ({primaryCount} as primary club, {additionalCount} as additional club)";
                    }
                    else if (primaryCount > 0)
                    {
                        message += $" (as primary club)";
                    }
                    else
                    {
                        message += $" (as additional club)";
                    }

                    message += ". Please unlink all members from this club before deletion.";

                    // Add member details for better user experience
                    var memberDetails = new List<object>();

                    foreach (var member in membersWithThisClubAsPrimary)
                    {
                        memberDetails.Add(new
                        {
                            Id = member.Id,
                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
                            Email = member.Email,
                            LinkType = "Primary Club"
                        });
                    }

                    foreach (var member in membersWithThisClubAsAdditional)
                    {
                        memberDetails.Add(new
                        {
                            Id = member.Id,
                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
                            Email = member.Email,
                            LinkType = "Additional Club"
                        });
                    }

                    return Json(new {
                        canDelete = false,
                        message = message,
                        linkedMembers = memberDetails
                    });
                }

                return Json(new { canDelete = true, message = "Club can be deleted safely." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error checking club: " + ex.Message });
            }
        }

        #endregion

        #region Club Members

        [HttpGet]
        public async Task<IActionResult> GetClubMembers(int clubId)
        {
            // Allow site admins or club admins for this specific club
            if (!await _authService.IsClubAdminForClub(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var clubs = GetClubsFromStorage();
                var club = clubs.FirstOrDefault(c => c.Id == clubId);

                if (club == null)
                {
                    return Json(new { success = false, message = "Club not found" });
                }

                var allMembersForClubList = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var members = allMembersForClubList.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
                    .Select(m => new
                    {
                        id = m.Id,
                        memberName = $"{m.GetValue("firstName")?.ToString() ?? ""} {m.GetValue("lastName")?.ToString() ?? ""}".Trim(),
                        firstName = m.GetValue("firstName")?.ToString() ?? "",
                        lastName = m.GetValue("lastName")?.ToString() ?? "",
                        email = m.Email ?? "",
                        phoneNumber = m.GetValue("phoneNumber")?.ToString() ?? "",
                        profilePictureUrl = m.GetValue<string>("profilePictureUrl") ?? "",
                        isApproved = m.IsApproved
                    }).ToList();

                return Json(new { success = true, data = members });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading club members: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetClubMembersForClubAdmin(int clubId)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var clubMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
                               (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',')
                               .Any(id => id.Trim() == clubId.ToString()))
                    .Select(m => new
                    {
                        id = m.Id,
                        firstName = m.GetValue("firstName")?.ToString() ?? "",
                        lastName = m.GetValue("lastName")?.ToString() ?? "",
                        email = m.Email ?? "",
                        primaryClubId = int.TryParse(m.GetValue("primaryClubId")?.ToString(), out int pId) ? pId : (int?)null,
                        isApproved = m.IsApproved,
                        additionalClubIds = (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',')
                                          .Where(id => !string.IsNullOrWhiteSpace(id))
                                          .Select(id => int.TryParse(id.Trim(), out int aid) ? aid : (int?)null)
                                          .Where(id => id.HasValue)
                                          .Select(id => id.Value)
                                          .ToList()
                    }).ToList();

                return Json(new { success = true, data = clubMembers });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading club members: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetPendingApprovalsCount(int clubId)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var pendingCount = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .Where(m => !m.IsApproved &&
                               (m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
                                (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',')
                                .Any(id => id.Trim() == clubId.ToString())))
                    .Count();

                return Json(new { success = true, count = pendingCount });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading pending count: " + ex.Message });
            }
        }

        /// <summary>
        /// Adds a new member to a club (Club Admin feature)
        /// Member is auto-approved and assigned to Users group
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AddClubMember(int clubId, string firstName, string lastName, string email,
            string phoneNumber = "", string shooterIdNumber = "", string address = "", string postalCode = "",
            string city = "", string personNumber = "", string memberSince = "")
        {
            _logger.LogInformation($"[AddClubMember] Starting for clubId: {clubId}, email: {email}");

            // Authorization: Site Admin OR Club Admin for this specific club
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            bool isClubAdmin = await _authService.IsClubAdminForClub(clubId);

            if (!isSiteAdmin && !isClubAdmin)
            {
                _logger.LogWarning($"[AddClubMember] Access denied for clubId: {clubId}");
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Validate required fields
                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
                {
                    return Json(new { success = false, message = "Förnamn, efternamn och e-post är obligatoriska" });
                }

                // Validate club exists
                var clubs = GetClubsFromStorage();
                var club = clubs.FirstOrDefault(c => c.Id == clubId);
                if (club == null)
                {
                    return Json(new { success = false, message = "Klubben hittades inte" });
                }

                // Check if email already exists
                var existingMember = _memberService.GetByEmail(email);
                if (existingMember != null)
                {
                    return Json(new { success = false, message = "En medlem med denna e-postadress finns redan" });
                }

                // Create new member (password will be set via invitation)
                var member = _memberService.CreateMember(email, email, $"{firstName} {lastName}", "hpskMember");

                // Set member properties
                member.SetValue("firstName", firstName);
                member.SetValue("lastName", lastName);
                member.SetValue("primaryClubId", clubId);
                member.SetValue("phoneNumber", phoneNumber ?? "");
                member.SetValue("shooterIdNumber", shooterIdNumber ?? "");
                member.SetValue("address", address ?? "");
                member.SetValue("postalCode", postalCode ?? "");
                member.SetValue("city", city ?? "");
                member.SetValue("personNumber", personNumber ?? "");
                member.SetValue("memberSince", memberSince ?? "");

                // Auto-approve member
                member.IsApproved = true;

                // Save member
                _memberService.Save(member);

                // Assign to Users group
                _memberService.AssignRole(member.Id, "Users");

                _logger.LogInformation($"[AddClubMember] Successfully added member {member.Id} to club {clubId}");

                return Json(new
                {
                    success = true,
                    message = "Medlem tillagd",
                    memberId = member.Id
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[AddClubMember] Error adding member to club {clubId}");
                return Json(new { success = false, message = "Fel vid tillägg av medlem: " + ex.Message });
            }
        }

        #endregion

        #region Public Endpoints

        /// <summary>
        /// Public endpoint to get active clubs for registration dropdown
        /// </summary>
        [HttpGet]
        public IActionResult GetClubsForRegistration()
        {
            try
            {
                var clubs = GetClubsFromStorage();
                var activeClubs = clubs.Where(c => c.IsActive)
                    .Select(c => new {
                        id = c.Id,
                        name = c.Name
                    })
                    .OrderBy(c => c.name)
                    .ToList();

                return Json(new { success = true, clubs = activeClubs });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        /// <summary>
        /// Public endpoint to get active clubs filtered by regional federation (krets)
        /// OPTIMIZED: Server-side filtering for performance with 500+ clubs
        /// </summary>
        [HttpGet]
        public IActionResult GetClubsByKrets(string krets = "")
        {
            try
            {
                var clubs = GetClubsFromStorage();

                // Filter by krets if specified
                var filteredClubs = clubs.Where(c => c.IsActive);
                if (!string.IsNullOrEmpty(krets))
                {
                    filteredClubs = filteredClubs.Where(c => c.RegionalFederation == krets);
                }

                var result = filteredClubs
                    .Select(c => new {
                        id = c.Id,
                        name = c.Name,
                        description = c.Description,
                        city = c.City,
                        webSite = c.WebSite,
                        contactPerson = c.ContactPerson,
                        contactEmail = c.ContactEmail,
                        contactPhone = c.ContactPhone,
                        urlSegment = c.UrlSegment,
                        isActive = c.IsActive,
                        clubId = c.ClubId,
                        regionalFederation = c.RegionalFederation
                    })
                    .OrderBy(c => c.name)
                    .ToList();

                return Json(new { success = true, clubs = result });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        /// <summary>
        /// Public endpoint to get active clubs for clubs page (no authentication required)
        /// OPTIMIZED: Does not calculate member counts - not displayed on public page
        /// </summary>
        [HttpGet]
        public IActionResult GetClubsPublic()
        {
            try
            {
                var clubs = GetClubsFromStorage();

                // Note: Member counts NOT calculated for performance (not shown on public page)
                var activeClubs = clubs.Where(c => c.IsActive)
                    .Select(c => new {
                        id = c.Id,
                        name = c.Name,
                        description = c.Description,
                        city = c.City,
                        webSite = c.WebSite,
                        contactEmail = c.ContactEmail,
                        contactPhone = c.ContactPhone,
                        urlSegment = c.UrlSegment,
                        isActive = c.IsActive,
                        clubId = c.ClubId,
                        regionalFederation = c.RegionalFederation
                    })
                    .OrderBy(c => c.name)
                    .ToList();

                return Json(new { success = true, clubs = activeClubs });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
            }
        }

        /// <summary>
        /// ONE-TIME FIX: Resync clubs cache after direct SQL import
        /// Re-saves all clubs through Umbraco's content service to trigger cache refresh
        /// </summary>
        [HttpGet]
        public IActionResult ResyncClubsCache()
        {
            try
            {
                _logger.LogInformation("Starting club cache resync...");

                // Get clubsPage from content service (database query, bypasses cache)
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                var clubsHub = rootChildren.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");

                if (clubsHub == null)
                {
                    return Json(new { success = false, message = "clubsPage not found" });
                }

                // Get all club nodes directly from database
                var allClubs = _contentService.GetPagedChildren(clubsHub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "club")
                    .ToList();

                _logger.LogInformation("Found {ClubCount} clubs to resync", allClubs.Count);

                int syncedCount = 0;
                foreach (var club in allClubs)
                {
                    try
                    {
                        // Re-save and publish each club to trigger cache refresh
                        _contentService.Save(club);
                        var publishResult = _contentService.Publish(club, Array.Empty<string>());

                        if (publishResult.Success)
                        {
                            syncedCount++;
                            _logger.LogInformation("Resynced club {ClubId}: {ClubName}", club.Id, club.Name);
                        }
                        else
                        {
                            _logger.LogWarning("Failed to publish club {ClubId}: {ClubName}", club.Id, club.Name);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error resyncing club {ClubId}: {ClubName}", club.Id, club.Name);
                    }
                }

                _logger.LogInformation("Club cache resync complete: {SyncedCount}/{TotalCount} clubs synced", syncedCount, allClubs.Count);

                return Json(new {
                    success = true,
                    message = $"Successfully resynced {syncedCount} out of {allClubs.Count} clubs",
                    totalClubs = allClubs.Count,
                    syncedCount = syncedCount,
                    clubs = allClubs.Select(c => new { id = c.Id, name = c.Name }).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during club cache resync");
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        #endregion

        #region Club Admin Assignment

        /// <summary>
        /// Assigns club admin role to a member
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> AssignClubAdmin(int memberId, int clubId)
        {
            if (!await _authService.IsCurrentUserAdminAsync() && !await _authService.IsClubAdmin(clubId))
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

                // Get club from content service (clubs are Document Type nodes, NOT members)
                var club = _contentService.GetById(clubId);
                if (club == null || club.ContentType.Alias != "club")
                {
                    return Json(new { success = false, message = "Club not found" });
                }

                // Ensure club admin group exists
                var clubName = club.GetValue<string>("clubName") ?? club.Name ?? $"Club_{clubId}";
                await _authService.EnsureClubAdminGroup(clubId, clubName);

                // Assign club admin role
                var groupName = $"ClubAdmin_{clubId}";
                _memberService.AssignRole(member.Id, groupName);

                return Json(new {
                    success = true,
                    message = $"Member assigned as admin for {club.Name}"
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error assigning club admin: " + ex.Message });
            }
        }

        /// <summary>
        /// Removes club admin role from a member
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> RemoveClubAdmin(int memberId, int clubId)
        {
            if (!await _authService.IsCurrentUserAdminAsync() && !await _authService.IsClubAdmin(clubId))
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

                var groupName = $"ClubAdmin_{clubId}";
                var currentRoles = _memberService.GetAllRoles(member.Id);

                if (currentRoles.Contains(groupName))
                {
                    _memberService.DissociateRole(member.Id, groupName);
                    return Json(new { success = true, message = "Club admin role removed" });
                }

                return Json(new { success = true, message = "Member was not a club admin" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error removing club admin: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets list of club admins for a specific club
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubAdmins(int clubId)
        {
            if (!await _authService.IsCurrentUserAdminAsync() && !await _authService.IsClubAdmin(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var groupName = $"ClubAdmin_{clubId}";
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                var clubAdmins = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
                    .Where(m => _memberService.GetAllRoles(m.Id).Contains(groupName))
                    .Select(m => new {
                        Id = m.Id,
                        Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                        Email = m.Email,
                        IsApproved = m.IsApproved
                    })
                    .ToList();

                return Json(new { success = true, admins = clubAdmins });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading club admins: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets available members for club admin assignment (excluding existing admins)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAvailableMembersForClubAdmin(int clubId)
        {
            if (!await _authService.IsCurrentUserAdminAsync() && !await _authService.IsClubAdmin(clubId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var groupName = $"ClubAdmin_{clubId}";
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                // Get members who are not clubs and not already admins of this club
                var availableMembers = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
                    .Where(m => !_memberService.GetAllRoles(m.Id).Contains(groupName)) // Exclude existing club admins
                    .Where(m => m.IsApproved) // Only approved members
                    .Select(m => new {
                        Id = m.Id,
                        Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                        Email = m.Email
                    })
                    .OrderBy(m => m.Name)
                    .ToList();

                return Json(new { success = true, members = availableMembers });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading available members: " + ex.Message });
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Get clubs from content hierarchy (club document type nodes)
        /// OPTIMIZED: Does NOT load members - member/admin counts are loaded on-demand when editing
        /// </summary>
        private List<ClubViewModel> GetClubsAsContent()
        {
            try
            {
                var clubs = new List<ClubViewModel>();

                if (UmbracoContext.Content == null)
                    return clubs;

                var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                if (root == null)
                    return clubs;

                var clubNodes = new List<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent>();

                // NEW STRUCTURE: Find clubs under regional pages (Home → RegionalPage → clubsPage → clubs)
                var regionalPages = root.Children().Where(c => c.ContentType.Alias == "regionalPage").ToList();
                foreach (var regionalPage in regionalPages)
                {
                    var regionalClubsPage = regionalPage.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    if (regionalClubsPage != null)
                    {
                        var regionalClubs = regionalClubsPage.Children().Where(c => c.ContentType.Alias == "club");
                        clubNodes.AddRange(regionalClubs);
                    }
                }

                // BACKWARDS COMPATIBILITY: Also check for clubs under root-level clubsPage
                var rootClubsHub = root.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                if (rootClubsHub != null)
                {
                    var rootClubs = rootClubsHub.Children().Where(c => c.ContentType.Alias == "club");
                    clubNodes.AddRange(rootClubs);
                }

                // Convert club nodes to ClubViewModels (NO member counting - too slow with 500+ clubs)
                foreach (var clubNode in clubNodes)
                {
                    var clubId = clubNode.Id;
                    var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";

                    var club = new ClubViewModel
                    {
                        Id = clubId,
                        Name = clubName,
                        Description = clubNode.Value<string>("description") ?? "",
                        ContactPerson = clubNode.Value<string>("contactPerson") ?? "",
                        ContactEmail = clubNode.Value<string>("contactEmail") ?? "",
                        ContactPhone = clubNode.Value<string>("contactPhone") ?? "",
                        WebSite = clubNode.Value<string>("clubUrl") ?? "",
                        Address = clubNode.Value<string>("address") ?? "",
                        City = clubNode.Value<string>("city") ?? "",
                        PostalCode = clubNode.Value<string>("postalCode") ?? "",
                        UrlSegment = clubNode.UrlSegment,
                        IsActive = clubNode.IsPublished(),
                        MemberCount = 0,  // Not calculated for performance - load on demand
                        AdminCount = 0,   // Not calculated for performance - load on demand
                        ClubId = clubNode.Value<int?>("clubId"),
                        RegionalFederation = clubNode.Value<string>("regionalFederation") ?? ""
                    };

                    clubs.Add(club);
                }

                return clubs.OrderBy(c => c.Name).ToList();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to read clubs from content: {ex.Message}");
            }
        }

        private List<ClubViewModel> GetClubsFromStorage()
        {
            try
            {
                // Get clubs from content nodes (Document Type: club)
                return GetClubsAsContent();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to read clubs from storage: {ex.Message}");
            }
        }

        /// <summary>
        /// MIGRATION ENDPOINT: Fix all clubs to have clubName property set
        /// </summary>
        [HttpGet]
        public IActionResult FixClubNameProperties()
        {
            try
            {
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                var clubsHub = rootChildren.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");

                if (clubsHub == null)
                {
                    return Json(new { success = false, message = "clubsPage not found" });
                }

                var allClubs = _contentService.GetPagedChildren(clubsHub.Id, 0, int.MaxValue, out _)
                    .Where(c => c.ContentType.Alias == "club")
                    .ToList();

                int fixedCount = 0;
                foreach (var club in allClubs)
                {
                    var clubNameProp = club.GetValue<string>("clubName");
                    if (string.IsNullOrEmpty(clubNameProp))
                    {
                        // Set clubName property to match the node name
                        club.SetValue("clubName", club.Name);
                        _contentService.Save(club);
                        fixedCount++;
                        _logger.LogInformation("Fixed club {ClubId}: Set clubName to '{ClubName}'", club.Id, club.Name);
                    }
                }

                return Json(new { success = true, message = $"Fixed {fixedCount} out of {allClubs.Count} clubs", totalClubs = allClubs.Count, fixedCount = fixedCount });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fixing club name properties");
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        #endregion

        #region Regional Management

        /// <summary>
        /// Gets all regions with basic info for the admin table
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegions()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var regions = new List<object>();

                if (UmbracoContext.Content == null)
                {
                    return Json(new { success = false, message = "Content context not available" });
                }

                var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                if (root == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                // Find all regional pages
                var regionalPages = root.Children()
                    .Where(c => c.ContentType.Alias == "regionalPage")
                    .ToList();

                foreach (var regionalPage in regionalPages)
                {
                    var regionCode = regionalPage.Value<string>("regionCode") ?? "";
                    var regionName = regionalPage.Value<string>("regionName") ?? regionalPage.Name ?? "";
                    var contactPerson = regionalPage.Value<string>("contactPerson") ?? "";
                    var contactEmail = regionalPage.Value<string>("contactEmail") ?? "";

                    // Count clubs in this region
                    var clubsPage = regionalPage.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    var clubCount = clubsPage?.Children().Count(c => c.ContentType.Alias == "club") ?? 0;

                    regions.Add(new
                    {
                        regionCode = regionCode,
                        regionName = regionName,
                        contactPerson = contactPerson,
                        contactEmail = contactEmail,
                        clubCount = clubCount,
                        pageId = regionalPage.Id
                    });
                }

                // Sort by Swedish alphabetical order
                var sortedRegions = regions
                    .Cast<dynamic>()
                    .OrderBy(r => (string)r.regionName, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), false))
                    .ToList();

                return Json(new { success = true, data = sortedRegions });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading regions");
                return Json(new { success = false, message = "Error loading regions: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets full regional page data for editing
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegion(string regionCode)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (string.IsNullOrEmpty(regionCode))
                {
                    return Json(new { success = false, message = "Region code is required" });
                }

                if (UmbracoContext.Content == null)
                {
                    return Json(new { success = false, message = "Content context not available" });
                }

                var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
                if (root == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                // Find the regional page
                var regionalPage = root.Children()
                    .FirstOrDefault(c => c.ContentType.Alias == "regionalPage" &&
                                        (c.Value<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));

                if (regionalPage == null)
                {
                    return Json(new { success = false, message = "Region not found" });
                }

                var regionData = new
                {
                    regionCode = regionalPage.Value<string>("regionCode") ?? "",
                    regionName = regionalPage.Value<string>("regionName") ?? regionalPage.Name ?? "",
                    welcomeTitle = regionalPage.Value<string>("welcomeTitle") ?? "",
                    welcomeText = regionalPage.Value<string>("welcomeText") ?? "",
                    contactPerson = regionalPage.Value<string>("contactPerson") ?? "",
                    contactEmail = regionalPage.Value<string>("contactEmail") ?? "",
                    contactPhone = regionalPage.Value<string>("contactPhone") ?? "",
                    pageId = regionalPage.Id
                };

                return Json(new { success = true, data = regionData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading region {RegionCode}", regionCode);
                return Json(new { success = false, message = "Error loading region: " + ex.Message });
            }
        }

        /// <summary>
        /// Saves regional page changes
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveRegion(
            string regionCode,
            string regionName,
            string welcomeTitle,
            string welcomeText,
            string contactPerson,
            string contactEmail,
            string contactPhone)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (string.IsNullOrEmpty(regionCode))
                {
                    return Json(new { success = false, message = "Region code is required" });
                }

                // Find the regional page using content service
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                var regionalPage = rootChildren.FirstOrDefault(c =>
                    c.ContentType.Alias == "regionalPage" &&
                    (c.GetValue<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));

                if (regionalPage == null)
                {
                    return Json(new { success = false, message = "Region not found" });
                }

                // Update the regional page properties
                regionalPage.SetValue("regionName", regionName ?? "");
                regionalPage.SetValue("welcomeTitle", welcomeTitle ?? "");
                regionalPage.SetValue("welcomeText", welcomeText ?? "");
                regionalPage.SetValue("contactPerson", contactPerson ?? "");
                regionalPage.SetValue("contactEmail", contactEmail ?? "");
                regionalPage.SetValue("contactPhone", contactPhone ?? "");

                _contentService.Save(regionalPage);
                _contentService.Publish(regionalPage, Array.Empty<string>());

                _logger.LogInformation("Updated regional page {RegionCode}", regionCode);

                return Json(new { success = true, message = "Region updated successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving region {RegionCode}", regionCode);
                return Json(new { success = false, message = "Error saving region: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets current regional admins for a specific region
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegionalAdmins(string regionCode)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (string.IsNullOrEmpty(regionCode))
                {
                    return Json(new { success = false, message = "Region code is required" });
                }

                var groupName = $"RegionalAdmin_{regionCode}";
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                var regionalAdmins = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .Where(m => _memberService.GetAllRoles(m.Id).Contains(groupName))
                    .Select(m => new
                    {
                        id = m.Id,
                        name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                        email = m.Email
                    })
                    .ToList();

                return Json(new { success = true, admins = regionalAdmins });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading regional admins for {RegionCode}", regionCode);
                return Json(new { success = false, message = "Error loading regional admins: " + ex.Message });
            }
        }

        /// <summary>
        /// Gets available members for regional admin assignment (excluding existing regional admins)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAvailableMembersForRegionalAdmin(string regionCode)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (string.IsNullOrEmpty(regionCode))
                {
                    return Json(new { success = false, message = "Region code is required" });
                }

                var groupName = $"RegionalAdmin_{regionCode}";
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                var availableMembers = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .Where(m => !_memberService.GetAllRoles(m.Id).Contains(groupName))
                    .Where(m => m.IsApproved)
                    .Select(m => new
                    {
                        id = m.Id,
                        name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                        email = m.Email
                    })
                    .OrderBy(m => m.name)
                    .ToList();

                return Json(new { success = true, members = availableMembers });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error loading available members for regional admin {RegionCode}", regionCode);
                return Json(new { success = false, message = "Error loading available members: " + ex.Message });
            }
        }

        /// <summary>
        /// Assigns regional admin role to a member
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignRegionalAdmin(int memberId, string regionCode)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (string.IsNullOrEmpty(regionCode))
                {
                    return Json(new { success = false, message = "Region code is required" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Ensure regional admin group exists
                await _authService.EnsureRegionalAdminGroup(regionCode);

                // Assign regional admin role
                var groupName = $"RegionalAdmin_{regionCode}";
                _memberService.AssignRole(member.Id, groupName);

                var memberName = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();
                _logger.LogInformation("Assigned {MemberName} as regional admin for {RegionCode}", memberName, regionCode);

                return Json(new { success = true, message = $"Member assigned as regional admin for {regionCode}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error assigning regional admin for {RegionCode}", regionCode);
                return Json(new { success = false, message = "Error assigning regional admin: " + ex.Message });
            }
        }

        /// <summary>
        /// Removes regional admin role from a member
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveRegionalAdmin(int memberId, string regionCode)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                if (string.IsNullOrEmpty(regionCode))
                {
                    return Json(new { success = false, message = "Region code is required" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                var groupName = $"RegionalAdmin_{regionCode}";
                var currentRoles = _memberService.GetAllRoles(member.Id);

                if (currentRoles.Contains(groupName))
                {
                    _memberService.DissociateRole(member.Id, groupName);

                    var memberName = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();
                    _logger.LogInformation("Removed {MemberName} as regional admin for {RegionCode}", memberName, regionCode);

                    return Json(new { success = true, message = "Regional admin role removed" });
                }

                return Json(new { success = true, message = "Member was not a regional admin" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error removing regional admin for {RegionCode}", regionCode);
                return Json(new { success = false, message = "Error removing regional admin: " + ex.Message });
            }
        }

        #endregion

        #region Regional Structure Migration

        /// <summary>
        /// MIGRATION ENDPOINT: Creates regional structure and moves clubs
        /// Creates 26 regional pages under Home, each with a clubsPage child
        /// Moves existing clubs to their regional clubsPage based on regionalFederation property
        /// Creates RegionalAdmin_{regionCode} member groups for each region
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MigrateClubsToRegionalStructure(bool dryRun = true)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied. Only site administrators can run this migration." });
            }

            try
            {
                var results = new List<string>();
                var errors = new List<string>();

                // Get root content node
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                _logger.LogInformation("Starting regional structure migration (DryRun: {DryRun})", dryRun);
                results.Add($"Migration started (DryRun: {dryRun})");

                // Get existing clubsPage at root level
                var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                var existingClubsPage = rootChildren.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");

                List<Umbraco.Cms.Core.Models.IContent> existingClubs = new();
                if (existingClubsPage != null)
                {
                    existingClubs = _contentService.GetPagedChildren(existingClubsPage.Id, 0, int.MaxValue, out _)
                        .Where(c => c.ContentType.Alias == "club")
                        .ToList();
                    results.Add($"Found {existingClubs.Count} existing clubs to migrate");
                }
                else
                {
                    results.Add("No existing clubsPage found at root level - will create regional structure from scratch");
                }

                // Get all regional federations from enum
                var regionalFederations = Enum.GetValues(typeof(HpskSite.Models.Federations.RegionalFederations))
                    .Cast<HpskSite.Models.Federations.RegionalFederations>()
                    .ToList();

                results.Add($"Creating structure for {regionalFederations.Count} regions");

                var createdRegions = new List<string>();
                var movedClubs = new Dictionary<string, List<string>>();

                foreach (var federation in regionalFederations)
                {
                    var regionCode = federation.ToString();
                    var regionDescription = federation.GetDescription();

                    try
                    {
                        if (!dryRun)
                        {
                            // 1. Create regional page - use regionCode as node name for clean URLs
                            var regionalPage = _contentService.Create(
                                regionCode, // Node name (e.g., "Halland" for URL /halland/)
                                rootContent.Id,
                                "regionalPage"
                            );

                            regionalPage.SetValue("regionCode", regionCode);
                            regionalPage.SetValue("regionName", regionDescription);
                            regionalPage.SetValue("welcomeTitle", $"Välkommen till {regionDescription}");
                            regionalPage.SetValue("welcomeText", $"<p>Vi hälsar dig välkommen till {regionDescription}. Här hittar du information om våra klubbar och aktiviteter.</p>");

                            _contentService.Save(regionalPage);
                            _contentService.Publish(regionalPage, Array.Empty<string>());

                            // 2. Create clubsPage under regional page
                            var clubsPage = _contentService.Create(
                                "Klubbar",
                                regionalPage.Id,
                                "clubsPage"
                            );

                            clubsPage.SetValue("regionCode", regionCode);
                            _contentService.Save(clubsPage);
                            _contentService.Publish(clubsPage, Array.Empty<string>());

                            // 3. Create RegionalAdmin group
                            await _authService.EnsureRegionalAdminGroup(regionCode);

                            // 4. Move clubs that belong to this region
                            var clubsForRegion = existingClubs.Where(c =>
                            {
                                var clubRegion = c.GetValue<string>("regionalFederation") ?? "";
                                return clubRegion.Equals(regionCode, StringComparison.OrdinalIgnoreCase);
                            }).ToList();

                            movedClubs[regionCode] = new List<string>();

                            foreach (var club in clubsForRegion)
                            {
                                var clubName = club.GetValue<string>("clubName") ?? club.Name ?? "";
                                _contentService.Move(club, clubsPage.Id);
                                movedClubs[regionCode].Add(clubName);
                            }

                            createdRegions.Add(regionCode);
                            results.Add($"Created {regionCode}: {regionDescription} with {clubsForRegion.Count} clubs");
                        }
                        else
                        {
                            // Dry run - just count and log
                            var clubsForRegion = existingClubs.Where(c =>
                            {
                                var clubRegion = c.GetValue<string>("regionalFederation") ?? "";
                                return clubRegion.Equals(regionCode, StringComparison.OrdinalIgnoreCase);
                            }).ToList();

                            movedClubs[regionCode] = clubsForRegion.Select(c => c.GetValue<string>("clubName") ?? c.Name ?? "").ToList();
                            results.Add($"[DryRun] Would create {regionCode}: {regionDescription} with {clubsForRegion.Count} clubs");
                        }
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"Error processing region {regionCode}: {ex.Message}");
                        _logger.LogError(ex, "Error creating regional structure for {RegionCode}", regionCode);
                    }
                }

                // Count clubs without a region
                var clubsWithoutRegion = existingClubs.Where(c =>
                {
                    var clubRegion = c.GetValue<string>("regionalFederation") ?? "";
                    return string.IsNullOrEmpty(clubRegion);
                }).ToList();

                if (clubsWithoutRegion.Any())
                {
                    var clubNames = clubsWithoutRegion.Select(c => c.GetValue<string>("clubName") ?? c.Name ?? "").ToList();
                    results.Add($"WARNING: {clubsWithoutRegion.Count} clubs have no regionalFederation set: {string.Join(", ", clubNames.Take(10))}{(clubNames.Count > 10 ? "..." : "")}");
                }

                if (!dryRun && existingClubsPage != null && existingClubs.All(c =>
                {
                    var clubRegion = c.GetValue<string>("regionalFederation") ?? "";
                    return !string.IsNullOrEmpty(clubRegion);
                }))
                {
                    // All clubs have been moved - unpublish the old clubsPage
                    _contentService.Unpublish(existingClubsPage);
                    results.Add("Unpublished old root-level clubsPage (all clubs have been moved)");
                }

                _logger.LogInformation("Regional structure migration completed. Created {RegionCount} regions, processed {ClubCount} clubs",
                    createdRegions.Count, existingClubs.Count);

                return Json(new
                {
                    success = true,
                    dryRun = dryRun,
                    message = dryRun
                        ? $"Dry run completed. Would create {regionalFederations.Count} regions and move {existingClubs.Count} clubs."
                        : $"Migration completed. Created {createdRegions.Count} regions and moved clubs.",
                    results = results,
                    errors = errors,
                    clubsByRegion = movedClubs,
                    clubsWithoutRegion = clubsWithoutRegion.Select(c => new
                    {
                        id = c.Id,
                        name = c.GetValue<string>("clubName") ?? c.Name ?? ""
                    }).ToList()
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during regional structure migration");
                return Json(new { success = false, message = "Migration failed: " + ex.Message });
            }
        }

        /// <summary>
        /// Fix regional page names - renames pages from description to enum code for clean URLs
        /// Example: "Hallands Pistolskyttekrets" -> "Halland" (URL: /halland/)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> FixRegionalPageNames()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var results = new List<string>();

                // Get root content node
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                // Get existing regional pages
                var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                var existingRegionalPages = rootChildren.Where(c => c.ContentType.Alias == "regionalPage").ToList();

                if (!existingRegionalPages.Any())
                {
                    return Json(new { success = false, message = "No regional pages found to fix" });
                }

                // Build lookup from description to code
                var descriptionToCode = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var federation in Enum.GetValues(typeof(HpskSite.Models.Federations.RegionalFederations))
                    .Cast<HpskSite.Models.Federations.RegionalFederations>())
                {
                    var code = federation.ToString();
                    var description = federation.GetDescription();
                    descriptionToCode[description] = code;
                    // Also add the code itself in case it's already partially fixed
                    descriptionToCode[code] = code;
                }

                int fixedCount = 0;
                foreach (var page in existingRegionalPages)
                {
                    var currentName = page.Name ?? "";
                    var regionCode = page.GetValue<string>("regionCode") ?? "";

                    // Determine what the name should be
                    string correctName = regionCode;
                    if (string.IsNullOrEmpty(correctName) && descriptionToCode.ContainsKey(currentName))
                    {
                        correctName = descriptionToCode[currentName];
                    }

                    if (string.IsNullOrEmpty(correctName))
                    {
                        results.Add($"SKIP: Could not determine correct name for '{currentName}'");
                        continue;
                    }

                    if (currentName.Equals(correctName, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add($"OK: '{currentName}' already has correct name");
                        continue;
                    }

                    // Rename the page
                    page.Name = correctName;
                    _contentService.Save(page);
                    _contentService.Publish(page, Array.Empty<string>());

                    results.Add($"FIXED: '{currentName}' -> '{correctName}'");
                    fixedCount++;
                }

                _logger.LogInformation("Fixed {FixedCount} regional page names", fixedCount);

                return Json(new
                {
                    success = true,
                    message = $"Fixed {fixedCount} regional page names",
                    fixedCount = fixedCount,
                    totalPages = existingRegionalPages.Count,
                    results = results
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fixing regional page names");
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        /// <summary>
        /// Preview migration - shows what would be created/moved without making changes
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PreviewRegionalMigration()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get root content node
                var rootContent = _contentService.GetRootContent().FirstOrDefault();
                if (rootContent == null)
                {
                    return Json(new { success = false, message = "Root content not found" });
                }

                // Get existing clubsPage at root level
                var rootChildren = _contentService.GetPagedChildren(rootContent.Id, 0, int.MaxValue, out _);
                var existingClubsPage = rootChildren.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");

                List<Umbraco.Cms.Core.Models.IContent> existingClubs = new();
                if (existingClubsPage != null)
                {
                    existingClubs = _contentService.GetPagedChildren(existingClubsPage.Id, 0, int.MaxValue, out _)
                        .Where(c => c.ContentType.Alias == "club")
                        .ToList();
                }

                // Check if regional structure already exists
                var existingRegionalPages = rootChildren.Where(c => c.ContentType.Alias == "regionalPage").ToList();

                // Group clubs by region - use object list for JSON serialization
                var clubsByRegion = existingClubs
                    .GroupBy(c => c.GetValue<string>("regionalFederation") ?? "")
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(c => (object)new
                        {
                            id = c.Id,
                            name = c.GetValue<string>("clubName") ?? c.Name ?? ""
                        }).ToList()
                    );

                // Get all regional federations from enum
                var regionalFederations = Enum.GetValues(typeof(HpskSite.Models.Federations.RegionalFederations))
                    .Cast<HpskSite.Models.Federations.RegionalFederations>()
                    .Select(f => new
                    {
                        code = f.ToString(),
                        name = f.GetDescription(),
                        clubCount = clubsByRegion.ContainsKey(f.ToString()) ? clubsByRegion[f.ToString()].Count : 0
                    })
                    .OrderBy(r => r.name)
                    .ToList();

                // Get clubs without region
                var clubsWithoutRegion = clubsByRegion.ContainsKey("")
                    ? clubsByRegion[""]
                    : new List<object>();

                return Json(new
                {
                    success = true,
                    existingClubsPageExists = existingClubsPage != null,
                    existingClubsCount = existingClubs.Count,
                    existingRegionalPagesCount = existingRegionalPages.Count,
                    existingRegionalPages = existingRegionalPages.Select(r => r.Name).ToList(),
                    regionalFederations = regionalFederations,
                    clubsByRegion = clubsByRegion,
                    clubsWithoutRegion = clubsWithoutRegion,
                    canMigrate = existingRegionalPages.Count == 0 // Only migrate if no regional pages exist yet
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error previewing regional migration");
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        #endregion
    }
}
