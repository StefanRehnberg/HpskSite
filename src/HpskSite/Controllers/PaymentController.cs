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
        private readonly EmailService _emailService;
        private readonly ClubService _clubService;
        private readonly InvoiceAuditService _auditService;
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
            EmailService emailService,
            ClubService clubService,
            InvoiceAuditService auditService,
            ILogger<PaymentController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _paymentService = paymentService;
            _authService = authService;
            _emailService = emailService;
            _clubService = clubService;
            _auditService = auditService;
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
            bool sendReceipt = false)
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
                    invoiceId, paymentStatus, paymentDate, transactionId, notes, paymentMethod, actorId, actorName, actualAmount);

                if (!success)
                {
                    return Json(new { success = false, message = "Ett fel uppstod vid uppdatering av betalningsstatus." });
                }

                // Item #8: optional email receipt. Best-effort — failure logs but doesn't
                // bubble up; the cashier already saw "betald" and the audit row will reflect
                // whether the receipt actually went out.
                bool receiptSent = false;
                string? receiptError = null;
                if (sendReceipt && paymentStatus == "Paid")
                {
                    (receiptSent, receiptError) = await TrySendReceiptAsync(invoiceId, actorId, actorName);
                }

                return Json(new {
                    success = true,
                    message = "Betalningsstatus uppdaterad.",
                    receiptRequested = sendReceipt,
                    receiptSent,
                    receiptError
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating payment status for invoice {InvoiceId}", invoiceId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Build the receipt context and email it to the shooter. All lookups are best
        /// effort — a missing field degrades gracefully (e.g. classes left blank when the
        /// invoice's registration cannot be resolved) rather than blocking the email.
        /// Logs an InvoicePaymentEvents row of type ReceiptSent so the audit trail
        /// reflects what was actually emailed.
        /// </summary>
        private async Task<(bool sent, string? error)> TrySendReceiptAsync(
            int invoiceId, int? actorId, string? actorName)
        {
            try
            {
                var invoice = _contentService.GetById(invoiceId);
                if (invoice == null) return (false, "Faktura hittades inte.");

                var memberIdStr = invoice.GetValue<string>("memberId") ?? "";
                if (!int.TryParse(memberIdStr, out var memberId) || memberId <= 0)
                    return (false, "Skytt saknas på fakturan.");

                var member = _memberService.GetById(memberId);
                var memberEmail = member?.Email;
                if (string.IsNullOrWhiteSpace(memberEmail))
                    return (false, "Skytten saknar e-postadress.");

                var memberName = invoice.GetValue<string>("memberName") ?? member?.Name ?? "";
                var billed = invoice.GetValue<decimal>("totalAmount");
                var actual = invoice.GetValue<decimal?>("actualPaidAmount") ?? billed;
                var paymentMethod = invoice.GetValue<string>("paymentMethod") ?? "";
                var transactionId = invoice.GetValue<string>("transactionId") ?? "";
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? "";
                var paidAt = invoice.GetValue<DateTime?>("paymentDate") ?? DateTime.Now;

                var competitionId = invoice.GetValue<int>("competitionId");
                var competition = competitionId > 0 ? _contentService.GetById(competitionId) : null;
                var competitionName = competition?.GetValue<string>("competitionName") ?? competition?.Name ?? "";

                var organizerClubId = competition?.GetValue<int>("clubId") ?? 0;
                var organizerName = organizerClubId > 0 ? (_clubService.GetClubNameById(organizerClubId) ?? "") : "";

                // Try to resolve the linked registration to list class names. The invoice's
                // single-reg property is preferred; fall back to the legacy CSV/JSON property
                // for older multi-reg invoices.
                var classes = "";
                var registrationId = invoice.GetValue<int>("registrationId");
                if (registrationId > 0)
                {
                    var reg = _contentService.GetById(registrationId);
                    if (reg != null)
                    {
                        var json = reg.GetValue<string>("shootingClasses") ?? "";
                        var entries = HpskSite.Models.CompetitionRegistrationDocument.DeserializeShootingClasses(json);
                        classes = string.Join(", ", entries.Select(e => e.Class).Where(c => !string.IsNullOrEmpty(c)));
                    }
                }

                await _emailService.SendPaymentReceiptAsync(
                    memberEmail: memberEmail,
                    memberName: memberName,
                    competitionName: competitionName,
                    organizerName: organizerName,
                    paidAt: paidAt,
                    shootingClasses: classes,
                    billedAmount: billed,
                    actualAmount: actual,
                    paymentMethod: paymentMethod,
                    reference: transactionId,
                    invoiceNumber: invoiceNumber);

                await _auditService.LogAsync(
                    invoiceId: invoiceId,
                    competitionId: competitionId,
                    eventType: HpskSite.Models.InvoicePaymentEventTypes.ReceiptSent,
                    byMemberId: actorId,
                    byMemberName: actorName,
                    paymentMethod: paymentMethod,
                    amount: actual,
                    reference: $"Email: {memberEmail}",
                    notes: null);

                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to send payment receipt for invoice {InvoiceId}", invoiceId);
                return (false, ex.Message);
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





