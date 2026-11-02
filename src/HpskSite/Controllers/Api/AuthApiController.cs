using HpskSite.Migrations;
using HpskSite.Services;
using HpskSite.Services.Security;
using HpskSite.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using NPoco;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Controllers.Api
{
    /// <summary>
    /// API controller for JWT authentication (Mobile app)
    /// </summary>
    [Route("api/auth")]
    [ApiController]
    public class AuthApiController : ControllerBase
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly SignInManager<MemberIdentityUser> _signInManager;
        private readonly JwtTokenService _jwtTokenService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IScopeProvider _scopeProvider;
        private readonly IConfiguration _configuration;
        private readonly MemberActivityService _memberActivityService;
        private readonly ILogger<AuthApiController> _logger;

        public AuthApiController(
            IMemberManager memberManager,
            IMemberService memberService,
            SignInManager<MemberIdentityUser> signInManager,
            JwtTokenService jwtTokenService,
            AdminAuthorizationService authorizationService,
            IScopeProvider scopeProvider,
            IConfiguration configuration,
            MemberActivityService memberActivityService,
            ILogger<AuthApiController> logger)
        {
            _logger = logger;
            _memberManager = memberManager;
            _memberService = memberService;
            _signInManager = signInManager;
            _jwtTokenService = jwtTokenService;
            _authorizationService = authorizationService;
            _scopeProvider = scopeProvider;
            _configuration = configuration;
            _memberActivityService = memberActivityService;
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
                siteUrl = $"{Request.PublicBase()}";
            }

            return relativeUrl.StartsWith("/")
                ? $"{siteUrl}{relativeUrl}"
                : $"{siteUrl}/{relativeUrl}";
        }

        /// <summary>
        /// Login with email and password
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            if (string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Password))
            {
                return BadRequest(ApiResponse<LoginResponse>.Error("Email och lösenord krävs"));
            }

            // Find member by email
            var member = await _memberManager.FindByEmailAsync(request.Email);
            if (member == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Felaktiga inloggningsuppgifter"));
            }

            // Check password
            var result = await _signInManager.CheckPasswordSignInAsync(member, request.Password, lockoutOnFailure: true);
            if (!result.Succeeded)
            {
                if (result.IsLockedOut)
                {
                    return Unauthorized(ApiResponse<LoginResponse>.Error("Kontot är tillfälligt låst. Försök igen senare."));
                }
                return Unauthorized(ApiResponse<LoginResponse>.Error("Felaktiga inloggningsuppgifter"));
            }

            // Get member ID
            var umbracoMember = _memberService.GetByEmail(request.Email);
            if (umbracoMember == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Medlemskontot hittades inte"));
            }

            var memberId = umbracoMember.Id;

            // Check if member is approved
            if (!umbracoMember.IsApproved)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Ditt konto väntar på godkännande"));
            }

            // Check admin status
            var isAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var adminClubIds = await _authorizationService.GetManagedClubIds();

            // Generate tokens
            var accessToken = _jwtTokenService.GenerateAccessToken(member, memberId, isAdmin, adminClubIds);
            var refreshToken = _jwtTokenService.GenerateRefreshToken();
            var refreshTokenExpires = _jwtTokenService.GetRefreshTokenExpiration(request.RememberMe);

            // Store refresh token in database
            await StoreRefreshToken(memberId, refreshToken, refreshTokenExpires, Guid.NewGuid());

            // Get member name parts
            var nameParts = (member.Name ?? string.Empty).Split(' ', 2);
            var firstName = nameParts.Length > 0 ? nameParts[0] : string.Empty;
            var lastName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

            var response = new LoginResponse
            {
                AccessToken = accessToken,
                RefreshToken = refreshToken,
                AccessTokenExpires = DateTime.UtcNow.AddMinutes(60),
                RefreshTokenExpires = refreshTokenExpires,
                User = new UserInfo
                {
                    MemberId = memberId,
                    Email = member.Email ?? string.Empty,
                    FirstName = firstName,
                    LastName = lastName,
                    ProfilePictureUrl = ToAbsoluteUrl(umbracoMember.GetValue<string>("profilePictureUrl")),
                    IsAdmin = isAdmin,
                    AdminClubIds = adminClubIds
                }
            };

            return Ok(ApiResponse<LoginResponse>.Ok(response));
        }

        /// <summary>
        /// Refresh access token using refresh token.
        ///
        /// <para>⚠️ Säkerhetsplanen 2026-11-02 (H6): token slås upp på sin HASH, roteras atomärt
        /// (<c>UPDATE … WHERE RevokedAt IS NULL</c> — två samtidiga anrop kan inte båda lyckas), och en token som visas upp
        /// igen efter nådefönstret återkallar hela familjen (<see cref="RefreshRotationRules"/>). Kontot måste vara
        /// godkänt och inte låst. Förut: läs-sedan-skriv utan lås och ingen kontroll av kontots status.</para>
        /// </summary>
        [HttpPost("refresh")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
            {
                return BadRequest(ApiResponse<LoginResponse>.Error("Refresh token krävs"));
            }

            var now = DateTime.UtcNow;
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var stored = await FindRefreshTokenAsync(db, request.RefreshToken);
            if (stored == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Ogiltig eller utgången refresh token"));
            }

            var outcome = RefreshRotationRules.Decide(now, stored.ExpiresAt, stored.RevokedAt, stored.LastUsedAt, stored.ReplacedByHash != null);
            if (outcome == RefreshRotationRules.Outcome.ReuseDetected)
            {
                await RevokeFamilyAsync(db, stored, now);
                scope.Complete();
                _logger.LogWarning("SEC: Refresh-token återanvänd efter nådefönstret — familjen för medlem {MemberId} återkallad", stored.MemberId);
                return Unauthorized(ApiResponse<LoginResponse>.Error("Ogiltig eller utgången refresh token"));
            }
            if (outcome == RefreshRotationRules.Outcome.Expired)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Ogiltig eller utgången refresh token"));
            }

            // Get member
            var umbracoMember = _memberService.GetById(stored.MemberId);
            if (umbracoMember == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Medlemskontot hittades inte"));
            }

            // Ett konto som stängts av eller låsts sedan inloggningen får inte förlänga sin session.
            if (!umbracoMember.IsApproved || umbracoMember.IsLockedOut)
            {
                await RevokeFamilyAsync(db, stored, now);
                scope.Complete();
                _logger.LogWarning("SEC: Refresh nekades — konto {MemberId} är inte godkänt eller är låst; familjen återkallad", stored.MemberId);
                return Unauthorized(ApiResponse<LoginResponse>.Error("Kontot är inte aktivt"));
            }

            var member = await _memberManager.FindByEmailAsync(umbracoMember.Email ?? string.Empty);
            if (member == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Medlemskontot hittades inte"));
            }

            // Check admin status (oförändrat: samma värden som vid inloggningen — anropet är anonymt, claims litas inte på av servern)
            var isAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var adminClubIds = await _authorizationService.GetManagedClubIds();

            // Generate new tokens
            var newAccessToken = _jwtTokenService.GenerateAccessToken(member, stored.MemberId, isAdmin, adminClubIds);
            var newRefreshToken = _jwtTokenService.GenerateRefreshToken();
            var newHash = RefreshTokenHasher.Hash(newRefreshToken);
            var refreshTokenExpires = _jwtTokenService.GetRefreshTokenExpiration(true);
            var family = stored.FamilyId ?? Guid.NewGuid();

            if (outcome == RefreshRotationRules.Outcome.Rotate)
            {
                // Atomärt: bara EN förfrågan får återkalla token. (db.ExecuteAsync ger antalet rader — inte @@ROWCOUNT,
                // som NPoco läser som en escape.)
                var claimed = await db.ExecuteAsync(
                    @"UPDATE RefreshTokens
                         SET RevokedAt = @0, RevokedByIp = @1, LastUsedAt = @0, ReplacedByHash = @2, FamilyId = COALESCE(FamilyId, @3)
                       WHERE Id = @4 AND RevokedAt IS NULL",
                    now, GetIpAddress(), newHash, family, stored.Id);

                if (claimed != 1)
                {
                    // En samtidig förfrågan hann före. Inom nådefönstret är det ett dubbelanrop (ny efterföljare);
                    // annars en återanvändning.
                    var fresh = await db.FirstOrDefaultAsync<RefreshTokenDto>("WHERE Id = @0", stored.Id);
                    var again = fresh == null
                        ? RefreshRotationRules.Outcome.ReuseDetected
                        : RefreshRotationRules.Decide(now, fresh.ExpiresAt, fresh.RevokedAt, fresh.LastUsedAt, fresh.ReplacedByHash != null);
                    if (again != RefreshRotationRules.Outcome.GraceRetry)
                    {
                        await RevokeFamilyAsync(db, stored, now);
                        scope.Complete();
                        return Unauthorized(ApiResponse<LoginResponse>.Error("Ogiltig eller utgången refresh token"));
                    }
                }
            }

            await StoreRefreshToken(stored.MemberId, newRefreshToken, refreshTokenExpires, family, scope);

            scope.Complete();

            // Get member name parts
            var nameParts = (member.Name ?? string.Empty).Split(' ', 2);
            var firstName = nameParts.Length > 0 ? nameParts[0] : string.Empty;
            var lastName = nameParts.Length > 1 ? nameParts[1] : string.Empty;

            var response = new LoginResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken,
                AccessTokenExpires = DateTime.UtcNow.AddMinutes(60),
                RefreshTokenExpires = refreshTokenExpires,
                User = new UserInfo
                {
                    MemberId = stored.MemberId,
                    Email = member.Email ?? string.Empty,
                    FirstName = firstName,
                    LastName = lastName,
                    ProfilePictureUrl = ToAbsoluteUrl(umbracoMember.GetValue<string>("profilePictureUrl")),
                    IsAdmin = isAdmin,
                    AdminClubIds = adminClubIds
                }
            };

            return Ok(ApiResponse<LoginResponse>.Ok(response));
        }

        /// <summary>
        /// Logout - revoke refresh token (och hela dess familj: enheten loggas ut)
        /// </summary>
        [HttpPost("logout")]
        public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
            {
                return Ok(ApiResponse.Ok("Utloggad"));
            }

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var stored = await FindRefreshTokenAsync(db, request.RefreshToken);
            if (stored != null)
            {
                await RevokeFamilyAsync(db, stored, DateTime.UtcNow);
            }

            scope.Complete();

            return Ok(ApiResponse.Ok("Utloggad"));
        }

        /// <summary>Slår upp en refresh-token på hashen; äldre rader (före migreringen) har klartexten kvar tills steg 2.</summary>
        private static async Task<RefreshTokenDto?> FindRefreshTokenAsync(IDatabase db, string token)
        {
            var hash = RefreshTokenHasher.Hash(token);
            var row = await db.FirstOrDefaultAsync<RefreshTokenDto>("WHERE TokenHash = @0", hash);
            if (row != null) return row;
            return await db.FirstOrDefaultAsync<RefreshTokenDto>("WHERE TokenHash IS NULL AND Token = @0", token);
        }

        /// <summary>Återkallar alla aktiva token i token-raden familj (eller bara raden själv om den saknar familj).</summary>
        private async Task RevokeFamilyAsync(IDatabase db, RefreshTokenDto stored, DateTime now)
        {
            await db.ExecuteAsync(
                @"UPDATE RefreshTokens SET RevokedAt = @0, RevokedByIp = @1
                   WHERE RevokedAt IS NULL AND (Id = @2 OR (@3 IS NOT NULL AND FamilyId = @3))",
                now, GetIpAddress(), stored.Id, stored.FamilyId);
        }

        /// <summary>
        /// Get current user info
        /// </summary>
        [HttpGet("me")]
        [Authorize(AuthenticationSchemes = "JwtBearer")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var memberId = _jwtTokenService.GetMemberIdFromClaims(User);
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<UserInfo>.Error("Ogiltig token"));
            }

            var umbracoMember = _memberService.GetById(memberId.Value);
            if (umbracoMember == null)
            {
                return NotFound(ApiResponse<UserInfo>.Error("Medlemskontot hittades inte"));
            }

            var isAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var adminClubIds = await _authorizationService.GetManagedClubIds();

            var userInfo = new UserInfo
            {
                MemberId = memberId.Value,
                Email = umbracoMember.Email ?? string.Empty,
                FirstName = umbracoMember.GetValue<string>("firstName") ?? string.Empty,
                LastName = umbracoMember.GetValue<string>("lastName") ?? string.Empty,
                ProfilePictureUrl = ToAbsoluteUrl(umbracoMember.GetValue<string>("profilePictureUrl")),
                IsAdmin = isAdmin,
                AdminClubIds = adminClubIds
            };

            return Ok(ApiResponse<UserInfo>.Ok(userInfo));
        }

        /// <summary>
        /// Report mobile app activity - called by mobile app to track usage
        /// </summary>
        [HttpPost("activity")]
        [Authorize(AuthenticationSchemes = "JwtBearer")]
        public async Task<IActionResult> ReportActivity()
        {
            var memberId = _jwtTokenService.GetMemberIdFromClaims(User);
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ogiltig token"));
            }

            var umbracoMember = _memberService.GetById(memberId.Value);
            if (umbracoMember == null || string.IsNullOrEmpty(umbracoMember.Email))
            {
                return NotFound(ApiResponse.Error("Medlemskontot hittades inte"));
            }

            _memberActivityService.UpdateMobileActivity(umbracoMember.Id, DateTime.UtcNow);
            await Task.CompletedTask;

            return Ok(ApiResponse.Ok("Aktivitet registrerad"));
        }

        /// <summary>
        /// Store refresh token in database — bara HASHEN (säkerhetsplanen 2026-11-02, H6); klartexten skrivs aldrig.
        /// </summary>
        private async Task StoreRefreshToken(int memberId, string token, DateTime expiresAt, Guid familyId, IScope? existingScope = null)
        {
            var scope = existingScope ?? _scopeProvider.CreateScope();
            try
            {
                var db = scope.Database;
                var now = DateTime.UtcNow;

                var refreshToken = new RefreshTokenDto
                {
                    MemberId = memberId,
                    Token = null,
                    TokenHash = RefreshTokenHasher.Hash(token),
                    FamilyId = familyId,
                    ExpiresAt = expiresAt,
                    CreatedAt = now,
                    CreatedByIp = GetIpAddress(),
                    UserAgent = Request.Headers["User-Agent"].ToString()
                };

                await db.InsertAsync(refreshToken);

                // Gallring: rader som gått ut eller återkallats för mer än 30 dagar sedan (en av femtio gånger).
                if (Random.Shared.Next(50) == 0)
                {
                    await db.ExecuteAsync(
                        "DELETE FROM RefreshTokens WHERE ExpiresAt < @0 OR (RevokedAt IS NOT NULL AND RevokedAt < @0)",
                        now.AddDays(-30));
                }

                if (existingScope == null)
                {
                    scope.Complete();
                }
            }
            finally
            {
                if (existingScope == null)
                {
                    scope.Dispose();
                }
            }
        }

        /// <summary>
        /// Get client IP address
        /// </summary>
        private string? GetIpAddress()
        {
            // Check for forwarded IP (behind proxy/load balancer)
            if (Request.Headers.ContainsKey("X-Forwarded-For"))
            {
                return Request.Headers["X-Forwarded-For"].ToString().Split(',').FirstOrDefault()?.Trim();
            }

            return HttpContext.Connection.RemoteIpAddress?.ToString();
        }
    }
}
