using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Controller for handling guest QR code claims and match access
    /// </summary>
    [Route("match")]
    public class GuestMatchController : Controller
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<GuestMatchController> _logger;

        public GuestMatchController(IScopeProvider scopeProvider, ILogger<GuestMatchController> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        /// <summary>
        /// Handle QR code claim - validates token, sets session cookie, redirects to match
        /// GET /match/{code}/guest/{token}
        /// </summary>
        [HttpGet("{code}/guest/{token}")]
        public async Task<IActionResult> ClaimGuestSpot(string code, string token)
        {
            _logger.LogInformation("ClaimGuestSpot called with code={Code}, token={Token}", code, token);

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("ClaimGuestSpot: Empty code or token");
                return RedirectToErrorPage("Ogiltig länk");
            }

            // Parse token
            if (!Guid.TryParse(token, out var claimToken))
            {
                _logger.LogWarning("ClaimGuestSpot: Could not parse token as GUID: {Token}", token);
                return RedirectToErrorPage("Ogiltig token");
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find the match
            var match = await db.FirstOrDefaultAsync<GuestTrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                _logger.LogWarning("ClaimGuestSpot: Match not found for code {Code}", code);
                scope.Complete();
                return RedirectToErrorPage("Matchen hittades inte");
            }

            if (match.Status != "Active")
            {
                _logger.LogWarning("ClaimGuestSpot: Match {Code} is not active (status={Status})", code, match.Status);
                scope.Complete();
                return RedirectToErrorPage("Matchen är inte aktiv");
            }

            // Look up the claim token in TrainingMatchGuests
            var guest = await db.FirstOrDefaultAsync<GuestParticipantClaimDbDto>(
                "WHERE ClaimToken = @0", claimToken);

            if (guest == null)
            {
                _logger.LogWarning("ClaimGuestSpot: No guest found with ClaimToken {ClaimToken}", claimToken);
                scope.Complete();
                return RedirectToErrorPage("Ogiltig inbjudningstoken");
            }

            _logger.LogInformation("ClaimGuestSpot: Found guest {GuestId} ({DisplayName})", guest.Id, guest.DisplayName);

            // Check if token has expired
            if (guest.ClaimTokenExpiresAt.HasValue && guest.ClaimTokenExpiresAt.Value < DateTime.UtcNow)
            {
                scope.Complete();
                return RedirectToErrorPage("Inbjudningslänken har gått ut. Be arrangören skapa en ny.");
            }

            // Verify guest is participant in this match
            var participant = await db.FirstOrDefaultAsync<GuestParticipantEntryDbDto>(
                "WHERE TrainingMatchId = @0 AND GuestParticipantId = @1",
                match.Id, guest.Id);

            if (participant == null)
            {
                scope.Complete();
                return RedirectToErrorPage("Du är inte inbjuden till denna match");
            }

            // Generate session token (valid for 24 hours or until match ends)
            var sessionToken = Guid.NewGuid();
            var sessionExpiry = DateTime.UtcNow.AddHours(24);

            // Update guest with session token (clear claim token - single use)
            await db.ExecuteAsync(
                @"UPDATE TrainingMatchGuests
                  SET SessionToken = @0, SessionExpiresAt = @1, ClaimToken = NULL, ClaimTokenExpiresAt = NULL
                  WHERE Id = @2",
                sessionToken, sessionExpiry, guest.Id);

            scope.Complete();

            // Set session cookie
            // Path must be "/" so the cookie is sent with API calls to /umbraco/surface/...
            var cookieOptions = new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = sessionExpiry,
                Path = "/"
            };

            Response.Cookies.Append($"guest_session_{code.ToUpper()}", sessionToken.ToString(), cookieOptions);

            // Redirect to match view
            return Redirect($"/traningsmatch/?join={code.ToUpper()}");
        }

        /// <summary>
        /// Validate guest session and get guest info
        /// Used by other controllers to check guest authentication
        /// </summary>
        public static async Task<GuestSessionInfo?> ValidateGuestSession(
            string matchCode,
            HttpRequest request,
            IDatabase db)
        {
            // Check for guest session cookie
            var cookieName = $"guest_session_{matchCode.ToUpper()}";
            if (!request.Cookies.TryGetValue(cookieName, out var sessionTokenStr))
            {
                return null;
            }

            if (!Guid.TryParse(sessionTokenStr, out var sessionToken))
            {
                return null;
            }

            // Look up session token
            var guest = await db.FirstOrDefaultAsync<GuestParticipantClaimDbDto>(
                "WHERE SessionToken = @0 AND SessionExpiresAt > @1",
                sessionToken, DateTime.UtcNow);

            if (guest == null)
            {
                return null;
            }

            // Verify guest is in this match
            var match = await db.FirstOrDefaultAsync<GuestTrainingMatchDbDto>(
                "WHERE MatchCode = @0", matchCode.ToUpper());

            if (match == null)
            {
                return null;
            }

            var participant = await db.FirstOrDefaultAsync<GuestParticipantEntryDbDto>(
                "WHERE TrainingMatchId = @0 AND GuestParticipantId = @1",
                match.Id, guest.Id);

            if (participant == null)
            {
                return null;
            }

            return new GuestSessionInfo
            {
                GuestId = guest.Id,
                DisplayName = guest.DisplayName,
                MatchId = match.Id,
                MatchCode = match.MatchCode,
                ParticipantId = participant.Id
            };
        }

        private IActionResult RedirectToErrorPage(string message)
        {
            // URL encode the message for the query string
            var encodedMessage = Uri.EscapeDataString(message);
            return Redirect($"/traningsmatch/?error={encodedMessage}");
        }
    }

    /// <summary>
    /// Information about an authenticated guest session
    /// </summary>
    public class GuestSessionInfo
    {
        public int GuestId { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public int MatchId { get; set; }
        public string MatchCode { get; set; } = string.Empty;
        public int ParticipantId { get; set; }
    }

    // Database DTOs for GuestMatchController (named differently to avoid conflicts with MatchApiController)
    [TableName("TrainingMatches")]
    [PrimaryKey("Id", AutoIncrement = true)]
    internal class GuestTrainingMatchDbDto
    {
        public int Id { get; set; }
        public string MatchCode { get; set; } = string.Empty;
        public string Status { get; set; } = "Active";
    }

    [TableName("TrainingMatchGuests")]
    [PrimaryKey("Id", AutoIncrement = true)]
    internal class GuestParticipantClaimDbDto
    {
        public int Id { get; set; }
        public string DisplayName { get; set; } = string.Empty;
        public Guid? ClaimToken { get; set; }
        public DateTime? ClaimTokenExpiresAt { get; set; }
        public Guid? SessionToken { get; set; }
        public DateTime? SessionExpiresAt { get; set; }
    }

    [TableName("TrainingMatchParticipants")]
    [PrimaryKey("Id", AutoIncrement = true)]
    internal class GuestParticipantEntryDbDto
    {
        public int Id { get; set; }
        public int TrainingMatchId { get; set; }
        public int? GuestParticipantId { get; set; }
    }
}
