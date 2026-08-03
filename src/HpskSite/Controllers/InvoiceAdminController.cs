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
        private readonly ConsolidatedInvoiceService _consolidatedService;

        // Cache configuration
        // NB `region` MUST be part of the key: it's a filter on the result set, so leaving it out
        // served one krets's invoice list to another (and made the krets dropdown show stale data).
        private const string InvoicesListCacheKey = "admin_invoices_{0}_{1}_{2}_{3}_{4}_{5}_{6}"; // competitionId, clubId, excludePaid, activeOnly, page, viewType, region
        private static readonly TimeSpan InvoiceCacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The message used in a bulk payment reminder when the operator leaves the
        /// "Meddelande" field empty. Single source of truth — the modal prefills the
        /// textarea with this (via CountReminderRecipients) and SendPaymentReminders
        /// falls back to it server-side, so the two can never drift apart.
        /// </summary>
        public const string DefaultReminderMessage =
            "För att slutföra din anmälan, betala tävlingsavgiften med Swish genom att scanna QR-koden nedan:";

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
            IMemberManager memberManager,
            ConsolidatedInvoiceService consolidatedService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _consolidatedService = consolidatedService;
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
            string? viewType = null,
            bool regionOwnCompetitionsOnly = false)
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

            // A regional admin may only ever read their OWN krets. `region` arrives from the client,
            // so without this a Blekinge admin could simply ask for region=Halland. Club admins are
            // already scoped by clubId, and site admins may read any krets.
            if (!isSiteAdmin && !isClubAdmin && isRegionalAdmin)
            {
                if (string.IsNullOrWhiteSpace(region))
                {
                    region = managedRegions.First();
                }
                else if (!managedRegions.Any(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase)))
                {
                    return Json(new { success = false, message = "Access denied" });
                }
            }

            try
            {
                // Check cache first (only for simple queries without text search)
                string? cacheKey = null;
                if (string.IsNullOrEmpty(memberSearch) && string.IsNullOrEmpty(invoiceNumberSearch) && string.IsNullOrEmpty(paymentStatus))
                {
                    cacheKey = string.Format(InvoicesListCacheKey, competitionId ?? 0, clubId ?? 0, excludePaid, activeCompetitionsOnly, page, viewType ?? "", $"{region ?? ""}|{regionOwnCompetitionsOnly}");
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
                    ViewType = viewType,
                    RegionOwnCompetitionsOnly = regionOwnCompetitionsOnly
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
        /// Dry run: group the selected invoices per competition and say which are payable and why the
        /// rest are not. Read-only — nothing is written, so the UI can show a confirmation safely.
        /// </summary>
        // Antiforgery deliberately NOT ignored: every state-changing endpoint in this controller
        // (MarkAsPaid, CancelInvoice, …) requires the token, and these move money too.
        [HttpPost]
        public async Task<IActionResult> PreviewConsolidation([FromBody] ConsolidationRequest request)
        {
            if (request == null || request.InvoiceIds == null || request.InvoiceIds.Length == 0)
                return Json(new { success = false, message = "Inga fakturor valda." });

            if (!await CanPayForClubAsync(request.PayerClubId))
                return Json(new { success = false, message = "Du har inte behörighet att betala för den föreningen." });

            var preview = _consolidatedService.Preview(request.InvoiceIds);
            return Json(new
            {
                success = true,
                ready = preview.Ready,
                missingProperties = preview.MissingProperties,
                grandTotal = preview.GrandTotal,
                groups = preview.Groups.Select(g => new
                {
                    competitionId = g.CompetitionId,
                    competitionName = g.CompetitionName,
                    total = g.Total,
                    needsParent = g.NeedsParent,
                    invoices = g.Invoices.Select(i => new
                    {
                        invoiceId = i.InvoiceId, invoiceNumber = i.InvoiceNumber,
                        memberName = i.MemberName, amount = i.Amount
                    })
                }),
                rejected = preview.Rejected.Select(r => new
                {
                    invoiceId = r.InvoiceId, invoiceNumber = r.InvoiceNumber,
                    memberName = r.MemberName, reason = r.Reason
                })
            });
        }

        /// <summary>
        /// Create one parent invoice per competition for the selected invoices. Re-validates
        /// server-side — the client's list can be stale, and paying the wrong invoices is precisely
        /// the failure this must not allow.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateConsolidatedInvoices([FromBody] ConsolidationRequest request)
        {
            if (request == null || request.InvoiceIds == null || request.InvoiceIds.Length == 0)
                return Json(new { success = false, message = "Inga fakturor valda." });

            if (!await CanPayForClubAsync(request.PayerClubId))
                return Json(new { success = false, message = "Du har inte behörighet att betala för den föreningen." });

            var actor = await _memberManager.GetCurrentMemberAsync();
            var actorData = actor == null ? null : _memberService.GetByEmail(actor.Email ?? "");

            var (success, message, parents, rejected) = await _consolidatedService.CreateAsync(
                request.PayerClubId, request.InvoiceIds, actorData?.Id ?? 0);

            if (success) InvalidateInvoiceCaches();

            return Json(new
            {
                success,
                message,
                parents = parents.Select(p => new
                {
                    competitionId = p.CompetitionId,
                    competitionName = p.CompetitionName,
                    parentInvoiceId = p.ParentInvoiceId,
                    parentInvoiceNumber = p.ParentInvoiceNumber,
                    total = p.Total,
                    coveredCount = p.CoveredCount,
                    payDirectlyInvoiceId = p.PayDirectlyInvoiceId,
                    error = p.Error
                }),
                rejected = rejected.Select(r => new
                {
                    invoiceId = r.InvoiceId, invoiceNumber = r.InvoiceNumber,
                    memberName = r.MemberName, reason = r.Reason
                })
            });
        }

        /// <summary>
        /// Undo an unpaid samlingsfaktura. Allowed for the PAYING club as well as the organiser: the
        /// payer can consolidate invoices on another club's competition, and the organiser-scoped
        /// CancelInvoice would refuse them — leaving a club that ticked the wrong boxes stuck.
        /// Refused once Paid; that correction is a kreditfaktura.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CancelConsolidatedInvoice([FromBody] InvoiceActionRequest request)
        {
            if (request == null || request.InvoiceId <= 0)
                return Json(new { success = false, message = "Ingen faktura angiven." });

            var payerClubId = _consolidatedService.ReadPayerClubId(request.InvoiceId);
            var isPayer = payerClubId > 0 && await CanPayForClubAsync(payerClubId);
            var isOrganiser = await _authService.CanManageCompetitionInvoice(request.InvoiceId);
            if (!isPayer && !isOrganiser)
                return Json(new { success = false, message = "Du har inte behörighet att makulera den fakturan." });

            var (success, message, freed, _, status) = _consolidatedService.CancelUnpaidParent(request.InvoiceId);
            if (success)
            {
                InvalidateInvoiceCaches();
                var (actorId, actorName) = await GetCurrentActorAsync();
                _ = _auditService.LogAsync(
                    invoiceId: request.InvoiceId,
                    competitionId: 0,
                    eventType: InvoicePaymentEventTypes.Cancelled,
                    byMemberId: actorId,
                    byMemberName: actorName,
                    paymentMethod: null,
                    amount: null,
                    reference: null,
                    notes: $"Samlingsfaktura makulerad – {freed} fakturor frigjorda");
            }

            return Json(new { success, message, freedCount = freed, parentStatus = status });
        }

        /// <summary>
        /// May the current user pay on behalf of this club? Site admin, or a club/regional admin for
        /// it (IsClubAdminForClub covers regional admins of the club's region).
        /// </summary>
        private async Task<bool> CanPayForClubAsync(int payerClubId)
        {
            if (payerClubId <= 0) return false;
            if (await _authService.IsCurrentUserAdminAsync()) return true;
            return await _authService.IsClubAdminForClub(payerClubId);
        }

        /// <summary>
        /// Is the consolidated-payment ("samlingsfaktura") flow usable on this installation?
        /// The parent invoice needs properties on the `registrationInvoice` doctype that an operator
        /// has to create by hand, and SetValue on a missing property is a SILENT no-op — a parent
        /// would save and publish with no link to the invoices it covers, i.e. money collected
        /// against nothing. So the UI asks first and blocks the feature rather than half-write.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetConsolidationReadiness()
        {
            var isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedClubs = await _authService.GetManagedClubIds();
            var managedRegions = await _authService.GetManagedRegions();
            if (!isSiteAdmin && !managedClubs.Any() && !managedRegions.Any())
                return Json(new { success = false, message = "Access denied" });

            var missing = _paymentService.MissingInvoiceProperties();
            return Json(new
            {
                success = true,
                ready = missing.Count == 0,
                missingProperties = missing,
                // Deliberately actionable: a club admin who sees this can't fix it themselves.
                message = missing.Count == 0
                    ? ""
                    : "Samlingsfakturor är inte aktiverade än — egenskaper saknas på fakturatypen. "
                      + "Kontakta sajtadministratören och ange: " + string.Join(", ", missing)
            });
        }

        /// <summary>
        /// Get active competitions for filter dropdown
        /// Optionally filtered by region
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetActiveCompetitionsForFilter(string? region = null, bool regionOwnCompetitionsOnly = false)
        {
            // Allow site admins and regional admins
            bool isSiteAdmin = await _authService.IsCurrentUserAdminAsync();
            var managedRegions = await _authService.GetManagedRegions();
            bool isRegionalAdmin = !isSiteAdmin && managedRegions.Any();

            if (!isSiteAdmin && !isRegionalAdmin)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            // Same scoping as GetInvoices: a regional admin only ever sees their own krets.
            if (!isSiteAdmin && isRegionalAdmin)
            {
                if (string.IsNullOrWhiteSpace(region))
                    region = managedRegions.First();
                else if (!managedRegions.Any(r => string.Equals(r, region, StringComparison.OrdinalIgnoreCase)))
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
                        clubRegions[club.Id] = InvoiceAdminService.NormalizeRegionCode(
                            club.Value<string>("regionalFederation"));
                    }
                }

                var competitionsQuery = umbracoContext.Content.GetAtRoot()
                    .SelectMany(root => root.DescendantsOfType("competition"))
                    .Where(comp => comp.Value<bool>("isActive"));

                // Filter by region if specified
                if (!string.IsNullOrEmpty(region) && clubRegions != null)
                {
                    // A competition is hosted EITHER by a club in the region OR by the region itself
                    // (clubId unset, region code on the competition's own regionalFederation). The old
                    // `clubId > 0` requirement dropped every region-hosted competition, so a region's
                    // own competitions never appeared in this filter. Mirrors
                    // InvoiceAdminService.ResolveCompetitionRegion.
                    var wantedRegion = InvoiceAdminService.NormalizeRegionCode(region);
                    competitionsQuery = competitionsQuery.Where(comp =>
                    {
                        var clubId = comp.Value<int>("clubId");
                        if (clubId > 0)
                        {
                            // The krets's own Fakturor tab defaults to the krets's own competitions.
                            if (regionOwnCompetitionsOnly) return false;
                            return clubRegions.TryGetValue(clubId, out var clubRegion)
                                && clubRegion.Length > 0 && clubRegion == wantedRegion;
                        }

                        return InvoiceAdminService.NormalizeRegionCode(
                            comp.Value<string>("regionalFederation")) == wantedRegion;
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
        /// Record a "payment sent" CLAIM (payer side) — the shooter says "I've paid" or a club
        /// admin marks "betald av klubben". Does NOT set the organizer's authoritative received
        /// state (that stays <see cref="MarkAsPaid"/>, gated to the organizer). Auth is the
        /// payer-side <see cref="AdminAuthorizationService.CanClaimPaymentForInvoice"/>.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> MarkPaymentSent([FromBody] InvoiceActionRequest request)
        {
            if (!await _authService.CanClaimPaymentForInvoice(request.InvoiceId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var (actorId, actorName) = await GetCurrentActorAsync();
                var ok = await _paymentService.SetPaymentSentAsync(request.InvoiceId, true, actorId, actorName);
                if (!ok)
                    return Json(new { success = false, message = "Kunde inte spara betalningsanmälan." });

                InvalidateInvoiceCaches();
                return Json(new { success = true, paymentSentBy = actorName, paymentSentDate = DateTime.Now });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
            }
        }

        /// <summary>
        /// Withdraw a "payment sent" claim. Same payer-side auth as <see cref="MarkPaymentSent"/>.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> ClearPaymentSent([FromBody] InvoiceActionRequest request)
        {
            if (!await _authService.CanClaimPaymentForInvoice(request.InvoiceId))
            {
                return Json(new { success = false, message = "Access denied" });
            }

            try
            {
                var (actorId, actorName) = await GetCurrentActorAsync();
                var ok = await _paymentService.SetPaymentSentAsync(request.InvoiceId, false, actorId, actorName);
                if (!ok)
                    return Json(new { success = false, message = "Kunde inte uppdatera betalningsanmälan." });

                InvalidateInvoiceCaches();
                return Json(new { success = true });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Ett fel uppstod: " + ex.Message });
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
                    notes: $"QR-kod mejlad till {memberEmail}");

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
                var amountString = totalAmount.ToString("F2");
                var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                var qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);

                return Json(new
                {
                    success = true,
                    qrCodeBase64 = qrCodeBase64,
                    swishAppUrl = SwishQrCodeGenerator.GetSwishAppUrl(normalizedSwishNumber, amountString, message),
                    amount = amountString,
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
        public async Task<IActionResult> SendPaymentReminders(int competitionId, string? reminderMessage = null, List<int>? invoiceIds = null)
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

            // When the operator picked a subset in the modal, honour it. An empty/absent
            // list keeps the original "send to everyone pending" behaviour (back-compat).
            if (invoiceIds != null && invoiceIds.Count > 0)
            {
                var selected = new HashSet<int>(invoiceIds);
                pendingInvoices = pendingInvoices.Where(x => selected.Contains(x.Id)).ToList();
            }

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
                        ? DefaultReminderMessage
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
                        qrMessage,        // Swish payment reference (matches the QR code)
                        emailMessage);    // visible reminder note in the body

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

            return Json(new { success = true, count = pending, defaultMessage = DefaultReminderMessage });
        }

        /// <summary>
        /// The actual eligible recipients of a bulk reminder (individual pending invoices
        /// with a resolvable email), so the operator can tick/untick who gets one. Each
        /// entry carries when/how many reminders already went out so "redan påminda" can be
        /// deselected. This list is the source of truth for the selection UI — the chosen
        /// invoiceIds are posted straight back to SendPaymentReminders.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetReminderRecipients(int competitionId)
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
            if (invoicesHub == null)
                return Json(new { success = true, recipients = Array.Empty<object>(), defaultMessage = DefaultReminderMessage });

            var pendingInvoices = _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                .Where(x => x.ContentType.Alias == "registrationInvoice")
                .Where(x => x.GetValue<string>("paymentStatus") == "Pending")
                .ToList();

            // Reminder history (EmailSent events) for the whole competition, grouped per invoice.
            var reminderByInvoice = new Dictionary<int, (DateTime last, int count)>();
            try
            {
                var auditEvents = await _auditService.GetForCompetitionAsync(competitionId);
                foreach (var ev in auditEvents.Where(e => e.EventType == InvoicePaymentEventTypes.EmailSent))
                {
                    if (reminderByInvoice.TryGetValue(ev.InvoiceId, out var existing))
                        reminderByInvoice[ev.InvoiceId] = (existing.last >= ev.OccurredAt ? existing.last : ev.OccurredAt, existing.count + 1);
                    else
                        reminderByInvoice[ev.InvoiceId] = (ev.OccurredAt, 1);
                }
            }
            catch
            {
                // Reminder history is purely informational here — never fail the recipient
                // list because the audit read hiccupped; just show no "redan påmind" markers.
            }

            var list = new List<(int invoiceId, string name, string email, string club, decimal amount, DateTime? last, int count)>();
            foreach (var invoice in pendingInvoices)
            {
                var memberIdString = invoice.GetValue<string>("memberId");
                if (memberIdString != null && memberIdString.StartsWith("team-")) continue;  // teams handled separately
                if (!int.TryParse(memberIdString, out int memberId)) continue;

                var member = _memberService.GetById(memberId);
                if (member == null || string.IsNullOrEmpty(member.Email)) continue;  // no email = can't be a recipient

                string club = "";
                var regId = invoice.GetValue<int>("registrationId");
                if (regId > 0)
                {
                    var reg = _contentService.GetById(regId);
                    var clubId = reg?.GetValue<int>("clubId") ?? 0;
                    if (clubId > 0) club = _clubService.GetClubNameById(clubId) ?? "";
                }

                DateTime? last = null;
                int count = 0;
                if (reminderByInvoice.TryGetValue(invoice.Id, out var r)) { last = r.last; count = r.count; }

                list.Add((invoice.Id, member.Name ?? "", member.Email, club, invoice.GetValue<decimal>("totalAmount"), last, count));
            }

            var recipients = list
                .OrderBy(r => r.name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    invoiceId = r.invoiceId,
                    memberName = r.name,
                    email = r.email,
                    club = r.club,
                    amount = r.amount,
                    lastReminderSentDate = r.last,
                    reminderCount = r.count
                })
                .ToList();

            return Json(new { success = true, recipients, defaultMessage = DefaultReminderMessage });
        }

        /// <summary>
        /// Send a single test reminder to the *current operator's own* email address so
        /// they can preview exactly what the shooters will receive before doing the bulk
        /// send. Reuses the same email template + a representative QR code (real amount
        /// from the first pending invoice when available, otherwise the competition's base
        /// fee). Writes NO audit row and touches no invoice — it is purely a preview.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendTestReminder(int competitionId, string? reminderMessage = null)
        {
            if (competitionId <= 0)
                return Json(new { success = false, message = "competitionId is required" });

            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return Json(new { success = false, message = "Competition not found" });

            // Same four-tier rule as the bulk send.
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

            // Where does the test go? The logged-in operator's own email.
            var currentMember = await _memberManager.GetCurrentMemberAsync();
            var operatorEmail = currentMember?.Email;
            var operatorName = currentMember?.Name ?? "";
            if (string.IsNullOrEmpty(operatorEmail))
                return Json(new { success = false, message = "Din inloggning saknar e-postadress att skicka testet till." });

            // Pick a representative amount + invoice number: prefer a real pending invoice
            // so the preview looks authentic; fall back to the competition's base fee.
            decimal amount = 0m;
            var invoiceNumber = "TEST";
            var competitionChildren = _contentService.GetPagedChildren(competitionId, 0, 100, out _).ToList();
            var invoicesHub = competitionChildren
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (invoicesHub != null)
            {
                var firstPending = _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                    .Where(x => x.ContentType.Alias == "registrationInvoice")
                    .Where(x => x.GetValue<string>("paymentStatus") == "Pending")
                    .OrderBy(x => x.Id)
                    .FirstOrDefault();
                if (firstPending != null)
                {
                    amount = firstPending.GetValue<decimal>("totalAmount");
                    invoiceNumber = firstPending.GetValue<string>("invoiceNumber") ?? "TEST";
                }
            }
            if (amount <= 0m)
            {
                decimal.TryParse(competition.GetValue<string>("registrationFee"), out amount);
                if (amount <= 0m) amount = 1m;  // QR needs a positive amount
            }

            var emailMessage = string.IsNullOrWhiteSpace(reminderMessage)
                ? DefaultReminderMessage
                : reminderMessage;

            try
            {
                var competitionName = competition.GetValue<string>("competitionName") ?? competition.Name ?? "";
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                var qrMessage = $"Betalning: {invoiceNumber}";
                var qrBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amount.ToString("F2"), qrMessage);

                await _emailService.SendSwishQRCodeEmailAsync(
                    operatorEmail,
                    operatorName,
                    competitionName,
                    qrBytes,
                    amount,
                    "",  // shootingClasses
                    invoiceNumber,
                    swishNumber,
                    qrMessage,        // Swish payment reference (matches the QR code)
                    emailMessage);    // visible reminder note in the body

                return Json(new
                {
                    success = true,
                    email = operatorEmail,
                    message = $"Testpåminnelse skickad till {operatorEmail}."
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte skicka testet: " + ex.Message });
            }
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

            // Deltävling (sub-competition) revenue is bundled into each invoice's totalAmount by
            // RegistrationFeeCalculator. To itemise it on the books, derive the sub-comp portion
            // per registration and credit it once — to the registration's representative invoice
            // (its first Paid one, else its oldest) — so multi-invoice top-ups don't double-count.
            var regById = registrations.ToDictionary(r => r.Id);

            int ResolveRegistrationId(IContent inv)
            {
                var rid = inv.GetValue<int>("registrationId");
                if (rid > 0) return rid;
                var rel = inv.GetValue<string>("relatedRegistrationIds");
                if (!string.IsNullOrWhiteSpace(rel))
                {
                    var m = System.Text.RegularExpressions.Regex.Match(rel, @"\d+");
                    if (m.Success && int.TryParse(m.Value, out var legacyId)) return legacyId;
                }
                return 0;
            }

            decimal SubPortionForRegistration(int registrationId)
            {
                if (!regById.TryGetValue(registrationId, out var reg)) return 0m;
                var isSub = reg.HasProperty("isSubCompetition") && reg.GetValue<bool>("isSubCompetition");
                if (!isSub) return 0m;
                var classes = CompetitionRegistrationDocument
                    .DeserializeShootingClasses(reg.GetValue<string>("shootingClasses") ?? "")
                    .Select(c => c.Class)
                    .Where(c => !string.IsNullOrEmpty(c))
                    .ToList();
                return RegistrationFeeCalculator.CalculateSubCompetitionPortion(competition, classes, true);
            }

            // invoices are already ordered oldest-first, so the representative pick is stable.
            var subCompByInvoiceId = new Dictionary<int, decimal>();
            foreach (var grp in invoices.GroupBy(ResolveRegistrationId).Where(g => g.Key > 0))
            {
                var portion = SubPortionForRegistration(grp.Key);
                if (portion <= 0m) continue;
                var ordered = grp.ToList();
                var representative = ordered.FirstOrDefault(i =>
                    CleanStatus(i.GetValue<string>("paymentStatus") ?? "Pending") == "Paid") ?? ordered.First();
                subCompByInvoiceId[representative.Id] = portion;
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
                    Notes = inv.GetValue<string>("notes"),
                    SubCompetitionAmount = subCompByInvoiceId.TryGetValue(inv.Id, out var sub) ? sub : 0m
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
                PaidSubCompetitionTotal = paidRows.Sum(r => r.SubCompetitionAmount),
                PendingSubCompetitionTotal = outstandingRows.Sum(r => r.SubCompetitionAmount),
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
                SubCompetitionName = competition.GetValue<string>("subCompetitionName"),
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

    /// <summary>A club paying a set of invoices in one go ("samlingsfaktura").</summary>
    public class ConsolidationRequest
    {
        /// <summary>The club that will pay — not necessarily the club that issued the invoices.</summary>
        public int PayerClubId { get; set; }
        public int[]? InvoiceIds { get; set; }
    }
}
