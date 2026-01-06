using HpskSite.Middleware;

namespace HpskSite.Extensions
{
    /// <summary>
    /// Extension methods for registering member activity tracking middleware
    /// </summary>
    public static class MemberActivityMiddlewareExtensions
    {
        /// <summary>
        /// Add member activity tracking middleware to the pipeline
        /// </summary>
        public static IApplicationBuilder UseMemberActivityTracking(this IApplicationBuilder builder)
        {
            return builder.UseMiddleware<MemberActivityTrackingMiddleware>();
        }
    }
}
