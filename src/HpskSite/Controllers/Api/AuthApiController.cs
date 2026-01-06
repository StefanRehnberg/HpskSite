using HpskSite.Migrations;
using HpskSite.Services;
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

        public AuthApiController(
            IMemberManager memberManager,
            IMemberService memberService,
            SignInManager<MemberIdentityUser> signInManager,
            JwtTokenService jwtTokenService,
            AdminAuthorizationService authorizationService,
            IScopeProvider scopeProvider,
            IConfiguration configuration,
            MemberActivityService memberActivityService)
        {
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
                siteUrl = $"{Request.Scheme}://{Request.Host}";
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
            await StoreRefreshToken(memberId, refreshToken, refreshTokenExpires);

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
        /// Refresh access token using refresh token
        /// </summary>
        [HttpPost("refresh")]
        public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
        {
            if (string.IsNullOrEmpty(request.RefreshToken))
            {
                return BadRequest(ApiResponse<LoginResponse>.Error("Refresh token krävs"));
            }

            // Find and validate refresh token
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var storedToken = await db.FirstOrDefaultAsync<RefreshTokenDto>(
                "WHERE Token = @0 AND RevokedAt IS NULL", request.RefreshToken);

            if (storedToken == null || !storedToken.IsActive)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Ogiltig eller utgången refresh token"));
            }

            // Get member
            var umbracoMember = _memberService.GetById(storedToken.MemberId);
            if (umbracoMember == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Medlemskontot hittades inte"));
            }

            var member = await _memberManager.FindByEmailAsync(umbracoMember.Email ?? string.Empty);
            if (member == null)
            {
                return Unauthorized(ApiResponse<LoginResponse>.Error("Medlemskontot hittades inte"));
            }

            // Check admin status
            var isAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            var adminClubIds = await _authorizationService.GetManagedClubIds();

            // Generate new tokens
            var newAccessToken = _jwtTokenService.GenerateAccessToken(member, storedToken.MemberId, isAdmin, adminClubIds);
            var newRefreshToken = _jwtTokenService.GenerateRefreshToken();
            var refreshTokenExpires = _jwtTokenService.GetRefreshTokenExpiration(true);

            // Revoke old refresh token and store new one
            storedToken.RevokedAt = DateTime.UtcNow;
            storedToken.RevokedByIp = GetIpAddress();
            storedToken.ReplacedByToken = newRefreshToken;
            await db.UpdateAsync(storedToken);

            await StoreRefreshToken(storedToken.MemberId, newRefreshToken, refreshTokenExpires, scope);

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
                    MemberId = storedToken.MemberId,
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
        /// Logout - revoke refresh token
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

            var storedToken = await db.FirstOrDefaultAsync<RefreshTokenDto>(
                "WHERE Token = @0 AND RevokedAt IS NULL", request.RefreshToken);

            if (storedToken != null)
            {
                storedToken.RevokedAt = DateTime.UtcNow;
                storedToken.RevokedByIp = GetIpAddress();
                await db.UpdateAsync(storedToken);
            }

            scope.Complete();

            return Ok(ApiResponse.Ok("Utloggad"));
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

            await _memberActivityService.UpdateMobileActivityAsync(umbracoMember.Email, DateTime.UtcNow);

            return Ok(ApiResponse.Ok("Aktivitet registrerad"));
        }

        /// <summary>
        /// Store refresh token in database
        /// </summary>
        private async Task StoreRefreshToken(int memberId, string token, DateTime expiresAt, IScope? existingScope = null)
        {
            var scope = existingScope ?? _scopeProvider.CreateScope();
            try
            {
                var db = scope.Database;

                var refreshToken = new RefreshTokenDto
                {
                    MemberId = memberId,
                    Token = token,
                    ExpiresAt = expiresAt,
                    CreatedAt = DateTime.UtcNow,
                    CreatedByIp = GetIpAddress(),
                    UserAgent = Request.Headers["User-Agent"].ToString()
                };

                await db.InsertAsync(refreshToken);

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
