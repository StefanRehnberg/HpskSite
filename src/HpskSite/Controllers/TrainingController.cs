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
using Microsoft.Extensions.Logging;
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
        private readonly TrainingGroupService _trainingGroupService;
        private readonly EmailService _emailService;
        private readonly MarkenLedgerService _markenLedger;
        private readonly ILogger<TrainingController> _logger;
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
            AdminAuthorizationService authorizationService,
            TrainingGroupService trainingGroupService,
            EmailService emailService,
            MarkenLedgerService markenLedger,
            ILogger<TrainingController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _authorizationService = authorizationService;
            _trainingGroupService = trainingGroupService;
            _emailService = emailService;
            _markenLedger = markenLedger;
            _logger = logger;
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
                    // Check authorization: site admin, trainer for member, skjutledare, or club admin
                    bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                    bool isTrainer = false;
                    bool isSkjutledare = false;
                    bool isClubAdmin = false;

                    if (!isSiteAdmin)
                    {
                        var currentUser = await _memberManager.GetCurrentMemberAsync();
                        var currentData = currentUser != null ? _memberService.GetByEmail(currentUser.Email ?? "") : null;
                        if (currentData != null)
                            isTrainer = await _trainingGroupService.IsTrainerForMember(currentData.Id, memberId.Value);

                        if (!isTrainer)
                            isSkjutledare = await _authorizationService.IsSkjutledareForMember(memberId.Value);

                        if (!isTrainer && !isSkjutledare)
                        {
                            var target = _memberService.GetById(memberId.Value);
                            var targetClubId = int.TryParse(target?.GetValue("primaryClubId")?.ToString(), out int cid) ? cid : 0;
                            if (targetClubId > 0)
                                isClubAdmin = await _authorizationService.IsClubAdminForClub(targetClubId);
                        }
                    }

                    if (!isSiteAdmin && !isTrainer && !isSkjutledare && !isClubAdmin)
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
        /// Complete a training step.
        /// Functionaries (site admin, trainer, skjutledare, club admin) may approve any step.
        /// From <see cref="TrainingDefinitions.SelfServiceMinLevel"/> and up a shooter may also tick
        /// their own next step - see <see cref="CanSelfReport"/> for why the beginner levels cannot.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteStep(int memberId, int levelId, int stepNumber, string? notes = null)
        {
            try
            {
                var (isFunctionary, _, currentMemberData) = await ResolveStepAuthorityAsync(memberId);
                var currentUser = await _memberManager.GetCurrentMemberAsync();
                bool isSelf = currentMemberData != null && currentMemberData.Id == memberId;

                if (!isFunctionary && !isSelf)
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

                // No functionary role: this is a shooter reporting their own progress. Allowed only on
                // the self-service levels, and only for the step they are actually standing on.
                bool isSelfService = false;
                if (!isFunctionary)
                {
                    if (!CanSelfReport(progress, levelId, stepNumber, out var refusal))
                    {
                        return Json(new { success = false, message = refusal });
                    }
                    isSelfService = true;
                }

                // Get instructor name (null for self-reported steps - nobody signed them off)
                var instructor = currentUser;
                var instructorName = isSelfService ? null : (instructor?.Name ?? "Admin");

                // Complete the step
                progress.CompleteStep(levelId, stepNumber, instructorName, notes, isSelfService);

                // Save progress
                progress.SaveToMember(member);
                _memberService.Save(member);

                // Skyttetrappan → Pistolskyttemärket link: completing all steps of levels 1/2/3
                // (Nybörjartrappa Brons/Silver/Guld) awards the matching base valör, stamped with
                // the approving functionary. Idempotent; best-effort so it never breaks step approval.
                // Belt and braces: self-service can never reach levels 1-3, but if that ever changes
                // the badge must NOT be minted from a self-reported step.
                if (!isSelfService && levelId is 1 or 2 or 3)
                {
                    try
                    {
                        var actingMember = _memberService.GetByEmail(instructor?.Email ?? string.Empty);
                        await _markenLedger.SyncTrappaBadgesAsync(memberId, progress.CompletedSteps, actingMember?.Id);
                    }
                    catch (Exception markenEx)
                    {
                        _logger.LogWarning(markenEx, "Failed to sync Skyttetrappan märke for member {MemberId}", memberId);
                    }
                }

                // Send notification email (non-blocking). Skipped for self-reported steps - there is
                // no point mailing the shooter about something they just ticked themselves.
                try
                {
                    var memberEmail = member.Email;
                    if (!isSelfService && !string.IsNullOrEmpty(memberEmail))
                    {
                        var level = TrainingDefinitions.GetLevel(levelId);
                        _ = _emailService.SendTrainingStepApprovedAsync(
                            memberEmail,
                            member.Name ?? "",
                            level?.Name ?? $"Niv\u00e5 {levelId}",
                            level?.Badge ?? "",
                            stepNumber,
                            step.Description,
                            instructorName);
                    }
                }
                catch (Exception emailEx)
                {
                    _logger.LogWarning(emailEx, "Failed to send step approval email to member {MemberId}", memberId);
                }

                return Json(new {
                    success = true,
                    message = "Step completed successfully!",
                    data = new {
                        newLevel = progress.CurrentLevel,
                        newStep = progress.CurrentStep,
                        levelCompleted = progress.CurrentStep == 1 && progress.CurrentLevel > levelId,
                        selfReported = isSelfService
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// The four-tier "may act on this member's trappa" check, shared by CompleteStep and
        /// UncompleteStep so approving and undoing cannot drift apart:
        /// site admin, trainer of the member's active training group, skjutledare at their club,
        /// club admin of their club.
        /// </summary>
        private async Task<(bool IsFunctionary, bool IsSiteAdmin, IMember? CurrentMember)> ResolveStepAuthorityAsync(int memberId)
        {
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

            var currentUser = await _memberManager.GetCurrentMemberAsync();
            var currentMemberData = currentUser != null ? _memberService.GetByEmail(currentUser.Email ?? "") : null;

            if (isSiteAdmin) return (true, true, currentMemberData);

            bool isTrainer = false;
            if (currentMemberData != null)
                isTrainer = await _trainingGroupService.IsTrainerForMember(currentMemberData.Id, memberId);

            bool isSkjutledare = false;
            if (!isTrainer)
                isSkjutledare = await _authorizationService.IsSkjutledareForMember(memberId);

            bool isClubAdmin = false;
            if (!isTrainer && !isSkjutledare)
            {
                var targetMember = _memberService.GetById(memberId);
                var targetClubId = int.TryParse(targetMember?.GetValue("primaryClubId")?.ToString(), out int cid) ? cid : 0;
                if (targetClubId > 0)
                    isClubAdmin = await _authorizationService.IsClubAdminForClub(targetClubId);
            }

            return (isTrainer || isSkjutledare || isClubAdmin, false, currentMemberData);
        }

        /// <summary>
        /// Decide whether a shooter may record this step on their own, and if not, why.
        /// Two rules, both deliberate:
        ///  1. Only levels 4+ (Guldmarkesskytt and up). Levels 1-3 finish with an official
        ///     Pistolskyttemarke being minted, and nobody signs off their own marke.
        ///  2. Only the step they are standing on, so the ladder cannot be skipped to the top.
        /// </summary>
        private static bool CanSelfReport(MemberProgress progress, int levelId, int stepNumber, out string refusal)
        {
            if (!TrainingDefinitions.IsSelfServiceLevel(levelId))
            {
                refusal = "Stegen i nybörjartrappan (brons, silver och guld) godkänns av din tränare, "
                        + "skjutledare eller klubbadmin, eftersom de ger ett officiellt märke.";
                return false;
            }

            if (levelId != progress.CurrentLevel || stepNumber != progress.CurrentStep)
            {
                refusal = "Du kan bara markera det steg du står på just nu, "
                        + $"{TrainingDefinitions.GetLevel(progress.CurrentLevel)?.Name} steg {progress.CurrentStep}.";
                return false;
            }

            refusal = string.Empty;
            return true;
        }

        /// <summary>
        /// Undo a completed step. Restricted to the self-service levels (4+) on purpose: undoing a
        /// step in levels 1-3 would leave an already-minted marke behind with nothing backing it,
        /// so those corrections stay a site-admin ResetProgress matter.
        /// Whoever may APPROVE a step may also undo it (symmetry - otherwise a trainer's mis-tick on
        /// level 4 could only be cleared by a site admin). A shooter may undo only their own
        /// self-reported steps: a functionary's sign-off is not theirs to withdraw.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UncompleteStep(int memberId, int levelId, int stepNumber)
        {
            try
            {
                if (!TrainingDefinitions.IsSelfServiceLevel(levelId))
                {
                    return Json(new { success = false, message = "Steg i nybörjartrappan kan inte ångras här - kontakta en administratör." });
                }

                var (isFunctionary, _, currentMemberData) = await ResolveStepAuthorityAsync(memberId);
                bool isSelf = currentMemberData != null && currentMemberData.Id == memberId;

                if (!isFunctionary && !isSelf)
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                var progress = MemberProgress.FromMember(member);
                var completion = progress.GetCompletion(levelId, stepNumber);
                if (completion == null)
                {
                    return Json(new { success = false, message = "Steget är inte markerat som klart." });
                }

                if (!isFunctionary && !completion.SelfReported)
                {
                    return Json(new { success = false, message = "Det här steget godkändes av en funktionär och kan bara ångras av dem." });
                }

                progress.UncompleteStep(levelId, stepNumber);
                progress.SaveToMember(member);
                _memberService.Save(member);

                return Json(new {
                    success = true,
                    message = "Markeringen är borttagen.",
                    data = new { newLevel = progress.CurrentLevel, newStep = progress.CurrentStep }
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

                // Scope: participants/trainers see only their own training-group peers;
                // club/regional admins + skjutledare see their clubs; site admins see everyone.
                var currentMemberData = currentMember != null ? _memberService.GetByEmail(currentMember.Email ?? "") : null;
                var scope = await GetVisibleMemberScopeAsync(currentMemberData, allMembers);

                var leaderboard = new List<object>();

                foreach (var member in allMembers)
                {
                    if (scope != null && !scope.Contains(member.Id)) continue;
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
        /// The set of member ids the current viewer may see on Skyttetrappan.
        /// Returns null when unrestricted (site admin). Participants/trainers are scoped to their
        /// own training-group peers; club/regional admins + skjutledare to their clubs.
        /// </summary>
        private async Task<HashSet<int>?> GetVisibleMemberScopeAsync(IMember? currentMemberData, List<IMember> allMembers)
        {
            if (await _authorizationService.IsCurrentUserAdminAsync()) return null;

            var clubScope = (await _authorizationService.GetManagedClubIds())
                .Concat(await _authorizationService.GetSkjutledareClubIds())
                .ToHashSet();
            if (clubScope.Count > 0)
            {
                var set = new HashSet<int>();
                foreach (var m in allMembers)
                {
                    var pc = m.GetValue("primaryClubId")?.ToString();
                    if (int.TryParse(pc, out var pcid) && clubScope.Contains(pcid)) { set.Add(m.Id); continue; }
                    var extra = m.GetValue("memberClubIds")?.ToString() ?? "";
                    if (extra.Split(',').Select(s => s.Trim()).Any(s => int.TryParse(s, out var cid) && clubScope.Contains(cid)))
                        set.Add(m.Id);
                }
                return set;
            }

            if (currentMemberData == null) return new HashSet<int>();
            return _trainingGroupService.GetGroupPeerMemberIds(currentMemberData.Id).ToHashSet();
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
                var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();

                return Json(new {
                    success = true,
                    data = new {
                        isAdmin = isSiteAdmin || managedClubIds.Any(),
                        isSiteAdmin = isSiteAdmin,
                        managedClubIds = managedClubIds,
                        isSkjutledare = skjutledareClubIds.Any(),
                        skjutledareClubIds = skjutledareClubIds
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