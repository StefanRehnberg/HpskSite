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
        private readonly MemberClubService _memberClubService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        // Cache configuration
        // NB `region` MUST be part of the key: it's a filter on the result set, so leaving it out
        // served one krets's invoice list to another (and made the krets dropdown show stale data).
        private const string InvoicesListCacheKey = "admin_invoices_{0}_{1}_{2}_{3}_{4}_{5}_{6}"; // competitionId, clubId, excludePaid, activeOnly, page, viewType, region
        private static readonly TimeSpan InvoiceCacheDuration = TimeSpan.FromMinutes(5);

        /// <summary>
        /// The message used in a bulk payment reminder when the operator leaves the
        /// "Meddelande" field empty. Single source of truth — the modal prefills the
        /// textarea with this (via CountReminderRecipients) and SendPaymentReminders
        /// falls back to it server-side, so the two can never drift apart. Aliases
        /// <see cref="EmailService.DefaultPaymentIntroMessage"/> so the reminder default and the
        /// registration payment mail's own fallback stay one string.
        /// </summary>
        public const string DefaultReminderMessage = EmailService.DefaultPaymentIntroMessage;

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
            ConsolidatedInvoiceService consolidatedService,
            MemberClubService memberClubService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _consolidatedService = consolidatedService;
            _memberClubService = memberClubService;
            _databaseFactory = databaseFactory;
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

            // A competition manager runs the competition, so they may read ITS invoices — the Fakturor
            // view and Bokföringsunderlag are part of running the day. They hold no club or region role,
            // so this list previously came back "Access denied" for them.
            bool isCompetitionManagerOnly = false;
            if (!isSiteAdmin && !isClubAdmin && !isRegionalAdmin && competitionId.HasValue && competitionId.Value > 0)
            {
                isCompetitionManagerOnly = await _authService.IsCompetitionManager(competitionId.Value)
                                        || await _authService.IsRegionHostAdminAsync(competitionId.Value);
            }

            if (!isSiteAdmin && !isClubAdmin && !isRegionalAdmin && !isCompetitionManagerOnly)
            {
                return Json(new { success = false, message = "Access denied" });
            }

            // Access granted solely by managing ONE competition stays pinned to that competition — the
            // filters arrive from the client, so without this the grant would widen to other clubs and
            // kretsar simply by asking.
            if (isCompetitionManagerOnly)
            {
                clubId = null;
                region = null;
                regionOwnCompetitionsOnly = false;
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
        /// The competition's unpaid invoices grouped by the club that would pay them — the data behind
        /// the organiser-side samlingsfaktura on the Anmälningar tab (2026-08-20).
        ///
        /// Why this exists: the samlingsfaktura was a PULL model. Only the paying club could build one,
        /// on its own klubbsida, which is the page the clubs who most need it have never opened. The
        /// motivation sits with the organiser — they want to be paid — so the desk gets the same
        /// operation. Nobody outside the sekretariat then has to know what a samlingsfaktura is.
        ///
        /// **The club is the REGISTRATION's club, not the member's `primaryClubId`.** A shooter entered
        /// for their second club must land in that club's bill; see <see cref="MemberClubService"/> and
        /// the "Tävlar för" section in CLAUDE.md. Team invoices (`team-{id}`) take the TEAM's club.
        /// </summary>
        /// <summary>
        /// The club's RECEIVABLES, grouped by the club that owes them — the data behind
        /// "Skapa samlingsfaktura" in the Fakturor tab's *"att få betalt för"* view (2026-08-20).
        ///
        /// **The direction is the whole point, and it is the opposite of the payer flow.** When a club
        /// bundles what it OWES, the parent is created in the ORGANISER's ledger and addressed to
        /// itself — `payerClubId` is the club clicking. Here the club is the **utställare**: the parent
        /// lands in its OWN ledger, addressed to the debtor, and `payerClubId` is the other club.
        ///
        /// Why it needed its own surface: the checkbox column was gated to the payer views
        /// (`clubSelectionAllowed`), on the reasoning that "in att få betalt för the club is the
        /// recipient, so there is nothing to pay". True, but it left the club able to bundle what it
        /// owes and not what it is owed — and to do the latter it had to leave its own page entirely
        /// and go to the competition. The receivables view is the more natural home for "send them one
        /// bill"; the competition page is for the desk on the day.
        ///
        /// Scoped exactly like the incoming invoice list: competitions this club HOSTS
        /// (`competition.clubId == clubId`), so the payee resolved for the parent is this club.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClubReceivableDebtors(int clubId = 0, string region = "")
        {
            // ⚠️ BOTH HOST SHAPES. A competition is hosted either by a club (clubId set) or by the
            // KRETS itself (clubId unset, regionalFederation set) — an SM is the latter, and regions
            // arrange competitions and carry receivables exactly like clubs do. Serving only the club
            // shape is the mistake this codebase has made four separate times; see
            // AdminAuthorizationService.IsRegionHostAdminAsync.
            var wantRegion = (region ?? "").Trim();
            if (clubId <= 0 && wantRegion.Length == 0)
                return Json(new { success = false, message = "Ingen förening eller krets angiven." });

            // The organisation is issuing invoices in its own name, so admin rights over IT are the bar.
            //
            // ⚠️ Pass the region code in the casing the region NODE uses ("Halland"), which is what
            // RegionalAdminPanel supplies via data-locked-region. IsRegionalAdminForRegion builds the
            // member-group name `RegionalAdmin_{code}` and compares it EXACTLY, while competition
            // matching goes through NormalizeRegionCode, which lowercases. So a lowercased code still
            // finds the right competitions but is refused by auth — a mismatch that reads as a
            // permission problem rather than a casing one.
            var allowed = clubId > 0
                ? await _authService.IsClubAdminForClub(clubId)
                : await _authService.IsRegionalAdminForRegion(wantRegion);
            if (!allowed)
                return Json(new { success = false, message = "Du har inte behörighet att fakturera för den organisationen." });

            try
            {
                var hosted = clubId > 0
                    ? _invoiceService.GetCompetitionsHostedByClub(clubId)
                    : _invoiceService.GetCompetitionsHostedByRegion(wantRegion);

                // debtorClubId -> competitionId -> invoices
                var byDebtor = new Dictionary<int, Dictionary<int, List<PayerInvoice>>>();
                foreach (var comp in hosted)
                {
                    foreach (var kv in BuildPayerClubGroups(comp.Id))
                    {
                        // A club owing ITSELF is not a receivable — those are the host's own entries,
                        // settled internally rather than invoiced. A krets is not a club, so on the
                        // region shape every club is a legitimate debtor and nothing is skipped.
                        if (clubId > 0 && kv.Key == clubId) continue;
                        if (!byDebtor.TryGetValue(kv.Key, out var perComp))
                            byDebtor[kv.Key] = perComp = new Dictionary<int, List<PayerInvoice>>();
                        perComp[comp.Id] = kv.Value;
                    }
                }

                var clubs = byDebtor.Select(d => new
                {
                    clubId = d.Key,
                    clubName = _clubService.GetClubNameById(d.Key) ?? $"Förening #{d.Key}",
                    invoiceCount = d.Value.Sum(c => c.Value.Count),
                    total = d.Value.Sum(c => c.Value.Sum(i => i.Amount)),
                    // One samlingsfaktura per competition is what the engine produces, so the debtor
                    // owing across two competitions gets two bills. Show that split up front.
                    competitions = d.Value.Select(c => new
                    {
                        competitionId = c.Key,
                        competitionName = hosted.FirstOrDefault(h => h.Id == c.Key)?.Name ?? "",
                        invoices = c.Value.Select(i => new
                        {
                            id = i.InvoiceId,
                            invoiceNumber = i.InvoiceNumber,
                            name = i.Label,
                            amount = i.Amount,
                            isTeam = i.IsTeam
                        })
                    }),
                    invoices = d.Value.SelectMany(c => c.Value).Select(i => new
                    {
                        id = i.InvoiceId,
                        invoiceNumber = i.InvoiceNumber,
                        name = i.Label,
                        amount = i.Amount,
                        isTeam = i.IsTeam
                    })
                })
                .OrderByDescending(c => c.invoiceCount).ThenBy(c => c.clubName)
                .ToList();

                return Json(new { success = true, clubs });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Kunde inte hämta fordringarna: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetCompetitionPayerClubs(int competitionId)
        {
            if (competitionId <= 0)
                return Json(new { success = false, message = "Ingen tävling angiven." });
            if (!await _authService.CanManageCompetitionFinanceAsync(competitionId))
                return Json(new { success = false, message = "Du har inte behörighet att hantera den här tävlingens fakturor." });

            try
            {
                // Ask for the excluded rows too — the picker shows the whole field and says why a row
                // cannot be taken, rather than quietly leaving it out. A number that cannot be
                // reconciled against the Fakturor list beside it is worse than an awkward one.
                var orphans = new List<PayerInvoice>();
                var byClub = BuildPayerClubGroups(competitionId, includeExcluded: true, orphansOut: orphans);

                var clubs = byClub
                    .Select(kv => new
                    {
                        clubId = kv.Key,
                        clubName = _clubService.GetClubNameById(kv.Key) ?? $"Förening #{kv.Key}",
                        // Counts and totals describe what would actually be BILLED, so they count
                        // only the selectable rows. The excluded ones are shown, never summed —
                        // adding them would put money in the total that no invoice will ask for.
                        invoiceCount = kv.Value.Count(i => i.Selectable),
                        total = kv.Value.Where(i => i.Selectable).Sum(i => i.Amount),
                        excludedCount = kv.Value.Count(i => !i.Selectable),
                        invoices = kv.Value
                            // Selectable first; within each, the excluded ones keep their order so the
                            // reasons read as a list rather than being scattered through the rows.
                            .OrderByDescending(i => i.Selectable)
                            .Select(i => new
                            {
                                id = i.InvoiceId,
                                invoiceNumber = i.InvoiceNumber,
                                name = i.Label,
                                amount = i.Amount,
                                isTeam = i.IsTeam,
                                selectable = i.Selectable,
                                excludedReason = i.ExcludedReason
                            })
                    })
                    // Most invoices first: the club with several entries is the whole reason this exists.
                    .OrderByDescending(c => c.invoiceCount).ThenBy(c => c.clubName)
                    .ToList();

                // Reported rather than merely skipped. These have no club to bill, so they can never
                // be part of a samlingsfaktura — but the organiser is looking at a Fakturor list that
                // DOES show them, and an unexplained gap between the two screens is what sent people
                // hunting at SM 2026.
                var orphanRows = orphans
                    .OrderBy(o => o.InvoiceNumber)
                    .Select(o => new
                    {
                        id = o.InvoiceId,
                        invoiceNumber = o.InvoiceNumber,
                        name = o.Label,
                        amount = o.Amount,
                        reason = o.ExcludedReason
                    })
                    .ToList();

                return Json(new { success = true, clubs, orphans = orphanRows });
            }
            catch (Exception ex)
            {
                // This controller has no injected logger; surface the reason like its siblings do.
                return Json(new { success = false, message = "Kunde inte hämta fakturorna: " + ex.Message });
            }
        }

        private sealed class PayerInvoice
        {
            public int InvoiceId { get; init; }
            public string InvoiceNumber { get; init; } = "";
            public string Label { get; init; } = "";
            public decimal Amount { get; init; }
            public bool IsTeam { get; init; }

            /// <summary>
            /// False when the row cannot go into a samlingsfaktura. It is still returned — see
            /// <see cref="ExcludedReason"/> — because silently omitting it is what made the picker
            /// impossible to reconcile against the invoice list next to it.
            /// </summary>
            public bool Selectable { get; init; } = true;

            /// <summary>Plain-language reason the row cannot be included; empty when it can.</summary>
            public string ExcludedReason { get; init; } = "";
        }

        /// <summary>
        /// The competition's consolidatable invoices, keyed by the club that would pay each one. Used by
        /// both the picker and the server-side ownership guard, so the screen and the rule can never
        /// disagree about which club an invoice belongs to.
        /// </summary>
        /// <param name="includeExcluded">
        /// Opt IN to also getting the rows that CANNOT be consolidated, each carrying its reason.
        /// Only the picker wants those — it exists to show the organiser the whole field. The other
        /// two callers ask ownership and receivable questions where an unusable row would either
        /// inflate a debt or let an ineligible invoice pass a guard, so the default stays "eligible
        /// only" and adding the reasons cannot silently change their behaviour.
        /// </param>
        /// <param name="orphansOut">
        /// Receives invoices with no payer club at all — a team invoice whose team was deleted.
        /// They are deliberately NOT put in a pseudo-club group: a group with nothing selectable is
        /// dropped by the picker's "more than one invoice" filter, so they would disappear all over
        /// again. The picker names them in a note instead, which is the point — skipping them is the
        /// right outcome, arrived at invisibly.
        /// </param>
        private Dictionary<int, List<PayerInvoice>> BuildPayerClubGroups(
            int competitionId, bool includeExcluded = false, List<PayerInvoice>? orphansOut = null)
        {
            var byClub = new Dictionary<int, List<PayerInvoice>>();

            var hub = _contentService.GetPagedChildren(competitionId, 0, 200, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "registrationInvoicesHub");
            if (hub == null) return byClub;

            // Registration → club, resolved once for the whole competition rather than per invoice.
            var regClubs = _memberClubService.GetRegistrationClubIds(competitionId);

            // Team → club. One query; a competition has tens of teams, not thousands.
            var teamClubs = new Dictionary<int, int>();
            var teamNames = new Dictionary<int, string>();
            using (var db = _databaseFactory.CreateDatabase())
            {
                foreach (var t in db.Fetch<CompetitionTeamDto>(
                    "SELECT * FROM CompetitionTeam WHERE CompetitionId = @0", competitionId))
                {
                    teamClubs[t.Id] = t.ClubId;
                    teamNames[t.Id] = t.TeamName;
                }
            }

            // What each ANMÄLAN still owes — the question the picker has to ask. Asking each INVOICE
            // for its status instead is what offered Daniel Borg's 400 kr at SM 2026: a leftover
            // invoice still said Pending long after the registration had been paid through its twin.
            // Read-only by construction; EnsureOutstandingInvoiceAsync would answer the same question
            // but MUTATES, which a picker must never do.
            var owedByRegistration = _paymentService.GetOutstandingByRegistration(competitionId);

            foreach (var invoice in _contentService.GetPagedChildren(hub.Id, 0, 2000, out _))
            {
                if (invoice.ContentType.Alias != "registrationInvoice") continue;

                // Inspect() is the single source of truth for consolidatability (Pending, no
                // invoiceKind, not already covered, amount > 0); duplicating its rules here is how the
                // two drift. An ineligible row is now KEPT and labelled rather than dropped.
                var candidate = _consolidatedService.Inspect(invoice.Id);

                var memberId = invoice.GetValue<string>("memberId") ?? "";
                var isTeam = memberId.StartsWith("team-");

                // What "show the excluded rows too" is allowed to mean. Without these two guards the
                // picker fills with history — 203 of 207 rows on a real dev competition — and the
                // signal disappears into it, which is a different way of hiding the same thing.
                //
                //  * A samlingsfaktura or kreditfaktura is the OUTPUT of this operation, not an
                //    entry in it. Listing parents as "cannot be included" is nonsense, and reading
                //    their `club-{id}` memberId as an unresolvable club labelled them ORPHANED —
                //    a 45 550 kr parent announced as a deleted team.
                //  * A settled invoice is not outstanding, so nobody looking at "who still owes
                //    what" expects to see it.
                //
                // What remains is exactly what an organiser would expect in the picker and be
                // puzzled not to find: unpaid, ordinary invoices.
                var kind = invoice.GetValue<string>("invoiceKind") ?? "";
                if (kind == "consolidated" || kind == "creditNote") continue;
                var rawStatus = (invoice.GetValue<string>("paymentStatus") ?? "").Trim().Trim('[', ']').Trim('"');
                if (rawStatus != "Pending") continue;
                var label = candidate.MemberName;
                int clubId = 0;
                var selectable = candidate.Eligible;
                var reason = candidate.Eligible ? "" : (candidate.Reason ?? "Kan inte ingå i en samlingsfaktura.");

                if (isTeam)
                {
                    if (int.TryParse(memberId.Substring(5), out var teamId))
                    {
                        teamClubs.TryGetValue(teamId, out clubId);
                        if (teamNames.TryGetValue(teamId, out var tn) && !string.IsNullOrWhiteSpace(tn)) label = tn;

                        // The team is gone but its invoice stayed. This used to fall through the
                        // clubId <= 0 skip below and vanish — the right outcome reached silently,
                        // which left the organiser comparing a picker against a Fakturor list that
                        // showed rows the picker did not.
                        if (!teamClubs.ContainsKey(teamId))
                        {
                            selectable = false;
                            reason = "Laget är borttaget – makulera fakturan i Fakturor-vyn.";
                            if (string.IsNullOrWhiteSpace(label)) label = $"Borttaget lag (faktura {candidate.InvoiceNumber})";
                        }
                    }
                }
                else if (int.TryParse(memberId, out var mid))
                {
                    if (!regClubs.TryGetValue(mid, out clubId) || clubId <= 0)
                        clubId = _memberClubService.GetPrimaryClubId(_memberService.GetById(mid));

                    // Ask the ANMÄLAN, not the invoice. An invoice that is Pending on a registration
                    // owing nothing is a leftover, and billing a club for it is billing them twice.
                    var registrationId = invoice.GetValue<int>("registrationId");
                    if (selectable && registrationId > 0
                        && owedByRegistration.TryGetValue(registrationId, out var owed) && owed <= 0m)
                    {
                        selectable = false;
                        reason = "Anmälan är redan betald – fakturan är en kvarleva.";
                    }
                }

                // Still no identifiable payer: an orphaned team already carries its own reason above,
                // so anything reaching here has no club at all and there is no group to show it in.
                if (!selectable && !includeExcluded) continue;

                if (clubId <= 0)
                {
                    // Only a TEAM invoice can be genuinely orphaned — its team row is gone. Anything
                    // else without a resolvable club is a data oddity, not junk to advertise, so it
                    // is skipped as before rather than announced under a label that would be wrong.
                    if (isTeam)
                    {
                        orphansOut?.Add(new PayerInvoice
                        {
                            InvoiceId = candidate.InvoiceId,
                            InvoiceNumber = candidate.InvoiceNumber,
                            Label = label,
                            Amount = candidate.Amount,
                            IsTeam = true,
                            Selectable = false,
                            ExcludedReason = string.IsNullOrWhiteSpace(reason)
                                ? "Laget är borttaget – makulera fakturan i Fakturor-vyn."
                                : reason
                        });
                    }
                    continue;
                }

                if (!byClub.TryGetValue(clubId, out var list)) byClub[clubId] = list = new List<PayerInvoice>();
                list.Add(new PayerInvoice
                {
                    InvoiceId = candidate.InvoiceId,
                    InvoiceNumber = candidate.InvoiceNumber,
                    Label = label,
                    Amount = candidate.Amount,
                    IsTeam = isTeam,
                    Selectable = selectable,
                    ExcludedReason = reason
                });
            }

            return byClub;
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

            // Payer side (the club builds its own bill) OR organiser side (the sekretariat issues it).
            var isPayerSide = await CanPayForClubAsync(request.PayerClubId);
            if (!isPayerSide)
            {
                var (ok, reason) = await CanOrganiseConsolidationAsync(request.PayerClubId, request.InvoiceIds);
                if (!ok) return Json(new { success = false, message = reason ?? "Du har inte behörighet att betala för den föreningen." });
            }

            // One club at a time. Applies when the caller got in as the ORGANISER (they are not an
            // admin of the paying club, so they must bill the club the invoices really belong to),
            // and also whenever the request says it came from the organiser UI - which matters when
            // the sekreterare happens to ALSO be a club admin of the paying club and would otherwise
            // fall through to the club page's deliberately permissive path.
            if (!isPayerSide || request.OrganiserScope)
            {
                var (single, mixedReason) = SelectionBelongsToPayerClub(request.PayerClubId, request.InvoiceIds);
                if (!single) return Json(new { success = false, message = mixedReason });
            }

            var preview = _consolidatedService.Preview(request.InvoiceIds);
            return Json(new
            {
                success = true,
                ready = preview.Ready,
                missingProperties = preview.MissingProperties,
                grandTotal = preview.GrandTotal,
                // One parent = one payment to one recipient. The client must show these before the
                // user commits: a selection spanning competitions/payees can never be one payment.
                payeeCount = preview.PayeeCount,
                spansMultipleCompetitions = preview.SpansMultipleCompetitions,
                spansMultiplePayees = preview.SpansMultiplePayees,
                warning = preview.Warning,
                groups = preview.Groups.Select(g => new
                {
                    competitionId = g.CompetitionId,
                    competitionName = g.CompetitionName,
                    payeeKey = g.Payee.Key,
                    payeeName = g.Payee.Name,
                    hasSwishNumber = !string.IsNullOrWhiteSpace(g.Payee.SwishNumber),
                    // A payee with only a bankgiro is perfectly payable — the club just pays by BG.
                    hasBgNumber = !string.IsNullOrWhiteSpace(g.Payee.BgNumber),
                    bgNumber = g.Payee.BgNumber,
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

            // Payer side (the club builds its own bill) OR organiser side (the sekretariat issues it).
            var isPayerSide = await CanPayForClubAsync(request.PayerClubId);
            if (!isPayerSide)
            {
                var (ok, reason) = await CanOrganiseConsolidationAsync(request.PayerClubId, request.InvoiceIds);
                if (!ok) return Json(new { success = false, message = reason ?? "Du har inte behörighet att betala för den föreningen." });
            }

            // One club at a time. Applies when the caller got in as the ORGANISER (they are not an
            // admin of the paying club, so they must bill the club the invoices really belong to),
            // and also whenever the request says it came from the organiser UI - which matters when
            // the sekreterare happens to ALSO be a club admin of the paying club and would otherwise
            // fall through to the club page's deliberately permissive path.
            if (!isPayerSide || request.OrganiserScope)
            {
                var (single, mixedReason) = SelectionBelongsToPayerClub(request.PayerClubId, request.InvoiceIds);
                if (!single) return Json(new { success = false, message = mixedReason });
            }

            var actor = await _memberManager.GetCurrentMemberAsync();
            var actorData = actor == null ? null : _memberService.GetByEmail(actor.Email ?? "");

            var (success, message, parents, rejected) = await _consolidatedService.CreateAsync(
                request.PayerClubId, request.InvoiceIds, actorData?.Id ?? 0, actorData?.Name);

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

            var (cancelActorId, cancelActorName) = await GetCurrentActorAsync();
            var (success, message, freed, _, status) =
                _consolidatedService.CancelUnpaidParent(request.InvoiceId, cancelActorId ?? 0, cancelActorName);
            if (success)
            {
                InvalidateInvoiceCaches();
                var (actorId, actorName) = (cancelActorId, cancelActorName);
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
        /// What a samlingsfaktura charges for, what has been credited, and which covered registrations
        /// are still creditable — the credit-note dialog's data. Readable by the organiser (who issues
        /// credits) and by the paying club (who needs to see what it is being charged for).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetConsolidationDetail(int invoiceId)
        {
            if (invoiceId <= 0) return Json(new { success = false, message = "Ingen faktura angiven." });

            var payerClubId = _consolidatedService.ReadPayerClubId(invoiceId);
            var isPayer = payerClubId > 0 && await CanPayForClubAsync(payerClubId);
            var isOrganiser = await _authService.CanManageCompetitionInvoice(invoiceId);
            if (!isPayer && !isOrganiser)
                return Json(new { success = false, message = "Access denied" });

            var d = _consolidatedService.GetDetail(invoiceId);
            if (!d.Found) return Json(new { success = false, message = "Det här är inte en samlingsfaktura." });

            return Json(new
            {
                success = true,
                // Only the organiser may issue a credit, so the dialog hides the actions for a payer.
                canCredit = isOrganiser,
                invoiceId = d.InvoiceId,
                invoiceNumber = d.InvoiceNumber,
                competitionName = d.CompetitionName,
                payerName = d.PayerName,
                status = d.Status,
                isPaid = d.IsPaid,
                total = d.Total,
                credited = d.Credited,
                amountDue = d.AmountDue,
                maxCreditable = d.MaxCreditable,
                covered = d.Covered.Select(c => new
                {
                    invoiceId = c.InvoiceId, invoiceNumber = c.InvoiceNumber, memberName = c.MemberName,
                    amount = c.Amount, paymentStatus = c.PaymentStatus, alreadyCredited = c.AlreadyCredited
                }),
                creditNotes = d.CreditNotes.Select(c => new
                {
                    invoiceId = c.InvoiceId, invoiceNumber = c.InvoiceNumber,
                    amount = c.Amount, paymentStatus = c.PaymentStatus
                })
            });
        }

        /// <summary>
        /// Issue a kreditfaktura against a samlingsfaktura. This is the ORGANISER's act — they are the
        /// one reducing what they claim, or acknowledging they owe money back — so it is gated to the
        /// organiser, not the payer.
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> CreateCreditNote([FromBody] CreditNoteRequest request)
        {
            if (request == null || request.ParentInvoiceId <= 0)
                return Json(new { success = false, message = "Ingen samlingsfaktura angiven." });

            if (!await _authService.CanManageCompetitionInvoice(request.ParentInvoiceId))
                return Json(new { success = false, message = "Bara arrangören kan skapa en kreditfaktura." });

            var (actorId, actorName) = await GetCurrentActorAsync();
            var result = await _consolidatedService.CreateCreditNoteAsync(
                request.ParentInvoiceId, request.CreditedInvoiceId, request.Amount,
                request.Reason ?? "", actorId, actorName);

            if (result.Success) InvalidateInvoiceCaches();

            return Json(new
            {
                success = result.Success,
                message = result.Message,
                creditNoteId = result.CreditNoteId,
                creditNoteNumber = result.CreditNoteNumber,
                amount = result.Amount,
                remainingDue = result.RemainingDue,
                parentClosed = result.ParentClosed,
                awaitingRefund = result.AwaitingRefund
            });
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
        /// May the current user consolidate this selection as the ORGANISER — i.e. issue the bill rather
        /// than pay it? Two conditions, both required (2026-08-20):
        ///
        /// 1. They hold the finance right on the competition every selected invoice belongs to. No new
        ///    permission was introduced: this is the same right that already allows MarkAsPaid,
        ///    CancelInvoice and CreateCreditNote, so it grants nothing the holder did not already have.
        ///    Checked per invoice, so a selection cannot smuggle in one from another competition.
        ///
        /// 2. **Every invoice actually belongs to the payer club** — Stefan's "one club at a time" rule.
        ///    On the payer path this is implicit (a club admin can only reach their own club's
        ///    invoices), but an organiser sees the whole field, and stamping one `payerClubId` onto a
        ///    mixed selection would bill club A for club B's shooters. That is the specific accident
        ///    worth refusing outright rather than warning about.
        ///
        /// Returns the reason on failure so the UI can say which of the two it was.
        /// </summary>
        private async Task<(bool ok, string? reason)> CanOrganiseConsolidationAsync(int payerClubId, int[] invoiceIds)
        {
            if (payerClubId <= 0) return (false, "Ingen betalande förening angiven.");
            if (invoiceIds == null || invoiceIds.Length == 0) return (false, "Inga fakturor valda.");

            foreach (var id in invoiceIds)
            {
                if (!await _authService.CanManageCompetitionInvoice(id))
                    return (false, "Du har inte behörighet att fakturera för den här tävlingen.");
            }
            return (true, null);
        }

        /// <summary>
        /// Stefan's "one club at a time" rule for the ORGANISER path: every selected invoice must be one
        /// the picker actually offered for <paramref name="payerClubId"/> on that competition.
        ///
        /// **Why it is scoped to that path and not applied to everyone.** A first cut enforced
        /// homogeneity on every caller and broke two legitimate things at once: the club's own "Betala
        /// valda", where a club pays for a member who is registered as competing for ANOTHER club (the
        /// payer is still one club — the registration club is irrelevant to who pays), and the discovery
        /// pattern where a caller previews a broad selection to learn what is eligible. Preview writes
        /// nothing; refusing it is the wrong answer to "tell me what would happen".
        ///
        /// Structurally the endpoint already guarantees one payer per action — a single
        /// <c>payerClubId</c> means one bill to one club. What this adds is the organiser-specific
        /// property: the sekretariat sees the whole field, so it must not be able to sweep another
        /// club's shooters into the bill it is issuing.
        ///
        /// <paramref name="organiserScope"/> comes from the client, which is safe because it only ever
        /// TIGHTENS: omitting it yields the long-standing behaviour the caller was already authorised
        /// for, and setting it adds a check. It is not a security boundary — the finance right is.
        /// </summary>
        private (bool ok, string? reason) SelectionBelongsToPayerClub(int payerClubId, int[] invoiceIds)
        {
            var owned = new HashSet<int>();
            var seenCompetitions = new HashSet<int>();

            foreach (var id in invoiceIds)
            {
                var inv = _contentService.GetById(id);
                if (inv == null) continue;
                var compId = ConsolidatedInvoiceService.ReadInt(inv, "competitionId");
                if (compId <= 0 || !seenCompetitions.Add(compId)) continue;

                if (BuildPayerClubGroups(compId).TryGetValue(payerClubId, out var list))
                    foreach (var i in list) owned.Add(i.InvoiceId);
            }

            if (invoiceIds.Any(id => !owned.Contains(id)))
                return (false, "Alla valda fakturor måste tillhöra samma förening — "
                             + "gör en samlingsfaktura per förening.");

            return (true, null);
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

                // A samlingsfaktura settles everything it covers: the organiser received ONE payment
                // for N registrations, so those registrations must not keep showing as unpaid.
                // Idempotent, and each child gets its own audit row + betalningsbekräftelse — the club
                // paid, but the shooter still needs to know their registration is settled.
                var cascade = await _consolidatedService.CascadePaidToChildrenAsync(
                    parentInvoiceId: request.InvoiceId,
                    paymentDate: DateTime.Now,
                    paymentMethod: null,
                    actorMemberId: actorId,
                    actorMemberName: actorName);

                // Invalidate cache
                InvalidateInvoiceCaches();

                var extra = cascade.paid > 0 || cascade.skipped > 0 || cascade.failed > 0
                    ? $" {cascade.paid} underliggande fakturor markerades som betalda"
                      + (cascade.skipped > 0 ? $", {cascade.skipped} var redan betalda" : "")
                      + (cascade.failed > 0 ? $", {cascade.failed} MISSLYCKADES" : "") + "."
                    : "";

                return Json(new
                {
                    success = true,
                    message = "Invoice marked as paid" + extra,
                    cascadedPaid = cascade.paid,
                    cascadedSkipped = cascade.skipped,
                    cascadedFailed = cascade.failed
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
        /// What is actually left to pay on an invoice. For a samlingsfaktura the issued total is never
        /// edited, so the outstanding amount is DERIVED (total − credit notes) — anything generating a
        /// QR or showing "att betala" must read this rather than totalAmount.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetInvoiceBalance(int invoiceId)
        {
            if (invoiceId <= 0) return Json(new { success = false, message = "Ingen faktura angiven." });

            var payerClubId = _consolidatedService.ReadPayerClubId(invoiceId);
            var isPayer = payerClubId > 0 && await CanPayForClubAsync(payerClubId);
            if (!isPayer && !await _authService.CanManageCompetitionInvoice(invoiceId))
                return Json(new { success = false, message = "Access denied" });

            var b = _consolidatedService.GetBalance(invoiceId);
            return Json(new
            {
                success = true,
                isParent = b.IsParent,
                total = b.Total,
                credited = b.Credited,
                amountDue = b.AmountDue,
                status = b.Status,
                coveredCount = b.CoveredCount,
                payerClubId = b.PayerClubId
            });
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

                // An invoice a samlingsfaktura is still charging for must not be cancelled from here:
                // the parent is never recalculated, so the club would keep paying for a registration
                // that no longer exists. Say what to do instead rather than failing opaquely.
                if (_paymentService.IsCoveredByOpenConsolidation(request.InvoiceId, out var coverParent, out var coverPaid))
                {
                    return Json(new
                    {
                        success = false,
                        message = PaymentService.CoveredByConsolidationMessage(coverParent, coverPaid)
                    });
                }

                // Update invoice status to Cancelled (logs Cancelled audit event)
                var cancelled = await _paymentService.UpdatePaymentStatusAsync(
                    invoiceId: request.InvoiceId,
                    paymentStatus: "Cancelled",
                    paymentDate: null,
                    transactionId: null,
                    notes: "Cancelled by admin",
                    paymentMethod: null,
                    actorMemberId: actorId,
                    actorMemberName: actorName
                );
                if (!cancelled)
                {
                    return Json(new { success = false, message = "Fakturan kunde inte makuleras." });
                }

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

                // Get Swish details from competition. The mail embeds a Swish QR, so it still needs a
                // Swish number; the organiser's bankgiro rides along in the body as the alternative.
                var swishNumber = competition.Value<string>("swishNumber");
                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Competition has no Swish number configured" });
                }
                var emailPayee = _consolidatedService.ResolvePayee(competitionId);

                // Generate QR code
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber");

                // This endpoint had NO status check at all, so an already-paid invoice could be
                // resent with a live QR — an invitation to pay twice. The resolver answers both
                // "may this be sent" and "for how much" (the issued total is not what is left to
                // pay once a kreditfaktura exists).
                var resolved = _consolidatedService.ResolveQrAmount(invoice.Id);
                if (!resolved.Ok)
                    return Json(new { success = false, message = resolved.Message });
                var totalAmount = resolved.Amount;
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
                    "Faktura skickad av administratör",  // invoiceMessage
                    bgNumber: emailPayee.BgNumber,             // bankgiro alternative in the mail
                    payeeName: emailPayee.Name
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
            //
            // PLUS the club a samlingsfaktura was issued to: it is routinely paying a competition run
            // by another club or by the krets, so the organiser-scoped check refuses it — and without
            // a QR the club has no way to actually pay the invoice it was just handed.
            var qrPayerClubId = _consolidatedService.ReadPayerClubId(invoiceId);
            var qrIsPayer = qrPayerClubId > 0 && await CanPayForClubAsync(qrPayerClubId);
            if (!qrIsPayer && !await _authService.CanManageCompetitionInvoice(invoiceId))
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

                // Payment details: Swish comes from the competition, bankgiro from the organising
                // club/krets. Either one alone is enough to pay — a BG-only organiser used to get
                // "Competition has no Swish number configured" and no way to pay at all.
                var payee = _consolidatedService.ResolvePayee(competitionId);
                var swishNumber = competition.Value<string>("swishNumber");
                var bgNumber = payee.BgNumber;
                if (string.IsNullOrEmpty(swishNumber) && string.IsNullOrEmpty(bgNumber))
                {
                    return Json(new
                    {
                        success = false,
                        message = "Tävlingen saknar Swish-nummer och arrangören saknar bankgiro — "
                                + "lägg till minst ett av dem för att kunna ta betalt."
                    });
                }

                // Get invoice details
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber");
                var message = $"Betalning: {invoiceNumber}";

                // The QR must collect what is OUTSTANDING, not the issued total. For a samlingsfaktura
                // an issued invoice is never edited, so a correction is a credit note and the amount to
                // pay is derived (total − credits). Reading totalAmount here would collect the
                // pre-credit amount — i.e. overcharge the club by the credited sum.
                var balance = _consolidatedService.GetBalance(invoiceId);
                var totalAmount = balance.AmountDue;
                if (totalAmount <= 0)
                {
                    return Json(new
                    {
                        success = false,
                        message = balance.Credited > 0 && balance.Total > 0 && balance.Status != "Paid"
                            ? "Ingenting kvar att betala – fakturan är helt krediterad."
                            : "Ingenting kvar att betala på den här fakturan."
                    });
                }

                // Generate QR code — only when there is a Swish number; a BG-only organiser still gets
                // a payable dialog, just without a QR.
                var amountString = totalAmount.ToString("F2");
                string? qrCodeBase64 = null, swishAppUrl = null;
                if (!string.IsNullOrEmpty(swishNumber))
                {
                    var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                    qrCodeBase64 = Convert.ToBase64String(
                        SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message));
                    swishAppUrl = SwishQrCodeGenerator.GetSwishAppUrl(normalizedSwishNumber, amountString, message);
                }

                // Bankgiro QR in the Swedish invoice-QR format, read by the payer's own bank app. Needs
                // no agreement with the banks, so any organiser with a bankgiro gets one; a malformed
                // or missing number simply yields no QR rather than failing the dialog.
                string? bgQrBase64 = null, bgQrPayload = null;
                if (BankgiroQrCodeGenerator.IsValidBankgiro(bgNumber))
                {
                    try
                    {
                        var invoiceDate = invoice.GetValue<DateTime?>("createdDate");
                        // Exposed so a payment problem can be diagnosed from what the QR actually says,
                        // rather than by decoding an image.
                        bgQrPayload = BankgiroQrCodeGenerator.BuildPayload(
                            payee.Name, bgNumber, totalAmount, invoiceNumber ?? "",
                            payee.OrgNumber, invoiceDate);
                        bgQrBase64 = Convert.ToBase64String(BankgiroQrCodeGenerator.GeneratePng(
                            payee.Name, bgNumber, totalAmount, invoiceNumber ?? "",
                            payeeOrgNumber: payee.OrgNumber,
                            invoiceDate: invoiceDate));
                    }
                    catch
                    {
                        // The QR is a convenience on top of the bankgiro details, which are always shown
                        // as text — so a malformed number costs the shortcut, never the ability to pay.
                    }
                }

                return Json(new
                {
                    success = true,
                    qrCodeBase64 = qrCodeBase64,
                    swishAppUrl = swishAppUrl,
                    amount = amountString,
                    invoiceNumber = invoiceNumber,
                    competitionName = competition.Name,
                    // Bankgiro alternative: the invoice number is the payment reference, and the payee
                    // name matters because a club often pays another club's or the krets's invoice.
                    bgNumber = bgNumber,
                    bgReference = invoiceNumber,
                    bgQrCodeBase64 = bgQrBase64,
                    bgQrPayload = bgQrPayload,
                    payeeName = payee.Name,
                    // So a payment dialog can explain why the QR is for less than the invoice says.
                    issuedTotal = balance.Total,
                    credited = balance.Credited,
                    isConsolidated = balance.IsParent,
                    coveredCount = balance.CoveredCount
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
                    // Skjutledare deliberately NOT accepted on the finance surface (2026-08-19) --
                    // same rule as AdminAuthorizationService.CanManageCompetitionInvoice, whose
                    // remarks explain why, and how to grant a sekretariat person access instead.
                    authorized = await _authService.IsClubAdminForClub(competitionClubId);
                }
                else
                {
                    // Region-hosted (clubId unset — the SM shape): the krets is the organiser, so it is
                    // the party entitled to chase its own unpaid entries.
                    authorized = await _authService.IsRegionHostAdminAsync(
                        competitionClubId, competition.GetValue<string>("regionalFederation"));
                }
            }
            if (!authorized) return Json(new { success = false, message = "Access denied" });

            var swishNumber = competition.GetValue<string>("swishNumber");
            if (string.IsNullOrEmpty(swishNumber))
                return Json(new { success = false, message = "Tävlingen saknar Swish-nummer." });
            var reminderPayee = _consolidatedService.ResolvePayee(competitionId);

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
            // Never silently omit: the operator is told WHY someone was skipped, grouped by reason.
            var skippedReasons = new List<ConsolidatedInvoiceService.QrRefusal>();

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

                    // How much to chase — and WHETHER to chase — comes from the one resolver, never
                    // from totalAmount. The status filter above cannot see an invoice the club is
                    // already paying through an open samlingsfaktura (it stays "Pending"), so without
                    // this the shooter gets a Swish QR for money that is on its way from someone else.
                    var resolved = _consolidatedService.ResolveQrAmount(invoice.Id);
                    if (!resolved.Ok)
                    {
                        skippedCount++;
                        skippedReasons.Add(resolved.Refusal);
                        continue;
                    }
                    var totalAmount = resolved.Amount;
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
                        emailMessage,     // visible reminder note in the body
                        bgNumber: reminderPayee.BgNumber,
                        payeeName: reminderPayee.Name);

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

            var skipNote = DescribeSkippedReasons(skippedReasons);
            return Json(new
            {
                success = true,
                sentCount,
                skippedCount,
                errorCount = errors.Count,
                errors = errors.Take(10).ToList(),  // cap error list so the response stays reasonable
                message = $"Påminnelser skickade: {sentCount}. Hoppade över: {skippedCount}."
                    + (string.IsNullOrEmpty(skipNote) ? "" : $" ({skipNote})")
            });
        }

        /// <summary>
        /// Plain-Swedish summary of why recipients were skipped, so the count is never a bare number
        /// the operator has to guess at. Only reasons that actually occurred are named.
        /// </summary>
        private static string DescribeSkippedReasons(List<ConsolidatedInvoiceService.QrRefusal> reasons)
        {
            if (reasons.Count == 0) return "";
            var parts = new List<string>();
            foreach (var group in reasons.GroupBy(r => r).OrderByDescending(g => g.Count()))
            {
                var what = group.Key switch
                {
                    ConsolidatedInvoiceService.QrRefusal.CoveredByConsolidation => "täcks av en samlingsfaktura",
                    ConsolidatedInvoiceService.QrRefusal.AlreadyPaid => "redan betalda",
                    ConsolidatedInvoiceService.QrRefusal.Cancelled => "makulerade",
                    ConsolidatedInvoiceService.QrRefusal.NothingToCollect => "inget kvar att betala",
                    _ => "annat skäl"
                };
                parts.Add($"{group.Count()} {what}");
            }
            return string.Join(", ", parts);
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
                    // Skjutledare deliberately NOT accepted on the finance surface (2026-08-19) --
                    // same rule as AdminAuthorizationService.CanManageCompetitionInvoice, whose
                    // remarks explain why, and how to grant a sekretariat person access instead.
                    authorized = await _authService.IsClubAdminForClub(competitionClubId);
                }
                else
                {
                    // Region-hosted (clubId unset — the SM shape): the krets is the organiser, so it is
                    // the party entitled to chase its own unpaid entries.
                    authorized = await _authService.IsRegionHostAdminAsync(
                        competitionClubId, competition.GetValue<string>("regionalFederation"));
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
                    if (member == null || string.IsNullOrEmpty(member.Email)) return false;
                    // "You're about to email N shooters" has to mean N, so the same eligibility
                    // rule as the send itself applies — a covered or settled invoice is not a
                    // recipient however Pending it looks.
                    return _consolidatedService.ResolveQrAmount(x.Id).Ok;
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
                    // Skjutledare deliberately NOT accepted on the finance surface (2026-08-19) --
                    // same rule as AdminAuthorizationService.CanManageCompetitionInvoice, whose
                    // remarks explain why, and how to grant a sekretariat person access instead.
                    authorized = await _authService.IsClubAdminForClub(competitionClubId);
                }
                else
                {
                    // Region-hosted (clubId unset — the SM shape): the krets is the organiser, so it is
                    // the party entitled to chase its own unpaid entries.
                    authorized = await _authService.IsRegionHostAdminAsync(
                        competitionClubId, competition.GetValue<string>("regionalFederation"));
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

            var list = new List<(int invoiceId, string name, string email, string club, decimal amount, DateTime? last, int count, bool eligible, string reason)>();
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

                // Show what cannot be chased, with the reason spelled out, rather than omitting it:
                // a shooter missing from this list without explanation is exactly how the ghost
                // invoices stayed invisible. The row is rendered ticked-off and disabled.
                var resolved = _consolidatedService.ResolveQrAmount(invoice.Id);
                var displayAmount = resolved.Ok ? resolved.Amount : invoice.GetValue<decimal>("totalAmount");

                list.Add((invoice.Id, member.Name ?? "", member.Email, club, displayAmount, last, count,
                    resolved.Ok, resolved.Ok ? "" : resolved.Message));
            }

            var recipients = list
                // Chaseable shooters first — the ineligible rows are context, not the work.
                .OrderByDescending(r => r.eligible)
                .ThenBy(r => r.name, StringComparer.OrdinalIgnoreCase)
                .Select(r => new
                {
                    invoiceId = r.invoiceId,
                    memberName = r.name,
                    email = r.email,
                    club = r.club,
                    amount = r.amount,
                    lastReminderSentDate = r.last,
                    reminderCount = r.count,
                    eligible = r.eligible,
                    ineligibleReason = r.reason
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
                    // Skjutledare deliberately NOT accepted on the finance surface (2026-08-19) --
                    // same rule as AdminAuthorizationService.CanManageCompetitionInvoice, whose
                    // remarks explain why, and how to grant a sekretariat person access instead.
                    authorized = await _authService.IsClubAdminForClub(competitionClubId);
                }
                else
                {
                    // Region-hosted (clubId unset — the SM shape): the krets is the organiser, so it is
                    // the party entitled to chase its own unpaid entries.
                    authorized = await _authService.IsRegionHostAdminAsync(
                        competitionClubId, competition.GetValue<string>("regionalFederation"));
                }
            }
            if (!authorized) return Json(new { success = false, message = "Access denied" });

            var swishNumber = competition.GetValue<string>("swishNumber");
            if (string.IsNullOrEmpty(swishNumber))
                return Json(new { success = false, message = "Tävlingen saknar Swish-nummer." });
            var testPayee = _consolidatedService.ResolvePayee(competitionId);

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
                    // Same resolver as the real send, so the preview cannot show an amount the
                    // shooters would never be asked for. A refusal just leaves amount at 0 and
                    // falls through to the competition fee below — this is a preview, not a demand.
                    var previewResolved = _consolidatedService.ResolveQrAmount(firstPending.Id);
                    amount = previewResolved.Ok ? previewResolved.Amount : 0m;
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
                    emailMessage,     // visible reminder note in the body
                    bgNumber: testPayee.BgNumber,
                    payeeName: testPayee.Name);

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
                    // Skjutledare deliberately NOT accepted on the finance surface (2026-08-19) --
                    // same rule as AdminAuthorizationService.CanManageCompetitionInvoice, whose
                    // remarks explain why, and how to grant a sekretariat person access instead.
                    authorized = await _authService.IsClubAdminForClub(competitionClubId);
                }
                else
                {
                    // Region-hosted competition: the krets organises it, so its admins own the books.
                    // Same club-hosted-only assumption that hid region-organised competitions from the
                    // invoice filter and locked them out of receipts — an SM is region-hosted.
                    var regionCode = (competition.GetValue<string>("regionalFederation") ?? "").Trim();
                    if (!string.IsNullOrWhiteSpace(regionCode))
                        authorized = await _authService.IsRegionalAdminForRegion(regionCode);
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

            var allInvoiceNodes = invoicesHub != null
                ? _contentService.GetPagedChildren(invoicesHub.Id, 0, 1000, out _)
                    .Where(c => c.ContentType.Alias == "registrationInvoice")
                    .OrderBy(c => c.GetValue<DateTime?>("paymentDate") ?? c.GetValue<DateTime?>("createdDate") ?? DateTime.MinValue)
                    .ToList()
                : new List<IContent>();

            // A samlingsfaktura carries the SAME money as the invoices it covers, so including it here
            // would double every consolidated payment in PaidTotal/PendingTotal — and inflate allRows,
            // which NoInvoiceCount subtracts from, hiding genuinely un-invoiced registrations. Drop the
            // parents; the children carry the per-registration detail the books actually need.
            //
            // Credit notes are kept ONLY when they credit an invoice that was already PAID, i.e. money
            // genuinely went back. A credit against an UNPAID parent is a discount, not a refund: the
            // covered invoice was cancelled and no income was ever recognised, so counting it as
            // refunded would invent a repayment that never happened.
            var creditedStatusById = allInvoiceNodes.ToDictionary(
                c => c.Id, c => (c.GetValue<string>("paymentStatus") ?? "").Trim());

            var invoices = allInvoiceNodes.Where(c =>
            {
                var kind = c.GetValue<string>("invoiceKind") ?? "";
                if (kind == ConsolidatedInvoiceService.KindConsolidated) return false;
                if (kind == ConsolidatedInvoiceService.KindCreditNote)
                {
                    var creditsId = int.TryParse((c.GetValue<string>("creditsInvoiceId") ?? "").Trim(), out var cid) ? cid : 0;
                    return creditsId > 0
                        && creditedStatusById.TryGetValue(creditsId, out var st)
                        && string.Equals(st, "Paid", StringComparison.OrdinalIgnoreCase);
                }
                return true;
            }).ToList();

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

    /// <summary>Issue a credit note against a samlingsfaktura.</summary>
    public class CreditNoteRequest
    {
        public int ParentInvoiceId { get; set; }
        /// <summary>The covered invoice being credited. 0 for a free-standing amount.</summary>
        public int CreditedInvoiceId { get; set; }
        /// <summary>Explicit amount; when null the credited invoice's own total is used.</summary>
        public decimal? Amount { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>A club paying a set of invoices in one go ("samlingsfaktura").</summary>
    public class ConsolidationRequest
    {
        /// <summary>The club that will pay — not necessarily the club that issued the invoices.</summary>
        public int PayerClubId { get; set; }
        public int[]? InvoiceIds { get; set; }

        /// <summary>
        /// Sent by the organiser's samlingsfaktura on the Anmälningar tab, but the server does NOT
        /// depend on it: "one club at a time" is intrinsic to the organiser authorization branch, so it
        /// cannot be skipped by omitting a flag. An earlier cut did gate on this and left a hole —
        /// an organiser could bill an arbitrary club for invoices that were not theirs by using the
        /// legacy call shape. Kept only so the intent is visible in the request. See
        /// <c>SelectionBelongsToPayerClub</c>.
        /// </summary>
        public bool OrganiserScope { get; set; }
    }
}
