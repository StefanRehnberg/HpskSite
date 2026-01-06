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
            ILogger<PaymentController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _paymentService = paymentService;
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
            string? notes = null)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Check if user has permission to update this invoice
                var memberData = _memberService.GetById(currentMember.Key);
                var roles = _memberService.GetAllRoles(memberData.Id);
                var rolesList = roles?.ToList() ?? new List<string>();
                bool isSiteAdmin = rolesList.Any(r => r.Equals("Administrators", StringComparison.OrdinalIgnoreCase));

                if (!isSiteAdmin)
                {
                    // Check if this invoice belongs to the current user
                    var umbracoContext = UmbracoContext;
                    var invoice = umbracoContext.Content.GetById(invoiceId);
                    if (invoice?.Value<string>("memberId") != currentMember.Id.ToString())
                    {
                        return Json(new { success = false, message = "Du har inte behörighet att uppdatera denna faktura." });
                    }
                }

                var success = await _paymentService.UpdatePaymentStatusAsync(
                    invoiceId, paymentStatus, paymentDate, transactionId, notes);

                if (success)
                {
                    return Json(new { success = true, message = "Betalningsstatus uppdaterad." });
                }
                else
                {
                    return Json(new { success = false, message = "Ett fel uppstod vid uppdatering av betalningsstatus." });
                }
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





