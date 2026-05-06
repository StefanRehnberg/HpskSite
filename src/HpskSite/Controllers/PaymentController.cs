using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;
using Microsoft.Extensions.Logging;

namespace HpskSite.Controllers
{
    public class PaymentController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly PaymentService _paymentService;
        private readonly AdminAuthorizationService _authService;
        private readonly ILogger<PaymentController> _logger;

        public PaymentController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            PaymentService paymentService,
            AdminAuthorizationService authService,
            ILogger<PaymentController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _paymentService = paymentService;
            _authService = authService;
            _logger = logger;
        }

        /// <summary>
        /// Create an invoice for a registration
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateInvoice(
            int competitionId,
            int registrationId,
            string paymentMethod = "Swish")
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Validate that the registration belongs to the current user
                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata kunde inte hittas." });
                }

                // Verify ownership of registration
                var registration = _contentService.GetById(registrationId);
                if (registration == null || registration.GetValue<string>("memberId") != currentMember.Id.ToString())
                {
                    return Json(new { success = false, message = "Du kan bara skapa fakturor för dina egna anmälningar." });
                }

                // Calculate total amount based on number of classes
                var totalAmount = _paymentService.CalculateRegistrationTotal(competitionId, registrationId);
                if (totalAmount <= 0)
                {
                    return Json(new { success = false, message = "Kunde inte beräkna totalbelopp." });
                }

                // Create the invoice
                var invoice = await _paymentService.CreateInvoiceAsync(
                    competitionId,
                    currentMember.Id.ToString(),
                    currentMember.Name ?? "Okänd medlem",
                    registrationId,
                    totalAmount,
                    paymentMethod);

                if (invoice != null)
                {
                    return Json(new {
                        success = true,
                        message = $"Faktura skapad för {totalAmount:C}",
                        invoiceId = invoice.Id,
                        amount = totalAmount
                    });
                }
                else
                {
                    return Json(new { success = false, message = "Ett fel uppstod vid skapandet av fakturan." });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating invoice for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Update payment status for an invoice
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePaymentStatus(
            int invoiceId,
            string paymentStatus,
            DateTime? paymentDate = null,
            string? transactionId = null,
            string? notes = null,
            string? paymentMethod = null,
            decimal? actualAmount = null,
            // Receipt is opt-out: every Paid transition tries to email the shooter unless
            // explicitly suppressed (cash desk where the operator doesn't want to spam an
            // address). Members without an email still no-op silently — the audit log just
            // never gets a ReceiptSent row.
            bool sendReceipt = true)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Authorization: site admin OR competition manager OR club admin OR skjutledare
                // for the invoice's competition's club OR the invoice's owner.
                bool canManage = await _authService.CanManageCompetitionInvoice(invoiceId);
                if (!canManage)
                {
                    var umbracoContext = UmbracoContext;
                    var invoice = umbracoContext.Content.GetById(invoiceId);
                    bool isOwner = invoice?.Value<string>("memberId") == currentMember.Id.ToString();
                    if (!isOwner)
                    {
                        return Json(new { success = false, message = "Du har inte behörighet att uppdatera denna faktura." });
                    }
                }

                // Pass actor info so PaymentService can stamp the audit row with who did it.
                int? actorId = null;
                string? actorName = null;
                var memberData = _memberService.GetByEmail(currentMember.Email ?? string.Empty);
                if (memberData != null)
                {
                    actorId = memberData.Id;
                    actorName = memberData.Name ?? currentMember.Name;
                }

                var success = await _paymentService.UpdatePaymentStatusAsync(
                    invoiceId, paymentStatus, paymentDate, transactionId, notes, paymentMethod,
                    actorId, actorName, actualAmount, sendReceiptOnPaid: sendReceipt);

                if (!success)
                {
                    return Json(new { success = false, message = "Ett fel uppstod vid uppdatering av betalningsstatus." });
                }

                // Receipt-send happens inside PaymentService now (so InvoiceAdmin's mark-paid
                // path benefits from the same behaviour). The cashier UI doesn't need a
                // success/failure echo — failures land in the application log and the audit
                // row's absence (no ReceiptSent event) tells the operator after the fact.
                return Json(new {
                    success = true,
                    message = "Betalningsstatus uppdaterad.",
                    receiptRequested = sendReceipt
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating payment status for invoice {InvoiceId}", invoiceId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get payment status for a registration
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRegistrationPaymentStatus(int registrationId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var umbracoContext = UmbracoContext;
                var registration = umbracoContext.Content.GetById(registrationId);
                
                if (registration?.Value<string>("memberId") != currentMember.Id.ToString())
                {
                    return Json(new { success = false, message = "Du kan bara se betalningsstatus för dina egna anmälningar." });
                }

                var paymentStatus = _paymentService.GetRegistrationPaymentStatus(registrationId);
                
                return Json(new { 
                    success = true, 
                    paymentStatus = paymentStatus,
                    displayText = GetPaymentStatusDisplay(paymentStatus),
                    colorClass = GetPaymentStatusColorClass(paymentStatus)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting payment status for registration {RegistrationId}", registrationId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get all invoices for the current user
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUserInvoices()
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var invoices = _paymentService.GetMemberInvoices(currentMember.Id.ToString());
                
                var invoiceData = invoices.Select(invoice => new
                {
                    id = invoice.Id,
                    competitionId = invoice.CompetitionId,
                    amount = invoice.TotalAmount,
                    paymentStatus = invoice.PaymentStatus,
                    paymentStatusDisplay = invoice.GetPaymentStatusDisplay(),
                    paymentStatusColorClass = invoice.GetPaymentStatusColorClass(),
                    paymentMethod = invoice.GetPaymentMethodDisplay(),
                    paymentDate = invoice.PaymentDate,
                    createdDate = invoice.CreatedDate,
                    notes = invoice.Notes
                }).ToList();

                return Json(new { success = true, invoices = invoiceData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting user invoices");
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        private string GetPaymentStatusDisplay(string status)
        {
            return status switch
            {
                "No Invoice" => "Ingen faktura",
                "Pending" => "Väntar på betalning",
                "Paid" => "Betald",
                "Failed" => "Betalning misslyckades",
                "Refunded" => "Återbetalad",
                "Cancelled" => "Makulerad",
                "Unknown" => "Okänd status",
                _ => "Okänd status"
            };
        }

        private string GetPaymentStatusColorClass(string status)
        {
            return status switch
            {
                "No Invoice" => "secondary",
                "Pending" => "warning",
                "Paid" => "success",
                "Failed" => "danger",
                "Refunded" => "info",
                "Cancelled" => "secondary",
                "Unknown" => "secondary",
                _ => "secondary"
            };
        }
    }
}





