using HpskSite.Services.Mail;
using HpskSite.Models;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.Ledger;
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

namespace HpskSite.Controllers
{
    /// <summary>
    /// Ekonomiytan för en klubb eller en krets — portalen som ersatte fliken "Fakturor".
    ///
    /// <para><b>⚠️⚠️ UTSTÄLLAREN KOMMER FRÅN ANROPET, BEHÖRIGHETEN FRÅN NODEN.</b> Varje endpoint
    /// tar <c>issuerType</c> och <c>issuerId</c> från klienten och kontrollerar dem mot
    /// innehållsträdet innan den rör liggaren. Kretsens kod läses <b>ur noden</b>, aldrig ur
    /// anropet: en klient som fick skicka sin egen <c>regionCode</c> hade kunnat läsa en annan
    /// krets ekonomi genom att byta ut den.</para>
    ///
    /// <para><b>⚠️ Grinden är en egen metod med flit.</b> Idag är svaret "klubbadmin för sin klubb,
    /// kretsadmin för sin krets" (Stefan 2026-09-21), men revisorn ska in här senare med läsrätt
    /// utan ändringsrätt och åtkomst kvar efter årets slut (P12). Ligger kontrollen utspridd i
    /// varje endpoint blir den utbyggnaden en genomsökning av hela filen i stället för en ändring
    /// på ett ställe — och en missad endpoint är en läcka, inte ett skönhetsfel.</para>
    /// </summary>
    public class EkonomiAdminController : SurfaceController
    {
        private readonly AdminAuthorizationService _authService;
        private readonly LedgerSetupService _setupService;
        private readonly LedgerPostingService _postingService;
        private readonly LedgerPaymentService _paymentService;
        private readonly LedgerOverviewService _overviewService;
        private readonly LedgerBudgetService _budgetService;
        private readonly LedgerChartService _chartService;
        private readonly LedgerManualPostingService _manualPosting;
        private readonly LedgerMembershipFeeBridge _feeBridge;
        private readonly LedgerSandboxService _sandbox;
        private readonly LedgerAccessService _access;
        private readonly LedgerBankImportService _bankService;
        private readonly LedgerClosingService _closingService;
        private readonly LedgerSieExportService _sieService;
        private readonly LedgerReceivableExportService _receivableService;
        private readonly LedgerJournalService _journalService;
        private readonly LedgerAttachmentService _attachmentService;
        private readonly LedgerAuditorService _auditorService;
        private readonly LedgerAssetService _assetService;
        private readonly LedgerExpenseService _expenseService;
        private readonly BoardRoleService _boardRoles;
        private readonly LedgerProjectService _projectService;
        private readonly LedgerProjectGroupService _projectGroups;
        private readonly LedgerAttachmentStorage _attachmentStorage;
        private readonly EmailService _emailService;

        /// <summary>
        /// Taket för ett uppladdat kontoutdrag.
        /// <para>⚠️ Ett ärligt tak, inte en gissning: ett års utdrag för en klubb är några hundra
        /// rader, alltså tiotals kilobyte. 8 MB rymmer det med marginal och stoppar samtidigt en
        /// felvald fil innan den läses in i minnet.</para>
        /// </summary>
        private const long MaxBankFileBytes = 8 * 1024 * 1024;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ILogger<EkonomiAdminController> _logger;

        public EkonomiAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            AdminAuthorizationService authService,
            LedgerSetupService setupService,
            LedgerPostingService postingService,
            LedgerPaymentService paymentService,
            LedgerOverviewService overviewService,
            LedgerBudgetService budgetService,
            LedgerChartService chartService,
            LedgerManualPostingService manualPosting,
            LedgerMembershipFeeBridge feeBridge,
            LedgerSandboxService sandbox,
            LedgerAccessService access,
            LedgerBankImportService bankService,
            LedgerClosingService closingService,
            LedgerSieExportService sieService,
            LedgerReceivableExportService receivableService,
            LedgerJournalService journalService,
            LedgerAttachmentService attachmentService,
            LedgerAuditorService auditorService,
            LedgerAssetService assetService,
            LedgerExpenseService expenseService,
            BoardRoleService boardRoles,
            LedgerProjectService projectService,
            LedgerProjectGroupService projectGroups,
            LedgerAttachmentStorage attachmentStorage,
            EmailService emailService,
            IMemberManager memberManager,
            IMemberService memberService,
            ILogger<EkonomiAdminController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _authService = authService;
            _setupService = setupService;
            _postingService = postingService;
            _paymentService = paymentService;
            _overviewService = overviewService;
            _budgetService = budgetService;
            _chartService = chartService;
            _bankService = bankService;
            _closingService = closingService;
            _sieService = sieService;
            _receivableService = receivableService;
            _journalService = journalService;
            _attachmentService = attachmentService;
            _auditorService = auditorService;
            _assetService = assetService;
            _expenseService = expenseService;
            _boardRoles = boardRoles;
            _projectService = projectService;
            _projectGroups = projectGroups;
            _attachmentStorage = attachmentStorage;
            _emailService = emailService;
            _manualPosting = manualPosting;
            _feeBridge = feeBridge;
            _sandbox = sandbox;
            _access = access;
            _memberManager = memberManager;
            _memberService = memberService;
            _logger = logger;
        }

        /// <summary>
        /// Den enda behörighetskontrollen i den här controllern. Svarar också med föreningens namn,
        /// eftersom uppslaget ändå är gjort — och för att namnet ska komma ur noden och inte ur
        /// något klienten skickat.
        /// </summary>
        private async Task<(bool ok, string name)> AuthorizeIssuerAsync(int issuerType, int issuerId)
        {
            var r = await ResolveAccessAsync(issuerType, issuerId);
            return (r.CanRead, r.OwnerName);
        }

        /// <summary>
        /// Samma uppslag, men kräver SKRIVRÄTT.
        ///
        /// <para><b>⚠️⚠️ VARJE MUTERANDE ENDPOINT MÅSTE ANVÄNDA DEN HÄR.</b> Styrelsen får läsa
        /// föreningens ekonomi men inte bokföra i den (se <see cref="LedgerAccessService"/>), och
        /// den skillnaden finns bara om skrivvägarna frågar efter den. Missas den på EN endpoint
        /// är hela delningen borta just där, och det syns inte: ytan gömmer ändå knappen, så felet
        /// upptäcks först av den som postar anropet för hand.</para>
        ///
        /// <para>⚠️ Läsande endpoints som BETJÄNAR ett formulär kräver också skrivrätt
        /// (<c>GetPostingContext</c>, <c>GetBudgetEditor</c>) — den senare SKRIVER dessutom, den
        /// skapar utkastet.</para>
        /// </summary>
        private async Task<(bool ok, string name)> AuthorizeWriteAsync(int issuerType, int issuerId)
        {
            var r = await ResolveAccessAsync(issuerType, issuerId);
            return (r.CanWrite, r.OwnerName);
        }

        private async Task<LedgerAccessResult> ResolveAccessAsync(int issuerType, int issuerId)
        {
            // ⚠️ Bara NOLL är ogiltigt. Ett negativt id är en sandlåda, inte ett fel —
            // `issuerId <= 0` hade nekat varje sandlåda och sett ut som ett behörighetsproblem.
            if (issuerId == 0) return LedgerAccessResult.None;

            // ⚠️⚠️ ETT UTSTÄLLAR-ID ÄR INTE ETT NOD-ID. Sedan sandlådorna (2026-09-22) kan
            // issuerId vara NEGATIVT, och då finns ingen nod med det numret — behörigheten
            // MÅSTE gå via utställaren för att hitta ÄGAREN. Slog vi upp noden direkt skulle
            // varje sandlåda nekas.
            // De levande utställarna har Id = nodens id, så den här omvägen är gratis för dem.
            // ⚠️ Anroparens issuerType är ett PÅSTÅENDE från klienten. Är utställaren känd vinner
            // databasens uppgift om ägaren; annars (en förening utan utställarrad ännu) faller vi
            // tillbaka på anropet, och då är issuerId ett nod-id.
            var issuer = _sandbox.GetById(issuerId);
            var ownerType = issuer?.OwnerType ?? issuerType;
            var ownerId = issuer?.OwnerId ?? issuerId;


            // ⚠️ ownerId, ALDRIG issuerId. En sandlåda har negativt id och är ingen klubb.
            return await _access.ResolveAsync(ownerType, ownerId);
        }

        /// <summary>
        /// Läser upp statusen OCH fyller i beredskapen att bokföra.
        ///
        /// <para><b>⚠️ Egen metod för att båda endpointarna ska svara likadant.</b> Först låg
        /// ifyllningen bara i <see cref="GetSetupStatus"/>, och då returnerade
        /// <see cref="SaveSetup"/> en status där <c>CanPost</c> alltid var falskt — direkt efter
        /// en lyckad uppsättning. Ytan visade alltså "kan inte bokföra" i exakt det ögonblick den
        /// skulle ha visat motsatsen, och utan angivet skäl, eftersom fältet aldrig fyllts i.
        /// Upptäckt av <c>ekonomi-setup-e2e.mjs</c> 2026-09-21.</para>
        /// </summary>
        private LedgerSetupStatus ReadStatus(int issuerType, int issuerId, string issuerName)
        {
            var status = _setupService.GetStatus(issuerType, issuerId);
            status.IssuerName = issuerName;

            // Arbetsåret, och upplysningen om dagens datum ligger utanför det.
            var working = LedgerFiscalYearPicker.Working(status.FiscalYears, DateTime.Today);
            status.WorkingYear = working?.Year;
            status.WorkingYearStart = working?.StartDate;
            status.WorkingYearEnd = working?.EndDate;
            status.PostingDateNote = LedgerFiscalYearPicker.DateNote(status.FiscalYears, DateTime.Today);

            // Beredskapen ägs av betalvägens egen spärr — vi frågar den, vi bedömer inte själva.
            // Rollmappningen lägger vi till, för den kontrollerar spärren inte.
            if (status.IsSetUp && LedgerIssuerShape.KeepsBooks(status.Shape))
            {
                // ⚠️⚠️ PRÖVAS MOT ARBETSÅRET, inte mot i dag. En förening som lagt upp 2025 fick
                // annars "Det finns inget räkenskapsår som omfattar 2026-09-22" — ett falskt larm
                // om ett år som fanns, var öppet och gick att bokföra i. Att mata in ett passerat
                // år för att jämföra med den befintliga bokföringen är vad en klubb GÖR när den
                // provar oss. Rapporterat från Hallands kretsens sandlåda 2026-09-22.
                var probe = LedgerFiscalYearPicker.ProbeDate(status.FiscalYears, DateTime.Today);
                var blocked = _postingService.PostingBlockedReason(issuerType, issuerId, probe);

                if (blocked is null && status.MissingRoles.Count > 0)
                {
                    blocked = $"{status.MissingRoles.Count} kontoroller saknar konto. "
                            + "Kör uppsättningen igen så fylls de i ur mallen.";
                }

                status.BlockedReason = blocked;
                status.CanPost = blocked is null;
            }

            return status;
        }


        /// <summary>
        /// ⚠️ Ett gemensamt nekande, och det måste rymma BÅDA fallen: den som inte har med
        /// föreningens ekonomi att göra, och styrelseledamoten som får läsa men inte skriva. Ett
        /// blankt "du har inte behörighet" till den senare är falskt — hen står och läser sidan.
        /// </summary>
        private const string DeniedMessage =
            "Du har inte behörighet till den här åtgärden. Styrelsen kan läsa föreningens ekonomi; "
            + "det är kassören som bokför.";

        /// <summary>
        /// Läser upp föreningens ekonomiuppsättning. Ytan ritar sig helt ur det här svaret, så
        /// "inte uppsatt" måste vara ett giltigt svar med 200 — inte ett fel.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSetupStatus(int issuerType, int issuerId)
        {
            var (ok, name) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                return Json(new { success = true, status = ReadStatus(issuerType, issuerId, name) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte läsa ekonomiuppsättningen för utställare {Typ}/{Id}.",
                    issuerType, issuerId);

                return Json(new { success = false, message = "Ekonomiuppgifterna gick inte att läsa just nu." });
            }
        }

        /// <summary>
        /// Ekonomiöversikten — wireframens tre paneler.
        ///
        /// <para><b>⚠️ Visas för ALLA föreningsformer.</b> Den tredje panelen ("48 anmälda, 44
        /// betalt, 4 saknas") är den enda nyttan vi tillför en klubb som redan har ett fungerande
        /// bokföringsprogram, och den frågan kan bokföringen aldrig svara på.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetOverview(int issuerType, int issuerId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var overview = _overviewService.Build(issuerType, issuerId);

                // ⚠️ Avstamningslaget for panel 2. EGEN latt fraga - inte hela avstamningen,
                //    som laser kontots alla bokforingsrader. Null = ingen har stamt av, och da
                //    star panelen kvar i sitt varnande lage.
                var rs = _bankService.Summary(issuerType, issuerId);

                return Json(new
                {
                    success = true,
                    overview,
                    reconciliation = rs is null ? null : new
                    {
                        periodTo = rs.Value.PeriodTo,
                        unmatched = rs.Value.Unmatched,
                        rows = rs.Value.Rows
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte bygga ekonomiöversikten för utställare {Typ}/{Id}.", issuerType, issuerId);

                return Json(new { success = false, message = "Översikten gick inte att läsa just nu." });
            }
        }

        // ══ VERIFIKATIONERNA OCH UNDERLAGEN ═══════════════════════════════════
        //
        // ⚠️⚠️ DEN HÄR GRUPPEN ÄR REVISIONENS RYGGRAD. Skatteverket kräver att revisorn
        //    självständigt kan följa *bokförd transaktion → verifikation → faktisk betalning* åt
        //    båda hållen. Alla tre leden fanns i databasen; det som saknades var en väg att gå dem.

        /// <summary>Verifikationslistan — i nummerordning, med bilagor och bankmatchning per rad.</summary>
        [HttpGet]
        public async Task<IActionResult> GetJournal(
            int issuerType, int issuerId,
            int? fiscalYearId = null, string? from = null, string? to = null,
            int? accountNumber = null, string? search = null,
            int skip = 0, int take = 50)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var page = _journalService.List(
                    issuerType, issuerId, fiscalYearId,
                    ParseDate(from), ParseDate(to), accountNumber, search, skip, take);

                return Json(new
                {
                    success = true,
                    total = page.TotalCount,
                    skip = page.Skip,
                    take = page.Take,
                    // ⚠️ Luckkontrollen är ett eget fält, inte en rad i listan. Numreringen påstås
                    //    vara luckfri; det påståendet är värdelöst om det inte går att pröva.
                    hasGaps = page.HasGaps,
                    series = page.Series,
                    rows = page.Rows
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Verifikationslistan gick inte att läsa för {Typ}/{Id}.", issuerType, issuerId);
                return Json(new { success = false, message = "Verifikationslistan gick inte att läsa just nu." });
            }
        }

        /// <summary>En verifikation med rader, underlag och de kontoutdragsrader som matchar den.</summary>
        [HttpGet]
        public async Task<IActionResult> GetJournalEntry(int issuerType, int issuerId, int entryId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var detail = _journalService.Detail(issuerType, issuerId, entryId);

            if (detail is null)
                return Json(new { success = false, message = "Verifikationen finns inte." });

            return Json(new
            {
                success = true,
                head = detail.Head,
                lines = detail.Lines,
                attachments = detail.Attachments,
                bankRows = detail.BankRows,
                debitTotal = detail.DebitTotal,
                creditTotal = detail.CreditTotal,
                balances = detail.Balances
            });
        }

        /// <summary>Huvudboken för ett konto — rader i datumordning med löpande saldo.</summary>
        [HttpGet]
        public async Task<IActionResult> GetAccountLedger(
            int issuerType, int issuerId, int accountNumber, string? from = null, string? to = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var status = _setupService.GetStatus(issuerType, issuerId);
            var (f, t) = ResolvePeriod(from, to, status);

            var ledger = _journalService.AccountLedger(issuerType, issuerId, accountNumber, f, t);

            if (ledger is null)
                return Json(new { success = false, message = "Huvudboken gick inte att läsa." });

            return Json(new
            {
                success = true,
                from = f,
                to = t,
                account = new { number = ledger.AccountNumber, name = ledger.AccountName },
                openingBalance = ledger.OpeningBalance,
                closingBalance = ledger.ClosingBalance,
                debitTotal = ledger.DebitTotal,
                creditTotal = ledger.CreditTotal,
                rows = ledger.Rows
            });
        }

        /// <summary>
        /// Kopplar ett underlag till en verifikation.
        ///
        /// <para>⚠️ Kräver SKRIVRÄTT. Styrelsen får läsa bokföringen men inte lagga underlag i
        /// den — och en inbjuden revisor allra minst.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadAttachment(
            int issuerType, int issuerId, int entryId, IFormFile? file)
        {
            var (ok, _) = await AuthorizeWriteAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            if (file is null || file.Length == 0)
                return Json(new { success = false, message = "Välj en fil." });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            await using var stream = file.OpenReadStream();

            var (saved, error) = await _attachmentService.AddAsync(
                issuerType, issuerId, entryId, stream, file.FileName, file.Length, actorId.Value);

            return Json(saved
                ? new { success = true, message = "Underlaget är kopplat till verifikationen." }
                : new { success = false, message = error! });
        }

        /// <summary>
        /// Strömmar ett underlag.
        ///
        /// <para>⚠️ LÄSRÄTT räcker — det är hela poängen för en revisor. Filerna ligger under
        /// <c>App_Data</c> och nås bara härifrån; en direktlänk finns inte.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAttachment(int issuerType, int issuerId, int attachmentId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Content(DeniedMessage);

            var (path, fileName, contentType) =
                _attachmentService.Resolve(issuerType, issuerId, attachmentId);

            if (path is null) return Content("Underlaget hittades inte.");

            // ⚠️ Inline, inte attachment: revisorn ska kunna bläddra genom kvitton utan att ladda
            //    ner tjugo filer. Filnamnet följer ändå med för den som sparar.
            Response.Headers.ContentDisposition =
                $"inline; filename=\"{Uri.EscapeDataString(fileName)}\"";

            return PhysicalFile(path, contentType);
        }

        /// <summary>Kopplar bort ett underlag. Raderar aldrig — se <see cref="LedgerAttachmentService"/>.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VoidAttachment([FromBody] VoidAttachmentRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att göra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            var (done, error) = _attachmentService.Void(
                request.IssuerType, request.IssuerId, request.AttachmentId,
                request.Reason ?? "", actorId.Value);

            return Json(done
                ? new { success = true, message = "Underlaget är bortkopplat." }
                : new { success = false, message = error! });
        }

        // ══ UTGIFTSSIDAN ═════════════════════════════════════════════════════════════════════
        //
        // ⚠️⚠️ ATT REGISTRERA EN UTGIFT BOKFÖR INGENTING. Föreningen bokför enligt kontantmetoden,
        //    så verifikationen skrivs vid BETALNINGEN. Endpointarna här hanterar alltså en
        //    arbetslista och en attestkedja — bokföringen sker i exakt en av dem.

        /// <summary>Utgifterna, med kontoplanen och projekten ytan behöver.</summary>
        [HttpGet]
        public async Task<IActionResult> GetExpenses(int issuerType, int issuerId, bool includeSettled = true)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var today = DateTime.Today;
            var expenses = _expenseService.List(issuerType, issuerId, today, includeSettled);
            var context = _manualPosting.BuildContext(issuerType, issuerId);

            return Json(new
            {
                success = true,
                expenses,

                // Sammanfattningen som driver rubrikerna: vad väntar på mig just nu?
                toApprove = expenses.Count(e => e.Status == LedgerExpenseStatus.Registered),
                toPay = expenses.Count(e => e.CanPay),
                toPayAmount = expenses.Where(e => e.CanPay).Sum(e => e.Amount),
                overdue = expenses.Count(e => e.IsOverdue),

                // ⚠️ Bara KOSTNADSkonton erbjuds. Ett intäktskonto här ger en resultatrapport där
                //    utgiften ökar intäkterna, och felet syns först när någon läser rapporten.
                accounts = context.Accounts
                    .Where(a => LedgerAccountClass.Of(a.Number) is >= 4 and <= 8)
                    .Select(a => new { number = a.Number, name = a.Name }),

                paymentAccounts = context.PaymentAccounts.Select(a => new { number = a.Number, name = a.Name }),

                projects = _projectService.List(issuerType, issuerId)
                    .Select(p => new { id = p.Id, name = p.Name })
            });
        }

        /// <summary>
        /// Vilka som kan ta emot ett utlägg.
        ///
        /// <para><b>⚠️⚠️ BÅDA VÄRDFORMERNA, PÅ ETT STÄLLE.</b> En klubb har MEDLEMMAR; en krets har
        /// KLUBBAR, inte medlemmar — dess utlägg görs i praktiken av dess egen styrelse. Skrivs
        /// regeln i klienten eller per anropsplats blir kretsens lista tom, och då ser
        /// utläggsformuläret trasigt ut i stället för att vara avgränsat. Den här kodbasen har
        /// skrivit värdkontrollen för hand och fått den fel fyra gånger; det här är samma familj.</para>
        ///
        /// <para>⚠️ Läses LAT, först när någon väljer "Utlägg" i formuläret. Klubbgrenen går genom
        /// hela medlemsregistret, och det ska inte ligga i varje sidladdning av ekonomiytan.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetExpensePayees(int issuerType, int issuerId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            // ⚠️ ÄGAREN, aldrig utställaren. En sandlåda har ett negativt id och inga medlemmar —
            //    dess utlägg görs av föreningens folk, precis som den riktiga liggarens. Samma
            //    uppsättning som behörigheten gör några rader upp.
            var issuer = _sandbox.GetById(issuerId);
            var ownerType = issuer?.OwnerType ?? issuerType;
            var ownerId = issuer?.OwnerId ?? issuerId;

            try
            {
                if (ownerType == (int)DocumentOwnerType.Region)
                {
                    var board = _boardRoles.GetBoardMembers(ownerType, ownerId)
                        .Select(r => r.MemberId)
                        .Distinct()
                        .Select(id => _memberService.GetById(id))
                        .Where(m => m is not null)
                        .Select(m => new { id = m!.Id, name = m.Name ?? "" })
                        .OrderBy(m => m.name)
                        .ToList();

                    return Json(new
                    {
                        success = true,
                        payees = board,
                        // ⚠️ Avgränsningen SÄGS. En lista som bara innehåller styrelsen, utan att
                        //    något förklarar varför, läser som att medlemmarna saknas.
                        note = "En krets har klubbar, inte medlemmar — listan visar kretsens styrelse."
                    });
                }

                var members = _memberService.GetAll(0, int.MaxValue, out _)
                    .Where(m => m.GetValue("primaryClubId")?.ToString() == ownerId.ToString()
                                || (m.GetValue("memberClubIds")?.ToString() ?? "")
                                    .Split(',')
                                    .Any(id => id.Trim() == ownerId.ToString()))
                    .Select(m => new { id = m.Id, name = m.Name ?? "" })
                    .OrderBy(m => m.name)
                    .ToList();

                return Json(new { success = true, payees = members, note = (string?)null });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Mottagarlistan gick inte att läsa för {Typ}/{Id}.",
                    issuerType, issuerId);

                return Json(new { success = false, message = "Mottagarlistan gick inte att läsa." });
            }
        }

        /// <summary>Lägger till eller ändrar en utgift.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveExpense([FromBody] SaveExpenseRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var date = ParseDate(request.ExpenseDate);
            if (date is null) return Json(new { success = false, message = "Ange vilket datum utgiften gäller." });

            var (saved, error, id, dropped) = _expenseService.Save(new LedgerExpense
            {
                Id = request.Id,
                IssuerType = request.IssuerType,
                IssuerId = request.IssuerId,
                Kind = request.Kind ?? LedgerExpenseKind.Utlagg,
                PayeeMemberId = request.PayeeMemberId,
                PayeeName = request.PayeeName ?? "",
                Description = request.Description ?? "",
                Amount = request.Amount,
                ExpenseDate = date.Value,
                DueDate = ParseDate(request.DueDate),
                AccountNumber = request.AccountNumber,
                // ⚠️ `!= 0`, av samma skäl som ovan: ett projekt i sandlådan har negativt id,
                //    och `> 0` hade gjort det omöjligt att märka en utgift med det — tyst.
                ProjectId = request.ProjectId != 0 ? request.ProjectId : null
            }, actorId.Value);

            return Json(saved
                ? new
                {
                    success = true,
                    id,
                    // ⚠️ En ändring som river attesten måste SÄGAS. Ett tyst "sparad" hade lämnat
                    //    kassören i tron att utgiften fortfarande var godkänd.
                    message = dropped
                        ? "Utgiften är sparad. Attesten föll eftersom beloppet eller kontot ändrades "
                          + "— den behöver godkännas på nytt."
                        : "Utgiften är sparad."
                }
                : new { success = false, id = 0, message = error! });
        }

        /// <summary>
        /// Laddar upp kvittot till en utgift.
        ///
        /// <para><b>⚠️ Filen lagras nu, men blir en bilaga till verifikationen först vid
        /// betalningen.</b> Attesten sker mot kvittot, alltså innan det finns någon verifikation
        /// att hänga det på.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UploadExpenseReceipt(
            int issuerType, int issuerId, int expenseId, IFormFile? file)
        {
            var (ok, _) = await AuthorizeWriteAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            if (file is null || file.Length == 0)
                return Json(new { success = false, message = "Välj en fil." });

            var (valid, validationError) = _attachmentStorage.Validate(file.FileName, file.Length);
            if (!valid) return Json(new { success = false, message = validationError! });

            var expense = _expenseService.Get(issuerType, issuerId, expenseId);
            if (expense is null) return Json(new { success = false, message = "Utgiften finns inte." });

            try
            {
                await using var stream = file.OpenReadStream();
                var (storedAs, size) = await _attachmentStorage.SaveAsync(stream, file.FileName);

                var (linked, error) = _expenseService.SetReceipt(
                    issuerType, issuerId, expenseId, file.FileName, storedAs, size);

                return Json(linked
                    ? new { success = true, message = "Kvittot är kopplat till utgiften." }
                    : new { success = false, message = error! });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kvittot kunde inte sparas för utgift {Id}.", expenseId);
                return Json(new { success = false, message = "Kvittot kunde inte sparas. Försök igen." });
            }
        }

        /// <summary>Visar kvittot. Läsrätt räcker — revisorn ska kunna se underlaget.</summary>
        [HttpGet]
        public async Task<IActionResult> GetExpenseReceipt(int issuerType, int issuerId, int expenseId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Content(DeniedMessage);

            var expense = _expenseService.Get(issuerType, issuerId, expenseId);
            if (expense?.ReceiptStoredAs is null) return Content("Kvittot hittades inte.");

            var path = _attachmentStorage.GetFilePath(expense.ReceiptStoredAs);
            if (path is null) return Content("Kvittot hittades inte på disk.");

            // ⚠️ Inline, inte attachment: attestanten ska kunna titta på kvittot utan att ladda ner
            //    det. Samma val som verifikationernas underlag.
            Response.Headers.ContentDisposition =
                $"inline; filename=\"{Uri.EscapeDataString(expense.ReceiptFileName ?? "kvitto")}\"";

            return PhysicalFile(path, LedgerAttachmentStorage.ContentTypeFor(expense.ReceiptStoredAs));
        }

        /// <summary>Attesterar en utgift.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ApproveExpense([FromBody] ExpenseActionRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att attestera." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (approved, error, bySelf) = _expenseService.Approve(
                request.IssuerType, request.IssuerId, request.ExpenseId, actorId.Value, request.Text);

            return Json(approved
                ? new
                {
                    success = true,
                    bySelf,
                    // ⚠️ Varningen om att man attesterat sin egen registrering SÄGS, men stoppar
                    //    ingenting. En liten förening har ofta en enda person, och en spärr där
                    //    hade gjort funktionen oanvändbar för just dem.
                    message = bySelf
                        ? "Utgiften är attesterad. Du registrerade den själv, och det noteras på "
                          + "verifikationen — be gärna någon annan i styrelsen attestera nästa gång."
                        : "Utgiften är attesterad och kan betalas."
                }
                : new { success = false, bySelf = false, message = error! });
        }

        /// <summary>Avvisar en utgift. Skälet är obligatoriskt.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RejectExpense([FromBody] ExpenseActionRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att avvisa." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (rejected, error) = _expenseService.Reject(
                request.IssuerType, request.IssuerId, request.ExpenseId, actorId.Value, request.Text ?? "");

            return Json(rejected
                ? new { success = true, message = "Utgiften är avvisad." }
                : new { success = false, message = error! });
        }

        /// <summary>
        /// Betalar ut en utgift och bokför den.
        ///
        /// <para><b>⚠️ Det är HÄR verifikationen skrivs</b> — och bokföringsdatumet är
        /// betaldatumet, inte fakturadatumet. Det är hela kontantmetoden.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PayExpense([FromBody] PayExpenseRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att betala." });

            var (ok, name) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var paid = ParseDate(request.PaidDate);
            if (paid is null) return Json(new { success = false, message = "Ange vilket datum utgiften betalades." });

            var (done, error, entryId) = _expenseService.Pay(
                request.IssuerType, request.IssuerId, request.ExpenseId,
                paid.Value, request.PaymentAccountNumber, actorId.Value);

            if (done)
            {
                _logger.LogInformation(
                    "Ekonomi: {Forening} betalade utgift {Id} och bokförde den som {Entry}.",
                    name, request.ExpenseId, entryId);
            }

            return Json(done
                ? new { success = true, entryId, message = "Utgiften är betald och bokförd." }
                : new { success = false, entryId, message = error! });
        }

        // ══ PROJEKTEN ════════════════════════════════════════════════════════════════════════
        //
        // ⚠️⚠️ MARKERA FINAST, GRUPPERA EFTERÅT. Tävlingarnas och evenemangens projekt föds av sig
        //    själva vid första kronan (LedgerPostingService.Post). Här skapar kassören sina EGNA
        //    projekt (Klubbstugan, Ungdomssektionen) och grupperar — och grupperingen rör aldrig en
        //    bokförd rad, så den får ändras hur ofta som helst.
        //
        // ⚠️ Varje id i en skrivbegäran prövas mot FÖRENINGEN innan något ändras. Projekt- och
        //    grupp-id kommer från klienten, och tjänsterna tar dem som de är.

        /// <summary>
        /// Projekten med utfall, grupperna med sina summor, och räkenskapsåren att filtrera på.
        ///
        /// <para>⚠️ LÄSRÄTT räcker. Styrelsen och revisorn ska kunna se vad tävlingarna gav.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetProjects(int issuerType, int issuerId, int? fiscalYearId = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                // ⚠️ Ett år som inte hör till föreningen är ett påhittat filter — ignorera det
                //    hellre än att visa nollor som ser ut som ett svar.
                var years = _setupService.GetStatus(issuerType, issuerId).FiscalYears;
                if (fiscalYearId is int fy && years.All(y => y.Id != fy)) fiscalYearId = null;

                var summaries = _projectService.Summarise(issuerType, issuerId, fiscalYearId);
                var groups = _projectGroups.List(issuerType, issuerId);
                var members = _projectGroups.Members(issuerType, issuerId);

                var report = LedgerProjectGroupReport.Build(groups, members,
                    summaries.Select(s => new ProjectFigures
                    {
                        ProjectId = s.ProjectId, Name = s.ProjectName, Income = s.Income, Costs = s.Costs
                    }));

                var groupsOf = members
                    .GroupBy(m => m.ProjectId)
                    .ToDictionary(g => g.Key, g => g.Select(m => m.GroupId).ToList());

                // ⚠️ Beskrivning och datum följer med, fast summeringen inte behöver dem:
                //    redigeringen skriver alla fält, och ett formulär som förifylls utan dem hade
                //    TÖMT beskrivningen och datumen vid varje namnbyte.
                var details = _projectService.List(issuerType, issuerId, includeClosed: true)
                    .ToDictionary(p => p.Id);

                return Json(new
                {
                    success = true,
                    fiscalYearId,
                    fiscalYears = years.OrderByDescending(y => y.Year)
                                       .Select(y => new { id = y.Id, year = y.Year }),
                    projects = summaries.Select(s => new
                    {
                        id = s.ProjectId,
                        name = s.ProjectName,
                        isClosed = s.IsClosed,
                        description = details.TryGetValue(s.ProjectId, out var dp) ? dp.Description : null,
                        startDate = dp?.StartDate?.ToString("yyyy-MM-dd"),
                        endDate = dp?.EndDate?.ToString("yyyy-MM-dd"),
                        // ⚠️ Vad projektet ÄR, i klartext — ett automatiskt projekt ska gå att
                        //    skilja från kassörens egna utan att man läser databasen.
                        sourceKind = s.SourceType,
                        sourceLabel = s.SourceType switch
                        {
                            LedgerProjectSource.Competition => "Tävling",
                            LedgerProjectSource.Event => "Evenemang",
                            _ => null
                        },
                        income = s.Income,
                        costs = s.Costs,
                        net = s.Net,
                        entryCount = s.EntryCount,
                        groupIds = groupsOf.TryGetValue(s.ProjectId, out var gids) ? gids : new List<int>()
                    }),
                    // ⚠️⚠️ INGEN TOTALSUMMA ÖVER GRUPPER, och det är strukturellt, inte en
                    //    försummelse. Grupper får överlappa; en summa över dem vore ett tal som
                    //    inte finns. Varje grupp bär i stället sina överlapp.
                    groups = report.Select(g => new
                    {
                        id = g.GroupId,
                        name = g.Name,
                        description = g.Description,
                        isFromSeries = g.IsFromSeries,
                        projectIds = g.ProjectIds,
                        income = g.Income,
                        costs = g.Costs,
                        net = g.Net,
                        overlaps = g.Overlaps.Select(o => new
                        {
                            groupId = o.GroupId, groupName = o.GroupName, sharedProjects = o.SharedProjects
                        })
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Projekten gick inte att läsa för {Typ}/{Id}.", issuerType, issuerId);
                return Json(new { success = false, message = "Projekten gick inte att läsa just nu." });
            }
        }

        /// <summary>Skapar eller ändrar ett projekt. Historiken följer med ett namnbyte.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveProject([FromBody] SaveProjectRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var start = ParseDate(request.StartDate);
            var end = ParseDate(request.EndDate);

            // ⚠️ `== 0` är ett NYTT projekt. Ett sandlådeprojekt har negativt id och är ett
            //    befintligt — `<= 0` hade skapat en dubblett vid varje sparning i sandlådan.
            if (request.Id == 0)
            {
                var created = _projectService.Create(request.IssuerType, request.IssuerId,
                    request.Name ?? "", actorId.Value, request.Description, start, end);

                return Json(created.Success
                    ? new { success = true, id = created.Project!.Id, message = "Projektet är skapat." }
                    : (object)new { success = false, message = created.Error });
            }

            if (!OwnsProject(request.Id, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Projektet hittades inte." });

            var updated = _projectService.Update(request.Id, request.Name ?? "", request.Description, start, end);

            return Json(updated.Success
                ? new { success = true, id = request.Id, message = "Projektet är sparat. Alla bokförda rader följer med." }
                : (object)new { success = false, message = updated.Error });
        }

        /// <summary>
        /// Stänger eller öppnar ett projekt. Stängt = visas inte i väljarna; rapporterna läser det
        /// ändå, och projekt raderas aldrig.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetProjectClosed([FromBody] ProjectClosedRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att göra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            if (!OwnsProject(request.ProjectId, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Projektet hittades inte." });

            var result = _projectService.SetClosed(request.ProjectId, request.Closed);

            return Json(result.Success
                ? new
                {
                    success = true,
                    message = request.Closed
                        ? "Projektet är stängt. Det syns inte längre när någon bokför, men finns kvar i rapporterna."
                        : "Projektet är öppet igen."
                }
                : (object)new { success = false, message = result.Error });
        }

        /// <summary>Skapar eller döper om en grupp.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveProjectGroup([FromBody] SaveProjectGroupRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            if (request.Id == 0)
            {
                var created = _projectGroups.Create(request.IssuerType, request.IssuerId,
                    request.Name ?? "", actorId.Value, request.Description);

                return Json(created.Success
                    ? new { success = true, id = created.Group!.Id, message = "Gruppen är skapad. Lägg till projekt i den." }
                    : (object)new { success = false, message = created.Error });
            }

            if (!OwnsGroup(request.Id, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Gruppen hittades inte." });

            var updated = _projectGroups.Update(request.Id, request.Name ?? "", request.Description);

            return Json(updated.Success
                ? new { success = true, id = request.Id, message = "Gruppen är sparad." }
                : (object)new { success = false, message = updated.Error });
        }

        /// <summary>Raderar en grupp. Projekten och deras bokföring rörs inte.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteProjectGroup([FromBody] ProjectGroupRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att göra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            if (!OwnsGroup(request.GroupId, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Gruppen hittades inte." });

            var done = _projectGroups.Delete(request.GroupId);

            return Json(done
                ? new { success = true, message = "Gruppen är borttagen. Projekten och deras bokföring är orörda." }
                : new { success = false, message = "Gruppen gick inte att ta bort." });
        }

        /// <summary>Lägger till eller tar bort ett projekt ur en grupp.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetProjectGroupMember([FromBody] ProjectGroupMemberRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att göra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            // ⚠️ BÅDA måste höra till föreningen. Annars kunde en annan förenings projekt hamna i
            //    vår grupp och dess siffror synas här.
            if (!OwnsGroup(request.GroupId, request.IssuerType, request.IssuerId)
                || !OwnsProject(request.ProjectId, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Gruppen eller projektet hittades inte." });

            _projectGroups.SetMember(request.GroupId, request.ProjectId, request.Member, actorId.Value);

            return Json(new { success = true });
        }

        private bool OwnsProject(int projectId, int issuerType, int issuerId)
        {
            if (projectId == 0) return false;
            var p = _projectService.Get(projectId);
            return p is not null && p.IssuerType == issuerType && p.IssuerId == issuerId;
        }

        private bool OwnsGroup(int groupId, int issuerType, int issuerId)
        {
            if (groupId == 0) return false;
            var g = _projectGroups.Get(groupId);
            return g is not null && g.IssuerType == issuerType && g.IssuerId == issuerId;
        }

        // ══ ANLÄGGNINGSREGISTRET ═════════════════════════════════════════════════════════════
        //
        // ⚠️⚠️ REGISTRET ÄR DEN ENDA PLATS DÄR ANSKAFFNINGSVÄRDET FINNS KVAR. Avskrivningen
        //    bokförs direkt mot tillgångskontot, så liggaren visar NETTO — och efter några år
        //    vet bokföringen inte längre vad pjäsen kostade. Därför utrangeras en rad, aldrig
        //    raderas.

        /// <summary>Registret för ett räkenskapsår, med plan och utfall per tillgång.</summary>
        [HttpGet]
        public async Task<IActionResult> GetAssets(int issuerType, int issuerId, int? year = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var fy = ResolveFiscalYear(issuerType, issuerId, year);

            if (fy is null)
                return Json(new { success = true, hasFiscalYear = false, assets = Array.Empty<object>() });

            var assets = _assetService.List(issuerType, issuerId, fy.StartDate, fy.EndDate);

            return Json(new
            {
                success = true,
                hasFiscalYear = true,
                year = fy.Year,
                from = fy.StartDate,
                to = fy.EndDate,
                assets,
                // Sammanfattningen som driver knappen: vad är kvar att bokföra för året?
                toPost = assets.Where(a => a.NeedsPosting).Sum(a => a.Remaining),
                toPostCount = assets.Count(a => a.NeedsPosting),
                bookValueTotal = assets.Where(a => !a.IsDisposed).Sum(a => a.BookValue)
            });
        }

        /// <summary>Lägger till eller ändrar en tillgång.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAsset([FromBody] SaveAssetRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var date = ParseDate(request.InUseDate);
            if (date is null) return Json(new { success = false, message = "Ange när tillgången togs i bruk." });

            var (saved, error, _) = _assetService.Save(new LedgerAsset
            {
                Id = request.Id,
                IssuerType = request.IssuerType,
                IssuerId = request.IssuerId,
                Name = request.Name ?? "",
                Note = request.Note,
                AssetAccountNumber = request.AssetAccountNumber,
                DepreciationAccountNumber = request.DepreciationAccountNumber,
                InUseDate = date.Value,
                AcquisitionAmount = request.AcquisitionAmount,
                UsefulLifeYears = request.UsefulLifeYears,
                ResidualValue = request.ResidualValue
            }, actorId.Value);

            return Json(saved
                ? new { success = true, message = "Tillgången är sparad." }
                : new { success = false, message = error! });
        }

        /// <summary>Utrangerar en tillgång — raden står kvar.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DisposeAsset([FromBody] DisposeAssetRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att göra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var when = ParseDate(request.When) ?? DateTime.Today;

            var (done, error) = _assetService.Dispose(
                request.IssuerType, request.IssuerId, request.AssetId,
                when, request.Reason ?? "", actorId.Value);

            return Json(done
                ? new
                {
                    success = true,
                    // ⚠️⚠️ SLUTBOKFÖRINGEN GÖRS INTE ÅT DEM, och det SÄGS. En försäljning ger en
                    //    intäkt och ett restvärde som ska bort — belopp vi inte känner. Att tyst
                    //    hoppa över det hade lämnat ett värde kvar på kontot som ingen letar efter.
                    message = "Tillgången är utrangerad och skrivs inte av längre. "
                            + "Finns ett bokfört värde kvar, eller såldes den, bokför du det själv "
                            + "under Bokför."
                }
                : new { success = false, message = error! });
        }

        /// <summary>Bokför årets avskrivningar — en verifikation per tillgång.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostDepreciation([FromBody] PostDepreciationRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att bokföra." });

            var (ok, name) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null) return Json(new { success = false, message = "Du måste vara inloggad." });

            var fy = ResolveFiscalYear(request.IssuerType, request.IssuerId, request.Year);
            if (fy is null) return Json(new { success = false, message = "Lägg upp räkenskapsåret först." });

            var (posted, amount, problems) = _assetService.PostYear(
                request.IssuerType, request.IssuerId, fy.StartDate, fy.EndDate, actorId.Value);

            _logger.LogInformation(
                "Ekonomi: {Forening} bokförde {Antal} avskrivningar på {Belopp} kr för {Ar}.",
                name, posted, amount, fy.Year);

            return Json(new
            {
                success = posted > 0 || problems.Count == 0,
                posted,
                amount,
                // ⚠️ Problemen SÄGS, aldrig sväljs. En tillgång som inte gick att bokföra är
                //    exakt det som gör bokslutet fel, och den som klickade måste få veta vilken.
                problems,
                message = posted == 0 && problems.Count == 0
                    ? "Ingenting återstod att bokföra för året."
                    : $"{posted} avskrivningar bokförda, {amount:N0} kr."
            });
        }

        // ══ REVISORNS ÅTKOMST ════════════════════════════════════════════════════════════════
        //
        // ⚠️⚠️ ÄGAREN, ALDRIG UTSTÄLLAREN. Man är revisor för en FÖRENING, inte för en sandlåda.
        //    Endpointarna tar därför ownerType/ownerId och grindar på skrivrätt i den föreningen
        //    — den som får bokföra får också bjuda in, och inbjudan loggas med vem och när.

        /// <summary>Föreningens revisorer — inklusive återkallade och utgångna.</summary>
        [HttpGet]
        public async Task<IActionResult> GetAuditors(int ownerType, int ownerId)
        {
            var (ok, _) = await AuthorizeWriteAsync(ownerType, ownerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var rows = _auditorService.ListForOwner(ownerType, ownerId);

            return Json(new
            {
                success = true,
                // ⚠️ TokenHash lämnar ALDRIG servern. Den är inte hemlig i sig, men den har inget
                //    här att göra och ett fält som finns blir förr eller senare använt.
                auditors = rows.Select(r => new
                {
                    id = r.Id,
                    name = r.Name,
                    email = r.Email,
                    status = r.StatusLabel,
                    isActive = r.IsActive,
                    invitedUtc = r.InvitedUtc,
                    expiresUtc = r.ExpiresUtc,
                    acceptedUtc = r.AcceptedUtc,
                    lastSeenUtc = r.LastSeenUtc,
                    revokeReason = r.RevokeReason
                })
            });
        }

        /// <summary>
        /// Bjuder in en revisor och mejlar länken.
        ///
        /// <para><b>⚠️ Token visas EN gång i svaret.</b> Går mejlet inte fram ska föreningen kunna
        /// lämna länken på annat sätt — men den lagras bara som hash, så den kan aldrig hämtas
        /// igen. Svaret säger det.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> InviteAuditor([FromBody] InviteAuditorRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att skicka." });

            var (ok, ownerName) = await AuthorizeWriteAsync(request.OwnerType, request.OwnerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, actorName) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            var (made, error, token, grantId) = _auditorService.Invite(
                request.OwnerType, request.OwnerId,
                request.Email ?? "", request.Name ?? "", actorId.Value);

            if (!made) return Json(new { success = false, message = error! });

            var url = $"{Request.Scheme}://{Request.Host}/revision/inbjudan?t={token}";
            var expires = DateTime.UtcNow.AddMonths(LedgerAuditorService.GrantMonths).ToLocalTime();

            // ⚠️⚠️ SVARSADRESSEN ÄR FÖRENINGEN, inte sajten. En revisor som svarar på inbjudan
            //    har en fråga till den som utsett hen — inte till oss. Reply-To pekar därför på
            //    den som bjöd in.
            //    ⚠️ Saknas adressen faller vi tillbaka på sajtens, UTTRYCKLIGEN. Ett tomt
            //    Reply-To hade tyst gjort mejlet obesvarbart, vilket är sämre än fel mottagare.
            var actorEmail = await CurrentActorEmailAsync();

            var replyTo = string.IsNullOrWhiteSpace(actorEmail)
                ? MailReplyTo.SiteAdmin
                : MailReplyTo.To(actorEmail, actorName ?? ownerName);

            bool sent;
            try
            {
                sent = await _emailService.SendAuditorInviteAsync(
                    request.Email!.Trim(), request.Name!.Trim(), ownerName, url, expires, replyTo);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Revisorsinbjudan {Id} gick inte att mejla.", grantId);
                sent = false;
            }

            _logger.LogInformation(
                "Ekonomi: {Forening} bjöd in {Revisor} som revisor (uppdrag {Id}, mejl {Mejl}).",
                ownerName, request.Email, grantId, sent ? "skickat" : "MISSLYCKADES");

            return Json(new
            {
                success = true,
                sent,
                // ⚠️ Länken returneras så att föreningen kan lämna den för hand när mejlet inte
                //    går fram. Den går aldrig att hämta igen — hashen är allt vi sparar.
                url,
                message = sent
                    ? $"Inbjudan är skickad till {request.Email}."
                    : "Inbjudan är skapad, men mejlet gick INTE att skicka. Lämna länken nedan till revisorn på annat sätt — den går inte att hämta igen."
            });
        }

        /// <summary>Återkallar en revisors åtkomst. Raden står kvar.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevokeAuditor([FromBody] RevokeAuditorRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att göra." });

            var (ok, ownerName) = await AuthorizeWriteAsync(request.OwnerType, request.OwnerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            var (done, error) = _auditorService.Revoke(
                request.OwnerType, request.OwnerId, request.GrantId, request.Reason, actorId.Value);

            if (done)
                _logger.LogInformation("Ekonomi: {Forening} återkallade revisorsuppdrag {Id}.",
                    ownerName, request.GrantId);

            return Json(done
                ? new { success = true, message = "Åtkomsten är återkallad." }
                : new { success = false, message = error! });
        }

        // ══ FORDRINGSEXPORTEN ════════════════════════════════════════════════════════════════
        //
        // ⚠️⚠️ ANNAN FIL, ANNAT ÄNDAMÅL än SIE-exporten nedan. Den exporterar vår journal; den här
        //    exporterar ANSPRÅKEN — vad föreningen har rätt att få in. Formen är Fredriks:
        //    1510 debet mot valt intäktskonto, så klubbens bankkoppling kan boka 1930/1510 när
        //    pengarna kommer i stället för att skapa intäkten en andra gång.

        /// <summary>Förhandsvisning: vilka fordringar skulle filen bära för perioden?</summary>
        [HttpGet]
        public async Task<IActionResult> GetReceivables(
            int issuerType, int issuerId, string? from = null, string? to = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var status = _setupService.GetStatus(issuerType, issuerId);

            // ⚠️ Bara den förening som bokför NÅGON ANNANSTANS har nytta av den här filen. Bokför
            //    vi åt dem finns fordran redan i deras egen liggare, och en import skulle bokföra
            //    samma anspråk två gånger.
            if (LedgerIssuerShape.KeepsBooks(status.Shape))
                return Json(new { success = true, applicable = false });

            if (!LedgerIssuerShape.OffersExport(status.Shape))
                return Json(new { success = true, applicable = false, bankLinked = true });

            var (f, t) = ResolvePeriod(from, to, status);

            try
            {
                var r = _receivableService.Build(issuerType, issuerId, f, t);

                return Json(new
                {
                    success = true,
                    applicable = true,
                    from = f,
                    to = t,
                    isSplit = r.IsSplit,
                    splitConfigured = r.SplitConfigured,
                    total = r.Total,
                    missingRoles = r.MissingRoles,
                    rows = r.Rows
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fordringarna gick inte att summera för {Typ}/{Id}.",
                    issuerType, issuerId);
                return Json(new { success = false, message = "Fordringarna gick inte att läsa." });
            }
        }

        /// <summary>Laddar ner fordringarna som SIE-fil.</summary>
        [HttpGet]
        public async Task<IActionResult> ExportReceivables(
            int issuerType, int issuerId, string? from = null, string? to = null)
        {
            var (ok, name) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Content(DeniedMessage);

            var status = _setupService.GetStatus(issuerType, issuerId);

            if (LedgerIssuerShape.KeepsBooks(status.Shape))
                return Content("Föreningen bokför här, så fordringarna finns redan i liggaren.");

            var (f, t) = ResolvePeriod(from, to, status);

            try
            {
                var r = _receivableService.Build(issuerType, issuerId, f, t);

                if (r.Rows.Count == 0)
                    return Content("Inga avgifter uppstod under perioden.");

                var bytes = _receivableService.BuildFile(r, name ?? "Förening", issuerId < 0);
                var fileName = (issuerId < 0 ? "SANDLADA-" : "")
                             + $"fordringar-{f:yyyyMMdd}-{t:yyyyMMdd}.se";

                return File(bytes, "text/plain", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fordringsexporten misslyckades för {Typ}/{Id}.",
                    issuerType, issuerId);
                return Content("Filen gick inte att skapa.");
            }
        }

        /// <summary>
        /// Perioden, med arbetsårets gränser som förval.
        /// <para>⚠️ Ett oläsbart datum blir ALDRIG tyst "hela året" — då hade en felskrivning
        /// kunnat exportera om en period som redan bokförts, och dubbelbokfört intäkten.</para>
        /// </summary>
        private static (DateTime From, DateTime To) ResolvePeriod(
            string? from, string? to, LedgerSetupStatus status)
        {
            var start = status.WorkingYearStart ?? new DateTime(DateTime.Today.Year, 1, 1);
            var end = status.WorkingYearEnd ?? new DateTime(DateTime.Today.Year, 12, 31);

            if (DateTime.TryParse(from, out var f)) start = f.Date;
            if (DateTime.TryParse(to, out var t)) end = t.Date;

            return (start, end);
        }

        // ══ SIE-EXPORTEN (P10.1) ═════════════════════════════════════════════════════════════

        /// <summary>
        /// Laddar ner räkenskapsåret som en SIE 4-fil.
        ///
        /// <para><b>⚠️ Skälen är inlåsning, arkivering och granskning</b> — en förening ska kunna
        /// lämna oss utan att lämna sin historik, och filen ska gå att spara i sju år oberoende
        /// av vår drift.</para>
        ///
        /// <para><b>⚠️ Bara den som bokför HOS OSS har en journal att exportera.</b> En klubb vars
        /// bokföring ligger i ett eget program får en tom och meningslös fil — deras export är en
        /// annan (fordringar, 1510 mot intäktskontot) och väntar på besked om vilka konton som
        /// ska ingå.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> ExportSie(int issuerType, int issuerId, int fiscalYearId)
        {
            var (ok, name) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Content(DeniedMessage);

            var status = _setupService.GetStatus(issuerType, issuerId);

            if (!LedgerIssuerShape.KeepsBooks(status.Shape))
                return Content("Föreningen bokför inte här, så det finns ingen verifikationslista "
                             + "att exportera.");

            try
            {
                // ⚠️ Sandlådan MÅSTE märkas i filen. Den lämnar sidan och tar ingen ram med sig -
                //    samma regel som kvittot följer, och en omärkt testfil i en förenings arkiv
                //    är oskiljbar från riktig bokföring.
                var sandbox = issuerId < 0;

                var bytes = _sieService.Build(
                    issuerType, issuerId, fiscalYearId, name ?? "Förening", null, sandbox);

                if (bytes == null) return Content("Räkenskapsåret hittades inte.");

                var year = (status.FiscalYears ?? new List<LedgerFiscalYear>())
                    .FirstOrDefault(y => y.Id == fiscalYearId)?.Year ?? DateTime.Today.Year;

                var fileName = (sandbox ? "SANDLADA-" : "") + $"bokforing-{year}.se";

                // ⚠️ .se är SIE:s filändelse. text/plain, inte application/octet-stream: filen ÄR
                //    text och ska gå att öppna i en editor när något ser konstigt ut.
                return File(bytes, "text/plain", fileName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SIE-exporten misslyckades för {Typ}/{Id} år {Ar}.",
                    issuerType, issuerId, fiscalYearId);
                return Content("Filen gick inte att skapa.");
            }
        }

        // ══ BOKSLUTET (P7) ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Bokslutets sju steg och årets resultat- och balansräkning.
        ///
        /// <para>⚠️ Stegen är HÄRLEDDA ur verkligt tillstånd, aldrig kryssrutor. En kryssruta
        /// säger att kassören TROR att steget är gjort; det här säger om det ÄR gjort — och det
        /// är hela poängen med en checklista i ett bokslut.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetClosing(int issuerType, int issuerId, int? fiscalYearId = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var status = _setupService.GetStatus(issuerType, issuerId);

                // ⚠️ Bokslutet hör till den som bokför HOS OSS. En förening som bokför i sitt
                //    eget program stänger sitt år där, och en halvtom bokslutsyta här vore det
                //    snabbaste sättet till slutsatsen "det här är inte färdigt".
                if (!LedgerIssuerShape.KeepsBooks(status.Shape))
                    return Json(new { success = true, applicable = false });

                var years = status.FiscalYears ?? new List<LedgerFiscalYear>();

                // ⚠️⚠️ FRÅGA OM LISTAN ÄR TOM — ALDRIG OM ID:T ÄR POSITIVT. Sandlådans
                //    räkenskapsår har NEGATIVT id (IDENTITY(-1,-1)), så ett `yearId <= 0` svarar
                //    "inget räkenskapsår upplagt" för varje sandlåda, tyst. Det är tredje gången
                //    den kontrollen skrivs i den här kodbasen och tredje gången den är fel —
                //    se sandlådans egen dokumentation: "kontroller som issuerId > 0 eller <= 0
                //    är BUGGAR". Hittad av ekonomi-sie-verify, inte av kompilatorn.
                if (years.Count == 0)
                    return Json(new { success = true, applicable = true, hasYear = false });

                var yearId = fiscalYearId
                    ?? years.OrderByDescending(y => y.Year).Select(y => y.Id).First();

                var checklist = _closingService.Checklist(issuerType, issuerId, yearId);
                var statements = _closingService.Statements(issuerType, issuerId, yearId);

                return Json(new
                {
                    success = true,
                    applicable = true,
                    hasYear = true,
                    years = years.OrderByDescending(y => y.Year)
                                 .Select(y => new { id = y.Id, year = y.Year, status = y.Status }),
                    checklist,
                    statements
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Bokslutet gick inte att bygga för {Typ}/{Id}.",
                    issuerType, issuerId);
                return Json(new { success = false, message = "Bokslutet gick inte att läsa." });
            }
        }

        /// <summary>
        /// Flyttar året mellan öppet, bokslutsarbete och fastställt.
        ///
        /// <para><b>⚠️⚠️ FASTSTÄLLANDE ÄR ENKELRIKTAT</b> — ett fastställt år tar inte emot
        /// skrivningar, och spärren ligger i databastriggern. Tjänsten vägrar dessutom fastställa
        /// ett år vars balansräkning inte går ihop: att frysa det låser in felet.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetFiscalYearStatus([FromBody] FiscalYearStatusRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Ogiltig begäran." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (done, message) = _closingService.SetStatus(
                request.IssuerType, request.IssuerId, request.FiscalYearId,
                request.Status ?? "", await CurrentMemberIdAsync());

            return Json(new { success = done, message });
        }

        public class FiscalYearStatusRequest
        {
            public int IssuerType { get; set; }
            public int IssuerId { get; set; }
            public int FiscalYearId { get; set; }
            public string? Status { get; set; }
        }

        // ══ BANKAVSTÄMNINGEN (P11) ═══════════════════════════════════════════════════════════
        //
        // ⚠️ Inget bank-API. PSD2 är licens- och kostnadsdrivet och valdes bort — föreningen
        //    laddar upp filen själv, och ingen tredje part får läsrätt till kontot.

        /// <summary>Kontoutdragen som lästs in, nyast först.</summary>
        [HttpGet]
        public async Task<IActionResult> GetBankImports(int issuerType, int issuerId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var accounts = _chartService.List(issuerType, issuerId)
                .Where(a => a.IsActive && a.Number >= 1900 && a.Number <= 1999)
                .Select(a => new { number = a.Number, name = a.Name })
                .ToList();

            return Json(new
            {
                success = true,
                // ⚠️ Kontona erbjuds ur FÖRENINGENS egen kontoplan, aldrig ur en lista i koden.
                //    En klubb som lagt upp 1932 för sitt andra konto ska kunna stämma av det.
                accounts,
                imports = _bankService.List(issuerType, issuerId).Select(i => new
                {
                    id = i.Id,
                    accountNumber = i.AccountNumber,
                    fileName = i.FileName,
                    periodFrom = i.PeriodFrom,
                    periodTo = i.PeriodTo,
                    rowCount = i.RowCount,
                    importedUtc = i.ImportedUtc
                })
            });
        }

        /// <summary>
        /// Läser filen och svarar med vad vi TROR att den innehåller. <b>Skriver ingenting.</b>
        ///
        /// <para>⚠️ Mappningen är ett FÖRSLAG. Bankerna döper kolumnerna olika, och en tyst
        /// felmappning ger ett kontoutdrag som stäms av mot fel siffror — operatören ska se och
        /// kunna rätta varje val innan något importeras.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PreviewBankFile(
            int issuerType, int issuerId, IFormFile? file)
        {
            var (ok, _) = await AuthorizeWriteAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "Ingen fil vald." });

            if (file.Length > MaxBankFileBytes)
                return Json(new { success = false, message = "Filen är för stor (max 8 MB)." });

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);

                var (map, sample, header) = _bankService.Preview(ms.ToArray());

                return Json(new
                {
                    success = true,
                    header,
                    sample,
                    // ⚠️ Delimitern som STRÄNG. Ett tabbtecken i JSON är osynligt i en dropdown,
                    //    och operatören kan inte välja något hen inte kan se.
                    delimiter = map.Delimiter.ToString(),
                    mapping = new
                    {
                        date = map.Date, text = map.Text, amount = map.Amount,
                        amountOut = map.AmountOut, balance = map.Balance, headerRow = map.HeaderRow
                    },
                    usable = map.Date >= 0 && (map.Amount >= 0 || map.AmountOut >= 0)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa kontoutdraget för {Typ}/{Id}.",
                    issuerType, issuerId);
                return Json(new { success = false, message = "Filen gick inte att läsa." });
            }
        }

        /// <summary>Läser in utdraget enligt operatörens mappning och parar ihop det entydiga.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ImportBankFile(
            int issuerType, int issuerId, int accountNumber, IFormFile? file,
            int date, int text, int amount, int amountOut, int balance,
            string delimiter, int headerRow)
        {
            var (ok, _) = await AuthorizeWriteAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });
            if (file == null || file.Length == 0)
                return Json(new { success = false, message = "Ingen fil vald." });
            if (file.Length > MaxBankFileBytes)
                return Json(new { success = false, message = "Filen är för stor (max 8 MB)." });
            if (accountNumber <= 0)
                return Json(new { success = false, message = "Välj vilket konto utdraget gäller." });

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms);

                var map = new LedgerBankImportService.Mapping
                {
                    Date = date, Text = text, Amount = amount, AmountOut = amountOut,
                    Balance = balance, HeaderRow = headerRow,
                    Delimiter = string.IsNullOrEmpty(delimiter) ? ';' : delimiter[0]
                };

                var parsed = _bankService.Parse(ms.ToArray(), map);

                if (parsed.Rows.Count == 0)
                    return Json(new
                    {
                        success = false,
                        message = "Ingen rad gick att läsa med den mappningen.",
                        // ⚠️ Skälen följer med. "Ingen rad gick att läsa" utan att säga varför
                        //    lämnar operatören att gissa om det är filen eller mappningen.
                        skipped = parsed.Skipped.Take(10)
                    });

                var importId = _bankService.Store(
                    issuerType, issuerId, accountNumber,
                    Path.GetFileName(file.FileName) ?? "kontoutdrag",
                    parsed, await CurrentMemberIdAsync());

                return Json(new
                {
                    success = true,
                    importId,
                    rows = parsed.Rows.Count,
                    // ⚠️ Överhoppade rader SÄGS alltid, även när importen lyckades. En bortfallen
                    //    rad är en differens operatören annars får leta efter för hand.
                    skipped = parsed.Skipped
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Import av kontoutdrag misslyckades för {Typ}/{Id}.",
                    issuerType, issuerId);
                return Json(new { success = false, message = "Filen gick inte att läsa in." });
            }
        }

        /// <summary>Avstämningen för ett utdrag.</summary>
        [HttpGet]
        public async Task<IActionResult> GetReconciliation(int issuerType, int issuerId, int importId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var r = _bankService.Reconciliation(issuerType, issuerId, importId);
                if (r == null) return Json(new { success = false, message = "Utdraget hittades inte." });

                return Json(new { success = true, reconciliation = r });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Avstämningen gick inte att bygga för utdrag {Id}.", importId);
                return Json(new { success = false, message = "Avstämningen gick inte att läsa." });
            }
        }

        /// <summary>Operatörens egen matchning. <c>lineId = 0</c> tar bort den.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBankMatch([FromBody] BankMatchRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Ogiltig begäran." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var done = _bankService.SetMatch(
                request.IssuerId, request.RowId, request.LineId, await CurrentMemberIdAsync());

            return Json(new
            {
                success = done,
                // ⚠️ Det unika indexet är spärren mot att samma bokföringsrad kvittas två gånger.
                //    Beskedet måste säga VAD som hindrade, annars läser det som en trasig knapp.
                message = done ? null
                    : "Den bokföringsraden är redan avstämd mot en annan bankrad."
            });
        }

        /// <summary>Tar bort ett utdrag. Raderna följer med.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteBankImport([FromBody] BankMatchRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Ogiltig begäran." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            return Json(new { success = _bankService.Delete(request.IssuerId, request.ImportId) });
        }

        public class BankMatchRequest
        {
            public int IssuerType { get; set; }
            public int IssuerId { get; set; }
            public int ImportId { get; set; }
            public int RowId { get; set; }
            public int LineId { get; set; }
        }

        /// <summary>
        /// Avprickningslistan för EN tävling eller händelse: vilka som betalat och vilka som inte har.
        ///
        /// <para><b>⚠️⚠️ ÖVERSIKTENS PANEL 3 SÄGER SIFFRAN, DEN HÄR SÄGER NAMNEN.</b> "4 saknas" är
        /// det Fredrik beskrev som okänt för kassören, men en siffra går inte att agera på — det
        /// gör en lista. Utan den här vägen är panelen ett larm utan nästa steg, och kassören
        /// tvingas tillbaka till de Pending-fakturor som ska ersättas.</para>
        ///
        /// <para><b>⚠️ TRE TILLSTÅND, ALDRIG EN BOOLEAN.</b> <i>Väntar</i> och <i>säger sig ha
        /// betalat</i> är två skilda arbetsuppgifter: den ena ska påminnas, den andra stämmas av
        /// mot kontoutdraget. Slås de ihop tappar listan sitt värde som kontroll — samma regel som
        /// <c>ClubEvent/GetPayments</c> följer, och de två ytorna får inte säga olika saker.</para>
        ///
        /// <para><b>⚠️ Läser i UTSTÄLLARENS schema</b> via
        /// <c>LedgerPaymentService.ForSourceInIssuer</c>. En sandlåda som visade den skarpa listan
        /// vore precis den blandning den fysiska separationen finns för att omöjliggöra.</para>
        ///
        /// <para>Läsning, ingen skrivning — därför <c>AuthorizeIssuerAsync</c>. Att bekräfta en
        /// betalning är arrangörens handling på händelsens egen yta; den här är kassörens
        /// kontrollfråga, och styrelsen ska kunna ställa den utan skrivrätt.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSourcePayments(
            int issuerType, int issuerId, string sourceType, int sourceId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            if (string.IsNullOrWhiteSpace(sourceType) || sourceId <= 0)
                return Json(new { success = false, message = "Ingen tävling eller händelse angiven." });

            try
            {
                // ⚠️ Makulerade rader faller bort: de beskriver en avgift som inte längre finns,
                //    och en avprickningslista som räknar dem påstår en skuld som ingen har.
                var rows = _paymentService
                    .ForSourceInIssuer(issuerId, sourceType, sourceId)
                    .Where(p => p.VoidedUtc is null)
                    .ToList();

                return Json(new
                {
                    success = true,
                    // ⚠️ Inget namn i svaret, med flit. Raden man klickade på bär det redan, och
                    //    namnuppslaget bor på ETT ställe (LedgerOverviewService.NameForSource).
                    //    Ett andra uppslag hade kunnat svara något annat än rubriken ovanför.
                    expected = rows.Count,
                    settled = rows.Count(p => p.IsMoney),
                    missingAmount = rows.Where(p => !p.IsMoney).Sum(p => p.Amount),
                    payments = rows.Select(p => new
                    {
                        id = p.Id,
                        payerName = p.PayerName,
                        amount = p.Amount,
                        // ⚠️ Det BEKRÄFTADE beloppet kan skilja sig från det begärda. Visas det
                        //    begärda på en betald rad ser en delbetalning ut som en helbetalning.
                        actualAmount = p.ActualAmount,
                        claimedDate = p.ClaimedUtc,
                        confirmedDate = p.ConfirmedUtc,
                        method = p.Method,
                        receiptId = p.ReceiptId
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte läsa avprickningslistan för {Typ}/{Id}, källa {S}/{SId}.",
                    issuerType, issuerId, sourceType, sourceId);

                return Json(new { success = false, message = "Listan gick inte att läsa just nu." });
            }
        }

        /// <summary>
        /// Skapar en ny sandlåda för föreningen. En tidigare sandlåda <b>överges</b>.
        ///
        /// <para><b>⚠️ Det här ÄR nollställningen.</b> Den raderar ingenting och rör ingen
        /// spärr — "börja om" är en ny utställare, inte en tömd liggare.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateSandbox([FromBody] CreateSandboxRequest request)
        {
            if (request is null || request.OwnerId <= 0)
                return Json(new { success = false, message = "Ingen förening angiven." });

            // ⚠️ Grinden prövas mot ÄGAREN (nod-id), inte mot en utställare — det är en ny
            // sandlåda som ska skapas, och den finns inte ännu.
            var (ok, _) = await AuthorizeWriteAsync(request.OwnerType, request.OwnerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad." });

            try
            {
                var sandbox = _sandbox.CreateSandbox(
                    request.OwnerType, request.OwnerId, request.Label, actorId.Value);

                return Json(new
                {
                    success = true,
                    issuerId = sandbox.Id,
                    url = $"/ekonomi?type={request.OwnerType}&id={request.OwnerId}&issuer={sandbox.Id}"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte skapa sandlåda för {Typ}/{Id}.",
                    request.OwnerType, request.OwnerId);

                return Json(new { success = false, message = "Sandlådan gick inte att skapa. Försök igen." });
            }
        }

        /// <summary>
        /// Medlemsavgifternas läge, sett från liggaren.
        ///
        /// <para><b>⚠️ Bara för en KLUBB.</b> Kretsen har ingen medlemsavgift — den fakturerar sina
        /// klubbar, vilket är samma motor men en annan part, och den vägen går inte genom
        /// <c>MembershipFeeCharge</c>.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMembershipFees(int issuerType, int issuerId, int? year = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            // ⚠️ Kretsen har också avgifter nu — kretsavgiften från sina klubbar. Samma motor, samma
            //    kort; bara rubriken skiljer, och den kommer härifrån så att ytan inte gissar.
            if (issuerType != DocumentOwnerType.Club && issuerType != DocumentOwnerType.Region)
                return Json(new { success = true, applicable = false });

            var label = issuerType == DocumentOwnerType.Region ? "Kretsavgifter" : "Medlemsavgifter";

            // ⚠️⚠️ SANDLÅDAN SER INGA MEDLEMSAVGIFTER, OCH DET MÅSTE STÅ PÅ SKÄRMEN.
            //    `MembershipFeeCharge` är LIVE-data nycklad på klubbens riktiga nod-id; en
            //    sandlåda har ett negativt id och träffar därför noll rader. Utan den här grenen
            //    svarade ytan "Inget · Inget" — oskiljbart från en klubb där alla har betalat, på
            //    årets STÖRSTA intäktspost. En klubb som provar i sandlådan drog då slutsatsen
            //    att avgifterna inte finns i systemet.
            //    ⚠️ Att i stället läsa ÄGARENS avgifter vore värre: knappen "Bokför" hade då
            //    skrivit verifikationer för riktiga krav in i en kastbar liggare, och nästa
            //    sandlåda hade gjort det igen. Sandlådan säger vad den inte kan visa.
            if (issuerId < 0)
                return Json(new
                {
                    success = true,
                    applicable = true,
                    sandbox = true,
                    label,
                    message = $"{label} ligger i den riktiga bokföringen och följer inte "
                            + "med hit. En sandlåda får inte bokföra riktiga krav — byt till "
                            + "Riktig bokföring för att se och bokföra dem."
                });

            try
            {
                return Json(new
                {
                    success = true,
                    applicable = true,
                    sandbox = false,
                    label,
                    fees = _feeBridge.Summarise(issuerType, issuerId, year)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa medlemsavgifterna för klubb {Id}.", issuerId);
                return Json(new { success = false, message = "Medlemsavgifterna gick inte att läsa just nu." });
            }
        }

        /// <summary>
        /// Bokför betalda medlemsavgifter som saknar verifikation.
        ///
        /// <para>Behövs eftersom avgiftsmodulen levde utanför liggaren fram till 2026-09-22 — allt
        /// som kvitterats före det är betalt men obokfört.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostMembershipFees([FromBody] PostMembershipFeesRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att bokföra." });

            // ⚠️ IssuerType saknas i äldre anrop och är då 0 = klubb, alltså dagens beteende.
            var ownerType = request.IssuerType == DocumentOwnerType.Region ? DocumentOwnerType.Region : DocumentOwnerType.Club;

            var (ok, name) = await AuthorizeWriteAsync(ownerType, request.ClubId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad för att bokföra." });

            try
            {
                var year = request.Year > 0 ? request.Year : DateTime.Today.Year;
                var count = _feeBridge.PostPending(ownerType, request.ClubId, year, actorId.Value);

                _logger.LogInformation(
                    "Ekonomi: {Forening} efterbokförde {Antal} avgifter för {Ar}.", name, count, year);

                return Json(new
                {
                    success = true,
                    posted = count,
                    fees = _feeBridge.Summarise(ownerType, request.ClubId, year)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Efterbokföringen av medlemsavgifter fallerade för klubb {Id}.", request.ClubId);
                return Json(new { success = false, message = "Avgifterna gick inte att bokföra. Försök igen." });
            }
        }

        /// <summary>
        /// Det bokför-ytan behöver: föreningens konton och de senast bokförda verifikationerna.
        ///
        /// <para><b>⚠️ Kontolistan kommer ur FÖRENINGENS kontoplan, aldrig ur en lista i koden.</b>
        /// En klubb som döpt om 4010 eller lagt till 4015 ska se sina egna konton här — det är hela
        /// skälet att kontoplanen är data.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPostingContext(int issuerType, int issuerId)
        {
            var (ok, _) = await AuthorizeWriteAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var ctx = _manualPosting.BuildContext(issuerType, issuerId);
                return Json(new { success = true, context = ctx });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa bokföringsunderlaget för {Typ}/{Id}.", issuerType, issuerId);
                return Json(new { success = false, message = "Underlaget gick inte att läsa just nu." });
            }
        }

        /// <summary>
        /// Bokför en manuell post — <b>den enda ytan där något skrivs in för hand</b>.
        ///
        /// <para><b>⚠️ Beloppet och riktningen räcker.</b> Kassören anger hur mycket och om pengarna
        /// gick in eller ut; konteringen byggs av tjänsten och visas innan den sparas. Att kräva
        /// debet och kredit av en förtroendevald är att kräva att hen kan dubbel bokföring, vilket
        /// är exakt det den här modulen finns för att slippa.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostManual([FromBody] ManualEntryRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att bokföra." });

            var (ok, name) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad för att bokföra." });

            try
            {
                var result = _manualPosting.Post(request, actorId.Value);

                if (!result.Success)
                    return Json(new { success = false, message = result.Error });

                _logger.LogInformation(
                    "Ekonomi: {Forening} bokförde manuellt {Belopp} kr som verifikation {EntryId}.",
                    name, request.Amount, result.EntryId);

                return Json(new
                {
                    success = true,
                    entryId = result.EntryId,
                    number = result.Number,
                    context = _manualPosting.BuildContext(request.IssuerType, request.IssuerId)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Manuell bokföring fallerade för {Typ}/{Id}.",
                    request.IssuerType, request.IssuerId);

                return Json(new { success = false, message = "Posten gick inte att bokföra. Försök igen." });
            }
        }

        /// <summary>
        /// Kön "att bokföra": bekräftade betalningar utan verifikation.
        ///
        /// <para><b>⚠️ Bara för en förening som bokför hos oss.</b> För en
        /// <see cref="LedgerIssuerShape.FeesAndExport"/>-förening är listan per definition full och
        /// helt ointressant — varje betalning saknar verifikation, eftersom bokföringen sker
        /// någon annanstans. Att visa den där vore att påstå att något är ogjort.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetUnposted(int issuerType, int issuerId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var status = _setupService.GetStatus(issuerType, issuerId);

                if (!LedgerIssuerShape.KeepsBooks(status.Shape))
                    return Json(new { success = true, rows = Array.Empty<UnpostedPaymentRow>(), total = 0m });

                var rows = _paymentService.UnpostedConfirmed(issuerType, issuerId)
                    .Select(p => new UnpostedPaymentRow
                    {
                        Id = p.Id,
                        PayerName = p.PayerName,
                        Amount = p.SettledAmount,
                        Method = p.Method,
                        // ConfirmedUtc kan inte vara null här — kön är definierad som bekräftad
                        // utan verifikation — men läs den försiktigt ändå.
                        ConfirmedDate = (p.ConfirmedUtc ?? p.CreatedUtc).Date,
                        Description = LedgerPaymentService.DescribeFor(p)
                    })
                    .ToList();

                return Json(new { success = true, rows, total = rows.Sum(r => r.Amount) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte läsa kön att bokföra för utställare {Typ}/{Id}.", issuerType, issuerId);

                return Json(new { success = false, message = "Listan gick inte att läsa just nu." });
            }
        }

        /// <summary>
        /// Bokför en betalning ur kön.
        ///
        /// <para><b>⚠️ Betalningens utställare avgör behörigheten, inte anropets.</b> Ett
        /// <c>paymentId</c> från klienten säger ingenting om vem som äger raden — den kontrollen
        /// måste göras mot betalningen som faktiskt ligger i databasen, annars kan vem som helst
        /// med ett giltigt id bokföra i en annan förenings liggare.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> PostPending([FromBody] PostPendingRequest request)
        {
            // ⚠️⚠️ NOLL, inte `<= 0`. Sandlådans betalningar har NEGATIVA id, så ett `<= 0` nekar
            //    varje bokföring i en sandlåda med "Ingen betalning angiven" — alltså ett besked
            //    om BEGÄRAN när sanningen är att koden inte förstår id:t. Samma fälla som
            //    räkenskapsåret i GetClosing, hittad i samma svep.
            if (request is null || request.PaymentId == 0)
                return Json(new { success = false, message = "Ingen betalning angiven." });

            var payment = _paymentService.GetById(request.PaymentId);
            if (payment is null)
                return Json(new { success = false, message = "Betalningen finns inte." });

            var (ok, name) = await AuthorizeWriteAsync(payment.IssuerType, payment.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad för att bokföra." });

            try
            {
                var result = _paymentService.PostPending(request.PaymentId, actorId.Value);

                if (!result.Success)
                    return Json(new { success = false, message = result.Error });

                _logger.LogInformation(
                    "Ekonomi: {Forening} efterbokförde betalning {PaymentId} som verifikation {EntryId}.",
                    name, result.PaymentId, result.JournalEntryId);

                return Json(new
                {
                    success = true,
                    journalEntryId = result.JournalEntryId,
                    accountingDate = result.AccountingDate.ToString("yyyy-MM-dd"),
                    status = ReadStatus(payment.IssuerType, payment.IssuerId, name)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Efterbokföringen av betalning {PaymentId} fallerade.", request.PaymentId);
                return Json(new { success = false, message = "Betalningen gick inte att bokföra. Försök igen." });
            }
        }

        /// <summary>
        /// Resolverar inloggad medlem till id — verifikationen ska bära vem som bokförde.
        /// </summary>
        /// <summary>
        /// Den inloggades e-postadress, för svarsadressen på ett utgående mejl.
        /// <para>⚠️ Tom sträng när den inte går att läsa — anroparen måste välja vad det betyder,
        /// och den som mejlar får aldrig gissa en adress.</para>
        /// </summary>
        private async Task<string> CurrentActorEmailAsync()
        {
            try
            {
                var current = await _memberManager.GetCurrentMemberAsync();
                return current?.Email ?? "";
            }
            catch
            {
                return "";
            }
        }

        private async Task<(int? id, string? name)> GetCurrentActorAsync()
        {
            try
            {
                var current = await _memberManager.GetCurrentMemberAsync();
                if (current is null) return (null, null);

                var data = _memberService.GetByEmail(current.Email ?? string.Empty);
                return data is null ? (null, current.Name) : (data.Id, data.Name ?? current.Name);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>
        /// Sätter upp föreningen, eller lägger till ett räkenskapsår i en som redan är uppsatt.
        ///
        /// <para><b>⚠️ Formen skrivs i två steg med flit.</b> <c>EnsureIssuer</c> sätter den bara
        /// när inställningsraden skapas — den får aldrig skriva över ett val föreningen gjort — så
        /// ett byte går via <c>SetShape</c>. Utan det andra steget hade en förening som en gång
        /// hamnat i fel form suttit fast i den.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveSetup([FromBody] EkonomiSetupRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, name) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            if (!LedgerIssuerShape.IsValid(request.Shape))
            {
                return Json(new { success = false, message = "Välj hur föreningen sköter sin bokföring innan du sparar." });
            }

            // Ett gissat räkenskapsår är fel i tysthet — därför krävs året, det härleds inte.
            if (request.Year < 2000 || request.Year > 2100)
            {
                return Json(new { success = false, message = "Ange vilket räkenskapsår det gäller." });
            }

            if (request.StartDate.HasValue && request.EndDate.HasValue
                && request.EndDate.Value.Date <= request.StartDate.Value.Date)
            {
                return Json(new { success = false, message = "Räkenskapsårets slut måste ligga efter dess början." });
            }

            try
            {
                var result = _setupService.EnsureIssuer(
                    request.IssuerType,
                    request.IssuerId,
                    request.Year,
                    request.Shape,
                    request.StartDate,
                    request.EndDate);

                var shapeChanged = _setupService.SetShape(request.IssuerType, request.IssuerId, request.Shape);

                var status = ReadStatus(request.IssuerType, request.IssuerId, name);

                _logger.LogInformation(
                    "Ekonomi: {Forening} ({Typ}/{Id}) uppsatt för {Ar} — {Konton} konton, {Roller} roller, "
                    + "år skapat: {ArSkapat}, form ändrad: {FormAndrad}.",
                    name, request.IssuerType, request.IssuerId, request.Year,
                    result.AccountsAdded, result.RolesMapped, result.FiscalYearCreated, shapeChanged);

                return Json(new
                {
                    success = true,
                    status,
                    created = new
                    {
                        accounts = result.AccountsAdded,
                        roles = result.RolesMapped,
                        series = result.SeriesCreated,
                        fiscalYear = result.FiscalYearCreated,
                        settings = result.SettingsCreated,
                        shapeChanged
                    },
                    // Tom lista är det normala. Är den inte tom är varje rad en betalning som inte
                    // går att bokföra, och då ska det synas — inte loggas tyst.
                    rolesWithoutDefault = result.RolesWithoutDefault
                });
            }
            catch (ArgumentException ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte spara ekonomiuppsättningen för utställare {Typ}/{Id}.",
                    request.IssuerType, request.IssuerId);

                return Json(new { success = false, message = "Uppsättningen gick inte att spara. Försök igen." });
            }
        }

        // ── Rapport: utfall mot budget ───────────────────────────────────────────────────

        /// <summary>
        /// Rapport-ytan. Svarar ALLTID 200 med ett läsbart tillstånd — "ingen budget antagen" är
        /// ett riktigt och vanligt läge, inte ett fel, och ytan ritar sig helt ur det här svaret.
        ///
        /// <para>⚠️ <paramref name="from"/> och <paramref name="to"/> är "Byt period" i skissen.
        /// Utelämnade betyder räkenskapsårets början fram till i dag.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetBudgetReport(
            int issuerType, int issuerId, int? year = null, string? from = null, string? to = null)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var fiscalYear = ResolveFiscalYear(issuerType, issuerId, year);

                if (fiscalYear is null)
                    return Json(new
                    {
                        success = true,
                        hasFiscalYear = false,
                        message = "Det finns inget räkenskapsår att följa upp ännu."
                    });

                var report = _budgetService.BuildReport(
                    issuerType, issuerId, fiscalYear, ParseDate(from), ParseDate(to));

                return Json(new
                {
                    success = true,
                    hasFiscalYear = true,
                    report,
                    periodLabel = report.PeriodLabel,
                    resultTone = report.ResultTone,
                    yearStart = fiscalYear.StartDate.ToString("yyyy-MM-dd"),
                    yearEnd = fiscalYear.EndDate.ToString("yyyy-MM-dd")
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte bygga budgetrapporten för {Typ}/{Id}.", issuerType, issuerId);
                return Json(new { success = false, message = "Rapporten gick inte att läsa just nu." });
            }
        }

        /// <summary>
        /// Underlaget för att skriva in budgeten: föreningens resultatkonton, utkastets belopp, och
        /// förra årets utfall som stöd.
        ///
        /// <para><b>⚠️ Kontolistan kommer ur FÖRENINGENS kontoplan</b>, aldrig ur en lista i koden —
        /// samma regel som bokföringsytan. En klubb som lagt till 4015 ska kunna budgetera på det.</para>
        ///
        /// <para><b>⚠️ Förra årets utfall visas men fylls ALDRIG i åt kassören.</b> Budgeten är ett
        /// beslut; ett förifyllt tal är en gissning som ser ut som ett beslut så fort någon trycker
        /// spara utan att läsa.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetBudgetEditor(int issuerType, int issuerId, int? year = null)
        {
            var (ok, _) = await AuthorizeWriteAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var fiscalYear = ResolveFiscalYear(issuerType, issuerId, year);
                if (fiscalYear is null)
                    return Json(new { success = false, message = "Lägg upp räkenskapsåret först." });

                var memberId = await CurrentMemberIdAsync();
                var draft = _budgetService.EnsureDraft(issuerType, issuerId, fiscalYear.Id, memberId);

                if (!draft.Success || draft.Budget is null)
                    return Json(new { success = false, message = draft.Error ?? "Budgetutkastet kunde inte skapas." });

                var lines = _budgetService.GetLines(draft.Budget.Id);
                var ctx = _manualPosting.BuildContext(issuerType, issuerId);

                // Föregående år, som stöd. Finns det inget är listan tom och ytan säger inget om det.
                var previous = _setupService.GetStatus(issuerType, issuerId).FiscalYears
                    .Where(y => y.Year == fiscalYear.Year - 1)
                    .Select(y => _budgetService.BuildReport(issuerType, issuerId, y, y.StartDate, y.EndDate))
                    .FirstOrDefault();

                var previousActuals = previous is null
                    ? new Dictionary<int, decimal>()
                    : previous.Income.Concat(previous.Costs).ToDictionary(r => r.AccountNumber, r => r.Actual);

                var accounts = ctx.Accounts
                    .Where(a => !a.IsBalance)
                    .Select(a => new
                    {
                        number = a.Number,
                        name = a.Name,
                        isIncome = LedgerBudgetService.IsIncome(a.Number),
                        amount = lines.FirstOrDefault(l => l.AccountNumber == a.Number)?.Amount ?? 0m,
                        previousActual = previousActuals.TryGetValue(a.Number, out var pa) ? pa : 0m
                    })
                    .OrderBy(a => a.number)
                    .ToList();

                return Json(new
                {
                    success = true,
                    draftId = draft.Budget.Id,
                    revision = draft.Budget.Revision,
                    fiscalYear = fiscalYear.Year,
                    previousYear = previous is null ? (int?)null : fiscalYear.Year - 1,
                    accounts
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa budgetunderlaget för {Typ}/{Id}.", issuerType, issuerId);
                return Json(new { success = false, message = "Budgetunderlaget gick inte att läsa just nu." });
            }
        }

        /// <summary>Sparar utkastets belopp. Raderna skrivs om helt — formuläret ÄR sanningen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveBudgetDraft([FromBody] SaveBudgetRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            // ⚠️⚠️ ETT SAKNAT FÄLT ÄR INTE ETT ÄGARSKAPSFEL. Utan den här grenen svarade servern
            // "Budgeten hör inte till den här föreningen" på en begäran som bara utelämnade
            // budgetId — ett påstående om ÄGARSKAP när sanningen är ett påstående om BEGÄRAN.
            // Det kostade en felsökningsrunda i kassörsgenomgången, och är samma felattribution
            // som redan rättats på DeleteResult och RemoveShooterFromStartList.
            if (request.BudgetId == 0)
                return Json(new { success = false, message = "Begäran saknar budgetId — hämta utkastet först (GetBudgetEditor svarar med draftId)." });

            // ⚠️ Utkastet måste tillhöra den utställare anroparen har behörighet till. Utan den
            // kontrollen räcker behörighet till EN förening för att skriva i en annans budget.
            if (!BudgetBelongsToIssuer(request.BudgetId, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Budgeten hör inte till den här föreningen." });

            var lines = (request.Lines ?? new List<BudgetLineInput>())
                .Select(l => new LedgerBudgetLine
                {
                    AccountNumber = l.AccountNumber,
                    AccountName = l.AccountName ?? "",
                    Amount = l.Amount
                })
                .ToList();

            var result = _budgetService.SaveDraftLines(request.BudgetId, lines);

            return Json(result.Success
                ? new { success = true, message = $"Budgeten sparad — {result.LineCount} konton." }
                : new { success = false, message = result.Error! });
        }

        /// <summary>
        /// Antar budgeten. <b>Efter det här går den inte att ändra</b> — en revidering är en ny
        /// version, och det säger svaret också.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AdoptBudget([FromBody] AdoptBudgetRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att anta." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            // ⚠️ Samma skillnad som i SaveBudgetDraft: saknat fält namnges, ägarskap är något annat.
            if (request.BudgetId == 0)
                return Json(new { success = false, message = "Begäran saknar budgetId — hämta utkastet först (GetBudgetEditor svarar med draftId)." });

            if (!BudgetBelongsToIssuer(request.BudgetId, request.IssuerType, request.IssuerId))
                return Json(new { success = false, message = "Budgeten hör inte till den här föreningen." });

            var date = ParseDate(request.AdoptedDate);
            if (date is null)
                return Json(new { success = false, message = "Ange vilket datum beslutet fattades." });

            var memberId = await CurrentMemberIdAsync();

            var result = _budgetService.Adopt(
                request.BudgetId, date.Value, request.AdoptedBody ?? LedgerBudgetAdoptedBy.AnnualMeeting,
                string.IsNullOrWhiteSpace(request.Note) ? null : request.Note!.Trim(), memberId);

            return Json(result.Success
                ? new
                {
                    success = true,
                    message = "Budgeten är antagen och kan inte längre ändras. "
                            + "Behöver den revideras blir det en ny version."
                }
                : new { success = false, message = result.Error! });
        }

        /// <summary>
        /// ⚠️ Budgeten adresseras med sitt EGET id, som klienten skickar. Utan den här kontrollen
        /// räcker behörighet till en förening för att skriva i en annans budget. Samma familj som
        /// kvittots ägarkontroll.
        /// </summary>
        private bool BudgetBelongsToIssuer(int budgetId, int issuerType, int issuerId)
        {
            if (budgetId == 0) return false;

            using var db = DatabaseFactory.CreateDatabase();
            var ldb = new LedgerDb(db, budgetId);

            return ldb.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM dbo.LedgerBudget WHERE Id = @0 AND IssuerType = @1 AND IssuerId = @2",
                budgetId, issuerType, issuerId) > 0;
        }

        /// <summary>Året som ska följas upp: det efterfrågade, annars det innevarande.</summary>
        private LedgerFiscalYear? ResolveFiscalYear(int issuerType, int issuerId, int? year)
        {
            var years = _setupService.GetStatus(issuerType, issuerId).FiscalYears;
            if (years.Count == 0) return null;

            if (year is int y)
                return years.FirstOrDefault(f => f.Year == y);

            var today = DateTime.Today;

            return years.FirstOrDefault(f => f.StartDate.Date <= today && f.EndDate.Date >= today)
                ?? years.OrderByDescending(f => f.Year).First();
        }

        private static DateTime? ParseDate(string? value)
            => DateTime.TryParse(value, out var d) ? d.Date : null;

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }

        // ── Kontoplanen ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Föreningens kontoplan, med kontorollerna i klartext.
        ///
        /// <para><b>⚠️ Kontoplanen gick inte att ändra från någon yta alls fram till 2026-09-22.</b>
        /// Kontona såddes ur mallen och sedan var det slut. För den som matar in förra årets
        /// bokföring är det blockerande — en förening har konton vi inte sått.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetChart(int issuerType, int issuerId)
        {
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            try
            {
                var rows = _chartService.List(issuerType, issuerId);

                return Json(new
                {
                    success = true,
                    accounts = rows.Select(a => new
                    {
                        number = a.Number,
                        name = a.Name,
                        isActive = a.IsActive,
                        fromTemplate = a.FromTemplate,
                        entryLines = a.EntryLines,
                        isBalance = a.IsBalance,
                        isIncome = a.IsIncome,
                        // ⚠️ Rollerna i KLARTEXT, aldrig som nycklar. "revenue-membership-fee"
                        // säger ingenting till en kassör som inte kan bokföring.
                        roles = a.Roles.Select(LedgerAccountRoles.Label).ToList()
                    }),
                    // Hela rollistan, som en fråga var: "när det här händer, vart går pengarna?"
                    // ⚠️⚠️ `Known`, inte `All` — de VALFRIA rollerna måste erbjudas, annars är
                    //    Fredriks uppdelade fordringskonton omöjliga att sätta och funktionen
                    //    finns bara i koden. `optional` är vad som skiljer "inte ifylld ännu"
                    //    från "en lucka i uppsättningen"; kravlistan räknar fortfarande `All`.
                    roles = LedgerAccountRoles.Known.Select(r => new
                    {
                        key = r,
                        label = LedgerAccountRoles.Label(r),
                        hint = LedgerAccountRoles.Hint(r),
                        optional = LedgerAccountRoles.IsOptional(r),
                        accountNumber = rows.FirstOrDefault(a => a.Roles.Contains(r))?.Number ?? 0
                    })
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte läsa kontoplanen för {Typ}/{Id}.", issuerType, issuerId);
                return Json(new { success = false, message = "Kontoplanen gick inte att läsa just nu." });
            }
        }

        /// <summary>Lägger till ett konto, eller döper om ett som finns.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveAccount([FromBody] SaveAccountRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att spara." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var result = request.Rename
                ? _chartService.Rename(request.IssuerType, request.IssuerId, request.Number, request.Name ?? "")
                : _chartService.Add(request.IssuerType, request.IssuerId, request.Number, request.Name ?? "");

            return Json(result.Success
                ? new { success = true, message = request.Rename
                    ? $"Konto {request.Number} har fått ett nytt namn."
                    : $"Konto {request.Number} tillagt." }
                : new { success = false, message = result.Error! });
        }

        /// <summary>
        /// Stänger eller öppnar ett konto. <b>Raderar aldrig</b> — bokförda rader pekar på numret.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAccountActive([FromBody] SetAccountActiveRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att ändra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var result = _chartService.SetActive(
                request.IssuerType, request.IssuerId, request.Number, request.Active);

            // ⚠️ Kvittot använder SAMMA ord som knappen. Knappen sa "Sluta använda" och svaret
            // "är stängt" — två ord för samma sak får läsaren att undra om något annat hände.
            return Json(result.Success
                ? new { success = true, message = request.Active
                    ? $"Konto {request.Number} används igen."
                    : $"Konto {request.Number} används inte längre. Det erbjuds inte när du bokför, "
                      + "men gamla poster ligger kvar." }
                : new { success = false, message = result.Error! });
        }

        /// <summary>Pekar om en kontoroll till ett annat konto.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAccountRole([FromBody] SetAccountRoleRequest request)
        {
            if (request is null) return Json(new { success = false, message = "Inget att ändra." });

            var (ok, _) = await AuthorizeWriteAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = DeniedMessage });

            var result = _chartService.SetRole(
                request.IssuerType, request.IssuerId, request.RoleKey ?? "", request.Number);

            return Json(result.Success
                ? new { success = true, message = $"Går nu till konto {request.Number}." }
                : new { success = false, message = result.Error! });
        }

    }

    /// <summary>Det sandlådeknappen skickar. ⚠️ ÄGARENS nod-id, inte ett utställar-id.</summary>
    /// <summary>⚠️ <c>Rename</c> skiljer "lägg till" från "döp om" — numret ändras aldrig.</summary>
    public class SaveAccountRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int Number { get; set; }
        public string? Name { get; set; }
        public bool Rename { get; set; }
    }

    public class SetAccountActiveRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int Number { get; set; }
        public bool Active { get; set; }
    }

    public class SetAccountRoleRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public string? RoleKey { get; set; }
        public int Number { get; set; }
    }

    public class SaveBudgetRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int BudgetId { get; set; }
        public List<BudgetLineInput>? Lines { get; set; }
    }

    public class BudgetLineInput
    {
        public int AccountNumber { get; set; }
        public string? AccountName { get; set; }
        public decimal Amount { get; set; }
    }

    /// <summary>⚠️ Datumet är BESLUTETS, inte dagens. Budgeten antogs på ett möte.</summary>
    public class AdoptBudgetRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int BudgetId { get; set; }
        public string? AdoptedDate { get; set; }
        public string? AdoptedBody { get; set; }
        public string? Note { get; set; }
    }

    public class CreateSandboxRequest
    {
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string? Label { get; set; }
    }

    /// <summary>Det avgiftskortet skickar.</summary>
    public class PostMembershipFeesRequest
    {
        /// <summary>Utställarens id — klubbens eller, med <see cref="IssuerType"/> = 1, kretsens.</summary>
        public int ClubId { get; set; }
        public int Year { get; set; }

        /// <summary>0 = klubb (standard, äldre anrop), 1 = krets.</summary>
        public int IssuerType { get; set; }
    }

    /// <summary>Det köytan skickar när en rad ska bokföras.</summary>
    public class PostPendingRequest
    {
        public int PaymentId { get; set; }
    }

    /// <summary>Ett projekt. <c>Id = 0</c> = nytt.</summary>
    public class SaveProjectRequest
    {
        public int Id { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public string? StartDate { get; set; }
        public string? EndDate { get; set; }
    }

    public class ProjectClosedRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int ProjectId { get; set; }
        public bool Closed { get; set; }
    }

    /// <summary>En projektgrupp. <c>Id = 0</c> = ny.</summary>
    public class SaveProjectGroupRequest
    {
        public int Id { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    public class ProjectGroupRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int GroupId { get; set; }
    }

    public class ProjectGroupMemberRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int GroupId { get; set; }
        public int ProjectId { get; set; }

        /// <summary>
        /// ⚠️ Skickas som JSON-boolean (<c>true</c>/<c>false</c>) i en [FromBody]-kropp, där
        /// System.Text.Json binder den rätt — till skillnad från formulärbindningens "1"/"0"-fälla.
        /// </summary>
        public bool Member { get; set; }
    }

    /// <summary>En utgift: ett utlägg eller en leverantörsfaktura.</summary>
    public class SaveExpenseRequest
    {
        public int Id { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }

        /// <summary>Ur <see cref="LedgerExpenseKind"/>.</summary>
        public string? Kind { get; set; }

        /// <summary>Medlemmen som ska ha pengarna. 0 för en leverantör.</summary>
        public int PayeeMemberId { get; set; }

        public string? PayeeName { get; set; }
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public string? ExpenseDate { get; set; }
        public string? DueDate { get; set; }
        public int AccountNumber { get; set; }
        public int ProjectId { get; set; }
    }

    /// <summary>Attest eller avslag. <c>Text</c> är noteringen respektive skälet.</summary>
    public class ExpenseActionRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int ExpenseId { get; set; }
        public string? Text { get; set; }
    }

    /// <summary>Utbetalningen — den enda av utgiftssidans handlingar som bokför något.</summary>
    public class PayExpenseRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int ExpenseId { get; set; }
        public string? PaidDate { get; set; }
        public int PaymentAccountNumber { get; set; }
    }

    /// <summary>En tillgång i anläggningsregistret.</summary>
    public class SaveAssetRequest
    {
        public int Id { get; set; }
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public string? Name { get; set; }
        public string? Note { get; set; }
        public int AssetAccountNumber { get; set; }
        public int DepreciationAccountNumber { get; set; }
        public string? InUseDate { get; set; }
        public decimal AcquisitionAmount { get; set; }
        public int UsefulLifeYears { get; set; }
        public decimal ResidualValue { get; set; }
    }

    /// <summary>Utrangering. <b>Skälet är obligatoriskt</b> — prövas i tjänsten.</summary>
    public class DisposeAssetRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int AssetId { get; set; }
        public string? When { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>Bokföring av årets avskrivningar.</summary>
    public class PostDepreciationRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int? Year { get; set; }
    }

    /// <summary>Inbjudan av en revisor. Ägaren, aldrig utställaren.</summary>
    public class InviteAuditorRequest
    {
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public string? Email { get; set; }
        public string? Name { get; set; }
    }

    /// <summary>Återkallning av en revisors åtkomst.</summary>
    public class RevokeAuditorRequest
    {
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public int GrantId { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>
    /// Bortkoppling av ett underlag.
    /// <para>⚠️ <see cref="Reason"/> är obligatoriskt och prövas i tjänsten, inte här — en
    /// bortkopplad bilaga utan skäl går inte att bedöma i efterhand, och det är en revisor som
    /// kommer att ställa frågan.</para>
    /// </summary>
    public class VoidAttachmentRequest
    {
        public int IssuerType { get; set; }
        public int IssuerId { get; set; }
        public int AttachmentId { get; set; }
        public string? Reason { get; set; }
    }

    /// <summary>En rad i kön "att bokföra", så som ytan behöver den.</summary>
    public class UnpostedPaymentRow
    {
        public int Id { get; set; }

        public string PayerName { get; set; } = "";

        /// <summary>Beloppet som ska bokföras — det mottagna om det avvek, annars det begärda.</summary>
        public decimal Amount { get; set; }

        public string Method { get; set; } = "";

        /// <summary>Dagen pengarna togs emot. <b>Det är också bokföringsdatumet.</b></summary>
        public DateTime ConfirmedDate { get; set; }

        /// <summary>Vad betalningen gällde, i klartext.</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>Det uppsättningsformuläret skickar.</summary>
    public class EkonomiSetupRequest
    {
        /// <summary>Ur <see cref="DocumentOwnerType"/>: Club = 0, Region = 1.</summary>
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        public int Year { get; set; }

        /// <summary>Ur <see cref="LedgerIssuerShape"/>. Inget förval — se controllerns kontroll.</summary>
        public string Shape { get; set; } = "";

        /// <summary>Null = 1 januari. Brutet räkenskapsår ska gå att sätta.</summary>
        public DateTime? StartDate { get; set; }

        /// <summary>Null = 31 december.</summary>
        public DateTime? EndDate { get; set; }
    }
}
