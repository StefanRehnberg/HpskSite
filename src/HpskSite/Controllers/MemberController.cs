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
using Umbraco.Cms.Core.Models.Membership;
using Microsoft.AspNetCore.Identity;
using System.Security.Claims;
using HpskSite.Models;
using HpskSite.Shared.Models;
using HpskSite.Services;
using HpskSite.CompetitionTypes.Precision.Services;
using System.Text.Json;
using NPoco;
using Microsoft.AspNetCore.Hosting;

namespace HpskSite.Controllers
{
    public class MemberController : SurfaceController
    {
        private readonly IMemberService _memberService;
        private readonly IMemberGroupService _memberGroupService;
        private readonly IMemberManager _memberManager;
        private readonly SignInManager<MemberIdentityUser> _signInManager;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IContentService _contentService;
        private readonly EmailService _emailService;
        private readonly ClubService _clubService;
        private readonly AppCaches _appCaches; // PHASE 4: Added for caching
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly IShooterStatisticsService _statisticsService;
        private readonly IHandicapCalculator _handicapCalculator;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly UnifiedResultsService _unifiedResultsService;
        private const string ClubMemberTypeAlias = "hpskClub";

        public MemberController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberService memberService,
            IMemberGroupService memberGroupService,
            IMemberManager memberManager,
            SignInManager<MemberIdentityUser> signInManager,
            IContentService contentService,
            EmailService emailService,
            ClubService clubService,
            IWebHostEnvironment webHostEnvironment,
            IShooterStatisticsService statisticsService,
            IHandicapCalculator handicapCalculator,
            AdminAuthorizationService authorizationService,
            UnifiedResultsService unifiedResultsService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberService = memberService;
            _memberGroupService = memberGroupService;
            _memberManager = memberManager;
            _signInManager = signInManager;
            _databaseFactory = databaseFactory;
            _contentService = contentService;
            _emailService = emailService;
            _clubService = clubService;
            _appCaches = appCaches; // PHASE 4: Store for caching
            _webHostEnvironment = webHostEnvironment;
            _statisticsService = statisticsService;
            _handicapCalculator = handicapCalculator;
            _authorizationService = authorizationService;
            _unifiedResultsService = unifiedResultsService;
        }

        [HttpGet]
        public async Task<IActionResult> GetCurrentMemberClubInfo()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get primary club (clubs are content nodes, not members)
                string primaryClubName = "Ingen klubb tilldelad";
                int? primaryClubId = null;
                string primaryClubUrl = "";
                var primaryClubIdStr = member.GetValue("primaryClubId")?.ToString();
                if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out int parsedPrimaryClubId))
                {
                    primaryClubId = parsedPrimaryClubId;
                    // Fetch club from content nodes (Document Type: club)
                    var primaryClubNode = UmbracoContext?.Content?.GetById(parsedPrimaryClubId);
                    if (primaryClubNode != null && primaryClubNode.ContentType.Alias == "club")
                    {
                        primaryClubName = primaryClubNode.Value<string>("clubName") ?? primaryClubNode.Name ?? "Ingen klubb tilldelad";
                        primaryClubUrl = primaryClubNode.Url() ?? "";
                    }
                }

                // Get additional clubs (also content nodes)
                var additionalClubs = new List<object>();
                var memberClubIdsStr = member.GetValue("memberClubIds")?.ToString();
                if (!string.IsNullOrEmpty(memberClubIdsStr))
                {
                    var clubIds = memberClubIdsStr.Split(',', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var clubIdStr in clubIds)
                    {
                        if (int.TryParse(clubIdStr.Trim(), out int clubId))
                        {
                            // Fetch club from content nodes
                            var clubNode = UmbracoContext?.Content?.GetById(clubId);
                            if (clubNode != null && clubNode.ContentType.Alias == "club")
                            {
                                var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";
                                additionalClubs.Add(new { id = clubNode.Id, name = clubName });
                            }
                        }
                    }
                }

                // Get member groups
                var memberGroups = _memberService.GetAllRoles(member.Id);

                var result = new
                {
                    primaryClubName = primaryClubName,
                    primaryClubId = primaryClubId,
                    primaryClubUrl = primaryClubUrl,
                    additionalClubs = additionalClubs,
                    memberGroups = memberGroups?.ToList() ?? new List<string>()
                };

                return Json(new { success = true, data = result });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateProfile(string firstName, string lastName, string email,
            string phoneNumber = null, string shooterIdNumber = null,
            string address = null, string postalCode = null, string city = null, string personNumber = null,
            string currentPassword = null, string newPassword = null,
            string precisionShooterClass = null,
            string milsnabbShooterClass = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Validate required fields
                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) || string.IsNullOrWhiteSpace(email))
                {
                    return Json(new { success = false, message = "Förnamn, efternamn och e-post är obligatoriska" });
                }

                // Update basic information
                member.SetValue("firstName", firstName);
                member.SetValue("lastName", lastName);
                member.SetValue("phoneNumber", phoneNumber ?? "");
                member.SetValue("shooterIdNumber", shooterIdNumber ?? "");
                member.SetValue("address", address ?? "");
                member.SetValue("postalCode", postalCode ?? "");
                member.SetValue("city", city ?? "");
                member.SetValue("personNumber", personNumber ?? "");
                // Only update shooter classes if explicitly provided (prevents clearing on general profile updates)
                if (!string.IsNullOrEmpty(precisionShooterClass))
                {
                    member.SetValue("precisionShooterClass", precisionShooterClass);
                }
                if (!string.IsNullOrEmpty(milsnabbShooterClass) && member.HasProperty("milsnabbShooterClass"))
                {
                    member.SetValue("milsnabbShooterClass", milsnabbShooterClass);
                }
                member.Email = email;
                member.Name = $"{firstName} {lastName}";

                // Handle password change if provided
                if (!string.IsNullOrEmpty(newPassword))
                {
                    if (string.IsNullOrEmpty(currentPassword))
                    {
                        return Json(new { success = false, message = "Nuvarande lösenord krävs för att ändra lösenord" });
                    }

                    // Validate current password using SignInManager
                    var validateResult = await _memberManager.CheckPasswordAsync(currentMember, currentPassword);
                    if (!validateResult)
                    {
                        return Json(new { success = false, message = "Nuvarande lösenord är felaktigt" });
                    }

                    // Change password
                    var changePasswordResult = await _memberManager.ChangePasswordAsync(currentMember, currentPassword, newPassword);
                    if (!changePasswordResult.Succeeded)
                    {
                        var errors = string.Join(", ", changePasswordResult.Errors.Select(e => e.Description));
                        return Json(new { success = false, message = "Kunde inte ändra lösenord: " + errors });
                    }
                }

                // Save member
                _memberService.Save(member);

                return Json(new {
                    success = true,
                    message = "Profil uppdaterad framgångsrikt",
                    data = new { memberName = member.Name }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(string currentPassword, string newPassword)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                if (string.IsNullOrEmpty(currentPassword) || string.IsNullOrEmpty(newPassword))
                {
                    return Json(new { success = false, message = "Både nuvarande och nytt lösenord krävs" });
                }

                if (newPassword.Length < 6)
                {
                    return Json(new { success = false, message = "Nytt lösenord måste vara minst 6 tecken långt" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Validate current password
                var validateResult = await _memberManager.CheckPasswordAsync(currentMember, currentPassword);
                if (!validateResult)
                {
                    return Json(new { success = false, message = "Nuvarande lösenord är felaktigt" });
                }

                // Change password
                var changePasswordResult = await _memberManager.ChangePasswordAsync(currentMember, currentPassword, newPassword);
                if (!changePasswordResult.Succeeded)
                {
                    var errors = string.Join(", ", changePasswordResult.Errors.Select(e => e.Description));
                    return Json(new { success = false, message = "Kunde inte ändra lösenord: " + errors });
                }

                return Json(new { success = true, message = "Lösenord ändrat framgångsrikt" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get current member's roles for authorization checks
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCurrentMemberRoles()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get member roles
                var roles = _memberService.GetAllRoles(member.Id);

                return Json(new { success = true, roles = roles });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterMember(string firstName, string lastName, string email,
            string password, string confirmPassword, int? primaryClubId = null, string? website = null)
        {
            try
            {
                // Honeypot check — bots fill hidden fields
                if (!string.IsNullOrEmpty(website))
                {
                    return Json(new { success = true, message = "Registrering lyckades!" });
                }

                // Validate required fields
                if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName) ||
                    string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
                {
                    return Json(new { success = false, message = "Förnamn, efternamn, e-post och lösenord är obligatoriska" });
                }

                // Require a valid club
                if (!primaryClubId.HasValue || primaryClubId.Value <= 0)
                {
                    return Json(new { success = false, message = "Du måste välja en klubb" });
                }

                var clubName = _clubService.GetClubNameById(primaryClubId.Value);
                if (clubName == null)
                {
                    return Json(new { success = false, message = "Den valda klubben är ogiltig" });
                }

                // Validate email format
                var emailRegex = new System.Text.RegularExpressions.Regex(
                    @"^[^\s@]+@[^\s@]+\.[^\s@]{2,}$");
                if (!emailRegex.IsMatch(email))
                {
                    return Json(new { success = false, message = "Ange en giltig e-postadress" });
                }

                if (password != confirmPassword)
                {
                    return Json(new { success = false, message = "Lösenorden matchar inte" });
                }

                if (password.Length < 6)
                {
                    return Json(new { success = false, message = "Lösenordet måste vara minst 6 tecken långt" });
                }

                // Check if email already exists
                var existingMember = _memberService.GetByEmail(email);
                if (existingMember != null)
                {
                    return Json(new { success = false, message = "En användare med denna e-postadress finns redan" });
                }

                // Create full name from first and last name
                var fullName = $"{firstName} {lastName}";

                // Create the member using member manager (this handles both member and identity creation)
                var identityUser = MemberIdentityUser.CreateNew(email, email, "hpskMember", true, fullName);
                var createResult = await _memberManager.CreateAsync(identityUser, password);

                if (!createResult.Succeeded)
                {
                    var errors = string.Join(", ", createResult.Errors.Select(e => e.Description));
                    return Json(new { success = false, message = "Kunde inte skapa användarkonto: " + errors });
                }

                // Get the created member to set custom properties
                var member = _memberService.GetByEmail(email);
                if (member != null)
                {
                    // Set custom properties
                    member.SetValue("firstName", firstName);
                    member.SetValue("lastName", lastName);

                    // Set club ID directly (no lookup needed)
                    if (primaryClubId.HasValue)
                    {
                        member.SetValue("primaryClubId", primaryClubId.Value);
                    }

                    // Assign to PendingApproval group (will move to Users when approved)
                    _memberService.AssignRole(member.Id, "PendingApproval");

                    // Member needs approval - do not auto-approve
                    member.IsApproved = false;
                    _memberService.Save(member);

                    // Use club name already validated above
                    string clubNameForEmail = clubName ?? "din valda klubb";

                    // Send email notifications
                    try
                    {
                        // Send confirmation email to user
                        await _emailService.SendRegistrationConfirmationToUserAsync(email, fullName, clubNameForEmail);

                        // Look up club and regional admins if a club was selected
                        List<string>? notifiedClubAdminNames = null;
                        List<string>? notifiedRegionalAdminNames = null;
                        string? regionCode = null;

                        if (primaryClubId.HasValue)
                        {
                            var (clubAdmins, regionalAdmins, region) = GetAdminContactsForClub(primaryClubId.Value);
                            regionCode = !string.IsNullOrEmpty(region) ? region : null;

                            // Notify club admins
                            if (clubAdmins.Any())
                            {
                                notifiedClubAdminNames = clubAdmins.Select(a => a.Name).ToList();
                                foreach (var (adminName, adminEmail) in clubAdmins)
                                {
                                    await _emailService.SendRegistrationNotificationToClubAdminAsync(
                                        adminEmail, adminName, fullName, email, clubNameForEmail,
                                        member.Id);
                                }
                            }

                            // Notify regional admins
                            if (regionalAdmins.Any())
                            {
                                notifiedRegionalAdminNames = regionalAdmins.Select(a => a.Name).ToList();
                                var hasClubAdmins = clubAdmins.Any();
                                var clubAdminNameList = clubAdmins.Select(a => a.Name).ToList();
                                foreach (var (adminName, adminEmail) in regionalAdmins)
                                {
                                    await _emailService.SendRegistrationNotificationToRegionalAdminAsync(
                                        adminEmail, adminName, fullName, email, clubNameForEmail,
                                        hasClubAdmins, clubAdminNameList, member.Id);
                                }
                            }
                        }

                        // Send notification to global admin (with info about who else was notified)
                        await _emailService.SendRegistrationNotificationToAdminAsync(
                            fullName, email,
                            primaryClubId.HasValue ? clubNameForEmail : "Ingen klubb vald",
                            notifiedClubAdminNames, notifiedRegionalAdminNames, regionCode,
                            member.Id);
                    }
                    catch (Exception emailEx)
                    {
                        // Log but don't fail registration if email fails
                        System.Diagnostics.Debug.WriteLine($"Email notification failed: {emailEx.Message}");
                    }
                }

                return Json(new { success = true, message = "Registrering lyckades! Din ansökan väntar nu på godkännande av en klubbadministratör. Du får ett bekräftelsemail inom kort." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod vid registrering: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> RequestMissingClub([FromBody] MissingClubRequest request)
        {
            try
            {
                // Validate required fields
                if (string.IsNullOrWhiteSpace(request.ClubName) || string.IsNullOrWhiteSpace(request.RequestorEmail))
                {
                    return Json(new { success = false, message = "Klubbnamn och e-postadress är obligatoriska" });
                }

                // Validate email format
                if (!request.RequestorEmail.Contains("@") || !request.RequestorEmail.Contains("."))
                {
                    return Json(new { success = false, message = "Ogiltig e-postadress" });
                }

                // Send email notification to admin
                await _emailService.SendMissingClubRequestAsync(
                    request.ClubName,
                    request.ClubLocation ?? "",
                    request.ContactPerson ?? "",
                    request.RequestorEmail,
                    request.AdditionalNotes ?? ""
                );

                return Json(new { success = true, message = "Din begäran har skickats! En administratör kommer att kontakta klubben och meddela dig." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Get current member's roles including club admin status
        /// Enhanced to include club admin information
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCurrentMemberRolesWithClubAdmin()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get member roles
                var roles = _memberService.GetAllRoles(member.Id);

                // Determine authorization levels
                bool isAdmin = roles.Contains("Administrators");
                bool isModerator = roles.Contains("Moderators");
                bool canApprove = isAdmin || isModerator;

                // Find club admin roles
                var clubAdminRoles = roles.Where(r => r.StartsWith("ClubAdmin_")).ToList();
                var adminClubIds = new List<int>();

                foreach (var clubAdminRole in clubAdminRoles)
                {
                    if (int.TryParse(clubAdminRole.Substring(10), out int clubId))
                    {
                        adminClubIds.Add(clubId);
                    }
                }

                return Json(new {
                    success = true,
                    roles = roles,
                    isAdmin = isAdmin,
                    isModerator = isModerator,
                    canApprove = canApprove,
                    isClubAdmin = adminClubIds.Any(),
                    adminClubIds = adminClubIds
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Check if current member can access specific club data
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CanAccessClub(int clubId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, canAccess = false, message = "Not logged in" });
                }

                var member = _memberService.GetByEmail(currentMember.Email);
                if (member == null)
                {
                    return Json(new { success = false, canAccess = false, message = "Member not found" });
                }

                var roles = _memberService.GetAllRoles(member.Id);

                // Site admins can access all clubs
                if (roles.Contains("Administrators"))
                {
                    return Json(new { success = true, canAccess = true, accessLevel = "Administrator" });
                }

                // Check club admin access
                if (roles.Contains($"ClubAdmin_{clubId}"))
                {
                    return Json(new { success = true, canAccess = true, accessLevel = "ClubAdmin" });
                }

                return Json(new { success = true, canAccess = false, accessLevel = "None" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, canAccess = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get combined member results from competitions and training scores
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberResults(string competitionType = "Precision", int? year = null, string weaponClass = "Alla")
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

                var memberId = member.Id;
                var filterYear = year ?? DateTime.Now.Year;
                var results = new List<object>();

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Query 1: Get competition results from the correct table based on type
                    var resultTable = competitionType == "Milsnabb" ? "MilsnabbResultEntry" : "PrecisionResultEntry";
                    var competitionResults = db.Fetch<dynamic>($@"
                        SELECT
                            CompetitionId,
                            ShootingClass,
                            SeriesNumber,
                            Shots
                        FROM {resultTable}
                        WHERE MemberId = @0
                        ORDER BY CompetitionId, SeriesNumber",
                        memberId);

                    // Group by CompetitionId and ShootingClass
                    var groupedCompetitions = competitionResults
                        .GroupBy(r => new { CompetitionId = (int)r.CompetitionId, ShootingClass = (string)r.ShootingClass })
                        .ToList();

                    foreach (var group in groupedCompetitions)
                    {
                        // Get competition details from Umbraco content
                        var competition = _contentService.GetById(group.Key.CompetitionId);
                        if (competition == null) continue;

                        var competitionDate = competition.GetValue<DateTime>("competitionDate");
                        if (competitionDate.Year != filterYear) continue;

                        var competitionName = competition.Name ?? "Okänd tävling";

                        // Map ShootingClass to WeaponClass
                        var shootingClassObj = ShootingClasses.GetById(group.Key.ShootingClass);
                        var weaponClassStr = shootingClassObj?.Weapon.ToString()
                            ?? (!string.IsNullOrEmpty(group.Key.ShootingClass) && group.Key.ShootingClass.Length > 0
                                ? group.Key.ShootingClass.Substring(0, 1).ToUpper()
                                : "?");

                        // Filter by weapon class
                        if (weaponClass != "Alla" && weaponClassStr != weaponClass) continue;

                        // Calculate totals
                        int totalScore = 0;
                        int seriesCount = 0;

                        foreach (var entry in group)
                        {
                            var shotsJson = (string)entry.Shots;
                            if (!string.IsNullOrEmpty(shotsJson))
                            {
                                var shots = JsonSerializer.Deserialize<List<string>>(shotsJson);
                                if (shots != null)
                                {
                                    totalScore += shots.Sum(s => s == "X" ? 10 : int.Parse(s));
                                    seriesCount++;
                                }
                            }
                        }

                        if (seriesCount == 0) continue;

                        var averageScore = Math.Round((double)totalScore / seriesCount, 1);

                        results.Add(new
                        {
                            date = competitionDate,
                            name = competitionName,
                            type = "Competition",
                            averageScore = averageScore,
                            weaponClass = weaponClassStr,
                            totalScore = totalScore,
                            seriesCount = seriesCount,
                            competitionId = group.Key.CompetitionId,
                            shootingClass = group.Key.ShootingClass
                        });
                    }

                    // Query 2: Get training scores from TrainingScores, filtered by discipline
                    // For Precision: include entries with Discipline = 'Precision' or NULL (backward compat)
                    // For other types: include only entries with matching Discipline
                    var disciplineFilter = competitionType == "Precision"
                        ? "AND (Discipline = 'Precision' OR Discipline IS NULL)"
                        : $"AND Discipline = @2";
                    var trainingScoreParams = competitionType == "Precision"
                        ? new object[] { memberId, filterYear }
                        : new object[] { memberId, filterYear, competitionType };
                    var trainingScores = db.Fetch<dynamic>($@"
                        SELECT
                            Id,
                            TrainingDate,
                            WeaponClass,
                            SeriesScores,
                            TotalScore,
                            Notes,
                            IsCompetition,
                            TrainingMatchId
                        FROM TrainingScores
                        WHERE MemberId = @0
                        AND YEAR(TrainingDate) = @1
                        {disciplineFilter}
                        ORDER BY TrainingDate DESC",
                        trainingScoreParams);

                    foreach (var score in trainingScores)
                    {
                        var weaponClassStr = (string)score.WeaponClass ?? "Unknown";

                        // Filter by weapon class
                        if (weaponClass != "Alla" && weaponClassStr != weaponClass) continue;

                        var seriesScoresJson = (string)score.SeriesScores;
                        var seriesCount = 0;

                        if (!string.IsNullOrEmpty(seriesScoresJson))
                        {
                            try
                            {
                                var series = JsonSerializer.Deserialize<List<TrainingSeries>>(seriesScoresJson);
                                if (series != null && series.Count > 0)
                                {
                                    // For TotalOnly entries, use the seriesCount from the series object
                                    if (series.Count == 1 && series[0].EntryMethod == "TotalOnly" && series[0].SeriesCount.HasValue)
                                    {
                                        seriesCount = series[0].SeriesCount.Value;
                                    }
                                    else
                                    {
                                        seriesCount = series.Count;
                                    }
                                }
                            }
                            catch { }
                        }

                        var totalScore = (int)score.TotalScore;
                        var averageScore = seriesCount > 0 ? Math.Round((double)totalScore / seriesCount, 1) : 0;

                        var trainingName = "Träningspass";
                        var notes = (string)score.Notes;
                        if (!string.IsNullOrEmpty(notes) && notes.Length < 50)
                        {
                            trainingName = notes;
                        }

                        // Determine type based on IsCompetition flag
                        var isCompetition = (bool?)score.IsCompetition ?? false;

                        results.Add(new
                        {
                            date = (DateTime)score.TrainingDate,
                            name = trainingName,
                            type = isCompetition ? "Competition" : "Training",
                            averageScore = averageScore,
                            weaponClass = weaponClassStr,
                            totalScore = totalScore,
                            seriesCount = seriesCount,
                            trainingScoreId = (int)score.Id,
                            trainingMatchId = (int?)score.TrainingMatchId
                        });
                    }
                }

                // Sort by date descending
                results = results.OrderByDescending(r => ((dynamic)r).date).ToList();

                return Json(new { success = true, results = results });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Request a password reset - generates token and sends email
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestPasswordReset([FromBody] PasswordResetRequest request)
        {
            try
            {
                // Validate email
                if (string.IsNullOrEmpty(request.Email))
                {
                    return Json(new { success = false, message = "E-postadress krävs." });
                }

                // Find member by email
                var member = _memberService.GetByEmail(request.Email);

                // SECURITY: Always return success even if member doesn't exist
                // This prevents email enumeration attacks
                if (member == null)
                {
                    // Return success but don't send email
                    return Json(new {
                        success = true,
                        message = "Om e-postadressen finns i systemet har en återställningslänk skickats."
                    });
                }

                // Check if member is approved
                if (!member.IsApproved)
                {
                    return Json(new {
                        success = false,
                        message = "Ditt konto har inte godkänts ännu. Kontakta en administratör."
                    });
                }

                // Rate limiting - check if there's an active, non-expired token
                var existingTokenExpiryStr = member.GetValue<string>("passwordResetTokenExpiry");
                var existingToken = member.GetValue<string>("passwordResetToken");

                if (!string.IsNullOrEmpty(existingToken) &&
                    !string.IsNullOrEmpty(existingTokenExpiryStr) &&
                    DateTime.TryParse(existingTokenExpiryStr, out DateTime existingExpiry))
                {
                    // If there's a valid, non-expired token, check if it was created within last 5 minutes (use UTC)
                    if (existingExpiry > DateTime.UtcNow)
                    {
                        // Token hasn't expired yet - calculate when it was created (1 hour before expiry)
                        var tokenCreatedAt = existingExpiry.AddHours(-1);
                        if (tokenCreatedAt > DateTime.UtcNow.AddMinutes(-5))
                        {
                            return Json(new {
                                success = false,
                                message = "Du har redan begärt en återställningslänk nyligen. Vänta 5 minuter innan du försöker igen."
                            });
                        }
                    }
                }

                // Get MemberIdentityUser for token generation
                var identityUser = await _memberManager.FindByEmailAsync(request.Email);
                if (identityUser == null)
                {
                    return Json(new {
                        success = true,
                        message = "Om e-postadressen finns i systemet har en återställningslänk skickats."
                    });
                }

                // Generate password reset token using Umbraco's built-in method
                var resetToken = await _memberManager.GeneratePasswordResetTokenAsync(identityUser);

                // Store token and expiry in member properties (use UTC for consistency)
                var tokenExpiry = DateTime.UtcNow.AddHours(1); // Token valid for 1 hour
                member.SetValue("passwordResetToken", resetToken);
                member.SetValue("passwordResetTokenExpiry", tokenExpiry.ToString("o")); // ISO 8601 format with UTC
                _memberService.Save(member);

                // Get member name
                var firstName = member.GetValue<string>("firstName") ?? "";
                var lastName = member.GetValue<string>("lastName") ?? "";
                var memberName = !string.IsNullOrEmpty(firstName) ? firstName : member.Name;

                // Send password reset email
                await _emailService.SendPasswordResetEmailAsync(
                    request.Email,
                    memberName,
                    resetToken
                );

                return Json(new {
                    success = true,
                    message = "Om e-postadressen finns i systemet har en återställningslänk skickats. Länken är giltig i 1 timme."
                });
            }
            catch (Exception ex)
            {
                // Log error but don't reveal details to user
                Console.WriteLine($"Password reset request error: {ex.Message}");
                return Json(new {
                    success = false,
                    message = "Ett fel uppstod. Försök igen senare."
                });
            }
        }

        /// <summary>
        /// Reset password using token - validates token and updates password
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ResetPasswordWithToken([FromBody] PasswordResetConfirm request)
        {
            try
            {
                // Validate inputs
                if (string.IsNullOrEmpty(request.Email) ||
                    string.IsNullOrEmpty(request.Token) ||
                    string.IsNullOrEmpty(request.NewPassword))
                {
                    return Json(new { success = false, message = "Alla fält krävs." });
                }

                // Validate password length
                if (request.NewPassword.Length < 6)
                {
                    return Json(new { success = false, message = "Lösenordet måste vara minst 6 tecken långt." });
                }

                // Find member by email
                var member = _memberService.GetByEmail(request.Email);
                if (member == null)
                {
                    return Json(new { success = false, message = "Ogiltig återställningslänk." });
                }

                // Get stored token and expiry
                var storedToken = member.GetValue<string>("passwordResetToken");
                var tokenExpiryStr = member.GetValue<string>("passwordResetTokenExpiry");

                // Validate token exists
                if (string.IsNullOrEmpty(storedToken) || string.IsNullOrEmpty(tokenExpiryStr))
                {
                    return Json(new { success = false, message = "Ingen giltig återställningslänk hittades." });
                }

                // Validate token hasn't expired (compare with UTC)
                if (!DateTime.TryParse(tokenExpiryStr, out DateTime tokenExpiry) || tokenExpiry < DateTime.UtcNow)
                {
                    // Clear expired token
                    member.SetValue("passwordResetToken", string.Empty);
                    member.SetValue("passwordResetTokenExpiry", string.Empty);
                    _memberService.Save(member);

                    return Json(new { success = false, message = "Återställningslänken har gått ut. Begär en ny länk." });
                }

                // Validate token matches (tokens should match exactly)
                if (storedToken != request.Token)
                {
                    return Json(new { success = false, message = "Ogiltig återställningslänk." });
                }

                // Get MemberIdentityUser
                var identityUser = await _memberManager.FindByEmailAsync(request.Email);
                if (identityUser == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte." });
                }

                // Reset password using Umbraco's built-in method
                var result = await _memberManager.ResetPasswordAsync(identityUser, request.Token, request.NewPassword);

                if (!result.Succeeded)
                {
                    var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                    return Json(new { success = false, message = $"Kunde inte återställa lösenordet: {errors}" });
                }

                // IMPORTANT: Explicitly unlock the user after successful password reset
                // ResetPasswordAsync does NOT automatically reset the lockout counter
                if (await _memberManager.IsLockedOutAsync(identityUser))
                {
                    await _memberManager.SetLockoutEndDateAsync(identityUser, null);
                }
                await _memberManager.ResetAccessFailedCountAsync(identityUser);

                // Clear token after successful reset (single-use token)
                member.SetValue("passwordResetToken", string.Empty);
                member.SetValue("passwordResetTokenExpiry", string.Empty);
                _memberService.Save(member);

                // Note: In Umbraco v16, we use the built-in login form for authentication
                // User will need to log in with their new password

                return Json(new {
                    success = true,
                    message = "Ditt lösenord har återställts! Du kan nu logga in med ditt nya lösenord.",
                    redirectUrl = "/login-register"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Password reset error: {ex.Message}");
                return Json(new {
                    success = false,
                    message = "Ett fel uppstod vid återställning av lösenord. Försök igen eller begär en ny återställningslänk."
                });
            }
        }

        /// <summary>
        /// Auto-login endpoint for approved members
        /// Uses single-use token sent in approval email
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> AutoLogin(string token, string email)
        {
            Console.WriteLine($"[AutoLogin] Attempting auto-login for email: {email}");

            // Validate parameters
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(email))
            {
                Console.WriteLine("[AutoLogin] Missing token or email parameter");
                TempData["ErrorMessage"] = "Ogiltig inloggningslänk. Token eller e-postadress saknas.";
                return Redirect("/login-register?tab=login");
            }

            try
            {
                // Find member by email
                var member = _memberService.GetByEmail(email);
                if (member == null)
                {
                    Console.WriteLine($"[AutoLogin] Member not found for email: {email}");
                    TempData["ErrorMessage"] = "Kontot kunde inte hittas. Kontakta support om problemet kvarstår.";
                    return Redirect("/login-register?tab=login");
                }

                // Check if member is approved
                if (!member.IsApproved)
                {
                    Console.WriteLine($"[AutoLogin] Member not approved: {email}");
                    TempData["ErrorMessage"] = "Ditt konto är inte godkänt ännu. Vänta på godkännande från klubbadministratör.";
                    return Redirect("/login-register?tab=login");
                }

                // Get stored token and expiry
                var storedToken = member.GetValue<string>("autoLoginToken");
                var tokenExpiryStr = member.GetValue<string>("autoLoginTokenExpiry");

                // Check if token exists
                if (string.IsNullOrEmpty(storedToken))
                {
                    Console.WriteLine($"[AutoLogin] No token found for member: {email}");
                    TempData["ErrorMessage"] = "Inloggningslänken har redan använts eller är ogiltig. Använd vanlig inloggning istället.";
                    return Redirect("/login-register?tab=login");
                }

                // Validate token hasn't expired
                if (!DateTime.TryParse(tokenExpiryStr, out DateTime tokenExpiry) || tokenExpiry < DateTime.UtcNow)
                {
                    Console.WriteLine($"[AutoLogin] Token expired for member: {email}");
                    // Clear expired token
                    member.SetValue("autoLoginToken", string.Empty);
                    member.SetValue("autoLoginTokenExpiry", string.Empty);
                    _memberService.Save(member);

                    TempData["ErrorMessage"] = "Inloggningslänken har gått ut (giltig i 7 dagar). Använd vanlig inloggning istället.";
                    return Redirect("/login-register?tab=login");
                }

                // Validate token matches
                if (storedToken != token)
                {
                    Console.WriteLine($"[AutoLogin] Token mismatch for member: {email}");
                    TempData["ErrorMessage"] = "Ogiltig inloggningslänk. Använd vanlig inloggning istället.";
                    return Redirect("/login-register?tab=login");
                }

                // Token is valid - clear it (single-use)
                Console.WriteLine($"[AutoLogin] Token validated successfully for: {email}. Clearing token.");
                member.SetValue("autoLoginToken", string.Empty);
                member.SetValue("autoLoginTokenExpiry", string.Empty);
                _memberService.Save(member);

                // Sign in the member with persistent cookie (remember me for 90 days)
                var identityUser = await _memberManager.FindByEmailAsync(email);
                if (identityUser == null)
                {
                    Console.WriteLine($"[AutoLogin] Identity user not found for: {email}");
                    TempData["ErrorMessage"] = "Ett tekniskt fel uppstod. Använd vanlig inloggning istället.";
                    return Redirect("/login-register?tab=login");
                }

                // Sign in with persistent cookie using SignInManager
                await _signInManager.SignInAsync(identityUser, isPersistent: true);
                Console.WriteLine($"[AutoLogin] Member signed in successfully: {email}");

                // Set success message
                TempData["SuccessMessage"] = "Välkommen! Du är nu inloggad.";

                // Redirect to user profile
                return Redirect("/user-profile-page/");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[AutoLogin] Error during auto-login for {email}: {ex.Message}");
                Console.WriteLine($"[AutoLogin] Stack trace: {ex.StackTrace}");
                TempData["ErrorMessage"] = "Ett fel uppstod vid automatisk inloggning. Använd vanlig inloggning istället.";
                return Redirect("/login-register?tab=login");
            }
        }

        /// <summary>
        /// One-click member approval from admin notification email.
        /// Uses HMAC-signed stateless token for authorization.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> QuickApprove(int memberId, long ts, string sig)
        {
            Console.WriteLine($"[QuickApprove] Attempting quick approve for memberId: {memberId}");

            // Validate parameters
            if (memberId <= 0 || ts <= 0 || string.IsNullOrEmpty(sig))
            {
                Console.WriteLine("[QuickApprove] Missing or invalid parameters");
                return Content(QuickApproveHtmlPage("Ogiltig l&#228;nk", "L&#228;nken &#228;r ogiltig eller saknar parametrar.", false), "text/html");
            }

            // Validate HMAC signature and expiry
            if (!_emailService.ValidateQuickApproveSignature(memberId, ts, sig))
            {
                Console.WriteLine($"[QuickApprove] Invalid or expired signature for memberId: {memberId}");
                return Content(QuickApproveHtmlPage("Ogiltig eller utg&#229;ngen l&#228;nk", "L&#228;nken &#228;r ogiltig eller har g&#229;tt ut. Logga in p&#229; administrationspanelen f&#246;r att godk&#228;nna medlemmen manuellt.", false), "text/html");
            }

            try
            {
                // Find member
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    Console.WriteLine($"[QuickApprove] Member not found: {memberId}");
                    return Content(QuickApproveHtmlPage("Medlem hittades inte", "Medlemmen kunde inte hittas i systemet.", false), "text/html");
                }

                // Check if already approved
                if (member.IsApproved)
                {
                    var alreadyName = member.Name;
                    if (string.IsNullOrWhiteSpace(alreadyName))
                    {
                        var fn = member.GetValue<string>("firstName") ?? "";
                        var ln = member.GetValue<string>("lastName") ?? "";
                        alreadyName = $"{fn} {ln}".Trim();
                    }
                    Console.WriteLine($"[QuickApprove] Member already approved: {memberId}");
                    return Content(QuickApproveHtmlPage("Redan godk&#228;nd", $"Medlemmen <strong>{System.Net.WebUtility.HtmlEncode(alreadyName)}</strong> &#228;r redan godk&#228;nd.", true), "text/html");
                }

                // Approve the member (same logic as MemberAdminController.ApproveMember)
                member.IsApproved = true;
                _memberService.Save(member);
                Console.WriteLine($"[QuickApprove] Member approved and saved: {memberId}");

                // Assign to Users group and remove from PendingApproval group
                _memberService.AssignRole(member.Id, "Users");
                _memberService.DissociateRole(member.Id, "PendingApproval");
                Console.WriteLine($"[QuickApprove] Member groups updated: {memberId}");

                // Generate auto-login token for approval email
                var autoLoginToken = Guid.NewGuid().ToString("N");
                var tokenExpiry = DateTime.UtcNow.AddDays(7);
                member.SetValue("autoLoginToken", autoLoginToken);
                member.SetValue("autoLoginTokenExpiry", tokenExpiry.ToString("o"));
                _memberService.Save(member);

                // Send approval notification email to the member
                var memberName = member.Name;
                if (string.IsNullOrWhiteSpace(memberName))
                {
                    var firstName = member.GetValue<string>("firstName") ?? "";
                    var lastName = member.GetValue<string>("lastName") ?? "";
                    memberName = $"{firstName} {lastName}".Trim();
                }

                try
                {
                    await _emailService.SendApprovalNotificationAsync(member.Email, memberName, autoLoginToken);
                    Console.WriteLine($"[QuickApprove] Approval email sent to: {member.Email}");

                    // Send confirmation to admin
                    var clubId = member.GetValue<int>("primaryClubId");
                    var clubName = "Ingen klubb";
                    if (clubId > 0)
                    {
                        clubName = _clubService.GetClubNameById(clubId) ?? $"Klubb (ID: {clubId})";
                    }
                    await _emailService.SendApprovalConfirmationToAdminAsync(
                        memberName, member.Email, clubName, "E-postl\u00e4nk (snabbgodk\u00e4nnande)");
                }
                catch (Exception emailEx)
                {
                    Console.WriteLine($"[QuickApprove] Failed to send approval email: {emailEx.Message}");
                }

                Console.WriteLine($"[QuickApprove] Approval completed for: {memberId}");
                return Content(QuickApproveHtmlPage("Medlem godk&#228;nd!", $"<strong>{System.Net.WebUtility.HtmlEncode(memberName)}</strong> har godk&#228;nts och f&#229;tt ett e-postmeddelande med inloggningsl&#228;nk.", true), "text/html");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[QuickApprove] Error: {ex.Message}");
                return Content(QuickApproveHtmlPage("Ett fel uppstod", "Kunde inte godk&#228;nna medlemmen. F&#246;rs&#246;k igen eller anv&#228;nd administrationspanelen.", false), "text/html");
            }
        }

        private static string QuickApproveHtmlPage(string title, string message, bool success)
        {
            var bgColor = success ? "#28a745" : "#dc3545";
            var icon = success ? "&#10003;" : "&#10007;";
            return $@"<!DOCTYPE html>
<html lang=""sv"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{title} - Pistol.nu</title>
    <style>
        body {{ font-family: Arial, sans-serif; background-color: #f5f5f5; margin: 0; padding: 40px 20px; }}
        .container {{ max-width: 500px; margin: 0 auto; background: white; border-radius: 8px; box-shadow: 0 2px 10px rgba(0,0,0,0.1); overflow: hidden; }}
        .header {{ background-color: {bgColor}; color: white; padding: 30px; text-align: center; }}
        .header h1 {{ margin: 0; font-size: 24px; }}
        .header .icon {{ font-size: 48px; display: block; margin-bottom: 15px; }}
        .content {{ padding: 30px; text-align: center; line-height: 1.6; color: #333; }}
        .content a {{ color: #007bff; text-decoration: none; }}
        .content a:hover {{ text-decoration: underline; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <span class=""icon"">{icon}</span>
            <h1>{title}</h1>
        </div>
        <div class=""content"">
            <p>{message}</p>
        </div>
    </div>
</body>
</html>";
        }

        /// <summary>
        /// Accept invitation endpoint - validates token and shows password setup page
        /// </summary>
        [HttpGet]
        public IActionResult AcceptInvitation(string token, string email)
        {
            Console.WriteLine($"[AcceptInvitation] Token received for email: {email}");

            // Validate parameters
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(email))
            {
                Console.WriteLine("[AcceptInvitation] Missing token or email parameter");
                TempData["ErrorMessage"] = "Ogiltig inbjudningslänk. Token eller e-postadress saknas.";
                return Redirect("/login-register?tab=register");
            }

            // Store token and email in ViewData for the view
            ViewData["Token"] = token;
            ViewData["Email"] = email;

            return View("~/Views/AcceptInvitation.cshtml");
        }

        /// <summary>
        /// Complete invitation - sets password and auto-approves member
        /// </summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> CompleteInvitation(string token, string email, string password)
        {
            Console.WriteLine($"[CompleteInvitation] Processing invitation for email: {email}");

            // Validate parameters
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                return Json(new { success = false, message = "Alla fält måste fyllas i" });
            }

            try
            {
                // Find member by email
                var member = _memberService.GetByEmail(email);
                if (member == null)
                {
                    Console.WriteLine($"[CompleteInvitation] Member not found for email: {email}");
                    return Json(new { success = false, message = "Kontot kunde inte hittas" });
                }

                // Get stored token and expiry
                var storedToken = member.GetValue<string>("invitationToken");
                var tokenExpiryStr = member.GetValue<string>("invitationTokenExpiry");

                // Check if token exists
                if (string.IsNullOrEmpty(storedToken))
                {
                    Console.WriteLine($"[CompleteInvitation] No invitation token found for: {email}");
                    return Json(new { success = false, message = "Inbjudningslänken har redan använts eller är ogiltig" });
                }

                // Validate token hasn't expired
                if (!DateTime.TryParse(tokenExpiryStr, out DateTime tokenExpiry) || tokenExpiry < DateTime.UtcNow)
                {
                    Console.WriteLine($"[CompleteInvitation] Token expired for: {email}");
                    member.SetValue("invitationToken", string.Empty);
                    member.SetValue("invitationTokenExpiry", string.Empty);
                    _memberService.Save(member);
                    return Json(new { success = false, message = "Inbjudningslänken har gått ut (giltig i 7 dagar)" });
                }

                // Validate token matches
                if (storedToken != token)
                {
                    Console.WriteLine($"[CompleteInvitation] Token mismatch for: {email}");
                    return Json(new { success = false, message = "Ogiltig inbjudningslänk" });
                }

                // Get identity user and change password
                var identityUser = await _memberManager.FindByEmailAsync(email);
                if (identityUser == null)
                {
                    Console.WriteLine($"[CompleteInvitation] Identity user not found for: {email}");
                    return Json(new { success = false, message = "Ett tekniskt fel uppstod" });
                }

                // Generate password reset token and use it to set new password
                var resetToken = await _memberManager.GeneratePasswordResetTokenAsync(identityUser);
                var resetPasswordResult = await _memberManager.ResetPasswordAsync(identityUser, resetToken, password);

                if (!resetPasswordResult.Succeeded)
                {
                    var errors = string.Join(", ", resetPasswordResult.Errors);
                    Console.WriteLine($"[CompleteInvitation] Password reset failed: {errors}");
                    return Json(new { success = false, message = "Kunde inte sätta lösenord: " + errors });
                }

                Console.WriteLine($"[CompleteInvitation] Password set successfully for: {email}");

                // Clear invitation token (single-use)
                member.SetValue("invitationToken", string.Empty);
                member.SetValue("invitationTokenExpiry", string.Empty);

                // Auto-approve member
                member.IsApproved = true;

                // Assign to Users group and remove from PendingApproval
                _memberService.AssignRole(member.Id, "Users");

                // Try to remove from PendingApproval (may not be in this group)
                try
                {
                    _memberService.DissociateRole(member.Id, "PendingApproval");
                }
                catch
                {
                    // Member might not be in PendingApproval group, which is fine
                }

                _memberService.Save(member);
                Console.WriteLine($"[CompleteInvitation] Member auto-approved: {email}");

                // Migrate guest scores if this member was invited via match guest system (Path B)
                await MigrateGuestScoresToMember(member);

                // Sign in the member with persistent cookie (90-day remember me)
                await _signInManager.SignInAsync(identityUser, isPersistent: true);
                Console.WriteLine($"[CompleteInvitation] Member signed in successfully: {email}");

                return Json(new { success = true, message = "Ditt konto har aktiverats!" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CompleteInvitation] Error: {ex.Message}");
                Console.WriteLine($"[CompleteInvitation] Stack trace: {ex.StackTrace}");
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Get current member's competition registrations with filtering
        /// GET: /umbraco/surface/Member/GetMyCompetitionRegistrations
        /// OPTIMIZED: Uses direct traversal from competitions hub instead of full site scan
        /// PHASE 4: Cached per member for 30 seconds to reduce repeated database queries
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyCompetitionRegistrations(
            int? year = null,
            string? competitionStatus = null,  // "upcoming", "past", "all"
            string? paymentStatus = null,      // "paid", "pending", "no_invoice", "all"
            string? searchText = null)
        {
            try
            {
                // 1. Get current member
                var member = await _memberManager.GetCurrentMemberAsync();
                if (member == null)
                {
                    return Json(new { success = false, message = "Not authenticated" });
                }

                var memberIdInt = int.Parse(member.Id);

                // PHASE 4: Check cache first (only for unfiltered requests to maximize cache hits)
                // Cache key includes member ID only (filters applied after cache retrieval)
                var baseCacheKey = $"member_competitions_{memberIdInt}";

                // Only use cache for base query (no filters) - filters are applied to cached data
                if (!year.HasValue && string.IsNullOrEmpty(competitionStatus) &&
                    string.IsNullOrEmpty(paymentStatus) && string.IsNullOrEmpty(searchText))
                {
                    var cachedResult = _appCaches.RuntimeCache.GetCacheItem<List<object>>(baseCacheKey);
                    if (cachedResult != null)
                    {
                        return Json(new { success = true, registrations = cachedResult });
                    }
                }

                // 2. PHASE 1 OPTIMIZATION: Direct traversal from competitions hub
                // Find competitions hub
                var competitionsHub = GetCompetitionsHub();
                if (competitionsHub == null)
                {
                    return Json(new { success = true, registrations = new List<object>() });
                }

                // Get all competitions and their series (including year folders)
                var allCompetitions = new List<IContent>();
                var hubChildren = _contentService.GetPagedChildren(competitionsHub.Id, 0, int.MaxValue, out _);

                foreach (var child in hubChildren)
                {
                    if (child.ContentType.Alias == "competition")
                    {
                        allCompetitions.Add(child);
                    }
                    else if (child.ContentType.Alias == "competitionSeries")
                    {
                        // Series can contain competitions
                        var seriesChildren = _contentService.GetPagedChildren(child.Id, 0, int.MaxValue, out _);
                        var competitionsInSeries = seriesChildren.Where(c => c.ContentType.Alias == "competition").ToList();
                        allCompetitions.AddRange(competitionsInSeries);
                    }
                    else
                    {
                        // Could be a year folder or other container - check its children
                        var containerChildren = _contentService.GetPagedChildren(child.Id, 0, int.MaxValue, out _);

                        foreach (var containerChild in containerChildren)
                        {
                            if (containerChild.ContentType.Alias == "competition")
                            {
                                allCompetitions.Add(containerChild);
                            }
                            else if (containerChild.ContentType.Alias == "competitionSeries")
                            {
                                // Series inside year folder
                                var nestedSeriesChildren = _contentService.GetPagedChildren(containerChild.Id, 0, int.MaxValue, out _);
                                var nestedCompetitions = nestedSeriesChildren.Where(c => c.ContentType.Alias == "competition").ToList();
                                allCompetitions.AddRange(nestedCompetitions);
                            }
                        }
                    }
                }

                // Collect registrations from all competitions
                var myRegistrations = new List<IContent>();

                foreach (var competition in allCompetitions)
                {
                    var registrationsHub = GetRegistrationsHubForCompetition(competition.Id);
                    if (registrationsHub != null)
                    {
                        var registrations = _contentService.GetPagedChildren(registrationsHub.Id, 0, int.MaxValue, out _)
                            .Where(r => r.ContentType.Alias == "competitionRegistration" &&
                                       r.GetValue<int>("memberId") == memberIdInt)
                            .ToList();

                        myRegistrations.AddRange(registrations);
                    }
                }

                // 3. PHASE 2 OPTIMIZATION: Batch load all competitions into dictionary
                var competitionIds = myRegistrations
                    .Select(r => r.GetValue<int>("competitionId"))
                    .Where(id => id > 0)
                    .Distinct()
                    .ToList();

                var competitionDict = new Dictionary<int, IContent>();
                foreach (var compId in competitionIds)
                {
                    try
                    {
                        var comp = _contentService.GetById(compId);
                        if (comp != null)
                        {
                            competitionDict[compId] = comp;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error loading competition {compId}: {ex.Message}");
                    }
                }

                // 4. PHASE 3 OPTIMIZATION: Batch load all invoices into lookup dictionary
                var invoiceLookup = BuildInvoiceLookup(competitionIds);

                // 5. Map to response objects with competition details, payment status
                var results = new List<object>();

                foreach (var reg in myRegistrations)
                {
                    try
                    {
                        var competitionId = reg.GetValue<int>("competitionId");

                        // Use batch-loaded competition from dictionary
                        if (!competitionDict.TryGetValue(competitionId, out var competition))
                        {
                            continue; // Skip if competition not found
                        }

                        // Parse shooting classes JSON
                        var shootingClassesJson = reg.GetValue<string>("shootingClasses") ?? "[]";
                        var classes = ParseShootingClasses(shootingClassesJson);

                        // Determine competition status
                        var competitionDate = competition.GetValue<DateTime?>("startDate") ?? DateTime.MinValue;
                        var status = competitionDate > DateTime.Now ? "upcoming" : "past";

                        // Get payment status from batch-loaded invoice lookup
                        var paymentInfo = invoiceLookup.TryGetValue((competitionId, memberIdInt), out var invoice)
                            ? invoice
                            : ("no_invoice", 0m, false, (int?)null);

                        results.Add(new
                        {
                            registrationId = reg.Id,
                            competitionId = competitionId,
                            competitionName = competition.Name ?? "Unknown",
                            competitionDate = competitionDate,
                            competitionType = competition.GetValue<string>("competitionType") ?? "Precision",
                            shootingClasses = classes,
                            competitionStatus = status,
                            paymentStatus = paymentInfo.Item1,
                            paymentAmount = paymentInfo.Item2,
                            hasInvoice = paymentInfo.Item3,
                            invoiceId = paymentInfo.Item4,
                            canUnregister = CanUnregister(competitionDate, paymentInfo.Item1),
                            registrationDate = reg.CreateDate
                        });
                    }
                    catch (Exception ex)
                    {
                        // Log and skip this registration if there's an error
                        Console.WriteLine($"Error processing registration {reg.Id}: {ex.Message}");
                        continue;
                    }
                }

                // PHASE 4: Cache the unfiltered results for 30 seconds
                // Only cache if no filters were applied (to maximize cache hits)
                if (!year.HasValue && string.IsNullOrEmpty(competitionStatus) &&
                    string.IsNullOrEmpty(paymentStatus) && string.IsNullOrEmpty(searchText))
                {
                    _appCaches.RuntimeCache.InsertCacheItem(baseCacheKey, () => results, TimeSpan.FromSeconds(30));
                }

                // 6. Apply filters
                if (year.HasValue)
                {
                    results = results.Where(r =>
                    {
                        var date = (DateTime)((dynamic)r).competitionDate;
                        return date.Year == year.Value;
                    }).ToList<object>();
                }

                if (!string.IsNullOrEmpty(competitionStatus) && competitionStatus != "all")
                {
                    results = results.Where(r => ((dynamic)r).competitionStatus == competitionStatus).ToList<object>();
                }

                if (!string.IsNullOrEmpty(paymentStatus) && paymentStatus != "all")
                {
                    results = results.Where(r => ((dynamic)r).paymentStatus == paymentStatus).ToList<object>();
                }

                if (!string.IsNullOrEmpty(searchText))
                {
                    results = results.Where(r =>
                    {
                        var name = ((dynamic)r).competitionName as string ?? "";
                        return name.Contains(searchText, StringComparison.OrdinalIgnoreCase);
                    }).ToList<object>();
                }

                // 5. Sort by date (upcoming first, then past)
                results = results.OrderByDescending(r => ((dynamic)r).competitionStatus == "upcoming")
                                 .ThenBy(r => ((dynamic)r).competitionDate)
                                 .ToList<object>();

                return Json(new { success = true, registrations = results });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in GetMyCompetitionRegistrations: {ex.Message}");
                return Json(new { success = false, message = "Error loading registrations: " + ex.Message });
            }
        }

        /// <summary>
        /// Parse shooting classes JSON array to simple class names
        /// </summary>
        private List<string> ParseShootingClasses(string shootingClassesJson)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(shootingClassesJson) || shootingClassesJson == "[]")
                {
                    return new List<string>();
                }

                // Try to deserialize as array of objects with "class" property
                var classObjects = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(shootingClassesJson);
                if (classObjects != null && classObjects.Count > 0)
                {
                    return classObjects
                        .Where(c => c.ContainsKey("class"))
                        .Select(c => c["class"])
                        .ToList();
                }

                // Fallback: try as simple string array
                var simpleArray = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shootingClassesJson);
                return simpleArray ?? new List<string>();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// PHASE 3 OPTIMIZATION: Build invoice lookup dictionary for all competitions
        /// Scans invoice hubs directly instead of full tree traversal
        /// Returns dictionary keyed by (competitionId, memberId)
        /// </summary>
        private Dictionary<(int competitionId, int memberId), (string Status, decimal Amount, bool HasInvoice, int? InvoiceId)> BuildInvoiceLookup(List<int> competitionIds)
        {
            var lookup = new Dictionary<(int, int), (string, decimal, bool, int?)>();

            try
            {
                // Navigate directly to invoice hubs for each competition
                foreach (var competitionId in competitionIds)
                {
                    try
                    {
                        var competition = _contentService.GetById(competitionId);
                        if (competition == null) continue;

                        // Find registrationInvoicesHub (usually first 20 children)
                        var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
                        var invoiceHub = children.FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");

                        if (invoiceHub == null) continue;

                        // Get all invoices under this hub
                        var invoices = _contentService.GetPagedChildren(invoiceHub.Id, 0, int.MaxValue, out _)
                            .Where(c => c.ContentType.Alias == "registrationInvoice")
                            .ToList();

                        // Add to lookup dictionary
                        foreach (var invoice in invoices)
                        {
                            var memberId = invoice.GetValue<int>("memberId");
                            if (memberId <= 0) continue;

                            var status = invoice.GetValue<string>("paymentStatus") ?? "pending";
                            var amount = invoice.GetValue<decimal?>("totalAmount") ?? 0m;

                            // Map status to simple format
                            var simpleStatus = status.ToLower() switch
                            {
                                "paid" => "paid",
                                "pending" => "pending",
                                "cancelled" => "no_invoice",
                                "failed" => "pending",
                                _ => "pending"
                            };

                            lookup[(competitionId, memberId)] = (simpleStatus, amount, true, invoice.Id);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error loading invoices for competition {competitionId}: {ex.Message}");
                    }
                }

                return lookup;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error building invoice lookup: {ex.Message}");
                return lookup;
            }
        }

        /// <summary>
        /// Get payment status for a competition registration (DEPRECATED - use BuildInvoiceLookup instead)
        /// Kept for backward compatibility with other code
        /// </summary>
        private (string Status, decimal Amount, bool HasInvoice, int? InvoiceId) GetPaymentStatus(int competitionId, int memberId)
        {
            try
            {
                // Find invoice for this competition + member
                var allInvoices = new List<Umbraco.Cms.Core.Models.IContent>();
                var rootContent = _contentService.GetRootContent();
                foreach (var root in rootContent)
                {
                    var descendants = GetFlatDescendants(root);
                    allInvoices.AddRange(descendants.Where(c => c.ContentType.Alias == "registrationInvoice"));
                }

                var invoice = allInvoices.FirstOrDefault(i =>
                    i.GetValue<int>("competitionId") == competitionId &&
                    i.GetValue<int>("memberId") == memberId);

                if (invoice == null)
                {
                    return ("no_invoice", 0m, false, null);
                }

                var status = invoice.GetValue<string>("paymentStatus") ?? "pending";
                var amount = invoice.GetValue<decimal?>("totalAmount") ?? 0m;

                // Map status to simple format
                var simpleStatus = status.ToLower() switch
                {
                    "paid" => "paid",
                    "pending" => "pending",
                    "cancelled" => "no_invoice",
                    "failed" => "pending",
                    _ => "pending"
                };

                return (simpleStatus, amount, true, invoice.Id);
            }
            catch
            {
                return ("no_invoice", 0m, false, null);
            }
        }

        /// <summary>
        /// Determine if user can unregister from competition
        /// </summary>
        private bool CanUnregister(DateTime competitionDate, string paymentStatus)
        {
            // Can't unregister if:
            // 1. Competition has already started
            // 2. Payment has been made (needs refund process)

            if (competitionDate <= DateTime.Now)
            {
                return false; // Competition already started or past
            }

            if (paymentStatus == "paid")
            {
                return false; // Payment already made - needs admin intervention
            }

            return true; // Can unregister if future competition and not paid
        }

        /// <summary>
        /// Recursively gets all descendants of a content node
        /// </summary>
        private IEnumerable<IContent> GetAllDescendants(IContent content)
        {
            yield return content;

            var children = _contentService.GetPagedChildren(content.Id, 0, int.MaxValue, out var totalRecords);
            foreach (var child in children)
            {
                foreach (var descendant in GetAllDescendants(child))
                {
                    yield return descendant;
                }
            }
        }

        /// <summary>
        /// Get all descendants of a content item in flat list (OPTIMIZED)
        /// Uses breadth-first iteration instead of recursion for better performance
        /// </summary>
        private List<Umbraco.Cms.Core.Models.IContent> GetFlatDescendants(Umbraco.Cms.Core.Models.IContent root)
        {
            var result = new List<Umbraco.Cms.Core.Models.IContent>();
            var queue = new Queue<Umbraco.Cms.Core.Models.IContent>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                var children = _contentService.GetPagedChildren(current.Id, 0, int.MaxValue, out _);
                foreach (var child in children)
                {
                    queue.Enqueue(child);
                }
            }

            return result;
        }

        /// <summary>
        /// PHASE 1 OPTIMIZATION: Find competitions hub node
        /// PHASE 4 OPTIMIZATION: Cached for 10 minutes to avoid repeated lookups
        /// Returns the competitionsHub node or null if not found
        /// </summary>
        private IContent? GetCompetitionsHub()
        {
            try
            {
                // PHASE 4: Check cache first
                var cacheKey = "competitions_hub_node";
                var cached = _appCaches.RuntimeCache.GetCacheItem<IContent?>(cacheKey);
                if (cached != null)
                {
                    return cached;
                }

                // Cache miss - load from database
                var rootContent = _contentService.GetRootContent();
                IContent? hub = null;

                foreach (var root in rootContent)
                {
                    // Check direct children for competitions hub
                    var children = _contentService.GetPagedChildren(root.Id, 0, 100, out _);
                    hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
                    if (hub != null)
                    {
                        break;
                    }
                }

                // Store in cache for 10 minutes (even if null to prevent repeated failed lookups)
                _appCaches.RuntimeCache.InsertCacheItem(cacheKey, () => hub, TimeSpan.FromMinutes(10));

                return hub;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error finding competitions hub: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// PHASE 1 OPTIMIZATION: Get registrations hub for a specific competition
        /// Returns the competitionRegistrationsHub node or null if not found
        /// </summary>
        private IContent? GetRegistrationsHubForCompetition(int competitionId)
        {
            try
            {
                var competition = _contentService.GetById(competitionId);
                if (competition == null)
                {
                    return null;
                }

                // Check first 20 children for registrations hub (hub is usually near top)
                var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
                var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");

                return hub;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error finding registrations hub for competition {competitionId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Upload a profile picture for the current member
        /// POST: /umbraco/surface/Member/UploadProfilePicture
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadProfilePicture(IFormFile profilePicture)
        {
            try
            {
                // Validate user is logged in
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }

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

                // Get member from member service
                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
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
        /// Remove profile picture for the current member
        /// POST: /umbraco/surface/Member/RemoveProfilePicture
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> RemoveProfilePicture()
        {
            try
            {
                // Validate user is logged in
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }

                // Get member from member service
                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
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

        /// <summary>
        /// Get handicap profile for the current member.
        /// Returns shooter class and handicap info for each weapon class with statistics.
        /// GET: /umbraco/surface/Member/GetHandicapProfile
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetHandicapProfile(string discipline = "Precision")
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get shooter class from member profile based on discipline
                var shooterClassProperty = discipline == "Milsnabb" ? "milsnabbShooterClass" : "precisionShooterClass";
                string shooterClass = "";
                if (member.HasProperty(shooterClassProperty))
                {
                    shooterClass = member.GetValue<string>(shooterClassProperty) ?? "";
                }

                // Get all statistics for this member filtered by discipline
                var allStats = await _statisticsService.GetAllStatisticsAsync(member.Id, discipline);

                // Calculate handicap for each weapon class (only if shooter class is set)
                var weaponClassProfiles = new List<object>();
                if (!string.IsNullOrEmpty(shooterClass))
                {
                    foreach (var stats in allStats)
                    {
                        var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);

                        weaponClassProfiles.Add(new
                        {
                            weaponClass = stats.WeaponClass,
                            weaponClassName = GetWeaponClassName(stats.WeaponClass),
                            handicapPerSeries = profile.HandicapPerSeries,
                            isProvisional = profile.IsProvisional,
                            completedMatches = profile.CompletedMatches,
                            requiredMatches = _handicapCalculator.Settings.RequiredMatches,
                            effectiveAverage = profile.EffectiveAverage,
                            actualAverage = profile.ActualAverage,
                            referenceScore = _handicapCalculator.Settings.ReferenceSeriesScore
                        });
                    }
                }

                // Include all settings for the explanatory text
                var settings = _handicapCalculator.Settings;
                var provisionalAverages = settings.ProvisionalAverages
                    .Select(kvp => new { className = kvp.Key, average = kvp.Value })
                    .ToList();

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        shooterClass = shooterClass,
                        weaponClasses = weaponClassProfiles,
                        settings = new
                        {
                            referenceScore = settings.ReferenceSeriesScore,
                            maxHandicap = settings.MaxHandicapPerSeries,
                            requiredMatches = settings.RequiredMatches,
                            rollingWindowMatchCount = settings.RollingWindowMatchCount,
                            provisionalAverages = provisionalAverages
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting handicap profile: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get handicap profile for a specific member (admin-only endpoint).
        /// Returns shooter class and handicap info for each weapon class with statistics.
        /// GET: /umbraco/surface/Member/GetHandicapProfileForMember?memberId=123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetHandicapProfileForMember(int memberId, string discipline = "Precision")
        {
            try
            {
                // Check if current user is admin
                if (!await _authorizationService.IsCurrentUserAdminAsync())
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Get member by ID
                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get shooter class from member profile based on discipline
                var shooterClassProperty = discipline == "Milsnabb" ? "milsnabbShooterClass" : "precisionShooterClass";
                string shooterClass = "";
                if (member.HasProperty(shooterClassProperty))
                {
                    shooterClass = member.GetValue<string>(shooterClassProperty) ?? "";
                }

                // Get all statistics for this member filtered by discipline
                var allStats = await _statisticsService.GetAllStatisticsAsync(memberId, discipline);

                // Calculate handicap for each weapon class (only if shooter class is set)
                var weaponClassProfiles = new List<object>();
                if (!string.IsNullOrEmpty(shooterClass))
                {
                    foreach (var stats in allStats)
                    {
                        var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);

                        weaponClassProfiles.Add(new
                        {
                            weaponClass = stats.WeaponClass,
                            weaponClassName = GetWeaponClassName(stats.WeaponClass),
                            handicapPerSeries = profile.HandicapPerSeries,
                            isProvisional = profile.IsProvisional,
                            completedMatches = profile.CompletedMatches,
                            requiredMatches = _handicapCalculator.Settings.RequiredMatches,
                            effectiveAverage = profile.EffectiveAverage,
                            actualAverage = profile.ActualAverage,
                            referenceScore = _handicapCalculator.Settings.ReferenceSeriesScore
                        });
                    }
                }

                // Include all settings for the explanatory text
                var settings = _handicapCalculator.Settings;
                var provisionalAverages = settings.ProvisionalAverages
                    .Select(kvp => new { className = kvp.Key, average = kvp.Value })
                    .ToList();

                return Json(new
                {
                    success = true,
                    data = new
                    {
                        shooterClass = shooterClass,
                        weaponClasses = weaponClassProfiles,
                        settings = new
                        {
                            referenceScore = settings.ReferenceSeriesScore,
                            maxHandicap = settings.MaxHandicapPerSeries,
                            requiredMatches = settings.RequiredMatches,
                            rollingWindowMatchCount = settings.RollingWindowMatchCount,
                            provisionalAverages = provisionalAverages
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting handicap profile for member {memberId}: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Recalculate handicap statistics for the current member.
        /// Triggers a full recalculation from historical data for all weapon classes.
        /// POST: /umbraco/surface/Member/RecalculateMyHandicap
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RecalculateMyHandicap(string discipline = "Precision")
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Not logged in" });
                }

                var memberEmail = currentMember.Email ?? string.Empty;
                var member = _memberService.GetByEmail(memberEmail);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Recalculate for all weapon classes
                var weaponClasses = new[] { "A", "B", "C", "R", "M", "L" };
                foreach (var wc in weaponClasses)
                {
                    await _statisticsService.RecalculateFromHistoryAsync(member.Id, wc, discipline);
                }

                return Json(new { success = true, message = "Handicap omberäknat" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error recalculating handicap: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Recalculate handicap statistics for a specific member (admin-only endpoint).
        /// Triggers a full recalculation from historical data for all weapon classes.
        /// POST: /umbraco/surface/Member/RecalculateHandicapForMember
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RecalculateHandicapForMember([FromBody] RecalculateHandicapRequest request)
        {
            try
            {
                // Check if current user can edit this member (site admin, regional admin, or club admin)
                if (!await _authorizationService.CanEditMemberAsync(request.MemberId))
                {
                    return Json(new { success = false, message = "Access denied" });
                }

                // Verify member exists
                var member = _memberService.GetById(request.MemberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Recalculate for all weapon classes
                var weaponClasses = new[] { "A", "B", "C", "R", "M", "L" };
                foreach (var wc in weaponClasses)
                {
                    await _statisticsService.RecalculateFromHistoryAsync(request.MemberId, wc);
                }

                return Json(new { success = true, message = "Handicap omberäknat" });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error recalculating handicap for member {request.MemberId}: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Request model for recalculating handicap for a specific member.
        /// </summary>
        public class RecalculateHandicapRequest
        {
            public int MemberId { get; set; }
        }

        /// <summary>
        /// Get human-readable weapon class name.
        /// </summary>
        private string GetWeaponClassName(string weaponClass)
        {
            return weaponClass switch
            {
                "A" => "Tjänstevapen",
                "B" => "Kal. 32-45",
                "C" => "Kal. 22",
                "R" => "Revolver",
                "M" => "Magnum",
                "L" => "Luftpistol",
                _ => weaponClass
            };
        }

        #region Dashboard Sharing

        /// <summary>
        /// Get the current member's dashboard sharing level.
        /// GET /umbraco/surface/Member/GetDashboardSharingLevel
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDashboardSharingLevel()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Not authenticated" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                var rawLevel = member.GetValue<string>("dashboardSharingLevel");
                var sharingLevel = string.IsNullOrEmpty(rawLevel) ? "club" : rawLevel;

                return Json(new { success = true, sharingLevel });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Update the current member's dashboard sharing level.
        /// POST /umbraco/surface/Member/UpdateDashboardSharing
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateDashboardSharing([FromBody] DashboardSharingRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Not authenticated" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Validate sharing level
                var validLevels = new[] { "none", "club", "all" };
                var sharingLevel = request.SharingLevel?.ToLower() ?? "none";
                if (!validLevels.Contains(sharingLevel))
                {
                    sharingLevel = "none";
                }

                member.SetValue("dashboardSharingLevel", sharingLevel);
                _memberService.Save(member);

                return Json(new { success = true, message = "Delningsinställningar sparade", sharingLevel });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get dashboard statistics for a specific member (for viewing shared dashboards).
        /// Permission check: Admin always allowed, otherwise based on sharing level.
        /// GET /umbraco/surface/Member/GetMemberDashboard?memberId={id}&year={year}
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMemberDashboard(int memberId, int? year = null, string competitionType = "Precision")
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Not authenticated" });
            }

            try
            {
                // Get target member
                var targetMember = _memberService.GetById(memberId);
                if (targetMember == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                // Get viewer member
                var viewerMember = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (viewerMember == null)
                {
                    return Json(new { success = false, message = "Viewer member not found" });
                }

                // Check permissions
                var isAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                var rawLevel = targetMember.GetValue<string>("dashboardSharingLevel");
                var sharingLevel = string.IsNullOrEmpty(rawLevel) ? "club" : rawLevel;

                if (!isAdmin)
                {
                    if (sharingLevel == "none")
                    {
                        return Json(new { success = false, message = "This member has not shared their dashboard" });
                    }

                    if (sharingLevel == "club")
                    {
                        // Check if viewer is in same club as target
                        var viewerPrimaryClub = viewerMember.GetValue<string>("primaryClubId") ?? "";
                        var viewerClubIds = viewerMember.GetValue<string>("memberClubIds") ?? "";
                        var targetPrimaryClub = targetMember.GetValue<string>("primaryClubId") ?? "";
                        var targetClubIds = targetMember.GetValue<string>("memberClubIds") ?? "";

                        var viewerClubs = new HashSet<string>();
                        if (!string.IsNullOrEmpty(viewerPrimaryClub)) viewerClubs.Add(viewerPrimaryClub);
                        if (!string.IsNullOrEmpty(viewerClubIds))
                        {
                            foreach (var c in viewerClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                viewerClubs.Add(c.Trim());
                        }

                        var targetClubs = new HashSet<string>();
                        if (!string.IsNullOrEmpty(targetPrimaryClub)) targetClubs.Add(targetPrimaryClub);
                        if (!string.IsNullOrEmpty(targetClubIds))
                        {
                            foreach (var c in targetClubIds.Split(',', StringSplitOptions.RemoveEmptyEntries))
                                targetClubs.Add(c.Trim());
                        }

                        var hasCommonClub = viewerClubs.Intersect(targetClubs).Any();
                        if (!hasCommonClub)
                        {
                            return Json(new { success = false, message = "Access denied - you are not in the same club" });
                        }
                    }
                    // sharingLevel == "all" - allow any logged-in member
                }

                // Get dashboard statistics for target member
                List<HpskSite.Shared.Models.UnifiedResultEntry> allResultsUnfiltered;
                if (competitionType == "Precision")
                {
                    allResultsUnfiltered = _unifiedResultsService.GetMemberResults(memberId);
                }
                else
                {
                    allResultsUnfiltered = GetMemberResultsForDiscipline(memberId, competitionType);
                }

                // Calculate available years from all data (before filtering)
                var availableYears = allResultsUnfiltered
                    .Select(r => r.Date.Year)
                    .Distinct()
                    .OrderByDescending(y => y)
                    .ToList();
                if (!availableYears.Any()) availableYears.Add(DateTime.Now.Year);

                // Default to most recent year with data, or fall back to current year if no data
                var selectedYear = year ?? availableYears.First();
                var allResults = allResultsUnfiltered.Where(r => r.Date.Year == selectedYear).ToList();

                // Separate training and competition results
                // TrainingMatch = from mobile app training matches, Training = manually entered training
                var trainingResults = allResults.Where(r => r.SourceType == "Training" || r.SourceType == "TrainingMatch").ToList();
                var competitionResults = allResults.Where(r => r.SourceType == "Competition" || r.SourceType == "Official").ToList();

                var totalSessions = allResults.Count;
                var totalTrainingSessions = trainingResults.Count;
                var totalCompetitions = competitionResults.Count;
                var overallAverage = allResults.Any() ? allResults.Average(r => r.AverageScore) : 0;

                // Calculate trend (last 30 days vs previous 30 days)
                var recentDate = DateTime.Now.AddDays(-30);
                var previousDate = DateTime.Now.AddDays(-60);

                var recentResults = allResults.Where(r => r.Date >= recentDate).ToList();
                var previousResults = allResults.Where(r => r.Date >= previousDate && r.Date < recentDate).ToList();

                var recentAverage = recentResults.Any() ? recentResults.Average(r => r.AverageScore) : 0;
                var previousAverage = previousResults.Any() ? previousResults.Average(r => r.AverageScore) : 0;

                // Calculate 30-day average breakdown by weapon class
                var recentAverageByClass = recentResults
                    .GroupBy(r => r.WeaponClass)
                    .Select(g => new
                    {
                        weaponClass = g.Key,
                        average = Math.Round(g.Average(r => r.AverageScore), 1)
                    })
                    .OrderBy(x => x.weaponClass)
                    .ToList();

                // Generate individual entry data for all results
                var monthlyData = allResults
                    .Select(r => new
                    {
                        date = r.Date,
                        year = r.Date.Year,
                        month = r.Date.Month,
                        day = r.Date.Day,
                        weaponClass = r.WeaponClass,
                        isCompetition = r.SourceType == "Competition" || r.SourceType == "Official",
                        averageScore = r.AverageScore,
                        totalScore = r.TotalScore,
                        seriesCount = r.SeriesCount,
                        competitionName = r.CompetitionName,
                        id = r.Id
                    })
                    .OrderBy(x => x.date)
                    .ToList();

                // Generate weapon class distribution
                var weaponClassData = allResults
                    .GroupBy(r => new { r.WeaponClass, IsCompetition = r.SourceType == "Competition" || r.SourceType == "Official" })
                    .Select(g => new
                    {
                        weaponClass = g.Key.WeaponClass,
                        isCompetition = g.Key.IsCompetition,
                        averageScore = g.Average(r => r.AverageScore),
                        sessionCount = g.Count()
                    })
                    .OrderBy(x => x.weaponClass)
                    .ToList();

                // Calculate personal bests by series count (discipline-aware)
                int[] GetSeriesCountsForWeapon(string wc)
                {
                    if (competitionType == "Milsnabb") return new[] { 12 };
                    return wc == "L" ? new[] { 6, 8, 12 } : new[] { 6, 7, 10 };
                }

                var personalBestsBySeriesCount = allResults
                    .Where(r => {
                        var seriesCounts = GetSeriesCountsForWeapon(r.WeaponClass);
                        return seriesCounts.Contains(r.SeriesCount);
                    })
                    .GroupBy(r => new { r.WeaponClass, r.SeriesCount, IsCompetition = r.SourceType == "Competition" || r.SourceType == "Official" })
                    .Select(g => new
                    {
                        weaponClass = g.Key.WeaponClass,
                        seriesCount = g.Key.SeriesCount,
                        isCompetition = g.Key.IsCompetition,
                        bestTotalScore = g.Max(r => r.TotalScore)
                    })
                    .OrderBy(x => x.weaponClass)
                    .ThenBy(x => x.seriesCount)
                    .ToList();

                // Get member name for display
                var memberName = targetMember.Name ?? "Unknown";

                // Calculate medal statistics for the selected year (discipline-aware)
                var medalStats = GetMemberMedalStats(memberId, selectedYear, competitionType);

                // Calculate handicap profiles (discipline-aware)
                var shooterClassProperty = competitionType == "Milsnabb" ? "milsnabbShooterClass" : "precisionShooterClass";
                string shooterClass = "";
                if (targetMember.HasProperty(shooterClassProperty))
                {
                    shooterClass = targetMember.GetValue<string>(shooterClassProperty) ?? "";
                }
                var allHandicapStats = await _statisticsService.GetAllStatisticsAsync(memberId, competitionType);
                var handicapProfiles = new List<object>();
                if (!string.IsNullOrEmpty(shooterClass))
                {
                    foreach (var handicapStats in allHandicapStats)
                    {
                        var profile = _handicapCalculator.CalculateHandicap(handicapStats, shooterClass);
                        handicapProfiles.Add(new
                        {
                            weaponClass = handicapStats.WeaponClass,
                            handicapPerSeries = profile.HandicapPerSeries,
                            isProvisional = profile.IsProvisional,
                            completedMatches = profile.CompletedMatches,
                            requiredMatches = _handicapCalculator.Settings.RequiredMatches
                        });
                    }
                }

                var stats = new
                {
                    memberName,
                    memberId,
                    competitionType,
                    totalSessions,
                    totalTrainingSessions,
                    totalCompetitions,
                    overallAverage = Math.Round(overallAverage, 1),
                    recentAverage = Math.Round(recentAverage, 1),
                    recentAverageByClass,
                    previousAverage = Math.Round(previousAverage, 1),
                    monthlyData,
                    weaponClassData,
                    availableYears,
                    selectedYear,
                    personalBestsBySeriesCount,
                    medalStats,
                    handicapProfiles,
                    shooterClass
                };

                return Json(new { success = true, data = stats });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        /// <summary>
        /// Get unified results for a non-Precision discipline by querying
        /// the discipline-specific result entry table and TrainingScores.
        /// </summary>
        private List<HpskSite.Shared.Models.UnifiedResultEntry> GetMemberResultsForDiscipline(int memberId, string discipline)
        {
            var results = new List<HpskSite.Shared.Models.UnifiedResultEntry>();
            var resultTable = discipline == "Milsnabb" ? "MilsnabbResultEntry" : "PrecisionResultEntry";
            int defaultSeriesCount = discipline == "Milsnabb" ? 12 : 0;

            using (var db = _databaseFactory.CreateDatabase())
            {
                // Source 1: TrainingScores filtered by discipline
                var trainingScores = db.Fetch<dynamic>(@"
                    SELECT Id, MemberId, TrainingDate, WeaponClass, SeriesScores,
                           TotalScore, XCount, Notes, IsCompetition, TrainingMatchId
                    FROM TrainingScores
                    WHERE MemberId = @0 AND Discipline = @1
                    ORDER BY TrainingDate DESC",
                    memberId, discipline);

                foreach (var score in trainingScores)
                {
                    int seriesCount = 0;
                    try
                    {
                        var seriesJson = score.SeriesScores?.ToString();
                        if (!string.IsNullOrEmpty(seriesJson))
                        {
                            var trainingSeries = System.Text.Json.JsonSerializer.Deserialize<List<TrainingSeries>>(seriesJson);
                            if (trainingSeries != null && trainingSeries.Count > 0)
                            {
                                if (trainingSeries.Count == 1 && trainingSeries[0].EntryMethod == "TotalOnly")
                                    seriesCount = trainingSeries[0].SeriesCount ?? 0;
                                else
                                    seriesCount = trainingSeries.Count;
                            }
                        }
                    }
                    catch { }
                    if (seriesCount == 0 && defaultSeriesCount > 0) seriesCount = defaultSeriesCount;

                    int totalScore = (int)(score.TotalScore ?? 0);
                    bool isCompetition = score.IsCompetition is bool b ? b : false;
                    bool isTrainingMatch = score.TrainingMatchId != null;
                    string sourceType = isCompetition ? "Competition" : (isTrainingMatch ? "TrainingMatch" : "Training");

                    results.Add(new HpskSite.Shared.Models.UnifiedResultEntry
                    {
                        Id = (int)score.Id,
                        Date = (DateTime)score.TrainingDate,
                        SourceType = sourceType,
                        WeaponClass = score.WeaponClass?.ToString() ?? "",
                        TotalScore = totalScore,
                        XCount = (int)(score.XCount ?? 0),
                        SeriesCount = seriesCount,
                        AverageScore = seriesCount > 0 ? Math.Round((double)totalScore / seriesCount, 1) : 0,
                        Notes = score.Notes?.ToString(),
                        CanEdit = true,
                        CanDelete = true
                    });
                }

                // Source 2: Competition results from discipline-specific table
                var competitionResults = db.Fetch<dynamic>($@"
                    SELECT CompetitionId, MemberId, ShootingClass, Shots, SeriesNumber, EnteredAt
                    FROM {resultTable}
                    WHERE MemberId = @0
                    ORDER BY EnteredAt DESC",
                    memberId);

                // Group by competition to build unified entries
                var byCompetition = competitionResults.GroupBy(r => (int)r.CompetitionId);
                foreach (var group in byCompetition)
                {
                    int competitionId = group.Key;
                    var competition = _contentService.GetById(competitionId);
                    string competitionName = competition?.Name ?? $"Tävling #{competitionId}";
                    string weaponClass = "";
                    int totalScore = 0;
                    int xCount = 0;
                    int seriesCount = group.Count();
                    DateTime enteredAt = DateTime.MinValue;

                    foreach (var entry in group)
                    {
                        string shootingClass = entry.ShootingClass?.ToString() ?? "";
                        if (string.IsNullOrEmpty(weaponClass) && shootingClass.Length > 0)
                            weaponClass = shootingClass.Substring(0, 1);

                        if (entry.EnteredAt != null && (DateTime)entry.EnteredAt > enteredAt)
                            enteredAt = (DateTime)entry.EnteredAt;

                        // Parse shots JSON to calculate score
                        try
                        {
                            var shotsJson = entry.Shots?.ToString();
                            if (!string.IsNullOrEmpty(shotsJson))
                            {
                                var shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shotsJson);
                                if (shots != null)
                                {
                                    foreach (var shot in shots)
                                    {
                                        if (shot.Equals("X", StringComparison.OrdinalIgnoreCase))
                                        {
                                            totalScore += 10;
                                            xCount++;
                                        }
                                        else if (int.TryParse(shot, out int shotValue))
                                        {
                                            totalScore += shotValue;
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    // Use competition date if available
                    DateTime competitionDate = enteredAt;
                    if (competition != null)
                    {
                        var compDate = competition.GetValue<DateTime>("competitionDate");
                        if (compDate != default) competitionDate = compDate;
                    }

                    results.Add(new HpskSite.Shared.Models.UnifiedResultEntry
                    {
                        Id = competitionId,
                        Date = competitionDate,
                        SourceType = "Official",
                        WeaponClass = weaponClass,
                        TotalScore = totalScore,
                        XCount = xCount,
                        SeriesCount = seriesCount,
                        AverageScore = seriesCount > 0 ? Math.Round((double)totalScore / seriesCount, 1) : 0,
                        CompetitionName = competitionName,
                        CompetitionId = competitionId,
                        CanEdit = false,
                        CanDelete = false
                    });
                }
            }

            return results.OrderByDescending(r => r.Date).ToList();
        }

        /// <summary>
        /// Request model for updating dashboard sharing preferences.
        /// </summary>
        public class DashboardSharingRequest
        {
            public string? SharingLevel { get; set; }
        }

        /// <summary>
        /// Get medal statistics for a member for a specific year.
        /// Sources: TrainingScores (external competitions) and Competition Results documents.
        /// </summary>
        private object GetMemberMedalStats(int memberId, int year, string competitionType = "Precision")
        {
            int silverCount = 0;
            int bronzeCount = 0;

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Source 1: TrainingScores table - external competitions with medals (discipline-filtered)
                    var disciplineFilter = competitionType == "Precision"
                        ? "AND (Discipline = 'Precision' OR Discipline IS NULL)"
                        : $"AND Discipline = @2";
                    var trainingMedals = db.Fetch<dynamic>($@"
                        SELECT CompetitionStdMedal, COUNT(*) as MedalCount
                        FROM TrainingScores
                        WHERE MemberId = @0
                          AND IsCompetition = 1
                          AND CompetitionStdMedal IS NOT NULL
                          AND CompetitionStdMedal != ''
                          AND YEAR(TrainingDate) = @1
                          {disciplineFilter}
                        GROUP BY CompetitionStdMedal",
                        memberId, year, competitionType);

                    foreach (var medal in trainingMedals)
                    {
                        string medalType = medal.CompetitionStdMedal?.ToString()?.ToUpper() ?? "";
                        int count = (int)(medal.MedalCount ?? 0);

                        if (medalType == "S")
                            silverCount += count;
                        else if (medalType == "B")
                            bronzeCount += count;
                    }

                    // Source 2: Competition Results - get competitions the member participated in
                    var resultTable = competitionType == "Milsnabb" ? "MilsnabbResultEntry" : "PrecisionResultEntry";
                    var competitionIds = db.Fetch<int>($@"
                        SELECT DISTINCT CompetitionId
                        FROM {resultTable}
                        WHERE MemberId = @0",
                        memberId);

                    // Cache of competition IDs for this year (optimization)
                    var competitionIdsForYear = GetCompetitionIdsForYear(competitionIds, year);

                    foreach (var competitionId in competitionIdsForYear)
                    {
                        // Find the specific result page named "Resultat" (official results)
                        var resultPage = _contentService.GetPagedChildren(competitionId, 0, 50, out _)
                            .FirstOrDefault(n => n.ContentType.Alias == "competitionResult" && n.Name == "Resultat");

                        if (resultPage == null) continue;

                        var resultDataJson = resultPage.GetValue<string>("resultData");
                        if (string.IsNullOrEmpty(resultDataJson)) continue;

                        try
                        {
                            var finalResults = Newtonsoft.Json.JsonConvert.DeserializeObject<HpskSite.CompetitionTypes.Precision.Models.PrecisionFinalResults>(resultDataJson);
                            if (finalResults?.ClassGroups == null) continue;

                            foreach (var classGroup in finalResults.ClassGroups)
                            {
                                var shooter = classGroup.Shooters?.FirstOrDefault(s => s.MemberId == memberId);
                                if (shooter != null && !string.IsNullOrEmpty(shooter.StandardMedal))
                                {
                                    if (shooter.StandardMedal.ToUpper() == "S")
                                        silverCount++;
                                    else if (shooter.StandardMedal.ToUpper() == "B")
                                        bronzeCount++;
                                }
                            }
                        }
                        catch
                        {
                            // Skip invalid JSON
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail - return zero counts
                System.Diagnostics.Debug.WriteLine($"[MedalStats] ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[MedalStats] Stack: {ex.StackTrace}");
            }

            // Calculate total points: Silver = 2, Bronze = 1
            int totalPoints = (silverCount * 2) + (bronzeCount * 1);

            System.Diagnostics.Debug.WriteLine($"[MedalStats] Final result: Silver={silverCount}, Bronze={bronzeCount}, Points={totalPoints}");

            return new
            {
                silverCount,
                bronzeCount,
                totalPoints
            };
        }

        /// <summary>
        /// Filter competition IDs by year (optimization - reduces content service calls)
        /// </summary>
        private List<int> GetCompetitionIdsForYear(List<int> competitionIds, int year)
        {
            var result = new List<int>();

            // Batch load competitions to check dates
            var competitions = _contentService.GetByIds(competitionIds);

            foreach (var competition in competitions)
            {
                var competitionDate = competition.GetValue<DateTime>("competitionDate");
                if (competitionDate.Year == year)
                {
                    result.Add(competition.Id);
                }
            }

            return result;
        }

        #endregion

        #region Guest Score Migration

        /// <summary>
        /// Migrate guest scores to member account after invitation completion (Path B)
        /// This handles the case where a guest was invited to a match via email and later
        /// completed their registration. Their scores should transfer to their new member account.
        /// </summary>
        private async Task MigrateGuestScoresToMember(IMember member)
        {
            try
            {
                // Check if this member has a pending match code (set during Path B invitation)
                var pendingMatchCode = member.GetValue<string>("pendingMatchCode");

                if (string.IsNullOrEmpty(pendingMatchCode))
                {
                    // Not a Path B invitation - nothing to migrate
                    return;
                }

                Console.WriteLine($"[GuestMigration] Found pending match code: {pendingMatchCode} for member {member.Id}");

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Find GuestParticipant record with this member as PendingMemberId
                    var guestParticipant = db.SingleOrDefault<dynamic>(
                        "SELECT Id FROM TrainingMatchGuests WHERE PendingMemberId = @0",
                        member.Id);

                    if (guestParticipant == null)
                    {
                        Console.WriteLine($"[GuestMigration] No guest participant found for member {member.Id}");
                        // Clear the property anyway
                        member.SetValue("pendingMatchCode", string.Empty);
                        _memberService.Save(member);
                        return;
                    }

                    int guestParticipantId = (int)guestParticipant.Id;
                    Console.WriteLine($"[GuestMigration] Found guest participant ID: {guestParticipantId}");

                    // Update TrainingMatchParticipants - set MemberId and clear GuestParticipantId
                    var participantsUpdated = db.Execute(
                        @"UPDATE TrainingMatchParticipants
                          SET MemberId = @0, GuestParticipantId = NULL
                          WHERE GuestParticipantId = @1",
                        member.Id, guestParticipantId);

                    Console.WriteLine($"[GuestMigration] Updated {participantsUpdated} participant record(s)");

                    // Update TrainingScores - set MemberId and clear GuestParticipantId
                    var scoresUpdated = db.Execute(
                        @"UPDATE TrainingScores
                          SET MemberId = @0, GuestParticipantId = NULL
                          WHERE GuestParticipantId = @1",
                        member.Id, guestParticipantId);

                    Console.WriteLine($"[GuestMigration] Updated {scoresUpdated} score record(s)");

                    // Delete the GuestParticipant record (no longer needed)
                    db.Execute("DELETE FROM TrainingMatchGuests WHERE Id = @0", guestParticipantId);
                    Console.WriteLine($"[GuestMigration] Deleted guest participant record");
                }

                // Clear the pending match code property
                member.SetValue("pendingMatchCode", string.Empty);
                _memberService.Save(member);

                Console.WriteLine($"[GuestMigration] Migration completed successfully for member {member.Id}");
            }
            catch (Exception ex)
            {
                // Log but don't fail the invitation completion
                Console.WriteLine($"[GuestMigration] Error during score migration: {ex.Message}");
                Console.WriteLine($"[GuestMigration] Stack trace: {ex.StackTrace}");
            }
        }

        #endregion

        #region Admin Contact Helpers

        /// <summary>
        /// Gets club admin and regional admin contacts for a given club.
        /// Used to notify the right admins when a new member registers.
        /// </summary>
        private (List<(string Name, string Email)> ClubAdmins, List<(string Name, string Email)> RegionalAdmins, string RegionCode) GetAdminContactsForClub(int clubId)
        {
            var clubAdmins = new List<(string Name, string Email)>();
            var regionalAdmins = new List<(string Name, string Email)>();
            var regionCode = "";

            try
            {
                // Single GetAll call reused for both lookups
                var allMembers = _memberService.GetAll(0, int.MaxValue, out var totalRecords);
                var members = allMembers
                    .Where(m => m.ContentType.Alias != ClubMemberTypeAlias)
                    .ToList();

                // Find club admins
                var clubAdminGroup = $"ClubAdmin_{clubId}";
                foreach (var member in members)
                {
                    var roles = _memberService.GetAllRoles(member.Id);
                    if (roles.Contains(clubAdminGroup))
                    {
                        var name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();
                        if (!string.IsNullOrEmpty(member.Email))
                        {
                            clubAdmins.Add((name, member.Email));
                        }
                    }
                }

                // Get the club's region code
                var clubContent = _contentService.GetById(clubId);
                if (clubContent != null)
                {
                    regionCode = clubContent.GetValue<string>("regionalFederation") ?? "";
                }

                // Find regional admins
                if (!string.IsNullOrEmpty(regionCode))
                {
                    var regionalAdminGroup = $"RegionalAdmin_{regionCode}";
                    foreach (var member in members)
                    {
                        var roles = _memberService.GetAllRoles(member.Id);
                        if (roles.Contains(regionalAdminGroup))
                        {
                            var name = $"{member.GetValue("firstName")} {member.GetValue("lastName")}".Trim();
                            if (!string.IsNullOrEmpty(member.Email))
                            {
                                regionalAdmins.Add((name, member.Email));
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error looking up admin contacts for club {clubId}: {ex.Message}");
            }

            return (clubAdmins, regionalAdmins, regionCode);
        }

        #endregion

    }
}