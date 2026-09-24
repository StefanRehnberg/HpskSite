using HpskSite.Models;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.CompetitionFees;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fakturan till en klubb, utskriftsbar: <c>/klubbfaktura/{id}?t=</c>.
    ///
    /// <para><b>⚠️⚠️ FUNGERAR UTAN INLOGGNING — med länkens token.</b> Den fakturerade klubbens kassör
    /// loggar aldrig in (<c>organiser-side-consolidation</c>): mejlet är räkningen, och länken är
    /// dokumentet. Token är skyddad av DataProtection och binder exakt EN faktura.
    /// Utan token: arrangören, mottagarklubbens admin eller sajtadmin.</para>
    ///
    /// <para><b>Egen adress, inte <c>/faktura/{id}</c>.</b> Den gamla modellens fakturor är
    /// Umbraco-noder med nod-id; de nya är rader med egna id. Samma adress hade låtit samma tal peka på
    /// två olika handlingar.</para>
    ///
    /// <para><b>⚠️ Ingen betalstatus på dokumentet</b> (<c>kvitto-vs-betalningsbekraftelse</c>): en
    /// utskriven faktura hade fortsatt påstå ett läge som ändrats. Sidan visar däremot "betald" som en
    /// ruta UTANFÖR arket, och den skrivs inte ut.</para>
    /// </summary>
    [Route("klubbfaktura")]
    public class ClubInvoiceDocumentController : Controller
    {
        private const string Purpose = "LedgerCharge.Document.v1";

        private readonly LedgerChargeService _charges;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _auth;
        private readonly IDataProtector _protector;

        public ClubInvoiceDocumentController(
            LedgerChargeService charges,
            IMemberManager memberManager,
            AdminAuthorizationService auth,
            IDataProtectionProvider dataProtection)
        {
            _charges = charges;
            _memberManager = memberManager;
            _auth = auth;
            _protector = CreateProtector(dataProtection);
        }

        public static IDataProtector CreateProtector(IDataProtectionProvider provider) => provider.CreateProtector(Purpose);

        public static bool TokenMatches(IDataProtector protector, string? token, int chargeId)
        {
            if (string.IsNullOrWhiteSpace(token)) return false;
            try { return protector.Unprotect(token) == chargeId.ToString(); }
            catch { return false; }
        }

        [HttpGet("{chargeId:int}")]
        public async Task<IActionResult> Index(int chargeId, string? t = null)
        {
            var charge = _charges.Get(chargeId);
            if (charge == null) return NotFound();

            var viaToken = TokenMatches(_protector, t, chargeId);
            if (!viaToken)
            {
                var current = await _memberManager.GetCurrentMemberAsync();
                if (current == null)
                    return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/klubbfaktura/{chargeId}")}");

                var allowed = await _auth.CanManageCompetitionFinanceAsync(charge.SourceId)
                              || await _auth.IsClubAdminForClub(charge.RecipientId);
                if (!allowed) return NotFound();
            }

            // Kreditnotan visas mot sin faktura; saldot är alltid fakturans.
            var invoice = charge.IsCredit && charge.CreditsChargeId is int invId ? _charges.Get(invId) ?? charge : charge;
            var balance = _charges.BalanceOf(invoice);

            string? bgQr = null;
            if (!charge.IsCredit && !charge.IsVoided && balance.Outstanding > 0
                && BankgiroQrCodeGenerator.IsValidBankgiro(charge.IssuerBankgiro))
            {
                try
                {
                    bgQr = Convert.ToBase64String(BankgiroQrCodeGenerator.GeneratePng(
                        charge.IssuerName, charge.IssuerBankgiro!, balance.Outstanding, charge.Reference,
                        payeeOrgNumber: charge.IssuerOrgNumber, invoiceDate: charge.IssueDate));
                }
                catch { /* Uppgifterna står som text ändå. */ }
            }

            return View("~/Views/ClubInvoiceDocument.cshtml", new ClubInvoiceDocumentModel
            {
                Charge = charge,
                Invoice = invoice,
                Balance = balance,
                BgQrCodeBase64 = bgQr,
                Token = viaToken ? t : null
            });
        }
    }

    public class ClubInvoiceDocumentModel
    {
        public LedgerCharge Charge { get; set; } = new();
        public LedgerCharge Invoice { get; set; } = new();
        public LedgerChargeBalance Balance { get; set; }
        public string? BgQrCodeBase64 { get; set; }
        public string? Token { get; set; }
    }
}
