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
    /// Surface controller for a member's clubhouse keys / access tags / door codes
    /// (MemberAccessKey). Reading is self-or-admin; writing is club-managed (admins only).
    /// </summary>
    public class MemberAccessKeyController : SurfaceController
    {
        private readonly MemberAccessKeyService _keyService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ILogger<MemberAccessKeyController> _logger;

        public MemberAccessKeyController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            MemberAccessKeyService keyService,
            AdminAuthorizationService authorizationService,
            IMemberService memberService,
            IMemberManager memberManager,
            ILogger<MemberAccessKeyController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _keyService = keyService;
            _authorizationService = authorizationService;
            _memberService = memberService;
            _memberManager = memberManager;
            _logger = logger;
        }

        /// <summary>
        /// List a member's access keys. A member can always read their own; admins can read
        /// anyone's; a club admin can read members of the club they administer.
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

            var keys = _keyService.GetForMember(memberId);
            return Json(new { success = true, data = keys.Select(ProjectKey) });
        }

        /// <summary>
        /// Create or update an access key. Keys are club-managed — only a club admin for the
        /// member's club (or a site admin) may write; the member themself may NOT.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveKey(int? id, int memberId, string keyType, string identifier,
            decimal? deposit, string issuedDate, string returnedDate, string notes)
        {
            try
            {
                if (!await CanManageKeysForMember(memberId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                if (string.IsNullOrWhiteSpace(identifier))
                    return Json(new { success = false, message = "Identifierare måste anges" });

                var type = string.IsNullOrWhiteSpace(keyType) ? "Nyckel" : keyType.Trim();

                var current = await GetCurrentMemberDataAsync();

                if (id.HasValue && id.Value > 0)
                {
                    var existing = _keyService.GetById(id.Value);
                    if (existing == null) return Json(new { success = false, message = "Nyckeln hittades inte" });

                    existing.KeyType = type;
                    existing.Identifier = identifier.Trim();
                    existing.Deposit = deposit;
                    existing.IssuedDate = ParseDate(issuedDate);
                    existing.ReturnedDate = ParseDate(returnedDate);
                    existing.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();

                    _keyService.Update(existing);
                    return Json(new { success = true, message = "Nyckel uppdaterad", data = new { existing.Id } });
                }

                var key = new MemberAccessKey
                {
                    MemberId = memberId,
                    ClubId = await GetMemberClubId(memberId),
                    KeyType = type,
                    Identifier = identifier.Trim(),
                    Deposit = deposit,
                    IssuedDate = ParseDate(issuedDate),
                    ReturnedDate = ParseDate(returnedDate),
                    Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
                    CreatedDate = DateTime.UtcNow,
                    CreatedByMemberId = current?.Id
                };

                var saved = _keyService.Add(key);
                _logger.LogInformation("Access key {Identifier} ({KeyType}) added for member {MemberId}",
                    saved.Identifier, saved.KeyType, memberId);

                return Json(new { success = true, message = "Nyckel tillagd", data = new { saved.Id } });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error saving access key for member {MemberId}", memberId);
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        /// <summary>Delete an access key (hard delete). Club admin for the key's member, or site admin.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteKey(int id)
        {
            try
            {
                var memberId = _keyService.GetMemberIdForKey(id);
                if (memberId <= 0) return Json(new { success = false, message = "Nyckeln hittades inte" });

                if (!await CanManageKeysForMember(memberId))
                    return Json(new { success = false, message = "Åtkomst nekad" });

                _keyService.Delete(id);
                _logger.LogInformation("Access key {Id} deleted (member {MemberId})", id, memberId);

                return Json(new { success = true, message = "Nyckel borttagen" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting access key {Id}", id);
                return Json(new { success = false, message = "Ett fel uppstod" });
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        /// <summary>
        /// True if the current user may manage (write) keys for the given member: a site admin,
        /// or a club admin for the member's primary club. The member themself is NOT permitted.
        /// </summary>
        private async Task<bool> CanManageKeysForMember(int memberId)
        {
            if (await _authorizationService.IsCurrentUserAdminAsync()) return true;

            var clubId = await GetMemberClubId(memberId);
            return clubId > 0 && await _authorizationService.IsClubAdminForClub(clubId);
        }

        private Task<int> GetMemberClubId(int memberId)
        {
            var member = _memberService.GetById(memberId);
            if (member == null) return Task.FromResult(0);
            int.TryParse(member.GetValue<string>("primaryClubId") ?? "", out int clubId);
            return Task.FromResult(clubId);
        }

        private async Task<Umbraco.Cms.Core.Models.IMember?> GetCurrentMemberDataAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            return _memberService.GetByEmail(current.Email ?? "");
        }

        /// <summary>Parse a Y-m-d date string; returns null for empty/invalid input.</summary>
        private static DateTime? ParseDate(string? value) =>
            DateTime.TryParseExact(value, "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var d)
                ? d : (DateTime?)null;

        private static object ProjectKey(MemberAccessKey k) => new
        {
            id = k.Id,
            memberId = k.MemberId,
            memberName = k.MemberName,
            clubId = k.ClubId,
            keyType = k.KeyType,
            identifier = k.Identifier,
            deposit = k.Deposit,
            issuedDate = k.IssuedDate?.ToString("yyyy-MM-dd") ?? "",
            returnedDate = k.ReturnedDate?.ToString("yyyy-MM-dd") ?? "",
            notes = k.Notes,
            createdDate = k.CreatedDate.ToString("yyyy-MM-dd"),
            createdByMemberId = k.CreatedByMemberId
        };
    }
}
