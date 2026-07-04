using System.Globalization;
using HpskSite.Models;
using HpskSite.Services;
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

namespace HpskSite.Controllers
{
    /// <summary>
    /// Club-admin management of a member's per-club membership record (ClubMembership).
    /// Club-scoped: a club admin edits the membership for THEIR club only. Person-level
    /// facts are edited by the member at Min sida (MemberController). See MEMBER_DATABASE.md.
    /// </summary>
    public class ClubMembershipController : SurfaceController
    {
        private readonly ClubMembershipService _membershipService;
        private readonly AdminAuthorizationService _authService;
        private readonly ILogger<ClubMembershipController> _logger;

        public ClubMembershipController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            ClubMembershipService membershipService,
            AdminAuthorizationService authService,
            ILogger<ClubMembershipController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _membershipService = membershipService;
            _authService = authService;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetMembership(int memberId, int clubId)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var m = _membershipService.Get(memberId, clubId);
            return Json(new { success = true, data = Project(m, memberId, clubId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveMembership(
            int memberId, int clubId,
            string membershipType = "", string membershipStatus = "Aktiv",
            string memberSince = "", string memberUntil = "", string endReason = "",
            bool backgroundCheckApproved = false, string backgroundCheckDate = "",
            bool registeredInMap = false, string federations = "", string memberNotes = "",
            string householdId = "", bool householdPrimary = false)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var existing = _membershipService.Get(memberId, clubId);
            var m = existing ?? new ClubMembership { MemberId = memberId, ClubId = clubId };

            m.MembershipType = string.IsNullOrWhiteSpace(membershipType) ? null : membershipType.Trim();
            m.MembershipStatus = string.IsNullOrWhiteSpace(membershipStatus) ? "Aktiv" : membershipStatus.Trim();
            m.MemberSince = ParseDate(memberSince);
            m.MemberUntil = ParseDate(memberUntil);
            m.EndReason = NullIfEmpty(endReason);
            m.BackgroundCheckApproved = backgroundCheckApproved;
            m.BackgroundCheckDate = ParseDate(backgroundCheckDate);
            m.RegisteredInMap = registeredInMap;
            m.Federations = NullIfEmpty(federations);
            m.MemberNotes = NullIfEmpty(memberNotes);
            m.HouseholdId = NullIfEmpty(householdId);
            m.HouseholdPrimary = householdPrimary;

            _membershipService.Save(m);
            return Json(new { success = true });
        }

        private static object Project(ClubMembership? m, int memberId, int clubId) => new
        {
            memberId,
            clubId,
            membershipType = m?.MembershipType ?? "",
            membershipStatus = m?.MembershipStatus ?? "Aktiv",
            memberSince = Fmt(m?.MemberSince),
            memberUntil = Fmt(m?.MemberUntil),
            endReason = m?.EndReason ?? "",
            backgroundCheckApproved = m?.BackgroundCheckApproved ?? false,
            backgroundCheckDate = Fmt(m?.BackgroundCheckDate),
            registeredInMap = m?.RegisteredInMap ?? false,
            federations = m?.Federations ?? "",
            memberNotes = m?.MemberNotes ?? "",
            householdId = m?.HouseholdId ?? "",
            householdPrimary = m?.HouseholdPrimary ?? false
        };

        private static string Fmt(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";
        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }
    }
}
