using Microsoft.AspNetCore.DataProtection;
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
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Club-admin surface for the membership-fee (medlemsavgift) feature: define fee
    /// categories per year, generate per-member charges, confirm payments, and send
    /// payment requests by email with the /medlemsavgift/{token} pay link.
    /// All endpoints are gated to club admins (or site admins) of the club in question.
    /// </summary>
    public class MembershipFeeAdminController : SurfaceController
    {
        private readonly MembershipFeeService _feeService;
        private readonly ClubMembershipService _clubMembershipService;
        private readonly AdminAuthorizationService _auth;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly ClubService _clubService;
        private readonly EmailService _emailService;
        private readonly IDataProtector _protector;
        private readonly ILogger<MembershipFeeAdminController> _logger;

        // Same purpose string as the public MembershipFeeController — tokens must round-trip.
        private const string ProtectorPurpose = "Membership.FeeCharge.v1";

        public MembershipFeeAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            MembershipFeeService feeService,
            ClubMembershipService clubMembershipService,
            AdminAuthorizationService auth,
            IMemberService memberService,
            IMemberManager memberManager,
            ClubService clubService,
            EmailService emailService,
            IDataProtectionProvider dataProtectionProvider,
            ILogger<MembershipFeeAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _feeService = feeService;
            _clubMembershipService = clubMembershipService;
            _auth = auth;
            _memberService = memberService;
            _memberManager = memberManager;
            _clubService = clubService;
            _emailService = emailService;
            _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            _logger = logger;
        }

        // ── Overview ──────────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetOverview(int clubId, int year)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var categories = _feeService.GetCategories(clubId, year);
            var charges = _feeService.GetChargesForClubYear(clubId, year);

            return Json(new
            {
                success = true,
                categories = categories.Select(c => new
                {
                    c.Id,
                    c.MembershipType,
                    c.Label,
                    c.Amount
                }),
                paid = charges.Where(c => c.PaymentStatus == "Paid").Select(ChargeDto),
                unpaid = charges.Where(c => c.PaymentStatus != "Paid").Select(ChargeDto)
            });
        }

        private static object ChargeDto(MembershipFeeCharge c) => new
        {
            c.Id,
            c.MemberId,
            c.MemberName,
            c.MemberEmail,
            c.Amount,
            c.PaymentStatus,
            paymentSentDate = c.PaymentSentDate?.ToString("yyyy-MM-dd HH:mm"),
            c.PaymentSentBy,
            paidDate = c.PaidDate?.ToString("yyyy-MM-dd HH:mm"),
            hasEmail = !string.IsNullOrWhiteSpace(c.MemberEmail),
            covered = c.HouseholdCoveredByChargeId.HasValue
        };

        // ── Categories ────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCategory(int id, int clubId, int year,
            string membershipType, string label, decimal amount)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            if (string.IsNullOrWhiteSpace(membershipType))
                return Json(new { success = false, message = "Medlemstyp måste anges" });

            var cat = new MembershipFeeCategory
            {
                Id = id,
                ClubId = clubId,
                Year = year,
                MembershipType = membershipType.Trim(),
                Label = string.IsNullOrWhiteSpace(label) ? membershipType.Trim() : label.Trim(),
                Amount = amount
            };
            _feeService.SaveCategory(cat);
            return Json(new { success = true, data = new { cat.Id } });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteCategory(int id, int clubId)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            _feeService.DeleteCategory(id);
            return Json(new { success = true });
        }

        // ── Charges ───────────────────────────────────────────────────

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> GenerateCharges(int clubId, int year)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            // Bill from the per-club membership records (ClubMembership). Exclude members who
            // have left or are deceased; membership type drives which fee category applies.
            var members = _clubMembershipService.GetForClub(clubId)
                .Where(cm => cm.MembershipStatus != "Utträdd" && cm.MembershipStatus != "Avliden")
                .Select(cm => new MemberFeeInput
                {
                    MemberId = cm.MemberId,
                    MembershipType = cm.MembershipType ?? "",
                    HouseholdId = cm.HouseholdId ?? "",
                    HouseholdPrimary = cm.HouseholdPrimary
                })
                .ToList();

            var created = _feeService.GenerateChargesForClub(clubId, year, members);
            return Json(new { success = true, created });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkPaid(int chargeId)
        {
            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return Json(new { success = false, message = "Avgiften hittades inte" });
            if (!await _auth.IsClubAdminForClub(charge.ClubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var byId = await GetCurrentMemberIdAsync();
            _feeService.MarkPaid(chargeId, byId);
            return Json(new { success = true });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> MarkUnpaid(int chargeId)
        {
            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return Json(new { success = false, message = "Avgiften hittades inte" });
            if (!await _auth.IsClubAdminForClub(charge.ClubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            _feeService.MarkUnpaid(chargeId);
            return Json(new { success = true });
        }

        // ── Payment links ─────────────────────────────────────────────

        [HttpGet]
        public async Task<IActionResult> GetPaymentLink(int chargeId)
        {
            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return Json(new { success = false, message = "Avgiften hittades inte" });
            if (!await _auth.IsClubAdminForClub(charge.ClubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            return Json(new { success = true, url = BuildPayUrl(chargeId) });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendPaymentRequests(int clubId, int year)
        {
            if (!await _auth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var clubName = _clubService.GetClubNameById(clubId) ?? "Klubben";
            var charges = _feeService.GetChargesForClubYear(clubId, year);

            var sent = 0;
            foreach (var charge in charges)
            {
                if (charge.PaymentStatus == "Paid") continue;
                if (charge.HouseholdCoveredByChargeId.HasValue) continue; // covered by the primary's charge
                if (string.IsNullOrWhiteSpace(charge.MemberEmail)) continue;

                var payUrl = BuildPayUrl(charge.Id);
                try
                {
                    await _emailService.SendMembershipFeeRequestAsync(
                        charge.MemberEmail!, charge.MemberName ?? "medlem", clubName, year, charge.Amount, payUrl);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to send membership fee request for charge {ChargeId}", charge.Id);
                }
            }

            return Json(new { success = true, sent });
        }

        // ── Helpers ───────────────────────────────────────────────────

        private string BuildPayUrl(int chargeId)
        {
            var token = _protector.Protect(chargeId.ToString());
            return $"{Request.Scheme}://{Request.Host}/medlemsavgift/{Uri.EscapeDataString(token)}";
        }

        private async Task<int> GetCurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return 0;
            var data = _memberService.GetByEmail(current.Email ?? "");
            return data?.Id ?? 0;
        }

    }
}
