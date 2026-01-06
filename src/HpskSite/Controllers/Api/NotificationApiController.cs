using HpskSite.Services;
using HpskSite.Shared.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers.Api
{
    /// <summary>
    /// API controller for push notification management (Mobile app)
    /// </summary>
    [Route("api/notifications")]
    [ApiController]
    [Authorize(AuthenticationSchemes = "JwtBearer")]
    public class NotificationApiController : ControllerBase
    {
        private readonly PushNotificationService _pushNotificationService;
        private readonly JwtTokenService _jwtTokenService;
        private readonly ILogger<NotificationApiController> _logger;

        public NotificationApiController(
            PushNotificationService pushNotificationService,
            JwtTokenService jwtTokenService,
            ILogger<NotificationApiController> logger)
        {
            _pushNotificationService = pushNotificationService;
            _jwtTokenService = jwtTokenService;
            _logger = logger;
        }

        /// <summary>
        /// Register a device for push notifications
        /// POST: /api/notifications/register-device
        /// </summary>
        [HttpPost("register-device")]
        public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest request)
        {
            var memberId = _jwtTokenService.GetMemberIdFromClaims(User);
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ogiltig token"));
            }

            if (string.IsNullOrEmpty(request.DeviceToken))
            {
                return BadRequest(ApiResponse.Error("DeviceToken krävs"));
            }

            if (string.IsNullOrEmpty(request.Platform) ||
                (request.Platform != "Android" && request.Platform != "iOS"))
            {
                return BadRequest(ApiResponse.Error("Platform måste vara 'Android' eller 'iOS'"));
            }

            try
            {
                await _pushNotificationService.RegisterDeviceAsync(memberId.Value, request.DeviceToken, request.Platform);
                return Ok(ApiResponse.Ok("Enhet registrerad för notiser"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to register device for member {MemberId}", memberId.Value);
                return StatusCode(500, ApiResponse.Error("Kunde inte registrera enheten"));
            }
        }

        /// <summary>
        /// Unregister a device from push notifications
        /// POST: /api/notifications/unregister-device
        /// </summary>
        [HttpPost("unregister-device")]
        public async Task<IActionResult> UnregisterDevice([FromBody] UnregisterDeviceRequest request)
        {
            if (string.IsNullOrEmpty(request.DeviceToken))
            {
                return BadRequest(ApiResponse.Error("DeviceToken krävs"));
            }

            try
            {
                await _pushNotificationService.UnregisterDeviceAsync(request.DeviceToken);
                return Ok(ApiResponse.Ok("Enhet avregistrerad"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to unregister device");
                return StatusCode(500, ApiResponse.Error("Kunde inte avregistrera enheten"));
            }
        }

        /// <summary>
        /// Get notification preferences for the current user
        /// GET: /api/notifications/preferences
        /// </summary>
        [HttpGet("preferences")]
        public async Task<IActionResult> GetPreferences()
        {
            var memberId = _jwtTokenService.GetMemberIdFromClaims(User);
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse<NotificationPreferencesResponse>.Error("Ogiltig token"));
            }

            try
            {
                var prefs = await _pushNotificationService.GetPreferencesAsync(memberId.Value);
                return Ok(ApiResponse<NotificationPreferencesResponse>.Ok(prefs));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get preferences for member {MemberId}", memberId.Value);
                return StatusCode(500, ApiResponse<NotificationPreferencesResponse>.Error("Kunde inte hämta inställningar"));
            }
        }

        /// <summary>
        /// Update notification preferences for the current user
        /// PUT: /api/notifications/preferences
        /// </summary>
        [HttpPut("preferences")]
        public async Task<IActionResult> UpdatePreferences([FromBody] UpdateNotificationPreferencesRequest request)
        {
            var memberId = _jwtTokenService.GetMemberIdFromClaims(User);
            if (!memberId.HasValue)
            {
                return Unauthorized(ApiResponse.Error("Ogiltig token"));
            }

            if (string.IsNullOrEmpty(request.NotificationPreference) ||
                (request.NotificationPreference != "OpenMatchesOnly" && request.NotificationPreference != "All"))
            {
                return BadRequest(ApiResponse.Error("NotificationPreference måste vara 'OpenMatchesOnly' eller 'All'"));
            }

            try
            {
                await _pushNotificationService.UpdatePreferencesAsync(
                    memberId.Value,
                    request.NotificationsEnabled,
                    request.NotificationPreference);
                return Ok(ApiResponse.Ok("Inställningar uppdaterade"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to update preferences for member {MemberId}", memberId.Value);
                return StatusCode(500, ApiResponse.Error("Kunde inte uppdatera inställningar"));
            }
        }
    }
}
