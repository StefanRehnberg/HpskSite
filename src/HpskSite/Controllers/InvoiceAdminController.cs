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
using HpskSite.Models;
using HpskSite.Models.ViewModels;
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
        private readonly InvoiceAuditService _auditService;
        private readonly EmailService _emailService;
        private readonly ClubService _clubService;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly AppCaches _appCaches;

        // Cache configuration
        private const string InvoicesListCacheKey = "admin_invoices_{0}_{1}_{2}_{3}_{4}_{5}"; // competitionId, clubId, excludePaid, activeOnly, page, viewType
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
            InvoiceAuditService auditService,
            EmailService emailService,
            ClubService clubService,
            IContentService contentService,
            IMemberService memberService,
            IMemberManager memberManager)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _authService = authService;
            _invoiceService = invoiceService;
            _paymentService = paymentService;
            _auditService = auditService;
            _emailService = emailService;
            _clubService = clubService;
            _contentService = contentService;
            _memberService = memberService;
            _memberManager = memberManager;
            _umbracoContextAccessor = umbracoContextAccessor;
            _appCaches = appCaches;
        }

        /// <summary>
        /// Resolves the current logged-in member to (id, name) for stamping audit rows.
        /// Returns (null, null) when no member is logged in or the lookup fails.
        /// </summary>
        private async Task<(int? id, string? name)> GetCurrentActorAsync()
        {
            try
            {
                var current = await _memberManager.GetCurrentMemberAsync();
                if (current == null) return (null, null);
                var data = _memberService.GetByEmail(current.Email ?? string.Empty);
                if (data == null) return (null, current.Name);
                return (data.Id, data.Name ?? current.Name);
            }
            catch
            {
                return (null, null);
            }
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
            string? region = null,
            string? paymentStatus = null,
            string? memberSearch = null,
            string? invoiceNumberSearch = null,
            bool activeCompetitionsOnly = true,
            bool excludePaid = true,
            int page = 1,
            int pageSize = 50,
            string? viewType = null)
        {
            // Authorization: Site admin OR club admin for specified club OR regional admin
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            bool isClubAdmin = clubId.HasValue && await _authService.IsClubAdminForClub(clubId.Value);
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isClubAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                // Check cache first (only for simple queries without text search)
                string? cacheKey = null;
                if (string.IsNullOrEmpty(memberSearch) && string.IsNullOrEmpty(invoiceNumberSearch) && string.IsNullOrEmpty(paymentStatus))
                {
                    cacheKey = string.Format(InvoicesListCacheKey, competitionId ?? 0, clubId ?? 0, excludePaid, activeCompetitionsOnly, page, viewType ?? "");
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
                    Region = region,
                    PaymentStatus = paymentStatus,
                    MemberSearch = memberSearch,
                    InvoiceNumberSearch = invoiceNumberSearch,
                    ActiveCompetitionsOnly = activeCompetitionsOnly,
                    ExcludePaid = excludePaid,
                    Page = page,
                    PageSize = pageSize,
                    ViewType = viewType
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
        /// Optionally filtered by region
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetActiveCompetitionsForFilter(string? region = null)
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var umbracoContext = _umbracoContextAccessor.GetRequiredUmbracoContext();

                // Build a lookup of club regions if filtering by region
                Dictionary<int, string>? clubRegions = null;
                if (!string.IsNullOrEmpty(region))
                {
                    clubRegions = new Dictionary<int, string>();
                    var clubs = umbracoContext.Content.GetAtRoot()
                        .SelectMany(root => root.DescendantsOfType("club"))
                        .ToList();
                    foreach (var club in clubs)
                    {
                        var clubRegion = club.Value<string>("regionalFederation") ?? "";
                        clubRegions[club.Id] = clubRegion;
                    }
                }

                var competitionsQuery = umbracoContext.Content.GetAtRoot()
                    .SelectMany(root => root.DescendantsOfType("competition"))
                    .Where(comp => comp.Value<bool>("isActive"));

                // Filter by region if specified
                if (!string.IsNullOrEmpty(region) && clubRegions != null)
                {
                    competitionsQuery = competitionsQuery.Where(comp =>
                    {
                        var clubId = comp.Value<int>("clubId");
                        return clubId > 0 && clubRegions.TryGetValue(clubId, out var clubRegion) && clubRegion == region;
                    });
                }

                var competitions = competitionsQuery
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
            if (!await _authService.CanManageCompetitionInvoice(request.InvoiceId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var (actorId, actorName) = await GetCurrentActorAsync();

                // Update invoice status using PaymentService (logs MarkedPaid audit event)
                await _paymentService.UpdatePaymentStatusAsync(
                    invoiceId: request.InvoiceId,
                    paymentStatus: "Paid",
                    paymentDate: DateTime.Now,
                    transactionId: null,
                    notes: "Marked as paid by admin",
                    paymentMethod: null,
                    actorMemberId: actorId,
                    actorMemberName: actorName
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
            if (!await _authService.CanManageCompetitionInvoice(request.InvoiceId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var (actorId, actorName) = await GetCurrentActorAsync();

                // Update invoice status to Cancelled (logs Cancelled audit event)
                await _paymentService.UpdatePaymentStatusAsync(
                    invoiceId: request.InvoiceId,
                    paymentStatus: "Cancelled",
                    paymentDate: null,
                    transactionId: null,
                    notes: "Cancelled by admin",
                    paymentMethod: null,
                    actorMemberId: actorId,
                    actorMemberName: actorName
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
            if (!await _authService.CanManageCompetitionInvoice(request.InvoiceId))
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

                // Audit: log who resent the QR email so the per-invoice history shows it.
                var (actorId, actorName) = await GetCurrentActorAsync();
                await _auditService.LogAsync(
                    invoiceId: request.InvoiceId,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.EmailSent,
                    byMemberId: actorId,
                    byMemberName: actorName,
                    amount: totalAmount,
                    reference: invoiceNumber,
                    notes: $"QR-faktura mejlad till {memberEmail}");

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
            // Same four-tier auth as the rest of the per-competition surface — was
            // site-admin only, breaking the cashier's "show QR for this specific
            // invoice" flow when used by a club admin.
            if (!await _authService.CanManageCompetitionInvoice(invoiceId))
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
        /// Get the audit history for one invoice (newest first). Returns events suitable
        /// for the "Visa historik" modal: type, when, who, method, amount, reference, notes.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetInvoiceHistory(int invoiceId)
        {
            if (!await _authService.CanManageCompetitionInvoice(invoiceId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            var events = await _auditService.GetForInvoiceAsync(invoiceId);
            return Json(new
            {
                success = true,
                events = events.Select(e => new
                {
                    eventType = e.EventType,
                    occurredAt = e.OccurredAt,
                    byMemberName = e.ByMemberName,
                    paymentMethod = e.PaymentMethod,
                    amount = e.Amount,
                    reference = e.Reference,
                    notes = e.Notes
                }).ToList()
            });
        }

        /// <summary>
        /// Bulk action: email a Swish QR-code reminder to every shooter on a competition
        /// who is currently in Pending status. Each individual send produces one EmailSent
        /// audit row tagged with a "bulk reminder" note so the per-invoice history shows
        /// when reminders went out, and a competition-wide query can count reminders sent.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendPaymentReminders(int competitionId, string? reminderMessage = null)
        {
            if (competitionId <= 0)
                return Json(new { success = false, message = "competitionId is required" });

            // Same four-tier rule as the rest of the per-competition surface.
            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Competition not found" });

            bool authorized = await _authService.IsCurrentUserAdminAsync()
                || await _authService.IsCompetitionManager(competitionId);
            if (!authorized)
            {
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    authorized = await _authService.IsClubAdminForClub(competitionClubId)
                              || await _authService.IsSkjutledareForClub(competitionClubId);
                }
            }
            if (!authorized) return Json(new { success = false, message = "Access denied" });

            var swishNumber = competition.GetValue<string>("swishNumber");
            if (string.IsNullOrEmpty(swishNumber))
                return Json(new { success = false, message = "Tävlingen saknar Swish-nummer." });

            var competitionChildren = _contentService.GetPagedChildren(competitionId, 0, 100, out _).ToList();
            var invoicesHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (invoicesHub == null)
                return Json(new { success = true, sentCount = 0, skippedCount = 0, message = "Inga fakturor finns." });

            var pendingInvoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                .Where(x => x.ContentType.Alias == "registrationInvoice")
                .Where(x => x.GetValue<string>("paymentStatus") == "Pending")
                .ToList();

            var (actorId, actorName) = await GetCurrentActorAsync();
            var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
            var competitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";
            var sentCount = 0;
            var skippedCount = 0;
            var errors = new List<string>();

            foreach (var invoice in pendingInvoices)
            {
                try
                {
                    var memberIdString = invoice.GetValue<string>("memberId");
                    // Skip team invoices ("team-{id}") — bulk reminder targets individual shooters.
                    // Team payments are typically handled by club treasurers separately.
                    if (memberIdString != null && memberIdString.StartsWith("team-"))
                    {
                        skippedCount++;
                        continue;
                    }
                    if (!int.TryParse(memberIdString, out int memberId))
                    {
                        skippedCount++;
                        continue;
                    }

                    var member = _memberService.GetById(memberId);
                    if (member == null || string.IsNullOrEmpty(member.Email))
                    {
                        skippedCount++;
                        continue;
                    }

                    var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? "";
                    var totalAmount = invoice.GetValue<decimal>("totalAmount");
                    var qrMessage = $"Betalning: {invoiceNumber}";
                    var qrBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, totalAmount.ToString("F2"), qrMessage);

                    var emailMessage = string.IsNullOrWhiteSpace(reminderMessage)
                        ? "Påminnelse: din anmälan är inte betald. Använd QR-koden nedan för att betala via Swish."
                        : reminderMessage;

                    await _emailService.SendSwishQRCodeEmailAsync(
                        member.Email,
                        member.Name ?? "",
                        competitionName,
                        qrBytes,
                        totalAmount,
                        "",  // shootingClasses
                        invoiceNumber,
                        swishNumber,
                        emailMessage);

                    await _auditService.LogAsync(
                        invoiceId: invoice.Id,
                        competitionId: competitionId,
                        eventType: InvoicePaymentEventTypes.EmailSent,
                        byMemberId: actorId,
                        byMemberName: actorName,
                        amount: totalAmount,
                        reference: invoiceNumber,
                        notes: $"Bulk-påminnelse skickad till {member.Email}");

                    sentCount++;
                }
                catch (Exception ex)
                {
                    skippedCount++;
                    errors.Add($"Faktura {invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString()}: {ex.Message}");
                }
            }

            return Json(new
            {
                success = true,
                sentCount,
                skippedCount,
                errorCount = errors.Count,
                errors = errors.Take(10).ToList(),  // cap error list so the response stays reasonable
                message = $"Påminnelser skickade: {sentCount}. Hoppade över: {skippedCount}."
            });
        }

        /// <summary>
        /// Count of pending-payment recipients eligible for a bulk reminder. Used by the
        /// confirmation modal to tell the operator "you're about to email N shooters".
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> CountReminderRecipients(int competitionId)
        {
            if (competitionId <= 0)
                return Json(new { success = false, message = "competitionId is required" });

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Competition not found" });

            bool authorized = await _authService.IsCurrentUserAdminAsync()
                || await _authService.IsCompetitionManager(competitionId);
            if (!authorized)
            {
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    authorized = await _authService.IsClubAdminForClub(competitionClubId)
                              || await _authService.IsSkjutledareForClub(competitionClubId);
                }
            }
            if (!authorized) return Json(new { success = false, message = "Access denied" });

            var competitionChildren = _contentService.GetPagedChildren(competitionId, 0, 100, out _).ToList();
            var invoicesHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (invoicesHub == null) return Json(new { success = true, count = 0 });

            var pending = _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                .Where(x => x.ContentType.Alias == "registrationInvoice")
                .Where(x => x.GetValue<string>("paymentStatus") == "Pending")
                .Where(x =>
                {
                    // Only count individual invoices with a resolvable email.
                    var memberIdString = x.GetValue<string>("memberId");
                    if (memberIdString == null || memberIdString.StartsWith("team-")) return false;
                    if (!int.TryParse(memberIdString, out int memberId)) return false;
                    var member = _memberService.GetById(memberId);
                    return member != null && !string.IsNullOrEmpty(member.Email);
                })
                .Count();

            return Json(new { success = true, count = pending });
        }

        /// <summary>
        /// Print-ready accounting summary (Bokföringsunderlag) for a single competition.
        /// Renders a Razor view rather than JSON — the page itself is the deliverable; the
        /// operator hits Ctrl+P to produce the PDF/paper artefact for the bookkeeper.
        /// Same four-tier auth as the rest of the per-competition surface.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> BokforingsUnderlag(int competitionId, bool includeOutstanding = true)
        {
            if (competitionId <= 0) return BadRequest("competitionId is required");

            var competition = _contentService.GetById(competitionId);
            if (competition == null) return NotFound("Competition not found");

            bool authorized = await _authService.IsCurrentUserAdminAsync()
                || await _authService.IsCompetitionManager(competitionId);
            if (!authorized)
            {
                var competitionClubId = competition.GetValue<int>("clubId");
                if (competitionClubId > 0)
                {
                    authorized = await _authService.IsClubAdminForClub(competitionClubId)
                              || await _authService.IsSkjutledareForClub(competitionClubId);
                }
            }
            if (!authorized) return Forbid();

            var competitionChildren = _contentService.GetPagedChildren(competitionId, 0, 100, out _).ToList();
            var registrationsHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "competitionRegistrationsHub");
            var invoicesHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");

            var registrations = registrationsHub != null
                ? _contentService.GetPagedChildren(registrationsHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "competitionRegistration")
                    .ToList()
                : new List<IContent>();

            var invoices = invoicesHub != null
                ? _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "registrationInvoice")
                    .OrderBy(c => c.GetValue<DateTime?>("paymentDate") ?? c.GetValue<DateTime?>("createdDate") ?? DateTime.MinValue)
                    .ToList()
                : new List<IContent>();

            // Pull operator info for the footer
            var (actorId, actorName) = await GetCurrentActorAsync();

            // Build a registration → club lookup so each paid row can show the shooter's
            // club without hitting ClubService once per invoice. ClubService caches names
            // internally so we still let it own the lookup.
            var regClubByMemberId = new Dictionary<string, string?>();
            foreach (var reg in registrations)
            {
                var memberIdStr = reg.GetValue<string>("memberId");
                if (string.IsNullOrEmpty(memberIdStr)) continue;
                if (regClubByMemberId.ContainsKey(memberIdStr)) continue;

                var clubId = reg.GetValue<int>("clubId");
                string? clubName = clubId > 0 ? _clubService.GetClubNameById(clubId) : null;
                if (string.IsNullOrEmpty(clubName))
                {
                    var legacy = reg.GetValue<string>("memberClub");
                    if (!string.IsNullOrEmpty(legacy)) clubName = legacy;
                }
                regClubByMemberId[memberIdStr] = clubName;
            }

            // Strip any JSON-array wrapping that the paymentStatus property occasionally
            // arrives with (legacy data was stored as ["Paid"] in some places).
            static string CleanStatus(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "Pending";
                return s.Trim().Trim('[', ']').Trim('"', '\'').Trim();
            }

            BokforingsTransactionRow ToRow(IContent inv)
            {
                var memberIdStr = inv.GetValue<string>("memberId") ?? "";
                regClubByMemberId.TryGetValue(memberIdStr, out var clubName);
                return new BokforingsTransactionRow
                {
                    InvoiceId = inv.Id,
                    InvoiceNumber = inv.GetValue<string>("invoiceNumber") ?? "",
                    MemberName = inv.GetValue<string>("memberName") ?? "",
                    ClubName = clubName,
                    Amount = inv.GetValue<decimal>("totalAmount"),
                    ActualAmount = inv.GetValue<decimal?>("actualPaidAmount"),
                    PaymentStatus = CleanStatus(inv.GetValue<string>("paymentStatus") ?? "Pending"),
                    PaymentMethod = inv.GetValue<string>("paymentMethod"),
                    PaymentDate = inv.GetValue<DateTime?>("paymentDate"),
                    CreatedDate = inv.GetValue<DateTime?>("createdDate"),
                    TransactionId = inv.GetValue<string>("transactionId"),
                    Notes = inv.GetValue<string>("notes")
                };
            }

            var allRows = invoices.Select(ToRow).ToList();

            var paidRows       = allRows.Where(r => r.PaymentStatus == "Paid").OrderBy(r => r.PaymentDate).ToList();
            var outstandingRows = allRows.Where(r => r.PaymentStatus == "Pending").OrderBy(r => r.MemberName).ToList();
            var cancelledRows  = allRows.Where(r => r.PaymentStatus == "Cancelled").OrderBy(r => r.MemberName).ToList();
            var refundedRows   = allRows.Where(r => r.PaymentStatus == "Refunded").OrderBy(r => r.PaymentDate).ToList();

            var summary = new BokforingsSummary
            {
                TotalRegistrations = registrations.Count,
                PaidCount = paidRows.Count,
                PendingCount = outstandingRows.Count,
                NoInvoiceCount = Math.Max(0, registrations.Count - allRows.Count(r => r.PaymentStatus != "Cancelled")),
                CancelledCount = cancelledRows.Count,
                RefundedCount = refundedRows.Count,
                PaidTotal = paidRows.Sum(r => r.RecordedAmount),
                PendingTotal = outstandingRows.Sum(r => r.Amount),
                RefundedTotal = refundedRows.Sum(r => r.RecordedAmount),
                PaidByMethod = paidRows
                    .GroupBy(r => string.IsNullOrEmpty(r.PaymentMethod) ? "Okänd" : r.PaymentMethod!)
                    .ToDictionary(g => g.Key, g => g.Sum(r => r.RecordedAmount)),
                PaidCountByMethod = paidRows
                    .GroupBy(r => string.IsNullOrEmpty(r.PaymentMethod) ? "Okänd" : r.PaymentMethod!)
                    .ToDictionary(g => g.Key, g => g.Count())
            };

            var organizerClubId = competition.GetValue<int>("clubId");
            string? organizerName = organizerClubId > 0
                ? _clubService.GetClubNameById(organizerClubId)
                : null;

            // For final bookkeeping verification the operator only wants what's actually
            // been collected — outstanding (Pending / No Invoice) rows would muddy the
            // verifikat. When includeOutstanding is false, drop the section entirely so
            // the printout doesn't even mention pending amounts.
            var model = new BokforingsUnderlagViewModel
            {
                CompetitionId = competitionId,
                CompetitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "",
                CompetitionDate = competition.GetValue<DateTime?>("competitionDate"),
                CompetitionEndDate = competition.GetValue<DateTime?>("competitionEndDate"),
                Venue = competition.GetValue<string>("venue"),
                Organizer = organizerName,
                Scope = competition.GetValue<string>("competitionScope"),
                GeneratedAt = DateTime.Now,
                GeneratedBy = actorName,
                IncludeOutstanding = includeOutstanding,
                Summary = summary,
                PaidTransactions = paidRows,
                OutstandingTransactions = includeOutstanding ? outstandingRows : new List<BokforingsTransactionRow>(),
                CancelledTransactions = cancelledRows,
                RefundedTransactions = refundedRows
            };

            return View("BokforingsUnderlag", model);
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
