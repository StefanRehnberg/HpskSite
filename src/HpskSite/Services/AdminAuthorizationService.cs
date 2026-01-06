using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using HpskSite.Models.ViewModels;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Services
{
    /// <summary>
    /// Centralized service for admin authorization checks across the application
    /// </summary>
    public class AdminAuthorizationService
    {
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberGroupService _memberGroupService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public AdminAuthorizationService(
            IMemberService memberService,
            IMemberManager memberManager,
            IMemberGroupService memberGroupService,
            IUmbracoContextAccessor umbracoContextAccessor)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _memberGroupService = memberGroupService;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        /// <summary>
        /// Checks if the current user is a site administrator
        /// </summary>
        public async Task<bool> IsCurrentUserAdminAsync()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return false;

            var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (currentMemberData == null) return false;

            var memberRoles = _memberService.GetAllRoles(currentMemberData.Id);
            return memberRoles.Contains("Administrators");
        }

        /// <summary>
        /// Checks if the current user is a club admin for a specific club
        /// Site administrators have access to all clubs
        /// </summary>
        public async Task<bool> IsClubAdminForClub(int clubId)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return false;

            // Full admins can manage any club
            if (await IsCurrentUserAdminAsync()) return true;

            var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (currentMemberData == null) return false;

            // Check if user is admin for this specific club
            var clubAdminGroup = $"ClubAdmin_{clubId}";
            var memberRoles = _memberService.GetAllRoles(currentMemberData.Id);
            return memberRoles.Contains(clubAdminGroup);
        }

        /// <summary>
        /// Gets list of club IDs that the current user can administer
        /// Returns all clubs for site administrators
        /// </summary>
        public async Task<List<int>> GetManagedClubIds()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return new List<int>();

            // Full admins can manage all clubs
            if (await IsCurrentUserAdminAsync())
            {
                return GetClubsFromContent()
                    .Where(c => c.Id.HasValue && c.Id.Value > 0)
                    .Select(c => c.Id!.Value)
                    .ToList();
            }

            var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            if (currentMemberData == null) return new List<int>();

            // Extract club IDs from ClubAdmin groups
            var memberRoles = _memberService.GetAllRoles(currentMemberData.Id);
            var clubIds = new List<int>();

            foreach (var role in memberRoles.Where(r => r.StartsWith("ClubAdmin_")))
            {
                if (int.TryParse(role.Replace("ClubAdmin_", ""), out int clubId))
                {
                    clubIds.Add(clubId);
                }
            }

            return clubIds;
        }

        /// <summary>
        /// Checks if a specific member has a specific role
        /// </summary>
        public async Task<bool> IsUserInRole(int memberId, string roleName)
        {
            try
            {
                var member = _memberService.GetById(memberId);
                if (member == null) return false;

                var roles = _memberService.GetAllRoles(member.Id);
                return roles?.Any(r => r == roleName) ?? false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Ensures that a club admin group exists for a specific club
        /// Creates the group if it doesn't exist
        /// </summary>
        public async Task<bool> EnsureClubAdminGroup(int clubId, string clubName)
        {
            try
            {
                var groupName = $"ClubAdmin_{clubId}";
                var existingGroup = await _memberGroupService.GetByNameAsync(groupName);

                if (existingGroup == null)
                {
                    var newGroup = new MemberGroup();
                    newGroup.Name = groupName;
                    // Using Umbraco v16.2 async pattern
                    await _memberGroupService.CreateAsync(newGroup);
                    return true;
                }

                return true;
            }
            catch (Exception ex)
            {
                // Log error but don't break the flow
                return false;
            }
        }

        /// <summary>
        /// Checks if current user is admin for a specific club (alias for IsClubAdminForClub)
        /// </summary>
        public async Task<bool> IsClubAdmin(int clubId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null) return false;

                var member = _memberService.GetByEmail(currentMember.Email);
                if (member == null) return false;

                var roles = _memberService.GetAllRoles(member.Id);

                // Check if user has site-wide admin or specific club admin
                return roles.Contains("Administrators") ||
                       roles.Contains($"ClubAdmin_{clubId}");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Gets the club ID that the current user can administer (if any)
        /// Returns null if user is site admin (can access all clubs)
        /// Returns null if user has no club admin role
        /// Returns clubId if user is admin of a specific club
        /// </summary>
        public async Task<int?> GetCurrentUserClubAdminId()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null) return null;

                var member = _memberService.GetByEmail(currentMember.Email);
                if (member == null) return null;

                var roles = _memberService.GetAllRoles(member.Id);

                // Site admins can access all clubs
                if (roles.Contains("Administrators")) return null; // null means all clubs

                // Find club admin role
                var clubAdminRole = roles.FirstOrDefault(r => r.StartsWith("ClubAdmin_"));
                if (clubAdminRole != null && int.TryParse(clubAdminRole.Substring(10), out int clubId))
                {
                    return clubId;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Checks if the current user is a competition manager for a specific competition
        /// Site administrators have access to all competitions
        /// </summary>
        public async Task<bool> IsCompetitionManager(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null) return false;

                // Site admins can manage all competitions
                if (await IsCurrentUserAdminAsync()) return true;

                var member = _memberService.GetByEmail(currentMember.Email);
                if (member == null) return false;

                if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext) && umbracoContext.Content != null)
                {
                    var competition = umbracoContext.Content.GetById(competitionId);
                    if (competition == null) return false;

                    var json = competition.Value<string>("competitionManagers") ?? "[]";
                    var managerIds = JsonConvert.DeserializeObject<int[]>(json) ?? Array.Empty<int>();

                    return managerIds.Contains(member.Id);
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Checks if the current user can approve a specific member
        /// Returns true if user is site admin OR club admin for the member's applied club
        /// </summary>
        public async Task<bool> CanApproveMemberAsync(int memberId)
        {
            try
            {
                // Site admins can approve anyone
                if (await IsCurrentUserAdminAsync()) return true;

                // Get the member to check their primaryClubId
                var member = _memberService.GetById(memberId);
                if (member == null) return false;

                // Get member's applied club ID (stored as int, not string)
                var primaryClubId = member.GetValue<int?>("primaryClubId");
                if (!primaryClubId.HasValue || primaryClubId.Value <= 0) return false;

                // Check if current user is club admin for this club
                return await IsClubAdminForClub(primaryClubId.Value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Helper method to retrieve clubs from Umbraco content tree
        /// </summary>
        private List<ClubViewModel> GetClubsFromContent()
        {
            try
            {
                var clubs = new List<ClubViewModel>();

                if (_umbracoContextAccessor.TryGetUmbracoContext(out var umbracoContext) && umbracoContext.Content != null)
                {
                    // Get the clubs hub page (clubsPage)
                    var root = umbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root == null) return clubs;

                    // Find clubsPage - it should be a direct child of root
                    var clubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    if (clubsHub == null) return clubs;

                    // Get all club nodes (children of clubsPage)
                    var clubNodes = clubsHub.Children.Where(c => c.ContentType.Alias == "club").ToList();

                    // Convert club nodes to ClubViewModels
                    foreach (var clubNode in clubNodes)
                    {
                        var clubId = clubNode.Id;
                        var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";

                        var club = new ClubViewModel
                        {
                            Id = clubId,
                            Name = clubName,
                            IsActive = clubNode.IsPublished()
                        };

                        clubs.Add(club);
                    }
                }

                return clubs;
            }
            catch
            {
                return new List<ClubViewModel>();
            }
        }
    }
}
