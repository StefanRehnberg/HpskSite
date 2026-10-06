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
using HpskSite.Services.Mail;
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
        private readonly ReplyContactResolver _replyContacts;
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
            ReplyContactResolver replyContacts,
            ILogger<TrainingGroupController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _trainingGroupService = trainingGroupService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _emailService = emailService;
            _replyContacts = replyContacts;
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

        /// <summary>
        /// Klubbens folk för gruppens klubb: sajtadmin, klubbadmin eller skjutledare. En tränare som
        /// bara är instruktör i gruppen får hantera gruppens DELTAGARE (2026-10-05) och LÄGGA TILL andra
        /// instruktörer (2026-10-06), men inte ta bort en instruktör eller byta roll — det är klubbens beslut.
        /// </summary>
        private async Task<bool> IsClubStaffForGroupAsync(int trainingGroupId)
        {
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;
            var clubId = _trainingGroupService.GetTrainingGroupClubId(trainingGroupId);
            return clubId > 0 && (await _authorizationService.IsClubAdminForClub(clubId)
                                  || await _authorizationService.IsSkjutledareForClub(clubId));
        }

        private string? TrainerRefusal(int trainingGroupId, int memberId)
        {
            var g = _trainingGroupService.GetTrainingGroup(trainingGroupId);
            var m = g?.Members.FirstOrDefault(x => x.MemberId == memberId);
            return m != null && m.Role == "Trainer"
                ? "Bara klubbadmin eller skjutledare kan ändra kursens tränare." : null;
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddTrainingGroupMember(int trainingGroupId, int memberId, string role = "Member", bool sendEmail = false)
        {
            try
            {
                // ⚠️ Gruppen prövas först: annars blev ett ogiltigt id ett rått SQL-fel (främmande nyckel)
                // i en dialogruta. Sajtadmin släpps igenom av CanManage även för en grupp som inte finns.
                if (_trainingGroupService.GetTrainingGroup(trainingGroupId) == null)
                    return Json(new { success = false, message = "Träningsgruppen finns inte." });

                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Du har inte behörighet till den här gruppen." });

                if (role != "Member" && role != "Trainer")
                    return Json(new { success = false, message = "Okänd roll." });
                // Kursens instruktör får lägga till ANDRA instruktörer (Stefan 2026-10-06: "instruktör måste
                // få lägga till andra som instruktörer i sina kurser" — t.ex. en vikarie). CanManage ovan
                // har redan släppt in klubbadmin, skjutledare och gruppens instruktörer. Att TA BORT en
                // instruktör eller byta roll är fortfarande klubbens beslut (se RemoveTrainingGroupMember).
                // ⚠️ Tjänsten skriver om ROLLEN på en befintlig rad — utan spärren nedan kunde en instruktör
                // göra en deltagare till instruktör (eller tvärtom) genom att "lägga till" hen igen.
                var existing = _trainingGroupService.GetTrainingGroup(trainingGroupId)?.Members
                    .FirstOrDefault(x => x.MemberId == memberId);
                if (existing != null && existing.Role != role && !await IsClubStaffForGroupAsync(trainingGroupId))
                    return Json(new { success = false, message = "Personen är redan med i kursen. Att byta roll görs av klubbadmin eller skjutledare." });

                var member = _memberService.GetById(memberId);
                if (member == null)
                    return Json(new { success = false, message = "Medlemmen finns inte." });

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
                                    otherTrainerNames, startDate, clubName,
                                    _replyContacts.ForClub(group?.ClubId ?? 0));
                            }
                            else
                            {
                                var trainerNames = string.Join(", ", group?.Members
                                    .Where(m => m.Role == "Trainer")
                                    .Select(m => m.MemberName) ?? Enumerable.Empty<string>());

                                _ = _emailService.SendTrainingGroupMemberAddedAsync(
                                    memberEmail, member.Name ?? "", group?.Name ?? "",
                                    trainerNames, startDate, clubName,
                                    _replyContacts.ForClub(group?.ClubId ?? 0));
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
                _logger.LogError(ex, "Kunde inte lägga till medlem {MemberId} i träningsgrupp {GroupId}", memberId, trainingGroupId);
                return Json(new { success = false, message = "Medlemmen kunde inte läggas till. Försök igen, eller kontakta klubbens admin." });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveTrainingGroupMember(int trainingGroupId, int memberId)
        {
            try
            {
                if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId))
                    return Json(new { success = false, message = "Du har inte behörighet till den här gruppen." });
                if (!await IsClubStaffForGroupAsync(trainingGroupId) && TrainerRefusal(trainingGroupId, memberId) is { } refusal)
                    return Json(new { success = false, message = refusal });

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
                // Roller är klubbens beslut — en tränare får inte göra deltagare till tränare.
                if (!await IsClubStaffForGroupAsync(trainingGroupId))
                    return Json(new { success = false, message = "Bara klubbadmin eller skjutledare kan ändra roller i gruppen." });

                if (role != "Member" && role != "Trainer")
                    return Json(new { success = false, message = "Okänd roll." });

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
        public async Task<IActionResult> SearchMembers(string query, int? clubId = null, int? trainingGroupId = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Not logged in" });

                // Admin/skjutledare någonstans — eller tränare i den angivna gruppen. ⚠️ Kommentaren sa
                // förut "or trainer" men koden nekade tränare; därför kunde ingen kursledare lägga in
                // sina deltagare. Med trainingGroupId låses sökningen till gruppens klubb.
                if (trainingGroupId is > 0)
                {
                    if (!await _trainingGroupService.CanManageTrainingGroup(trainingGroupId.Value))
                        return Json(new { success = false, message = "Du har inte behörighet till den här gruppen." });
                    clubId = _trainingGroupService.GetTrainingGroupClubId(trainingGroupId.Value);
                }
                else
                {
                    bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                    var managedClubIds = await _authorizationService.GetManagedClubIds();
                    var skjutledareClubIds = await _authorizationService.GetSkjutledareClubIds();
                    if (!isSiteAdmin && !managedClubIds.Any() && !skjutledareClubIds.Any())
                        return Json(new { success = false, message = "Access denied" });
                }

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
        /// <param name="memberIds">Kommaseparerade mottagare (Min kurs → Deltagare → Mejla). Bara gruppens
        /// DELTAGARE godtas. Utelämnat = hela gruppen som förut (äldre anropare).</param>
        /// <param name="includeGuardians">"1" = även målsmännens e-post (sträng: "1" binder inte till bool).</param>
        public async Task<IActionResult> SendGroupMessage(int trainingGroupId, string subject, string message,
            string? memberIds = null, string? includeGuardians = null, string? includeCourseLink = null)
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
                var senderName = currentMember?.Name ?? "Instruktören";

                var group = _trainingGroupService.GetTrainingGroup(trainingGroupId);
                if (group == null)
                    return Json(new { success = false, message = "Träningsgrupp hittades inte" });

                var currentMemberData = _memberService.GetByEmail(currentMember?.Email ?? "");
                int senderId = currentMemberData?.Id ?? 0;

                // Urvalet: bara gruppens deltagare när mottagare anges — ett handpostat id utanför
                // gruppen (eller en instruktör) mejlas aldrig.
                var recipients = group.Members.Where(gm => gm.MemberId != senderId).ToList();
                if (!string.IsNullOrWhiteSpace(memberIds))
                {
                    var wanted = memberIds.Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => int.TryParse(s.Trim(), out var v) ? v : 0).Where(v => v > 0).ToHashSet();
                    recipients = recipients.Where(gm => wanted.Contains(gm.MemberId) && gm.Role != "Trainer").ToList();
                    if (recipients.Count == 0)
                        return Json(new { success = false, message = "Välj minst en deltagare i kursen." });
                }
                var withGuardians = includeGuardians is "1" or "true" or "on";
                // En länk till kursen i stället för ett avskrivet schema (Stefan 2026-10-06): där står
                // alltid de aktuella tillfällena och vilka som är obligatoriska.
                var courseUrl = includeCourseLink is "1" or "true" or "on"
                    ? $"{Request.Scheme}://{Request.Host}/min-kurs?g={trainingGroupId}" : null;

                int sentCount = 0;
                var noEmail = new List<string>();
                var failed = new List<string>();
                // Avsändaren är INSTRUKTÖREN: "Anna Andersson via Pistol.nu", svaret till hen
                // (Stefan 2026-10-06). Ett gruppmeddelande är ett SAMTAL, inte ett ärende.
                var reply = _replyContacts.ForPersonalMessage(senderId, group.ClubId);

                // Pass 1: mottagarna — deltagaren själv och målsmännen när det valts, en gång per adress.
                var targets = new List<(string Email, string Name, string? GuardianOf, int MemberId)>();
                foreach (var gm in recipients)
                {
                    var member = _memberService.GetById(gm.MemberId);
                    if (member == null) continue;
                    var before = targets.Count;
                    if (!string.IsNullOrWhiteSpace(member.Email)
                        && !targets.Any(t => t.Email.Equals(member.Email, StringComparison.OrdinalIgnoreCase)))
                        targets.Add((member.Email, member.Name ?? "", null, member.Id));
                    if (withGuardians)
                        foreach (var g in new[] { "guardian1", "guardian2" })
                        {
                            var ge = member.GetValue(g + "Email")?.ToString()?.Trim();
                            if (!string.IsNullOrWhiteSpace(ge) && !targets.Any(t => t.Email.Equals(ge, StringComparison.OrdinalIgnoreCase)))
                                targets.Add((ge, member.GetValue(g + "Name")?.ToString() ?? "", member.Name, member.Id));
                        }
                    if (targets.Count == before && string.IsNullOrWhiteSpace(member.Email)) noEmail.Add(member.Name ?? $"Medlem {member.Id}");
                }

                // ⚠️ Gränsen prövas FÖRE första mejlet — ett halvt utskick går inte att ta tillbaka.
                if (targets.Count > HpskSite.Services.Mail.MailLimits.MaxRecipientsPerSend)
                    return Json(new { success = false, message = $"För många mottagare ({targets.Count}). Ett utskick via pistol.nu får ha högst {HpskSite.Services.Mail.MailLimits.MaxRecipientsPerSend}." });

                // Pass 2: utskicket.
                foreach (var t in targets)
                {
                    try
                    {
                        if (await _emailService.SendTrainingGroupMessageAsync(
                                t.Email, t.Name, senderName, group.Name, subject, message, reply, t.GuardianOf,
                                t.GuardianOf == null ? courseUrl : null))   // målsman har sällan konto — ingen länk dit
                            sentCount++;
                        else failed.Add(t.Name.Length > 0 ? t.Name : t.Email);
                    }
                    catch (Exception emailEx)
                    {
                        _logger.LogWarning(emailEx, "Failed to send group message to member {MemberId}", t.MemberId);
                        failed.Add(t.Name.Length > 0 ? t.Name : t.Email);
                    }
                }

                // ⚠️ Räknar bara mejl som FAKTISKT gick iväg, och namnger dem som inte nåddes.
                var msg = sentCount == 0 && (failed.Count > 0 || noEmail.Count > 0)
                    ? "Inget mejl kunde skickas."
                    : $"Meddelandet skickades till {sentCount} mottagare.";
                if (noEmail.Count > 0) msg += " Saknar e-post: " + string.Join(", ", noEmail) + ".";
                if (failed.Count > 0) msg += " Kunde inte skickas till: " + string.Join(", ", failed) + ".";
                return Json(new { success = sentCount > 0, sent = sentCount, noEmail, failed, message = msg });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}
