using HpskSite.Services;
using Umbraco.Cms.Core.Security;

namespace HpskSite.Middleware
{
    /// <summary>
    /// Middleware to track member activity on frontend pages
    /// Excludes backoffice/admin panel requests
    /// </summary>
    public class MemberActivityTrackingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<MemberActivityTrackingMiddleware> _logger;

        public MemberActivityTrackingMiddleware(
            RequestDelegate next,
            ILogger<MemberActivityTrackingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(
            HttpContext context,
            IMemberManager memberManager,
            MemberActivityService activityService)
        {
            try
            {
                // Get current path
                var path = context.Request.Path.Value ?? "";

                // Exclude backoffice paths (admin panel)
                if (!path.StartsWith("/umbraco", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if member is authenticated
                    var currentMember = await memberManager.GetCurrentMemberAsync();

                    _logger.LogInformation("🔍 Middleware invoked for path: {Path}, Member: {Email}",
                        path, currentMember?.Email ?? "Not authenticated");

                    if (currentMember != null && !string.IsNullOrEmpty(currentMember.Email))
                    {
                        // Update activity (throttled internally by service)
                        await activityService.UpdateActivityAsync(
                            currentMember.Email,
                            DateTime.UtcNow);
                    }
                }
                else
                {
                    _logger.LogDebug("Skipping activity tracking for backoffice path: {Path}", path);
                }
            }
            catch (Exception ex)
            {
                // Log error but don't break the request pipeline
                _logger.LogError(ex, "Error in MemberActivityTrackingMiddleware");
            }

            // Continue pipeline
            await _next(context);
        }
    }
}
