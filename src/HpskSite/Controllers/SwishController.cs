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
using HpskSite.Models;
using HpskSite.Services.Mail;
using HpskSite.Services;
using Microsoft.Extensions.Logging;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    public class HandleInvoiceChoiceRequest
    {
        [System.Text.Json.Serialization.JsonPropertyName("competitionId")]
        public int CompetitionId { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("createNew")]
        public bool CreateNew { get; set; }
        
        [System.Text.Json.Serialization.JsonPropertyName("existingInvoiceId")]
        public int? ExistingInvoiceId { get; set; }
    }
    public class SwishController : SurfaceController
    {
        /// <summary>De gamla fakturamapparna — EN väg dit (C3-planen F1). Ur RequestServices, så att
        /// konstruktorn inte ändras.</summary>
        private HpskSite.Services.LegacyInvoiceHubs InvoiceHubs =>
            HttpContext.RequestServices.GetRequiredService<HpskSite.Services.LegacyInvoiceHubs>();

        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly PaymentService _paymentService;
        private readonly EmailService _emailService;
        private readonly ReplyContactResolver _replyContacts;
        private readonly ClubService _clubService;
        private readonly InvoiceAuditService _auditService;
        private readonly ConsolidatedInvoiceService _consolidatedService;
        private readonly AdminAuthorizationService _authorizationService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<SwishController> _logger;

        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        public SwishController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            PaymentService paymentService,
            EmailService emailService,
            ReplyContactResolver replyContacts,
            ClubService clubService,
            InvoiceAuditService auditService,
            ConsolidatedInvoiceService consolidatedService,
            AdminAuthorizationService authorizationService,
            ILogger<SwishController> logger,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations,
            HpskSite.Services.Competitions.ICompetitionStore competitions)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _competitions = competitions;
            _registrations = registrations;
            _memberManager = memberManager ?? throw new ArgumentNullException(nameof(memberManager));
            _memberService = memberService ?? throw new ArgumentNullException(nameof(memberService));
            _paymentService = paymentService ?? throw new ArgumentNullException(nameof(paymentService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
            _replyContacts = replyContacts;
            _clubService = clubService ?? throw new ArgumentNullException(nameof(clubService));
            _auditService = auditService ?? throw new ArgumentNullException(nameof(auditService));
            _consolidatedService = consolidatedService ?? throw new ArgumentNullException(nameof(consolidatedService));
            _authorizationService = authorizationService ?? throw new ArgumentNullException(nameof(authorizationService));
            _databaseFactory = databaseFactory ?? throw new ArgumentNullException(nameof(databaseFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;

        /// <summary>
        /// Medlemmens aktiva anmälan till tävlingen, via anmälningslagringen (sajtstrukturen fas 1a).
        /// Medlemmen anges som text — så kommer id:t från klienten — och ett id som inte är ett tal
        /// ger ingen anmälan.
        /// </summary>
        private HpskSite.Models.Registrations.RegistrationRecord? FindActiveRegistration(int competitionId, string? memberId)
            => int.TryParse(memberId, out var id) && id > 0
                ? _registrations.ListForMemberInCompetition(competitionId, id).FirstOrDefault(r => r.IsActive)
                : null;

        // TestSimplePost / TestJsonBinding / DebugInvoiceDetection / TestRoute deleted
        // 2026-08-05: ungated test + debug endpoints with no caller anywhere.

        /// <summary>
        /// ⚠️⚠️ VÄXELN PER TÄVLING (P3/P4). En tävling i den nya modellen har inga fakturor — varje
        /// endpoint här läser eller skapar fakturor och skulle svara med en QR mot ingenting (eller,
        /// värre, ett felaktigt belopp). Den nya vägen är CompetitionFee-kontrollern; svaret säger det,
        /// så en gammal klient som når hit får ett besked i stället för en tyst felbetalning.
        /// </summary>
        private IActionResult? LedgerModelAnswer(int competitionId)
        {
            var models = HttpContext?.RequestServices?.GetService(typeof(HpskSite.Services.CompetitionFees.CompetitionPaymentModelService))
                as HpskSite.Services.CompetitionFees.CompetitionPaymentModelService;
            if (models == null) return null;
            var model = models.Get(competitionId);
            // ⚠️ Oläsbart beslut: ingen QR, ingen faktura — ett tydligt besked (Stefan 2026-10-12).
            if (model == HpskSite.Models.CompetitionFees.CompetitionPaymentModels.Unknown)
                return Json(new
                {
                    success = false,
                    paymentModel = model,
                    message = HpskSite.Models.CompetitionFees.CompetitionPaymentModels.UnknownMessage
                });
            if (model != HpskSite.Models.CompetitionFees.CompetitionPaymentModels.Ledger) return null;
            return Json(new
            {
                success = false,
                paymentModel = "ledger",
                message = "Tävlingen använder det nya betalsättet. Öppna tävlingen för att se vad som ska betalas."
            });
        }

        /// <summary>
        /// Generate Swish QR code for competition payment
        /// </summary>
        /// <param name="competitionId">Competition ID</param>
        /// <param name="targetMemberId">Optional: Target member ID (for admin registering on behalf of someone else)</param>
        [HttpGet]
        public async Task<IActionResult> GeneratePaymentQR(int competitionId, string targetMemberId = null)
        {
            if (LedgerModelAnswer(competitionId) is { } ledger) return ledger;
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Get competition details
                var umbracoContext = UmbracoContext;
                var competition = _competitions.Get(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                var swishNumber = competition.SwishNumber;
                var _feeStr = competition.RegistrationFee ?? "0";
                decimal.TryParse(_feeStr, out var registrationFee);
                var _juniorFeeStr = competition.JuniorRegistrationFee ?? "0";
                decimal.TryParse(_juniorFeeStr, out var juniorFeeConfigured);
                var _subCompFeeStr = competition.SubCompetitionFee ?? "0";
                decimal.TryParse(_subCompFeeStr, out var subCompFeeConfigured);

                _logger.LogInformation("Swish payment request - CompetitionId: {CompetitionId}, SwishNumber: {SwishNumber}, RegistrationFee: {RegistrationFee}, JuniorFee: {JuniorFee}, SubCompFee: {SubCompFee}",
                    competitionId, swishNumber, registrationFee, juniorFeeConfigured, subCompFeeConfigured);

                // Bankgiro is the alternative to Swish and lives on the organising club/krets. With a BG
                // on file the payment dialog is still useful without a Swish number — it then shows the
                // bankgiro details instead of a QR, rather than refusing to open at all.
                var payee = _consolidatedService.ResolvePayee(competitionId);
                var hasSwish = !string.IsNullOrEmpty(swishNumber);
                if (!hasSwish && string.IsNullOrEmpty(payee.BgNumber))
                {
                    return Json(new { success = false, message = "Inget Swish-nummer eller bankgiro är konfigurerat för denna tävling." });
                }

                if (registrationFee <= 0 && juniorFeeConfigured <= 0 && subCompFeeConfigured <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Get user's registrations for this competition
                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata kunde inte hittas." });
                }

                // Find user's registration for this competition (NEW: single registration per user)
                int? userRegistrationId = null;
                var userShootingClasses = new List<string>();
                string registeredMemberId = null; // The member who is registered (may differ from logged-in user if admin)
                string registeredMemberName = null;
                bool registeredIsSubCompetition = false;

                _logger.LogInformation("Looking for registration for member {MemberId} in competition {CompetitionId}", memberData.Id, competitionId);

                // Determine which member ID to search for
                var searchMemberId = !string.IsNullOrEmpty(targetMemberId) ? targetMemberId : currentMember.Id.ToString();
                _logger.LogInformation("Searching for registration for member {SearchMemberId} (targetMemberId param: '{TargetMemberId}', currentMember: {CurrentMemberId})",
                    searchMemberId, targetMemberId ?? "null", currentMember.Id);

                // Anmälan via anmälningslagringen (sajtstrukturen fas 1a) — alla anmälningar,
                // publicerade eller inte. Ersätter "publicerad cache först, sedan innehållstjänsten".
                var found = FindActiveRegistration(competitionId, searchMemberId);
                if (found != null)
                {
                    userRegistrationId = found.Id;
                    registeredMemberId = found.MemberId.ToString();
                    registeredMemberName = found.MemberName;
                    registeredIsSubCompetition = found.IsSubCompetition;
                    foreach (var cls in found.ClassIds)
                        if (!userShootingClasses.Contains(cls)) userShootingClasses.Add(cls);
                }

                if (!userRegistrationId.HasValue)
                {
                    return Json(new { success = false, message = "Du har inga aktiva anmälningar för denna tävling." });
                }

                // Classes for the deltävling-portion display below (cosmetic breakdown only).
                var classesForCalc = userShootingClasses.Count > 0
                    ? (IReadOnlyCollection<string>)userShootingClasses
                    : new[] { string.Empty }; // single non-junior bucket so baseFee applies once when class list is empty

                var memberId = registeredMemberId ?? currentMember.Id.ToString();
                var memberName = registeredMemberName ?? currentMember.Name;

                // Bill only what's still OWED, not the full fee again. Under the delta/top-up model, a
                // shooter who already paid for some classes and then added/swapped classes owes only the
                // difference. EnsureOutstandingInvoiceAsync reconciles the invoices (Paid ones are never
                // touched) and returns the single outstanding Pending invoice, creating/patching it so
                // the QR is correct even before the background reconcile has run.
                var billing = await _paymentService.EnsureOutstandingInvoiceAsync(competitionId, userRegistrationId.Value);

                _logger.LogInformation("Payment QR (delta model) - RegistrationId: {RegistrationId}, FullFee: {Full}, SumPaid: {Paid}, Outstanding: {Out}",
                    userRegistrationId.Value, billing.FullFee, billing.SumPaid, billing.Outstanding);

                if (billing.PendingInvoice == null || billing.Outstanding <= 0)
                {
                    // Nothing (more) to collect: fully paid already, or the fee dropped to/below what's
                    // been paid (a swap or removal). Any refund is handled manually by the organizer.
                    var msg = billing.SumPaid > 0
                        ? "Din anmälan är betald. Om avgiften har minskat hanteras eventuell återbetalning av arrangören."
                        : "Ingen anmälningsavgift är konfigurerad.";
                    return Json(new { success = false, message = msg });
                }

                var invoice = billing.PendingInvoice;
                var totalAmount = billing.Outstanding;
                var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                // Get the invoice number from the created invoice
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                
                // Generate QR code message
                var message = $"Betalning: {invoiceNumber}";

                // Swish QR only when the competition has a Swish number — with bankgiro only, the dialog
                // shows the BG details instead (and the invoice above was still created).
                string? qrCodeDataUri = null, swishAppUrl = null;
                if (hasSwish)
                {
                    var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                    if (!SwishQrCodeGenerator.IsValidSwishNumber(normalizedSwishNumber))
                    {
                        return Json(new { success = false, message = "Swish-numret måste vara 10 siffror — antingen en privat-/Företag-mobil som börjar med 07 (t.ex. 0701234567) eller ett Swish Handel-alias som börjar med 123 (t.ex. 1234567890)." });
                    }

                    _logger.LogInformation("Generating QR code - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}",
                        normalizedSwishNumber, amountString, message);

                    try
                    {
                        qrCodeDataUri = "data:image/png;base64," + Convert.ToBase64String(
                            SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message));
                        swishAppUrl = SwishQrCodeGenerator.GetSwishAppUrl(normalizedSwishNumber, amountString, message);
                    }
                    catch (Exception qrEx)
                    {
                        _logger.LogError(qrEx, "QR code generation failed - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}",
                            normalizedSwishNumber, amountString, message);
                        return Json(new { success = false, message = $"QR-kod generering misslyckades: {qrEx.Message}" });
                    }
                }

                var subCompPortion = RegistrationFeeCalculator.CalculateSubCompetitionPortion(
                    competition, classesForCalc, registeredIsSubCompetition);
                var subCompetitionName = competition.SubCompetitionName ?? "";

                return Json(new {
                    success = true,
                    qrCode = qrCodeDataUri,
                    swishAppUrl = swishAppUrl,
                    amount = totalAmount,
                    registrationCount = userShootingClasses.Count,
                    shootingClasses = string.Join(", ", userShootingClasses.Select(HpskSite.Models.ShootingClasses.DisplayName)),
                    invoiceId = invoice.Id,
                    invoiceNumber = invoiceNumber,
                    message = message,
                    // Bankgiro alternative — same shape every payment dialog's renderBankgiroBlock reads.
                    bgNumber = payee.BgNumber,
                    bgReference = invoiceNumber,
                    payeeName = payee.Name,
                    paymentAlreadySent = invoice.GetValue<DateTime?>("paymentSentDate").HasValue,
                    // Only surface the deltävling breakdown on a full (nothing-yet-paid) invoice — on a
                    // partial top-up the outstanding amount may be less than the deltävling portion.
                    includesSubCompetition = subCompPortion > 0 && billing.SumPaid == 0m,
                    subCompetitionName = subCompetitionName,
                    subCompetitionFeeTotal = billing.SumPaid == 0m ? subCompPortion : 0m,
                    isTopUp = billing.SumPaid > 0m,
                    fullFee = billing.FullFee,
                    alreadyPaid = billing.SumPaid
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌❌❌ EXCEPTION IN GeneratePaymentQR ❌❌❌");
                Console.WriteLine($"Exception Type: {ex.GetType().Name}");
                Console.WriteLine($"Exception Message: {ex.Message}");
                Console.WriteLine($"Stack Trace: {ex.StackTrace}");
                Console.WriteLine($"❌❌❌ EXCEPTION END ❌❌❌");
                
                _logger.LogError(ex, "Error generating Swish QR code for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Get unpaid invoices for the current user in a competition.
        /// Returns both individual and team invoices belonging to the user.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUnpaidInvoices(int competitionId)
        {
            if (LedgerModelAnswer(competitionId) is { } ledger) return ledger;
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, invoices = Array.Empty<object>() });

                var competition = _competitions.Get(competitionId);
                if (competition == null)
                    return Json(new { success = false, invoices = Array.Empty<object>() });

                var memberId = currentMember.Id.ToString();
                var feeStr = competition.RegistrationFee ?? "0";
                decimal.TryParse(feeStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var registrationFee);

                var unpaid = new List<object>();
                var hasIndividualInvoice = false;

                // Check existing invoices
                var invoicesHub = InvoiceHubs.PublishedHub(competition.Id);

                if (invoicesHub != null)
                {
                    var allInvoices = invoicesHub.Children()
                        .Where(c => c.ContentType?.Alias == "registrationInvoice")
                        .ToList();

                    foreach (var inv in allInvoices)
                    {
                        try
                        {
                            var invMemberId = inv.Value<string>("memberId") ?? "";

                            // Read paymentStatus via raw source value — the Dropdown property editor's
                            // FlexibleDropdownPropertyValueConverter crashes on plain strings like "Pending"
                            var statusProp = inv.GetProperty("paymentStatus");
                            var status = statusProp?.GetSourceValue()?.ToString()?.Trim('"', '\'', ' ') ?? "Pending";

                            // Clean status (handle JSON array format)
                            if (status.StartsWith("["))
                            {
                                try { status = System.Text.Json.JsonSerializer.Deserialize<string[]>(status)?[0] ?? "Pending"; }
                                catch { status = status.Trim('[', ']', '"', ' '); }
                            }
                            status = status.Trim('"', '\'', ' ');

                            // Track if this member has any invoice (paid or unpaid)
                            if (invMemberId == memberId)
                                hasIndividualInvoice = true;

                            if (status.Equals("Paid", StringComparison.OrdinalIgnoreCase) ||
                                status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))
                                continue;

                            // Parse amount as string first (Umbraco may store as string)
                            var amountStr = inv.Value<string>("totalAmount") ?? "0";
                            decimal.TryParse(amountStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var amount);

                            // Include individual invoices for this member
                            if (invMemberId == memberId)
                            {
                                unpaid.Add(new
                                {
                                    id = inv.Id,
                                    invoiceNumber = inv.Value<string>("invoiceNumber") ?? "",
                                    amount = amount,
                                    memberName = inv.Value<string>("memberName") ?? "",
                                    status = status,
                                    createdDate = UmbracoTime.Local(inv.CreateDate).ToString("yyyy-MM-dd"),
                                    isTeam = false,
                                    noInvoice = false
                                });
                            }
                            // Include team invoices where the user created the team
                            else if (invMemberId.StartsWith("team-"))
                            {
                                if (int.TryParse(invMemberId.Substring(5), out var teamId))
                                {
                                    using var db = _databaseFactory.CreateDatabase();
                                    var team = db.SingleOrDefault<CompetitionTeamDto>(
                                        "SELECT * FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
                                    if (team != null && team.CreatedBy.ToString() == memberId)
                                    {
                                        unpaid.Add(new
                                        {
                                            id = inv.Id,
                                            invoiceNumber = inv.Value<string>("invoiceNumber") ?? "",
                                            amount = amount,
                                            memberName = inv.Value<string>("memberName") ?? "",
                                            status = status,
                                            createdDate = UmbracoTime.Local(inv.CreateDate).ToString("yyyy-MM-dd"),
                                            isTeam = true,
                                            noInvoice = false
                                        });
                                    }
                                }
                            }
                        }
                        catch (Exception invEx)
                        {
                            _logger.LogError(invEx, "GetUnpaidInvoices: Error processing invoice {InvoiceId}", inv.Id);
                        }
                    }
                }

                // If the member is registered but has no invoice at all, include a "no invoice" entry
                if (!hasIndividualInvoice && registrationFee > 0)
                {
                    // Anmälan via anmälningslagringen (sajtstrukturen fas 1a).
                    {
                        var userReg = FindActiveRegistration(competitionId, memberId);

                        if (userReg != null)
                        {
                            var memberData = _memberService.GetById(currentMember.Key);
                            var memberName = memberData != null
                                ? $"{memberData.GetValue<string>("firstName")} {memberData.GetValue<string>("lastName")}"
                                : currentMember.Email ?? "";

                            unpaid.Add(new
                            {
                                id = 0,
                                invoiceNumber = "",
                                amount = registrationFee,
                                memberName = memberName.Trim(),
                                status = "Ej fakturerad",
                                createdDate = userReg.RegistrationDate.ToString("yyyy-MM-dd"),
                                isTeam = false,
                                noInvoice = true
                            });
                        }
                    }
                }

                return Json(new { success = true, invoices = unpaid });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting unpaid invoices for competition {CompetitionId}", competitionId);
                return Json(new { success = false, invoices = Array.Empty<object>() });
            }
        }

        /// <summary>
        /// How much a team QR should collect for <paramref name="invoiceId"/>, and whether it may be
        /// collected at all. Both team-QR endpoints go through here so the on-screen QR and the mailed
        /// QR can never disagree, and so neither of them can charge for something already being paid.
        ///
        /// Two things the fee property alone cannot know:
        /// • The club may be paying this invoice through a SAMLINGSFAKTURA. A covered child keeps
        ///   paymentStatus "Pending" (only <c>settledByInvoiceId</c> is set), so a status check does not
        ///   see it and the desk would happily hand out a second QR for money already on its way.
        ///   <see cref="PaymentService.IsCoveredByOpenConsolidation"/> is the same predicate that guards
        ///   cancelling and re-pricing such an invoice; paying it is the third case.
        /// • A kreditfaktura reduces what is owed without ever editing the issued invoice, which is why
        ///   <see cref="ConsolidatedInvoiceService.GetBalance"/> exists — and why reading `totalAmount`
        ///   directly is not enough either.
        ///
        /// The fee is kept as a FALLBACK rather than removed: if a legacy invoice has no readable
        /// amount, producing today's (possibly wrong) QR still beats leaving the registration desk with
        /// no way to take payment at all. That case is logged, because it means bad invoice data.
        /// </summary>
        private (bool ok, decimal amount, string? refusal) ResolveTeamQrAmount(int invoiceId, decimal feeFallback)
        {
            // The rule itself lives in ConsolidatedInvoiceService.ResolveQrAmount — this method used
            // to carry its own copy, and the mailed reminders never got the same treatment. Only the
            // team-specific WORDING stays here.
            var resolved = _consolidatedService.ResolveQrAmount(invoiceId, feeFallback);
            if (resolved.Ok)
                return (true, resolved.Amount, null);

            return resolved.Refusal switch
            {
                ConsolidatedInvoiceService.QrRefusal.AlreadyPaid =>
                    (false, 0m, "Lagavgiften har redan betalats."),
                ConsolidatedInvoiceService.QrRefusal.Cancelled =>
                    (false, 0m, "Fakturan är makulerad. Skapa en ny anmälan eller kontakta arrangören."),
                ConsolidatedInvoiceService.QrRefusal.NothingToCollect =>
                    (false, 0m, "Ingen lagavgift är konfigurerad."),
                _ => (false, 0m, resolved.Message)
            };
        }

        /// <summary>
        /// Generate Swish QR code for team registration payment
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GenerateTeamPaymentQR(int competitionId, int teamId)
        {
            if (LedgerModelAnswer(competitionId) is { } ledger) return ledger;
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var competition = _competitions.Get(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                // Team fees are typically paid BY THE CLUB, which normally means bankgiro — so a missing
                // Swish number must not block the payment dialog when the organiser has a BG.
                var swishNumber = competition.SwishNumber;
                var teamPayee = _consolidatedService.ResolvePayee(competitionId);
                var teamHasSwish = !string.IsNullOrEmpty(swishNumber);
                if (!teamHasSwish && string.IsNullOrEmpty(teamPayee.BgNumber))
                    return Json(new { success = false, message = "Inget Swish-nummer eller bankgiro är konfigurerat." });

                // Look up the team first so we can determine relay vs regular fee
                CompetitionTeamDto team;
                using (var db = _databaseFactory.CreateDatabase())
                {
                    team = db.SingleOrDefault<CompetitionTeamDto>(
                        "SELECT * FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
                }

                if (team == null)
                    return Json(new { success = false, message = "Laget kunde inte hittas." });

                var feeStr = (team.IsRelay ? competition.StafettRegistrationFee : competition.TeamRegistrationFee) ?? "0";
                if (!decimal.TryParse(feeStr, out var teamFee) || teamFee <= 0)
                    return Json(new { success = false, message = "Ingen lagavgift är konfigurerad." });

                var clubName = _clubService.GetClubNameById(team.ClubId) ?? "Okänd förening";

                // Team invoices are identified by memberId "team-{id}"; the mirror node link is gone (fas 1c).
                int teamRegistrationDocId = 0;

                // Check for existing team invoice
                var teamMemberId = $"team-{teamId}";
                var existingInvoice = await _paymentService.GetExistingInvoiceForMember(competitionId, teamMemberId);
                IContent? invoice = null;

                if (existingInvoice != null)
                {
                    var paymentStatusProp = existingInvoice.GetProperty("paymentStatus");
                    var paymentStatus = paymentStatusProp?.GetSourceValue()?.ToString()?.Trim('"', '\'', ' ') ?? "Pending";

                    if (paymentStatus == "Paid")
                        return Json(new { success = false, message = "Lagavgiften har redan betalats." });

                    if (paymentStatus == "Cancelled")
                    {
                        invoice = await _paymentService.CreateTeamInvoiceAsync(
                            competitionId, teamId, team.TeamName, clubName, teamFee, teamRegistrationDocId);
                    }
                    else
                    {
                        // Reuse pending invoice
                        invoice = Services.ContentService.GetById(existingInvoice.Id);
                    }
                }
                else
                {
                    invoice = await _paymentService.CreateTeamInvoiceAsync(
                        competitionId, teamId, team.TeamName, clubName, teamFee, teamRegistrationDocId);
                }

                if (invoice == null)
                    return Json(new { success = false, message = "Kunde inte skapa faktura." });

                // The amount comes from the INVOICE, not the fee property — see ResolveTeamQrAmount.
                var (amountOk, teamAmount, amountRefusal) = ResolveTeamQrAmount(invoice.Id, teamFee);
                if (!amountOk)
                    return Json(new { success = false, message = amountRefusal });

                var amountString = teamAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                var message = $"Lag: {invoiceNumber}";

                string? teamQrDataUri = null, teamSwishAppUrl = null;
                if (teamHasSwish)
                {
                    var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                    if (!SwishQrCodeGenerator.IsValidSwishNumber(normalizedSwishNumber))
                        return Json(new { success = false, message = "Ogiltigt Swish-nummer." });

                    teamQrDataUri = "data:image/png;base64," + Convert.ToBase64String(
                        SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message));
                    teamSwishAppUrl = SwishQrCodeGenerator.GetSwishAppUrl(normalizedSwishNumber, amountString, message);
                }

                return Json(new
                {
                    success = true,
                    qrCode = teamQrDataUri,
                    swishAppUrl = teamSwishAppUrl,
                    // Must be the same number the QR encodes — the dialog prints it next to the code.
                    amount = teamAmount,
                    teamName = team.TeamName,
                    teamClass = team.TeamClass,
                    clubName = clubName,
                    invoiceNumber = invoiceNumber,
                    message = message,
                    // Bankgiro alternative — the club paying for its teams normally uses this.
                    bgNumber = teamPayee.BgNumber,
                    bgReference = invoiceNumber,
                    payeeName = teamPayee.Name
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating team Swish QR for competition {CompetitionId}, team {TeamId}", competitionId, teamId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Handle user's choice for existing invoice (create new or use existing)
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> HandleExistingInvoiceChoice()
        {
            try
            {
                // Read the request body manually
                string requestBody;
                using (var reader = new StreamReader(Request.Body))
                {
                    requestBody = await reader.ReadToEndAsync();
                }
                
                _logger.LogInformation("HandleExistingInvoiceChoice called with raw body: {RequestBody}", requestBody);
                
                if (string.IsNullOrEmpty(requestBody))
                {
                    _logger.LogWarning("HandleExistingInvoiceChoice received empty request body");
                    return Json(new { success = false, message = "Ogiltig begäran - tom request body." });
                }
                
                // Parse JSON manually
                HandleInvoiceChoiceRequest request;
                try
                {
                    request = System.Text.Json.JsonSerializer.Deserialize<HandleInvoiceChoiceRequest>(requestBody);
                }
                catch (Exception jsonEx)
                {
                    _logger.LogError(jsonEx, "Failed to deserialize JSON request body: {RequestBody}", requestBody);
                    return Json(new { success = false, message = $"JSON parsing error: {jsonEx.Message}" });
                }
                
                if (request == null)
                {
                    _logger.LogWarning("HandleExistingInvoiceChoice deserialized to null request");
                    return Json(new { success = false, message = "Ogiltig begäran - request är null efter deserialisering." });
                }
                
                _logger.LogInformation("HandleExistingInvoiceChoice parsed request: CompetitionId={CompetitionId}, CreateNew={CreateNew}, ExistingInvoiceId={ExistingInvoiceId}", 
                    request.CompetitionId, request.CreateNew, request.ExistingInvoiceId);
                
                if (request.CompetitionId <= 0)
                {
                    _logger.LogWarning("HandleExistingInvoiceChoice received invalid CompetitionId: {CompetitionId}", request.CompetitionId);
                    return Json(new { success = false, message = "Ogiltig tävlings-ID." });
                }

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                if (!request.CreateNew && request.ExistingInvoiceId.HasValue)
                {
                    // Use existing invoice - get it and generate QR code
                    var umbracoContext = UmbracoContext;
                    var existingInvoice = umbracoContext.Content.GetById(request.ExistingInvoiceId.Value);
                    
                    if (existingInvoice == null)
                    {
                        return Json(new { success = false, message = "Befintlig faktura kunde inte hittas." });
                    }

                    // Verify the invoice belongs to the current user
                    string invoiceMemberId = null;
                    try
                    {
                        var memberIdProperty = existingInvoice.GetProperty("memberId");
                        if (memberIdProperty != null)
                        {
                            var rawSourceValue = memberIdProperty.GetSourceValue();
                            if (rawSourceValue != null)
                            {
                                invoiceMemberId = rawSourceValue.ToString().Trim('"', '\'', ' ');
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Error reading memberId from existing invoice {InvoiceId}, defaulting to empty", existingInvoice.Id);
                        invoiceMemberId = "";
                    }
                    
                    if (string.IsNullOrEmpty(invoiceMemberId) || invoiceMemberId != currentMember.Id.ToString())
                    {
                        return Json(new { success = false, message = "Du har inte behörighet till denna faktura." });
                    }

                    // Generate QR code for existing invoice
                    var competition = _competitions.Get(request.CompetitionId);
                    if (competition == null)
                    {
                        return Json(new { success = false, message = "Tävling kunde inte hittas." });
                    }

                    var swishNumber = competition.SwishNumber;
                    var totalAmount = existingInvoice.Value<decimal>("totalAmount");
                    var invoiceNumber = existingInvoice.Value<string>("invoiceNumber") ?? existingInvoice.Id.ToString();
                    var message = $"Betalning: {invoiceNumber}";

                    // Validate Swish number format
                    var normalizedSwishNumber = swishNumber?.Trim().Replace(" ", "").Replace("-", "") ?? "";
                    if (!SwishQrCodeGenerator.IsValidSwishNumber(normalizedSwishNumber))
                    {
                        return Json(new { success = false, message = "Swish-numret måste vara 10 siffror — antingen en privat-/Företag-mobil som börjar med 07 (t.ex. 0701234567) eller ett Swish Handel-alias som börjar med 123 (t.ex. 1234567890)." });
                    }

                    var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                    _logger.LogInformation("Generating QR code for existing invoice - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}", 
                        normalizedSwishNumber, amountString, message);

                    try
                    {
                        var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                        var qrCodeBase64 = Convert.ToBase64String(qrCodeBytes);

                        // Get shooting classes from the existing invoice's related registrations
                        var relatedRegistrationIds = existingInvoice.Value<string>("relatedRegistrationIds");
                        var shootingClasses = new List<string>();
                        
                        if (!string.IsNullOrEmpty(relatedRegistrationIds))
                        {
                            try
                            {
                                // Parse JSON array: [123, 124, 125]
                                var jsonArray = relatedRegistrationIds.Trim('[', ']');
                                if (!string.IsNullOrEmpty(jsonArray))
                                {
                                    var registrationIds = jsonArray.Split(',')
                                        .Select(id => id.Trim())
                                        .Where(id => int.TryParse(id, out _))
                                        .Select(int.Parse)
                                        .ToList();
                                    
                                    // Klasserna för de här anmälningarna, via anmälningslagringen.
                                    foreach (var regId in registrationIds)
                                    {
                                        var reg = _registrations.Get(request.CompetitionId, regId);
                                        if (reg == null) continue;
                                        foreach (var shootingClass in reg.ClassIds)
                                            if (!shootingClasses.Contains(shootingClass))
                                                shootingClasses.Add(shootingClass);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "Error parsing related registration IDs from existing invoice");
                            }
                        }

                        return Json(new { 
                            success = true, 
                            qrCode = $"data:image/png;base64,{qrCodeBase64}",
                            amount = totalAmount,
                            shootingClasses = string.Join(", ", shootingClasses.Select(HpskSite.Models.ShootingClasses.DisplayName)),
                            invoiceId = existingInvoice.Id,
                            invoiceNumber = invoiceNumber,
                            message = message,
                            usingExisting = true
                        });
                    }
                    catch (Exception qrEx)
                    {
                        _logger.LogError(qrEx, "QR code generation failed for existing invoice - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}", 
                            normalizedSwishNumber, amountString, message);
                        return Json(new { success = false, message = $"QR-kod generering misslyckades: {qrEx.Message}" });
                    }
                }
                else
                {
                    // Create new invoice - this will be handled by the normal flow
                    return Json(new { success = false, message = "Skapa ny faktura", createNew = true });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling existing invoice choice");
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        // TestInvoiceDetection deleted 2026-08-05: leaked every member's invoice numbers +
        // payment statuses for a competition to any logged-in caller. No caller anywhere.

        /// <summary>
        /// Redirect endpoint for Swish deep links (Gmail-compatible)
        /// </summary>
        [HttpGet]
        public IActionResult SwishRedirect(string payee, decimal amount, string message)
        {
            try
            {
                if (string.IsNullOrEmpty(payee) || amount <= 0 || string.IsNullOrEmpty(message))
                {
                    return BadRequest("Invalid payment parameters");
                }

                // Single source of truth for the swish:// deep-link format —
                // same helper that builds the working QR. Previously this
                // endpoint built a JSON-then-base64 payload that the Swish
                // app rejects with "Felaktig länk".
                var amountString = amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                var swishDeepLink = SwishQrCodeGenerator.GetSwishAppUrl(payee, amountString, message);

                _logger.LogInformation("Redirecting to Swish for payment - Payee: {Payee}, Amount: {Amount}", payee, amount);

                return Redirect(swishDeepLink);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating Swish redirect");
                return BadRequest("Failed to create Swish payment link");
            }
        }

        /// <summary>
        /// Get payment status for user's registrations in a competition
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPaymentStatus(int competitionId)
        {
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                // Get user's registrations for this competition
                var umbracoContext = UmbracoContext;
                var userRegistrations = new List<object>();

                // Get the competition first, then find its registrations
                var competitionForPayment = _competitions.Get(competitionId);
                if (competitionForPayment == null)
                {
                    return Json(new { success = false, message = "Tävling kunde inte hittas." });
                }

                // Medlemmens anmälningar via anmälningslagringen (sajtstrukturen fas 1a).
                if (int.TryParse(currentMember.Id, out var currentMemberIntId))
                {
                    foreach (var registration in _registrations.ListForMemberInCompetition(competitionId, currentMemberIntId)
                                 .Where(r => r.IsActive))
                    {
                        var invoiceId = registration.InvoiceId;
                        var paymentStatus = invoiceId.HasValue ? _paymentService.GetRegistrationPaymentStatus(registration.Id) : "No Invoice";

                        userRegistrations.Add(new
                        {
                            registrationId = registration.Id,
                            shootingClass = string.Join(", ", registration.ClassIds),
                            paymentStatus = paymentStatus,
                            invoiceId = invoiceId
                        });
                    }
                }

                return Json(new { success = true, registrations = userRegistrations });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting payment status for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Send Swish QR code via email to the registered member
        /// </summary>
        /// <param name="competitionId">Competition ID</param>
        /// <param name="targetMemberId">Optional: Target member ID (for admin registering on behalf of someone else)</param>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendQRCodeEmail(int competitionId, string targetMemberId = null)
        {
            if (LedgerModelAnswer(competitionId) is { } ledger) return ledger;
            try
            {
                _logger.LogInformation("SendQRCodeEmail called for CompetitionId: {CompetitionId}", competitionId);

                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                {
                    return Json(new { success = false, message = "Du måste vara inloggad." });
                }

                var memberData = _memberService.GetById(currentMember.Key);
                if (memberData == null)
                {
                    return Json(new { success = false, message = "Medlemsdata kunde inte hittas." });
                }

                // The recipient (shooter) email is resolved later, after we've decided
                // whether targetMemberId points at someone other than the logged-in user.
                // Don't bail here on missing memberData.Email — when an admin sends a QR
                // on behalf of a shooter, the admin's own email is irrelevant.

                // Get competition details
                var umbracoContext = UmbracoContext;
                var competition = _competitions.Get(competitionId);
                if (competition == null)
                {
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });
                }

                var competitionName = competition.Name;
                var swishNumber = competition.SwishNumber;
                var _feeStr = competition.RegistrationFee ?? "0";
                decimal.TryParse(_feeStr, out var registrationFee);
                var _juniorFeeStr = competition.JuniorRegistrationFee ?? "0";
                decimal.TryParse(_juniorFeeStr, out var juniorFeeConfigured);
                var _subCompFeeStr = competition.SubCompetitionFee ?? "0";
                decimal.TryParse(_subCompFeeStr, out var subCompFeeConfigured);

                if (string.IsNullOrEmpty(swishNumber))
                {
                    return Json(new { success = false, message = "Ingen Swish-nummer är konfigurerad för denna tävling." });
                }

                if (registrationFee <= 0 && juniorFeeConfigured <= 0 && subCompFeeConfigured <= 0)
                {
                    return Json(new { success = false, message = "Ingen anmälningsavgift är konfigurerad." });
                }

                // Get user's registration for this competition (single registration with multiple classes)
                int? userRegistrationId = null;
                var userShootingClasses = new List<string>();
                string targetMemberIdFromReg = null;
                string targetMemberNameFromReg = null;
                string searchMemberId = null; // Declare outside the if block so it's accessible later
                bool registeredIsSubCompetition = false;

                searchMemberId = !string.IsNullOrEmpty(targetMemberId) ? targetMemberId : currentMember.Id.ToString();

                // Anmälan via anmälningslagringen (sajtstrukturen fas 1a) — alla anmälningar,
                // publicerade eller inte. Ersätter "publicerad cache först, sedan innehållstjänsten".
                var found = FindActiveRegistration(competitionId, searchMemberId);
                if (found != null)
                {
                    userRegistrationId = found.Id;
                    targetMemberIdFromReg = found.MemberId.ToString();
                    targetMemberNameFromReg = found.MemberName;
                    registeredIsSubCompetition = found.IsSubCompetition;
                    foreach (var cls in found.ClassIds)
                        if (!userShootingClasses.Contains(cls)) userShootingClasses.Add(cls);
                }

                if (!userRegistrationId.HasValue)
                {
                    return Json(new { success = false, message = "Du har inga aktiva anmälningar för denna tävling." });
                }

                // Same billing entry point as GenerateQRCode, and for the same two reasons.
                //
                // (1) AMOUNT. This path used to recompute the FULL fee and bill that, so a shooter who
                //     had already paid for two classes and added a third got a QR for everything again.
                //     EnsureOutstandingInvoiceAsync bills only what is still owed (Paid invoices are
                //     never touched).
                // (2) DUPLICATES. Its own existence check read the PUBLISHED cache and keyed on the
                //     MEMBER rather than the registration — so it missed an invoice the eager
                //     background job had just written, and minted a second one. That is one half of
                //     the phantom-invoice pairs found in prod 2026-08-20; see the gate comment on
                //     PaymentService.CreateInvoiceAsync for the other half.
                var billing = await _paymentService.EnsureOutstandingInvoiceAsync(competitionId, userRegistrationId.Value);

                if (billing.PendingInvoice == null || billing.Outstanding <= 0)
                {
                    var nothingDue = billing.SumPaid > 0
                        ? "Anmälan är betald. Om avgiften har minskat hanteras eventuell återbetalning av arrangören."
                        : "Ingen anmälningsavgift är konfigurerad.";
                    return Json(new { success = false, message = nothingDue });
                }

                var invoice = billing.PendingInvoice;
                var totalAmount = billing.Outstanding;
                var amountString = totalAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                var invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();

                // Generate QR code message
                var message = $"Betalning: {invoiceNumber}";

                // Validate Swish number format
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                if (!SwishQrCodeGenerator.IsValidSwishNumber(normalizedSwishNumber))
                {
                    return Json(new { success = false, message = "Swish-numret måste vara 10 siffror — antingen en privat-/Företag-mobil som börjar med 07 (t.ex. 0701234567) eller ett Swish Handel-alias som börjar med 123 (t.ex. 1234567890)." });
                }

                // Generate QR code
                byte[] qrCodeBytes;
                try
                {
                    qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                }
                catch (Exception qrEx)
                {
                    _logger.LogError(qrEx, "QR code generation failed - SwishNumber: {SwishNumber}, Amount: {Amount}, Message: {Message}",
                        normalizedSwishNumber, amountString, message);
                    return Json(new { success = false, message = $"QR-kod generering misslyckades: {qrEx.Message}" });
                }

                // Resolve the actual recipient. When an admin clicks "Skicka QR via e-post"
                // on a shooter's row, targetMemberId points at the shooter — that's who
                // should get the email, not the admin. Fall back to the logged-in user
                // when targetMemberId is empty or matches them (the public self-pay path).
                string recipientEmail;
                string recipientName;
                if (!string.IsNullOrEmpty(targetMemberId)
                    && int.TryParse(targetMemberId, out var targetMemberIdInt)
                    && targetMemberIdInt != memberData.Id)
                {
                    var targetMember = _memberService.GetById(targetMemberIdInt);
                    if (targetMember == null)
                    {
                        return Json(new { success = false, message = "Mottagande skytt kunde inte hittas." });
                    }
                    if (string.IsNullOrEmpty(targetMember.Email))
                    {
                        var displayName = targetMemberNameFromReg ?? targetMember.Name ?? "skytten";
                        return Json(new { success = false, message = $"{displayName} saknar e-postadress." });
                    }
                    recipientEmail = targetMember.Email;
                    recipientName = targetMemberNameFromReg ?? targetMember.Name ?? "Medlem";
                }
                else
                {
                    if (string.IsNullOrEmpty(memberData.Email))
                    {
                        return Json(new { success = false, message = "Ingen e-postadress registrerad för ditt konto." });
                    }
                    recipientEmail = memberData.Email;
                    recipientName = currentMember.Name ?? "Medlem";
                }

                var mailPayee = _consolidatedService.ResolvePayee(competitionId);

                // Send email with QR code as inline attachment
                await _emailService.SendSwishQRCodeEmailAsync(
                    recipientEmail,
                    recipientName,
                    competitionName ?? "Tävling",
                    qrCodeBytes,
                    totalAmount,
                    string.Join(", ", userShootingClasses.Select(HpskSite.Models.ShootingClasses.DisplayName)),
                    invoiceNumber,
                    normalizedSwishNumber,
                    message,
                    // ⚠️ Svaret hör till ARRANGÖREN, och `ForCompetitionOrganiser` bär BÅDA
                    // värdformerna — en kretsarrangerad tävling har inget `clubId`.
                    _replyContacts.ForCompetitionOrganiser(competitionId),
                    customMessage: null,
                    bgNumber: mailPayee.BgNumber,   // bankgiro alternative in the mail body
                    payeeName: mailPayee.Name);

                _logger.LogInformation("Swish QR code email sent to {Email} for competition {CompetitionId}", recipientEmail, competitionId);

                // Audit: actor = the logged-in admin who triggered the send; recipient
                // captured in the notes so the history modal shows where the email
                // actually went.
                await _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.EmailSent,
                    byMemberId: memberData.Id,
                    byMemberName: memberData.Name,
                    amount: totalAmount,
                    reference: invoiceNumber,
                    notes: $"QR-kod mejlad till {recipientEmail}");

                return Json(new {
                    success = true,
                    message = $"QR-kod skickad till {recipientEmail}",
                    email = recipientEmail
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending Swish QR code email for competition {CompetitionId}", competitionId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }

        /// <summary>
        /// Send Swish QR code for team payment via email to the logged-in user
        /// </summary>
        [HttpPost]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> SendTeamQRCodeEmail(int competitionId, int teamId, int targetMemberId = 0)
        {
            if (LedgerModelAnswer(competitionId) is { } ledger) return ledger;
            try
            {
                var currentMember = await _memberManager.GetCurrentMemberAsync();
                if (currentMember == null)
                    return Json(new { success = false, message = "Du måste vara inloggad." });

                var callerData = _memberService.GetById(currentMember.Key);
                if (callerData == null)
                    return Json(new { success = false, message = "Kunde inte hitta din profil." });

                var competition = _competitions.Get(competitionId);
                if (competition == null)
                    return Json(new { success = false, message = "Tävlingen kunde inte hittas." });

                // Look up the team BEFORE reading the fee — a stafett is billed from
                // stafettRegistrationFee, not teamRegistrationFee. Reading the lag fee for a relay
                // mailed a QR for the wrong amount (and refused outright on a competition that only
                // configures a stafett fee). GenerateTeamPaymentQR has always branched here; this
                // endpoint did not, so the on-screen QR and the mailed QR could disagree.
                CompetitionTeamDto team;
                using (var db = _databaseFactory.CreateDatabase())
                {
                    team = db.SingleOrDefault<CompetitionTeamDto>(
                        "SELECT * FROM CompetitionTeam WHERE Id = @0 AND CompetitionId = @1", teamId, competitionId);
                }
                if (team == null)
                    return Json(new { success = false, message = "Laget kunde inte hittas." });

                var kindLabel = team.IsRelay ? "Stafett" : "Lag";
                var swishNumber = competition.SwishNumber;
                var feeStr = (team.IsRelay ? competition.StafettRegistrationFee : competition.TeamRegistrationFee) ?? "0";
                if (!decimal.TryParse(feeStr, out var teamFee) || teamFee <= 0 || string.IsNullOrEmpty(swishNumber))
                    return Json(new { success = false, message = "Betalning ej konfigurerad." });

                // Recipient. Default is the caller — that is right on the public page, where the club's
                // own lagledare creates the team and mails themselves the QR. At the registration desk
                // it is not: the organiser creating a late lag would mail the QR to themselves rather
                // than to the club standing at the desk. So the desk passes an explicit recipient, which
                // only someone running this competition (or a site admin) may do.
                var recipient = callerData;
                if (targetMemberId > 0 && targetMemberId != callerData.Id)
                {
                    if (!await _authorizationService.IsCurrentUserAdminAsync()
                        && !await _authorizationService.HasCompetitionStaffAccessAsync(competitionId))
                        return Json(new { success = false, message = "Ingen behörighet att skicka till en annan mottagare." });

                    recipient = _memberService.GetById(targetMemberId);
                    if (recipient == null)
                        return Json(new { success = false, message = "Mottagaren kunde inte hittas." });
                }
                if (string.IsNullOrEmpty(recipient.Email))
                    return Json(new { success = false, message = "Ingen e-postadress registrerad." });

                var clubName = _clubService.GetClubNameById(team.ClubId) ?? "Okänd förening";
                var teamMemberId = $"team-{teamId}";

                int teamRegDocId = 0; // no mirror node any more (sajtstrukturen fas 1c)

                // Get or create invoice
                var existingInvoice = await _paymentService.GetExistingInvoiceForMember(competitionId, teamMemberId);
                IContent? invoice;
                string invoiceNumber;

                if (existingInvoice != null)
                {
                    invoice = Services.ContentService.GetById(existingInvoice.Key);
                    if (invoice == null)
                        return Json(new { success = false, message = "Kunde inte hämta fakturadata." });
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                }
                else
                {
                    invoice = await _paymentService.CreateTeamInvoiceAsync(
                        competitionId, teamId, team.TeamName, clubName, teamFee, teamRegDocId);
                    if (invoice == null)
                        return Json(new { success = false, message = "Kunde inte skapa faktura." });
                    invoiceNumber = invoice.GetValue<string>("invoiceNumber") ?? invoice.Id.ToString();
                }

                // Same invoice-derived amount and same refusals as the on-screen QR — a mailed QR that
                // disagreed with the dialog would be worse than either being wrong on its own.
                var (mailAmountOk, teamAmount, mailAmountRefusal) = ResolveTeamQrAmount(invoice.Id, teamFee);
                if (!mailAmountOk)
                    return Json(new { success = false, message = mailAmountRefusal });

                var amountString = teamAmount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                // Payment reference stays "Lag: …" for a relay too — GenerateTeamPaymentQR builds it
                // that way, and the on-screen QR and the mailed QR must carry the SAME reference or
                // the organiser cannot reconcile the payment.
                var message = $"Lag: {invoiceNumber}";
                var normalizedSwishNumber = swishNumber.Trim().Replace(" ", "").Replace("-", "");
                if (!SwishQrCodeGenerator.IsValidSwishNumber(normalizedSwishNumber))
                    return Json(new { success = false, message = "Ogiltigt Swish-nummer." });

                var qrCodeBytes = SwishQrCodeGenerator.GeneratePng(normalizedSwishNumber, amountString, message);
                var mailPayee = _consolidatedService.ResolvePayee(competitionId);

                await _emailService.SendSwishQRCodeEmailAsync(
                    recipient.Email,
                    recipient.Name ?? "Medlem",
                    $"{competition.Name} - {kindLabel}: {team.TeamName}",
                    qrCodeBytes,
                    teamAmount,
                    $"{kindLabel}klass: {team.TeamClass}",
                    invoiceNumber,
                    normalizedSwishNumber,
                    message,
                    _replyContacts.ForCompetitionOrganiser(competitionId),
                    customMessage: null,
                    bgNumber: mailPayee.BgNumber,   // bankgiro alternative in the mail body
                    payeeName: mailPayee.Name);

                // Audit: log the team-invoice email send. The actor is whoever clicked (the desk
                // operator on a late registration); the recipient goes in the note.
                await _auditService.LogAsync(
                    invoiceId: invoice.Id,
                    competitionId: competitionId,
                    eventType: InvoicePaymentEventTypes.EmailSent,
                    byMemberId: callerData.Id,
                    byMemberName: callerData.Name,
                    amount: teamAmount,
                    reference: invoiceNumber,
                    notes: $"{kindLabel}-QR mejlad till {recipient.Email} ({kindLabel.ToLower()}: {team.TeamName})");

                return Json(new { success = true, email = recipient.Email, message = $"QR-kod skickad till {recipient.Email}" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending team Swish QR email for competition {CompetitionId}, team {TeamId}", competitionId, teamId);
                return Json(new { success = false, message = $"Ett fel uppstod: {ex.Message}" });
            }
        }
    }
}
