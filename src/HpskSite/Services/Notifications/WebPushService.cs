using HpskSite.Models.WebPush;
using Microsoft.Extensions.Configuration;
using Umbraco.Cms.Infrastructure.Scoping;
using WebPush;

namespace HpskSite.Services.Notifications
{
    /// <summary>
    /// Browser Web Push (RFC 8291) — stores per-browser subscriptions and sends VAPID-signed,
    /// encrypted notifications via the WebPush library. Separate from the MAUI FCM pipe; this is
    /// the channel that reaches users on the web app.
    /// </summary>
    public class WebPushService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IConfiguration _config;
        private readonly ILogger<WebPushService> _logger;
        private readonly WebPushClient _client = new WebPushClient();

        public WebPushService(IScopeProvider scopeProvider, IConfiguration config, ILogger<WebPushService> logger)
        {
            _scopeProvider = scopeProvider;
            _config = config;
            _logger = logger;
        }

        public string? PublicKey => _config["WebPush:PublicKey"];

        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_config["WebPush:PublicKey"]) &&
            !string.IsNullOrWhiteSpace(_config["WebPush:PrivateKey"]);

        private VapidDetails? Vapid()
        {
            if (!IsConfigured) return null;
            var subject = _config["WebPush:Subject"];
            if (string.IsNullOrWhiteSpace(subject)) subject = "mailto:admin@pistol.nu";
            return new VapidDetails(subject, _config["WebPush:PublicKey"], _config["WebPush:PrivateKey"]);
        }

        public void SaveSubscription(int memberId, string endpoint, string p256dh, string auth, string? userAgent)
        {
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            db.Execute("DELETE FROM WebPushSubscription WHERE Endpoint = @0", endpoint); // dedupe / re-point to this member
            db.Insert("WebPushSubscription", "Id", true, new
            {
                MemberId = memberId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                UserAgent = userAgent ?? "",
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = (DateTime?)null
            });
            scope.Complete();
        }

        public void RemoveSubscription(string endpoint)
        {
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Execute("DELETE FROM WebPushSubscription WHERE Endpoint = @0", endpoint);
            scope.Complete();
        }

        /// <summary>
        /// Sends to a member's browsers. When onlyRanking is true, only subscriptions with the ranking
        /// notification enabled receive it (used by the nightly "din träningsform förbättrades" push).
        /// </summary>
        public async Task<int> SendToMemberAsync(int memberId, string title, string body, string url, string? tag = null, bool onlyRanking = false)
        {
            List<WebPushSubscriptionRow> subs;
            using (var scope = _scopeProvider.CreateScope())
            {
                subs = scope.Database.Fetch<WebPushSubscriptionRow>("SELECT * FROM WebPushSubscription WHERE MemberId = @0", memberId);
                scope.Complete();
            }
            if (onlyRanking) subs = subs.Where(s => s.RankingEnabled).ToList();
            return await SendToSubscriptionsAsync(subs, title, body, url, tag);
        }

        /// <summary>
        /// Broadcasts a "ny träningsmatch" push to every subscriber whose MatchPref matches — mirrors the
        /// FCM rule: open match → 'OpenMatchesOnly' + 'All'; closed match → 'All' only. Skips the creator.
        /// </summary>
        public async Task<int> SendMatchCreatedAsync(string matchCode, string matchName, string creatorName,
            string weaponClass, bool isOpen, int? excludeMemberId = null)
        {
            List<WebPushSubscriptionRow> subs;
            using (var scope = _scopeProvider.CreateScope())
            {
                var sql = isOpen
                    ? "SELECT * FROM WebPushSubscription WHERE MatchPref IN ('OpenMatchesOnly','All')"
                    : "SELECT * FROM WebPushSubscription WHERE MatchPref = 'All'";
                subs = scope.Database.Fetch<WebPushSubscriptionRow>(sql);
                scope.Complete();
            }
            if (excludeMemberId.HasValue) subs = subs.Where(s => s.MemberId != excludeMemberId.Value).ToList();

            var displayName = string.IsNullOrEmpty(matchName) ? matchCode : matchName;
            return await SendToSubscriptionsAsync(subs, "Ny träningsmatch!",
                $"{creatorName} skapade '{displayName}' ({weaponClass})", "/traningsmatch/", "match");
        }

        private async Task<int> SendToSubscriptionsAsync(List<WebPushSubscriptionRow> subs, string title, string body, string url, string? tag)
        {
            var vapid = Vapid();
            if (vapid == null || subs == null || subs.Count == 0) return 0;

            var payload = System.Text.Json.JsonSerializer.Serialize(new { title, body, url, tag });
            var sent = 0;
            var expired = new List<string>();

            foreach (var s in subs)
            {
                try
                {
                    await _client.SendNotificationAsync(new PushSubscription(s.Endpoint, s.P256dh, s.Auth), payload, vapid);
                    sent++;
                }
                catch (WebPushException ex)
                {
                    if ((int)ex.StatusCode == 404 || (int)ex.StatusCode == 410)
                        expired.Add(s.Endpoint); // gone / unsubscribed
                    else
                        _logger.LogWarning(ex, "WebPush send failed ({Status})", ex.StatusCode);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WebPush send error");
                }
            }

            if (expired.Count > 0)
            {
                using var scope = _scopeProvider.CreateScope();
                foreach (var e in expired) scope.Database.Execute("DELETE FROM WebPushSubscription WHERE Endpoint = @0", e);
                scope.Complete();
            }

            return sent;
        }

        public (string MatchPref, bool RankingEnabled)? GetPreferences(string endpoint)
        {
            using var scope = _scopeProvider.CreateScope();
            var row = scope.Database.FirstOrDefault<WebPushSubscriptionRow>("SELECT * FROM WebPushSubscription WHERE Endpoint = @0", endpoint);
            scope.Complete();
            return row == null ? null : (row.MatchPref, row.RankingEnabled);
        }

        public void SavePreferences(string endpoint, string? matchPref, bool rankingEnabled)
        {
            var mp = matchPref switch { "All" => "All", "Off" => "Off", _ => "OpenMatchesOnly" };
            using var scope = _scopeProvider.CreateScope();
            scope.Database.Execute("UPDATE WebPushSubscription SET MatchPref = @0, RankingEnabled = @1 WHERE Endpoint = @2",
                mp, rankingEnabled, endpoint);
            scope.Complete();
        }

        /// <summary>Generate a fresh VAPID key pair (admin bootstrap — paste into config).</summary>
        public static (string PublicKey, string PrivateKey) GenerateKeys()
        {
            var keys = VapidHelper.GenerateVapidKeys();
            return (keys.PublicKey, keys.PrivateKey);
        }
    }
}
