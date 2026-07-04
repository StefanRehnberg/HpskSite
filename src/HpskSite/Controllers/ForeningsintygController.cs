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
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Surface controller for the föreningsintyg log — the per-member record of licence-support
    /// certificates a club issues. Issuing is a club/board act, never self-service.
    /// </summary>
    public class ForeningsintygController : SurfaceController
    {
        private readonly ForeningsintygService _foreningsintygService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<ForeningsintygController> _logger;

        public ForeningsintygController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            ForeningsintygService foreningsintygService,
            AdminAuthorizationService authorizationService,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<ForeningsintygController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _foreningsintygService = foreningsintygService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
        }

        /// <summary>
        /// List all föreningsintyg issued to a member. Readable by the member themselves, site admins,
        /// or a club admin for the member's primary club.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ListForMember(int memberId)
        {
            var current = await GetCurrentMemberDataAsync();
            if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

            bool isSelf = current.Id == memberId;
            bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
            if (!isSelf && !isSiteAdmin)
            {
                var candidate = _memberService.GetById(memberId);
                if (candidate == null) return Json(new { success = false, message = "Medlemmen hittades inte." });

                int.TryParse(candidate.GetValue<string>("primaryClubId") ?? "", out int candidateClubId);
                bool isClubAdmin = candidateClubId > 0 && await _authorizationService.IsClubAdminForClub(candidateClubId);
                if (!isClubAdmin) return Json(new { success = false, message = "Access denied" });
            }

            var entries = _foreningsintygService.GetForMember(memberId);
            return Json(new { success = true, data = entries.Select(Project) });
        }

        /// <summary>
        /// Record a new föreningsintyg for a member. Issuing is a club/board act — only a club admin
        /// for the member's primary club (or a site admin) may do it, never the member themselves.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddIntyg(int memberId, string issuedDate, string purpose,
            string description, string notes)
        {
            try
            {
                var current = await GetCurrentMemberDataAsync();
                if (current == null) return Json(new { success = false, message = "Du måste vara inloggad." });

                var member = _memberService.GetById(memberId);
                if (member == null) return Json(new { success = false, message = "Medlemmen hittades inte." });

                int.TryParse(member.GetValue<string>("primaryClubId") ?? "", out int clubId);

                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool isClubAdmin = clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId);
                if (!isSiteAdmin && !isClubAdmin)
                    return Json(new { success = false, message = "Du har inte behörighet att utfärda föreningsintyg för den här medlemmen." });

                if (string.IsNullOrWhiteSpace(purpose))
                    return Json(new { success = false, message = "Ändamål måste anges." });

                var parsedDate = ParseDate(issuedDate) ?? DateTime.Today;

                var entry = _foreningsintygService.Add(
                    memberId,
                    clubId,
                    parsedDate,
                    purpose.Trim(),
                    string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
                    current.Id,
                    string.IsNullOrWhiteSpace(notes) ? null : notes.Trim());

                _logger.LogInformation("Föreningsintyg {Id} issued to member {MemberId} by {IssuerId}",
                    entry.Id, memberId, current.Id);

                return Json(new { success = true, message = "Föreningsintyg registrerat.", data = new { entry.Id } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding föreningsintyg for member {MemberId}", memberId);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        /// <summary>
        /// Delete a föreningsintyg. Only a club admin for the member's primary club (or a site admin) may.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteIntyg(int id)
        {
            try
            {
                var memberId = _foreningsintygService.GetMemberIdForEntry(id);
                if (memberId == 0) return Json(new { success = false, message = "Intyget hittades inte." });

                var member = _memberService.GetById(memberId);
                int clubId = 0;
                if (member != null) int.TryParse(member.GetValue<string>("primaryClubId") ?? "", out clubId);

                bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
                bool isClubAdmin = clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId);
                if (!isSiteAdmin && !isClubAdmin)
                    return Json(new { success = false, message = "Åtkomst nekad" });

                _foreningsintygService.Delete(id);

                _logger.LogInformation("Föreningsintyg {Id} deleted", id);
                return Json(new { success = true, message = "Föreningsintyg borttaget." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting föreningsintyg {Id}", id);
                return Json(new { success = false, message = "Ett fel uppstod." });
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private object Project(MemberCertificateIssue e) => new
        {
            e.Id,
            e.MemberId,
            e.MemberName,
            e.ClubId,
            issuedDate = e.IssuedDate.ToString("yyyy-MM-dd"),
            e.Purpose,
            e.Description,
            e.IssuedByMemberId,
            e.IssuedByName,
            e.Notes,
            createdDate = e.CreatedDate.ToString("yyyy-MM-dd")
        };

        private static DateTime? ParseDate(string? value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)
                ? d : (DateTime?)null;

        private async Task<Umbraco.Cms.Core.Models.IMember?> GetCurrentMemberDataAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            return _memberService.GetByEmail(current.Email ?? "");
        }
    }
}
