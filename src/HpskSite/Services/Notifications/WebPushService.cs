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

        /// <summary>
        /// Stores (or re-points) a browser subscription. <paramref name="matchPref"/> is the default the
        /// opting-in surface asks for — see WebPushSubscribeRequest. It applies only to genuinely NEW
        /// subscriptions: re-subscribing the same browser as the same member carries the member's own
        /// choices across, because this is a DELETE+INSERT and silently resetting their prefs would be
        /// indistinguishable from us ignoring them.
        /// </summary>
        public void SaveSubscription(int memberId, string endpoint, string p256dh, string auth, string? userAgent,
            string? matchPref = null)
        {
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            var existing = db.Fetch<WebPushSubscriptionRow>(
                "SELECT * FROM WebPushSubscription WHERE Endpoint = @0", endpoint).FirstOrDefault();
            var keepPrefs = existing != null && existing.MemberId == memberId;

            db.Execute("DELETE FROM WebPushSubscription WHERE Endpoint = @0", endpoint); // dedupe / re-point to this member
            db.Insert("WebPushSubscription", "Id", true, new
            {
                MemberId = memberId,
                Endpoint = endpoint,
                P256dh = p256dh,
                Auth = auth,
                UserAgent = userAgent ?? "",
                MatchPref = keepPrefs ? existing!.MatchPref : (matchPref ?? "OpenMatchesOnly"),
                RankingEnabled = keepPrefs ? existing!.RankingEnabled : true,
                CreatedAt = DateTime.UtcNow,
                LastUsedAt = (DateTime?)null
            });

            // Separate UPDATE, not part of the INSERT above: ScheduleRemindersEnabled arrived in a later
            // migration than the table, so referencing it in the insert would break subscribing outright
            // on any environment where add-schedulereminders-to-webpushsubscription.sql hasn't been run.
            if (keepPrefs && existing!.ScheduleRemindersEnabled)
            {
                try
                {
                    db.Execute("UPDATE WebPushSubscription SET ScheduleRemindersEnabled = 1 WHERE Endpoint = @0", endpoint);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "WebPush: could not carry over schedule-reminder opt-in (migration pending?)");
                }
            }

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
        /// Start-time reminder ("Du börjar om 30 min…"). Goes ONLY to this member's browsers that have
        /// explicitly opted in via ScheduleRemindersEnabled — participant-facing pushes on this site are
        /// opt-in only, and the column defaults to 0 precisely so an existing subscriber never starts
        /// receiving these without asking. Returns how many browsers were reached.
        /// </summary>
        public async Task<int> SendScheduleReminderAsync(int memberId, string title, string body, string url, string? tag = null)
        {
            List<WebPushSubscriptionRow> subs;
            using (var scope = _scopeProvider.CreateScope())
            {
                subs = scope.Database.Fetch<WebPushSubscriptionRow>(
                    "SELECT * FROM WebPushSubscription WHERE MemberId = @0 AND ScheduleRemindersEnabled = 1", memberId);
                scope.Complete();
            }
            return await SendToSubscriptionsAsync(subs, title, body, url, tag);
        }

        /// <summary>
        /// Licenspaminnelse ("din licens forfaller om 90 dagar").
        ///
        /// <para><b>Opt-in-kolumnen defaultar till 1, tvartemot ScheduleRemindersEnabled</b> — och
        /// det ar ett medvetet undantag (Stefans beslut 2026-09-02). Medlemmen har SJALV skrivit in
        /// ett forfallodatum, och den handlingen AR opt-in:en; man skriver inte in datumet om man
        /// inte vill bli pamind. Starttidspaminnelser ber ingen om, darav skillnaden.</para>
        /// </summary>
        public async Task<int> SendLicenseReminderAsync(int memberId, string title, string body, string url, string? tag = null)
        {
            List<WebPushSubscriptionRow> subs;
            using (var scope = _scopeProvider.CreateScope())
            {
                subs = scope.Database.Fetch<WebPushSubscriptionRow>(
                    "SELECT * FROM WebPushSubscription WHERE MemberId = @0 AND LicenseRemindersEnabled = 1", memberId);
                scope.Complete();
            }
            return await SendToSubscriptionsAsync(subs, title, body, url, tag);
        }

        /// <summary>
        /// Member ids reachable by a licence reminder.
        ///
        /// <para><b>⚠️ Anvands INTE for att avgora VILKA vapen som ska pminnas om.</b> Svepet
        /// utgar fran forfallodatumen i Firearm-tabellen och kollar opt-in per medlem forst darefter
        /// — motsatt ordning jamfort med schemasvepet, och med flit: en medlem utan push ska anda
        /// fa paminnelsen i appen, sa svepet far inte hoppa over hens vapen.</para>
        /// </summary>
        public HashSet<int> GetLicenseReminderMemberIds()
        {
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var ids = scope.Database.Fetch<int>(
                    "SELECT DISTINCT MemberId FROM WebPushSubscription WHERE LicenseRemindersEnabled = 1");
                scope.Complete();
                return ids.ToHashSet();
            }
            catch (Exception ex)
            {
                // Kolumnen saknas = migreringen inte kord an -> funktionen ar helt enkelt av.
                _logger.LogDebug(ex, "WebPush: licence-reminder opt-in lookup failed (migration pending?)");
                return new HashSet<int>();
            }
        }

        /// <summary>Member ids with at least one browser opted in to start-time reminders. The reminder
        /// sweep uses this to avoid building itineraries for members who'd get nothing anyway.</summary>
        public List<int> GetScheduleReminderMemberIds()
        {
            try
            {
                using var scope = _scopeProvider.CreateScope();
                var ids = scope.Database.Fetch<int>(
                    "SELECT DISTINCT MemberId FROM WebPushSubscription WHERE ScheduleRemindersEnabled = 1");
                scope.Complete();
                return ids;
            }
            catch (Exception ex)
            {
                // Column missing = migration not run yet → the feature is simply off.
                _logger.LogDebug(ex, "WebPush: schedule-reminder opt-in lookup failed (migration pending?)");
                return new List<int>();
            }
        }

        /// <summary>
        /// Of the given member ids, which have at least one browser registered for push — i.e. who can
        /// actually be reached by a notification right now. One query for the whole set, because the
        /// caller is a desk table of a few hundred rows and a per-row lookup would be N+1.
        ///
        /// Note this asks "is this member reachable at all", NOT "have they opted in to any particular
        /// kind of push" — subscriptions are per-browser and hard-deleted on unsubscribe, so a row's
        /// existence is the whole signal. ScheduleRemindersEnabled is a separate, narrower opt-in.
        /// </summary>
        public HashSet<int> GetMemberIdsWithSubscriptions(IEnumerable<int> memberIds)
        {
            var ids = memberIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return new HashSet<int>();

            try
            {
                using var scope = _scopeProvider.CreateScope();
                var found = scope.Database.Fetch<int>(
                    "SELECT DISTINCT MemberId FROM WebPushSubscription WHERE MemberId IN (@0)", ids);
                scope.Complete();
                return found.ToHashSet();
            }
            catch (Exception ex)
            {
                // Table missing = push never provisioned here → nobody is reachable, which is the
                // truthful answer. The indicator just stays dark rather than breaking the table.
                _logger.LogDebug(ex, "WebPush: bulk subscription lookup failed (migration pending?)");
                return new HashSet<int>();
            }
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

        public (string MatchPref, bool RankingEnabled, bool ScheduleRemindersEnabled)? GetPreferences(string endpoint)
        {
            using var scope = _scopeProvider.CreateScope();
            var row = scope.Database.FirstOrDefault<WebPushSubscriptionRow>("SELECT * FROM WebPushSubscription WHERE Endpoint = @0", endpoint);
            scope.Complete();
            return row == null ? null : (row.MatchPref, row.RankingEnabled, row.ScheduleRemindersEnabled);
        }

        public void SavePreferences(string endpoint, string? matchPref, bool rankingEnabled, bool scheduleRemindersEnabled = false)
        {
            var mp = matchPref switch { "All" => "All", "Off" => "Off", _ => "OpenMatchesOnly" };
            using var scope = _scopeProvider.CreateScope();
            try
            {
                scope.Database.Execute(
                    "UPDATE WebPushSubscription SET MatchPref = @0, RankingEnabled = @1, ScheduleRemindersEnabled = @2 WHERE Endpoint = @3",
                    mp, rankingEnabled, scheduleRemindersEnabled, endpoint);
            }
            catch
            {
                // add-schedulereminders-to-webpushsubscription.sql not run yet - still save the prefs
                // that DO exist rather than losing the member's whole change.
                scope.Database.Execute("UPDATE WebPushSubscription SET MatchPref = @0, RankingEnabled = @1 WHERE Endpoint = @2",
                    mp, rankingEnabled, endpoint);
            }
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
