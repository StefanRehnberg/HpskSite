using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Middleware
{
    /// <summary>
    /// Anonymous page-view logger feeding the Statistik tab visitor chart.
    /// Sets a short-lived opaque session cookie, stores SHA-256 of its value alongside path
    /// and timestamp. Throttled per (sessionHash, path) so a user clicking around doesn't
    /// flood the table.
    /// </summary>
    public class VisitorTrackingMiddleware
    {
        private const string CookieName = "_pn_v";
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan ThrottleWindow = TimeSpan.FromMinutes(5);

        // Session+path -> last-write timestamp. Pruned opportunistically when entries expire.
        private static readonly ConcurrentDictionary<string, DateTime> _lastWrite = new();
        private static DateTime _lastPrune = DateTime.UtcNow;

        private static readonly string[] SkipPathPrefixes =
        {
            "/umbraco", "/api", "/hubs", "/css", "/js", "/scripts", "/lib",
            "/images", "/media", "/fonts", "/favicon"
        };

        private static readonly string[] SkipExtensions =
        {
            ".css", ".js", ".map", ".png", ".jpg", ".jpeg", ".gif", ".svg",
            ".webp", ".ico", ".woff", ".woff2", ".ttf", ".eot", ".pdf", ".xml",
            ".txt", ".json"
        };

        private static readonly string[] BotMarkers =
        {
            "bot", "crawler", "spider", "slurp", "facebookexternalhit",
            "embedly", "preview", "monitor", "uptime"
        };

        private readonly RequestDelegate _next;
        private readonly ILogger<VisitorTrackingMiddleware> _logger;

        public VisitorTrackingMiddleware(
            RequestDelegate next,
            ILogger<VisitorTrackingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IUmbracoDatabaseFactory databaseFactory)
        {
            var shouldTrack = ShouldTrack(context);
            string? sessionHash = null;

            if (shouldTrack)
            {
                sessionHash = EnsureSessionCookie(context);
            }

            await _next(context);

            if (!shouldTrack || sessionHash == null) return;

            // Skip 4xx/5xx — those aren't real page views.
            if (context.Response.StatusCode >= 400) return;

            try
            {
                var path = context.Request.Path.Value ?? "/";
                if (path.Length > 512) path = path.Substring(0, 512);

                var throttleKey = sessionHash + "|" + path;
                var now = DateTime.UtcNow;

                if (_lastWrite.TryGetValue(throttleKey, out var last) && (now - last) < ThrottleWindow)
                {
                    return;
                }
                _lastWrite[throttleKey] = now;
                MaybePrune(now);

                using var db = databaseFactory.CreateDatabase();
                await db.ExecuteAsync(
                    "INSERT INTO [VisitorLogs] (VisitedAt, SessionHash, [Path]) VALUES (@0, @1, @2)",
                    now, sessionHash, path);
            }
            catch (Exception ex)
            {
                // Never break the request pipeline because of telemetry.
                _logger.LogWarning(ex, "Failed to record visitor log");
            }
        }

        private static bool ShouldTrack(HttpContext context)
        {
            if (!HttpMethods.IsGet(context.Request.Method)) return false;
            if (context.WebSockets.IsWebSocketRequest) return false;

            var path = context.Request.Path.Value;
            if (string.IsNullOrEmpty(path)) return false;

            foreach (var prefix in SkipPathPrefixes)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
            }

            foreach (var ext in SkipExtensions)
            {
                if (path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return false;
            }

            var ua = context.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrEmpty(ua)) return false;
            var uaLower = ua.ToLowerInvariant();
            foreach (var marker in BotMarkers)
            {
                if (uaLower.Contains(marker)) return false;
            }

            return true;
        }

        private static string EnsureSessionCookie(HttpContext context)
        {
            var existing = context.Request.Cookies[CookieName];

            string raw;
            if (!string.IsNullOrEmpty(existing) && existing.Length >= 16)
            {
                raw = existing;
            }
            else
            {
                raw = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
                    .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            }

            // Re-issue every request to slide the 30-min expiry.
            context.Response.Cookies.Append(CookieName, raw, new CookieOptions
            {
                HttpOnly = true,
                Secure = context.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.Add(SessionLifetime),
                IsEssential = false
            });

            return Sha256Hex(raw);
        }

        private static string Sha256Hex(string input)
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        private static void MaybePrune(DateTime now)
        {
            if ((now - _lastPrune).TotalMinutes < 30) return;
            _lastPrune = now;

            var cutoff = now - ThrottleWindow;
            foreach (var kv in _lastWrite)
            {
                if (kv.Value < cutoff)
                {
                    _lastWrite.TryRemove(kv.Key, out _);
                }
            }
        }
    }
}
