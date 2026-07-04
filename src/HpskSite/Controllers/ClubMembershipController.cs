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
using Umbraco.Extensions;

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
        private readonly IMemberService _memberService;
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
            IMemberService memberService,
            ILogger<ClubMembershipController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _membershipService = membershipService;
            _authService = authService;
            _memberService = memberService;
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
            bool registeredInMap = false, string federations = "", string memberNotes = "")
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
            // HouseholdId / HouseholdPrimary are managed by SetHousehold (family multi-select), not here.

            _membershipService.Save(m);
            return Json(new { success = true });
        }

        /// <summary>
        /// Club admin's "remove from club": end this club's membership by setting it to Utträdd
        /// (+ MemberUntil = today). The person, login, data and any other-club memberships remain —
        /// reversible (set the status back to Aktiv). Distinct from the site-admin hard delete.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EndMembership(int memberId, int clubId)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var m = _membershipService.Get(memberId, clubId)
                    ?? new ClubMembership { MemberId = memberId, ClubId = clubId };
            m.MembershipStatus = "Utträdd";
            if (m.MemberUntil == null) m.MemberUntil = DateTime.Today;
            _membershipService.Save(m);

            return Json(new { success = true, memberUntil = m.MemberUntil?.ToString("yyyy-MM-dd") });
        }

        /// <summary>
        /// Family (familjeavgift) linking. The given member becomes the household's paying
        /// Huvudmedlem; the selected members are linked into the same household and set to
        /// membershipType "Familj". Members previously in this household but no longer selected
        /// are unlinked. Passing an empty selection dissolves the household.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetHousehold(int primaryMemberId, int clubId, int[] memberIds)
        {
            if (!await _authService.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var selected = (memberIds ?? Array.Empty<int>()).Where(id => id != primaryMemberId).Distinct().ToList();

            var primary = _membershipService.Get(primaryMemberId, clubId)
                          ?? new ClubMembership { MemberId = primaryMemberId, ClubId = clubId, MembershipStatus = "Aktiv" };

            // Determine the household id (existing, or a fresh GUID when forming a new household).
            var householdId = string.IsNullOrWhiteSpace(primary.HouseholdId) ? null : primary.HouseholdId;

            if (selected.Count == 0)
            {
                // Dissolve: unlink everyone currently in this household, clear the primary.
                if (householdId != null)
                    foreach (var existingMember in _membershipService.GetByHousehold(householdId, clubId))
                    {
                        existingMember.HouseholdId = null;
                        existingMember.HouseholdPrimary = false;
                        _membershipService.Save(existingMember);
                    }
                primary.HouseholdId = null;
                primary.HouseholdPrimary = false;
                _membershipService.Save(primary);
                return Json(new { success = true, householdMemberCount = 0 });
            }

            if (householdId == null) householdId = Guid.NewGuid().ToString();

            // Primary: family membership holder (billed the Familj fee).
            primary.HouseholdId = householdId;
            primary.HouseholdPrimary = true;
            primary.MembershipType = "Familj";
            _membershipService.Save(primary);

            // Unlink members that were in the household but are no longer selected.
            foreach (var existingMember in _membershipService.GetByHousehold(householdId, clubId))
            {
                if (existingMember.MemberId == primaryMemberId) continue;
                if (!selected.Contains(existingMember.MemberId))
                {
                    existingMember.HouseholdId = null;
                    existingMember.HouseholdPrimary = false;
                    _membershipService.Save(existingMember);
                }
            }

            // Link (or refresh) each selected member as a covered family member.
            foreach (var mid in selected)
            {
                var fm = _membershipService.Get(mid, clubId)
                         ?? new ClubMembership { MemberId = mid, ClubId = clubId, MembershipStatus = "Aktiv" };
                fm.HouseholdId = householdId;
                fm.HouseholdPrimary = false;
                fm.MembershipType = "Familj";
                _membershipService.Save(fm);
            }

            return Json(new { success = true, householdMemberCount = selected.Count });
        }

        private object Project(ClubMembership? m, int memberId, int clubId)
        {
            // Household context for the family multi-select.
            bool isPrimary = m?.HouseholdPrimary ?? false;
            var householdMemberIds = new List<int>();
            int mainMemberId = 0;
            string mainMemberName = "";
            if (m != null && !string.IsNullOrWhiteSpace(m.HouseholdId))
            {
                var household = _membershipService.GetByHousehold(m.HouseholdId, clubId);
                if (isPrimary)
                {
                    householdMemberIds = household.Where(h => h.MemberId != memberId).Select(h => h.MemberId).ToList();
                }
                else
                {
                    var mainRow = household.FirstOrDefault(h => h.HouseholdPrimary);
                    if (mainRow != null)
                    {
                        mainMemberId = mainRow.MemberId;
                        mainMemberName = MemberName(mainRow.MemberId);
                    }
                }
            }

            return new
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
                // Family / household
                isHouseholdPrimary = isPrimary,
                inHousehold = m != null && !string.IsNullOrWhiteSpace(m.HouseholdId),
                householdMemberIds,          // other members (when this member is the Huvudmedlem)
                householdMainMemberId = mainMemberId,   // the Huvudmedlem (when this member is covered)
                householdMainMemberName = mainMemberName
            };
        }

        private string MemberName(int memberId)
        {
            var mem = _memberService.GetById(memberId);
            if (mem == null) return "";
            var name = $"{mem.GetValue<string>("firstName")} {mem.GetValue<string>("lastName")}".Trim();
            return string.IsNullOrEmpty(name) ? (mem.Name ?? "") : name;
        }

        private static string Fmt(DateTime? d) => d?.ToString("yyyy-MM-dd") ?? "";
        private static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static DateTime? ParseDate(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParse(s.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : (DateTime?)null;
        }
    }
}
