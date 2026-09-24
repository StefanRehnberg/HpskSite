using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Models.CompetitionFees;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.CompetitionFees;
using HpskSite.Services.Ledger;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Tävlingsavgifterna i den nya modellen (P3/P4) — skyttens betalning, arrangörens avprickning
    /// och fakturan till en klubb. Design: <c>notes/fakturamodellen-design-2026-09-24.md</c>.
    ///
    /// <para><b>⚠️ Tre roller, tre grindar — och ingen av dem är klientens påstående:</b></para>
    /// <list type="bullet">
    /// <item><b>Betalaren</b> — skytten på sin egen anmälan, eller en klubbadmin för anmälans klubb
    ///   ("Betald av klubben"). Får se sin avgift och PÅSTÅ att den är betald. Aldrig mer.</item>
    /// <item><b>Arrangören</b> — <see cref="AdminAuthorizationService.CanManageCompetitionFinanceAsync"/>
    ///   (tävlingsansvarig, klubbadmin för arrangerande klubb, kretsvärd; skjutledare medvetet utesluten).
    ///   Bekräftar pengar, fakturerar klubbar, krediterar.</item>
    /// <item><b>Den fakturerade klubben</b> — klubbadmin för mottagaren. Får påstå att fakturan är betald.</item>
    /// </list>
    ///
    /// <para><b>⚠️ Varje id prövas mot tävlingen det sägs höra till.</b> En betalningsrad eller faktura
    /// läses ur databasen och dess tävling jämförs, annars räcker ett giltigt id för att nå en annan
    /// förenings liggare (samma regel som evenemangens endpoints).</para>
    /// </summary>
    public class CompetitionFeeController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly AdminAuthorizationService _auth;
        private readonly CompetitionFeeService _fees;
        private readonly LedgerChargeService _charges;
        private readonly LedgerPaymentService _payments;
        private readonly CompetitionPaymentModelService _models;
        private readonly ConsolidatedInvoiceService _payees;
        private readonly ClubService _clubService;
        private readonly EmailService _email;
        private readonly IDataProtector _documentProtector;
        private readonly ILogger<CompetitionFeeController> _logger;

        public CompetitionFeeController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            IContentService contentService,
            AdminAuthorizationService auth,
            CompetitionFeeService fees,
            LedgerChargeService charges,
            LedgerPaymentService payments,
            CompetitionPaymentModelService models,
            ConsolidatedInvoiceService payees,
            ClubService clubService,
            EmailService email,
            IDataProtectionProvider dataProtection,
            ILogger<CompetitionFeeController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _contentService = contentService;
            _auth = auth;
            _fees = fees;
            _charges = charges;
            _payments = payments;
            _models = models;
            _payees = payees;
            _clubService = clubService;
            _email = email;
            _documentProtector = ClubInvoiceDocumentController.CreateProtector(dataProtection);
            _logger = logger;
        }

        private const string Denied = "Åtkomst nekad.";

        private async Task<int> MeAsync()
        {
            var m = await _memberManager.GetCurrentMemberAsync();
            return m == null || !int.TryParse(m.Id, out var id) ? 0 : id;
        }

        private Task<bool> IsOrganiserAsync(int competitionId) => _auth.CanManageCompetitionFinanceAsync(competitionId);

        /// <summary>
        /// Får den inloggade agera BETALARE på raden? Skytten själv, eller en klubbadmin för anmälans
        /// klubb — det är "Betald av klubben" (<c>payment-sent-vs-received</c>). Arrangören också.
        /// </summary>
        private async Task<bool> CanActAsPayerAsync(LedgerPayment row, int me)
        {
            if (me <= 0) return false;
            if (row.PayerMemberId == me) return true;
            if (row.PayerClubId is > 0 && await _auth.IsClubAdminForClub(row.PayerClubId.Value)) return true;
            return await IsOrganiserAsync(row.SourceId ?? 0);
        }

        private LedgerPayment? RowOnCompetition(int competitionId, int paymentId)
            => _fees.LoadFeeRows(competitionId).FirstOrDefault(r => r.Id == paymentId);

        private object PayeeJson(int competitionId)
        {
            var p = _payees.ResolvePayee(competitionId);
            return new
            {
                name = p.Name,
                swishNumber = SwishQrCodeGenerator.IsValidSwishNumber(p.SwishNumber) ? p.SwishNumber : "",
                bgNumber = p.BgNumber
            };
        }

        private static object RowJson(LedgerPayment r) => new
        {
            id = r.Id,
            itemType = r.SourceType,
            itemId = r.SourceItemId,
            part = r.FeePart,
            payerName = r.PayerName,
            payerClubId = r.PayerClubId,
            amount = r.Amount,
            settledAmount = r.SettledAmount,
            reference = CompetitionFeeService.ReferenceFor(r),
            claimed = r.ClaimedUtc,
            confirmed = r.ConfirmedUtc,
            voided = r.VoidedUtc,
            voidReason = r.VoidReason,
            coveredByChargeId = r.CoveredByChargeId,
            receiptId = r.ReceiptId,
            posted = r.JournalEntryId != null,
            method = r.Method
        };

        private static object StatusJson(FeeItemStatus s) => new
        {
            key = s.Key,
            label = FeeStatusKeys.Label(s.Key),
            fee = s.Fee,
            paid = s.Paid,
            claimed = s.Claimed,
            invoiced = s.Invoiced,
            awaitingInvoice = s.AwaitingInvoice,
            invoicePaid = s.InvoicePaid,
            open = s.Open + s.Missing,
            overpaid = s.Overpaid,
            chargeIds = s.ChargeIds
        };

        // ═════════════════════════════════ BETALAREN ═════════════════════════════════════════

        /// <summary>
        /// GET /umbraco/surface/CompetitionFee/GetMyFees — skyttens avgift på en tävling, med det som
        /// ska betalas och hur. <c>memberId</c> = någon annan (den som anmält en klubbkamrat).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyFees(int competitionId, int? memberId = null)
        {
            int me = await MeAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var model = _models.Get(competitionId);
            if (model != CompetitionPaymentModels.Ledger)
                return Json(new { success = true, model });

            var target = memberId is > 0 ? memberId.Value : me;
            var regs = _fees.LoadRegistrations(competitionId).Where(r => r.MemberId == target).ToList();

            if (target != me)
            {
                var allowed = await IsOrganiserAsync(competitionId);
                foreach (var r in regs.Where(r => r.ClubId > 0))
                    allowed = allowed || await _auth.IsClubAdminForClub(r.ClubId) || await _auth.IsSkjutledareForClub(r.ClubId);
                if (!allowed) return Json(new { success = false, message = Denied });
            }

            var competition = _contentService.GetById(competitionId);
            var settings = _fees.GetSettings(competitionId);
            var rows = _fees.LoadFeeRows(competitionId);
            var coverage = _fees.LoadCoverage(rows);
            var config = competition == null ? default : RegistrationFeeCalculator.ReadConfig(competition);

            var items = regs.Select(r =>
            {
                var myRows = rows.Where(x => x.SourceType == LedgerSourceType.CompetitionRegistration && x.SourceItemId == r.Id).ToList();
                var fee = r.Classes.Sum(c => RegistrationFeeCalculator.FeeForClass(config, c, r.IsSubCompetition))
                          + RegistrationFeeCalculator.PerRegistrationSurcharge(config, r.IsSubCompetition);
                var status = FeeItemStatus.For(fee, myRows, coverage);
                var open = myRows.Where(x => x.VoidedUtc is null && x.ConfirmedUtc is null).ToList();
                return new
                {
                    registrationId = r.Id,
                    memberName = r.MemberName,
                    classes = r.Classes,
                    clubId = r.ClubId,
                    clubName = r.ClubId > 0 ? _clubService.GetClubNameById(r.ClubId) : "",
                    status = StatusJson(status),
                    clubPays = _fees.GetClubPaysChoice(r.Id),
                    canChooseClubPays = CompetitionFeePlanner.AnyClubPayable(r.Classes, settings.ClubPayable),
                    // Det skytten själv ska betala. Klubbens del väntar på arrangörens faktura.
                    toPay = open.Where(x => x.FeePart != CompetitionFeePart.Club).Select(RowJson),
                    clubPart = open.Where(x => x.FeePart == CompetitionFeePart.Club).Select(RowJson),
                    receipts = myRows.Where(x => x.IsMoney && x.ReceiptId != null).Select(x => new { id = x.ReceiptId, amount = x.SettledAmount })
                };
            }).ToList();

            return Json(new
            {
                success = true,
                model,
                payee = PayeeJson(competitionId),
                clubPayableTypes = settings.ClubPayable,
                registrations = items
            });
        }

        /// <summary>
        /// POST SetClubPays — "Klubben betalar" för en anmälan. En HINT: klasser av tillåten typ hoppar
        /// över betalsteget och väntar på arrangörens faktura till klubben.
        /// <para>⚠️ Bara när arrangören tillåtit någon av anmälans typer. Annars är direktbetalning
        /// obligatorisk, och en skytt kan inte binda sin klubb genom att kryssa i en ruta.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetClubPays([FromBody] FeeRequest request)
        {
            int me = await MeAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var reg = _fees.LoadRegistration(request?.RegistrationId ?? 0);
            if (reg == null || reg.CompetitionId != request!.CompetitionId)
                return Json(new { success = false, message = "Anmälan hittades inte." });

            var allowed = reg.MemberId == me || await IsOrganiserAsync(reg.CompetitionId)
                          || (reg.ClubId > 0 && (await _auth.IsClubAdminForClub(reg.ClubId) || await _auth.IsSkjutledareForClub(reg.ClubId)));
            if (!allowed) return Json(new { success = false, message = Denied });

            var settings = _fees.GetSettings(reg.CompetitionId);
            if (request.ClubPays && !CompetitionFeePlanner.AnyClubPayable(reg.Classes, settings.ClubPayable))
                return Json(new { success = false, message = "Arrangören har inte tillåtit att klubben betalar för de här klasserna." });

            _fees.SetClubPaysChoice(reg.CompetitionId, reg.Id, request.ClubPays, me);
            var sync = _fees.SyncRegistration(reg.CompetitionId, reg.Id, me);
            if (sync.Error != null) return Json(new { success = false, message = sync.Error });

            return Json(new
            {
                success = true,
                message = request.ClubPays
                    ? "Noterat att klubben betalar. Arrangören skickar en faktura till klubben."
                    : "Du betalar själv."
            });
        }

        /// <summary>GET GetPaymentQr — Swish-QR:en för en avgiftsrad, byggd på servern ur radens eget belopp.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPaymentQr(int competitionId, int paymentId)
        {
            int me = await MeAsync();
            var row = RowOnCompetition(competitionId, paymentId);
            if (row == null || row.VoidedUtc != null || !await CanActAsPayerAsync(row, me)) return NotFound();

            var payee = _payees.ResolvePayee(competitionId);
            if (!SwishQrCodeGenerator.IsValidSwishNumber(payee.SwishNumber)) return NotFound();
            try
            {
                return File(SwishQrCodeGenerator.GeneratePng(payee.SwishNumber,
                    EventPaymentFormat.Amount(row.Amount), CompetitionFeeService.ReferenceFor(row)), "image/png");
            }
            catch (ArgumentException ex)
            {
                _logger.LogWarning(ex, "Swish-QR kunde inte skapas för tävling {CompetitionId}.", competitionId);
                return NotFound();
            }
        }

        /// <summary>GET GetPaymentDetails — belopp, referens, Swish-djuplänk och bankgiro för en rad.</summary>
        [HttpGet]
        public async Task<IActionResult> GetPaymentDetails(int competitionId, int paymentId)
        {
            int me = await MeAsync();
            var row = RowOnCompetition(competitionId, paymentId);
            if (row == null || !await CanActAsPayerAsync(row, me))
                return Json(new { success = false, message = "Betalningen hittades inte." });

            var payee = _payees.ResolvePayee(competitionId);
            var reference = CompetitionFeeService.ReferenceFor(row);
            var hasSwish = SwishQrCodeGenerator.IsValidSwishNumber(payee.SwishNumber);
            string? bgQr = null;
            if (BankgiroQrCodeGenerator.IsValidBankgiro(payee.BgNumber))
            {
                try
                {
                    bgQr = Convert.ToBase64String(BankgiroQrCodeGenerator.GeneratePng(
                        payee.Name, payee.BgNumber, row.Amount, reference,
                        payeeOrgNumber: payee.OrgNumber, invoiceDate: row.CreatedUtc));
                }
                catch { /* Bekvämlighet — uppgifterna står som text ändå. */ }
            }

            return Json(new
            {
                success = true,
                payment = RowJson(row),
                payeeName = payee.Name,
                swishNumber = hasSwish ? payee.SwishNumber : "",
                swishAppUrl = hasSwish ? SwishQrCodeGenerator.GetSwishAppUrl(payee.SwishNumber, EventPaymentFormat.Amount(row.Amount), reference) : null,
                qrUrl = hasSwish ? Url.Action(nameof(GetPaymentQr), "CompetitionFee", new { competitionId, paymentId }) : null,
                bgNumber = payee.BgNumber,
                bgReference = reference,
                bgQrCodeBase64 = bgQr,
                amount = row.Amount
            });
        }

        /// <summary>
        /// POST ClaimPayment — "Jag har betalat" / "Betald av klubben".
        /// <para><b>⚠️⚠️ DET HÄR ÄR INTE PENGAR.</b> Inget kvitto, ingen verifikation. Arrangören bekräftar.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClaimPayment([FromBody] FeeRequest request)
        {
            int me = await MeAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var row = RowOnCompetition(request?.CompetitionId ?? 0, request?.PaymentId ?? 0);
            if (row == null) return Json(new { success = false, message = "Betalningen hittades inte." });
            if (!await CanActAsPayerAsync(row, me)) return Json(new { success = false, message = Denied });

            var ok = _payments.RegisterClaim(row.Id, me);
            return Json(ok
                ? new { success = true, message = "Tack! Arrangören stämmer av betalningen och skickar kvitto." }
                : new { success = false, message = "Betalningen är redan anmäld, mottagen eller ersatt." });
        }

        /// <summary>POST EmailPaymentCode — mejlar betalningsuppgifterna till betalarens egen adress.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EmailPaymentCode([FromBody] FeeRequest request)
        {
            int me = await MeAsync();
            var row = RowOnCompetition(request?.CompetitionId ?? 0, request?.PaymentId ?? 0);
            if (row == null) return Json(new { success = false, message = "Betalningen hittades inte." });
            if (!await CanActAsPayerAsync(row, me)) return Json(new { success = false, message = Denied });

            var payer = _memberService.GetById(row.PayerMemberId ?? me);
            if (payer == null || string.IsNullOrWhiteSpace(payer.Email))
                return Json(new { success = false, message = "Betalaren saknar e-postadress." });

            var payee = _payees.ResolvePayee(row.SourceId ?? 0);
            var competition = _contentService.GetById(row.SourceId ?? 0);
            var sent = await _email.SendCompetitionFeeCodeAsync(
                payer.Email, payer.Name ?? "", competition?.GetValue<string>("competitionName") ?? competition?.Name ?? "",
                payee.Name, SwishQrCodeGenerator.IsValidSwishNumber(payee.SwishNumber) ? payee.SwishNumber : null,
                payee.BgNumber, row.Amount, CompetitionFeeService.ReferenceFor(row),
                competition?.GetValue<string>("contactEmail"));

            return Json(new
            {
                success = sent,
                message = sent ? $"Betalningsuppgifterna är skickade till {payer.Email}."
                               : "Mejlet kunde inte skickas. Visa uppgifterna på skärmen i stället."
            });
        }

        /// <summary>GET GetTeamFee — lagets avgift och vad som ska betalas direkt.</summary>
        [HttpGet]
        public async Task<IActionResult> GetTeamFee(int competitionId, int teamId)
        {
            int me = await MeAsync();
            var team = _fees.LoadTeam(teamId);
            if (me <= 0 || team == null || team.CompetitionId != competitionId)
                return Json(new { success = false, message = "Laget hittades inte." });

            var allowed = await IsOrganiserAsync(competitionId) || await _auth.IsClubAdminForClub(team.ClubId)
                          || IsTeamMember(teamId, me);
            if (!allowed) return Json(new { success = false, message = Denied });

            var model = _models.Get(competitionId);
            if (model != CompetitionPaymentModels.Ledger) return Json(new { success = true, model });

            _fees.SyncTeam(competitionId, teamId, me);
            var rows = _fees.LoadFeeRows(competitionId).Where(r => r.SourceType == LedgerSourceType.TeamFee && r.SourceItemId == teamId).ToList();
            var competition = _contentService.GetById(competitionId);
            var status = FeeItemStatus.For(competition == null ? 0 : CompetitionFeeService.TeamFee(competition, team.IsRelay), rows, _fees.LoadCoverage(rows));
            var open = rows.Where(r => r.VoidedUtc is null && r.ConfirmedUtc is null).ToList();

            return Json(new
            {
                success = true,
                model,
                payee = PayeeJson(competitionId),
                status = StatusJson(status),
                // "all" = ska betalas direkt; "club" = arrangören har tillåtit att laget faktureras.
                toPay = open.Where(r => r.FeePart != CompetitionFeePart.Club).Select(RowJson),
                invoiceLater = open.Where(r => r.FeePart == CompetitionFeePart.Club).Select(RowJson)
            });
        }

        private bool IsTeamMember(int teamId, int memberId)
        {
            using var db = DatabaseFactory.CreateDatabase();
            return db.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.CompetitionTeamMember WHERE TeamId = @0 AND MemberId = @1", teamId, memberId) > 0;
        }

        // ═════════════════════════════════ ARRANGÖREN ════════════════════════════════════════

        /// <summary>
        /// GET GetOverview — arrangörens avprickningslista: varje anmälan och lag med sitt läge, plus
        /// tävlingens fakturor. <b>Samma data på Anmälningar-fliken och i Ekonomi</b> (Stefans beslut).
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetOverview(int competitionId)
        {
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });

            var overview = _fees.BuildOverview(competitionId);
            var invoices = overview.Model == CompetitionPaymentModels.Ledger
                ? _charges.ListForCompetition(competitionId)
                : new List<LedgerChargeService.ChargeView>();

            return Json(new
            {
                success = true,
                model = overview.Model,
                competitionName = overview.CompetitionName,
                payee = PayeeJson(competitionId),
                settings = new
                {
                    clubPayableTypes = overview.Settings.ClubPayable,
                    allTypes = CompetitionFeeTypes.All.Select(t => new { key = t, label = CompetitionFeeTypes.Label(t) })
                },
                items = overview.Items.Select(i => new
                {
                    itemType = i.ItemType,
                    itemId = i.ItemId,
                    name = i.Name,
                    memberId = i.MemberId,
                    clubId = i.ClubId,
                    clubName = i.ClubName,
                    clubPaysHint = i.ClubPaysHint,
                    status = StatusJson(i.Status),
                    rows = i.Rows.Select(RowJson)
                }),
                totals = new
                {
                    fee = overview.Items.Sum(i => i.Status.Fee),
                    paid = overview.Items.Sum(i => i.Status.Paid + i.Status.InvoicePaid),
                    claimed = overview.Items.Sum(i => i.Status.Claimed),
                    invoiced = overview.Items.Sum(i => i.Status.Invoiced),
                    open = overview.Items.Sum(i => i.Status.Open + i.Status.Missing)
                },
                invoices = invoices.Select(InvoiceJson)
            });
        }

        private object InvoiceJson(LedgerChargeService.ChargeView v) => new
        {
            id = v.Charge.Id,
            number = v.Charge.NumberText,
            reference = v.Charge.Reference,
            recipientId = v.Charge.RecipientId,
            recipientName = v.Charge.RecipientName,
            recipientEmail = v.Charge.RecipientEmail,
            competitionId = v.Charge.SourceId,
            competitionName = v.Charge.SourceName,
            issueDate = v.Charge.IssueDate.ToString("yyyy-MM-dd"),
            dueDate = v.Charge.DueDate?.ToString("yyyy-MM-dd"),
            amount = v.Charge.Amount,
            credited = v.Balance.Credited,
            paid = v.Balance.Paid,
            claimed = v.Balance.Claimed,
            outstanding = v.Balance.Outstanding,
            settled = v.Balance.IsSettled,
            overdue = v.IsOverdue,
            voided = v.Charge.VoidedUtc,
            voidReason = v.Charge.VoidReason,
            sentUtc = v.Charge.SentUtc,
            sentTo = v.Charge.SentToEmail,
            documentUrl = DocumentUrl(v.Charge.Id),
            lines = v.Charge.Lines.Select(l => new { id = l.Id, description = l.Description, amount = l.Amount, itemType = l.ItemType, itemId = l.ItemId }),
            credits = v.Credits.Select(c => new
            {
                id = c.Id, number = c.NumberText, amount = c.Amount, note = c.Note,
                issueDate = c.IssueDate.ToString("yyyy-MM-dd"), documentUrl = DocumentUrl(c.Id)
            }),
            payments = v.Payments.Where(p => p.VoidedUtc == null).Select(p => new
            {
                id = p.Id, amount = p.SettledAmount, claimed = p.ClaimedUtc, confirmed = p.ConfirmedUtc,
                receiptId = p.ReceiptId, method = p.Method
            })
        };

        /// <summary>Länken till fakturadokumentet — med en token, så den fungerar utan inloggning.</summary>
        private string DocumentUrl(int chargeId)
            => $"{Request.Scheme}://{Request.Host}/klubbfaktura/{chargeId}?t={Uri.EscapeDataString(_documentProtector.Protect(chargeId.ToString()))}";

        /// <summary>POST SaveSettings — vilka anmälningstyper klubben får betala för.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSettings([FromBody] FeeRequest request)
        {
            var competitionId = request?.CompetitionId ?? 0;
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });
            var saved = _fees.SaveSettings(competitionId, request!.Types, await MeAsync());
            return Json(new
            {
                success = true,
                clubPayableTypes = saved.ClubPayable,
                message = saved.ClubPayable.Count == 0
                    ? "Sparat. Alla betalar direkt vid anmälan."
                    : "Sparat. Klubben får betala för: "
                      + string.Join(", ", saved.ClubPayable.Select(CompetitionFeeTypes.Label)) + "."
            });
        }

        /// <summary>POST ConfirmPayment — arrangören har sett pengarna. Kvitto + verifikation.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment([FromBody] FeeRequest request)
        {
            var competitionId = request?.CompetitionId ?? 0;
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });

            var row = RowOnCompetition(competitionId, request!.PaymentId);
            if (row == null) return Json(new { success = false, message = "Betalningen hör inte till tävlingen." });

            DateTime? when = null;
            if (!string.IsNullOrWhiteSpace(request.PaymentDate))
            {
                if (!DateTime.TryParse(request.PaymentDate, out var d))
                    return Json(new { success = false, message = "Betalningsdatumet gick inte att läsa. Ingenting sparades." });
                when = d.Date;
            }

            var result = _payments.Confirm(row.Id, await MeAsync(), when, request.ActualAmount);
            return ConfirmJson(result);
        }

        private IActionResult ConfirmJson(LedgerPaymentService.ConfirmResult result)
        {
            if (result.Error != null) return Json(new { success = false, message = result.Error });
            // ⚠️ SÄG VAD SOM FAKTISKT HÄNDE — samma tre lägen som evenemangen.
            var message = result.JournalEntryId.HasValue
                ? "Betalningen är mottagen och bokförd."
                : result.PostingSkippedReason == null
                    ? "Betalningen är mottagen och kvitterad."
                    : "Betalningen är mottagen och kvitterad, men INTE bokförd: " + result.PostingSkippedReason
                      + " Den ligger kvar i listan över betalningar att bokföra.";
            return Json(new
            {
                success = true, message, receiptId = result.ReceiptId, receiptNumber = result.ReceiptNumber,
                posted = result.JournalEntryId.HasValue
            });
        }

        /// <summary>
        /// POST RegisterPayment — arrangören tar emot en betalning på plats (disken) för en anmälan
        /// eller ett lag. Återanvänder den öppna raden; skapar en först om ingen finns.
        /// <para>⚠️ Betalsättet är inte kosmetik: kontanter och Swish landar på olika konton.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterPayment([FromBody] FeeRequest request)
        {
            var competitionId = request?.CompetitionId ?? 0;
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });
            if (!LedgerPaymentMethod.All.Contains(request!.Method ?? ""))
                return Json(new { success = false, message = "Välj hur betalningen kom in." });

            var me = await MeAsync();
            var itemType = request.ItemType == LedgerSourceType.TeamFee ? LedgerSourceType.TeamFee : LedgerSourceType.CompetitionRegistration;
            var sync = itemType == LedgerSourceType.TeamFee
                ? _fees.SyncTeam(competitionId, request.ItemId, me)
                : _fees.SyncRegistration(competitionId, request.ItemId, me);
            if (sync.Error != null) return Json(new { success = false, message = sync.Error });

            var open = _fees.LoadFeeRows(competitionId)
                .Where(r => r.SourceType == itemType && r.SourceItemId == request.ItemId
                            && r.VoidedUtc is null && r.ConfirmedUtc is null)
                .OrderBy(r => r.FeePart == CompetitionFeePart.Club)
                .ToList();
            if (request.PaymentId > 0) open = open.Where(r => r.Id == request.PaymentId).ToList();

            var row = open.FirstOrDefault();
            if (row == null) return Json(new { success = false, message = "Det finns inget obetalt att ta emot på den här anmälan." });

            var amount = request.ActualAmount is > 0 ? request.ActualAmount.Value : row.Amount;
            // Betalsättet skrivs på raden innan bekräftelsen, så kontering och kvitto följer det.
            using (var db = DatabaseFactory.CreateDatabase())
                db.Execute("UPDATE dbo.LedgerPayment SET Method = @1 WHERE Id = @0 AND ConfirmedUtc IS NULL", row.Id, request.Method);

            return ConfirmJson(_payments.Confirm(row.Id, me, null, amount));
        }

        /// <summary>POST ReversePayment — ångra. Vad det betyder avgörs av raden (liggarens regel).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ReversePayment([FromBody] FeeRequest request)
        {
            var competitionId = request?.CompetitionId ?? 0;
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });

            var row = RowOnCompetition(competitionId, request!.PaymentId);
            if (row == null) return Json(new { success = false, message = "Betalningen hör inte till tävlingen." });
            if (row.CoveredByChargeId != null)
                return Json(new { success = false, message = "Avgiften ligger på en faktura. Kreditera fakturan i stället." });

            var me = await MeAsync();
            var result = _payments.Reverse(row.Id, me, request.Reason ?? "");
            if (result.Success && row.SourceItemId is > 0)
            {
                // Ångrad betalning = avgiften är obetald igen — en ny begäran.
                if (row.SourceType == LedgerSourceType.TeamFee) _fees.SyncTeam(competitionId, row.SourceItemId.Value, me);
                else _fees.SyncRegistration(competitionId, row.SourceItemId.Value, me);
            }
            return Json(new { success = result.Success, message = result.Success ? result.Message : result.Error, outcome = result.Outcome });
        }

        /// <summary>POST Resync — räkna om alla avgifter på tävlingen (efter en avgiftsändring).</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Resync([FromBody] FeeRequest request)
        {
            var competitionId = request?.CompetitionId ?? 0;
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });
            if (!_models.IsLedger(competitionId))
                return Json(new { success = false, message = "Tävlingen använder det gamla fakturasättet." });

            var (items, voided, created) = _fees.SyncCompetition(competitionId, await MeAsync());
            return Json(new
            {
                success = true,
                message = voided + created == 0
                    ? $"Alla {items} avgifter stämmer redan."
                    : $"Omräknat: {created} nya begäranden, {voided} ersatta."
            });
        }

        // ── Fakturor till klubbar ────────────────────────────────────────────────────────────

        /// <summary>
        /// GET GetInvoiceCandidates — klubbar med öppna avgifter på tävlingen, med raderna.
        /// Påstådda rader är INTE kandidater: någon säger att de redan betalat.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetInvoiceCandidates(int competitionId)
        {
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });

            var open = _fees.LoadFeeRows(competitionId)
                .Where(r => r.VoidedUtc is null && r.ConfirmedUtc is null && r.ClaimedUtc is null && r.PayerClubId is > 0)
                .ToList();

            var hints = new HashSet<int>();
            using (var db = DatabaseFactory.CreateDatabase())
                hints = db.Fetch<int>("SELECT RegistrationId FROM dbo.CompetitionFeeChoice WHERE CompetitionId = @0 AND ClubPays = 1", competitionId).ToHashSet();

            var clubs = open.GroupBy(r => r.PayerClubId!.Value).Select(g =>
            {
                var clubNode = _contentService.GetById(g.Key);
                return new
                {
                    clubId = g.Key,
                    clubName = _clubService.GetClubNameById(g.Key) ?? $"Förening #{g.Key}",
                    email = clubNode?.GetValue<string>("contactEmail") ?? "",
                    total = g.Sum(r => r.Amount),
                    rows = g.OrderBy(r => r.SourceType == LedgerSourceType.TeamFee).ThenBy(r => r.PayerName).Select(r => new
                    {
                        id = r.Id,
                        payerName = r.PayerName,
                        itemType = r.SourceType,
                        part = r.FeePart,
                        amount = r.Amount,
                        // Förvalt ikryssat: klubbens del, lag, och den som sagt "Klubben betalar".
                        suggested = r.FeePart == CompetitionFeePart.Club || r.SourceType == LedgerSourceType.TeamFee
                                    || (r.SourceItemId is int rid && hints.Contains(rid))
                    })
                };
            }).OrderBy(c => c.clubName).ToList();

            return Json(new { success = true, clubs });
        }

        /// <summary>POST CreateInvoice — skapa (och skicka) en faktura till EN klubb. Ett steg.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateInvoice([FromBody] FeeRequest request)
        {
            var competitionId = request?.CompetitionId ?? 0;
            if (!await IsOrganiserAsync(competitionId)) return Json(new { success = false, message = Denied });

            DateTime? due = null;
            if (!string.IsNullOrWhiteSpace(request!.DueDate))
            {
                if (!DateTime.TryParse(request.DueDate, out var d))
                    return Json(new { success = false, message = "Förfallodagen gick inte att läsa." });
                due = d.Date;
            }

            var result = _charges.CreateInvoice(competitionId, request.ClubId, request.PaymentIds ?? new List<int>(),
                due, request.Note, request.Email, await MeAsync());
            if (!result.Success) return Json(new { success = false, message = result.Error });

            var charge = result.Charge!;
            var sent = false;
            if (request.Send && !string.IsNullOrWhiteSpace(request.Email))
                sent = await SendAsync(charge, request.Email!);

            return Json(new
            {
                success = true,
                chargeId = charge.Id,
                number = charge.NumberText,
                documentUrl = DocumentUrl(charge.Id),
                sent,
                message = $"Faktura {charge.NumberText} till {charge.RecipientName} på {charge.Amount:0.00} kr är skapad"
                          + (request.Send ? sent ? $" och skickad till {request.Email}." : ", men mejlet kunde INTE skickas. Skicka om den, eller skicka länken själv." : ".")
            });
        }

        private async Task<bool> SendAsync(LedgerCharge charge, string email)
        {
            var competition = _contentService.GetById(charge.SourceId);
            var sent = await _email.SendClubInvoiceAsync(email.Trim(), charge, DocumentUrl(charge.Id),
                competition?.GetValue<string>("contactEmail"));
            if (sent) _charges.MarkSent(charge.Id, email.Trim());
            return sent;
        }

        private async Task<(LedgerCharge? Charge, IActionResult? Error)> OrganiserChargeAsync(int chargeId)
        {
            var charge = _charges.Get(chargeId);
            if (charge == null) return (null, Json(new { success = false, message = "Fakturan finns inte." }));
            if (!await IsOrganiserAsync(charge.SourceId)) return (null, Json(new { success = false, message = Denied }));
            return (charge, null);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SendInvoice([FromBody] FeeRequest request)
        {
            var (charge, error) = await OrganiserChargeAsync(request?.ChargeId ?? 0);
            if (error != null) return error;
            if (string.IsNullOrWhiteSpace(request!.Email)) return Json(new { success = false, message = "Ange en e-postadress." });
            var sent = await SendAsync(charge!, request.Email);
            return Json(new { success = sent, message = sent ? $"Fakturan är skickad till {request.Email}." : "Mejlet kunde inte skickas." });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VoidInvoice([FromBody] FeeRequest request)
        {
            var (charge, error) = await OrganiserChargeAsync(request?.ChargeId ?? 0);
            if (error != null) return error;
            var result = _charges.Void(charge!.Id, request!.Reason ?? "", await MeAsync());
            return Json(new
            {
                success = result.Success,
                message = result.Success ? $"Faktura {charge.NumberText} är makulerad. Avgifterna är öppna igen." : result.Error
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreditInvoice([FromBody] FeeRequest request)
        {
            var (charge, error) = await OrganiserChargeAsync(request?.ChargeId ?? 0);
            if (error != null) return error;
            var result = _charges.Credit(charge!.Id, request!.LineIds ?? new List<int>(), request.Reason ?? "", await MeAsync());
            return Json(new
            {
                success = result.Success,
                message = result.Success ? $"Kreditnota {result.Charge!.NumberText} på {-result.Charge.Amount:0.00} kr är skapad." : result.Error,
                documentUrl = result.Success ? DocumentUrl(result.Charge!.Id) : null
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RegisterInvoicePayment([FromBody] FeeRequest request)
        {
            var (charge, error) = await OrganiserChargeAsync(request?.ChargeId ?? 0);
            if (error != null) return error;
            DateTime? when = null;
            if (!string.IsNullOrWhiteSpace(request!.PaymentDate))
            {
                if (!DateTime.TryParse(request.PaymentDate, out var d))
                    return Json(new { success = false, message = "Betalningsdatumet gick inte att läsa." });
                when = d.Date;
            }
            var amount = request.ActualAmount ?? _charges.BalanceOf(charge!).Outstanding;
            return ConfirmJson(_charges.RegisterPayment(charge!.Id, amount, when, request.Method ?? LedgerPaymentMethod.BankGiro, await MeAsync()));
        }

        // ═════════════════════════════ DEN FAKTURERADE KLUBBEN ═══════════════════════════════

        /// <summary>
        /// POST ClaimInvoicePaid — klubben säger att den betalat fakturan. Inte pengar.
        /// <para>Klubbadmin för mottagaren, eller den som har dokumentlänkens token (kassören som
        /// fick mejlet och aldrig loggat in).</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClaimInvoicePaid([FromBody] FeeRequest request)
        {
            var charge = _charges.Get(request?.ChargeId ?? 0);
            if (charge == null || charge.IsCredit) return Json(new { success = false, message = "Fakturan finns inte." });

            var me = await MeAsync();
            var viaToken = ClubInvoiceDocumentController.TokenMatches(_documentProtector, request!.Token, charge.Id);
            var allowed = viaToken || (me > 0 && await _auth.IsClubAdminForClub(charge.RecipientId));
            if (!allowed) return Json(new { success = false, message = Denied });

            var ok = _charges.RegisterClaim(charge.Id, me);
            return Json(ok
                ? new { success = true, message = "Tack! Arrangören stämmer av betalningen och skickar kvitto." }
                : new { success = false, message = "Fakturan är redan betald, anmäld eller makulerad." });
        }

        /// <summary>GET GetIncomingInvoices — fakturor TILL en klubb från arrangörer i pistol.nu.</summary>
        [HttpGet]
        public async Task<IActionResult> GetIncomingInvoices(int clubId)
        {
            if (clubId <= 0 || !await _auth.IsClubAdminForClub(clubId)) return Json(new { success = false, message = Denied });
            var list = _charges.ListForRecipient(DocumentOwnerType.Club, clubId).Where(v => !v.Charge.IsVoided).ToList();
            return Json(new { success = true, invoices = list.Select(InvoiceJson) });
        }

        public class FeeRequest
        {
            public int CompetitionId { get; set; }
            public int RegistrationId { get; set; }
            public int PaymentId { get; set; }
            public int ChargeId { get; set; }
            public int ClubId { get; set; }
            public string? ItemType { get; set; }
            public int ItemId { get; set; }
            public bool ClubPays { get; set; }
            public List<string>? Types { get; set; }
            public List<int>? PaymentIds { get; set; }
            public List<int>? LineIds { get; set; }
            public string? PaymentDate { get; set; }
            public decimal? ActualAmount { get; set; }
            public string? Method { get; set; }
            public string? Reason { get; set; }
            public string? DueDate { get; set; }
            public string? Note { get; set; }
            public string? Email { get; set; }
            public bool Send { get; set; }
            public string? Token { get; set; }
        }
    }
}
