using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Print-friendly "Faktura" (invoice) at /faktura/{invoiceId}, opened from the club's and krets's
    /// Fakturor lists. The counterpart to /kvitto/{invoiceId}: the Kvitto proves a payment was made and
    /// is gated on Paid, while this document exists as soon as the invoice does and states what is to be
    /// paid, to whom, and with which reference. A club treasurer needs that on paper (or as a PDF) to
    /// get the payment through their own bookkeeping — which is why clubs and kretsar asked for it.
    ///
    /// Routed MVC controller (no Umbraco node), same pattern as ReceiptController: chromeless view,
    /// typed model, owner-or-payer-or-organiser gated.
    /// </summary>
    [Route("faktura")]
    public class InvoiceDocumentController : Controller
    {
        private readonly ReceiptModelBuilder _builder;
        private readonly IContentService _contentService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _auth;
        private readonly ConsolidatedInvoiceService _consolidated;

        public InvoiceDocumentController(
            ReceiptModelBuilder builder,
            IContentService contentService,
            IMemberManager memberManager,
            AdminAuthorizationService auth,
            ConsolidatedInvoiceService consolidated)
        {
            _builder = builder;
            _contentService = contentService;
            _memberManager = memberManager;
            _auth = auth;
            _consolidated = consolidated;
        }

        [HttpGet("{invoiceId:int}")]
        public async Task<IActionResult> Index(int invoiceId)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null)
                return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/faktura/{invoiceId}")}");

            var model = _builder.BuildInvoice(invoiceId);
            if (model == null || !model.Found)
                return NotFound();

            // Money comes from the one place that derives it (issued total − credit notes), so this
            // document can never disagree with the payment QR about what is left to pay.
            var balance = _consolidated.GetBalance(invoiceId);
            model.IssuedTotal = balance.Total;
            model.Credited = balance.Credited;
            model.AmountDue = balance.AmountDue;

            // Bankgiro QR for the printed invoice — the payer scans it in their own bank app, which is
            // where this is most useful: a paper invoice you can pay without retyping account, amount
            // and reference. Skipped when there is nothing left to pay.
            if (model.IssuerHasBgNumber && model.AmountDue > 0m
                && BankgiroQrCodeGenerator.IsValidBankgiro(model.IssuerBgNumber))
            {
                try
                {
                    var payee = _consolidated.ResolvePayee(model.CompetitionId);
                    model.BgQrCodeBase64 = Convert.ToBase64String(BankgiroQrCodeGenerator.GeneratePng(
                        string.IsNullOrWhiteSpace(model.IssuerName) ? payee.Name : model.IssuerName,
                        model.IssuerBgNumber,
                        model.AmountDue,
                        model.PaymentReference,
                        payeeOrgNumber: model.IssuerOrgNumber,
                        invoiceDate: model.IssuedAt));
                }
                catch
                {
                    // Convenience only: the bankgiro, amount and reference are printed as text anyway.
                }
            }

            // Three ways to be entitled to see it: you are billed for it (member, the member's club, or
            // a team's club — CanClaimPaymentForInvoice), you are the club a samlingsfaktura was issued
            // to, or you are the organiser being paid.
            var entitled = await _auth.CanClaimPaymentForInvoice(invoiceId)
                           || await IsPayingClubAdmin(invoiceId)
                           || await _auth.CanManageCompetitionInvoice(invoiceId);
            if (!entitled)
            {
                // Not Forbid(): that redirects to /Account/AccessDenied, which isn't an Umbraco
                // document, so the visitor would get a raw 404 quoting an internal path.
                model.AccessDenied = true;
            }

            return View("~/Views/InvoiceDocument.cshtml", model);
        }

        /// <summary>
        /// The club a samlingsfaktura was issued to — routinely NOT staff for the competition, since a
        /// club often pays invoices on another club's or the krets's competition.
        /// </summary>
        private async Task<bool> IsPayingClubAdmin(int invoiceId)
        {
            var payerClubId = _consolidated.ReadPayerClubId(invoiceId);
            return payerClubId > 0 && await _auth.IsClubAdminForClub(payerClubId);
        }
    }
}
