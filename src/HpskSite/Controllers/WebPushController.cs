using HpskSite.Models.WebPush;
using HpskSite.Services;
using HpskSite.Services.Notifications;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Browser Web Push subscribe/unsubscribe + VAPID public key, members-only.
    /// </summary>
    public class WebPushController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly WebPushService _webPush;

        public WebPushController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService authorizationService,
            WebPushService webPush)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _authorizationService = authorizationService;
            _webPush = webPush;
        }

        [HttpGet]
        public IActionResult GetPublicKey()
            => Json(new { success = true, configured = _webPush.IsConfigured, publicKey = _webPush.PublicKey });

        [HttpPost]
        public async Task<IActionResult> Subscribe([FromBody] WebPushSubscribeRequest request)
        {
            var memberId = await GetMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Du måste vara inloggad." });
            if (string.IsNullOrEmpty(request?.Endpoint) || string.IsNullOrEmpty(request.Keys?.P256dh) || string.IsNullOrEmpty(request.Keys?.Auth))
                return Json(new { success = false, message = "Ogiltig prenumeration." });

            _webPush.SaveSubscription(memberId.Value, request.Endpoint!, request.Keys!.P256dh!, request.Keys.Auth!,
                Request.Headers.UserAgent.ToString());
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> Unsubscribe([FromBody] WebPushUnsubscribeRequest request)
        {
            var memberId = await GetMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Du måste vara inloggad." });
            if (!string.IsNullOrEmpty(request?.Endpoint)) _webPush.RemoveSubscription(request.Endpoint!);
            return Json(new { success = true });
        }

        [HttpPost]
        public async Task<IActionResult> GetPreferences([FromBody] WebPushUnsubscribeRequest request)
        {
            var memberId = await GetMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Du måste vara inloggad." });
            var prefs = string.IsNullOrEmpty(request?.Endpoint) ? null : _webPush.GetPreferences(request.Endpoint!);
            return Json(new
            {
                success = true,
                matchPref = prefs?.MatchPref ?? "OpenMatchesOnly",
                rankingEnabled = prefs?.RankingEnabled ?? true
            });
        }

        [HttpPost]
        public async Task<IActionResult> SavePreferences([FromBody] WebPushPrefsRequest request)
        {
            var memberId = await GetMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Du måste vara inloggad." });
            if (string.IsNullOrEmpty(request?.Endpoint)) return Json(new { success = false, message = "Saknar prenumeration." });
            _webPush.SavePreferences(request.Endpoint!, request.MatchPref, request.RankingEnabled);
            return Json(new { success = true });
        }

        /// <summary>Send a test notification to the current member's own browsers.</summary>
        [HttpGet]
        public async Task<IActionResult> TestSend()
        {
            var memberId = await GetMemberIdAsync();
            if (memberId == null) return Json(new { success = false, message = "Du måste vara inloggad." });
            var n = await _webPush.SendToMemberAsync(memberId.Value, "pistol.nu",
                "Testnotis – web push fungerar! 🎯", "/traningsmatch/#topplista", "test");
            return Json(new { success = true, sent = n, configured = _webPush.IsConfigured });
        }

        /// <summary>Admin-only: generate a VAPID key pair to paste into appsettings WebPush section.</summary>
        [HttpGet]
        public async Task<IActionResult> GenerateVapidKeys()
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast administratörer." });
            var (pub, priv) = WebPushService.GenerateKeys();
            return Json(new { success = true, publicKey = pub, privateKey = priv });
        }

        private async Task<int?> GetMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            return _memberService.GetByEmail(current.Email ?? string.Empty)?.Id;
        }
    }
}
