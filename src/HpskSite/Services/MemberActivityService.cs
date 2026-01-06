using System.Collections.Concurrent;
using HpskSite.Models.Configuration;
using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for tracking member activity with throttling to minimize database writes
    /// </summary>
    public class MemberActivityService
    {
        // Thread-safe cache of last update times (email -> timestamp)
        private static readonly ConcurrentDictionary<string, DateTime> _lastUpdateCache = new();
        private static readonly ConcurrentDictionary<string, DateTime> _lastMobileUpdateCache = new();

        private readonly IMemberService _memberService;
        private readonly MemberActivityOptions _options;
        private readonly ILogger<MemberActivityService> _logger;

        public MemberActivityService(
            IMemberService memberService,
            IOptions<MemberActivityOptions> options,
            ILogger<MemberActivityService> logger)
        {
            _memberService = memberService;
            _options = options.Value;
            _logger = logger;
        }

        /// <summary>
        /// Update member's last active timestamp (throttled)
        /// </summary>
        /// <param name="email">Member email</param>
        /// <param name="activityTime">Activity timestamp (UTC)</param>
        public async Task UpdateActivityAsync(string email, DateTime activityTime)
        {
            try
            {
                // Check if tracking is enabled
                if (!_options.EnableTracking)
                {
                    return;
                }

                // Check if enough time has passed since last update (throttle)
                if (_lastUpdateCache.TryGetValue(email, out var lastUpdate))
                {
                    var elapsed = activityTime.Subtract(lastUpdate);
                    if (elapsed.TotalMinutes < _options.ThrottleMinutes)
                    {
                        // Skip update - not enough time has passed
                        _logger.LogDebug("Skipping activity update for {Email} - last updated {Elapsed} seconds ago",
                            email, elapsed.TotalSeconds);
                        return;
                    }
                }

                // Update member property
                _logger.LogInformation("🔍 Attempting to update activity for {Email}", email);

                await Task.Run(() =>
                {
                    var member = _memberService.GetByEmail(email);
                    if (member != null)
                    {
                        _logger.LogInformation("✅ Member found: {Name} ({Id})", member.Name, member.Id);

                        member.SetValue("lastActiveDate", activityTime);
                        _memberService.Save(member);

                        // Update cache
                        _lastUpdateCache[email] = activityTime;

                        _logger.LogInformation("✅ Updated last active for {Email} to {Time}",
                            email, activityTime);
                    }
                    else
                    {
                        _logger.LogWarning("❌ Member not found for activity update: {Email}", email);
                    }
                });
            }
            catch (Exception ex)
            {
                // Log error but don't throw - activity tracking should not break the site
                _logger.LogError(ex, "Error updating activity for {Email}", email);
            }
        }

        /// <summary>
        /// Update member's last mobile app active timestamp (throttled)
        /// </summary>
        /// <param name="email">Member email</param>
        /// <param name="activityTime">Activity timestamp (UTC)</param>
        public async Task UpdateMobileActivityAsync(string email, DateTime activityTime)
        {
            try
            {
                // Check if tracking is enabled
                if (!_options.EnableTracking)
                {
                    return;
                }

                // Check if enough time has passed since last update (throttle)
                if (_lastMobileUpdateCache.TryGetValue(email, out var lastUpdate))
                {
                    var elapsed = activityTime.Subtract(lastUpdate);
                    if (elapsed.TotalMinutes < _options.ThrottleMinutes)
                    {
                        _logger.LogDebug("Skipping mobile activity update for {Email} - last updated {Elapsed} seconds ago",
                            email, elapsed.TotalSeconds);
                        return;
                    }
                }

                // Update member property
                _logger.LogInformation("Updating mobile activity for {Email}", email);

                await Task.Run(() =>
                {
                    var member = _memberService.GetByEmail(email);
                    if (member != null)
                    {
                        member.SetValue("lastMobileActiveDate", activityTime);
                        _memberService.Save(member);

                        // Update cache
                        _lastMobileUpdateCache[email] = activityTime;

                        _logger.LogInformation("Updated last mobile active for {Email} to {Time}",
                            email, activityTime);
                    }
                    else
                    {
                        _logger.LogWarning("Member not found for mobile activity update: {Email}", email);
                    }
                });
            }
            catch (Exception ex)
            {
                // Log error but don't throw - activity tracking should not break the app
                _logger.LogError(ex, "Error updating mobile activity for {Email}", email);
            }
        }

        /// <summary>
        /// Clear the cache (useful for testing)
        /// </summary>
        public void ClearCache()
        {
            _lastUpdateCache.Clear();
            _lastMobileUpdateCache.Clear();
            _logger.LogInformation("Activity cache cleared");
        }
    }
}
