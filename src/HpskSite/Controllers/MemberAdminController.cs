using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models.ViewModels;
using HpskSite.Services;
using Microsoft.AspNetCore.Hosting;

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
            IWebHostEnvironment webHostEnvironment)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberGroupService = memberGroupService;
            _authService = authService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _emailService = emailService;
            _webHostEnvironment = webHostEnvironment;
        }

        /// <summary>
        /// Gets all members with their club information
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMembers()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get current valid clubs for validation
                var validClubs = GetClubsFromStorage().Where(c => c.IsActive).ToList();
                var validClubNames = validClubs.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var regularMembers = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias);
                var members = allMembers.Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .Select(m => {
                        var primaryClubId = m.GetValue("primaryClubId")?.ToString();
                        var primaryClubName = "No Club";
                        var hasInvalidClubReference = false;

                        if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out var clubId))
                        {
                            var club = validClubs.FirstOrDefault(c => c.Id == clubId);
                            if (club != null)
                            {
                                primaryClubName = club.Name;
                            }
                            else
                            {
                                primaryClubName = $"⚠️ Invalid Club (ID: {clubId})";
                                hasInvalidClubReference = true;
                            }
                        }

                        var lastActive = m.GetValue<DateTime?>("lastActiveDate");
                        var lastMobileActive = m.GetValue<DateTime?>("lastMobileActiveDate");

                        return new
                        {
                            Id = m.Id,
                            Name = m.Name,
                            Email = m.Email,
                            FirstName = m.GetValue("firstName")?.ToString() ?? "",
                            LastName = m.GetValue("lastName")?.ToString() ?? "",
                            ProfilePictureUrl = m.GetValue<string>("profilePictureUrl") ?? "",
                            PrimaryClubName = primaryClubName,
                            PrimaryClubId = primaryClubId,
                            IsApproved = m.IsApproved,
                            // Filter out ClubAdmin_* groups from display
                            Groups = _memberService.GetAllRoles(m.Id)
                                .Where(g => !g.StartsWith("ClubAdmin_", StringComparison.OrdinalIgnoreCase))
                                .ToArray(),
                            HasInvalidClubReference = hasInvalidClubReference,
                            LastActive = lastActive,
                            LastActiveDisplay = FormatLastActive(lastActive),
                            LastActiveSortValue = lastActive?.Ticks ?? 0,  // For sorting
                            LastMobileActive = lastMobileActive,
                            LastMobileActiveDisplay = FormatLastActive(lastMobileActive),
                            LastMobileActiveSortValue = lastMobileActive?.Ticks ?? 0
                        };
                    }).ToList();

                return Json(new { success = true, data = members });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading members: " + ex.Message });
            }
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

                    // Update groups
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
        /// Gets all available member groups
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberGroups()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
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
                }
                catch (Exception emailEx)
                {
                    // Log email error but don't fail the approval
                    Console.WriteLine($"[ApproveMember] Failed to send approval email to {member.Email}: {emailEx.Message}");
                    Console.WriteLine($"[ApproveMember] Email exception stack trace: {emailEx.StackTrace}");
                }

                Console.WriteLine($"[ApproveMember] Approval completed successfully for: {memberId}");
                return Json(new { success = true, message = "Member approved successfully" });
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

                // Send invitation email
                try
                {
                    var memberName = member.Name;
                    if (string.IsNullOrEmpty(memberName))
                    {
                        var firstName = member.GetValue<string>("firstName") ?? "";
                        var lastName = member.GetValue<string>("lastName") ?? "";
                        memberName = $"{firstName} {lastName}".Trim();
                        Console.WriteLine($"[SendInvitation] Using fallback name: '{memberName}'");
                    }

                    // Get primary club name
                    string clubName = "HPSK";
                    var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
                    if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
                    {
                        if (UmbracoContext?.Content != null)
                        {
                            var clubNode = UmbracoContext.Content.GetById(primaryClubId);
                            if (clubNode != null && clubNode.ContentType.Alias == "club")
                            {
                                clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "HPSK";
                                Console.WriteLine($"[SendInvitation] Using club name: '{clubName}'");
                            }
                        }
                    }

                    Console.WriteLine($"[SendInvitation] Sending invitation email to: {member.Email}");
                    await _emailService.SendMemberInvitationAsync(
                        member.Email,
                        memberName,
                        invitationToken,
                        clubName
                    );
                    Console.WriteLine($"[SendInvitation] Invitation email sent successfully to: {member.Email}");
                }
                catch (Exception emailEx)
                {
                    Console.WriteLine($"[SendInvitation] Failed to send invitation email to {member.Email}: {emailEx.Message}");
                    Console.WriteLine($"[SendInvitation] Email exception stack trace: {emailEx.StackTrace}");
                    return Json(new { success = false, message = "Failed to send invitation email: " + emailEx.Message });
                }

                Console.WriteLine($"[SendInvitation] Invitation sent successfully for: {memberId}");
                return Json(new { success = true, message = "Invitation sent successfully" });
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
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPendingApprovals()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);

                // Get clubs for club name lookup
                var clubs = GetClubsFromStorage();

                var pendingMembers = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias) // Exclude clubs
                    .Where(m => !m.IsApproved) // Only non-approved members
                    .Select(m => {
                        var primaryClubId = m.GetValue("primaryClubId")?.ToString();
                        var clubName = "No Club";

                        if (!string.IsNullOrEmpty(primaryClubId) && int.TryParse(primaryClubId, out var clubId))
                        {
                            var club = clubs.FirstOrDefault(c => c.Id == clubId);
                            clubName = club?.Name ?? $"Unknown Club (ID: {clubId})";
                        }

                        return new {
                            Id = m.Id,
                            Name = $"{m.GetValue("firstName")} {m.GetValue("lastName")}".Trim(),
                            Email = m.Email,
                            ClubName = clubName,
                            RegistrationDate = m.CreateDate
                        };
                    })
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
                    // Get the clubs hub page (clubsPage)
                    var root = umbracoContext.Content.GetAtRoot().FirstOrDefault();
                    if (root == null) return clubs;

                    // Find clubsPage - it should be a direct child of root
                    var clubsHub = root.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                    if (clubsHub == null) return clubs;

                    // Get all club nodes (children of clubsPage)
                    var clubNodes = clubsHub.Children().Where(c => c.ContentType.Alias == "club").ToList();

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
                return "Never";
            }

            var elapsed = DateTime.UtcNow - lastActive.Value;

            // Less than 5 minutes
            if (elapsed.TotalMinutes < 5)
            {
                return "Just now";
            }

            // Less than 1 hour
            if (elapsed.TotalMinutes < 60)
            {
                var minutes = (int)elapsed.TotalMinutes;
                return $"{minutes} min ago";
            }

            // Less than 24 hours
            if (elapsed.TotalHours < 24)
            {
                var hours = (int)elapsed.TotalHours;
                return $"{hours}h ago";
            }

            // 24 hours or more - show absolute date/time
            return lastActive.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
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
