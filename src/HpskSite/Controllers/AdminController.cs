//using Microsoft.AspNetCore.Mvc;
//using Umbraco.Cms.Core.Cache;
//using Umbraco.Cms.Core.Logging;
//using Umbraco.Cms.Core.Routing;
//using Umbraco.Cms.Core.Services;
//using Umbraco.Cms.Core.Web;
//using Umbraco.Cms.Infrastructure.Persistence;
//using Umbraco.Cms.Web.Website.Controllers;
//using Umbraco.Cms.Core.Security;
//using System.Text.Json;
//using HpskSite.Models.ViewModels;
//using Umbraco.Cms.Core.Models.PublishedContent;
//using Umbraco.Cms.Core.PublishedCache;
//using Umbraco.Extensions;
//using HpskSite.Models;
//using Umbraco.Cms.Core.Models;

//namespace HpskSite.Controllers
//{
//    public class AdminController : SurfaceController
//    {
//        private readonly IMemberService _memberService;
//        private readonly IMemberGroupService _memberGroupService;
//        private readonly IMemberManager _memberManager;
//        private readonly IWebHostEnvironment _webHostEnvironment;
//        private readonly IContentService _contentService;

//        public AdminController(
//            IUmbracoContextAccessor umbracoContextAccessor,
//            IUmbracoDatabaseFactory databaseFactory,
//            ServiceContext services,
//            AppCaches appCaches,
//            IProfilingLogger profilingLogger,
//            IPublishedUrlProvider publishedUrlProvider,
//            IMemberService memberService,
//            IMemberGroupService memberGroupService,
//            IMemberManager memberManager,
//            IWebHostEnvironment webHostEnvironment,
//            IContentService contentService)
//            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
//        {
//            _memberService = memberService;
//            _memberGroupService = memberGroupService;
//            _memberManager = memberManager;
//            _webHostEnvironment = webHostEnvironment;
//            _contentService = contentService;
//        }

//        private async Task<bool> IsCurrentUserAdminAsync()
//        {
//            var currentMember = await _memberManager.GetCurrentMemberAsync();
//            if (currentMember == null) return false;

//            var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
//            if (currentMemberData == null) return false;

//            var memberRoles = _memberService.GetAllRoles(currentMemberData.Id);
//            return memberRoles.Contains("Administrators");
//        }

//        private async Task<bool> IsClubAdminForClub(int clubId)
//        {
//            var currentMember = await _memberManager.GetCurrentMemberAsync();
//            if (currentMember == null) return false;

//            // Full admins can manage any club
//            if (await IsCurrentUserAdminAsync()) return true;

//            var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
//            if (currentMemberData == null) return false;

//            // Check if user is admin for this specific club
//            var clubAdminGroup = $"ClubAdmin_{clubId}";
//            var memberRoles = _memberService.GetAllRoles(currentMemberData.Id);
//            return memberRoles.Contains(clubAdminGroup);
//        }

//        private async Task<List<int>> GetManagedClubIds()
//        {
//            var currentMember = await _memberManager.GetCurrentMemberAsync();
//            if (currentMember == null) return new List<int>();

//            // Full admins can manage all clubs
//            if (await IsCurrentUserAdminAsync())
//            {
//                return GetClubsFromStorage()
//                    .Where(c => c.Id.HasValue && c.Id.Value > 0)
//                    .Select(c => c.Id!.Value)
//                    .ToList();
//            }

//            var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
//            if (currentMemberData == null) return new List<int>();

//            // Extract club IDs from ClubAdmin groups
//            var memberRoles = _memberService.GetAllRoles(currentMemberData.Id);
//            var clubIds = new List<int>();

//            foreach (var role in memberRoles.Where(r => r.StartsWith("ClubAdmin_")))
//            {
//                if (int.TryParse(role.Replace("ClubAdmin_", ""), out int clubId))
//                {
//                    clubIds.Add(clubId);
//                }
//            }

//            return clubIds;
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetMembers()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                // Get current valid clubs for validation
//                var validClubs = GetClubsFromStorage().Where(c => c.IsActive).ToList();
//                var validClubNames = validClubs.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
//                var members = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                    .Select(m => {
//                        var primaryClubId = m.GetValue("primaryClubId")?.ToString();
//                        var primaryClubName = "No Club";
//                        var hasInvalidClubReference = false;

//                        if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out var clubId))
//                        {
//                            var club = validClubs.FirstOrDefault(c => c.Id == clubId);
//                            if (club != null)
//                            {
//                                primaryClubName = club.Name;
//                            }
//                            else
//                            {
//                                primaryClubName = $"⚠️ Invalid Club (ID: {clubId})";
//                                hasInvalidClubReference = true;
//                            }
//                        }

//                        return new
//                        {
//                            Id = m.Id,
//                            Name = m.Name,
//                            Email = m.Email,
//                            FirstName = m.GetValue("firstName")?.ToString() ?? "",
//                            LastName = m.GetValue("lastName")?.ToString() ?? "",
//                            PrimaryClubName = primaryClubName,
//                            PrimaryClubId = primaryClubId,
//                            IsApproved = m.IsApproved,
//                            Groups = _memberService.GetAllRoles(m.Id).ToArray(),
//                            HasInvalidClubReference = hasInvalidClubReference
//                        };
//                    }).ToList();

//                return Json(new { success = true, data = members });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading members: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetMember(int id)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var member = _memberService.GetById(id);
//                if (member == null)
//                {
//                    return Json(new { success = false, message = "Member not found" });
//                }

//                // Get available clubs for dropdowns
//                var clubs = GetClubsFromStorage();
//                var availableClubs = clubs.Where(c => c.IsActive).Select(c => new { id = c.Id, name = c.Name }).ToArray();

//                // Parse additional club IDs
//                var additionalClubIds = new List<int>();
//                var memberClubIdsStr = member.GetValue("memberClubIds")?.ToString() ?? "";
//                if (!string.IsNullOrEmpty(memberClubIdsStr))
//                {
//                    additionalClubIds = memberClubIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries)
//                        .Where(s => int.TryParse(s.Trim(), out _))
//                        .Select(s => int.Parse(s.Trim()))
//                        .ToList();
//                }

//                var memberData = new
//                {
//                    Id = member.Id,
//                    FirstName = member.GetValue("firstName")?.ToString() ?? "",
//                    LastName = member.GetValue("lastName")?.ToString() ?? "",
//                    Email = member.Email,
//                    PrimaryClubName = member.GetValue("primaryClubName")?.ToString() ?? "",
//                    PrimaryClubId = member.GetValue("primaryClubId")?.ToString() ?? "",
//                    MemberClubIds = memberClubIdsStr,
//                    AdditionalClubIds = additionalClubIds.ToArray(),
//                    IsApproved = member.IsApproved,
//                    Groups = _memberService.GetAllRoles(member.Id).ToArray(),
//                    AvailableGroups = _memberGroupService.GetAllAsync().Result.Select(g => g.Name).ToArray(),
//                    AvailableClubs = availableClubs
//                };

//                return Json(new { success = true, data = memberData });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading member: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> SaveMember(
//            int? id,
//            string firstName,
//            string lastName,
//            string email,
//            int? primaryClubId,
//            string additionalClubIds,
//            bool isApproved,
//            string[] groups)
//        {
//            try
//            {
//                if (!await IsCurrentUserAdminAsync())
//                {
//                    return Json(new { success = false, message = "Access denied" });
//                }

//                Console.WriteLine($"SaveMember - ID: {id}, Email: {email}, FirstName: {firstName}");
//                Console.WriteLine($"PrimaryClubId: {primaryClubId}, AdditionalClubIds: {additionalClubIds}");
//                Console.WriteLine($"Groups: {string.Join(", ", groups ?? new string[0])}");

//                // Get current valid clubs for validation
//                var clubs = GetClubsFromStorage();
//                var validClubs = clubs.Where(c => c.IsActive).ToList();

//                // Validate primary club ID if provided
//                if (primaryClubId.HasValue)
//                {
//                    var primaryClub = validClubs.FirstOrDefault(c => c.Id == primaryClubId.Value);
//                    if (primaryClub == null)
//                    {
//                        return Json(new { success = false, message = $"Invalid primary club ID: {primaryClubId}. Club does not exist or is inactive." });
//                    }
//                }

//                // Validate additional club IDs if provided
//                var additionalClubIdList = new List<int>();
//                if (!string.IsNullOrEmpty(additionalClubIds))
//                {
//                    var additionalIds = additionalClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
//                        .Select(s => s.Trim())
//                        .Where(s => int.TryParse(s, out _))
//                        .Select(s => int.Parse(s))
//                        .ToList();

//                    foreach (var clubId in additionalIds)
//                    {
//                        var club = validClubs.FirstOrDefault(c => c.Id == clubId);
//                        if (club == null)
//                        {
//                            return Json(new { success = false, message = $"Invalid additional club ID: {clubId}. Club does not exist or is inactive." });
//                        }
//                        additionalClubIdList.Add(clubId);
//                    }
//                }

//                if (id.HasValue && id.Value > 0)
//                {
//                    // Edit existing member
//                    var member = _memberService.GetById(id.Value);
//                    if (member == null)
//                    {
//                        return Json(new { success = false, message = "Member not found" });
//                    }

//                    Console.WriteLine($"Updating member: {member.Name}");

//                    // Update member properties
//                    member.SetValue("firstName", firstName ?? "");
//                    member.SetValue("lastName", lastName ?? "");
//                    member.Email = email ?? "";
//                    member.Username = email ?? "";
//                    member.SetValue("primaryClubId", primaryClubId);
//                    member.SetValue("memberClubIds", string.Join(",", additionalClubIdList));
//                    member.IsApproved = isApproved;
//                    member.Name = $"{firstName} {lastName}";

//                    _memberService.Save(member);
//                    Console.WriteLine("Member saved successfully");

//                    // Update groups
//                    var currentRoles = _memberService.GetAllRoles(member.Id).ToList();
//                    var newGroups = groups ?? new string[0];
//                    var rolesToRemove = currentRoles.Except(newGroups).ToList();
//                    var rolesToAdd = newGroups.Except(currentRoles).ToList();

//                    Console.WriteLine($"Current roles: {string.Join(", ", currentRoles)}");
//                    Console.WriteLine($"New roles: {string.Join(", ", newGroups)}");

//                    foreach (var role in rolesToRemove)
//                    {
//                        _memberService.DissociateRole(member.Id, role);
//                    }
//                    foreach (var role in rolesToAdd)
//                    {
//                        _memberService.AssignRole(member.Id, role);
//                    }

//                    return Json(new { success = true, message = "Member updated successfully." });
//                }
//                else
//                {
//                    Console.WriteLine("Creating new member");
                    
//                    // Check if email already exists
//                    var existingMember = _memberService.GetByEmail(email);
//                    if (existingMember != null)
//                    {
//                        return Json(new { success = false, message = "A member with this email already exists" });
//                    }

//                    // Create member
//                    var newMember = _memberService.CreateMember(
//                        email, 
//                        email, 
//                        $"{firstName} {lastName}", 
//                        "hpskMember"
//                    );

//                    // Set custom properties
//                    newMember.SetValue("firstName", firstName ?? "");
//                    newMember.SetValue("lastName", lastName ?? "");
//                    newMember.SetValue("primaryClubId", primaryClubId);
//                    newMember.SetValue("memberClubIds", string.Join(",", additionalClubIdList));
//                    newMember.IsApproved = isApproved;

//                    _memberService.Save(newMember);
//                    Console.WriteLine("New member saved successfully");

//                    // Always assign to Users group first (default for all members)
//                    _memberService.AssignRole(newMember.Id, "Users");

//                    // Assign additional groups if provided
//                    if (groups != null)
//                    {
//                        foreach (var role in groups.Where(g => g != "Users")) // Avoid duplicate Users assignment
//                        {
//                            _memberService.AssignRole(newMember.Id, role);
//                        }
//                    }

//                    return Json(new { success = true, message = "Member created successfully. They will need to set their password via registration." });
//                }
//            }
//            catch (Exception ex)
//            {
//                Console.WriteLine($"SaveMember error: {ex.Message}");
//                Console.WriteLine($"Stack trace: {ex.StackTrace}");
//                return Json(new { success = false, message = "Error saving member: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> DeleteMember(int memberId)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var member = _memberService.GetById(memberId);
//                if (member != null)
//                {
//                    _memberService.Delete(member);
//                    return Json(new { success = true, message = "Member deleted successfully" });
//                }
//                return Json(new { success = false, message = "Member not found" });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error deleting member: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetMemberGroups()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var groups = _memberGroupService.GetAllAsync().Result.Select(g => g.Name).ToArray();
//                return Json(new { success = true, data = groups });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading groups: " + ex.Message });
//            }
//        }

//        // Club Management Methods - Using Umbraco Member Service (Club as Member Type)
//        private const string ClubMemberTypeAlias = "hpskClub";

//        /// <summary>
//        /// NEW: Get clubs from content hierarchy (club document type nodes)
//        /// </summary>
//        private List<ClubViewModel> GetClubsAsContent()
//        {
//            try
//            {
//                var clubs = new List<ClubViewModel>();

//                if (UmbracoContext.Content == null)
//                    return clubs;

//                // Get the clubs hub page (clubsPage)
//                var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
//                if (root == null)
//                    return clubs;

//                // Find clubsPage - it should be a direct child of root
//                var clubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
//                if (clubsHub == null)
//                    return clubs;

//                // Get all club nodes (children of clubsPage)
//                var clubNodes = clubsHub.Children.Where(c => c.ContentType.Alias == "club").ToList();

//                // Get all members for counting
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out _)
//                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                    .ToList();

//                // Convert club nodes to ClubViewModels
//                foreach (var clubNode in clubNodes)
//                {
//                    var clubId = clubNode.Id;
//                    var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";

//                    var club = new ClubViewModel
//                    {
//                        Id = clubId,
//                        Name = clubName,
//                        Description = clubNode.Value<string>("description") ?? "",
//                        ContactPerson = clubNode.Value<string>("contactPerson") ?? "",
//                        ContactEmail = clubNode.Value<string>("contactEmail") ?? "",
//                        ContactPhone = clubNode.Value<string>("contactPhone") ?? "",
//                        WebSite = clubNode.Value<string>("clubUrl") ?? "",
//                        Address = clubNode.Value<string>("address") ?? "",
//                        City = clubNode.Value<string>("city") ?? "",
//                        PostalCode = clubNode.Value<string>("postalCode") ?? "",
//                        UrlSegment = clubNode.UrlSegment,
//                        IsActive = clubNode.IsPublished(),
//                        MemberCount = 0,
//                        AdminCount = 0
//                    };

//                    // Count members assigned to this club (by content node ID)
//                    var memberCount = allMembers.Count(m =>
//                        m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
//                        (m.GetValue("memberClubIds")?.ToString()?.Split(',')
//                            .Select(s => s.Trim())
//                            .Contains(clubId.ToString()) ?? false));

//                    club.MemberCount = memberCount;

//                    // Count club admins
//                    var clubAdminGroupName = $"ClubAdmin_{clubId}";
//                    var adminCount = allMembers.Count(m =>
//                        _memberService.GetAllRoles(m.Id).Contains(clubAdminGroupName));

//                    club.AdminCount = adminCount;

//                    clubs.Add(club);
//                }

//                return clubs.OrderBy(c => c.Name).ToList();
//            }
//            catch (Exception ex)
//            {
//                throw new Exception($"Failed to read clubs from content: {ex.Message}");
//            }
//        }

//        private List<ClubViewModel> GetClubsFromStorage()
//        {
//            try
//            {
//                // Get clubs from content nodes (Document Type: club)
//                return GetClubsAsContent();
//            }
//            catch (Exception ex)
//            {
//                throw new Exception($"Failed to read clubs from storage: {ex.Message}");
//            }
//        }

//        [HttpPost]
//        public async Task<IActionResult> SeedRandomMembers(int count = 100)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var rnd = new Random();

//                // Load clubs (stored as members of type hpskClub)
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out _);
//                var clubs = allMembers.Where(m => m.ContentType.Alias == ClubMemberTypeAlias).ToList();
//                if (clubs.Count == 0)
//                {
//                    return Json(new { success = false, message = "No clubs found. Create clubs first." });
//                }

//                // Swedish names (sample lists)
//                string[] firstNames = new[] {
//                    "Anna","Erik","Lars","Eva","Karl","Maria","Johan","Karin","Per","Sara",
//                    "Anders","Ingrid","Nils","Lena","Björn","Elin","Oskar","Sofia","Fredrik","Maja",
//                    "Henrik","Emma","Mats","Ida","Niklas","Julia","Rolf","Gunilla","Pontus","Agnes"
//                };
//                string[] lastNames = new[] {
//                    "Johansson","Andersson","Karlsson","Nilsson","Eriksson","Larsson","Olsson","Persson","Svensson","Gustafsson",
//                    "Pettersson","Jonsson","Jansson","Hansson","Bengtsson","Jönsson","Lindberg","Jakobsson","Magnusson","Olofsson",
//                    "Lindström","Lundberg","Lundgren","Axelsson","Berg","Viklund","Nyström","Sandberg","Holm","Dahl" 
//                };

//                int created = 0;
//                for (int i = 0; i < count; i++)
//                {
//                    var first = firstNames[rnd.Next(firstNames.Length)];
//                    var last = lastNames[rnd.Next(lastNames.Length)];
//                    var fullName = $"{first} {last}";
//                    var unique = Guid.NewGuid().ToString("N").Substring(0, 8);
//                    var email = $"{first.ToLower()}.{last.ToLower()}.{unique}@example.invalid";

//                    // Create member (member type alias must exist)
//                    var member = _memberService.CreateMember(email, email, fullName, "hpskMember");

//                    // Assign random club id as primaryClubId (stored as string)
//                    var club = clubs[rnd.Next(clubs.Count)];
//                    member.SetValue("firstName", first);
//                    member.SetValue("lastName", last);
//                    member.SetValue("primaryClubId", club.Id);
//                    member.IsApproved = true;

//                    _memberService.Save(member);
//                    _memberService.AssignRole(member.Id, "Users");

//                    created++;
//                }

//                return Json(new { success = true, message = $"Created {created} test members.", created });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetClubs()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var clubs = GetClubsFromStorage();
                
//                // Update member counts and admin counts
//                var allMembersForCount = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

//                foreach (var club in clubs)
//                {
//                    // Calculate member count
//                    var memberCount = allMembersForCount.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                        .Count(m => m.GetValue("primaryClubId")?.ToString() == club.Id.ToString());
//                    club.MemberCount = memberCount;

//                    // Calculate admin count
//                    var groupName = $"ClubAdmin_{club.Id}";
//                    var adminCount = allMembersForCount.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                        .Count(m => _memberService.GetAllRoles(m.Id).Contains(groupName));

//                    club.AdminCount = adminCount;
//                }

//                return Json(new { success = true, data = clubs });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetClub(int id)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                // Try to get club from content first (new hierarchical structure)
//                IPublishedContent clubNode = null;
//                if (UmbracoContext.Content != null)
//                {
//                    clubNode = UmbracoContext.Content.GetById(id);
//                    if (clubNode != null && clubNode.ContentType.Alias != "club")
//                    {
//                        clubNode = null; // Not a club node
//                    }
//                }

//                // Fall back to member-based clubs if content not found
//                if (clubNode == null)
//                {
//                    var clubs = GetClubsFromStorage();
//                    var club = clubs.FirstOrDefault(c => c.Id == id);

//                    if (club == null)
//                    {
//                        return Json(new { success = false, message = "Club not found" });
//                    }

//                    // Get club members for contact person dropdown (exclude club member types)
//                    var allMembersForClub = _memberService.GetAll(0, int.MaxValue, out _);
//                    var clubMembers = allMembersForClub.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                        .Where(m => m.GetValue("primaryClubId")?.ToString() == club.Id.ToString() ||
//                                    (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
//                                    .Contains(club.Id.ToString()))
//                        .Select(m => new {
//                            id = m.Id,
//                            name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
//                            email = m.Email
//                        }).ToArray();

//                    var clubData = new
//                    {
//                        Id = club.Id,
//                        Name = club.Name,
//                        Description = club.Description,
//                        ContactPerson = club.ContactPerson,
//                        ContactEmail = club.ContactEmail,
//                        ContactPhone = club.ContactPhone,
//                        Address = club.Address,
//                        City = club.City,
//                        PostalCode = club.PostalCode,
//                        IsActive = club.IsActive,
//                        MemberCount = club.MemberCount,
//                        ClubMembers = clubMembers
//                    };

//                    return Json(new { success = true, data = clubData });
//                }

//                // Get club data from content node
//                var clubName = clubNode.Name ?? clubNode.Value<string>("clubName") ?? "";

//                // Get all members for counting
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias).ToList();

//                // Get club members for contact person dropdown
//                var clubMembersFromContent = regularMembers
//                    .Where(m => m.GetValue("primaryClubId")?.ToString() == id.ToString() ||
//                                (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries)
//                                .Contains(id.ToString()))
//                    .Select(m => new {
//                        id = m.Id,
//                        name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
//                        email = m.Email
//                    }).ToArray();

//                // Count members for this club
//                var memberCount = clubMembersFromContent.Length;

//                var clubDataFromContent = new
//                {
//                    Id = clubNode.Id,
//                    Name = clubName,
//                    Description = clubNode.Value<string>("description") ?? "",
//                    ContactPerson = clubNode.Value<string>("contactPerson") ?? "",
//                    ContactEmail = clubNode.Value<string>("contactEmail") ?? "",
//                    ContactPhone = clubNode.Value<string>("contactPhone") ?? "",
//                    Address = clubNode.Value<string>("address") ?? "",
//                    City = clubNode.Value<string>("city") ?? "",
//                    PostalCode = clubNode.Value<string>("postalCode") ?? "",
//                    WebSite = clubNode.Value<string>("clubUrl") ?? "",
//                    IsActive = clubNode.IsPublished(),
//                    MemberCount = memberCount,
//                    ClubMembers = clubMembersFromContent
//                };

//                return Json(new { success = true, data = clubDataFromContent });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading club: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> SaveClub(
//            int? id,
//            string name,
//            string description,
//            string contactPerson,
//            string contactEmail,
//            string contactPhone,
//            string webSite,
//            string address,
//            string city,
//            string postalCode,
//            bool isActive)
//        {
//            try
//            {
//                if (!await IsCurrentUserAdminAsync())
//                {
//                    return Json(new { success = false, message = "Access denied" });
//                }

//                if (string.IsNullOrEmpty(name))
//                {
//                    return Json(new { success = false, message = "Club name is required" });
//                }

//                if (id.HasValue && id.Value > 0)
//                {
//                    // Edit existing club - try content first
//                    IPublishedContent clubNode = null;
//                    if (UmbracoContext.Content != null)
//                    {
//                        clubNode = UmbracoContext.Content.GetById(id.Value);
//                        if (clubNode != null && clubNode.ContentType.Alias != "club")
//                        {
//                            clubNode = null;
//                        }
//                    }

//                    if (clubNode != null)
//                    {
//                        // Update content-based club
//                        var clubContent = _contentService.GetById(id.Value);
//                        if (clubContent == null)
//                        {
//                            return Json(new { success = false, message = "Club not found" });
//                        }

//                        // Check if name is taken by another club
//                        var clubs = GetClubsFromStorage();
//                        if (clubs.Any(c => c.Id != id.Value && c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
//                        {
//                            return Json(new { success = false, message = "A club with this name already exists" });
//                        }

//                        // Update club properties
//                        clubContent.Name = name;
//                        clubContent.SetValue("description", description ?? "");
//                        clubContent.SetValue("contactPerson", contactPerson ?? "");
//                        clubContent.SetValue("contactEmail", contactEmail ?? "");
//                        clubContent.SetValue("contactPhone", contactPhone ?? "");
//                        clubContent.SetValue("webSite", webSite ?? "");
//                        clubContent.SetValue("address", address ?? "");
//                        clubContent.SetValue("city", city ?? "");
//                        clubContent.SetValue("postalCode", postalCode ?? "");

//                        _contentService.Save(clubContent);

//                        // Publish if active, unpublish if inactive
//                        if (isActive)
//                        {
//                            _contentService.Publish(clubContent, Array.Empty<string>());
//                        }
//                        else
//                        {
//                            _contentService.Unpublish(clubContent);
//                        }

//                        return Json(new { success = true, message = "Club updated successfully" });
//                    }
//                    else
//                    {
//                        return Json(new { success = false, message = "Club not found in content hierarchy" });
//                    }
//                }
//                else
//                {
//                    // Create new club in content hierarchy
//                    var clubs = GetClubsFromStorage();
//                    if (clubs.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
//                    {
//                        return Json(new { success = false, message = "A club with this name already exists" });
//                    }

//                    // Try to create as content first if clubsPage exists
//                    if (UmbracoContext.Content != null)
//                    {
//                        var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
//                        if (root != null)
//                        {
//                            var clubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
//                            if (clubsHub != null)
//                            {
//                                // Create new club content node
//                                var newClubContent = _contentService.Create(
//                                    name,
//                                    clubsHub.Id,
//                                    "club"
//                                );

//                                newClubContent.SetValue("description", description ?? "");
//                                newClubContent.SetValue("contactPerson", contactPerson ?? "");
//                                newClubContent.SetValue("contactEmail", contactEmail ?? "");
//                                newClubContent.SetValue("contactPhone", contactPhone ?? "");
//                                newClubContent.SetValue("webSite", webSite ?? "");
//                                newClubContent.SetValue("address", address ?? "");
//                                newClubContent.SetValue("city", city ?? "");
//                                newClubContent.SetValue("postalCode", postalCode ?? "");

//                                _contentService.Save(newClubContent);

//                                if (isActive)
//                                {
//                                    _contentService.Publish(newClubContent, Array.Empty<string>());
//                                }

//                                // Create corresponding club admin group
//                                await EnsureClubAdminGroup(newClubContent.Id, name);

//                                return Json(new { success = true, message = "Club created successfully" });
//                            }
//                        }
//                    }

//                    return Json(new { success = false, message = "Unable to create club: clubsPage not found in content hierarchy" });
//                }
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error saving club: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> DeleteClub(int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var clubs = GetClubsFromStorage();
//                var club = clubs.FirstOrDefault(c => c.Id == clubId);

//                if (club == null)
//                {
//                    return Json(new { success = false, message = "Club not found" });
//                }

//                // Check if any members are assigned to this club (primary or additional)
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
//                var membersWithThisClubAsPrimary = regularMembers
//                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
//                    .ToList();

//                var membersWithThisClubAsAdditional = regularMembers
//                    .Where(m => !string.IsNullOrEmpty(m.GetValue("memberClubIds")?.ToString()) &&
//                               m.GetValue("memberClubIds")?.ToString()
//                                ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
//                                ?.Select(id => id.Trim())
//                                ?.Contains(clubId.ToString()) == true)
//                    .ToList();

//                var totalMembersWithThisClub = membersWithThisClubAsPrimary.Count + membersWithThisClubAsAdditional.Count;

//                if (totalMembersWithThisClub > 0)
//                {
//                    var primaryCount = membersWithThisClubAsPrimary.Count;
//                    var additionalCount = membersWithThisClubAsAdditional.Count;
//                    var message = $"Cannot delete club '{club.Name}'. {totalMembersWithThisClub} member(s) are still linked to this club";

//                    if (primaryCount > 0 && additionalCount > 0)
//                    {
//                        message += $" ({primaryCount} as primary club, {additionalCount} as additional club)";
//                    }
//                    else if (primaryCount > 0)
//                    {
//                        message += $" (as primary club)";
//                    }
//                    else
//                    {
//                        message += $" (as additional club)";
//                    }

//                    message += ". Please unlink all members from this club before deletion.";

//                    // Add member details for better user experience
//                    var memberDetails = new List<object>();

//                    foreach (var member in membersWithThisClubAsPrimary)
//                    {
//                        memberDetails.Add(new
//                        {
//                            Id = member.Id,
//                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
//                            Email = member.Email,
//                            LinkType = "Primary Club"
//                        });
//                    }

//                    foreach (var member in membersWithThisClubAsAdditional)
//                    {
//                        memberDetails.Add(new
//                        {
//                            Id = member.Id,
//                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
//                            Email = member.Email,
//                            LinkType = "Additional Club"
//                        });
//                    }

//                    return Json(new {
//                        success = false,
//                        message = message,
//                        linkedMembers = memberDetails
//                    });
//                }

//                // Try to delete as content first (new hierarchical structure)
//                IPublishedContent clubNode = null;
//                if (UmbracoContext.Content != null)
//                {
//                    clubNode = UmbracoContext.Content.GetById(clubId);
//                    if (clubNode != null && clubNode.ContentType.Alias != "club")
//                    {
//                        clubNode = null;
//                    }
//                }

//                if (clubNode != null)
//                {
//                    // Delete content-based club
//                    var clubContent = _contentService.GetById(clubId);
//                    if (clubContent != null)
//                    {
//                        // Unpublish first to remove from public site
//                        _contentService.Unpublish(clubContent);
//                        // Then delete
//                        _contentService.Delete(clubContent);
//                    }
//                    return Json(new { success = true, message = "Club deleted successfully" });
//                }
//                else
//                {
//                    // Fall back to member-based club deletion for backward compatibility
//                    var clubMember = _memberService.GetById(clubId);
//                    if (clubMember != null)
//                    {
//                        _memberService.Delete(clubMember);
//                    }
//                    return Json(new { success = true, message = "Club deleted successfully" });
//                }
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error deleting club: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> CleanupInvalidClubReferences()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                // Get current valid clubs
//                var validClubs = GetClubsFromStorage().Where(c => c.IsActive).ToList();
//                var validClubIds = validClubs.Select(c => c.Id).ToHashSet();

//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
//                var membersUpdated = 0;
//                var membersWithInvalidRefs = new List<string>();

//                foreach (var member in regularMembers)
//                {
//                    var memberUpdated = false;
//                    var memberName = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();

//                    // ALWAYS remove primaryClubName field (data normalization)
//                    var primaryClubName = member.GetValue("primaryClubName")?.ToString();
//                    if (!string.IsNullOrEmpty(primaryClubName))
//                    {
//                        member.SetValue("primaryClubName", "");
//                        memberUpdated = true;
//                    }

//                    // Check and clean invalid primary club IDs
//                    var primaryClubId = member.GetValue("primaryClubId")?.ToString();
//                    if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out var clubId) && !validClubIds.Contains(clubId))
//                    {
//                        member.SetValue("primaryClubId", null);
//                        memberUpdated = true;
//                        membersWithInvalidRefs.Add($"{memberName} (Invalid Primary Club ID: {primaryClubId})");
//                    }

//                    // Check and clean additional club references
//                    var memberClubIds = member.GetValue("memberClubIds")?.ToString();
//                    if (!string.IsNullOrEmpty(memberClubIds))
//                    {
//                        var clubIds = memberClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
//                            .Select(s => s.Trim())
//                            .Where(s => int.TryParse(s, out _))
//                            .Select(s => int.Parse(s))
//                            .ToList();

//                        var validAdditionalClubIds = clubIds.Where(id => validClubIds.Contains(id)).ToList();

//                        if (validAdditionalClubIds.Count != clubIds.Count)
//                        {
//                            var invalidIds = clubIds.Except(validAdditionalClubIds);
//                            member.SetValue("memberClubIds", string.Join(",", validAdditionalClubIds));
//                            memberUpdated = true;
//                            membersWithInvalidRefs.Add($"{memberName} (Invalid Additional Club IDs: {string.Join(", ", invalidIds)})");
//                        }
//                    }

//                    if (memberUpdated)
//                    {
//                        _memberService.Save(member);
//                        membersUpdated++;
//                    }
//                }

//                var message = membersUpdated > 0
//                    ? $"Cleaned up {membersUpdated} member(s) - removed redundant club name fields and invalid club references."
//                    : "No cleanup needed - all member data is valid.";

//                return Json(new {
//                    success = true,
//                    message = message,
//                    membersUpdated = membersUpdated,
//                    invalidReferences = membersWithInvalidRefs
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error cleaning up club references: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> CheckClubCanBeDeleted(int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var clubs = GetClubsFromStorage();
//                var club = clubs.FirstOrDefault(c => c.Id == clubId);

//                if (club == null)
//                {
//                    return Json(new { success = false, message = "Club not found" });
//                }

//                // Check if any members are assigned to this club (primary or additional)
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
//                var membersWithThisClubAsPrimary = regularMembers
//                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
//                    .ToList();

//                var membersWithThisClubAsAdditional = regularMembers
//                    .Where(m => !string.IsNullOrEmpty(m.GetValue("memberClubIds")?.ToString()) &&
//                               m.GetValue("memberClubIds")?.ToString()
//                                ?.Split(',', StringSplitOptions.RemoveEmptyEntries)
//                                ?.Select(id => id.Trim())
//                                ?.Contains(clubId.ToString()) == true)
//                    .ToList();

//                var totalMembersWithThisClub = membersWithThisClubAsPrimary.Count + membersWithThisClubAsAdditional.Count;

//                if (totalMembersWithThisClub > 0)
//                {
//                    var primaryCount = membersWithThisClubAsPrimary.Count;
//                    var additionalCount = membersWithThisClubAsAdditional.Count;
//                    var message = $"Cannot delete club '{club.Name}'. {totalMembersWithThisClub} member(s) are still linked to this club";

//                    if (primaryCount > 0 && additionalCount > 0)
//                    {
//                        message += $" ({primaryCount} as primary club, {additionalCount} as additional club)";
//                    }
//                    else if (primaryCount > 0)
//                    {
//                        message += $" (as primary club)";
//                    }
//                    else
//                    {
//                        message += $" (as additional club)";
//                    }

//                    message += ". Please unlink all members from this club before deletion.";

//                    // Add member details for better user experience
//                    var memberDetails = new List<object>();

//                    foreach (var member in membersWithThisClubAsPrimary)
//                    {
//                        memberDetails.Add(new
//                        {
//                            Id = member.Id,
//                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
//                            Email = member.Email,
//                            LinkType = "Primary Club"
//                        });
//                    }

//                    foreach (var member in membersWithThisClubAsAdditional)
//                    {
//                        memberDetails.Add(new
//                        {
//                            Id = member.Id,
//                            Name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim(),
//                            Email = member.Email,
//                            LinkType = "Additional Club"
//                        });
//                    }

//                    return Json(new {
//                        canDelete = false,
//                        message = message,
//                        linkedMembers = memberDetails
//                    });
//                }

//                return Json(new { canDelete = true, message = "Club can be deleted safely." });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error checking club: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetClubMembers(int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var clubs = GetClubsFromStorage();
//                var club = clubs.FirstOrDefault(c => c.Id == clubId);

//                if (club == null)
//                {
//                    return Json(new { success = false, message = "Club not found" });
//                }

//                var allMembersForClubList = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var members = allMembersForClubList.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString())
//                    .Select(m => new ClubMemberViewModel
//                    {
//                        MemberId = m.Id,
//                        MemberName = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
//                        Email = m.Email,
//                        IsPrimary = true
//                    }).ToList();

//                return Json(new { success = true, data = members });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading club members: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetClubMembersForClubAdmin(int clubId)
//        {
//            if (!await IsClubAdminForClub(clubId))
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var clubMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                    .Where(m => m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
//                               (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',')
//                               .Any(id => id.Trim() == clubId.ToString()))
//                    .Select(m => new
//                    {
//                        id = m.Id,
//                        firstName = m.GetValue("firstName")?.ToString() ?? "",
//                        lastName = m.GetValue("lastName")?.ToString() ?? "",
//                        email = m.Email ?? "",
//                        primaryClubId = int.TryParse(m.GetValue("primaryClubId")?.ToString(), out int pId) ? pId : (int?)null,
//                        isApproved = m.IsApproved,
//                        additionalClubIds = (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',')
//                                          .Where(id => !string.IsNullOrWhiteSpace(id))
//                                          .Select(id => int.TryParse(id.Trim(), out int aid) ? aid : (int?)null)
//                                          .Where(id => id.HasValue)
//                                          .Select(id => id.Value)
//                                          .ToList()
//                    }).ToList();

//                return Json(new { success = true, data = clubMembers });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading club members: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetPendingApprovalsCount(int clubId)
//        {
//            if (!await IsClubAdminForClub(clubId))
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var pendingCount = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                    .Where(m => !m.IsApproved &&
//                               (m.GetValue("primaryClubId")?.ToString() == clubId.ToString() ||
//                                (m.GetValue("memberClubIds")?.ToString() ?? "").Split(',')
//                                .Any(id => id.Trim() == clubId.ToString())))
//                    .Count();

//                return Json(new { success = true, count = pendingCount });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading pending count: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> FixUsersWithoutGroups()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
//                var membersFixed = 0;

//                foreach (var member in regularMembers)
//                {
//                    var memberRoles = _memberService.GetAllRoles(member.Id);

//                    // If member has no groups, assign them to Users group
//                    if (!memberRoles.Any())
//                    {
//                        _memberService.AssignRole(member.Id, "Users");
//                        membersFixed++;
//                    }
//                }

//                return Json(new {
//                    success = true,
//                    message = $"Fixed {membersFixed} members without groups. All members now belong to at least the 'Users' group.",
//                    membersFixed = membersFixed
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error fixing users without groups: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Public endpoint to get active clubs for registration dropdown
//        /// </summary>
//        [HttpGet]
//        public IActionResult GetClubsForRegistration()
//        {
//            try
//            {
//                var clubs = GetClubsFromStorage();
//                var activeClubs = clubs.Where(c => c.IsActive)
//                    .Select(c => new {
//                        id = c.Id,
//                        name = c.Name
//                    })
//                    .OrderBy(c => c.name)
//                    .ToList();

//                return Json(new { success = true, clubs = activeClubs });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Public endpoint to get active clubs for clubs page (no authentication required)
//        /// </summary>
//        [HttpGet]
//        public IActionResult GetClubsPublic()
//        {
//            try
//            {
//                var clubs = GetClubsFromStorage();

//                // Calculate member counts for each club
//                var allMembersForCount = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
//                foreach (var club in clubs)
//                {
//                    // Calculate member count
//                    var memberCount = allMembersForCount.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
//                        .Count(m => m.GetValue("primaryClubId")?.ToString() == club.Id.ToString());
//                    club.MemberCount = memberCount;
//                }

//                var activeClubs = clubs.Where(c => c.IsActive)
//                    .Select(c => new {
//                        id = c.Id,
//                        name = c.Name,
//                        description = c.Description,
//                        city = c.City,
//                        webSite = c.WebSite,
//                        contactEmail = c.ContactEmail,
//                        contactPhone = c.ContactPhone,
//                        urlSegment = c.UrlSegment,
//                        isActive = c.IsActive,
//                        memberCount = c.MemberCount
//                    })
//                    .OrderBy(c => c.name)
//                    .ToList();

//                return Json(new { success = true, clubs = activeClubs });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading clubs: " + ex.Message });
//            }
//        }

//        #region Club Admin Management

//        /// <summary>
//        /// Creates or ensures existence of club admin group for a specific club
//        /// </summary>
//        private async Task<bool> EnsureClubAdminGroup(int clubId, string clubName)
//        {
//            try
//            {
//                var groupName = $"ClubAdmin_{clubId}";
//                var existingGroup = await _memberGroupService.GetByNameAsync(groupName);

//                if (existingGroup == null)
//                {
//                    var newGroup = new Umbraco.Cms.Core.Models.MemberGroup();
//                    newGroup.Name = groupName;
//                    // Using Umbraco v16.2 async pattern
//                    await _memberGroupService.CreateAsync(newGroup);
//                    return true;
//                }

//                return true;
//            }
//            catch (Exception ex)
//            {
//                // Log error but don't break the flow
//                return false;
//            }
//        }

//        /// <summary>
//        /// Checks if current user is admin for a specific club
//        /// </summary>
//        private async Task<bool> IsClubAdmin(int clubId)
//        {
//            try
//            {
//                var currentMember = await _memberManager.GetCurrentMemberAsync();
//                if (currentMember == null) return false;

//                var member = _memberService.GetByEmail(currentMember.Email);
//                if (member == null) return false;

//                var roles = _memberService.GetAllRoles(member.Id);

//                // Check if user has site-wide admin or specific club admin
//                return roles.Contains("Administrators") ||
//                       roles.Contains($"ClubAdmin_{clubId}");
//            }
//            catch
//            {
//                return false;
//            }
//        }

//        /// <summary>
//        /// Gets the club ID that the current user can administer (if any)
//        /// </summary>
//        private async Task<int?> GetCurrentUserClubAdminId()
//        {
//            try
//            {
//                var currentMember = await _memberManager.GetCurrentMemberAsync();
//                if (currentMember == null) return null;

//                var member = _memberService.GetByEmail(currentMember.Email);
//                if (member == null) return null;

//                var roles = _memberService.GetAllRoles(member.Id);

//                // Site admins can access all clubs
//                if (roles.Contains("Administrators")) return null; // null means all clubs

//                // Find club admin role
//                var clubAdminRole = roles.FirstOrDefault(r => r.StartsWith("ClubAdmin_"));
//                if (clubAdminRole != null && int.TryParse(clubAdminRole.Substring(10), out int clubId))
//                {
//                    return clubId;
//                }

//                return null;
//            }
//            catch
//            {
//                return null;
//            }
//        }

//        /// <summary>
//        /// Assigns club admin role to a member
//        /// </summary>
//        [HttpPost]
//        public async Task<IActionResult> AssignClubAdmin(int memberId, int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync() && !await IsClubAdmin(clubId))
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var member = _memberService.GetById(memberId);
//                if (member == null)
//                {
//                    return Json(new { success = false, message = "Member not found" });
//                }

//                var club = _memberService.GetById(clubId);
//                if (club == null || club.ContentType.Alias != ClubMemberTypeAlias)
//                {
//                    return Json(new { success = false, message = "Club not found" });
//                }

//                // Ensure club admin group exists
//                await EnsureClubAdminGroup(clubId, club.Name ?? $"Club_{clubId}");

//                // Assign club admin role
//                var groupName = $"ClubAdmin_{clubId}";
//                _memberService.AssignRole(member.Id, groupName);

//                return Json(new {
//                    success = true,
//                    message = $"Member assigned as admin for {club.Name}"
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error assigning club admin: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Removes club admin role from a member
//        /// </summary>
//        [HttpPost]
//        public async Task<IActionResult> RemoveClubAdmin(int memberId, int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync() && !await IsClubAdmin(clubId))
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var member = _memberService.GetById(memberId);
//                if (member == null)
//                {
//                    return Json(new { success = false, message = "Member not found" });
//                }

//                var groupName = $"ClubAdmin_{clubId}";
//                var currentRoles = _memberService.GetAllRoles(member.Id);

//                if (currentRoles.Contains(groupName))
//                {
//                    _memberService.DissociateRole(member.Id, groupName);
//                    return Json(new { success = true, message = "Club admin role removed" });
//                }

//                return Json(new { success = true, message = "Member was not a club admin" });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error removing club admin: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Gets list of club admins for a specific club
//        /// </summary>
//        [HttpGet]
//        public async Task<IActionResult> GetClubAdmins(int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync() && !await IsClubAdmin(clubId))
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var groupName = $"ClubAdmin_{clubId}";
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

//                var clubAdmins = allMembers
//                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
//                    .Where(m => _memberService.GetAllRoles(m.Id).Contains(groupName))
//                    .Select(m => new {
//                        Id = m.Id,
//                        Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
//                        Email = m.Email,
//                        IsApproved = m.IsApproved
//                    })
//                    .ToList();

//                return Json(new { success = true, admins = clubAdmins });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading club admins: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Gets available members for club admin assignment (excluding existing admins)
//        /// </summary>
//        [HttpGet]
//        public async Task<IActionResult> GetAvailableMembersForClubAdmin(int clubId)
//        {
//            if (!await IsCurrentUserAdminAsync() && !await IsClubAdmin(clubId))
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var groupName = $"ClubAdmin_{clubId}";
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

//                // Get members who are not clubs and not already admins of this club
//                var availableMembers = allMembers
//                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
//                    .Where(m => !_memberService.GetAllRoles(m.Id).Contains(groupName)) // Exclude existing club admins
//                    .Where(m => m.IsApproved) // Only approved members
//                    .Select(m => new {
//                        Id = m.Id,
//                        Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
//                        Email = m.Email
//                    })
//                    .OrderBy(m => m.Name)
//                    .ToList();

//                return Json(new { success = true, members = availableMembers });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading available members: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Gets members pending approval
//        /// </summary>
//        [HttpGet]
//        public async Task<IActionResult> GetPendingApprovals()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

//                // Get clubs for club name lookup
//                var clubs = GetClubsFromStorage();

//                var pendingMembers = allMembers
//                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
//                    .Where(m => !m.IsApproved) // Only non-approved members
//                    .Select(m => {
//                        var primaryClubId = m.GetValue("primaryClubId")?.ToString();
//                        var clubName = "No Club";

//                        if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out var clubId))
//                        {
//                            var club = clubs.FirstOrDefault(c => c.Id == clubId);
//                            clubName = club?.Name ?? $"Unknown Club (ID: {clubId})";
//                        }

//                        return new {
//                            Id = m.Id,
//                            Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
//                            Email = m.Email,
//                            ClubName = clubName,
//                            RegistrationDate = m.CreateDate
//                        };
//                    })
//                    .OrderBy(m => m.RegistrationDate)
//                    .ToList();

//                return Json(new { success = true, data = pendingMembers });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = ex.Message });
//            }
//        }

//        #endregion

//        /// <summary>
//        /// Debug method to check club member types and their status
//        /// </summary>
//        [HttpGet]
//        public IActionResult DebugClubs()
//        {
//            try
//            {
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

//                var debugInfo = new
//                {
//                    TotalMembers = totalRecords,
//                    ClubMemberTypeAlias = ClubMemberTypeAlias,
//                    AllMemberTypes = allMembers.Select(m => new {
//                        Id = m.Id,
//                        Name = m.Name,
//                        Email = m.Email,
//                        ContentTypeAlias = m.ContentType.Alias,
//                        IsApproved = m.IsApproved
//                    }).Take(10).ToList(),
//                    ClubMembers = allMembers.Where(m => m.ContentType.Alias == ClubMemberTypeAlias)
//                        .Select(m => new {
//                            Id = m.Id,
//                            Name = m.Name,
//                            Email = m.Email,
//                            ContentTypeAlias = m.ContentType.Alias,
//                            IsApproved = m.IsApproved,
//                            Description = m.GetValue<string>("description"),
//                            City = m.GetValue<string>("city"),
//                            ContactEmail = m.GetValue<string>("contactEmail")
//                        }).ToList()
//                };

//                return Json(new { success = true, debug = debugInfo });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = ex.Message });
//            }
//        }

//        /// <summary>
//        /// Initialize the system with active clubs for development/testing
//        /// Only call this if clubs don't already exist
//        /// </summary>
//        [HttpPost]
//        public async Task<IActionResult> InitializeClubs()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied - Admin only" });
//            }

//            try
//            {
//                // Check if clubs already exist
//                var existingClubs = GetClubsFromStorage();
//                if (existingClubs.Any())
//                {
//                    return Json(new { success = false, message = "Clubs already exist", count = existingClubs.Count });
//                }

//                // Create the 5 active clubs from CLAUDE.md documentation
//                var clubsToCreate = new[]
//                {
//                    new { Name = "Haaplinge GoAss", City = "Hoor", Email = "info@haapling.se", Description = "Haaplinge GoAss - Hoor" },
//                    new { Name = "Malmö Skytteförening", City = "Malmö", Email = "info@malmosk.se", Description = "Malmö Skytteförening - Malmö" },
//                    new { Name = "Falkenbergs Pistolklubb", City = "Falkenberg", Email = "info@fpk.se", Description = "Falkenbergs Pistolklubb - Falkenberg" },
//                    new { Name = "Broaryds Pistolklubb", City = "Broaryd", Email = "info@bpk.se", Description = "Broaryds Pistolklubb - Broaryd" },
//                    new { Name = "Halmstad Snöstorp Pistolskytteförening", City = "Halmstad", Email = "info@hspf.se", Description = "Halmstad Snöstorp Pistolskytteförening - Halmstad" }
//                };

//                var createdClubs = 0;
//                var createdGroups = 0;

//                foreach (var clubData in clubsToCreate)
//                {
//                    // Create club as member
//                    var clubMember = _memberService.CreateMember(
//                        clubData.Email,
//                        clubData.Email,
//                        clubData.Name,
//                        ClubMemberTypeAlias
//                    );

//                    if (clubMember != null)
//                    {
//                        // Set club properties
//                        clubMember.SetValue("description", clubData.Description);
//                        clubMember.SetValue("contactEmail", clubData.Email);
//                        clubMember.SetValue("city", clubData.City);
//                        clubMember.SetValue("contactPerson", "Club Administrator");
//                        clubMember.SetValue("address", $"Address in {clubData.City}");
//                        clubMember.SetValue("postalCode", "12345");

//                        // Set as active
//                        clubMember.IsApproved = true;

//                        // Assign Users group
//                        _memberService.AssignRole(clubMember.Id, "Users");

//                        // Save the club
//                        _memberService.Save(clubMember);
//                        createdClubs++;

//                        // Create corresponding club admin group
//                        var groupCreated = await EnsureClubAdminGroup(clubMember.Id, clubMember.Name);
//                        if (groupCreated)
//                        {
//                            createdGroups++;
//                        }
//                    }
//                }

//                return Json(new {
//                    success = true,
//                    message = $"Successfully created {createdClubs} clubs and {createdGroups} admin groups",
//                    clubsCreated = createdClubs,
//                    groupsCreated = createdGroups
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error initializing clubs: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Preview migration without making any changes - dry run to see what will be migrated.
//        /// </summary>
//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> PreviewClubMigration()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                // Get all members
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out _).ToList();

//                // Get old club members (type hpskClub)
//                var oldClubs = allMembers.Where(m => m.ContentType.Alias == ClubMemberTypeAlias).ToList();

//                // Get new club content nodes
//                IPublishedContent clubsHub = null;
//                var newClubs = new List<IPublishedContent>();

//                if (UmbracoContext.Content != null)
//                {
//                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
//                    if (root != null)
//                    {
//                        clubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
//                        if (clubsHub != null)
//                        {
//                            newClubs = clubsHub.Children.Where(c => c.ContentType.Alias == "club").ToList();
//                        }
//                    }
//                }

//                if (!newClubs.Any())
//                {
//                    return Json(new { success = false, message = "No new club content nodes found. Please create club pages first." });
//                }

//                // Build mapping: oldClubId → newClubId by matching clubName property
//                var clubMapping = new Dictionary<int, int>();
//                var unmatchedOldClubs = new List<object>();
//                var matchedOldClubs = new List<object>();

//                foreach (var oldClub in oldClubs)
//                {
//                    var oldName = oldClub.Name?.Trim();

//                    // Match by clubName property (case-insensitive)
//                    var matchingNewClub = newClubs.FirstOrDefault(nc =>
//                        string.Equals(
//                            nc.Value<string>("clubName")?.Trim(),
//                            oldName,
//                            StringComparison.OrdinalIgnoreCase
//                        )
//                    );

//                    if (matchingNewClub != null)
//                    {
//                        clubMapping[oldClub.Id] = matchingNewClub.Id;
//                        matchedOldClubs.Add(new
//                        {
//                            oldClubId = oldClub.Id,
//                            oldClubName = oldClub.Name,
//                            newClubId = matchingNewClub.Id,
//                            newClubName = matchingNewClub.Name
//                        });
//                    }
//                    else
//                    {
//                        unmatchedOldClubs.Add(new
//                        {
//                            oldClubId = oldClub.Id,
//                            oldClubName = oldClub.Name
//                        });
//                    }
//                }

//                // If there are unmatched clubs, report and stop preview
//                if (unmatchedOldClubs.Any())
//                {
//                    return Json(new
//                    {
//                        success = false,
//                        message = $"Cannot proceed. {unmatchedOldClubs.Count} old club(s) have no matching new club content node.",
//                        unmatchedClubs = unmatchedOldClubs
//                    });
//                }

//                // Get regular members (not clubs)
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias).ToList();

//                // Preview what will be updated
//                var memberUpdates = new List<object>();

//                foreach (var member in regularMembers)
//                {
//                    var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
//                    var memberClubIdsStr = member.GetValue("memberClubIds")?.ToString();

//                    bool hasPrimaryUpdate = false;
//                    int? oldPrimaryId = null;
//                    int? newPrimaryId = null;

//                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int oldPrimary))
//                    {
//                        if (clubMapping.ContainsKey(oldPrimary))
//                        {
//                            hasPrimaryUpdate = true;
//                            oldPrimaryId = oldPrimary;
//                            newPrimaryId = clubMapping[oldPrimary];
//                        }
//                    }

//                    var additionalUpdates = new List<object>();
//                    if (!string.IsNullOrEmpty(memberClubIdsStr))
//                    {
//                        var clubIds = memberClubIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);

//                        foreach (var clubIdStr in clubIds)
//                        {
//                            if (int.TryParse(clubIdStr.Trim(), out int oldClubId))
//                            {
//                                if (clubMapping.ContainsKey(oldClubId))
//                                {
//                                    additionalUpdates.Add(new
//                                    {
//                                        oldClubId = oldClubId,
//                                        newClubId = clubMapping[oldClubId]
//                                    });
//                                }
//                            }
//                        }
//                    }

//                    if (hasPrimaryUpdate || additionalUpdates.Any())
//                    {
//                        memberUpdates.Add(new
//                        {
//                            memberId = member.Id,
//                            memberName = member.Name,
//                            memberEmail = member.Email,
//                            primaryClubUpdate = hasPrimaryUpdate ? new { oldId = oldPrimaryId, newId = newPrimaryId } : null,
//                            additionalClubUpdates = additionalUpdates
//                        });
//                    }
//                }

//                return Json(new
//                {
//                    success = true,
//                    message = "Preview complete. No changes were made.",
//                    preview = new
//                    {
//                        oldClubsFound = oldClubs.Count,
//                        oldClubsMatched = matchedOldClubs.Count,
//                        oldClubsUnmatched = unmatchedOldClubs.Count,
//                        matchedClubs = matchedOldClubs,
//                        totalMembers = regularMembers.Count,
//                        membersToUpdate = memberUpdates.Count,
//                        memberUpdates = memberUpdates
//                    }
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Preview failed: " + ex.Message });
//            }
//        }

//        /// <summary>
//        /// Migrate all members from old club members (type hpskClub) to new club content nodes.
//        /// Matches clubs by comparing old member name with clubName property of content nodes.
//        /// </summary>
//        [HttpPost]
//        [ValidateAntiForgeryToken]
//        public async Task<IActionResult> MigrateClubReferences()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var migrationStats = new
//                {
//                    totalMembers = 0,
//                    membersUpdated = 0,
//                    primaryClubUpdates = 0,
//                    additionalClubUpdates = 0,
//                    oldClubsFound = 0,
//                    oldClubsMatched = 0,
//                    oldClubsDeleted = 0,
//                    unmatchedClubs = new List<string>(),
//                    errors = new List<string>()
//                };

//                // Get all members
//                var allMembers = _memberService.GetAll(0, int.MaxValue, out _).ToList();

//                // Get old club members (type hpskClub)
//                var oldClubs = allMembers.Where(m => m.ContentType.Alias == ClubMemberTypeAlias).ToList();
//                migrationStats = migrationStats with { oldClubsFound = oldClubs.Count };

//                // Get new club content nodes
//                IPublishedContent clubsHub = null;
//                var newClubs = new List<IPublishedContent>();

//                if (UmbracoContext.Content != null)
//                {
//                    var root = UmbracoContext.Content.GetAtRoot().FirstOrDefault();
//                    if (root != null)
//                    {
//                        clubsHub = root.Children.FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
//                        if (clubsHub != null)
//                        {
//                            newClubs = clubsHub.Children.Where(c => c.ContentType.Alias == "club").ToList();
//                        }
//                    }
//                }

//                if (!newClubs.Any())
//                {
//                    return Json(new { success = false, message = "No new club content nodes found. Please create club pages first." });
//                }

//                // Build mapping: oldClubId → newClubId by matching clubName property
//                var clubMapping = new Dictionary<int, int>();
//                var unmatchedOldClubs = new List<string>();

//                foreach (var oldClub in oldClubs)
//                {
//                    var oldName = oldClub.Name?.Trim();

//                    // Match by clubName property (case-insensitive)
//                    var matchingNewClub = newClubs.FirstOrDefault(nc =>
//                        string.Equals(
//                            nc.Value<string>("clubName")?.Trim(),
//                            oldName,
//                            StringComparison.OrdinalIgnoreCase
//                        )
//                    );

//                    if (matchingNewClub != null)
//                    {
//                        clubMapping[oldClub.Id] = matchingNewClub.Id;
//                    }
//                    else
//                    {
//                        unmatchedOldClubs.Add($"{oldClub.Name} (ID: {oldClub.Id})");
//                    }
//                }

//                migrationStats = migrationStats with { oldClubsMatched = clubMapping.Count };

//                // If there are unmatched clubs, report and stop migration
//                if (unmatchedOldClubs.Any())
//                {
//                    migrationStats = migrationStats with { unmatchedClubs = unmatchedOldClubs };
//                    return Json(new
//                    {
//                        success = false,
//                        message = $"Cannot proceed. {unmatchedOldClubs.Count} old club(s) have no matching new club content node.",
//                        details = migrationStats
//                    });
//                }

//                // Get regular members (not clubs)
//                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias).ToList();
//                migrationStats = migrationStats with { totalMembers = regularMembers.Count };

//                var primaryClubUpdates = 0;
//                var additionalClubUpdates = 0;
//                var membersUpdated = 0;

//                // Update each member's club references
//                foreach (var member in regularMembers)
//                {
//                    bool memberUpdated = false;

//                    // Update primaryClubId
//                    var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
//                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int oldPrimaryId))
//                    {
//                        if (clubMapping.ContainsKey(oldPrimaryId))
//                        {
//                            member.SetValue("primaryClubId", clubMapping[oldPrimaryId]);
//                            memberUpdated = true;
//                            primaryClubUpdates++;
//                        }
//                    }

//                    // Update memberClubIds (comma-separated list)
//                    var memberClubIdsStr = member.GetValue("memberClubIds")?.ToString();
//                    if (!string.IsNullOrEmpty(memberClubIdsStr))
//                    {
//                        var clubIds = memberClubIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
//                        var updatedClubIds = new List<int>();
//                        bool additionalUpdated = false;

//                        foreach (var clubIdStr in clubIds)
//                        {
//                            if (int.TryParse(clubIdStr.Trim(), out int oldClubId))
//                            {
//                                if (clubMapping.ContainsKey(oldClubId))
//                                {
//                                    updatedClubIds.Add(clubMapping[oldClubId]);
//                                    additionalUpdated = true;
//                                }
//                                else
//                                {
//                                    updatedClubIds.Add(oldClubId); // Keep unmapped IDs as-is
//                                }
//                            }
//                        }

//                        if (additionalUpdated)
//                        {
//                            member.SetValue("memberClubIds", string.Join(",", updatedClubIds));
//                            memberUpdated = true;
//                            additionalClubUpdates++;
//                        }
//                    }

//                    // Save member if updated
//                    if (memberUpdated)
//                    {
//                        try
//                        {
//                            _memberService.Save(member);
//                            membersUpdated++;
//                        }
//                        catch (Exception ex)
//                        {
//                            migrationStats = migrationStats with
//                            {
//                                errors = migrationStats.errors.Append($"Failed to save member {member.Name}: {ex.Message}").ToList()
//                            };
//                        }
//                    }
//                }

//                // Delete old club members
//                var oldClubsDeletedCount = 0;
//                foreach (var oldClub in oldClubs)
//                {
//                    try
//                    {
//                        _memberService.Delete(oldClub);
//                        oldClubsDeletedCount++;
//                    }
//                    catch (Exception ex)
//                    {
//                        migrationStats = migrationStats with
//                        {
//                            errors = migrationStats.errors.Append($"Failed to delete club {oldClub.Name}: {ex.Message}").ToList()
//                        };
//                    }
//                }

//                // Return final report
//                return Json(new
//                {
//                    success = true,
//                    message = $"Migration completed successfully. Updated {membersUpdated} members, deleted {oldClubsDeletedCount} old clubs.",
//                    details = new
//                    {
//                        totalMembers = migrationStats.totalMembers,
//                        membersUpdated = membersUpdated,
//                        primaryClubUpdates = primaryClubUpdates,
//                        additionalClubUpdates = additionalClubUpdates,
//                        oldClubsFound = migrationStats.oldClubsFound,
//                        oldClubsMatched = migrationStats.oldClubsMatched,
//                        oldClubsDeleted = oldClubsDeletedCount,
//                        errors = migrationStats.errors
//                    }
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Migration failed: " + ex.Message });
//            }
//        }

//        #region Competition Management Permissions
//        // Simplified Permission Model:
//        // 1. Site Administrators (Administrators group) - Can manage all competitions
//        // 2. Assigned Managers (via User Picker on competition) - Can manage specific competition
//        // 3. Everyone else - No management access
//        //
//        // No separate CompetitionManager member group needed!

//        /// <summary>
//        /// Check if current user can manage a specific competition
//        /// </summary>
//        public async Task<bool> CanManageCompetition(int competitionId)
//        {
//            var currentMember = await _memberManager.GetCurrentMemberAsync();
//            if (currentMember == null) return false;

//            // Convert member key to member ID for consistency
//            var member = _memberService.GetByKey(currentMember.Key);
//            if (member == null) return false;

//            return await CanManageCompetition(member.Id, competitionId);
//        }

//        /// <summary>
//        /// Check if a specific member can manage a specific competition
//        /// </summary>
//        public async Task<bool> CanManageCompetition(int memberId, int competitionId)
//        {
//            // Site administrators can manage everything
//            if (await IsUserInRole(memberId, "Administrators"))
//                return true;

//            // For regular members, check if they are assigned to this specific competition
//            // This will be checked in the UI layer by looking at the competitionManagers property
//            // For now, return false - actual checking will be done client-side or in views
//            return false;
//        }

//        /// <summary>
//        /// Get all competitions that the current user can manage
//        /// </summary>
//        public async Task<List<int>> GetManagedCompetitions()
//        {
//            var currentMember = await _memberManager.GetCurrentMemberAsync();
//            if (currentMember == null) return new List<int>();

//            // Convert member key to member ID for consistency
//            var member = _memberService.GetByKey(currentMember.Key);
//            if (member == null) return new List<int>();

//            return await GetManagedCompetitions(member.Id);
//        }

//        /// <summary>
//        /// Get all competitions that a specific member can manage
//        /// </summary>
//        public async Task<List<int>> GetManagedCompetitions(int memberId)
//        {
//            // Site administrators can manage all competitions
//            if (await IsUserInRole(memberId, "Administrators"))
//            {
//                // Return special indicator for admin access to all competitions
//                return new List<int> { -1 }; // -1 indicates admin access to all
//            }

//            // For regular members, the specific competitions they can manage
//            // will be determined by checking the competitionManagers User Picker property
//            // This will be implemented in the UI layer where we have access to content
//            return new List<int>();
//        }

//        /// <summary>
//        /// API endpoint to check competition access
//        /// </summary>
//        [HttpGet]
//        public async Task<IActionResult> CanAccessCompetition(int competitionId)
//        {
//            try
//            {
//                var canAccess = await CanManageCompetition(competitionId);
//                return Json(new { success = true, canAccess = canAccess });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = ex.Message });
//            }
//        }

//        /// <summary>
//        /// Get competition managers for a specific competition
//        /// </summary>
//        [HttpGet]
//        public IActionResult GetCompetitionManagers(int competitionId)
//        {
//            try
//            {
//                // This will be implemented later when we have proper content access
//                // For now, return a placeholder response
//                return Json(new {
//                    success = true,
//                    data = new List<object>(),
//                    message = "Competition manager retrieval not yet implemented"
//                });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = ex.Message });
//            }
//        }

//        /// <summary>
//        /// Check if a user has a specific role
//        /// </summary>
//        private async Task<bool> IsUserInRole(int memberId, string roleName)
//        {
//            try
//            {
//                var member = _memberService.GetById(memberId);
//                if (member == null) return false;

//                var roles = _memberService.GetAllRoles(member.Id);
//                return roles?.Any(r => r == roleName) ?? false;
//            }
//            catch
//            {
//                return false;
//            }
//        }

//        #endregion

//        #region Registration Management

//        [HttpGet]
//        public async Task<IActionResult> GetCompetitionRegistrations(int? competitionId = null)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                // Get all registration documents
//                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
//                var allRegistrations = allContent
//                    .Where(c => c.ContentType.Alias == "competitionRegistration")
//                    .Select(reg => new
//                    {
//                        id = reg.Id,
//                        competitionId = reg.GetValue<int>("competitionId"),
//                        competitionName = GetCompetitionName(reg.GetValue<int>("competitionId")),
//                        memberId = reg.GetValue<int>("memberId"),
//                        memberName = reg.GetValue<string>("memberName") ?? "",
//                        memberClub = reg.GetValue<string>("memberClub") ?? "",
//                        shootingClass = reg.GetValue<string>("shootingClass") ?? "",
//                        startPreference = reg.GetValue<string>("startPreference") ?? "Inget",
//                        registrationDate = reg.GetValue<DateTime>("registrationDate"),
//                        registeredBy = reg.GetValue<string>("registeredBy") ?? "",
//                        isActive = reg.GetValue<bool>("isActive"),
//                        shooterNotes = reg.GetValue<string>("shooterNotes") ?? ""
//                    })
//                    .OrderByDescending(r => r.registrationDate);

//                // Filter by competition if specified
//                var registrations = competitionId.HasValue
//                    ? allRegistrations.Where(r => r.competitionId == competitionId.Value).ToList()
//                    : allRegistrations.ToList();

//                // Calculate statistics
//                var stats = new
//                {
//                    totalRegistrations = registrations.Count,
//                    activeCompetitions = registrations.Select(r => r.competitionId).Distinct().Count(),
//                    uniqueMembers = registrations.Select(r => r.memberId).Distinct().Count(),
//                    popularClass = registrations.GroupBy(r => r.shootingClass)
//                                              .OrderByDescending(g => g.Count())
//                                              .FirstOrDefault()?.Key ?? "-"
//                };

//                return Json(new { success = true, registrations = registrations, statistics = stats });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading registrations: " + ex.Message });
//            }
//        }

//        [HttpGet]
//        public async Task<IActionResult> GetActiveCompetitions()
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                // Get all competition documents
//                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
//                var competitions = allContent
//                    .Where(c => c.ContentType.Alias == "competition")
//                    .Select(comp => new
//                    {
//                        id = comp.Id,
//                        name = comp.Name
//                    })
//                    .OrderBy(c => c.name)
//                    .ToList();

//                return Json(new { success = true, competitions = competitions });
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error loading competitions: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        public async Task<IActionResult> UpdateCompetitionRegistration([FromBody] UpdateRegistrationRequest request)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var registration = _contentService.GetById(request.RegistrationId);
//                if (registration == null)
//                {
//                    return Json(new { success = false, message = "Registration not found" });
//                }

//                // Update properties
//                registration.SetValue("startPreference", request.StartPreference ?? "Inget");
//                registration.SetValue("isActive", request.IsActive);

//                var saveResult = _contentService.Save(registration);
//                if (saveResult.Success)
//                {
//                    _contentService.Publish(registration, Array.Empty<string>());
//                    return Json(new { success = true, message = "Registration updated successfully" });
//                }
//                else
//                {
//                    return Json(new { success = false, message = "Failed to save registration" });
//                }
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error updating registration: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        public async Task<IActionResult> DeleteCompetitionRegistration([FromBody] DeleteRegistrationRequest request)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return Json(new { success = false, message = "Access denied" });
//            }

//            try
//            {
//                var registration = _contentService.GetById(request.RegistrationId);
//                if (registration == null)
//                {
//                    return Json(new { success = false, message = "Registration not found" });
//                }

//                var result = _contentService.Delete(registration);
//                if (result.Success)
//                {
//                    return Json(new { success = true, message = "Registration deleted successfully" });
//                }
//                else
//                {
//                    return Json(new { success = false, message = "Failed to delete registration" });
//                }
//            }
//            catch (Exception ex)
//            {
//                return Json(new { success = false, message = "Error deleting registration: " + ex.Message });
//            }
//        }

//        [HttpPost]
//        public async Task<IActionResult> ExportCompetitionRegistrations(int? competitionId = null)
//        {
//            if (!await IsCurrentUserAdminAsync())
//            {
//                return BadRequest("Access denied");
//            }

//            try
//            {
//                // Get registrations
//                var allContent = _contentService.GetRootContent().SelectMany(GetAllDescendants);
//                var registrations = allContent
//                    .Where(c => c.ContentType.Alias == "competitionRegistration")
//                    .Where(c => !competitionId.HasValue || c.GetValue<int>("competitionId") == competitionId.Value)
//                    .Select(reg => new
//                    {
//                        CompetitionName = GetCompetitionName(reg.GetValue<int>("competitionId")),
//                        MemberName = reg.GetValue<string>("memberName") ?? "",
//                        MemberClub = reg.GetValue<string>("memberClub") ?? "",
//                        ShootingClass = reg.GetValue<string>("shootingClass") ?? "",
//                        StartPreference = reg.GetValue<string>("startPreference") ?? "Inget",
//                        RegistrationDate = reg.GetValue<DateTime>("registrationDate"),
//                        RegisteredBy = reg.GetValue<string>("registeredBy") ?? "",
//                        IsActive = reg.GetValue<bool>("isActive") ? "Active" : "Inactive",
//                        ShooterNotes = reg.GetValue<string>("shooterNotes") ?? ""
//                    })
//                    .OrderBy(r => r.CompetitionName)
//                    .ThenBy(r => r.MemberName)
//                    .ThenBy(r => r.ShootingClass)
//                    .ToList();

//                // Create CSV content
//                var csv = new System.Text.StringBuilder();
//                csv.AppendLine("Competition,Member Name,Club,Shooting Class,Start Preference,Registration Date,Registered By,Status,Notes");

//                foreach (var reg in registrations)
//                {
//                    csv.AppendLine($"\"{reg.CompetitionName}\",\"{reg.MemberName}\",\"{reg.MemberClub}\",\"{reg.ShootingClass}\",\"{reg.StartPreference}\",\"{reg.RegistrationDate:yyyy-MM-dd HH:mm}\",\"{reg.RegisteredBy}\",\"{reg.IsActive}\",\"{reg.ShooterNotes}\"");
//                }

//                var fileName = competitionId.HasValue
//                    ? $"Competition_Registrations_{competitionId}_{DateTime.Now:yyyy-MM-dd}.csv"
//                    : $"All_Competition_Registrations_{DateTime.Now:yyyy-MM-dd}.csv";

//                return File(System.Text.Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", fileName);
//            }
//            catch (Exception ex)
//            {
//                return BadRequest("Error exporting registrations: " + ex.Message);
//            }
//        }

//        // Helper methods for registration management
//        private IEnumerable<IContent> GetAllDescendants(IContent content)
//        {
//            yield return content;

//            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out var totalRecords);
//            foreach (var child in children)
//            {
//                foreach (var descendant in GetAllDescendants(child))
//                {
//                    yield return descendant;
//                }
//            }
//        }

//        private string GetCompetitionName(int competitionId)
//        {
//            try
//            {
//                var competition = _contentService.GetById(competitionId);
//                return competition?.Name ?? "Unknown Competition";
//            }
//            catch
//            {
//                return "Unknown Competition";
//            }
//        }

//        // Request models for registration management
//        public class UpdateRegistrationRequest
//        {
//            public int RegistrationId { get; set; }
//            public string? StartPreference { get; set; }
//            public bool IsActive { get; set; }
//        }

//        public class DeleteRegistrationRequest
//        {
//            public int RegistrationId { get; set; }
//        }

//        #endregion

//    }
//}
