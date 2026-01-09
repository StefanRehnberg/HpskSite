using HpskSite.Services;
using HpskSite.Shared.DTOs;
using HpskSite.Shared.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Controllers; // For UpdateMatchSettingsRequest
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using NPoco;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Infrastructure.Scoping;
using HpskSite.Hubs;
using System.Text.Json;

namespace HpskSite.Controllers.Api
{
    /// <summary>
    /// API controller for Training Match operations (Mobile app)
    /// </summary>
    [Route("api/match")]
    [ApiController]
    [Authorize(AuthenticationSchemes = "JwtBearer")]
    public class MatchApiController : ControllerBase
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly JwtTokenService _jwtTokenService;
        private readonly IHubContext<TrainingMatchHub> _hubContext;
        private readonly IHandicapCalculator _handicapCalculator;
        private readonly IShooterStatisticsService _statisticsService;
        private readonly IConfiguration _configuration;
        private readonly PushNotificationService _pushNotificationService;
        private readonly ImageResizeService _imageResizeService;
        private readonly IWebHostEnvironment _webHostEnvironment;
        private readonly ILogger<MatchApiController> _logger;

        public MatchApiController(
            IScopeProvider scopeProvider,
            IMemberService memberService,
            IMemberManager memberManager,
            JwtTokenService jwtTokenService,
            IHubContext<TrainingMatchHub> hubContext,
            IHandicapCalculator handicapCalculator,
            IShooterStatisticsService statisticsService,
            IConfiguration configuration,
            PushNotificationService pushNotificationService,
            ImageResizeService imageResizeService,
            IWebHostEnvironment webHostEnvironment,
            ILogger<MatchApiController> logger)
        {
            _scopeProvider = scopeProvider;
            _memberService = memberService;
            _memberManager = memberManager;
            _jwtTokenService = jwtTokenService;
            _hubContext = hubContext;
            _handicapCalculator = handicapCalculator;
            _statisticsService = statisticsService;
            _configuration = configuration;
            _pushNotificationService = pushNotificationService;
            _imageResizeService = imageResizeService;
            _webHostEnvironment = webHostEnvironment;
            _logger = logger;
        }

        /// <summary>
        /// Get current member ID from JWT token or cookie-based authentication
        /// Supports both mobile (JWT) and website (cookie) authentication
        /// </summary>
        private int? GetCurrentMemberId()
        {
            // First try JWT claims (mobile app)
            var memberId = _jwtTokenService.GetMemberIdFromClaims(User);
            if (memberId.HasValue)
            {
                return memberId;
            }

            // Fallback to cookie-based authentication (website)
            // Use synchronous check since we're in a private helper method
            try
            {
                var member = _memberManager.GetCurrentMemberAsync().GetAwaiter().GetResult();
                if (member != null)
                {
                    var memberData = _memberService.GetByEmail(member.Email ?? "");
                    return memberData?.Id;
                }
            }
            catch
            {
                // Cookie auth not available, return null
            }

            return null;
        }

        /// <summary>
        /// Convert a relative URL to an absolute URL using configured SiteUrl
        /// </summary>
        private string? ToAbsoluteUrl(string? relativeUrl)
        {
            if (string.IsNullOrEmpty(relativeUrl))
                return null;

            // If already absolute, return as-is
            if (relativeUrl.StartsWith("http://") || relativeUrl.StartsWith("https://"))
                return relativeUrl;

            // Use configured SiteUrl (e.g., https://hpsktest.se)
            var siteUrl = _configuration["EmailSettings:SiteUrl"]?.TrimEnd('/') ?? "";
            if (string.IsNullOrEmpty(siteUrl))
            {
                // Fallback to request-based URL
                siteUrl = $"{Request.Scheme}://{Request.Host}";
            }

            return relativeUrl.StartsWith("/")
                ? $"{siteUrl}{relativeUrl}"
                : $"{siteUrl}/{relativeUrl}";
        }

        /// <summary>
        /// Create a new training match
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateMatch([FromBody] CreateMatchRequest request)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<TrainingMatch>.Error("Ej inloggad"));
            }

            // Validate weapon class
            var validClasses = new[] { "A", "B", "C", "R", "M", "L" };
            if (!validClasses.Contains(request.WeaponClass?.ToUpper()))
            {
                return BadRequest(ApiResponse<TrainingMatch>.Error("Ogiltig vapenklass"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Generate unique match code
            string matchCode;
            do
            {
                matchCode = TrainingMatch.GenerateMatchCode();
            } while (await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM TrainingMatches WHERE MatchCode = @0", matchCode) > 0);

            // Create match
            var match = new TrainingMatchDbDto
            {
                MatchCode = matchCode,
                MatchName = request.MatchName,
                CreatedByMemberId = memberId.Value,
                WeaponClass = request.WeaponClass?.ToUpper() ?? "A",
                CreatedDate = DateTime.UtcNow,
                StartDate = request.StartDate,
                Status = "Active",
                IsOpen = request.IsOpen,
                HasHandicap = request.HasHandicap
            };

            await db.InsertAsync(match);

            // Calculate handicap for creator if match has handicap enabled
            decimal? frozenHandicap = null;
            bool? frozenIsProvisional = null;

            if (request.HasHandicap)
            {
                try
                {
                    var member = _memberService.GetById(memberId.Value);
                    var shooterClass = member?.GetValue<string>("precisionShooterClass");
                    var stats = await _statisticsService.GetStatisticsAsync(memberId.Value, request.WeaponClass?.ToUpper() ?? "A");
                    var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);
                    frozenHandicap = profile.HandicapPerSeries;
                    frozenIsProvisional = profile.IsProvisional;
                }
                catch
                {
                    // If handicap calculation fails (e.g., no shooter class set), proceed without handicap
                }
            }

            // Add creator as first participant
            var participant = new TrainingMatchParticipantDbDto
            {
                TrainingMatchId = match.Id,
                MemberId = memberId.Value,
                JoinedDate = DateTime.UtcNow,
                DisplayOrder = 1,
                FrozenHandicapPerSeries = frozenHandicap,
                FrozenIsProvisional = frozenIsProvisional
            };

            await db.InsertAsync(participant);

            // Get creator name for notification
            var creator = _memberService.GetById(memberId.Value);
            var creatorName = creator != null
                ? $"{creator.GetValue<string>("firstName")} {creator.GetValue<string>("lastName")}"
                : "Okänd";

            scope.Complete();

            // Get full match with participant info first (so response isn't delayed by notification)
            var result = await GetMatchByCode(matchCode);

            // Send push notification after scope is complete
            try
            {
                await _pushNotificationService.SendMatchCreatedNotificationAsync(
                    matchCode,
                    request.MatchName ?? matchCode,
                    creatorName,
                    match.WeaponClass,
                    match.IsOpen);
            }
            catch
            {
                // Don't fail the request if notification fails
            }
            return Ok(ApiResponse<TrainingMatch>.Ok(result!));
        }

        /// <summary>
        /// Get match by code
        /// </summary>
        [HttpGet("{code}")]
        public async Task<IActionResult> GetMatch(string code)
        {
            var match = await GetMatchByCode(code);
            if (match == null)
            {
                return NotFound(ApiResponse<TrainingMatch>.Error("Matchen hittades inte"));
            }

            return Ok(ApiResponse<TrainingMatch>.Ok(match));
        }

        /// <summary>
        /// Join a match
        /// </summary>
        [HttpPost("{code}/join")]
        public async Task<IActionResult> JoinMatch(string code)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<TrainingMatch>.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse<TrainingMatch>.Error("Matchen hittades inte"));
            }

            if (match.Status != "Active")
            {
                return BadRequest(ApiResponse<TrainingMatch>.Error("Matchen är inte aktiv"));
            }

            // Check if already joined
            var existing = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0 AND MemberId = @1",
                match.Id, memberId.Value);

            if (existing > 0)
            {
                return BadRequest(ApiResponse<TrainingMatch>.Error("Du deltar redan i denna match"));
            }

            // Get next display order
            var maxOrder = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(DisplayOrder), 0) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0",
                match.Id);

            // Calculate handicap if match has handicap enabled
            decimal? frozenHandicap = null;
            bool? frozenIsProvisional = null;

            if (match.HasHandicap)
            {
                var member = _memberService.GetById(memberId.Value);
                var shooterClass = member?.GetValue<string>("precisionShooterClass");

                // If handicap is enabled but user has no shooter class, require them to set it first
                if (string.IsNullOrEmpty(shooterClass))
                {
                    return BadRequest(new ApiResponse<TrainingMatch>
                    {
                        Success = false,
                        Message = "Du måste välja din skytteklass för att kunna gå med i en handicapmatch",
                        NeedsShooterClass = true
                    });
                }

                try
                {
                    var stats = await _statisticsService.GetStatisticsAsync(memberId.Value, match.WeaponClass);
                    var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);
                    frozenHandicap = profile.HandicapPerSeries;
                    frozenIsProvisional = profile.IsProvisional;
                }
                catch
                {
                    // If handicap calculation fails, proceed with zero handicap
                    frozenHandicap = 0;
                    frozenIsProvisional = true;
                }
            }

            // Add participant
            var participant = new TrainingMatchParticipantDbDto
            {
                TrainingMatchId = match.Id,
                MemberId = memberId.Value,
                JoinedDate = DateTime.UtcNow,
                DisplayOrder = maxOrder + 1,
                FrozenHandicapPerSeries = frozenHandicap,
                FrozenIsProvisional = frozenIsProvisional
            };

            await db.InsertAsync(participant);

            scope.Complete();

            // Notify via SignalR (use match_{code} group format to match hub's JoinMatchGroup)
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("ParticipantJoined", memberId.Value);

            var result = await GetMatchByCode(code);
            return Ok(ApiResponse<TrainingMatch>.Ok(result!));
        }

        /// <summary>
        /// Leave a match
        /// </summary>
        [HttpPost("{code}/leave")]
        public async Task<IActionResult> LeaveMatch(string code)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse.Error("Matchen hittades inte"));
            }

            // Can't leave if you're the creator
            if (match.CreatedByMemberId == memberId.Value)
            {
                return BadRequest(ApiResponse.Error("Matchskaparen kan inte lämna matchen"));
            }

            // Remove participant
            await db.ExecuteAsync(
                "DELETE FROM TrainingMatchParticipants WHERE TrainingMatchId = @0 AND MemberId = @1",
                match.Id, memberId.Value);

            scope.Complete();

            // Notify via SignalR (use match_{code} group format)
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("ParticipantLeft", memberId.Value);

            return Ok(ApiResponse.Ok("Du har lämnat matchen"));
        }

        /// <summary>
        /// Delete a match (only creator can delete)
        /// </summary>
        [HttpDelete("{code}")]
        public async Task<IActionResult> DeleteMatch(string code)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse.Error("Matchen hittades inte"));
            }

            // Only creator can delete
            if (match.CreatedByMemberId != memberId.Value)
            {
                return Forbid();
            }

            // Delete all related data in correct order (foreign key constraints)
            // 1. Delete join requests
            await db.ExecuteAsync(
                "DELETE FROM TrainingMatchJoinRequests WHERE TrainingMatchId = @0", match.Id);

            // 2. Delete scores
            await db.ExecuteAsync(
                "DELETE FROM TrainingScores WHERE TrainingMatchId = @0", match.Id);

            // 3. Delete participants
            await db.ExecuteAsync(
                "DELETE FROM TrainingMatchParticipants WHERE TrainingMatchId = @0", match.Id);

            // 4. Delete the match itself
            await db.DeleteAsync(match);

            scope.Complete();

            // Notify via SignalR
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("MatchDeleted", code.ToUpper());

            return Ok(ApiResponse.Ok("Matchen har raderats"));
        }

        /// <summary>
        /// Update match settings (max series count)
        /// </summary>
        [HttpPost("{code}/settings")]
        public async Task<IActionResult> UpdateMatchSettings(string code, [FromBody] UpdateMatchSettingsRequest request)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse.Error("Matchen hittades inte"));
            }

            // Only creator can update settings
            if (match.CreatedByMemberId != memberId.Value)
            {
                return Forbid();
            }

            // Update MaxSeriesCount
            await db.ExecuteAsync(
                "UPDATE TrainingMatches SET MaxSeriesCount = @0 WHERE Id = @1",
                request.MaxSeriesCount, match.Id);

            scope.Complete();

            // Notify all participants via SignalR to reload match data
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("SettingsUpdated", new { maxSeriesCount = request.MaxSeriesCount });

            return Ok(ApiResponse.Ok("Inställningar sparade"));
        }

        /// <summary>
        /// Complete a match
        /// </summary>
        [HttpPost("{code}/complete")]
        public async Task<IActionResult> CompleteMatch(string code)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse.Error("Matchen hittades inte"));
            }

            // Only creator can complete
            if (match.CreatedByMemberId != memberId.Value)
            {
                return Forbid();
            }

            // Update status
            match.Status = "Completed";
            match.CompletedDate = DateTime.UtcNow;
            await db.UpdateAsync(match);

            scope.Complete();

            // Notify via SignalR (use match_{code} group format)
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("MatchCompleted");

            return Ok(ApiResponse.Ok("Matchen är avslutad"));
        }

        /// <summary>
        /// Save a score for a series
        /// </summary>
        [HttpPost("{code}/score")]
        public async Task<IActionResult> SaveScore(string code, [FromBody] SaveScoreRequest request)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<TrainingMatchScore>.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse<TrainingMatchScore>.Error("Matchen hittades inte"));
            }

            if (match.Status != "Active")
            {
                return BadRequest(ApiResponse<TrainingMatchScore>.Error("Matchen är inte aktiv"));
            }

            // Check if participant
            var participant = await db.FirstOrDefaultAsync<TrainingMatchParticipantDbDto>(
                "WHERE TrainingMatchId = @0 AND MemberId = @1",
                match.Id, memberId.Value);

            if (participant == null)
            {
                return BadRequest(ApiResponse<TrainingMatchScore>.Error("Du deltar inte i denna match"));
            }

            // Check if participant already has a TrainingScores row for this match
            // (Web stores all series in ONE row per participant - we must match this behavior)
            var existingScore = await db.FirstOrDefaultAsync<TrainingScoreDbDto>(
                @"WHERE MemberId = @0 AND TrainingMatchId = @1",
                memberId.Value, match.Id);

            // Create the new series object
            var newSeries = new
            {
                seriesNumber = request.SeriesNumber,
                shots = request.Shots,
                total = request.Total,
                xCount = request.XCount,
                entryMethod = request.EntryMethod ?? "ShotByShot"
            };

            if (existingScore != null)
            {
                // UPDATE existing row - add series to JSON array (matching web behavior)
                var existingSeriesJson = existingScore.SeriesScores ?? "[]";
                var seriesList = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(existingSeriesJson)
                    ?? new List<Dictionary<string, object>>();

                // Check if this series number already exists (update it) or add new
                var existingSeriesIndex = seriesList.FindIndex(s =>
                    s.TryGetValue("seriesNumber", out var sn) &&
                    (sn is JsonElement je ? je.GetInt32() : Convert.ToInt32(sn)) == request.SeriesNumber);

                var seriesDict = new Dictionary<string, object>
                {
                    { "seriesNumber", request.SeriesNumber },
                    { "shots", request.Shots ?? new List<string>() },
                    { "total", request.Total },
                    { "xCount", request.XCount },
                    { "entryMethod", request.EntryMethod ?? "ShotByShot" }
                };

                if (existingSeriesIndex >= 0)
                {
                    // Preserve existing targetPhotoUrl and reactions if they exist
                    var existingSeries = seriesList[existingSeriesIndex];
                    if (existingSeries.TryGetValue("targetPhotoUrl", out var photoUrl) && photoUrl != null)
                    {
                        var photoUrlStr = photoUrl is JsonElement je ? je.GetString() : photoUrl?.ToString();
                        if (!string.IsNullOrEmpty(photoUrlStr))
                        {
                            seriesDict["targetPhotoUrl"] = photoUrlStr;
                        }
                    }
                    if (existingSeries.TryGetValue("reactions", out var reactions) && reactions != null)
                    {
                        seriesDict["reactions"] = reactions;
                    }
                    seriesList[existingSeriesIndex] = seriesDict;
                }
                else
                {
                    seriesList.Add(seriesDict);
                }

                // Recalculate totals from all series
                int totalScore = seriesList.Sum(s =>
                    s.TryGetValue("total", out var t) ? (t is JsonElement je ? je.GetInt32() : Convert.ToInt32(t)) : 0);
                int totalXCount = seriesList.Sum(s =>
                    s.TryGetValue("xCount", out var x) ? (x is JsonElement je ? je.GetInt32() : Convert.ToInt32(x)) : 0);

                existingScore.SeriesScores = JsonSerializer.Serialize(seriesList);
                existingScore.TotalScore = totalScore;
                existingScore.XCount = totalXCount;
                existingScore.UpdatedAt = DateTime.UtcNow;
                await db.UpdateAsync(existingScore);
            }
            else
            {
                // INSERT new row with first series
                var seriesJson = JsonSerializer.Serialize(new[] { newSeries });
                var score = new TrainingScoreDbDto
                {
                    MemberId = memberId.Value,
                    TrainingMatchId = match.Id,
                    TrainingDate = DateTime.UtcNow,
                    WeaponClass = match.WeaponClass,
                    IsCompetition = false,
                    SeriesScores = seriesJson,
                    TotalScore = request.Total,
                    XCount = request.XCount,
                    Notes = $"Träningsmatch: {match.MatchName}",
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                };
                await db.InsertAsync(score);
            }

            scope.Complete();

            // Notify via SignalR (use match_{code} group format)
            // Send as object to match website format
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("ScoreUpdated", new { memberId = memberId.Value, seriesNumber = request.SeriesNumber });

            var result = new TrainingMatchScore
            {
                SeriesNumber = request.SeriesNumber,
                Total = request.Total,
                XCount = request.XCount,
                Shots = request.Shots,
                EntryMethod = request.EntryMethod ?? "ShotByShot"
            };

            return Ok(ApiResponse<TrainingMatchScore>.Ok(result));
        }

        /// <summary>
        /// Upload a target photo for a series
        /// </summary>
        [HttpPost("{code}/series/{seriesNumber}/photo")]
        [RequestSizeLimit(10 * 1024 * 1024)] // 10MB limit for raw upload
        public async Task<IActionResult> UploadSeriesPhoto(string code, int seriesNumber, IFormFile photo)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<UploadPhotoResponse>.Error("Ej inloggad"));
            }

            // Validate file
            if (photo == null || photo.Length == 0)
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error("Ingen bild bifogad"));
            }

            var maxFileSizeMB = _configuration.GetValue("TargetPhotos:MaxFileSizeMB", 5);
            if (photo.Length > maxFileSizeMB * 1024 * 1024)
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error($"Bilden är för stor (max {maxFileSizeMB}MB)"));
            }

            // Validate content type
            var allowedTypes = new[] { "image/jpeg", "image/jpg", "image/png" };
            if (!allowedTypes.Contains(photo.ContentType?.ToLower()))
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error("Endast JPEG/PNG bilder tillåtna"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse<UploadPhotoResponse>.Error("Matchen hittades inte"));
            }

            if (match.Status != "Active")
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error("Matchen är inte aktiv"));
            }

            // Check if participant
            var participant = await db.FirstOrDefaultAsync<TrainingMatchParticipantDbDto>(
                "WHERE TrainingMatchId = @0 AND MemberId = @1",
                match.Id, memberId.Value);

            if (participant == null)
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error("Du deltar inte i denna match"));
            }

            // Check if score exists for this series
            var existingScore = await db.FirstOrDefaultAsync<TrainingScoreDbDto>(
                @"WHERE MemberId = @0 AND TrainingMatchId = @1",
                memberId.Value, match.Id);

            if (existingScore == null)
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error("Du måste registrera ett resultat först"));
            }

            // Verify series exists
            var existingSeriesJson = existingScore.SeriesScores ?? "[]";
            var seriesList = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(existingSeriesJson)
                ?? new List<Dictionary<string, object>>();

            var seriesIndex = seriesList.FindIndex(s =>
                s.TryGetValue("seriesNumber", out var sn) &&
                (sn is JsonElement je ? je.GetInt32() : Convert.ToInt32(sn)) == seriesNumber);

            if (seriesIndex < 0)
            {
                return BadRequest(ApiResponse<UploadPhotoResponse>.Error($"Serie {seriesNumber} hittades inte"));
            }

            try
            {
                // Process and resize image
                using var inputStream = photo.OpenReadStream();
                var resizedImage = await _imageResizeService.ResizeImageAsync(inputStream);

                // Generate filename: {matchCode}_{memberId}_{seriesNumber}_{timestamp}.jpg
                var timestamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
                var fileName = $"{code.ToUpper()}_{memberId.Value}_{seriesNumber}_{timestamp}.jpg";

                // Ensure target-photos directory exists
                var targetDir = Path.Combine(_webHostEnvironment.WebRootPath, "media", "target-photos");
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                    _logger.LogInformation("Created target-photos directory at {Path}", targetDir);
                }

                // Save file
                var filePath = Path.Combine(targetDir, fileName);
                await System.IO.File.WriteAllBytesAsync(filePath, resizedImage);

                // Generate relative URL for storage
                var relativeUrl = $"/media/target-photos/{fileName}";

                // Update series JSON with photo URL
                var series = seriesList[seriesIndex];
                series["targetPhotoUrl"] = relativeUrl;

                existingScore.SeriesScores = JsonSerializer.Serialize(seriesList);
                existingScore.UpdatedAt = DateTime.UtcNow;
                await db.UpdateAsync(existingScore);

                scope.Complete();

                _logger.LogInformation("Saved target photo for match {MatchCode}, member {MemberId}, series {SeriesNumber}: {FileName}",
                    code.ToUpper(), memberId.Value, seriesNumber, fileName);

                // Notify via SignalR so other participants refresh their scoreboard
                await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                    .SendAsync("ScoreUpdated", new { memberId = memberId.Value, seriesNumber, hasPhoto = true });

                // Return absolute URL for the client
                var absoluteUrl = ToAbsoluteUrl(relativeUrl);
                return Ok(ApiResponse<UploadPhotoResponse>.Ok(new UploadPhotoResponse { PhotoUrl = absoluteUrl ?? relativeUrl }));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to process target photo for match {MatchCode}, member {MemberId}, series {SeriesNumber}",
                    code.ToUpper(), memberId.Value, seriesNumber);
                return StatusCode(500, ApiResponse<UploadPhotoResponse>.Error("Kunde inte spara bilden"));
            }
        }

        /// <summary>
        /// Add or toggle a reaction to a target photo
        /// Supports both JWT (mobile) and cookie-based (website) authentication
        /// </summary>
        [HttpPost("{code}/series/{seriesNumber}/reaction")]
        [Authorize(AuthenticationSchemes = "JwtBearer,Identity.Application")]
        public async Task<IActionResult> AddReaction(string code, int seriesNumber, [FromBody] AddReactionRequest request)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<List<PhotoReaction>>.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse<List<PhotoReaction>>.Error("Matchen hittades inte"));
            }

            // Validate emoji
            var allowedEmojis = new[] { "❤️", "👍", "🔥", "😢", "🎯" };
            if (!allowedEmojis.Contains(request.Emoji))
            {
                return BadRequest(ApiResponse<List<PhotoReaction>>.Error("Ogiltig emoji"));
            }

            // Find the score record for the target member
            var targetScore = await db.FirstOrDefaultAsync<TrainingScoreDbDto>(
                @"WHERE MemberId = @0 AND TrainingMatchId = @1",
                request.TargetMemberId, match.Id);

            if (targetScore == null)
            {
                return BadRequest(ApiResponse<List<PhotoReaction>>.Error("Serien hittades inte"));
            }

            // Parse series JSON
            var existingSeriesJson = targetScore.SeriesScores ?? "[]";
            var seriesList = JsonSerializer.Deserialize<List<Dictionary<string, object>>>(existingSeriesJson)
                ?? new List<Dictionary<string, object>>();

            var seriesIndex = seriesList.FindIndex(s =>
                s.TryGetValue("seriesNumber", out var sn) &&
                (sn is JsonElement je ? je.GetInt32() : Convert.ToInt32(sn)) == seriesNumber);

            if (seriesIndex < 0)
            {
                return BadRequest(ApiResponse<List<PhotoReaction>>.Error($"Serie {seriesNumber} hittades inte"));
            }

            var series = seriesList[seriesIndex];

            // Note: Reactions are now allowed on any series, not just ones with photos

            // Get current reactions
            List<PhotoReaction> reactions;
            if (series.TryGetValue("reactions", out var existingReactions) && existingReactions != null)
            {
                if (existingReactions is JsonElement jsonElement)
                {
                    reactions = JsonSerializer.Deserialize<List<PhotoReaction>>(jsonElement.GetRawText()) ?? new List<PhotoReaction>();
                }
                else
                {
                    reactions = existingReactions as List<PhotoReaction> ?? new List<PhotoReaction>();
                }
            }
            else
            {
                reactions = new List<PhotoReaction>();
            }

            // Get reactor's name
            var reactor = _memberService.GetById(memberId.Value);
            var reactorName = reactor?.GetValue<string>("firstName") ?? "Okänd";

            // Toggle reaction: If same emoji exists from this user, remove it; otherwise add/replace
            var existingReaction = reactions.FirstOrDefault(r => r.MemberId == memberId.Value);

            if (existingReaction != null)
            {
                if (existingReaction.Emoji == request.Emoji)
                {
                    // Same emoji - remove it (toggle off)
                    reactions.Remove(existingReaction);
                }
                else
                {
                    // Different emoji - replace it
                    existingReaction.Emoji = request.Emoji;
                    existingReaction.FirstName = reactorName;
                }
            }
            else
            {
                // No existing reaction - add new one
                reactions.Add(new PhotoReaction
                {
                    MemberId = memberId.Value,
                    FirstName = reactorName,
                    Emoji = request.Emoji
                });
            }

            // Update series with reactions
            series["reactions"] = JsonSerializer.SerializeToElement(reactions);

            // Save back to database
            targetScore.SeriesScores = JsonSerializer.Serialize(seriesList);
            targetScore.UpdatedAt = DateTime.UtcNow;
            await db.UpdateAsync(targetScore);

            scope.Complete();

            // Notify via SignalR
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("ReactionUpdated", new
                {
                    targetMemberId = request.TargetMemberId,
                    seriesNumber = seriesNumber,
                    reactions = reactions
                });

            return Ok(ApiResponse<List<PhotoReaction>>.Ok(reactions));
        }

        /// <summary>
        /// Get ongoing matches for current user
        /// </summary>
        [HttpGet("ongoing")]
        public async Task<IActionResult> GetOngoingMatches()
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<List<TrainingMatch>>.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var matches = await db.FetchAsync<TrainingMatchDbDto>(
                @"SELECT DISTINCT m.* FROM TrainingMatches m
                  INNER JOIN TrainingMatchParticipants p ON m.Id = p.TrainingMatchId
                  WHERE p.MemberId = @0 AND m.Status = 'Active'
                  ORDER BY m.CreatedDate DESC", memberId.Value);

            var result = await EnrichMatches(matches);

            scope.Complete();

            return Ok(ApiResponse<List<TrainingMatch>>.Ok(result));
        }

        /// <summary>
        /// Get match history with filtering and user-specific data
        /// </summary>
        [HttpGet("history")]
        public async Task<IActionResult> GetMatchHistory(
            [FromQuery] int page = 1,
            [FromQuery] int pageSize = 20,
            [FromQuery] string? weaponClass = null,
            [FromQuery] string? dateFrom = null,
            [FromQuery] string? dateTo = null,
            [FromQuery] string? searchName = null,
            [FromQuery] bool myMatchesOnly = false)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<PagedResponse<MatchHistoryItem>>.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var skip = (page - 1) * pageSize;

            // Build WHERE clause dynamically
            var whereConditions = new List<string> { "m.Status = 'Completed'" };
            var parameters = new List<object>();
            int paramIndex = 0;

            // Filter by weapon class
            if (!string.IsNullOrEmpty(weaponClass))
            {
                whereConditions.Add($"m.WeaponClass = @{paramIndex}");
                parameters.Add(weaponClass.ToUpper());
                paramIndex++;
            }

            // Filter by date range
            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var fromDate))
            {
                whereConditions.Add($"m.CompletedDate >= @{paramIndex}");
                parameters.Add(fromDate.Date);
                paramIndex++;
            }
            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var toDate))
            {
                whereConditions.Add($"m.CompletedDate < @{paramIndex}");
                parameters.Add(toDate.Date.AddDays(1)); // Include the entire end date
                paramIndex++;
            }

            // Filter by search name (match name or match code)
            if (!string.IsNullOrEmpty(searchName))
            {
                whereConditions.Add($"(m.MatchName LIKE @{paramIndex} OR m.MatchCode LIKE @{paramIndex})");
                parameters.Add($"%{searchName}%");
                paramIndex++;
            }

            // Filter by my matches only (where user is a participant)
            string joinClause = "";
            if (myMatchesOnly)
            {
                joinClause = $"INNER JOIN TrainingMatchParticipants myp ON m.Id = myp.TrainingMatchId AND myp.MemberId = @{paramIndex}";
                parameters.Add(memberId.Value);
                paramIndex++;
            }

            var whereClause = string.Join(" AND ", whereConditions);

            // Get total count
            var countSql = $@"SELECT COUNT(DISTINCT m.Id) FROM TrainingMatches m {joinClause} WHERE {whereClause}";
            var totalCount = await db.ExecuteScalarAsync<int>(countSql, parameters.ToArray());

            // Get paginated matches
            var fetchSql = $@"SELECT DISTINCT m.* FROM TrainingMatches m {joinClause}
                              WHERE {whereClause}
                              ORDER BY m.CompletedDate DESC
                              OFFSET @{paramIndex} ROWS FETCH NEXT @{paramIndex + 1} ROWS ONLY";
            parameters.Add(skip);
            parameters.Add(pageSize);

            var matches = await db.FetchAsync<TrainingMatchDbDto>(fetchSql, parameters.ToArray());

            // Enrich with user-specific data
            var items = await EnrichMatchHistoryItems(matches, memberId.Value, db);

            scope.Complete();

            var response = new PagedResponse<MatchHistoryItem>
            {
                Items = items,
                Page = page,
                PageSize = pageSize,
                TotalCount = totalCount
            };

            return Ok(ApiResponse<PagedResponse<MatchHistoryItem>>.Ok(response));
        }

        /// <summary>
        /// Enrich matches with user-specific history data
        /// </summary>
        private async Task<List<MatchHistoryItem>> EnrichMatchHistoryItems(List<TrainingMatchDbDto> matches, int currentMemberId, IDatabase db)
        {
            var result = new List<MatchHistoryItem>();

            foreach (var match in matches)
            {
                var item = new MatchHistoryItem
                {
                    Id = match.Id,
                    MatchCode = match.MatchCode,
                    MatchName = match.MatchName,
                    WeaponClass = match.WeaponClass,
                    CreatedDate = match.CreatedDate,
                    CompletedDate = match.CompletedDate,
                    IsCreator = match.CreatedByMemberId == currentMemberId
                };

                // Get participants (max 5 for display)
                var participants = await db.FetchAsync<TrainingMatchParticipantDbDto>(
                    "SELECT TOP 5 * FROM TrainingMatchParticipants WHERE TrainingMatchId = @0 ORDER BY DisplayOrder",
                    match.Id);

                item.ParticipantCount = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0", match.Id);

                // Check if current user is a participant
                var userParticipant = await db.FirstOrDefaultAsync<TrainingMatchParticipantDbDto>(
                    "WHERE TrainingMatchId = @0 AND MemberId = @1", match.Id, currentMemberId);

                item.IsParticipant = userParticipant != null;

                // If user participated, get their score and ranking
                if (item.IsParticipant)
                {
                    var userScore = await db.FirstOrDefaultAsync<TrainingScoreDbDto>(
                        "WHERE TrainingMatchId = @0 AND MemberId = @1", match.Id, currentMemberId);

                    if (userScore != null)
                    {
                        item.UserScore = userScore.TotalScore;

                        // Count series from SeriesScores JSON
                        try
                        {
                            var series = JsonSerializer.Deserialize<List<TrainingSeries>>(userScore.SeriesScores ?? "[]");
                            item.UserSeriesCount = series?.Count ?? 0;
                        }
                        catch
                        {
                            item.UserSeriesCount = 0;
                        }
                    }

                    // Calculate ranking (position among all participants by total score)
                    var allScores = await db.FetchAsync<TrainingScoreDbDto>(
                        @"SELECT ts.* FROM TrainingScores ts
                          INNER JOIN TrainingMatchParticipants tmp ON ts.MemberId = tmp.MemberId AND ts.TrainingMatchId = tmp.TrainingMatchId
                          WHERE ts.TrainingMatchId = @0
                          ORDER BY ts.TotalScore DESC", match.Id);

                    var userRank = allScores.FindIndex(s => s.MemberId == currentMemberId) + 1;
                    if (userRank > 0)
                    {
                        item.UserRanking = userRank;
                    }
                }

                // Enrich participant info for display
                foreach (var p in participants)
                {
                    var member = _memberService.GetById(p.MemberId);
                    item.Participants.Add(new MatchHistoryParticipant
                    {
                        MemberId = p.MemberId,
                        FirstName = member?.GetValue<string>("firstName") ?? "",
                        LastName = member?.GetValue<string>("lastName") ?? "",
                        ProfilePictureUrl = ToAbsoluteUrl(member?.GetValue<string>("profilePictureUrl"))
                    });
                }

                result.Add(item);
            }

            return result;
        }

        /// <summary>
        /// Get all active matches (for viewing/joining)
        /// </summary>
        [HttpGet("active")]
        public async Task<IActionResult> GetActiveMatches()
        {
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Get all active matches (for display in match list)
            var matches = await db.FetchAsync<TrainingMatchDbDto>(
                @"SELECT * FROM TrainingMatches
                  WHERE Status = 'Active'
                  ORDER BY CreatedDate DESC");

            var result = await EnrichMatches(matches);

            scope.Complete();

            return Ok(ApiResponse<List<TrainingMatch>>.Ok(result));
        }

        /// <summary>
        /// View match as spectator (read-only, no participation required)
        /// </summary>
        [HttpGet("{code}/spectator")]
        public async Task<IActionResult> ViewMatchAsSpectator(string code)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<SpectatorMatchResponse>.Error("Ej inloggad"));
            }

            var match = await GetMatchByCode(code);
            if (match == null)
            {
                return NotFound(ApiResponse<SpectatorMatchResponse>.Error("Matchen hittades inte"));
            }

            // Check if user is a participant
            var isParticipant = match.Participants.Any(p => p.MemberId == memberId.Value);

            var response = new SpectatorMatchResponse
            {
                Match = match,
                IsSpectator = !isParticipant,
                IsParticipant = isParticipant,
                CanJoin = match.IsOpen && match.Status == "Active" && !isParticipant
            };

            return Ok(ApiResponse<SpectatorMatchResponse>.Ok(response));
        }

        /// <summary>
        /// Request to join a private match
        /// </summary>
        [HttpPost("{code}/request-join")]
        public async Task<IActionResult> RequestJoinMatch(string code)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return NotFound(ApiResponse.Error("Matchen hittades inte"));
            }

            if (match.Status != "Active")
            {
                return BadRequest(ApiResponse.Error("Matchen är inte aktiv"));
            }

            // Check if already a participant
            var isParticipant = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0 AND MemberId = @1",
                match.Id, memberId.Value) > 0;

            if (isParticipant)
            {
                return BadRequest(ApiResponse.Error("Du deltar redan i denna match"));
            }

            // Check if there's already a pending request
            var existingRequest = await db.FirstOrDefaultAsync<TrainingMatchJoinRequestDbDto>(
                "WHERE TrainingMatchId = @0 AND MemberId = @1 AND Status = 'Pending'",
                match.Id, memberId.Value);

            if (existingRequest != null)
            {
                return BadRequest(ApiResponse.Error("Du har redan en väntande förfrågan"));
            }

            // Check if user is blocked
            var blockedRequest = await db.FirstOrDefaultAsync<TrainingMatchJoinRequestDbDto>(
                "WHERE TrainingMatchId = @0 AND MemberId = @1 AND Status = 'Blocked'",
                match.Id, memberId.Value);

            if (blockedRequest != null)
            {
                return BadRequest(ApiResponse.Error("Du kan inte gå med i denna match"));
            }

            // Get member info
            var member = _memberService.GetById(memberId.Value);
            var memberName = member?.Name ?? "Okänd";
            var profilePictureUrl = member?.GetValue<string>("profilePictureUrl");

            // Create join request using raw SQL with OUTPUT to get the ID reliably
            var requestId = await db.ExecuteScalarAsync<int>(
                @"INSERT INTO TrainingMatchJoinRequests (TrainingMatchId, MemberId, MemberName, MemberProfilePictureUrl, Status, RequestDate)
                  OUTPUT INSERTED.Id
                  VALUES (@0, @1, @2, @3, @4, @5)",
                match.Id, memberId.Value, memberName, profilePictureUrl ?? "", "Pending", DateTime.UtcNow);

            scope.Complete();

            // Notify match owner via SignalR (use organizer_{matchCode} group - match host must join this group)
            await _hubContext.Clients.Group($"organizer_{match.MatchCode}")
                .SendAsync("JoinRequestReceived", new
                {
                    requestId = requestId,
                    matchCode = match.MatchCode,
                    memberId = memberId.Value,
                    memberName = memberName,
                    profilePictureUrl = ToAbsoluteUrl(profilePictureUrl)
                });

            return Ok(ApiResponse.Ok("Din förfrågan har skickats till matchvärden"));
        }

        /// <summary>
        /// Respond to a join request (accept or block)
        /// </summary>
        [HttpPost("respond-join")]
        public async Task<IActionResult> RespondToJoinRequest([FromBody] RespondJoinRequest request)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find join request
            var joinRequest = await db.FirstOrDefaultAsync<TrainingMatchJoinRequestDbDto>(
                "WHERE Id = @0", request.RequestId);

            if (joinRequest == null)
            {
                return NotFound(ApiResponse.Error("Förfrågan hittades inte"));
            }

            // Find match
            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE Id = @0", joinRequest.TrainingMatchId);

            if (match == null)
            {
                return NotFound(ApiResponse.Error("Matchen hittades inte"));
            }

            // Only match creator can respond
            if (match.CreatedByMemberId != memberId.Value)
            {
                return Forbid();
            }

            // Update request status
            joinRequest.Status = request.Action == "Accept" ? "Accepted" : "Blocked";
            joinRequest.ResponseDate = DateTime.UtcNow;
            joinRequest.ResponseByMemberId = memberId.Value;
            await db.UpdateAsync(joinRequest);

            if (request.Action == "Accept")
            {
                // Add user as participant
                var maxOrder = await db.ExecuteScalarAsync<int>(
                    "SELECT ISNULL(MAX(DisplayOrder), 0) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0",
                    match.Id);

                // Calculate handicap if needed
                decimal? frozenHandicap = null;
                bool? frozenIsProvisional = null;

                if (match.HasHandicap)
                {
                    try
                    {
                        var member = _memberService.GetById(joinRequest.MemberId);
                        var shooterClass = member?.GetValue<string>("precisionShooterClass");
                        var stats = await _statisticsService.GetStatisticsAsync(joinRequest.MemberId, match.WeaponClass);
                        var profile = _handicapCalculator.CalculateHandicap(stats, shooterClass);
                        frozenHandicap = profile.HandicapPerSeries;
                        frozenIsProvisional = profile.IsProvisional;
                    }
                    catch { }
                }

                var participant = new TrainingMatchParticipantDbDto
                {
                    TrainingMatchId = match.Id,
                    MemberId = joinRequest.MemberId,
                    JoinedDate = DateTime.UtcNow,
                    DisplayOrder = maxOrder + 1,
                    FrozenHandicapPerSeries = frozenHandicap,
                    FrozenIsProvisional = frozenIsProvisional
                };

                await db.InsertAsync(participant);

                // Notify the requester that they've been accepted (use member_{memberId} group - user auto-joins on connect)
                await _hubContext.Clients.Group($"member_{joinRequest.MemberId}")
                    .SendAsync("JoinRequestAccepted", match.MatchCode);

                // Notify all participants in the match group
                await _hubContext.Clients.Group($"match_{match.MatchCode}")
                    .SendAsync("ParticipantJoined", joinRequest.MemberId);
            }
            else
            {
                // Notify the requester that they've been blocked (use member_{memberId} group)
                await _hubContext.Clients.Group($"member_{joinRequest.MemberId}")
                    .SendAsync("JoinRequestBlocked", match.MatchCode);
            }

            scope.Complete();

            return Ok(ApiResponse.Ok(request.Action == "Accept" ? "Förfrågan godkänd" : "Förfrågan avvisad"));
        }

        /// <summary>
        /// Set the shooter class for the current member
        /// Used when joining a handicap match and the user hasn't set their class yet
        /// </summary>
        [HttpPost("set-shooter-class")]
        public IActionResult SetShooterClass([FromBody] SetShooterClassRequest request)
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            try
            {
                var member = _memberService.GetById(memberId.Value);
                if (member == null)
                {
                    return NotFound(ApiResponse.Error("Medlem hittades inte"));
                }

                // Validate shooter class
                var validClasses = new[]
                {
                    "Klass 1 - Nybörjare",
                    "Klass 2 - Guldmärkesskytt",
                    "Klass 3 - Riksmästare"
                };

                if (string.IsNullOrEmpty(request.ShooterClass) || !validClasses.Contains(request.ShooterClass))
                {
                    return BadRequest(ApiResponse.Error("Ogiltig skytteklass"));
                }

                // Save to member profile
                member.SetValue("precisionShooterClass", request.ShooterClass);
                _memberService.Save(member);

                return Ok(new SetShooterClassResponse
                {
                    Success = true,
                    Message = "Skytteklass sparad",
                    ShooterClass = request.ShooterClass
                });
            }
            catch (Exception ex)
            {
                return BadRequest(ApiResponse.Error("Fel vid sparande: " + ex.Message));
            }
        }

        /// <summary>
        /// Get the current member's shooter class
        /// </summary>
        [HttpGet("shooter-class")]
        public IActionResult GetShooterClass()
        {
            var memberId = GetCurrentMemberId();
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ej inloggad"));
            }

            var member = _memberService.GetById(memberId.Value);
            if (member == null)
            {
                return NotFound(ApiResponse.Error("Medlem hittades inte"));
            }

            var shooterClass = member.GetValue<string>("precisionShooterClass");

            return Ok(new SetShooterClassResponse
            {
                Success = true,
                ShooterClass = shooterClass
            });
        }

        /// <summary>
        /// Get match by code with full details
        /// </summary>
        private async Task<TrainingMatch?> GetMatchByCode(string code)
        {
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var match = await db.FirstOrDefaultAsync<TrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                return null;
            }

            var result = await EnrichMatch(match, db);

            scope.Complete();

            return result;
        }

        /// <summary>
        /// Enrich match with participant and score data
        /// </summary>
        private async Task<TrainingMatch> EnrichMatch(TrainingMatchDbDto match, IDatabase db)
        {
            var participants = await db.FetchAsync<TrainingMatchParticipantDbDto>(
                "WHERE TrainingMatchId = @0 ORDER BY DisplayOrder", match.Id);

            var result = new TrainingMatch
            {
                Id = match.Id,
                MatchCode = match.MatchCode,
                MatchName = match.MatchName,
                CreatedByMemberId = match.CreatedByMemberId,
                WeaponClass = match.WeaponClass,
                CreatedDate = match.CreatedDate,
                Status = match.Status,
                CompletedDate = match.CompletedDate,
                StartDate = match.StartDate,
                IsHandicapEnabled = match.HasHandicap,
                IsOpen = match.IsOpen,
                MaxSeriesCount = match.MaxSeriesCount
            };

            // Get creator name
            var creator = _memberService.GetById(match.CreatedByMemberId);
            result.CreatedByName = creator?.Name ?? "Okänd";

            // Enrich participants
            foreach (var p in participants)
            {
                var member = _memberService.GetById(p.MemberId);
                var participant = new TrainingMatchParticipant
                {
                    Id = p.Id,
                    TrainingMatchId = p.TrainingMatchId,
                    MemberId = p.MemberId,
                    FirstName = member?.GetValue<string>("firstName"),
                    LastName = member?.GetValue<string>("lastName"),
                    ProfilePictureUrl = ToAbsoluteUrl(member?.GetValue<string>("profilePictureUrl")),
                    JoinedDate = p.JoinedDate,
                    DisplayOrder = p.DisplayOrder,
                    HandicapPerSeries = p.FrozenHandicapPerSeries,
                    IsProvisional = p.FrozenIsProvisional
                };

                // Get scores for this participant
                var scores = await db.FetchAsync<TrainingScoreDbDto>(
                    "WHERE MemberId = @0 AND TrainingMatchId = @1",
                    p.MemberId, match.Id);

                foreach (var score in scores)
                {
                    var series = JsonSerializer.Deserialize<List<TrainingSeries>>(score.SeriesScores ?? "[]");
                    if (series != null)
                    {
                        foreach (var s in series)
                        {
                            participant.Scores.Add(new TrainingMatchScore
                            {
                                Id = score.Id,
                                SeriesNumber = s.SeriesNumber,
                                Total = s.Total,
                                XCount = s.XCount,
                                Shots = s.Shots,
                                EntryMethod = s.EntryMethod,
                                TargetPhotoUrl = ToAbsoluteUrl(s.TargetPhotoUrl),
                                Reactions = s.Reactions
                            });
                        }
                    }
                }

                result.Participants.Add(participant);
            }

            return result;
        }

        /// <summary>
        /// Enrich multiple matches
        /// </summary>
        private async Task<List<TrainingMatch>> EnrichMatches(List<TrainingMatchDbDto> matches)
        {
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var result = new List<TrainingMatch>();
            foreach (var match in matches)
            {
                result.Add(await EnrichMatch(match, db));
            }

            scope.Complete();

            return result;
        }
    }

    // Database DTOs
    [TableName("TrainingMatches")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingMatchDbDto
    {
        public int Id { get; set; }
        public string MatchCode { get; set; } = string.Empty;
        public string? MatchName { get; set; }
        public int CreatedByMemberId { get; set; }
        public string WeaponClass { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
        public string Status { get; set; } = "Active";
        public DateTime? CompletedDate { get; set; }
        public DateTime? StartDate { get; set; }
        public bool IsOpen { get; set; } = true;
        public bool HasHandicap { get; set; }
        public int? MaxSeriesCount { get; set; }
    }

    [TableName("TrainingMatchParticipants")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingMatchParticipantDbDto
    {
        public int Id { get; set; }
        public int TrainingMatchId { get; set; }
        public int MemberId { get; set; }
        public DateTime JoinedDate { get; set; }
        public int DisplayOrder { get; set; }
        public decimal? FrozenHandicapPerSeries { get; set; }
        public bool? FrozenIsProvisional { get; set; }
    }

    [TableName("TrainingScores")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingScoreDbDto
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public int? TrainingMatchId { get; set; }
        public DateTime TrainingDate { get; set; }
        public string WeaponClass { get; set; } = string.Empty;
        public bool IsCompetition { get; set; }
        public string? SeriesScores { get; set; }
        public int TotalScore { get; set; }
        public int XCount { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    // Request DTOs
    public class CreateMatchRequest
    {
        public string? MatchName { get; set; }
        public string? WeaponClass { get; set; }
        public DateTime? StartDate { get; set; }
        public bool IsOpen { get; set; } = true;
        public bool HasHandicap { get; set; }
    }

    public class SaveScoreRequest
    {
        public int SeriesNumber { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
        public List<string>? Shots { get; set; }
        public string? EntryMethod { get; set; }
    }

    public class AddReactionRequest
    {
        public int TargetMemberId { get; set; }
        public string Emoji { get; set; } = string.Empty;
    }

    public class RespondJoinRequest
    {
        public int RequestId { get; set; }
        public string Action { get; set; } = string.Empty; // "Accept" or "Block"
    }

    public class SpectatorMatchResponse
    {
        public TrainingMatch Match { get; set; } = null!;
        public bool IsSpectator { get; set; }
        public bool IsParticipant { get; set; }
        public bool CanJoin { get; set; }
    }

    [TableName("TrainingMatchJoinRequests")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingMatchJoinRequestDbDto
    {
        public int Id { get; set; }
        public int TrainingMatchId { get; set; }
        public int MemberId { get; set; }
        public string MemberName { get; set; } = string.Empty;
        public string? MemberProfilePictureUrl { get; set; }
        public string Status { get; set; } = "Pending"; // Pending, Accepted, Blocked
        public DateTime RequestDate { get; set; }
        public DateTime? ResponseDate { get; set; }
        public int? ResponseByMemberId { get; set; }
    }

    /// <summary>
    /// DTO for match history items with user-specific data
    /// </summary>
    public class MatchHistoryItem
    {
        public int Id { get; set; }
        public string MatchCode { get; set; } = string.Empty;
        public string? MatchName { get; set; }
        public string WeaponClass { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
        public DateTime? CompletedDate { get; set; }
        public int ParticipantCount { get; set; }
        public List<MatchHistoryParticipant> Participants { get; set; } = new();

        // User-specific fields
        public bool IsCreator { get; set; }
        public bool IsParticipant { get; set; }
        public int? UserScore { get; set; }
        public int? UserSeriesCount { get; set; }
        public int? UserRanking { get; set; }

        // Computed properties
        public string DisplayName => !string.IsNullOrWhiteSpace(MatchName) ? MatchName : MatchCode;
    }

    /// <summary>
    /// Participant info for match history display
    /// </summary>
    public class MatchHistoryParticipant
    {
        public int MemberId { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? ProfilePictureUrl { get; set; }
        public string Initials => $"{FirstName?.FirstOrDefault()}{LastName?.FirstOrDefault()}".ToUpper();
    }

    /// <summary>
    /// Request to set shooter class
    /// </summary>
    public class SetShooterClassRequest
    {
        public string? ShooterClass { get; set; }
    }

    /// <summary>
    /// Response for shooter class operations
    /// </summary>
    public class SetShooterClassResponse
    {
        public bool Success { get; set; }
        public string? Message { get; set; }
        public string? ShooterClass { get; set; }
    }

    /// <summary>
    /// Response for target photo upload
    /// </summary>
    public class UploadPhotoResponse
    {
        public string PhotoUrl { get; set; } = string.Empty;
    }
}
