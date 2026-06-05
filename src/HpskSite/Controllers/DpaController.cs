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
