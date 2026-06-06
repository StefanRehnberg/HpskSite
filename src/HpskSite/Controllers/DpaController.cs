using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Endpoints for the Personuppgiftsbiträdesavtal (DPA) acceptance gate shown to club
    /// administrators. A club admin reviews the agreement at /personuppgiftsbitradesavtal and
    /// accepts it on their club's behalf; the acceptance is the legally operative act
    /// (GDPR Art. 28(9) — electronic form is sufficient).
    /// </summary>
    public class DpaController : SurfaceController
    {
        private readonly DpaAcceptanceService _dpaService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ILogger<DpaController> _logger;

        public DpaController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            DpaAcceptanceService dpaService,
            AdminAuthorizationService authorizationService,
            IMemberManager memberManager,
            IMemberService memberService,
            ILogger<DpaController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _dpaService = dpaService;
            _authorizationService = authorizationService;
            _memberManager = memberManager;
            _memberService = memberService;
            _logger = logger;
        }

        public class AcceptDpaRequest
        {
            public int ClubId { get; set; }
        }

        /// <summary>
        /// Returns the club's DPA acceptance status. Any logged-in member who can see the
        /// club admin panel may read it (the panel itself is club-admin gated upstream).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDpaStatus(int clubId)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null)
                return Json(new { success = false, message = "Inte inloggad." });

            var status = await _dpaService.GetStatusForClubAsync(clubId);
            return Json(new
            {
                success = true,
                accepted = status.Accepted,
                currentVersion = status.CurrentVersion,
                acceptedVersion = status.AcceptedVersion,
                acceptedDate = status.AcceptedDate?.ToString("yyyy-MM-dd"),
                acceptedByName = status.AcceptedByName
            });
        }

        /// <summary>
        /// Records the current club admin's acceptance of the current DPA version for the club.
        /// Requires the caller to be a club admin for the club (site/regional admins included
        /// via IsClubAdminForClub).
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptDpa([FromBody] AcceptDpaRequest request)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null)
                return Json(new { success = false, message = "Inte inloggad." });

            if (request == null || request.ClubId <= 0)
                return Json(new { success = false, message = "Ogiltig klubb." });

            if (!await _authorizationService.IsClubAdminForClub(request.ClubId))
                return Json(new { success = false, message = "Du har inte behörighet att godkänna avtalet för den här klubben." });

            var member = _memberService.GetByEmail(current.Email ?? string.Empty);
            if (member == null)
                return Json(new { success = false, message = "Kunde inte identifiera medlemmen." });

            await _dpaService.RecordAcceptanceAsync(
                request.ClubId, member.Id, member.Name, GetClientIp());

            return Json(new
            {
                success = true,
                version = DpaInfo.Version,
                acceptedDate = DateTime.Now.ToString("yyyy-MM-dd"),
                acceptedByName = member.Name
            });
        }

        /// <summary>
        /// Site-admin-only overview of DPA acceptance across every club. Returns summary
        /// counts (current / outdated / never accepted) plus a per-club row carrying the
        /// evidence (version, date, who accepted). Used by the compliance card on the
        /// admin Klubbar tab to chase clubs without a current acceptance.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetDpaOverview()
        {
            if (!await _authorizationService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast siteadministratörer har åtkomst." });

            // One table read for all clubs (no per-club queries — perf rule).
            var statuses = await _dpaService.GetAllStatusesAsync();

            var clubs = new List<object>();
            int current = 0, outdated = 0, none = 0;

            var sortedNodes = EnumerateClubNodes()
                .OrderBy(n => n.Value<string>("clubName") ?? n.Name, StringComparer.CurrentCultureIgnoreCase);

            foreach (var clubNode in sortedNodes)
            {
                var clubName = clubNode.Value<string>("clubName") ?? clubNode.Name ?? "";
                var region = clubNode.Value<string>("regionalFederation") ?? "";

                statuses.TryGetValue(clubNode.Id, out var status);

                // status == null → no row at all → never accepted.
                string state;
                if (status == null) { state = "none"; none++; }
                else if (status.Accepted) { state = "current"; current++; }
                else { state = "outdated"; outdated++; }

                clubs.Add(new
                {
                    clubId = clubNode.Id,
                    clubName,
                    region,
                    state, // "current" | "outdated" | "none"
                    acceptedVersion = status?.AcceptedVersion,
                    acceptedDate = status?.AcceptedDate?.ToString("yyyy-MM-dd"),
                    acceptedByName = status?.AcceptedByName
                });
            }

            return Json(new
            {
                success = true,
                currentVersion = DpaInfo.Version,
                summary = new { total = clubs.Count, current, outdated, none },
                clubs
            });
        }

        /// <summary>
        /// Enumerates published club nodes from the content cache — regional structure
        /// (Home → RegionalPage → clubsPage → club) plus the legacy root-level clubsPage.
        /// Mirrors ClubAdminController.GetClubsAsContent without the per-club view model cost.
        /// </summary>
        private IEnumerable<Umbraco.Cms.Core.Models.PublishedContent.IPublishedContent> EnumerateClubNodes()
        {
            var content = UmbracoContext.Content;
            var root = content?.GetAtRoot().FirstOrDefault();
            if (root == null)
                yield break;

            foreach (var regionalPage in root.Children().Where(c => c.ContentType.Alias == "regionalPage"))
            {
                var clubsPage = regionalPage.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
                if (clubsPage == null) continue;
                foreach (var club in clubsPage.Children().Where(c => c.ContentType.Alias == "club"))
                    yield return club;
            }

            var rootClubsHub = root.Children().FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
            if (rootClubsHub != null)
            {
                foreach (var club in rootClubsHub.Children().Where(c => c.ContentType.Alias == "club"))
                    yield return club;
            }
        }

        /// <summary>Best-effort client IP, honouring the reverse proxy's X-Forwarded-For.</summary>
        private string? GetClientIp()
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded))
                return forwarded.Split(',')[0].Trim();
            return HttpContext.Connection.RemoteIpAddress?.ToString();
        }
    }
}
