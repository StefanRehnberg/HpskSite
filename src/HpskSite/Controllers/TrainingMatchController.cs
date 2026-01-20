using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models.ViewModels.TrainingScoring;
using HpskSite.Hubs;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Services;
using QRCoder;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using System.Text.Json;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Controller for managing training matches between multiple shooters
    /// </summary>
    public class TrainingMatchController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IHubContext<TrainingMatchHub> _hubContext;
        private readonly IHandicapCalculator _handicapCalculator;
        private readonly IShooterStatisticsService _statisticsService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly PushNotificationService _pushNotificationService;
        private readonly EmailService _emailService;

        public TrainingMatchController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IHubContext<TrainingMatchHub> hubContext,
            IHandicapCalculator handicapCalculator,
            IShooterStatisticsService statisticsService,
            AdminAuthorizationService authorizationService,
            PushNotificationService pushNotificationService,
            EmailService emailService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _databaseFactory = databaseFactory;
            _hubContext = hubContext;
            _handicapCalculator = handicapCalculator;
            _statisticsService = statisticsService;
            _authorizationService = authorizationService;
            _pushNotificationService = pushNotificationService;
            _emailService = emailService;
        }

        #region Helper Methods

        /// <summary>
        /// Get the current member's ID from the server session
        /// Used to include in API responses so client can refresh its member context
        /// </summary>
        private async Task<int?> GetCurrentMemberIdAsync()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null) return null;
            var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
            return memberData?.Id;
        }

        /// <summary>
        /// Safely get MaxSeriesCount from a dynamic match object.
        /// Returns null if the property doesn't exist (e.g., migration hasn't run yet).
        /// </summary>
        private static int? GetMaxSeriesCount(dynamic match)
        {
            try
            {
                if (match is IDictionary<string, object> dict)
                {
                    if (dict.TryGetValue("MaxSeriesCount", out var value) && value != null)
                    {
                        return Convert.ToInt32(value);
                    }
                    return null;
                }
                // Fallback for other dynamic types
                var maxSeriesCount = match.MaxSeriesCount;
                return maxSeriesCount != null ? (int?)maxSeriesCount : null;
            }
            catch
            {
                // Property doesn't exist - migration hasn't run yet
                return null;
            }
        }

        #endregion

        #region Match Management

        /// <summary>
        /// Create a new training match
        /// POST /umbraco/surface/TrainingMatch/CreateMatch
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateMatch([FromBody] CreateMatchRequest? request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad för att skapa en match" });
            }

            // Handle null request (JSON deserialization failed)
            if (request == null)
            {
                request = new CreateMatchRequest();
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                // If creating a handicap match, require shooter class to be set
                if (request.HasHandicap)
                {
                    var shooterClass = member.GetValue<string>("precisionShooterClass");
                    if (string.IsNullOrEmpty(shooterClass))
                    {
                        return Json(new
                        {
                            success = false,
                            needsShooterClass = true,
                            message = "Du måste välja din skytteklass för att kunna skapa en handicapmatch"
                        });
                    }
                }

                // Generate unique match code
                var matchCode = TrainingMatch.GenerateMatchCode();

                // Ensure code is unique
                using (var db = _databaseFactory.CreateDatabase())
                {
                    int attempts = 0;
                    while (attempts < 10)
                    {
                        var existing = db.SingleOrDefault<dynamic>(
                            "SELECT Id FROM TrainingMatches WHERE MatchCode = @0", matchCode);
                        if (existing == null) break;
                        matchCode = TrainingMatch.GenerateMatchCode();
                        attempts++;
                    }

                    // Create the match
                    // If StartDate is provided and in the future, use it; otherwise default to now
                    // Convert to local time for comparison (ISO dates from client are UTC)
                    DateTime? requestStartLocal = null;
                    if (request.StartDate.HasValue)
                    {
                        requestStartLocal = request.StartDate.Value.Kind == DateTimeKind.Utc
                            ? request.StartDate.Value.ToLocalTime()
                            : request.StartDate.Value;
                    }

                    var startDate = requestStartLocal.HasValue && requestStartLocal.Value > DateTime.Now
                        ? requestStartLocal.Value
                        : DateTime.Now;

                    var weaponClass = request.WeaponClass ?? "C";
                    var hasHandicap = request.HasHandicap;

                    db.Insert("TrainingMatches", "Id", true, new
                    {
                        MatchCode = matchCode,
                        MatchName = request.MatchName ?? $"Match {startDate:yyyy-MM-dd HH:mm}",
                        CreatedByMemberId = member.Id,
                        WeaponClass = weaponClass,
                        CreatedDate = DateTime.Now,
                        StartDate = startDate,
                        Status = "Active",
                        IsOpen = request.IsOpen,
                        HasHandicap = hasHandicap
                    });

                    // Get the new match ID
                    var newMatch = db.SingleOrDefault<dynamic>(
                        "SELECT Id FROM TrainingMatches WHERE MatchCode = @0", matchCode);

                    if (newMatch != null)
                    {
                        // Calculate frozen handicap for creator if handicap is enabled
                        decimal? frozenHandicap = null;
                        bool? frozenIsProvisional = null;

                        if (hasHandicap)
                        {
                            // Recalculate statistics before getting handicap to ensure it's up-to-date
                            await _statisticsService.RecalculateFromHistoryAsync(member.Id, weaponClass);

                            var stats = await _statisticsService.GetStatisticsAsync(member.Id, weaponClass);
                            var shooterClass = member.GetValue<string>("precisionShooterClass");
                            var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);
                            frozenHandicap = profile.HandicapPerSeries;
                            frozenIsProvisional = profile.IsProvisional;
                        }

                        // Add creator as first participant
                        db.Insert("TrainingMatchParticipants", "Id", true, new
                        {
                            TrainingMatchId = (int)newMatch.Id,
                            MemberId = member.Id,
                            JoinedDate = DateTime.Now,
                            DisplayOrder = 0,
                            FrozenHandicapPerSeries = frozenHandicap,
                            FrozenIsProvisional = frozenIsProvisional
                        });

                        // Get creator name for notification
                        var creatorName = $"{member.GetValue<string>("firstName")} {member.GetValue<string>("lastName")}";

                        // Send push notification after scope is complete
                        try
                        {
                            await _pushNotificationService.SendMatchCreatedNotificationAsync(
                                matchCode,
                                request.MatchName ?? $"Match {startDate:yyyy-MM-dd HH:mm}",
                                creatorName,
                                weaponClass,
                                request.IsOpen);
                        }
                        catch
                        {
                            // Don't fail the request if notification fails
                        }
                    }

                    return Json(new
                    {
                        success = true,
                        matchCode = matchCode,
                        message = "Match skapad!"
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid skapande av match: " + ex.Message });
            }
        }

        /// <summary>
        /// Get match details with participants and scores
        /// GET /umbraco/surface/TrainingMatch/GetMatch?matchCode=ABC123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMatch(string matchCode)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            GuestSessionInfo? guestSession = null;

            // If not logged in as member, check for guest session
            if (currentMember == null)
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    guestSession = await GuestMatchController.ValidateGuestSession(matchCode ?? "", Request, db);
                }

                if (guestSession == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }
            }

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", matchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Get creator name
                    var creator = _memberService.GetById((int)match.CreatedByMemberId);
                    var creatorName = creator != null
                        ? $"{creator.GetValue<string>("firstName")} {creator.GetValue<string>("lastName")}"
                        : "Okänd";

                    // Check if match has handicap enabled
                    bool hasHandicap = match.HasHandicap != null && (bool)match.HasHandicap;

                    // Get participants
                    var participants = db.Fetch<dynamic>(
                        @"SELECT * FROM TrainingMatchParticipants
                          WHERE TrainingMatchId = @0
                          ORDER BY DisplayOrder", (int)match.Id);

                    var participantList = new List<object>();
                    foreach (var p in participants)
                    {
                        string firstName, lastName, profilePictureUrl;
                        int? memberId = p.MemberId != null ? (int?)p.MemberId : null;

                        if (memberId.HasValue)
                        {
                            // Regular member
                            var pMember = _memberService.GetById(memberId.Value);
                            firstName = pMember?.GetValue<string>("firstName") ?? "";
                            lastName = pMember?.GetValue<string>("lastName") ?? "";
                            profilePictureUrl = pMember?.GetValue<string>("profilePictureUrl") ?? "";
                        }
                        else
                        {
                            // Guest participant - get name from GuestParticipantId
                            firstName = "";
                            lastName = "";
                            profilePictureUrl = "";
                            if (p.GuestParticipantId != null)
                            {
                                var guest = db.SingleOrDefault<dynamic>(
                                    "SELECT DisplayName FROM TrainingMatchGuests WHERE Id = @0",
                                    (int)p.GuestParticipantId);
                                if (guest != null)
                                {
                                    var displayName = (string?)guest.DisplayName ?? "Gäst";
                                    var nameParts = displayName.Split(' ', 2);
                                    firstName = nameParts[0];
                                    lastName = nameParts.Length > 1 ? nameParts[1] : "";
                                }
                            }
                        }

                        // Get the single score row for this participant in this match
                        // For guests, we use GuestParticipantId; for members, we use MemberId
                        dynamic? scoreRow = null;
                        if (memberId.HasValue)
                        {
                            scoreRow = db.SingleOrDefault<dynamic>(
                                @"SELECT ts.Id, ts.SeriesScores, ts.TotalScore, ts.XCount
                                  FROM TrainingScores ts
                                  WHERE ts.MemberId = @0 AND ts.TrainingMatchId = @1",
                                memberId.Value, (int)match.Id);
                        }
                        else if (p.GuestParticipantId != null)
                        {
                            scoreRow = db.SingleOrDefault<dynamic>(
                                @"SELECT ts.Id, ts.SeriesScores, ts.TotalScore, ts.XCount
                                  FROM TrainingScores ts
                                  WHERE ts.GuestParticipantId = @0 AND ts.TrainingMatchId = @1",
                                (int)p.GuestParticipantId, (int)match.Id);
                        }

                        var scoreList = new List<object>();
                        if (scoreRow != null)
                        {
                            // Parse series scores JSON to get individual series
                            var seriesJson = (string)scoreRow.SeriesScores;
                            if (!string.IsNullOrEmpty(seriesJson))
                            {
                                try
                                {
                                    var seriesList = JsonSerializer.Deserialize<List<TrainingSeries>>(seriesJson);
                                    if (seriesList != null)
                                    {
                                        foreach (var s in seriesList)
                                        {
                                            scoreList.Add(new
                                            {
                                                id = (int)scoreRow.Id,
                                                seriesNumber = s.SeriesNumber,
                                                total = s.Total,
                                                xCount = s.XCount,
                                                shots = s.Shots,
                                                entryMethod = s.EntryMethod,
                                                targetPhotoUrl = s.TargetPhotoUrl,
                                                reactions = s.Reactions
                                            });
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        // Get frozen handicap data
                        decimal? frozenHandicap = p.FrozenHandicapPerSeries != null ? (decimal?)p.FrozenHandicapPerSeries : null;
                        bool? frozenIsProvisional = p.FrozenIsProvisional != null ? (bool?)p.FrozenIsProvisional : null;
                        int rawTotalScore = scoreList.Sum(s => (int)((dynamic)s).total);
                        int seriesCount = scoreList.Count;

                        // Calculate final score with handicap overlay (if handicap enabled)
                        decimal? finalScore = null;
                        decimal? handicapTotal = null;
                        if (hasHandicap && frozenHandicap.HasValue)
                        {
                            handicapTotal = frozenHandicap.Value * seriesCount;
                            finalScore = _handicapCalculator.GetMatchFinalScore(rawTotalScore, frozenHandicap.Value, seriesCount);
                        }

                        participantList.Add(new
                        {
                            id = (int)p.Id,
                            memberId = memberId,
                            guestParticipantId = p.GuestParticipantId != null ? (int?)p.GuestParticipantId : null,
                            isGuest = !memberId.HasValue,
                            firstName = firstName,
                            lastName = lastName,
                            profilePictureUrl = profilePictureUrl,
                            displayOrder = (int)p.DisplayOrder,
                            scores = scoreList,
                            totalScore = rawTotalScore,
                            seriesCount = seriesCount,
                            // Handicap overlay fields (only meaningful when hasHandicap is true)
                            handicapPerSeries = frozenHandicap,
                            isProvisional = frozenIsProvisional,
                            handicapTotal = handicapTotal,
                            finalScore = finalScore
                        });
                    }

                    // Calculate hasStarted
                    var startDate = match.StartDate != null ? (DateTime?)match.StartDate : null;
                    var hasStarted = startDate == null || startDate <= DateTime.Now;

                    // Get current member ID so client can refresh its context
                    // For guests, this will be null (they're not members)
                    var serverMemberId = await GetCurrentMemberIdAsync();

                    return Json(new
                    {
                        success = true,
                        currentMemberId = serverMemberId,
                        // Include guest info for guest users
                        isGuestSession = guestSession != null,
                        guestId = guestSession?.GuestId,
                        guestName = guestSession?.DisplayName,
                        match = new
                        {
                            id = (int)match.Id,
                            matchCode = (string)match.MatchCode,
                            matchName = (string?)match.MatchName,
                            createdByMemberId = (int)match.CreatedByMemberId,
                            createdByName = creatorName,
                            weaponClass = (string)match.WeaponClass,
                            createdDate = (DateTime)match.CreatedDate,
                            startDate = startDate,
                            hasStarted = hasStarted,
                            status = (string)match.Status,
                            completedDate = match.CompletedDate != null ? (DateTime?)match.CompletedDate : null,
                            hasHandicap = hasHandicap,
                            maxSeriesCount = GetMaxSeriesCount(match),
                            participants = participantList
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av match: " + ex.Message });
            }
        }

        /// <summary>
        /// Join an existing match
        /// POST /umbraco/surface/TrainingMatch/JoinMatch
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> JoinMatch([FromBody] JoinMatchRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad för att gå med i en match" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode?.ToUpper());

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match med kod '" + request.MatchCode + "' hittades inte" });
                    }

                    if ((string)match.Status != "Active")
                    {
                        return Json(new { success = false, message = "Matchen är avslutad" });
                    }

                    // NOTE: Users CAN join before start time, they just can't enter scores until it starts

                    // Check if already a participant
                    var existing = db.SingleOrDefault<dynamic>(
                        @"SELECT Id FROM TrainingMatchParticipants
                          WHERE TrainingMatchId = @0 AND MemberId = @1",
                        (int)match.Id, member.Id);

                    if (existing != null)
                    {
                        return Json(new { success = true, message = "Du är redan med i matchen", matchCode = request.MatchCode });
                    }

                    // Get current max display order
                    var maxOrder = db.SingleOrDefault<int?>(
                        "SELECT MAX(DisplayOrder) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0",
                        (int)match.Id) ?? -1;

                    // Calculate frozen handicap if match has handicap enabled
                    decimal? frozenHandicap = null;
                    bool? frozenIsProvisional = null;
                    bool hasHandicap = match.HasHandicap != null && (bool)match.HasHandicap;

                    if (hasHandicap)
                    {
                        var shooterClass = member.GetValue<string>("precisionShooterClass");

                        // If handicap is enabled but user has no shooter class, require them to set it first
                        if (string.IsNullOrEmpty(shooterClass))
                        {
                            return Json(new
                            {
                                success = false,
                                needsShooterClass = true,
                                message = "Du måste välja din skytteklass för att kunna gå med i en handicapmatch"
                            });
                        }

                        var weaponClass = (string)match.WeaponClass;

                        // Recalculate statistics before getting handicap to ensure it's up-to-date
                        await _statisticsService.RecalculateFromHistoryAsync(member.Id, weaponClass);

                        var stats = await _statisticsService.GetStatisticsAsync(member.Id, weaponClass);
                        var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);
                        frozenHandicap = profile.HandicapPerSeries;
                        frozenIsProvisional = profile.IsProvisional;
                    }

                    // Add participant
                    db.Insert("TrainingMatchParticipants", "Id", true, new
                    {
                        TrainingMatchId = (int)match.Id,
                        MemberId = member.Id,
                        JoinedDate = DateTime.Now,
                        DisplayOrder = maxOrder + 1,
                        FrozenHandicapPerSeries = frozenHandicap,
                        FrozenIsProvisional = frozenIsProvisional
                    });

                    // Notify other participants via SignalR
                    await _hubContext.SendParticipantJoined((string)match.MatchCode, new
                    {
                        memberId = member.Id,
                        firstName = member.GetValue<string>("firstName") ?? "",
                        lastName = member.GetValue<string>("lastName") ?? "",
                        profilePictureUrl = member.GetValue<string>("profilePictureUrl") ?? "",
                        handicap = frozenHandicap,
                        isProvisional = frozenIsProvisional
                    });

                    return Json(new
                    {
                        success = true,
                        matchCode = (string)match.MatchCode,
                        message = "Du har gått med i matchen!"
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid anslutning till match: " + ex.Message });
            }
        }

        /// <summary>
        /// Leave a match
        /// POST /umbraco/surface/TrainingMatch/LeaveMatch
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> LeaveMatch([FromBody] LeaveMatchRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Check if creator - creator cannot leave
                    if ((int)match.CreatedByMemberId == member.Id)
                    {
                        return Json(new { success = false, message = "Matchskaparen kan inte lämna matchen. Avsluta matchen istället." });
                    }

                    // Delete participant record
                    db.Execute(
                        @"DELETE FROM TrainingMatchParticipants
                          WHERE TrainingMatchId = @0 AND MemberId = @1",
                        (int)match.Id, member.Id);

                    // Also delete any scores for this match
                    db.Execute(
                        @"DELETE FROM TrainingScores
                          WHERE TrainingMatchId = @0 AND MemberId = @1",
                        (int)match.Id, member.Id);

                    // Notify other participants via SignalR
                    await _hubContext.SendParticipantLeft(request.MatchCode ?? "", member.Id);

                    return Json(new { success = true, message = "Du har lämnat matchen" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid lämning av match: " + ex.Message });
            }
        }

        /// <summary>
        /// Complete/end a match
        /// POST /umbraco/surface/TrainingMatch/CompleteMatch
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CompleteMatch([FromBody] CompleteMatchRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Check if user is creator or admin
                    bool isCreator = (int)match.CreatedByMemberId == member.Id;
                    bool isAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                    if (!isCreator && !isAdmin)
                    {
                        return Json(new { success = false, message = "Endast matchskaparen eller administratör kan avsluta matchen" });
                    }

                    // Update match status
                    db.Execute(
                        @"UPDATE TrainingMatches
                          SET Status = 'Completed', CompletedDate = @0
                          WHERE Id = @1",
                        DateTime.Now, (int)match.Id);

                    // Recalculate statistics for all participants who have scores in this match
                    string weaponClass = (string)match.WeaponClass;
                    int matchId = (int)match.Id;

                    var participants = db.Fetch<dynamic>(
                        @"SELECT DISTINCT MemberId,
                          CASE
                            WHEN SeriesScores IS NOT NULL AND ISJSON(SeriesScores) = 1
                            THEN (SELECT COUNT(*) FROM OPENJSON(SeriesScores))
                            ELSE 0
                          END AS SeriesCount,
                          TotalScore
                          FROM TrainingScores
                          WHERE TrainingMatchId = @0",
                        matchId);

                    foreach (var participant in participants)
                    {
                        int participantMemberId = (int)participant.MemberId;
                        int seriesCount = participant.SeriesCount ?? 0;
                        decimal totalScore = participant.TotalScore != null ? Convert.ToDecimal(participant.TotalScore) : 0m;

                        await _statisticsService.UpdateAfterMatchAsync(
                            participantMemberId,
                            weaponClass,
                            seriesCount,
                            totalScore);
                    }

                    // Notify all viewers via SignalR
                    await _hubContext.SendMatchCompleted(request.MatchCode ?? "");

                    return Json(new { success = true, message = "Matchen har avslutats" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid avslutning av match: " + ex.Message });
            }
        }

        /// <summary>
        /// Update match settings (max series count)
        /// POST /umbraco/surface/TrainingMatch/UpdateMatchSettings
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateMatchSettings([FromBody] UpdateMatchSettingsRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Check if user is creator or admin
                    bool isCreator = (int)match.CreatedByMemberId == member.Id;
                    bool isAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                    if (!isCreator && !isAdmin)
                    {
                        return Json(new { success = false, message = "Endast matchskaparen kan ändra inställningar" });
                    }

                    // Update match settings
                    db.Execute(
                        @"UPDATE TrainingMatches
                          SET MaxSeriesCount = @0,
                              AllowGuests = COALESCE(@2, AllowGuests)
                          WHERE Id = @1",
                        request.MaxSeriesCount, (int)match.Id, request.AllowGuests);

                    // Notify all viewers via SignalR
                    await _hubContext.SendSettingsUpdated(request.MatchCode ?? "", request.MaxSeriesCount, request.AllowGuests);

                    return Json(new { success = true, message = "Inställningar sparade" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid uppdatering av inställningar: " + ex.Message });
            }
        }

        #endregion

        #region Score Entry

        /// <summary>
        /// Save a series score for current user in a match
        /// POST /umbraco/surface/TrainingMatch/SaveScore
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveScore([FromBody] SaveMatchScoreRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            GuestSessionInfo? guestSession = null;
            int? memberId = null;
            int? guestParticipantId = null;

            // Check if logged in member or guest
            if (currentMember != null)
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }
                memberId = member.Id;
            }
            else
            {
                // Check for guest session
                using (var guestDb = _databaseFactory.CreateDatabase())
                {
                    guestSession = await GuestMatchController.ValidateGuestSession(request.MatchCode ?? "", Request, guestDb);
                }

                if (guestSession == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }
                guestParticipantId = guestSession.GuestId;
            }

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    if ((string)match.Status != "Active")
                    {
                        return Json(new { success = false, message = "Matchen är avslutad" });
                    }

                    // Verify user is a participant (member or guest)
                    dynamic? participant = null;
                    if (memberId.HasValue)
                    {
                        participant = db.SingleOrDefault<dynamic>(
                            @"SELECT Id FROM TrainingMatchParticipants
                              WHERE TrainingMatchId = @0 AND MemberId = @1",
                            (int)match.Id, memberId.Value);
                    }
                    else if (guestParticipantId.HasValue)
                    {
                        participant = db.SingleOrDefault<dynamic>(
                            @"SELECT Id FROM TrainingMatchParticipants
                              WHERE TrainingMatchId = @0 AND GuestParticipantId = @1",
                            (int)match.Id, guestParticipantId.Value);
                    }

                    if (participant == null)
                    {
                        return Json(new { success = false, message = "Du är inte med i denna match" });
                    }

                    // Check if match has started (only creator can add scores before start time)
                    // Guests can never be creators, so they must wait for start time
                    bool isCreator = memberId.HasValue && (int)match.CreatedByMemberId == memberId.Value;
                    if (!isCreator && match.StartDate != null && (DateTime)match.StartDate > DateTime.Now)
                    {
                        var startDateFormatted = ((DateTime)match.StartDate).ToString("yyyy-MM-dd HH:mm");
                        return Json(new { success = false, message = $"Matchen har inte startat ännu. Startar {startDateFormatted}" });
                    }

                    // Create series data
                    var newSeries = new TrainingSeries
                    {
                        SeriesNumber = request.SeriesNumber,
                        Total = request.Total,
                        XCount = request.XCount,
                        Shots = request.Shots,
                        EntryMethod = request.EntryMethod ?? "SeriesTotal"
                    };

                    // If shots provided, recalculate total
                    if (newSeries.Shots != null && newSeries.Shots.Count > 0)
                    {
                        newSeries.CalculateScore();
                    }

                    // Check if participant already has a TrainingScores row for this match
                    dynamic? existingScore = null;
                    if (memberId.HasValue)
                    {
                        existingScore = db.SingleOrDefault<dynamic>(
                            @"SELECT * FROM TrainingScores
                              WHERE MemberId = @0 AND TrainingMatchId = @1",
                            memberId.Value, (int)match.Id);
                    }
                    else if (guestParticipantId.HasValue)
                    {
                        existingScore = db.SingleOrDefault<dynamic>(
                            @"SELECT * FROM TrainingScores
                              WHERE GuestParticipantId = @0 AND TrainingMatchId = @1",
                            guestParticipantId.Value, (int)match.Id);
                    }

                    if (existingScore != null)
                    {
                        // UPDATE existing row - add series to JSON array
                        var existingSeriesJson = (string)existingScore.SeriesScores ?? "[]";
                        var seriesList = JsonSerializer.Deserialize<List<TrainingSeries>>(existingSeriesJson)
                            ?? new List<TrainingSeries>();

                        // Set correct series number based on existing count
                        newSeries.SeriesNumber = seriesList.Count + 1;
                        seriesList.Add(newSeries);

                        // Recalculate totals
                        int totalScore = seriesList.Sum(s => s.Total);
                        int totalXCount = seriesList.Sum(s => s.XCount);

                        var updatedSeriesJson = JsonSerializer.Serialize(seriesList);

                        db.Execute(
                            @"UPDATE TrainingScores
                              SET SeriesScores = @0, TotalScore = @1, XCount = @2, UpdatedAt = @3
                              WHERE Id = @4",
                            updatedSeriesJson, totalScore, totalXCount, DateTime.Now, (int)existingScore.Id);

                        // Notify other participants via SignalR
                        await _hubContext.SendScoreUpdated(request.MatchCode ?? "", new
                        {
                            memberId = memberId,
                            guestId = guestParticipantId,
                            seriesNumber = newSeries.SeriesNumber,
                            total = newSeries.Total,
                            xCount = newSeries.XCount
                        });

                        return Json(new
                        {
                            success = true,
                            message = "Serie sparad!",
                            series = new
                            {
                                seriesNumber = newSeries.SeriesNumber,
                                total = newSeries.Total,
                                xCount = newSeries.XCount
                            },
                            totalSeriesCount = seriesList.Count
                        });
                    }
                    else
                    {
                        // INSERT new row with first series
                        newSeries.SeriesNumber = 1;
                        var seriesJson = JsonSerializer.Serialize(new List<TrainingSeries> { newSeries });

                        // Insert with either MemberId or GuestParticipantId
                        if (memberId.HasValue)
                        {
                            db.Insert("TrainingScores", "Id", true, new
                            {
                                MemberId = memberId.Value,
                                TrainingDate = DateTime.Now,
                                WeaponClass = (string)match.WeaponClass,
                                IsCompetition = false,
                                SeriesScores = seriesJson,
                                TotalScore = newSeries.Total,
                                XCount = newSeries.XCount,
                                Notes = $"Träningsmatch: {match.MatchName}",
                                CreatedAt = DateTime.Now,
                                UpdatedAt = DateTime.Now,
                                TrainingMatchId = (int)match.Id
                            });
                        }
                        else if (guestParticipantId.HasValue)
                        {
                            db.Insert("TrainingScores", "Id", true, new
                            {
                                GuestParticipantId = guestParticipantId.Value,
                                TrainingDate = DateTime.Now,
                                WeaponClass = (string)match.WeaponClass,
                                IsCompetition = false,
                                SeriesScores = seriesJson,
                                TotalScore = newSeries.Total,
                                XCount = newSeries.XCount,
                                Notes = $"Träningsmatch: {match.MatchName}",
                                CreatedAt = DateTime.Now,
                                UpdatedAt = DateTime.Now,
                                TrainingMatchId = (int)match.Id
                            });
                        }

                        // Notify other participants via SignalR
                        await _hubContext.SendScoreUpdated(request.MatchCode ?? "", new
                        {
                            memberId = memberId,
                            guestId = guestParticipantId,
                            seriesNumber = newSeries.SeriesNumber,
                            total = newSeries.Total,
                            xCount = newSeries.XCount
                        });

                        return Json(new
                        {
                            success = true,
                            message = "Serie sparad!",
                            series = new
                            {
                                seriesNumber = newSeries.SeriesNumber,
                                total = newSeries.Total,
                                xCount = newSeries.XCount
                            },
                            totalSeriesCount = 1
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid sparande av serie: " + ex.Message });
            }
        }

        /// <summary>
        /// Update an existing series score for current user in a match
        /// POST /umbraco/surface/TrainingMatch/UpdateScore
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateScore([FromBody] UpdateMatchScoreRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            GuestSessionInfo? guestSession = null;
            int? memberId = null;
            int? guestParticipantId = null;

            // Check if logged in member or guest
            if (currentMember != null)
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }
                memberId = member.Id;
            }
            else
            {
                // Check for guest session
                using (var guestDb = _databaseFactory.CreateDatabase())
                {
                    guestSession = await GuestMatchController.ValidateGuestSession(request.MatchCode ?? "", Request, guestDb);
                }

                if (guestSession == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }
                guestParticipantId = guestSession.GuestId;
            }

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    if ((string)match.Status != "Active")
                    {
                        return Json(new { success = false, message = "Matchen är avslutad - kan inte redigera resultat" });
                    }

                    // Check if match has started (only creator can edit scores before start time)
                    bool isCreator = memberId.HasValue && (int)match.CreatedByMemberId == memberId.Value;
                    if (!isCreator && match.StartDate != null && (DateTime)match.StartDate > DateTime.Now)
                    {
                        var startDateFormatted = ((DateTime)match.StartDate).ToString("yyyy-MM-dd HH:mm");
                        return Json(new { success = false, message = $"Matchen har inte startat ännu. Startar {startDateFormatted}" });
                    }

                    // Get existing score row (for member or guest)
                    dynamic? existingScore = null;
                    if (memberId.HasValue)
                    {
                        existingScore = db.SingleOrDefault<dynamic>(
                            @"SELECT * FROM TrainingScores
                              WHERE MemberId = @0 AND TrainingMatchId = @1",
                            memberId.Value, (int)match.Id);
                    }
                    else if (guestParticipantId.HasValue)
                    {
                        existingScore = db.SingleOrDefault<dynamic>(
                            @"SELECT * FROM TrainingScores
                              WHERE GuestParticipantId = @0 AND TrainingMatchId = @1",
                            guestParticipantId.Value, (int)match.Id);
                    }

                    if (existingScore == null)
                    {
                        return Json(new { success = false, message = "Inget resultat att redigera" });
                    }

                    // Parse existing series
                    var existingSeriesJson = (string)existingScore.SeriesScores ?? "[]";
                    var seriesList = JsonSerializer.Deserialize<List<TrainingSeries>>(existingSeriesJson)
                        ?? new List<TrainingSeries>();

                    // Find the series to update
                    var seriesIndex = seriesList.FindIndex(s => s.SeriesNumber == request.SeriesNumber);
                    if (seriesIndex < 0)
                    {
                        return Json(new { success = false, message = $"Serie {request.SeriesNumber} hittades inte" });
                    }

                    // Create updated series
                    var updatedSeries = new TrainingSeries
                    {
                        SeriesNumber = request.SeriesNumber,
                        Total = request.Total,
                        XCount = request.XCount,
                        Shots = request.Shots,
                        EntryMethod = request.EntryMethod ?? seriesList[seriesIndex].EntryMethod
                    };

                    // Recalculate if shots provided
                    if (updatedSeries.Shots != null && updatedSeries.Shots.Count > 0)
                    {
                        updatedSeries.CalculateScore();
                    }

                    // Replace the series
                    seriesList[seriesIndex] = updatedSeries;

                    // Recalculate totals
                    int totalScore = seriesList.Sum(s => s.Total);
                    int totalXCount = seriesList.Sum(s => s.XCount);

                    var updatedSeriesJson = JsonSerializer.Serialize(seriesList);

                    db.Execute(
                        @"UPDATE TrainingScores
                          SET SeriesScores = @0, TotalScore = @1, XCount = @2, UpdatedAt = @3
                          WHERE Id = @4",
                        updatedSeriesJson, totalScore, totalXCount, DateTime.Now, (int)existingScore.Id);

                    // Notify other participants via SignalR
                    await _hubContext.SendScoreUpdated(request.MatchCode ?? "", new
                    {
                        memberId = memberId,
                        guestId = guestParticipantId,
                        seriesNumber = updatedSeries.SeriesNumber,
                        total = updatedSeries.Total,
                        xCount = updatedSeries.XCount
                    });

                    return Json(new
                    {
                        success = true,
                        message = "Serie uppdaterad!",
                        series = new
                        {
                            seriesNumber = updatedSeries.SeriesNumber,
                            total = updatedSeries.Total,
                            xCount = updatedSeries.XCount,
                            shots = updatedSeries.Shots
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid uppdatering av serie: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a specific series from a match score
        /// POST /umbraco/surface/TrainingMatch/DeleteSeries
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteSeries([FromBody] DeleteMatchSeriesRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            GuestSessionInfo? guestSession = null;
            int? memberId = null;
            int? guestParticipantId = null;

            // Check if logged in member or guest
            if (currentMember != null)
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }
                memberId = member.Id;
            }
            else
            {
                // Check for guest session
                using (var guestDb = _databaseFactory.CreateDatabase())
                {
                    guestSession = await GuestMatchController.ValidateGuestSession(request.MatchCode ?? "", Request, guestDb);
                }

                if (guestSession == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }
                guestParticipantId = guestSession.GuestId;
            }

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    if ((string)match.Status != "Active")
                    {
                        return Json(new { success = false, message = "Matchen är avslutad - kan inte ta bort resultat" });
                    }

                    // Get existing score row (for member or guest)
                    dynamic? existingScore = null;
                    if (memberId.HasValue)
                    {
                        existingScore = db.SingleOrDefault<dynamic>(
                            @"SELECT * FROM TrainingScores
                              WHERE MemberId = @0 AND TrainingMatchId = @1",
                            memberId.Value, (int)match.Id);
                    }
                    else if (guestParticipantId.HasValue)
                    {
                        existingScore = db.SingleOrDefault<dynamic>(
                            @"SELECT * FROM TrainingScores
                              WHERE GuestParticipantId = @0 AND TrainingMatchId = @1",
                            guestParticipantId.Value, (int)match.Id);
                    }

                    if (existingScore == null)
                    {
                        return Json(new { success = false, message = "Inget resultat att ta bort" });
                    }

                    // Parse existing series
                    var existingSeriesJson = (string)existingScore.SeriesScores ?? "[]";
                    var seriesList = JsonSerializer.Deserialize<List<TrainingSeries>>(existingSeriesJson)
                        ?? new List<TrainingSeries>();

                    // Find and remove the series
                    var seriesIndex = seriesList.FindIndex(s => s.SeriesNumber == request.SeriesNumber);
                    if (seriesIndex < 0)
                    {
                        return Json(new { success = false, message = $"Serie {request.SeriesNumber} hittades inte" });
                    }

                    seriesList.RemoveAt(seriesIndex);

                    // Renumber remaining series
                    for (int i = 0; i < seriesList.Count; i++)
                    {
                        seriesList[i].SeriesNumber = i + 1;
                    }

                    if (seriesList.Count == 0)
                    {
                        // Delete the entire score row if no series left
                        db.Execute("DELETE FROM TrainingScores WHERE Id = @0", (int)existingScore.Id);
                    }
                    else
                    {
                        // Recalculate totals
                        int totalScore = seriesList.Sum(s => s.Total);
                        int totalXCount = seriesList.Sum(s => s.XCount);

                        var updatedSeriesJson = JsonSerializer.Serialize(seriesList);

                        db.Execute(
                            @"UPDATE TrainingScores
                              SET SeriesScores = @0, TotalScore = @1, XCount = @2, UpdatedAt = @3
                              WHERE Id = @4",
                            updatedSeriesJson, totalScore, totalXCount, DateTime.Now, (int)existingScore.Id);
                    }

                    // Notify other participants via SignalR
                    await _hubContext.SendScoreUpdated(request.MatchCode ?? "", new
                    {
                        memberId = memberId,
                        guestId = guestParticipantId,
                        seriesNumber = request.SeriesNumber,
                        deleted = true
                    });

                    return Json(new
                    {
                        success = true,
                        message = "Serie borttagen!",
                        remainingSeriesCount = seriesList.Count
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid borttagning av serie: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a score entry
        /// POST /umbraco/surface/TrainingMatch/DeleteScore
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteScore([FromBody] DeleteScoreRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Verify the score belongs to current user
                    var score = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingScores WHERE Id = @0 AND MemberId = @1",
                        request.ScoreId, member.Id);

                    if (score == null)
                    {
                        return Json(new { success = false, message = "Resultat hittades inte eller tillhör inte dig" });
                    }

                    db.Execute("DELETE FROM TrainingScores WHERE Id = @0", request.ScoreId);

                    return Json(new { success = true, message = "Resultat borttaget" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid borttagning: " + ex.Message });
            }
        }

        #endregion

        #region Member Profile

        /// <summary>
        /// Set the shooter class for the current member
        /// POST /umbraco/surface/TrainingMatch/SetShooterClass
        /// Used when joining a handicap match and the user hasn't set their class yet
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetShooterClass([FromBody] SetShooterClassRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                // Validate the shooter class
                var validClasses = new[] {
                    "Klass 1 - Nybörjare",
                    "Klass 2 - Guldmärkesskytt",
                    "Klass 3 - Riksmästare"
                };

                if (string.IsNullOrEmpty(request.ShooterClass) || !validClasses.Contains(request.ShooterClass))
                {
                    return Json(new { success = false, message = "Ogiltig skytteklass" });
                }

                // Save to member profile
                member.SetValue("precisionShooterClass", request.ShooterClass);
                _memberService.Save(member);

                return Json(new
                {
                    success = true,
                    message = "Skytteklass sparad",
                    shooterClass = request.ShooterClass
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid sparande: " + ex.Message });
            }
        }

        #endregion

        #region User Matches

        /// <summary>
        /// Get match history with pagination and filters
        /// GET /umbraco/surface/TrainingMatch/GetMatchHistory
        /// Shows ALL completed matches by default, with optional filters
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMatchHistory(
            int page = 1,
            int pageSize = 20,
            string? weaponClass = null,
            string? dateFrom = null,
            string? dateTo = null,
            string? searchName = null,
            bool myMatchesOnly = false)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Build dynamic WHERE clause
                    var whereConditions = new List<string> { "tm.Status = 'Completed'" };
                    var parameters = new List<object>();
                    int paramIndex = 0;

                    // Filter: My matches only
                    if (myMatchesOnly)
                    {
                        whereConditions.Add($"EXISTS (SELECT 1 FROM TrainingMatchParticipants tmp WHERE tmp.TrainingMatchId = tm.Id AND tmp.MemberId = @{paramIndex})");
                        parameters.Add(member.Id);
                        paramIndex++;
                    }

                    // Filter: Weapon class
                    if (!string.IsNullOrEmpty(weaponClass))
                    {
                        whereConditions.Add($"tm.WeaponClass = @{paramIndex}");
                        parameters.Add(weaponClass);
                        paramIndex++;
                    }

                    // Filter: Date from
                    if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var fromDate))
                    {
                        whereConditions.Add($"tm.CompletedDate >= @{paramIndex}");
                        parameters.Add(fromDate.Date);
                        paramIndex++;
                    }

                    // Filter: Date to
                    if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var toDate))
                    {
                        whereConditions.Add($"tm.CompletedDate < @{paramIndex}");
                        parameters.Add(toDate.Date.AddDays(1)); // Include the entire day
                        paramIndex++;
                    }

                    // Filter: Search by name
                    if (!string.IsNullOrEmpty(searchName))
                    {
                        whereConditions.Add($"(tm.MatchName LIKE @{paramIndex} OR tm.MatchCode LIKE @{paramIndex})");
                        parameters.Add($"%{searchName}%");
                        paramIndex++;
                    }

                    var whereClause = string.Join(" AND ", whereConditions);

                    // Get total count for pagination
                    var countSql = $@"SELECT COUNT(*) FROM TrainingMatches tm WHERE {whereClause}";
                    var totalCount = db.ExecuteScalar<int>(countSql, parameters.ToArray());

                    // Calculate offset
                    var offset = (page - 1) * pageSize;

                    // Get paginated matches
                    var matchesSql = $@"SELECT tm.*,
                                 (SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = tm.Id) as ParticipantCount
                          FROM TrainingMatches tm
                          WHERE {whereClause}
                          ORDER BY tm.CompletedDate DESC, tm.CreatedDate DESC
                          OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY";

                    var matches = db.Fetch<dynamic>(matchesSql, parameters.ToArray());

                    var matchList = new List<object>();
                    foreach (var m in matches)
                    {
                        // Get participants for this match (limited to 5 for display)
                        var participants = db.Fetch<dynamic>(
                            @"SELECT TOP 5 tmp.MemberId, tmp.GuestParticipantId FROM TrainingMatchParticipants tmp
                              WHERE tmp.TrainingMatchId = @0
                              ORDER BY tmp.DisplayOrder", (int)m.Id);

                        var participantDetails = new List<object>();
                        foreach (var p in participants)
                        {
                            if (p.MemberId != null)
                            {
                                // Regular member
                                var pMember = _memberService.GetById((int)p.MemberId);
                                var firstName = pMember?.GetValue<string>("firstName") ?? "";
                                var lastName = pMember?.GetValue<string>("lastName") ?? "";
                                var profilePictureUrl = pMember?.GetValue<string>("profilePictureUrl") ?? "";

                                participantDetails.Add(new
                                {
                                    memberId = (int)p.MemberId,
                                    firstName = firstName,
                                    lastName = lastName,
                                    profilePictureUrl = profilePictureUrl,
                                    isGuest = false
                                });
                            }
                            else if (p.GuestParticipantId != null)
                            {
                                // Guest participant
                                var guest = db.FirstOrDefault<dynamic>(
                                    "SELECT DisplayName FROM TrainingMatchGuests WHERE Id = @0",
                                    (int)p.GuestParticipantId);
                                var displayName = guest?.DisplayName ?? "Gäst";
                                var nameParts = displayName.Split(' ', 2);

                                participantDetails.Add(new
                                {
                                    memberId = 0,
                                    firstName = nameParts.Length > 0 ? nameParts[0] : "Gäst",
                                    lastName = nameParts.Length > 1 ? nameParts[1] : "",
                                    profilePictureUrl = "",
                                    isGuest = true
                                });
                            }
                        }

                        // Check if current user is a participant
                        var isParticipant = db.ExecuteScalar<int>(
                            @"SELECT COUNT(*) FROM TrainingMatchParticipants
                              WHERE TrainingMatchId = @0 AND MemberId = @1",
                            (int)m.Id, member.Id) > 0;

                        // Get user's score and ranking only if participant
                        int? userTotalScore = null;
                        int? userRanking = null;
                        int? userSeriesCount = null;

                        if (isParticipant)
                        {
                            // Fetch all scores with series data for equalized calculation
                            var allScoresWithSeries = db.Fetch<dynamic>(
                                @"SELECT ts.MemberId, ts.TotalScore, ts.SeriesScores
                                  FROM TrainingScores ts
                                  WHERE ts.TrainingMatchId = @0", (int)m.Id);

                            // Calculate series counts and find minimum
                            var participantData = new List<(int MemberId, int SeriesCount, List<int> SeriesTotals)>();

                            foreach (var score in allScoresWithSeries)
                            {
                                // Skip entries with null MemberId (guest scores are handled separately)
                                if (score.MemberId == null) continue;

                                var seriesTotals = new List<int>();
                                if (score.SeriesScores != null)
                                {
                                    try
                                    {
                                        var seriesJson = (string)score.SeriesScores;
                                        var seriesArray = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(seriesJson);
                                        foreach (var series in seriesArray.EnumerateArray())
                                        {
                                            if (series.TryGetProperty("total", out var totalProp))
                                            {
                                                seriesTotals.Add(totalProp.GetInt32());
                                            }
                                        }
                                    }
                                    catch { }
                                }
                                participantData.Add(((int)score.MemberId, seriesTotals.Count, seriesTotals));
                            }

                            // Find minimum series count (only from participants with scores)
                            var participantsWithScores = participantData.Where(p => p.SeriesCount > 0).ToList();
                            int minSeriesCount = participantsWithScores.Any()
                                ? participantsWithScores.Min(p => p.SeriesCount)
                                : 0;

                            // Calculate equalized scores and rank
                            var equalizedScores = participantData
                                .Select(p => new {
                                    MemberId = p.MemberId,
                                    EqualizedScore = p.SeriesTotals.Take(minSeriesCount).Sum(),
                                    SeriesCount = p.SeriesCount
                                })
                                .OrderByDescending(p => p.EqualizedScore)
                                .ToList();

                            // Find user's data
                            var userData = equalizedScores.FirstOrDefault(p => p.MemberId == member.Id);
                            if (userData != null)
                            {
                                userTotalScore = userData.EqualizedScore;
                                userSeriesCount = minSeriesCount; // Show the equalized series count
                                userRanking = equalizedScores.FindIndex(p => p.MemberId == member.Id) + 1;
                            }
                        }

                        matchList.Add(new
                        {
                            id = (int)m.Id,
                            matchCode = (string)m.MatchCode,
                            matchName = (string?)m.MatchName,
                            weaponClass = (string)m.WeaponClass,
                            createdDate = (DateTime)m.CreatedDate,
                            completedDate = m.CompletedDate != null ? (DateTime?)m.CompletedDate : null,
                            participantCount = (int)m.ParticipantCount,
                            participants = participantDetails,
                            isCreator = (int)m.CreatedByMemberId == member.Id,
                            isParticipant = isParticipant,
                            userScore = userTotalScore,
                            userSeriesCount = userSeriesCount,
                            userRanking = userRanking
                        });
                    }

                    return Json(new
                    {
                        success = true,
                        matches = matchList,
                        totalCount = totalCount,
                        page = page,
                        pageSize = pageSize,
                        hasMore = (offset + matches.Count) < totalCount
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av matchhistorik: " + ex.Message });
            }
        }

        /// <summary>
        /// Delete a completed match (only creator can delete)
        /// POST /umbraco/surface/TrainingMatch/DeleteMatch
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteMatch([FromBody] DeleteMatchRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Check if user is creator or admin
                    bool isCreator = (int)match.CreatedByMemberId == member.Id;
                    bool isAdmin = await _authorizationService.IsCurrentUserAdminAsync();

                    if (!isCreator && !isAdmin)
                    {
                        return Json(new { success = false, message = "Endast matchskaparen eller administratör kan radera matchen" });
                    }

                    // IMPORTANT: TrainingScores must NEVER be deleted - they remain as individual training records
                    // Set TrainingMatchId to NULL to unlink them from this match
                    db.Execute("UPDATE TrainingScores SET TrainingMatchId = NULL WHERE TrainingMatchId = @0", (int)match.Id);

                    // Delete join requests for this match
                    db.Execute("DELETE FROM TrainingMatchJoinRequests WHERE TrainingMatchId = @0", (int)match.Id);

                    // Delete participants
                    db.Execute("DELETE FROM TrainingMatchParticipants WHERE TrainingMatchId = @0", (int)match.Id);

                    // Delete the match
                    db.Execute("DELETE FROM TrainingMatches WHERE Id = @0", (int)match.Id);

                    // Notify all viewers via SignalR
                    await _hubContext.SendMatchDeleted(request.MatchCode ?? "");

                    return Json(new { success = true, message = "Matchen har raderats" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid radering av match: " + ex.Message });
            }
        }

        /// <summary>
        /// Get current user's active matches
        /// GET /umbraco/surface/TrainingMatch/GetMyActiveMatches
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyActiveMatches()
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    var matches = db.Fetch<dynamic>(
                        @"SELECT tm.*,
                                 (SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = tm.Id) as ParticipantCount
                          FROM TrainingMatches tm
                          INNER JOIN TrainingMatchParticipants tmp ON tm.Id = tmp.TrainingMatchId
                          WHERE tmp.MemberId = @0 AND tm.Status = 'Active'
                          ORDER BY tm.CreatedDate DESC", member.Id);

                    var matchList = matches.Select(m => new
                    {
                        id = (int)m.Id,
                        matchCode = (string)m.MatchCode,
                        matchName = (string?)m.MatchName,
                        weaponClass = (string)m.WeaponClass,
                        createdDate = (DateTime)m.CreatedDate,
                        participantCount = (int)m.ParticipantCount,
                        isCreator = (int)m.CreatedByMemberId == member.Id
                    }).ToList();

                    return Json(new
                    {
                        success = true,
                        matches = matchList,
                        currentMemberId = member.Id  // Include for client-side context refresh
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av matcher: " + ex.Message });
            }
        }

        #endregion

        #region Ongoing Matches & Join Requests

        /// <summary>
        /// Get all ongoing (started) matches for the Pågående tab
        /// GET /umbraco/surface/TrainingMatch/GetOngoingMatches
        /// Does not require login - but join/view actions will
        /// Only returns matches that have started (StartDate <= NOW)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetOngoingMatches()
        {
            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    var matches = db.Fetch<dynamic>(
                        @"SELECT tm.*,
                                 (SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = tm.Id) as ParticipantCount
                          FROM TrainingMatches tm
                          WHERE tm.Status = 'Active' AND tm.StartDate <= GETDATE()
                          ORDER BY tm.StartDate DESC, tm.CreatedDate DESC");

                    var matchList = new List<object>();
                    foreach (var m in matches)
                    {
                        // Get creator info
                        var creator = _memberService.GetById((int)m.CreatedByMemberId);
                        var creatorName = creator != null
                            ? $"{creator.GetValue<string>("firstName")} {creator.GetValue<string>("lastName")}"
                            : "Okänd";

                        // Get participants (limited to 5 for display)
                        var participants = db.Fetch<dynamic>(
                            @"SELECT p.MemberId, p.GuestParticipantId, g.DisplayName as GuestDisplayName
                              FROM TrainingMatchParticipants p
                              LEFT JOIN TrainingMatchGuests g ON p.GuestParticipantId = g.Id
                              WHERE p.TrainingMatchId = @0
                              ORDER BY p.DisplayOrder
                              OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY", (int)m.Id);

                        var participantList = new List<object>();
                        foreach (var p in participants)
                        {
                            if (p.MemberId != null)
                            {
                                // Regular member
                                var pMember = _memberService.GetById((int)p.MemberId);
                                participantList.Add(new
                                {
                                    memberId = (int)p.MemberId,
                                    firstName = pMember?.GetValue<string>("firstName") ?? "",
                                    lastName = pMember?.GetValue<string>("lastName") ?? "",
                                    profilePictureUrl = pMember?.GetValue<string>("profilePictureUrl") ?? "",
                                    isGuest = false
                                });
                            }
                            else if (p.GuestParticipantId != null)
                            {
                                // Guest participant
                                var guestName = (string?)p.GuestDisplayName ?? "Gäst";
                                var nameParts = guestName.Split(' ', 2);
                                participantList.Add(new
                                {
                                    memberId = (int?)null,
                                    firstName = nameParts.Length > 0 ? nameParts[0] : "",
                                    lastName = nameParts.Length > 1 ? nameParts[1] : "",
                                    profilePictureUrl = "",
                                    isGuest = true
                                });
                            }
                        }

                        // Calculate if match has started
                        var startDate = m.StartDate != null ? (DateTime?)m.StartDate : null;
                        var hasStarted = startDate == null || startDate <= DateTime.Now;

                        matchList.Add(new
                        {
                            id = (int)m.Id,
                            matchCode = (string)m.MatchCode,
                            matchName = (string?)m.MatchName,
                            weaponClass = (string)m.WeaponClass,
                            createdDate = (DateTime)m.CreatedDate,
                            startDate = startDate,
                            hasStarted = hasStarted,
                            createdByMemberId = (int)m.CreatedByMemberId,
                            createdByName = creatorName,
                            participantCount = (int)m.ParticipantCount,
                            participants = participantList,
                            isOpen = m.IsOpen != null ? (bool)m.IsOpen : true  // Default true for old matches
                        });
                    }

                    // Get current member ID so client can refresh its context
                    var serverMemberId = await GetCurrentMemberIdAsync();

                    return Json(new
                    {
                        success = true,
                        matches = matchList,
                        currentMemberId = serverMemberId
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av pågående matcher: " + ex.Message });
            }
        }

        /// <summary>
        /// Get all upcoming (scheduled but not started) matches for the Kommande tab
        /// GET /umbraco/surface/TrainingMatch/GetUpcomingMatches
        /// Does not require login - but join/view actions will
        /// Returns matches with StartDate > NOW
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUpcomingMatches()
        {
            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    var matches = db.Fetch<dynamic>(
                        @"SELECT tm.*,
                                 (SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = tm.Id) as ParticipantCount
                          FROM TrainingMatches tm
                          WHERE tm.Status = 'Active' AND tm.StartDate > GETDATE()
                          ORDER BY tm.StartDate ASC");

                    var matchList = new List<object>();
                    foreach (var m in matches)
                    {
                        // Get creator info
                        var creator = _memberService.GetById((int)m.CreatedByMemberId);
                        var creatorName = creator != null
                            ? $"{creator.GetValue<string>("firstName")} {creator.GetValue<string>("lastName")}"
                            : "Okänd";

                        // Get participants (limited to 5 for display)
                        var participants = db.Fetch<dynamic>(
                            @"SELECT p.MemberId, p.GuestParticipantId, g.DisplayName as GuestDisplayName
                              FROM TrainingMatchParticipants p
                              LEFT JOIN TrainingMatchGuests g ON p.GuestParticipantId = g.Id
                              WHERE p.TrainingMatchId = @0
                              ORDER BY p.DisplayOrder
                              OFFSET 0 ROWS FETCH NEXT 5 ROWS ONLY", (int)m.Id);

                        var participantList = new List<object>();
                        foreach (var p in participants)
                        {
                            if (p.MemberId != null)
                            {
                                // Regular member
                                var pMember = _memberService.GetById((int)p.MemberId);
                                participantList.Add(new
                                {
                                    memberId = (int)p.MemberId,
                                    firstName = pMember?.GetValue<string>("firstName") ?? "",
                                    lastName = pMember?.GetValue<string>("lastName") ?? "",
                                    profilePictureUrl = pMember?.GetValue<string>("profilePictureUrl") ?? "",
                                    isGuest = false
                                });
                            }
                            else if (p.GuestParticipantId != null)
                            {
                                // Guest participant
                                var guestName = (string?)p.GuestDisplayName ?? "Gäst";
                                var nameParts = guestName.Split(' ', 2);
                                participantList.Add(new
                                {
                                    memberId = (int?)null,
                                    firstName = nameParts.Length > 0 ? nameParts[0] : "",
                                    lastName = nameParts.Length > 1 ? nameParts[1] : "",
                                    profilePictureUrl = "",
                                    isGuest = true
                                });
                            }
                        }

                        var startDate = m.StartDate != null ? (DateTime?)m.StartDate : null;

                        matchList.Add(new
                        {
                            id = (int)m.Id,
                            matchCode = (string)m.MatchCode,
                            matchName = (string?)m.MatchName,
                            weaponClass = (string)m.WeaponClass,
                            createdDate = (DateTime)m.CreatedDate,
                            startDate = startDate,
                            hasStarted = false, // All upcoming matches haven't started
                            createdByMemberId = (int)m.CreatedByMemberId,
                            createdByName = creatorName,
                            participantCount = (int)m.ParticipantCount,
                            participants = participantList,
                            isOpen = m.IsOpen != null ? (bool)m.IsOpen : true  // Default true for old matches
                        });
                    }

                    // Get current member ID so client can refresh its context
                    var serverMemberId = await GetCurrentMemberIdAsync();

                    return Json(new
                    {
                        success = true,
                        matches = matchList,
                        currentMemberId = serverMemberId
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av kommande matcher: " + ex.Message });
            }
        }

        /// <summary>
        /// Get match counts for badge display (lightweight - no member lookups)
        /// GET /umbraco/surface/TrainingMatch/GetMatchCounts
        /// Returns ongoing and upcoming counts in a single query
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMatchCounts()
        {
            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get both counts in a single query
                    var counts = db.Single<dynamic>(
                        @"SELECT
                            (SELECT COUNT(*) FROM TrainingMatches WHERE Status = 'Active' AND StartDate <= GETDATE()) as OngoingCount,
                            (SELECT COUNT(*) FROM TrainingMatches WHERE Status = 'Active' AND StartDate > GETDATE()) as UpcomingCount");

                    return Json(new
                    {
                        success = true,
                        ongoingCount = (int)counts.OngoingCount,
                        upcomingCount = (int)counts.UpcomingCount
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av antal matcher: " + ex.Message });
            }
        }

        /// <summary>
        /// Auto-close stale matches that have been active for more than 24 hours after start
        /// GET /umbraco/surface/TrainingMatch/AutoCloseStaleMatches
        /// Called on page load to clean up old matches
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> AutoCloseStaleMatches()
        {
            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Close matches that started more than 24 hours ago
                    var closedCount = db.Execute(
                        @"UPDATE TrainingMatches
                          SET Status = 'Completed', CompletedDate = GETDATE()
                          WHERE Status = 'Active' AND StartDate < DATEADD(hour, -24, GETDATE())");

                    return Json(new
                    {
                        success = true,
                        closedCount = closedCount,
                        message = closedCount > 0 ? $"{closedCount} matcher avslutades automatiskt" : null
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid automatisk stängning: " + ex.Message });
            }
        }

        /// <summary>
        /// Request to join a match (requires organizer approval)
        /// POST /umbraco/surface/TrainingMatch/RequestJoinMatch
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestJoinMatch([FromBody] RequestJoinMatchRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad för att begära att gå med" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", request.MatchCode?.ToUpper());

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    if ((string)match.Status != "Active")
                    {
                        return Json(new { success = false, message = "Matchen är avslutad" });
                    }

                    // NOTE: Users CAN request to join before start time, they just can't enter scores until it starts

                    // Check if already a participant
                    var existingParticipant = db.SingleOrDefault<dynamic>(
                        @"SELECT Id FROM TrainingMatchParticipants
                          WHERE TrainingMatchId = @0 AND MemberId = @1",
                        (int)match.Id, member.Id);

                    if (existingParticipant != null)
                    {
                        return Json(new { success = false, message = "Du är redan med i matchen" });
                    }

                    // Check for existing request
                    var existingRequest = db.SingleOrDefault<dynamic>(
                        @"SELECT * FROM TrainingMatchJoinRequests
                          WHERE TrainingMatchId = @0 AND MemberId = @1",
                        (int)match.Id, member.Id);

                    // Get member details for notification
                    var firstName = member.GetValue<string>("firstName") ?? "";
                    var lastName = member.GetValue<string>("lastName") ?? "";
                    var memberName = $"{firstName} {lastName}".Trim();
                    var profilePictureUrl = member.GetValue<string>("profilePictureUrl") ?? "";

                    int requestId;

                    if (existingRequest != null)
                    {
                        var status = (string)existingRequest.Status;
                        if (status == JoinRequestStatus.Pending)
                        {
                            return Json(new { success = false, message = "Du har redan en väntande förfrågan" });
                        }
                        if (status == JoinRequestStatus.Blocked)
                        {
                            return Json(new { success = false, message = "Du har blockerats från denna match" });
                        }

                        // If status is Accepted (user left and wants to rejoin), reset to Pending
                        requestId = (int)existingRequest.Id;
                        db.Execute(
                            @"UPDATE TrainingMatchJoinRequests
                              SET Status = @0, RequestDate = @1, MemberName = @2, MemberProfilePictureUrl = @3,
                                  ResponseDate = NULL, ResponseByMemberId = NULL
                              WHERE Id = @4",
                            JoinRequestStatus.Pending, DateTime.Now, memberName, profilePictureUrl, requestId);
                    }
                    else
                    {
                        // Create new join request
                        db.Insert("TrainingMatchJoinRequests", "Id", true, new
                        {
                            TrainingMatchId = (int)match.Id,
                            MemberId = member.Id,
                            MemberName = memberName,
                            MemberProfilePictureUrl = profilePictureUrl,
                            Status = JoinRequestStatus.Pending,
                            RequestDate = DateTime.Now
                        });

                        // Get the ID of the newly inserted request
                        requestId = db.SingleOrDefault<int>(
                            @"SELECT TOP 1 Id FROM TrainingMatchJoinRequests
                              WHERE TrainingMatchId = @0 AND MemberId = @1
                              ORDER BY Id DESC",
                            (int)match.Id, member.Id);
                    }

                    // Send SignalR notification to organizer
                    await _hubContext.SendJoinRequestToOrganizer(
                        (string)match.MatchCode,
                        new
                        {
                            id = requestId,
                            memberId = member.Id,
                            memberName = memberName,
                            profilePictureUrl = profilePictureUrl,
                            matchCode = (string)match.MatchCode,
                            requestDate = DateTime.Now
                        });

                    return Json(new
                    {
                        success = true,
                        message = "Din förfrågan har skickats till matchvärden"
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid skapande av förfrågan: " + ex.Message });
            }
        }

        /// <summary>
        /// Respond to a join request (accept or block)
        /// POST /umbraco/surface/TrainingMatch/RespondToJoinRequest
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RespondToJoinRequest([FromBody] RespondToJoinRequestRequest request)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get the join request
                    var joinRequest = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatchJoinRequests WHERE Id = @0", request.RequestId);

                    if (joinRequest == null)
                    {
                        return Json(new { success = false, message = "Förfrågan hittades inte" });
                    }

                    // Get match to verify organizer
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE Id = @0", (int)joinRequest.TrainingMatchId);

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Verify current user is the organizer
                    if ((int)match.CreatedByMemberId != member.Id)
                    {
                        return Json(new { success = false, message = "Endast matchvärden kan svara på förfrågningar" });
                    }

                    var requestedMemberId = (int)joinRequest.MemberId;
                    var matchCode = (string)match.MatchCode;

                    if (request.Action == "Accept")
                    {
                        // Update request status
                        db.Execute(
                            @"UPDATE TrainingMatchJoinRequests
                              SET Status = @0, ResponseDate = @1, ResponseByMemberId = @2
                              WHERE Id = @3",
                            JoinRequestStatus.Accepted, DateTime.Now, member.Id, request.RequestId);

                        // Get current max display order
                        var maxOrder = db.SingleOrDefault<int?>(
                            "SELECT MAX(DisplayOrder) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0",
                            (int)match.Id) ?? -1;

                        // Calculate frozen handicap if match has handicap enabled
                        decimal? frozenHandicap = null;
                        bool? frozenIsProvisional = null;
                        bool hasHandicap = match.HasHandicap != null && (bool)match.HasHandicap;
                        var requestedMember = _memberService.GetById(requestedMemberId);

                        if (hasHandicap && requestedMember != null)
                        {
                            var shooterClass = requestedMember.GetValue<string>("precisionShooterClass");
                            var weaponClass = (string)match.WeaponClass;

                            if (!string.IsNullOrEmpty(shooterClass))
                            {
                                // Recalculate statistics before getting handicap to ensure it's up-to-date
                                await _statisticsService.RecalculateFromHistoryAsync(requestedMemberId, weaponClass);

                                var stats = await _statisticsService.GetStatisticsAsync(requestedMemberId, weaponClass);
                                var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);
                                frozenHandicap = profile.HandicapPerSeries;
                                frozenIsProvisional = profile.IsProvisional;
                            }
                        }

                        // Add as participant
                        db.Insert("TrainingMatchParticipants", "Id", true, new
                        {
                            TrainingMatchId = (int)match.Id,
                            MemberId = requestedMemberId,
                            JoinedDate = DateTime.Now,
                            DisplayOrder = maxOrder + 1,
                            FrozenHandicapPerSeries = frozenHandicap,
                            FrozenIsProvisional = frozenIsProvisional
                        });

                        // Send SignalR notification to requester
                        await _hubContext.SendJoinRequestAccepted(requestedMemberId, matchCode);

                        // Notify all match viewers
                        await _hubContext.SendParticipantJoined(matchCode, new
                        {
                            memberId = requestedMemberId,
                            firstName = requestedMember?.GetValue<string>("firstName") ?? "",
                            lastName = requestedMember?.GetValue<string>("lastName") ?? "",
                            profilePictureUrl = requestedMember?.GetValue<string>("profilePictureUrl") ?? "",
                            handicap = frozenHandicap,
                            isProvisional = frozenIsProvisional
                        });

                        return Json(new { success = true, message = "Spelaren har lagts till i matchen" });
                    }
                    else if (request.Action == "Block")
                    {
                        // Update request status to blocked
                        db.Execute(
                            @"UPDATE TrainingMatchJoinRequests
                              SET Status = @0, ResponseDate = @1, ResponseByMemberId = @2
                              WHERE Id = @3",
                            JoinRequestStatus.Blocked, DateTime.Now, member.Id, request.RequestId);

                        // Send SignalR notification to requester
                        await _hubContext.SendJoinRequestBlocked(requestedMemberId, matchCode);

                        return Json(new { success = true, message = "Spelaren har blockerats" });
                    }
                    else
                    {
                        return Json(new { success = false, message = "Ogiltig åtgärd" });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid svar på förfrågan: " + ex.Message });
            }
        }

        /// <summary>
        /// Get pending join requests for a match (organizer only)
        /// GET /umbraco/surface/TrainingMatch/GetPendingJoinRequests?matchCode=ABC123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPendingJoinRequests(string matchCode)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad" });
            }

            try
            {
                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Medlem hittades inte" });
                }

                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", matchCode?.ToUpper());

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Verify current user is the organizer
                    if ((int)match.CreatedByMemberId != member.Id)
                    {
                        return Json(new { success = false, message = "Endast matchvärden kan se förfrågningar" });
                    }

                    // Get pending requests
                    var requests = db.Fetch<dynamic>(
                        @"SELECT * FROM TrainingMatchJoinRequests
                          WHERE TrainingMatchId = @0 AND Status = @1
                          ORDER BY RequestDate DESC",
                        (int)match.Id, JoinRequestStatus.Pending);

                    var requestList = requests.Select(r => new
                    {
                        id = (int)r.Id,
                        memberId = (int)r.MemberId,
                        memberName = (string?)r.MemberName ?? "Okänd",
                        profilePictureUrl = (string?)r.MemberProfilePictureUrl ?? "",
                        requestDate = (DateTime)r.RequestDate
                    }).ToList();

                    return Json(new
                    {
                        success = true,
                        requests = requestList
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av förfrågningar: " + ex.Message });
            }
        }

        /// <summary>
        /// View a match as spectator (read-only, no participation)
        /// GET /umbraco/surface/TrainingMatch/ViewMatchAsSpectator?matchCode=ABC123
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ViewMatchAsSpectator(string matchCode)
        {
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember == null)
            {
                return Json(new { success = false, message = "Du måste vara inloggad för att titta på matcher" });
            }

            try
            {
                using (var db = _databaseFactory.CreateDatabase())
                {
                    // Get match
                    var match = db.SingleOrDefault<dynamic>(
                        "SELECT * FROM TrainingMatches WHERE MatchCode = @0", matchCode?.ToUpper());

                    if (match == null)
                    {
                        return Json(new { success = false, message = "Match hittades inte" });
                    }

                    // Get creator name
                    var creator = _memberService.GetById((int)match.CreatedByMemberId);
                    var creatorName = creator != null
                        ? $"{creator.GetValue<string>("firstName")} {creator.GetValue<string>("lastName")}"
                        : "Okänd";

                    // Get participants
                    var participants = db.Fetch<dynamic>(
                        @"SELECT * FROM TrainingMatchParticipants
                          WHERE TrainingMatchId = @0
                          ORDER BY DisplayOrder", (int)match.Id);

                    var participantList = new List<object>();
                    foreach (var p in participants)
                    {
                        string firstName, lastName, profilePictureUrl;
                        int? memberId = p.MemberId != null ? (int?)p.MemberId : null;

                        if (memberId.HasValue)
                        {
                            var pMember = _memberService.GetById(memberId.Value);
                            firstName = pMember?.GetValue<string>("firstName") ?? "";
                            lastName = pMember?.GetValue<string>("lastName") ?? "";
                            profilePictureUrl = pMember?.GetValue<string>("profilePictureUrl") ?? "";
                        }
                        else
                        {
                            // Guest participant
                            firstName = "";
                            lastName = "";
                            profilePictureUrl = "";
                            if (p.GuestParticipantId != null)
                            {
                                var guest = db.SingleOrDefault<dynamic>(
                                    "SELECT DisplayName FROM TrainingMatchGuests WHERE Id = @0",
                                    (int)p.GuestParticipantId);
                                if (guest != null)
                                {
                                    var displayName = (string?)guest.DisplayName ?? "Gäst";
                                    var nameParts = displayName.Split(' ', 2);
                                    firstName = nameParts[0];
                                    lastName = nameParts.Length > 1 ? nameParts[1] : "";
                                }
                            }
                        }

                        // Get scores for this participant
                        dynamic? scoreRow = null;
                        if (memberId.HasValue)
                        {
                            scoreRow = db.SingleOrDefault<dynamic>(
                                @"SELECT ts.Id, ts.SeriesScores, ts.TotalScore, ts.XCount
                                  FROM TrainingScores ts
                                  WHERE ts.MemberId = @0 AND ts.TrainingMatchId = @1",
                                memberId.Value, (int)match.Id);
                        }
                        else if (p.GuestParticipantId != null)
                        {
                            scoreRow = db.SingleOrDefault<dynamic>(
                                @"SELECT ts.Id, ts.SeriesScores, ts.TotalScore, ts.XCount
                                  FROM TrainingScores ts
                                  WHERE ts.GuestParticipantId = @0 AND ts.TrainingMatchId = @1",
                                (int)p.GuestParticipantId, (int)match.Id);
                        }

                        var scoreList = new List<object>();
                        if (scoreRow != null)
                        {
                            var seriesJson = (string)scoreRow.SeriesScores;
                            if (!string.IsNullOrEmpty(seriesJson))
                            {
                                try
                                {
                                    var seriesList = JsonSerializer.Deserialize<List<TrainingSeries>>(seriesJson);
                                    if (seriesList != null)
                                    {
                                        foreach (var s in seriesList)
                                        {
                                            scoreList.Add(new
                                            {
                                                id = (int)scoreRow.Id,
                                                seriesNumber = s.SeriesNumber,
                                                total = s.Total,
                                                xCount = s.XCount,
                                                shots = s.Shots,
                                                entryMethod = s.EntryMethod,
                                                targetPhotoUrl = s.TargetPhotoUrl,
                                                reactions = s.Reactions
                                            });
                                        }
                                    }
                                }
                                catch { }
                            }
                        }

                        participantList.Add(new
                        {
                            id = (int)p.Id,
                            memberId = memberId,
                            guestParticipantId = p.GuestParticipantId != null ? (int?)p.GuestParticipantId : null,
                            isGuest = !memberId.HasValue,
                            firstName = firstName,
                            lastName = lastName,
                            profilePictureUrl = profilePictureUrl,
                            displayOrder = (int)p.DisplayOrder,
                            scores = scoreList,
                            totalScore = scoreList.Sum(s => ((dynamic)s).total),
                            seriesCount = scoreList.Count
                        });
                    }

                    var startDate = match.StartDate != null ? (DateTime?)match.StartDate : null;

                    return Json(new
                    {
                        success = true,
                        isSpectator = true,
                        match = new
                        {
                            id = (int)match.Id,
                            matchCode = (string)match.MatchCode,
                            matchName = (string?)match.MatchName,
                            createdByMemberId = (int)match.CreatedByMemberId,
                            createdByName = creatorName,
                            weaponClass = (string)match.WeaponClass,
                            createdDate = (DateTime)match.CreatedDate,
                            startDate = startDate,
                            hasStarted = startDate == null || startDate <= DateTime.Now,
                            status = (string)match.Status,
                            completedDate = match.CompletedDate != null ? (DateTime?)match.CompletedDate : null,
                            participants = participantList
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Fel vid hämtning av match: " + ex.Message });
            }
        }

        #endregion

        #region QR Code

        /// <summary>
        /// Generate QR code for joining a match
        /// GET /umbraco/surface/TrainingMatch/GetJoinQrCode?matchCode=ABC123
        /// </summary>
        [HttpGet]
        public IActionResult GetJoinQrCode(string matchCode)
        {
            try
            {
                // Build join URL
                var request = HttpContext.Request;
                var baseUrl = $"{request.Scheme}://{request.Host}";
                var joinUrl = $"{baseUrl}/traningsmatch/?join={matchCode}";

                // Generate QR code
                var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(joinUrl, QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);

                using var img = qr.GetGraphic(
                    pixelsPerModule: 10,
                    darkColor: Color.Black,
                    lightColor: Color.White,
                    drawQuietZones: true
                );

                using var ms = new MemoryStream();
                img.Save(ms, new PngEncoder());
                var bytes = ms.ToArray();

                return File(bytes, "image/png");
            }
            catch (Exception ex)
            {
                return BadRequest("Kunde inte generera QR-kod: " + ex.Message);
            }
        }

        /// <summary>
        /// Generate QR code for guest claim URL
        /// GET /umbraco/surface/TrainingMatch/GetGuestClaimQrCode?code={matchCode}&amp;token={claimToken}
        /// </summary>
        [HttpGet]
        public IActionResult GetGuestClaimQrCode(string code, string token)
        {
            try
            {
                if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(token))
                {
                    return BadRequest("Match code and token required");
                }

                // Construct the guest claim URL server-side
                var baseUrl = $"{Request.Scheme}://{Request.Host}";
                var guestClaimUrl = $"{baseUrl}/match/{code.ToUpper()}/guest/{token}";

                // Generate QR code
                var gen = new QRCodeGenerator();
                using var data = gen.CreateQrCode(guestClaimUrl, QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);

                using var img = qr.GetGraphic(
                    pixelsPerModule: 10,
                    darkColor: Color.Black,
                    lightColor: Color.White,
                    drawQuietZones: true
                );

                using var ms = new MemoryStream();
                img.Save(ms, new PngEncoder());
                var bytes = ms.ToArray();

                return File(bytes, "image/png");
            }
            catch (Exception ex)
            {
                return BadRequest("Kunde inte generera QR-kod: " + ex.Message);
            }
        }

        #endregion

        #region Android App Test Access

        /// <summary>
        /// Request access to the Android app test program
        /// POST /umbraco/surface/TrainingMatch/RequestAndroidTestAccess
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestAndroidTestAccess()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad" });
                }

                var member = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (member == null)
                {
                    return Json(new { success = false, message = "Kunde inte hitta din profil" });
                }

                var firstName = member.GetValue<string>("firstName") ?? "";
                var lastName = member.GetValue<string>("lastName") ?? "";
                var memberName = $"{firstName} {lastName}".Trim();
                var memberEmail = currentMember.Email ?? "";

                // Send notification to admin
                await _emailService.SendAndroidTestAccessRequestAsync(memberName, memberEmail);

                return Json(new { success = true, message = "Din förfrågan har skickats" });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Något gick fel: " + ex.Message });
            }
        }

        #endregion
    }

    #region Request Models

    public class CreateMatchRequest
    {
        public string? MatchName { get; set; }
        public string? WeaponClass { get; set; }
        public DateTime? StartDate { get; set; }
        public bool IsOpen { get; set; } = true;  // Default true - anyone can join
        public bool HasHandicap { get; set; } = false;  // Enable handicap system for this match
    }

    public class JoinMatchRequest
    {
        public string? MatchCode { get; set; }
    }

    public class SetShooterClassRequest
    {
        public string? ShooterClass { get; set; }
    }

    public class LeaveMatchRequest
    {
        public string? MatchCode { get; set; }
    }

    public class CompleteMatchRequest
    {
        public string? MatchCode { get; set; }
    }

    public class UpdateMatchSettingsRequest
    {
        public string? MatchCode { get; set; }
        public int? MaxSeriesCount { get; set; }
        public bool? AllowGuests { get; set; }
    }

    public class SaveMatchScoreRequest
    {
        public string? MatchCode { get; set; }
        public int SeriesNumber { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
        public List<string>? Shots { get; set; }
        public string? EntryMethod { get; set; }
    }

    public class DeleteScoreRequest
    {
        public int ScoreId { get; set; }
    }

    public class UpdateMatchScoreRequest
    {
        public string? MatchCode { get; set; }
        public int SeriesNumber { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
        public List<string>? Shots { get; set; }
        public string? EntryMethod { get; set; }
    }

    public class DeleteMatchSeriesRequest
    {
        public string? MatchCode { get; set; }
        public int SeriesNumber { get; set; }
    }

    public class DeleteMatchRequest
    {
        public string? MatchCode { get; set; }
    }

    public class RequestJoinMatchRequest
    {
        public string? MatchCode { get; set; }
    }

    public class RespondToJoinRequestRequest
    {
        public int RequestId { get; set; }
        public string? Action { get; set; } // "Accept" or "Block"
    }

    #endregion
}
