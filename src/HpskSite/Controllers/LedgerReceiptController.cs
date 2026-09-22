using HpskSite.Models;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.Ledger;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kvittot på en mottagen betalning, på <c>/betalkvitto/{id}</c>.
    ///
    /// <para><b>⚠️⚠️ ALLT PÅ KVITTOT LÄSES UR KVITTORADEN, ALDRIG UR NULÄGET.</b> Föreningens namn,
    /// adress, bankgiro och momsregistrering är <b>snapshots</b> tagna när kvittot skrevs. Byter
    /// klubben bankgiro i morgon ska det här kvittot fortfarande visa det som gällde den dagen —
    /// annars ändrar en utfärdad handling sig i efterhand, vilket var fel 10 i den gamla modellen.
    /// Det är också därför den här sidan inte slår upp en enda uppgift om föreningen.</para>
    ///
    /// <para><b>⚠️ Skild från <c>/kvitto/{invoiceId}</c>.</b> Den vägen hör till den gamla
    /// fakturamodellen och blir fryst historik. De två får aldrig slås ihop: numren kommer ur
    /// olika serier och betyder olika saker.</para>
    /// </summary>
    [Route("betalkvitto")]
    public class LedgerReceiptController : Controller
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;

        public LedgerReceiptController(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth)
        {
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
        }

        [HttpGet("{receiptId:int}")]
        public async Task<IActionResult> Index(int receiptId)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null)
                return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/betalkvitto/{receiptId}")}");

            using var db = _databaseFactory.CreateDatabase();

            // ⚠️⚠️ SCHEMAT VÄLJS AV KVITTOTS EGET ID, inte av en utställare — den här vägen har
            // ingen. Sandlådans identiteter är IDENTITY(-1,-1), så tecknet ÄR schemat: ett
            // negativt kvittonummer kan bara betyda sbx. Läses ett sandlådekvitto ur dbo får vi
            // tomt och svarar 404, alltså "finns inte" om en handling som finns.
            var ldb = new LedgerDb(db, receiptId);

            var receipt = ldb.SingleOrDefault<LedgerReceipt>(
                "SELECT * FROM dbo.LedgerReceipt WHERE Id = @0", receiptId);

            if (receipt is null) return NotFound();

            var payment = ldb.SingleOrDefault<LedgerPayment>(
                "SELECT * FROM dbo.LedgerPayment WHERE Id = @0", receipt.PaymentId);

            var series = ldb.SingleOrDefault<LedgerNumberSeries>(
                "SELECT * FROM dbo.LedgerNumberSeries WHERE Id = @0", receipt.SeriesId);

            // ⚠️ Betalaren ska komma åt SITT kvitto utan att vara administratör — det är hela
            // meningen med det. Föreningens administratörer ska också komma åt det, för att kunna
            // svara på en fråga om det.
            var member = _memberService.GetByEmail(current.Email ?? string.Empty);
            var isPayer = member is not null && payment?.PayerMemberId == member.Id;

            if (!isPayer && !await IsIssuerAdminAsync(receipt.IssuerType, receipt.IssuerId))
                return Forbid();

            var model = new LedgerReceiptViewModel
            {
                Receipt = receipt,
                Payment = payment,
                Number = LedgerNumberAllocator.Format(series?.Prefix ?? "", receipt.Number),
                // ⚠️ Att betalningen ÄR återtagen är ett faktum om nuläget, inte om handlingen.
                // Kvittot rivs inte — det ligger hos betalaren — men sidan måste säga det, annars
                // visar vi en giltig handling om pengar som lämnats tillbaka.
                IsReversed = payment?.VoidedUtc is not null,
                ReversedReason = payment?.VoidReason,

                // ⚠️⚠️ ETT SANDLÅDEKVITTO MÅSTE SÄGA DET. Handlingen lämnar sidan — den skrivs ut,
                // mejlas, fotograferas — och tar ingen sidram med sig. Ett kvitto som ser äkta ut
                // men gäller ett test är en handling som ljuger, och den ligger hos betalaren.
                // ⚠️ Registret bor bara i dbo och skrivs aldrig om — se LedgerSchema.
                IsSandbox = ldb.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM dbo.LedgerIssuer WHERE Id = @0 AND Kind = 'sandbox'",
                    receipt.IssuerId) > 0
            };

            return View("~/Views/LedgerReceipt.cshtml", model);
        }

        private async Task<bool> IsIssuerAdminAsync(int issuerType, int issuerId)
        {
            if (issuerType == DocumentOwnerType.Club)
                return await _auth.IsClubAdminForClub(issuerId);

            if (issuerType == DocumentOwnerType.Region)
            {
                _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
                var regionCode = ctx?.Content?.GetById(issuerId)?.Value<string>("regionCode") ?? "";

                return !string.IsNullOrWhiteSpace(regionCode)
                       && await _auth.IsRegionalAdminForRegion(regionCode);
            }

            return false;
        }
    }

    /// <summary>Kvittot plus det lilla som INTE står på handlingen: numret och om raden är återtagen.</summary>
    public class LedgerReceiptViewModel
    {
        public LedgerReceipt Receipt { get; set; } = new();

        public LedgerPayment? Payment { get; set; }

        public string Number { get; set; } = "";

        public bool IsReversed { get; set; }

        public string? ReversedReason { get; set; }

        /// <summary>⚠️ Utfärdat i en sandlåda — handlingen måste säga det, i klartext.</summary>
        public bool IsSandbox { get; set; }
    }
}
