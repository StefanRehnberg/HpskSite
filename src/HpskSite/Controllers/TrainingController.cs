using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Models;
using HpskSite.Models.ViewModels.Training;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    public class TrainingController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _authorizationService;
        private const string ClubMemberTypeAlias = "hpskClub";

        public TrainingController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberManager memberManager,
            ClubService clubService,
            AdminAuthorizationService authorizationService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _authorizationService = authorizationService;
        }

        /// <summary>
        /// Get training overview with all member progress and statistics
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTrainingOverview()
        {
            try
            {
                var overview = new TrainingOverview
                {
                    AllLevels = TrainingDefinitions.GetAllLevels()
                };

                // Get all active members (excluding clubs)
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords)
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias && m.IsApproved)
                    .ToList();

                // Load progress for each member
                foreach (var member in allMembers)
                {
                    var clubName = GetMemberPrimaryClubName(member);
                    var progress = MemberProgress.FromMember(member, clubName);

                    // Only include members who have started training
                    if (progress.IsActive)
                    {
                        overview.MemberProgress.Add(progress);
                    }
                }

                // Get current member's progress if logged in
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                MemberProgress? currentMemberProgress = null;
                if (currentMember != null)
                {
                    var currentMemberData = _memberService.GetByEmail(currentMember.Email ?? "");
                    if (currentMemberData != null)
                    {
                        var clubName = GetMemberPrimaryClubName(currentMemberData);
                        currentMemberProgress = MemberProgress.FromMember(currentMemberData, clubName);
                        overview.CurrentMemberProgress = currentMemberProgress;
                    }
                }

                // Calculate statistics
                overview.Statistics = TrainingStatistics.Calculate(overview.MemberProgress);

                // Build response with serialized progress data
                var response = new
                {
                    allLevels = overview.AllLevels,
                    memberProgress = overview.MemberProgress,
                    statistics = overview.Statistics,
                    currentMemberProgress = currentMemberProgress != null ? new
                    {
                        currentMemberProgress.MemberId,
                        currentMemberProgress.MemberName,
                        currentMemberProgress.PrimaryClubName,
                        currentMemberProgress.CurrentLevel,
                        currentMemberProgress.CurrentStep,
                        currentMemberProgress.TrainingStartDate,
                        currentMemberProgress.LastActivityDate,
                        currentMemberProgress.CompletedSteps,
                        currentMemberProgress.Notes,
                        levelCompletionPercentage = currentMemberProgress.GetLevelCompletionPercentage(),
                        overallCompletionPercentage = currentMemberProgress.GetOverallCompletionPercentage()
                    } : null
                };

                return Json(new { success = true, data = response });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get detailed progress for a specific member
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberProgress(int? memberId = null)
        {
            try
            {
                IMember? member = null;

                if (memberId.HasValue)
                {
                    // Admin accessing specific member's progress
                    if (!await _authorizationService.IsCurrentUserAdminAsync())
                    {
                        return Json(new { success = false, message = "Access denied" });
                    }
                    member = _memberService.GetById(memberId.Value);
                }
                else
                {
                    // Current member accessing their own progress
                    var currentMember = await _memberManager.GetCurrentMemberAsync();
                    if (currentMember != null)
                    {
                        member = _memberService.GetByEmail(currentMember.Email ?? "");
                    }
                }

                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                var clubName = GetMemberPrimaryClubName(member);
                var progress = MemberProgress.FromMember(member, clubName);

                // Get current level and step details
                var currentLevel = TrainingDefinitions.GetLevel(progress.CurrentLevel);
                var currentStep = TrainingDefinitions.GetStep(progress.CurrentLevel, progress.CurrentStep);

                var result = new
                {
                    progress = new
                    {
                        progress.MemberId,
                        progress.MemberName,
                        progress.PrimaryClubName,
                        progress.CurrentLevel,
                        progress.CurrentStep,
                        progress.TrainingStartDate,
                        progress.LastActivityDate,
                        progress.CompletedSteps,
                        progress.Notes,
                        levelCompletionPercentage = progress.GetLevelCompletionPercentage(),
                        overallCompletionPercentage = progress.GetOverallCompletionPercentage()
                    },
                    currentLevel = currentLevel,
                    currentStep = currentStep,
                    allLevels = TrainingDefinitions.GetAllLevels()
                };

                return Json(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Start training for current member
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartTraining()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var member = _memberService.GetByEmail(currentMember.Email ?? "");
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                var progress = MemberProgress.FromMember(member);

                // Check if already started
                if (progress.IsActive)
                {
                    return Json(new { success = false, message = "Training already started" });
                }

                // Initialize training
                progress.TrainingStartDate = DateTime.Now;
                progress.CurrentLevel = 1;
                progress.CurrentStep = 1;
                progress.LastActivityDate = DateTime.Now;

                // Save to member
                progress.SaveToMember(member);
                _memberService.Save(member);

                return Json(new { success = true, message = "Training started successfully!" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Complete a training step (admin only)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteStep(int memberId, int levelId, int stepNumber, string? notes = null)
        {
            try
            {
                if (!await _authorizationService.IsCurrentUserAdminAsync())
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Validate step exists
                var step = TrainingDefinitions.GetStep(levelId, stepNumber);
                if (step == null)
                {
                    return Json(new { success = false, message = "Invalid step" });
                }

                var progress = MemberProgress.FromMember(member);

                // Check if step is already completed
                if (progress.IsStepCompleted(levelId, stepNumber))
                {
                    return Json(new { success = false, message = "Step already completed" });
                }

                // Get instructor name
                var currentUser = await _memberManager.GetCurrentMemberAsync();
                var instructorName = currentUser?.Name ?? "Admin";

                // Complete the step
                progress.CompleteStep(levelId, stepNumber, instructorName, notes);

                // Save progress
                progress.SaveToMember(member);
                _memberService.Save(member);

                return Json(new {
                    success = true,
                    message = "Step completed successfully!",
                    data = new {
                        newLevel = progress.CurrentLevel,
                        newStep = progress.CurrentStep,
                        levelCompleted = progress.CurrentStep == 1 && progress.CurrentLevel > levelId
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get training leaderboard
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetLeaderboard()
        {
            try
            {
                // Check if user is logged in for privacy control
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                var isLoggedIn = currentMember != null;

                // Get all members with training progress
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords)
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias && m.IsApproved)
                    .ToList();

                var leaderboard = new List<object>();

                foreach (var member in allMembers)
                {
                    var progress = MemberProgress.FromMember(member);
                    if (progress.IsActive)
                    {
                        var clubName = GetMemberPrimaryClubName(member);
                        var currentLevel = TrainingDefinitions.GetLevel(progress.CurrentLevel);

                        // Get firstName and lastName properties
                        var firstName = member.GetValue("firstName")?.ToString() ?? "";
                        var lastName = member.GetValue("lastName")?.ToString() ?? "";

                        // Format display name based on login status
                        var displayName = member.Name; // Fallback to full name
                        if (isLoggedIn)
                        {
                            // Logged in users see full name
                            displayName = member.Name;
                        }
                        else
                        {
                            // Non-logged in users see first name + last initial
                            if (!string.IsNullOrEmpty(firstName) && !string.IsNullOrEmpty(lastName))
                            {
                                displayName = $"{firstName} {lastName.Substring(0, 1)}.";
                            }
                            else if (!string.IsNullOrEmpty(firstName))
                            {
                                displayName = firstName;
                            }
                            else
                            {
                                // Fallback: extract from full name if no firstName/lastName properties
                                var nameParts = member.Name?.Split(' ') ?? new[] { "Okänd" };
                                if (nameParts.Length >= 2)
                                {
                                    displayName = $"{nameParts[0]} {nameParts[1].Substring(0, 1)}.";
                                }
                                else
                                {
                                    displayName = nameParts[0];
                                }
                            }
                        }

                        // Get club membership data for authorization
                        var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
                        int? primaryClubId = null;
                        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int parsedClubId))
                        {
                            primaryClubId = parsedClubId;
                        }
                        var memberClubIds = member.GetValue("memberClubIds")?.ToString() ?? "";

                        leaderboard.Add(new
                        {
                            memberId = member.Id,
                            memberName = displayName,
                            fullName = member.Name, // Keep full name for admin purposes
                            firstName = firstName,
                            lastName = lastName,
                            clubName = clubName,
                            primaryClubId = primaryClubId, // For authorization check
                            memberClubIds = memberClubIds, // CSV of additional club IDs for authorization
                            currentLevel = progress.CurrentLevel,
                            currentStep = progress.CurrentStep,
                            levelName = currentLevel?.Name ?? "Unknown",
                            levelBadge = currentLevel?.Badge ?? "",
                            completedSteps = progress.CompletedSteps.Count,
                            lastActivity = progress.LastActivityDate,
                            overallProgress = progress.GetOverallCompletionPercentage(),
                            isLoggedIn = isLoggedIn
                        });
                    }
                }

                // Sort by level (desc), then step (desc), then last activity (desc)
                leaderboard = leaderboard
                    .OrderByDescending(l => (int)l.GetType().GetProperty("currentLevel")!.GetValue(l)!)
                    .ThenByDescending(l => (int)l.GetType().GetProperty("currentStep")!.GetValue(l)!)
                    .ThenByDescending(l => (DateTime?)l.GetType().GetProperty("lastActivity")!.GetValue(l))
                    .ToList();

                return Json(new { success = true, data = leaderboard, isLoggedIn = isLoggedIn });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Reset member's training progress (admin only)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetProgress(int memberId)
        {
            try
            {
                if (!await _authorizationService.IsCurrentUserAdminAsync())
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Clear all training properties
                member.SetValue("currentTrainingLevel", 1);
                member.SetValue("currentTrainingStep", 1);
                member.SetValue("trainingStartDate", null);
                member.SetValue("lastTrainingActivity", null);
                member.SetValue("trainingNotes", null);
                member.SetValue("completedTrainingSteps", "[]");

                _memberService.Save(member);

                return Json(new { success = true, message = "Training progress reset successfully" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get current user's admin status and managed club IDs for training stairs authorization
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetTrainingAdminStatus()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = true, data = new {
                        isAdmin = false,
                        isSiteAdmin = false,
                        managedClubIds = new List<int>()
                    }});
                }

                var isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var managedClubIds = await _authorizationService.GetManagedClubIds();

                return Json(new {
                    success = true,
                    data = new {
                        isAdmin = isSiteAdmin || managedClubIds.Any(), // True if site admin OR club admin
                        isSiteAdmin = isSiteAdmin,
                        managedClubIds = managedClubIds
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }


        /// <summary>
        /// Get member's primary club name
        /// </summary>
        private string? GetMemberPrimaryClubName(IMember member)
        {
            var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
            if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int primaryClubId))
            {
                return _clubService.GetClubNameById(primaryClubId);
            }
            return null;
        }
    }
}