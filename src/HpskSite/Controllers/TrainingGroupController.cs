using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Cms.Core.Security;
using Microsoft.Extensions.Logging;
using HpskSite.Services;
using HpskSite.Models.ViewModels.Training;

namespace HpskSite.Controllers
{
    public class TrainingGroupController : SurfaceController
    {
        private readonly TrainingGroupService _trainingGroupService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ClubService _clubService;
        private readonly EmailService _emailService;
        private readonly ILogger<TrainingGroupController> _logger;

        public TrainingGroupController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            TrainingGroupService trainingGroupService,
            AdminAuthorizationService authorizationService,
            IMemberService memberService,
            IMemberManager memberManager,
            ClubService clubService,
            EmailService emailService,
            ILogger<TrainingGroupController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _trainingGroupService = trainingGroupService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _emailService = emailService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetTrainingGroups()
        {
            try
            {
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var managedClubIds = await _authorizationService.GetManagedClubIds();
                var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();

                if (!isSiteAdmin && !managedClubIds.Any() && !skjutledareClubIds.Any())
                    return Json(new { success = false, message = "Access denied" });

                List<Shared.Models.TrainingGroup> groups;

                if (isSiteAdmin)
                {
                    groups = _trainingGroupService.GetAllTrainingGroups(null, includeInactive: true);
                }
                else
                {
                    groups = new List<Shared.Models.TrainingGroup>();
                    // Combine managed club IDs and skjutledare club IDs
                    var allClubIds = new HashSet<int>(managedClubIds);
                    foreach (var id in skjutledareClubIds) allClubIds.Add(id);
                    foreach (var clubId in allClubIds)
                    {
                        groups.AddRange(_trainingGroupService.GetTrainingGroupsForClub(clubId, includeInactive: true));
                    }
                }

                return Json(new
                {
                    success = true,
                    data = groups.Select(g => new
                    {
                        g.Id,
                        g.Name,
                        g.ClubId,
                        g.ClubName,
                        g.Description,
                        startDate = g.StartDate.ToString("yyyy-MM-dd"),
                        g.IsActive,
                        createdDate = g.CreatedDate.ToString("yyyy-MM-dd"),
                        g.MemberCount,
                        g.TrainerCount
                    })
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetTrainingGroup(int trainingGroupId)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                var group = _trainingGroupService.GetTrainingGroup(trainingGroupId);
                if (group == null)
                    return Json(new { success = false, message = "Training group not found" });

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        group.Id,
                        group.Name,
                        group.ClubId,
                        group.ClubName,
                        group.Description,
                        startDate = group.StartDate.ToString("yyyy-MM-dd"),
                        group.IsActive,
                        createdDate = group.CreatedDate.ToString("yyyy-MM-dd"),
                        group.MemberCount,
                        group.TrainerCount,
                        members = group.Members.Select(m => new
                        {
                            m.Id,
                            m.MemberId,
                            m.MemberName,
                            m.ClubName,
                            m.Role,
                            joinedDate = m.JoinedDate.ToString("yyyy-MM-dd"),
                            m.IsTrainer
                        })
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateTrainingGroup(string name, int clubId, string? description, string startDate)
        {
            try
            {
                bool isClubAdmin = await _authorizationService.IsClubAdminForClub(clubId);
                bool isSkjutledare = !isClubAdmin && await _authorizationService.IsSkjutledareForClub(clubId);
                if (!isClubAdmin && !isSkjutledare)
                    return Json(new { success = false, message = "Access denied" });

                if (string.IsNullOrWhiteSpace(name))
                    return Json(new { success = false, message = "Name is required" });

                if (!DateTime.TryParse(startDate, out DateTime parsedDate))
                    return Json(new { success = false, message = "Invalid start date" });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Not logged in" });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = false, message = "Member not found" });

                var group = _trainingGroupService.CreateTrainingGroup(name, clubId, description, parsedDate, memberData.Id);

                return Json(new
                {
                    success = true,
                    message = "Träningsgrupp skapad",
                    data = new { group.Id, group.Name }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateTrainingGroup(int trainingGroupId, string name, string? description, string startDate, bool? isActive = null)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                if (string.IsNullOrWhiteSpace(name))
                    return Json(new { success = false, message = "Name is required" });

                if (!DateTime.TryParse(startDate, out DateTime parsedDate))
                    return Json(new { success = false, message = "Invalid start date" });

                _trainingGroupService.UpdateTrainingGroup(trainingGroupId, name, description, parsedDate, isActive);

                return Json(new { success = true, message = "Träningsgrupp uppdaterad" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeactivateTrainingGroup(int trainingGroupId)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                _trainingGroupService.DeactivateTrainingGroup(trainingGroupId);

                return Json(new { success = true, message = "Träningsgrupp borttagen" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTrainingGroupMember(int trainingGroupId, int memberId, string role = "Member", bool sendEmail = false)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                if (role != "Member" && role != "Trainer")
                    return Json(new { success = false, message = "Invalid role" });

                var member = _memberService.GetById(memberId);
                if (member == null)
                    return Json(new { success = false, message = "Member not found" });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var currentMemberData = currentMember != null ? _memberService.GetByEmail(currentMember.Email ?? "") : null;

                _trainingGroupService.AddTrainingGroupMember(trainingGroupId, memberId, role, currentMemberData?.Id);

                // Send welcome email if requested (non-blocking)
                if (sendEmail)
                {
                    try
                    {
                        var memberEmail = member.Email;
                        if (!string.IsNullOrEmpty(memberEmail))
                        {
                            var group = _trainingGroupService.GetTrainingGroup(trainingGroupId);
                            var clubName = group?.ClubName ?? "";
                            var startDate = group?.StartDate.ToString("yyyy-MM-dd") ?? "";

                            if (role == "Trainer")
                            {
                                // Trainer welcome — list OTHER trainers in the group (exclude the recipient).
                                var otherTrainerNames = string.Join(", ", group?.Members
                                    .Where(m => m.Role == "Trainer" && m.MemberId != memberId)
                                    .Select(m => m.MemberName) ?? Enumerable.Empty<string>());

                                _ = _emailService.SendTrainingGroupTrainerAddedAsync(
                                    memberEmail, member.Name ?? "", group?.Name ?? "",
                                    otherTrainerNames, startDate, clubName);
                            }
                            else
                            {
                                var trainerNames = string.Join(", ", group?.Members
                                    .Where(m => m.Role == "Trainer")
                                    .Select(m => m.MemberName) ?? Enumerable.Empty<string>());

                                _ = _emailService.SendTrainingGroupMemberAddedAsync(
                                    memberEmail, member.Name ?? "", group?.Name ?? "",
                                    trainerNames, startDate, clubName);
                            }
                        }
                    }
                    catch (Exception emailEx)
                    {
                        _logger.LogWarning(emailEx, "Failed to send training group welcome email to member {MemberId}", memberId);
                    }
                }

                return Json(new { success = true, message = "Medlem tillagd i träningsgrupp" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveTrainingGroupMember(int trainingGroupId, int memberId)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                _trainingGroupService.RemoveTrainingGroupMember(trainingGroupId, memberId);

                return Json(new { success = true, message = "Medlem borttagen från träningsgrupp" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetTrainingGroupMemberRole(int trainingGroupId, int memberId, string role)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                if (role != "Member" && role != "Trainer")
                    return Json(new { success = false, message = "Invalid role" });

                _trainingGroupService.SetTrainingGroupMemberRole(trainingGroupId, memberId, role);

                return Json(new { success = true, message = "Roll uppdaterad" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetMyTrainingGroups()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = true, data = new List<object>() });

                var memberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (memberData == null)
                    return Json(new { success = true, data = new List<object>() });

                var groups = _trainingGroupService.GetTrainingGroupsForMember(memberData.Id);

                return Json(new
                {
                    success = true,
                    data = groups.Select(g => new
                    {
                        g.Id,
                        g.Name,
                        g.ClubId,
                        g.ClubName,
                        g.Description,
                        startDate = g.StartDate.ToString("yyyy-MM-dd"),
                        g.MemberCount,
                        g.TrainerCount,
                        myRole = g.Members.FirstOrDefault(m => m.MemberId == memberData.Id)?.Role ?? "Member",
                        trainers = g.Members.Where(m => m.IsTrainer).Select(m => new
                        {
                            m.MemberId,
                            m.MemberName
                        })
                    })
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetTrainingGroupProgress(int trainingGroupId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Not logged in" });

                var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? "");
                if (currentMemberData == null)
                    return Json(new { success = false, message = "Member not found" });

                // Check if user is a member of this training group OR can manage it
                bool canView = await _trainingGroupService.CanManageTrainingGroup(trainingGroupId);

                if (!canView)
                {
                    // Check if member is in the training group
                    var myGroups = _trainingGroupService.GetTrainingGroupsForMember(currentMemberData.Id);
                    canView = myGroups.Any(g => g.Id == trainingGroupId);
                }

                if (!canView)
                    return Json(new { success = false, message = "Access denied" });

                var group = _trainingGroupService.GetTrainingGroup(trainingGroupId);
                if (group == null)
                    return Json(new { success = false, message = "Training group not found" });

                // Build progress for each member
                var memberProgress = new List<object>();

                foreach (var gm in group.Members)
                {
                    var member = _memberService.GetById(gm.MemberId);
                    if (member == null) continue;

                    var progress = MemberProgress.FromMember(member);
                    var currentLevel = TrainingDefinitions.GetLevel(progress.CurrentLevel);

                    memberProgress.Add(new
                    {
                        memberId = gm.MemberId,
                        memberName = gm.MemberName,
                        role = gm.Role,
                        currentLevel = progress.CurrentLevel,
                        currentStep = progress.CurrentStep,
                        levelName = currentLevel?.Name ?? "Okänd",
                        levelBadge = currentLevel?.Badge ?? "",
                        overallProgress = progress.GetOverallCompletionPercentage(),
                        lastActivity = progress.LastActivityDate?.ToString("yyyy-MM-dd"),
                        completedSteps = progress.CompletedSteps?.Count ?? 0
                    });
                }

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        group = new
                        {
                            group.Id,
                            group.Name,
                            group.ClubName,
                            startDate = group.StartDate.ToString("yyyy-MM-dd")
                        },
                        members = memberProgress
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> SearchMembers(string query, int? clubId = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Not logged in" });

                // Must be some kind of admin, skjutledare, or trainer
                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var managedClubIds = await _authorizationService.GetManagedClubIds();
                var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();
                if (!isSiteAdmin && !managedClubIds.Any() && !skjutledareClubIds.Any())
                    return Json(new { success = false, message = "Access denied" });

                if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
                    return Json(new { success = true, data = new List<object>() });

                var matches = _memberService.GetAll(0, int.MaxValue, out var totalRecords)
                    .Where(m => m.ContentType.Alias != "hpskClub" && m.IsApproved)
                    .Where(m => (m.Name ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                (m.Email ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));

                if (clubId.HasValue)
                {
                    var clubIdStr = clubId.Value.ToString();
                    // Match members affiliated with the club — primary club OR additional clubs.
                    // Applied before Take(20) so club members aren't dropped when a common query
                    // returns 20+ name matches from other clubs first.
                    matches = matches.Where(m =>
                    {
                        var pcid = m.GetValue("primaryClubId")?.ToString();
                        if (pcid == clubIdStr)
                            return true;

                        return (m.GetValue("memberClubIds")?.ToString()?.Split(',')
                            .Select(s => s.Trim())
                            .Contains(clubIdStr) ?? false);
                    });
                }

                var allMembers = matches.Take(20).ToList();

                var results = allMembers.Select(m =>
                {
                    var pcidStr = m.GetValue("primaryClubId")?.ToString();
                    string? memberClubName = null;
                    if (!string.IsNullOrEmpty(pcidStr) && int.TryParse(pcidStr, out int pcid))
                    {
                        memberClubName = _clubService.GetClubNameById(pcid);
                    }

                    return new
                    {
                        memberId = m.Id,
                        memberName = m.Name,
                        email = m.Email,
                        clubName = memberClubName
                    };
                });

                return Json(new { success = true, data = results });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendGroupMessage(int trainingGroupId, string subject, string message)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Access denied" });

                if (string.IsNullOrWhiteSpace(subject))
                    return Json(new { success = false, message = "Ämne krävs" });

                if (string.IsNullOrWhiteSpace(message))
                    return Json(new { success = false, message = "Meddelande krävs" });

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var senderName = currentMember?.Name ?? "Tränare";

                var group = _trainingGroupService.GetTrainingGroup(trainingGroupId);
                if (group == null)
                    return Json(new { success = false, message = "Träningsgrupp hittades inte" });

                var currentMemberData = _memberService.GetByEmail(currentMember?.Email ?? "");
                int senderId = currentMemberData?.Id ?? 0;

                int sentCount = 0;
                foreach (var gm in group.Members)
                {
                    // Don't send to the sender
                    if (gm.MemberId == senderId) continue;

                    var member = _memberService.GetById(gm.MemberId);
                    if (member == null || string.IsNullOrEmpty(member.Email)) continue;

                    try
                    {
                        await _emailService.SendTrainingGroupMessageAsync(
                            member.Email, member.Name ?? "", senderName,
                            group.Name, subject, message);
                        sentCount++;
                    }
                    catch (Exception emailEx)
                    {
                        _logger.LogWarning(emailEx, "Failed to send group message to member {MemberId}", gm.MemberId);
                    }
                }

                return Json(new { success = true, message = $"Meddelande skickat till {sentCount} mottagare" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
