using HpskSite.Models.Messaging;
using HpskSite.Services;
using HpskSite.Services.Messaging;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Outward participant (shooter-facing) competition notifications. Organizer composes and sends
    /// to registered shooters scoped by Alla / Klass / Individ; delivery is web-push + a persisted
    /// EventMessage row that also feeds the shooter's in-app inbox. The send/preview/audience
    /// endpoints use the same four-tier competition-staff gate as EventMessageController; the shooter
    /// inbox read (GetMyMessages) is gated on being registered for the competition (or staff).
    /// </summary>
    public class ParticipantMessageController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AdminAuthorizationService _auth;
        private readonly EventMessageService _messages;
        private readonly ParticipantAudienceResolver _audienceResolver;
        private readonly ParticipantNotificationService _notifier;
        private readonly ILogger<ParticipantMessageController> _logger;

        public ParticipantMessageController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            EventMessageService messages,
            ParticipantAudienceResolver audienceResolver,
            ParticipantNotificationService notifier,
            ILogger<ParticipantMessageController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _auth = auth;
            _messages = messages;
            _audienceResolver = audienceResolver;
            _notifier = notifier;
            _logger = logger;
        }

        // ---- Organizer: audience summary for the composer ----

        [HttpGet]
        public async Task<IActionResult> GetParticipantAudience(int competitionId)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var summary = _audienceResolver.GetAudienceSummary(competitionId);
            return Json(new { success = true, total = summary.Total, classes = summary.Classes });
        }

        [HttpGet]
        public async Task<IActionResult> PreviewCount(int competitionId, string scopeType, string? scopeKey = null)
        {
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var count = _audienceResolver.Count(competitionId, scopeType ?? MessageScopeType.All, scopeKey);
            return Json(new { success = true, count });
        }

        // ---- Organizer: send ----

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendToParticipants([FromBody] PostParticipantMessageRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.Body))
                return Json(new { success = false, message = "Meddelandet är tomt" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var count = _notifier.Notify(
                request.CompetitionId,
                string.IsNullOrWhiteSpace(request.ScopeType) ? MessageScopeType.All : request.ScopeType,
                request.ScopeKey,
                request.Body,
                request.Urgency,
                viewer.Id,
                viewer.Name);

            return Json(new { success = true, recipientCount = count });
        }

        // ---- Shooter: inbox ----

        [HttpGet]
        public async Task<IActionResult> GetMyMessages(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });

            // Staff see the full sent-log for the competition; a registered shooter sees their own feed.
            var isStaff = await HasCompetitionAccessAsync(competitionId);
            if (isStaff)
            {
                var log = _messages.GetParticipantLog(competitionId, viewer.Id);
                return Json(new { success = true, serverTime = log.ServerTime, messages = log.Messages });
            }

            if (!_audienceResolver.IsRegistered(competitionId, viewer.Id))
                return Json(new { success = true, serverTime = DateTime.UtcNow, messages = Array.Empty<object>() });

            var classes = _audienceResolver.GetMemberClasses(competitionId, viewer.Id);
            var feed = _messages.GetParticipantFeed(competitionId, classes, viewer.Id);
            return Json(new { success = true, serverTime = feed.ServerTime, messages = feed.Messages });
        }

        // ---- helpers (mirror EventMessageController) ----

        private record Viewer(int Id, string Name);

        private async Task<Viewer?> ResolveViewerAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            var md = _memberService.GetByEmail(current.Email ?? string.Empty);
            if (md == null) return null;
            var first = md.GetValue<string>("firstName") ?? "";
            var last = md.GetValue<string>("lastName") ?? "";
            var name = $"{first} {last}".Trim();
            if (string.IsNullOrEmpty(name)) name = md.Name ?? "";
            return new Viewer(md.Id, name);
        }

        private async Task<bool> HasCompetitionAccessAsync(int competitionId)
        {
            if (competitionId <= 0) return false;
            try
            {
                if (await _auth.IsCurrentUserAdminAsync()) return true;
                if (await _auth.IsCompetitionManager(competitionId)) return true;

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return false;
                var comp = ctx.Content.GetById(competitionId);
                if (comp == null) return false;

                var clubId = comp.Value<int>("clubId");
                if (clubId > 0)
                {
                    if (await _auth.IsClubAdminForClub(clubId)) return true;
                    if (await _auth.IsSkjutledareForClub(clubId)) return true;
                }
                else
                {
                    var regionCode = comp.Value<string>("regionalFederation") ?? "";
                    if (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode))
                        return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
