using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using HpskSite.Services;
using HpskSite.Models;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Handles all invoice management operations for administrators
    /// Provides endpoints for viewing, filtering, and managing invoices across competitions
    /// </summary>
    public class InvoiceAdminController : SurfaceController
    {
        private readonly AdminAuthorizationService _authService;
        private readonly InvoiceAdminService _invoiceService;
        private readonly PaymentService _paymentService;
        private readonly EmailService _emailService;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AppCaches _appCaches;

        // Cache configuration
        private const string InvoicesListCacheKey = "admin_invoices_{0}_{1}_{2}_{3}_{4}"; // competitionId, clubId, excludePaid, activeOnly, page
        private static readonly TimeSpan InvoiceCacheDuration = TimeSpan.FromMinutes(5);

        public InvoiceAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService authService,
            InvoiceAdminService invoiceService,
            PaymentService paymentService,
            EmailService emailService,
            IContentService contentService,
            IMemberService memberService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _authService = authService;
            _invoiceService = invoiceService;
            _paymentService = paymentService;
            _emailService = emailService;
            _contentService = contentService;
            _memberService = memberService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _appCaches = appCaches;
        }

        /// <summary>
        /// Get all invoices with optional filtering
        /// Main endpoint for invoice list display
        /// Supports both site-wide admin access and club-specific admin access
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetInvoices(
            int? competitionId = null,
            int? clubId = null,
            string? paymentStatus = null,
            string? memberSearch = null,
            string? invoiceNumberSearch = null,
            bool activeCompetitionsOnly = true,
            bool excludePaid = true,
            int page = 1,
            int pageSize = 50)
        {
            // Authorization: Site admin OR club admin for specified club
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            bool isClubAdmin = clubId.HasValue && await _authService.IsClubAdminForClub(clubId.Value);

            if (!isSiteAdmin && !isClubAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Check cache first (only for simple queries without text search)
                string? cacheKey = null;
                if (string.IsNullOrEmpty(memberSearch) && string.IsNullOrEmpty(invoiceNumberSearch) && string.IsNullOrEmpty(paymentStatus))
                {
                    cacheKey = string.Format(InvoicesListCacheKey, competitionId ?? 0, clubId ?? 0, excludePaid, activeCompetitionsOnly, page);
                    var cachedResult = _appCaches.RuntimeCache.Get(cacheKey);
                    if (cachedResult != null)
                    {
                        return Json(cachedResult);
                    }
                }

                // Build filter options
                var filters = new InvoiceFilterOptions
                {
                    CompetitionId = competitionId,
                    ClubId = clubId,
                    PaymentStatus = paymentStatus,
                    MemberSearch = memberSearch,
                    InvoiceNumberSearch = invoiceNumberSearch,
                    ActiveCompetitionsOnly = activeCompetitionsOnly,
                    ExcludePaid = excludePaid,
                    Page = page,
                    PageSize = pageSize
                };

                // Call service to aggregate and filter invoices
                var result = _invoiceService.GetAllInvoices(filters);

                // Cache the result if this was a cacheable query
                if (cacheKey != null && result.Success)
                {
                    _appCaches.RuntimeCache.Insert(cacheKey, () => result, InvoiceCacheDuration);
                }

                return Json(result);
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error loading invoices: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Get active competitions for filter dropdown
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetActiveCompetitionsForFilter()
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var competitions = umbracoContext.Content.GetAtRoot()
                    .SelectMany(root => root.DescendantsOfType("competition"))
                    .Where(comp => comp.Value<bool>("isActive"))
                    .OrderByDescending(comp => comp.Value<DateTime?>("competitionDate"))
                    .Select(comp => new
                    {
                        id = comp.Id,
                        name = comp.Name,
                        date = comp.Value<DateTime?>("competitionDate")?.ToString("yyyy-MM-dd")
                    })
                    .ToList();

                return Json(new { success = true, competitions });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error loading competitions: " + ex.Message });
            }
        }

        /// <summary>
        /// Mark invoice as paid
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> MarkAsPaid([FromBody] InvoiceActionRequest request)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Update invoice status using PaymentService
                await _paymentService.UpdatePaymentStatusAsync(
                    request.InvoiceId,
                    "Paid",
                    DateTime.Now,
                    null,  // transactionId
                    "Marked as paid by admin"
                );

                // Invalidate cache
                InvalidateInvoiceCaches();

                return Json(new
                {
                    success = true,
                    message = "Invoice marked as paid"
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error marking invoice as paid: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Cancel invoice
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CancelInvoice([FromBody] InvoiceActionRequest request)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Update invoice status to Cancelled
                await _paymentService.UpdatePaymentStatusAsync(
                    request.InvoiceId,
                    "Cancelled",
                    null,  // paymentDate
                    null,  // transactionId
                    "Cancelled by admin"
                );

                // Invalidate cache
                InvalidateInvoiceCaches();

                return Json(new
                {
                    success = true,
                    message = "Invoice cancelled"
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error cancelling invoice: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Resend invoice email with QR code
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ResendInvoiceEmail([FromBody] InvoiceActionRequest request)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get invoice details
                var invoice = _contentService.GetById(request.InvoiceId);
                if (invoice == null)
                {
                    return Json(new { success = false, message = "Invoice not found" });
                }

                // Get competition details
                var competitionId = invoice.GetValue<int>("competitionId");
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Competition not found" });
                }

                // Get member details
                var memberIdString = invoice.GetValue<string>("memberId");
                if (!int.TryParse(memberIdString, out int memberId))
                {
                    return Json(new { success = false, message = "Invalid member ID" });
                }

                var member = _memberService.GetById(memberId);
                if (member == null)
                {
                    return Json(new { success = false, message = "Member not found" });
                }

                var memberEmail = member.Email;
                if (string.IsNullOrEmpty(memberEmail))
                {
                    return Json(new { success = false, message = "Member has no email address" });
                }

                // Get Swish details from competition
                var swishNumber = competition.Value<string>("swishNumber");
                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Competition has no Swish number configured" });
                }

                // Generate QR code
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber");
                var totalAmount = invoice.GetValue<decimal>("totalAmount");
                var message = $"Betalning: {invoiceNumber}";

                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, totalAmount.ToString("F2"), message);

                // Send email
                await _emailService.SendSwishQRCodeEmailAsync(
                    memberEmail,
                    member.Name,
                    competition.Name,
                    qrCodeBytes,
                    totalAmount,
                    "",  // shootingClasses (not needed for invoice email)
                    invoiceNumber,
                    swishNumber,
                    "Faktura skickad av administratör"  // invoiceMessage
                );

                return Json(new
                {
                    success = true,
                    message = "Email sent successfully"
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error sending email: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Generate QR code for existing invoice (for display in modal)
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GenerateInvoiceQRCode(int invoiceId)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Get invoice details
                var invoice = _contentService.GetById(invoiceId);
                if (invoice == null)
                {
                    return Json(new { success = false, message = "Invoice not found" });
                }

                // Get competition details
                var competitionId = invoice.GetValue<int>("competitionId");
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();
                var competition = umbracoContext.Content.GetById(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Competition not found" });
                }

                // Get Swish details from competition
                var swishNumber = competition.Value<string>("swishNumber");
                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Competition has no Swish number configured" });
                }

                // Get invoice details
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber");
                var totalAmount = invoice.GetValue<decimal>("totalAmount");
                var message = $"Betalning: {invoiceNumber}";

                // Generate QR code
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, totalAmount.ToString("F2"), message);
                var qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);

                return Json(new
                {
                    success = true,
                    qrCodeBase64 = qrCodeBase64,
                    amount = totalAmount.ToString("F2"),
                    invoiceNumber = invoiceNumber,
                    competitionName = competition.Name
                });
            }
            catch (Exception ex)
            {
                return Json(new
                {
                    success = false,
                    message = "Error generating QR code: " + ex.Message
                });
            }
        }

        /// <summary>
        /// Invalidate all invoice caches
        /// Called after any status change on invoices
        /// </summary>
        private void InvalidateInvoiceCaches()
        {
            _appCaches.RuntimeCache.ClearByRegex("^admin_invoices_");
        }
    }

    /// <summary>
    /// Request model for invoice actions
    /// </summary>
    public class InvoiceActionRequest
    {
        public int InvoiceId { get; set; }
    }
}
