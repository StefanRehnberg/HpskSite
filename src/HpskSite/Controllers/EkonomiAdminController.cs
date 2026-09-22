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
        private readonly LedgerManualPostingService _manualPosting;
        private readonly LedgerMembershipFeeBridge _feeBridge;
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
            LedgerManualPostingService manualPosting,
            LedgerMembershipFeeBridge feeBridge,
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
            _manualPosting = manualPosting;
            _feeBridge = feeBridge;
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
            if (issuerId <= 0) return (false, "");

            var node = UmbracoContext.Content?.GetById(issuerId);
            if (node is null) return (false, "");

            if (issuerType == DocumentOwnerType.Club)
            {
                var ok = await _authService.IsClubAdminForClub(issuerId);
                return (ok, node.Value<string>("clubName") ?? node.Name ?? "");
            }

            if (issuerType == DocumentOwnerType.Region)
            {
                // ⚠️ Koden ur NODEN. Kom den ur anropet vore grinden bara en fråga om vilken
                // sträng klienten råkade skicka.
                var regionCode = node.Value<string>("regionCode") ?? "";
                if (string.IsNullOrWhiteSpace(regionCode)) return (false, "");

                var ok = await _authService.IsRegionalAdminForRegion(regionCode);
                return (ok, node.Name ?? "");
            }

            // Ett tredje utställarslag finns inte. Att svara nej är rätt svar, inte ett fel.
            return (false, "");
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

            // Beredskapen ägs av betalvägens egen spärr — vi frågar den, vi bedömer inte själva.
            // Rollmappningen lägger vi till, för den kontrollerar spärren inte.
            if (status.IsSetUp && LedgerIssuerShape.KeepsBooks(status.Shape))
            {
                var blocked = _postingService.PostingBlockedReason(issuerType, issuerId, DateTime.Today);

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
        /// Läser upp föreningens ekonomiuppsättning. Ytan ritar sig helt ur det här svaret, så
        /// "inte uppsatt" måste vara ett giltigt svar med 200 — inte ett fel.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSetupStatus(int issuerType, int issuerId)
        {
            var (ok, name) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

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
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

            try
            {
                return Json(new { success = true, overview = _overviewService.Build(issuerType, issuerId) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte bygga ekonomiöversikten för utställare {Typ}/{Id}.", issuerType, issuerId);

                return Json(new { success = false, message = "Översikten gick inte att läsa just nu." });
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
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

            if (issuerType != DocumentOwnerType.Club)
                return Json(new { success = true, applicable = false });

            try
            {
                return Json(new
                {
                    success = true,
                    applicable = true,
                    fees = _feeBridge.Summarise(issuerId, year)
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

            var (ok, name) = await AuthorizeIssuerAsync(DocumentOwnerType.Club, request.ClubId);
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

            var (actorId, _) = await GetCurrentActorAsync();
            if (actorId is null)
                return Json(new { success = false, message = "Du måste vara inloggad för att bokföra." });

            try
            {
                var year = request.Year > 0 ? request.Year : DateTime.Today.Year;
                var count = _feeBridge.PostPending(request.ClubId, year, actorId.Value);

                _logger.LogInformation(
                    "Ekonomi: {Forening} efterbokförde {Antal} medlemsavgifter för {Ar}.", name, count, year);

                return Json(new
                {
                    success = true,
                    posted = count,
                    fees = _feeBridge.Summarise(request.ClubId, year)
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
            var (ok, _) = await AuthorizeIssuerAsync(issuerType, issuerId);
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

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

            var (ok, name) = await AuthorizeIssuerAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

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
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

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
            if (request is null || request.PaymentId <= 0)
                return Json(new { success = false, message = "Ingen betalning angiven." });

            var payment = _paymentService.GetById(request.PaymentId);
            if (payment is null)
                return Json(new { success = false, message = "Betalningen finns inte." });

            var (ok, name) = await AuthorizeIssuerAsync(payment.IssuerType, payment.IssuerId);
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

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

            var (ok, name) = await AuthorizeIssuerAsync(request.IssuerType, request.IssuerId);
            if (!ok) return Json(new { success = false, message = "Du har inte behörighet till den här föreningens ekonomi." });

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
    }

    /// <summary>Det avgiftskortet skickar.</summary>
    public class PostMembershipFeesRequest
    {
        public int ClubId { get; set; }
        public int Year { get; set; }
    }

    /// <summary>Det köytan skickar när en rad ska bokföras.</summary>
    public class PostPendingRequest
    {
        public int PaymentId { get; set; }
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
