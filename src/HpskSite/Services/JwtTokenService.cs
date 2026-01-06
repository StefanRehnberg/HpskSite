using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using HpskSite.Models;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Umbraco.Cms.Core.Security;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for generating and validating JWT tokens
    /// </summary>
    public class JwtTokenService
    {
        private readonly JwtSettings _jwtSettings;
        private readonly SymmetricSecurityKey _signingKey;

        public JwtTokenService(IOptions<JwtSettings> jwtSettings)
        {
            _jwtSettings = jwtSettings.Value;
            _signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_jwtSettings.Secret));
        }

        /// <summary>
        /// Generate an access token for a member
        /// </summary>
        public string GenerateAccessToken(MemberIdentityUser member, int memberId, bool isAdmin = false, List<int>? adminClubIds = null)
        {
            var claims = new List<Claim>
            {
                new Claim(JwtRegisteredClaimNames.Sub, member.UserName ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Email, member.Email ?? string.Empty),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim("memberId", memberId.ToString()),
                new Claim("firstName", member.Name?.Split(' ').FirstOrDefault() ?? string.Empty),
                new Claim("lastName", member.Name?.Split(' ').Skip(1).FirstOrDefault() ?? string.Empty),
                new Claim("isAdmin", isAdmin.ToString().ToLower())
            };

            // Add club admin IDs if any
            if (adminClubIds != null && adminClubIds.Any())
            {
                claims.Add(new Claim("adminClubIds", string.Join(",", adminClubIds)));
            }

            var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

            var token = new JwtSecurityToken(
                issuer: _jwtSettings.Issuer,
                audience: _jwtSettings.Audience,
                claims: claims,
                expires: DateTime.UtcNow.AddMinutes(_jwtSettings.AccessTokenExpirationMinutes),
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }

        /// <summary>
        /// Generate a refresh token
        /// </summary>
        public string GenerateRefreshToken()
        {
            var randomBytes = new byte[64];
            using var rng = RandomNumberGenerator.Create();
            rng.GetBytes(randomBytes);
            return Convert.ToBase64String(randomBytes);
        }

        /// <summary>
        /// Get the refresh token expiration date
        /// </summary>
        public DateTime GetRefreshTokenExpiration(bool rememberMe = true)
        {
            // If "remember me" is checked, use the configured expiration
            // Otherwise, use a shorter expiration (e.g., 1 day)
            var days = rememberMe ? _jwtSettings.RefreshTokenExpirationDays : 1;
            return DateTime.UtcNow.AddDays(days);
        }

        /// <summary>
        /// Validate an access token and return the claims principal
        /// </summary>
        public ClaimsPrincipal? ValidateToken(string token)
        {
            try
            {
                var tokenHandler = new JwtSecurityTokenHandler();
                var validationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = _jwtSettings.Issuer,
                    ValidAudience = _jwtSettings.Audience,
                    IssuerSigningKey = _signingKey,
                    ClockSkew = TimeSpan.Zero // Don't allow any clock skew
                };

                var principal = tokenHandler.ValidateToken(token, validationParameters, out var validatedToken);

                // Ensure the token is a JWT token
                if (validatedToken is not JwtSecurityToken jwtToken ||
                    !jwtToken.Header.Alg.Equals(SecurityAlgorithms.HmacSha256, StringComparison.InvariantCultureIgnoreCase))
                {
                    return null;
                }

                return principal;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Extract member ID from claims
        /// </summary>
        public int? GetMemberIdFromClaims(ClaimsPrincipal principal)
        {
            var memberIdClaim = principal.FindFirst("memberId");
            if (memberIdClaim != null && int.TryParse(memberIdClaim.Value, out var memberId))
            {
                return memberId;
            }
            return null;
        }

        /// <summary>
        /// Extract email from claims
        /// </summary>
        public string? GetEmailFromClaims(ClaimsPrincipal principal)
        {
            return principal.FindFirst(JwtRegisteredClaimNames.Email)?.Value
                ?? principal.FindFirst(ClaimTypes.Email)?.Value;
        }

        /// <summary>
        /// Check if user is admin from claims
        /// </summary>
        public bool IsAdminFromClaims(ClaimsPrincipal principal)
        {
            var isAdminClaim = principal.FindFirst("isAdmin");
            return isAdminClaim != null && bool.TryParse(isAdminClaim.Value, out var isAdmin) && isAdmin;
        }

        /// <summary>
        /// Get admin club IDs from claims
        /// </summary>
        public List<int> GetAdminClubIdsFromClaims(ClaimsPrincipal principal)
        {
            var adminClubIdsClaim = principal.FindFirst("adminClubIds");
            if (adminClubIdsClaim != null && !string.IsNullOrEmpty(adminClubIdsClaim.Value))
            {
                return adminClubIdsClaim.Value.Split(',')
                    .Where(s => int.TryParse(s, out _))
                    .Select(int.Parse)
                    .ToList();
            }
            return new List<int>();
        }
    }
}
