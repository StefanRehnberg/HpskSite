using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Public, token-addressed membership-fee (medlemsavgift) pay page — NO login required
    /// so an older member can pay from an emailed link or a printed QR. Shows the club's
    /// Swish QR (reusing the competition-payment Swish machinery), the amount, and the
    /// "Betald" state once the club admin confirms receipt. Distinct claim/received model:
    /// the payer can lodge a "Jag har betalat" claim, only the club admin sets Paid.
    /// Routed MVC controller (no Umbraco node) — same pattern as ReceiptController.
    /// </summary>
    [Route("medlemsavgift")]
    public class MembershipFeeController : Controller
    {
        private readonly IDataProtectionProvider _dataProtectionProvider;
        private readonly IDataProtector _protector;
        private readonly MembershipFeeService _feeService;
        private readonly IUmbracoContextFactory _umbracoContextFactory;
        private readonly ILogger<MembershipFeeController> _logger;

        // Must match MembershipFeeAdminController's purpose so tokens round-trip.
        private const string ProtectorPurpose = "Membership.FeeCharge.v1";

        public MembershipFeeController(
            IDataProtectionProvider dataProtectionProvider,
            MembershipFeeService feeService,
            IUmbracoContextFactory umbracoContextFactory,
            ILogger<MembershipFeeController> logger)
        {
            _dataProtectionProvider = dataProtectionProvider;
            _protector = dataProtectionProvider.CreateProtector(ProtectorPurpose);
            _feeService = feeService;
            _umbracoContextFactory = umbracoContextFactory;
            _logger = logger;
        }

        [HttpGet("{token}")]
        public IActionResult Index(string token)
        {
            int chargeId;
            try { chargeId = int.Parse(_protector.Unprotect(token)); }
            catch { return View("~/Views/MembershipFeePay.cshtml", MembershipFeePayModel.Invalid()); }

            var charge = _feeService.GetCharge(chargeId);
            if (charge == null)
                return View("~/Views/MembershipFeePay.cshtml", MembershipFeePayModel.Invalid());

            var model = new MembershipFeePayModel
            {
                Found = true,
                Token = token,
                Year = charge.Year,
                Amount = charge.Amount,
                IsPaid = charge.PaymentStatus == "Paid",
                PaymentClaimed = charge.PaymentSentDate.HasValue,
                Covered = charge.HouseholdCoveredByChargeId.HasValue
            };

            // Resolve club name + Swish number from the club content node (has UmbracoContext).
            using (var cref = _umbracoContextFactory.EnsureUmbracoContext())
            {
                var club = cref.UmbracoContext.Content?.GetById(charge.ClubId);
                if (club != null)
                {
                    model.ClubName = club.Value<string>("clubName") ?? club.Name ?? "";
                    // NEW club-doctype property the owner will add; read defensively.
                    model.SwishNumber = (club.HasProperty("swishNumber") ? club.Value<string>("swishNumber") : null) ?? "";
                }
            }

            // Build the Swish QR + app URL in the controller (SwishQrCodeGenerator is static).
            // A household-covered charge (familjeavgift) has no separate fee → no QR.
            var normalized = model.SwishNumber.Trim().Replace(" ", "").Replace("-", "");
            if (!model.Covered && SwishQrCodeGenerator.IsValidSwishNumber(normalized))
            {
                var amountStr = charge.Amount.ToString("0.00", CultureInfo.InvariantCulture);
                var message = $"Medlemsavgift {charge.Year}";
                try
                {
                    var png = SwishQrCodeGenerator.GeneratePng(normalized, amountStr, message);
                    model.SwishQrDataUri = "data:image/png;base64," + Convert.ToBase64String(png);
                    model.SwishAppUrl = SwishQrCodeGenerator.GetSwishAppUrl(normalized, amountStr, message);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to build Swish QR for membership fee charge {ChargeId}", chargeId);
                }
            }

            return View("~/Views/MembershipFeePay.cshtml", model);
        }

        [HttpPost("{token}/betalat")]
        [IgnoreAntiforgeryToken] // public page, no login → no antiforgery token available
        public IActionResult MarkSent(string token)
        {
            int chargeId;
            try { chargeId = int.Parse(_protector.Unprotect(token)); }
            catch { return View("~/Views/MembershipFeePay.cshtml", MembershipFeePayModel.Invalid()); }

            _feeService.SetPaymentSent(chargeId, "Medlem via länk");

            // Redirect back to the pay page (PRG) so a refresh doesn't re-post.
            return Redirect($"/medlemsavgift/{token}");
        }
    }

    /// <summary>Typed model for the chromeless /medlemsavgift/{token} pay page.</summary>
    public class MembershipFeePayModel
    {
        public bool Found { get; set; }
        public string Token { get; set; } = "";
        public string ClubName { get; set; } = "";
        public int Year { get; set; }
        public decimal Amount { get; set; }
        public bool IsPaid { get; set; }
        public bool PaymentClaimed { get; set; }
        public bool Covered { get; set; }
        public string SwishNumber { get; set; } = "";
        public string SwishQrDataUri { get; set; } = "";
        public string SwishAppUrl { get; set; } = "";

        public bool HasSwish => !string.IsNullOrEmpty(SwishQrDataUri);

        public static MembershipFeePayModel Invalid() => new MembershipFeePayModel { Found = false };
    }
}
