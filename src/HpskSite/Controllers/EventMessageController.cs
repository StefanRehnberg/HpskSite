using HpskSite.Models.Messaging;
using HpskSite.Services;
using HpskSite.Services.Messaging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
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
    /// Transport for in-app functionary messaging. Discipline-agnostic: any staff screen (Fältskytte
    /// station / Stationer console, Springskytte start line / timing / scoring, Precision skjutledare,
    /// tävlingsledning) polls GetMessages with the scopes it represents, posts via PostMessage, and
    /// confirms receipt via AckMessage. Auth is the same four-tier competition-staff gate used across
    /// the site (site admin / competition manager / club admin / skjutledare, + regional admin for
    /// region-hosted comps). Mirrors the 10 s GetPatrolListState / SetPatrolDeparted poll pair.
    /// </summary>
    public class EventMessageController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AdminAuthorizationService _auth;
        private readonly EventMessageService _messages;
        private readonly ILogger<EventMessageController> _logger;

        public EventMessageController(
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
            ILogger<EventMessageController> logger)
            : base(umbracoContextAccessor, umbracoDatabaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _auth = auth;
            _messages = messages;
            _logger = logger;
        }

        // ---- Poll (read) ----

        [HttpGet]
        public async Task<IActionResult> GetMessages(int competitionId, string? scopes = null)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var selectors = ParseScopes(scopes);
            var feed = _messages.GetFeed(competitionId, selectors, viewer.Id);
            return Json(new { success = true, serverTime = feed.ServerTime, messages = feed.Messages });
        }

        /// <summary>Aggregating tävlingsledning console feed — every thread for the competition.</summary>
        [HttpGet]
        public async Task<IActionResult> GetAllMessages(int competitionId)
        {
            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(competitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var feed = _messages.GetAll(competitionId, viewer.Id);
            return Json(new { success = true, serverTime = feed.ServerTime, messages = feed.Messages });
        }

        // ---- Post ----

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostMessage([FromBody] PostEventMessageRequest request)
        {
            if (request == null || request.CompetitionId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });
            if (string.IsNullOrWhiteSpace(request.Body))
                return Json(new { success = false, message = "Meddelandet är tomt" });
            if (string.IsNullOrWhiteSpace(request.ScopeType))
                return Json(new { success = false, message = "Mottagare saknas" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });
            if (!await HasCompetitionAccessAsync(request.CompetitionId))
                return Json(new { success = false, message = "Ingen behörighet" });

            var body = request.Body.Trim();
            if (body.Length > 2000) body = body.Substring(0, 2000);

            var urgency = request.Urgency switch
            {
                MessageUrgency.Urgent => MessageUrgency.Urgent,
                MessageUrgency.Safety => MessageUrgency.Safety,
                _ => MessageUrgency.Normal
            };

            var msg = new EventMessage
            {
                CompetitionId = request.CompetitionId,
                Discipline = GetDiscipline(request.CompetitionId),
                ScopeType = request.ScopeType.Trim(),
                ScopeKey = string.Equals(request.ScopeType, MessageScopeType.All, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : (string.IsNullOrWhiteSpace(request.ScopeKey) ? null : request.ScopeKey.Trim()),
                FromMemberId = viewer.Id,
                FromName = viewer.Name,
                FromScopeType = string.IsNullOrWhiteSpace(request.FromScopeType) ? null : request.FromScopeType.Trim(),
                FromScopeKey = string.IsNullOrWhiteSpace(request.FromScopeKey) ? null : request.FromScopeKey.Trim(),
                Body = body,
                Urgency = urgency,
                // Store UTC — the app convention (client converts to local on display). Storing local
                // here made the browser read it as UTC and show times +2 h (CEST). Matches DepartedAt.
                CreatedDate = DateTime.UtcNow
            };

            var id = _messages.Post(msg);
            return Json(new { success = true, id });
        }

        // ---- Ack (mottaget-kvittens) ----

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AckMessage([FromBody] AckEventMessageRequest request)
        {
            if (request == null || request.MessageId <= 0)
                return Json(new { success = false, message = "Ogiltig förfrågan" });

            var viewer = await ResolveViewerAsync();
            if (viewer == null) return Json(new { success = false, message = "Inte inloggad" });

            // Authorize against the message's own competition (don't trust the client-supplied id blindly).
            var compId = _messages.GetCompetitionIdForMessage(request.MessageId);
            if (compId == null) return Json(new { success = false, message = "Meddelandet finns inte" });
            if (!await HasCompetitionAccessAsync(compId.Value))
                return Json(new { success = false, message = "Ingen behörighet" });

            _messages.Ack(request.MessageId, viewer.Id, viewer.Name);
            return Json(new { success = true });
        }

        // ---- helpers ----

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

        private static List<EventMessageScope> ParseScopes(string? scopes)
        {
            var list = new List<EventMessageScope>();
            if (string.IsNullOrWhiteSpace(scopes)) return list;
            foreach (var token in scopes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var s = EventMessageScope.Parse(token);
                if (s != null) list.Add(s);
            }
            return list;
        }

        private string GetDiscipline(int competitionId)
        {
            try
            {
                if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
                {
                    var comp = ctx.Content.GetById(competitionId);
                    // competitionType is stored as a plain string (FlexibleDropdown); read untyped to be safe.
                    return comp?.Value("competitionType")?.ToString() ?? "";
                }
            }
            catch { /* best-effort — discipline is informational */ }
            return "";
        }

        /// <summary>
        /// Four-tier competition-staff gate: site admin OR competition manager OR club admin for the
        /// competition's club OR skjutledare for that club; plus regional admin for region-hosted
        /// competitions (clubId unset). Mirrors AdminAuthorizationService.CanManageCompetitionInvoice.
        /// </summary>
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
                    if (await _auth.IsClubAdminForClub(clubId)) return true;   // includes regional admin for club's region
                    if (await _auth.IsSkjutledareForClub(clubId)) return true;
                }
                else
                {
                    // Region-hosted competition: gate on regional admin for the hosting region.
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
