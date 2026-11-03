using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using HpskSite.Hubs;

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
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly Services.ClubService _clubService;
        private readonly IHubContext<TrainingMatchHub> _hubContext;
        private readonly IAntiforgery _antiforgery;
        private readonly ITimeLimitedDataProtector _joinedTicket;

        /// <summary>Hur länge "du är med"-sidan efter en claim gäller (säkerhetsplanen 2026-11-02, H2).</summary>
        private static readonly TimeSpan JoinedTicketLifetime = TimeSpan.FromMinutes(30);

        public GuestMatchController(
            IScopeProvider scopeProvider,
            ILogger<GuestMatchController> logger,
            IMemberService memberService,
            IMemberManager memberManager,
            Services.ClubService clubService,
            IHubContext<TrainingMatchHub> hubContext,
            IAntiforgery antiforgery,
            IDataProtectionProvider dataProtection)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _hubContext = hubContext;
            _antiforgery = antiforgery;
            // ⚠️ Purpose-strängen byts aldrig (utfärdade biljetter slutar annars gälla).
            _joinedTicket = dataProtection.CreateProtector("Match.MemberClaim.Joined.v1").ToTimeLimitedDataProtector();
        }

        /// <summary>
        /// Handle QR code claim - validates token, sets session cookie, redirects to match
        /// GET /match/{code}/guest/{token}
        /// </summary>
        [HttpGet("{code}/guest/{token}")]
        public async Task<IActionResult> ClaimGuestSpot(string code, string token)
        {
            // ⚠️ Själva token loggas aldrig (säkerhetsplanen 2026-11-02): den är en bärarbehörighet.
            _logger.LogInformation("ClaimGuestSpot called with code={Code}", code);

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("ClaimGuestSpot: Empty code or token");
                return RedirectToErrorPage("Ogiltig länk");
            }

            // Parse token
            if (!Guid.TryParse(token, out var claimToken))
            {
                _logger.LogWarning("ClaimGuestSpot: Could not parse token as GUID");
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
                _logger.LogWarning("ClaimGuestSpot: No guest found for the claim token");
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
        /// Handle member claim QR code - shows confirmation page
        /// GET /match/{code}/member/{token}
        /// </summary>
        [HttpGet("{code}/member/{token}")]
        public async Task<IActionResult> ClaimMemberSpot(string code, string token)
        {
            _logger.LogInformation("ClaimMemberSpot called with code={Code}", code);

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(token))
            {
                _logger.LogWarning("ClaimMemberSpot: Empty code or token");
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Ogiltig länk"
                });
            }

            // Parse token
            if (!Guid.TryParse(token, out var claimToken))
            {
                _logger.LogWarning("ClaimMemberSpot: Could not parse token as GUID");
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Ogiltig token"
                });
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find the match
            var match = await db.FirstOrDefaultAsync<GuestTrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                _logger.LogWarning("ClaimMemberSpot: Match not found for code {Code}", code);
                scope.Complete();
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Matchen hittades inte"
                });
            }

            if (match.Status != "Active")
            {
                _logger.LogWarning("ClaimMemberSpot: Match {Code} is not active (status={Status})", code, match.Status);
                scope.Complete();
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Matchen är inte aktiv"
                });
            }

            // Look up the claim token
            var claimRecord = await db.FirstOrDefaultAsync<GuestParticipantClaimDbDto>(
                "WHERE ClaimToken = @0 AND LinkedMemberId IS NOT NULL", claimToken);

            if (claimRecord == null)
            {
                _logger.LogWarning("ClaimMemberSpot: No member claim found for the token");
                scope.Complete();
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Ogiltig inbjudningstoken"
                });
            }

            _logger.LogInformation("ClaimMemberSpot: Found claim {ClaimId} for member {LinkedMemberId} ({DisplayName})",
                claimRecord.Id, claimRecord.LinkedMemberId, claimRecord.DisplayName);

            // Check if token has expired
            if (claimRecord.ClaimTokenExpiresAt.HasValue && claimRecord.ClaimTokenExpiresAt.Value < DateTime.UtcNow)
            {
                scope.Complete();
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Inbjudningslänken har gått ut. Be arrangören skapa en ny."
                });
            }

            // Load teams for team matches
            var teams = new List<MemberClaimTeamOption>();
            if (match.IsTeamMatch)
            {
                var teamList = await db.FetchAsync<TrainingMatchTeamDto>(
                    @"SELECT t.Id, t.TeamName, t.ClubId, t.TeamNumber,
                             (SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TeamId = t.Id) AS MemberCount
                      FROM TrainingMatchTeams t
                      WHERE t.TrainingMatchId = @0
                      ORDER BY t.TeamNumber", match.Id);

                foreach (var team in teamList)
                {
                    var isFull = match.MaxShootersPerTeam.HasValue && team.MemberCount >= match.MaxShootersPerTeam;
                    teams.Add(new MemberClaimTeamOption
                    {
                        Id = team.Id,
                        TeamName = team.TeamName,
                        ClubName = team.ClubId.HasValue ? _clubService.GetClubNameById(team.ClubId.Value) : null,
                        MemberCount = team.MemberCount,
                        IsFull = isFull
                    });
                }
            }

            scope.Complete();

            // Look up member details for confirmation display
            string? clubName = null;
            string? memberEmail = null;
            var member = _memberService.GetById(claimRecord.LinkedMemberId!.Value);
            if (member != null)
            {
                memberEmail = member.Email;
                var clubId = member.GetValue<int>("primaryClubId");
                if (clubId > 0)
                {
                    clubName = _clubService.GetClubNameById(clubId);
                }
            }

            // Show confirmation page
            return View("MemberClaimConfirmation", new MemberClaimViewModel
            {
                ClaimToken = token,
                MatchCode = code.ToUpper(),
                MemberName = claimRecord.DisplayName,
                // Den som bara har länken får inte se hela adressen (säkerhetsplanen 2026-11-02).
                MemberEmail = HpskSite.Services.Security.MaskedEmail.Mask(memberEmail),
                ClubName = clubName,
                IsValid = true,
                IsTeamMatch = match.IsTeamMatch,
                IsOpen = match.IsOpen,
                Teams = teams,
                MaxShootersPerTeam = match.MaxShootersPerTeam
            });
        }

        /// <summary>
        /// Process member claim confirmation
        /// POST /match/{code}/member/confirm
        /// </summary>
        [HttpPost("{code}/member/confirm")]
        [IgnoreAntiforgeryToken] // tokenen prövas för hand i metoden (svensk felsida i stället för 400)
        public async Task<IActionResult> ConfirmMemberClaim(string code, [FromForm] string claimToken, [FromForm] int? teamId = null)
        {
            _logger.LogInformation("ConfirmMemberClaim called with code={Code}", code);

            // Formuläret har antiforgery-token (säkerhetsplanen 2026-11-02). Ett nekat formulär får en
            // svensk förklaring, inte ett tomt 400.
            try
            {
                await _antiforgery.ValidateRequestAsync(HttpContext);
            }
            catch (AntiforgeryValidationException)
            {
                _logger.LogWarning("SEC: ConfirmMemberClaim nekades — antiforgery-token saknades eller var ogiltigt");
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Sidan har varit öppen för länge. Skanna QR-koden igen och försök på nytt. Inget har sparats."
                });
            }

            if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(claimToken))
            {
                return RedirectToErrorPage("Ogiltig förfrågan");
            }

            if (!Guid.TryParse(claimToken, out var tokenGuid))
            {
                return RedirectToErrorPage("Ogiltig token");
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Find the match
            var match = await db.FirstOrDefaultAsync<GuestTrainingMatchDbDto>(
                "WHERE MatchCode = @0", code.ToUpper());

            if (match == null)
            {
                scope.Complete();
                return RedirectToErrorPage("Matchen hittades inte");
            }

            if (match.Status != "Active")
            {
                scope.Complete();
                return RedirectToErrorPage("Matchen är inte aktiv");
            }

            // Look up the claim token
            var claimRecord = await db.FirstOrDefaultAsync<GuestParticipantClaimDbDto>(
                "WHERE ClaimToken = @0 AND LinkedMemberId IS NOT NULL", tokenGuid);

            if (claimRecord == null)
            {
                scope.Complete();
                return RedirectToErrorPage("Ogiltig inbjudningstoken");
            }

            // Check if token has expired
            if (claimRecord.ClaimTokenExpiresAt.HasValue && claimRecord.ClaimTokenExpiresAt.Value < DateTime.UtcNow)
            {
                scope.Complete();
                return RedirectToErrorPage("Inbjudningslänken har gått ut. Be arrangören skapa en ny.");
            }

            // Check if member is already in the match
            var existingParticipant = await db.FirstOrDefaultAsync<MemberParticipantEntryDbDto>(
                "WHERE TrainingMatchId = @0 AND MemberId = @1",
                match.Id, claimRecord.LinkedMemberId!.Value);

            if (existingParticipant != null)
            {
                scope.Complete();
                return RedirectToErrorPage("Du är redan med i matchen");
            }

            // Get the member to sign them in
            var member = _memberService.GetById(claimRecord.LinkedMemberId!.Value);
            if (member == null)
            {
                scope.Complete();
                return RedirectToErrorPage("Medlemmen hittades inte i systemet");
            }

            // Get member's email to find identity user
            var memberEmail = member.Email;
            if (string.IsNullOrEmpty(memberEmail))
            {
                scope.Complete();
                return RedirectToErrorPage("Medlemmen saknar e-postadress");
            }

            // Get next display order
            var maxOrder = await db.ExecuteScalarAsync<int>(
                "SELECT ISNULL(MAX(DisplayOrder), 0) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0",
                match.Id);

            // Validate team for team matches
            if (match.IsTeamMatch && teamId.HasValue)
            {
                // Verify team belongs to this match
                var teamExists = await db.ExecuteScalarAsync<int>(
                    "SELECT COUNT(*) FROM TrainingMatchTeams WHERE Id = @0 AND TrainingMatchId = @1",
                    teamId.Value, match.Id);
                if (teamExists == 0)
                {
                    scope.Complete();
                    return RedirectToErrorPage("Ogiltigt lag");
                }

                // Check team capacity
                if (match.MaxShootersPerTeam.HasValue)
                {
                    var teamMemberCount = await db.ExecuteScalarAsync<int>(
                        "SELECT COUNT(*) FROM TrainingMatchParticipants WHERE TrainingMatchId = @0 AND TeamId = @1",
                        match.Id, teamId.Value);
                    if (teamMemberCount >= match.MaxShootersPerTeam.Value)
                    {
                        scope.Complete();
                        return RedirectToErrorPage("Laget är fullt");
                    }
                }
            }

            // Create participant with MemberId (not GuestParticipantId)
            var participant = new MemberParticipantEntryDbDto
            {
                TrainingMatchId = match.Id,
                MemberId = claimRecord.LinkedMemberId!.Value,
                GuestParticipantId = null, // This is a member claim, not a guest
                JoinedDate = DateTime.UtcNow,
                DisplayOrder = maxOrder + 1,
                TeamId = teamId
            };

            await db.InsertAsync(participant);

            // Clear the claim token (single use)
            await db.ExecuteAsync(
                @"UPDATE TrainingMatchGuests
                  SET ClaimToken = NULL, ClaimTokenExpiresAt = NULL
                  WHERE Id = @0",
                claimRecord.Id);

            scope.Complete();

            // ⚠️ SÄKERHETSPLANEN 2026-11-02 (A1/H2): ingen session skapas här. Förut anropades
            // SignInAsync för medlemmen, utan lösenord — så den som skapat en claim för ett annat konto
            // (även en sajtadmin) kunde själv bekräfta länken och bli inloggad som det kontot. Deltagaren är
            // redan inlagd; nu visas "du är med"-sidan, som låter personen logga in på vanligt sätt.
            _logger.LogInformation("ConfirmMemberClaim: Member {MemberId} joined match {MatchCode} via claim (ingen session skapad)",
                claimRecord.LinkedMemberId, code);

            // Notify via SignalR so creator's scoreboard updates
            await _hubContext.Clients.Group($"match_{code.ToUpper()}")
                .SendAsync("ParticipantJoined", claimRecord.LinkedMemberId!.Value);

            var ticket = _joinedTicket.Protect($"{claimRecord.LinkedMemberId.Value}|{code.ToUpper()}", JoinedTicketLifetime);
            return Redirect($"/match/{code.ToUpper()}/member/joined?t={Uri.EscapeDataString(ticket)}");
        }

        /// <summary>
        /// "Du är med i matchen" — visas efter en bekräftad claim. Skapar INGEN session. Är besökaren redan
        /// inloggad som medlemmen går hen rakt in i matchen; annars väljer hen att logga in, få en
        /// återställningslänk eller fortsätta utan att logga in.
        /// GET /match/{code}/member/joined?t=...
        /// </summary>
        [HttpGet("{code}/member/joined")]
        public async Task<IActionResult> MemberClaimJoined(string code, string? t)
        {
            var matchCode = (code ?? "").ToUpper();
            if (!TryReadJoinedTicket(t, matchCode, out var memberId))
            {
                return View("MemberClaimConfirmation", new MemberClaimViewModel
                {
                    ErrorMessage = "Länken har gått ut. Skanna QR-koden igen om du vill gå med."
                });
            }

            var member = _memberService.GetById(memberId);
            if (member == null)
            {
                return View("MemberClaimConfirmation", new MemberClaimViewModel { ErrorMessage = "Medlemmen hittades inte i systemet." });
            }

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current != null && current.Key == member.Key)
            {
                return Redirect($"/traningsmatch/?join={matchCode}");
            }

            var matchUrl = $"/traningsmatch/?join={matchCode}";
            return View("MemberClaimJoined", new MemberClaimJoinedViewModel
            {
                MatchCode = matchCode,
                MemberName = string.IsNullOrWhiteSpace(member.Name) ? "medlemmen" : member.Name,
                SignedInAsOther = current != null ? (current.Name ?? "ett annat konto") : null,
                LoginUrl = $"/login-register/?returnUrl={Uri.EscapeDataString(matchUrl)}",
                ForgotUrl = $"/login-register/?forgot=1&returnUrl={Uri.EscapeDataString(matchUrl)}",
                ContinueUrl = "/traningsmatch/"
            });
        }

        /// <summary>Läser "du är med"-biljetten: giltig, inte utgången och utfärdad för just den här matchen.</summary>
        private bool TryReadJoinedTicket(string? ticket, string matchCode, out int memberId)
        {
            memberId = 0;
            if (string.IsNullOrEmpty(ticket)) return false;
            try
            {
                var payload = _joinedTicket.Unprotect(ticket);
                var parts = payload.Split('|');
                return parts.Length == 2
                    && int.TryParse(parts[0], out memberId)
                    && string.Equals(parts[1], matchCode, StringComparison.OrdinalIgnoreCase);
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return false; // utgången eller förfalskad
            }
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
        public bool IsTeamMatch { get; set; }
        public bool IsOpen { get; set; }
        public int? MaxShootersPerTeam { get; set; }
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
        public int? LinkedMemberId { get; set; }
    }

    [TableName("TrainingMatchParticipants")]
    [PrimaryKey("Id", AutoIncrement = true)]
    internal class GuestParticipantEntryDbDto
    {
        public int Id { get; set; }
        public int TrainingMatchId { get; set; }
        public int? GuestParticipantId { get; set; }
    }

    [TableName("TrainingMatchParticipants")]
    [PrimaryKey("Id", AutoIncrement = true)]
    internal class MemberParticipantEntryDbDto
    {
        public int Id { get; set; }
        public int TrainingMatchId { get; set; }
        public int? MemberId { get; set; }
        public int? GuestParticipantId { get; set; }
        public DateTime JoinedDate { get; set; }
        public int DisplayOrder { get; set; }
        public int? TeamId { get; set; }
    }

    /// <summary>
    /// DTO for querying teams with member count
    /// </summary>
    internal class TrainingMatchTeamDto
    {
        public int Id { get; set; }
        public int TrainingMatchId { get; set; }
        public int TeamNumber { get; set; }
        public string TeamName { get; set; } = string.Empty;
        public int? ClubId { get; set; }
        public int MemberCount { get; set; }
    }

    /// <summary>
    /// ViewModel for member claim confirmation page
    /// </summary>
    public class MemberClaimViewModel
    {
        public string? ClaimToken { get; set; }
        public string? MatchCode { get; set; }
        public string? MemberName { get; set; }
        public string? MemberEmail { get; set; }
        public string? ClubName { get; set; }
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }

        // Team match support
        public bool IsTeamMatch { get; set; }
        public bool IsOpen { get; set; }
        public List<MemberClaimTeamOption> Teams { get; set; } = new();
        public int? MaxShootersPerTeam { get; set; }
    }

    /// <summary>Modell för "du är med i matchen"-sidan (efter en bekräftad claim).</summary>
    public class MemberClaimJoinedViewModel
    {
        public string MatchCode { get; set; } = string.Empty;
        public string MemberName { get; set; } = string.Empty;
        /// <summary>Satt när besökaren är inloggad som NÅGON ANNAN än den inbjudne.</summary>
        public string? SignedInAsOther { get; set; }
        public string LoginUrl { get; set; } = string.Empty;
        public string ForgotUrl { get; set; } = string.Empty;
        public string ContinueUrl { get; set; } = string.Empty;
    }

    public class MemberClaimTeamOption
    {
        public int Id { get; set; }
        public string TeamName { get; set; } = string.Empty;
        public string? ClubName { get; set; }
        public int MemberCount { get; set; }
        public bool IsFull { get; set; }
    }
}
