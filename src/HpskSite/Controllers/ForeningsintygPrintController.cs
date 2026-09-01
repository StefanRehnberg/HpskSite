using HpskSite.Models;
using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Utskriftsvänligt <b>Föreningsintyg</b> — bilaga till ansökan om tillstånd att inneha
    /// skjutvapen för målskjutning, efter Polisens blankett PM 551.24.
    ///
    /// Två ingångar, med olika innehåll och samma grind:
    /// <list type="bullet">
    /// <item><c>/foreningsintyg/{id}</c> — ett UTFÄRDAT intyg, återgivet ur sin snapshot. Aldrig
    /// omräknat ur dagens data: varje fält kan ha ändrats sedan styrelsen skrev under, och en
    /// återutskrift som tyst visar något annat än originalet är värdelös som handling.</item>
    /// <item><c>/foreningsintyg/utkast?memberId=&amp;clubId=&amp;year=</c> — ett UTKAST, för
    /// granskning innan utfärdande. Vattenstämplat "UTKAST" så det inte kan misstas för ett
    /// undertecknat intyg.</item>
    /// </list>
    ///
    /// Routad MVC-controller utan Umbraco-nod, chromeless vy och typad modell — samma mönster som
    /// <see cref="ReceiptController"/> och <c>FaltskyttePrintController</c>. Ingen serverside-PDF:
    /// husmönstret i den här kodbasen är HTML + webbläsarens utskrift.
    /// </summary>
    [Route("foreningsintyg")]
    public class ForeningsintygPrintController : Controller
    {
        private readonly ForeningsintygService _log;
        private readonly ForeningsintygDocumentService _builder;
        private readonly IMemberService _memberService;
        private readonly IMemberManager _memberManager;
        private readonly AdminAuthorizationService _auth;
        private readonly ILogger<ForeningsintygPrintController> _logger;

        public ForeningsintygPrintController(
            ForeningsintygService log,
            ForeningsintygDocumentService builder,
            IMemberService memberService,
            IMemberManager memberManager,
            AdminAuthorizationService auth,
            ILogger<ForeningsintygPrintController> logger)
        {
            _log = log;
            _builder = builder;
            _memberService = memberService;
            _memberManager = memberManager;
            _auth = auth;
            _logger = logger;
        }

        // ── Utfärdat intyg ───────────────────────────────────────────

        [HttpGet("{id:int}")]
        public async Task<IActionResult> Index(int id)
        {
            var current = await CurrentMemberAsync();
            if (current == null) return LoginRedirect($"/foreningsintyg/{id}");

            var entry = _log.GetById(id);
            if (entry == null) return NotFound();

            if (!await MayRead(current, entry.MemberId, entry.ClubId))
                return View("~/Views/ForeningsintygPrint.cshtml", Denied());

            var doc = ForeningsintygDocument.FromSnapshot(entry.Snapshot);
            if (doc == null)
            {
                // Utfärdat innan snapshot fanns, eller en snapshot som inte går att tolka. Säg det —
                // bygg ALDRIG ett nytt intyg ur dagens data och kalla det en återutskrift.
                return View("~/Views/ForeningsintygPrint.cshtml", new ForeningsintygPrintModel
                {
                    NotReprintable = true,
                    IssuedDate = entry.IssuedDate,
                    MemberName = entry.MemberName ?? $"Medlem {entry.MemberId}"
                });
            }

            return View("~/Views/ForeningsintygPrint.cshtml", new ForeningsintygPrintModel
            {
                Document = doc,
                IssueId = entry.Id,
                IssuedDate = entry.IssuedDate,
                IssuedByName = entry.IssuedByName,
                MemberName = entry.MemberName ?? doc.HelaNamnet
            });
        }

        // ── Utkast ───────────────────────────────────────────────────

        [HttpGet("utkast")]
        public async Task<IActionResult> Draft(int memberId, int clubId, int? year = null)
        {
            var current = await CurrentMemberAsync();
            if (current == null)
                return LoginRedirect($"/foreningsintyg/utkast?memberId={memberId}&clubId={clubId}");

            // ⚠️ Ett utkast avslöjar personuppgifter och ska grindas som det utfärdade intyget.
            if (!await MayRead(current, memberId, clubId))
                return View("~/Views/ForeningsintygPrint.cshtml", Denied());

            var doc = await _builder.BuildDraftAsync(memberId, clubId, year ?? DateTime.Today.Year);
            if (doc == null) return NotFound();

            return View("~/Views/ForeningsintygPrint.cshtml", new ForeningsintygPrintModel
            {
                Document = doc,
                IsDraft = true,
                MemberName = doc.HelaNamnet
            });
        }

        // ── Grind ────────────────────────────────────────────────────

        /// <summary>
        /// Samma grind som intygsloggen och aktivitetsunderlaget: medlemmen själv, klubbadmin för
        /// medlemmens primära klubb, eller sajtadmin. <b>Klubben på RADEN kontrolleras också</b> —
        /// ett intyg utfärdat av klubb A ska inte kunna läsas av klubb B:s admin bara för att
        /// medlemmen sedan bytte primär klubb.
        /// </summary>
        private async Task<bool> MayRead(Umbraco.Cms.Core.Models.IMember current, int memberId, int clubId)
        {
            if (current.Id == memberId) return true;
            if (await _auth.IsCurrentUserAdminAsync()) return true;

            if (clubId > 0 && await _auth.IsClubAdminForClub(clubId)) return true;

            var candidate = _memberService.GetById(memberId);
            if (candidate == null) return false;
            int.TryParse(candidate.GetValue<string>("primaryClubId") ?? "", out int primaryClubId);
            return primaryClubId > 0 && await _auth.IsClubAdminForClub(primaryClubId);
        }

        private static ForeningsintygPrintModel Denied() => new() { AccessDenied = true };

        /// <summary>
        /// Inte <c>Forbid()</c>: det omdirigerar till /Account/AccessDenied, som inte är ett
        /// Umbraco-dokument — besökaren får då en rå 404 som citerar en intern sökväg.
        /// </summary>
        private IActionResult LoginRedirect(string returnUrl) =>
            Redirect($"/login-register?returnUrl={Uri.EscapeDataString(returnUrl)}");

        private async Task<Umbraco.Cms.Core.Models.IMember?> CurrentMemberAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current == null) return null;
            return _memberService.GetByEmail(current.Email ?? "");
        }
    }

    /// <summary>Vad utskriftsvyn behöver. Bär både det lyckade och de tre misslyckade utfallen, så
    /// vyn kan säga vad som är fel i stället för att rendera ett tomt intyg.</summary>
    public class ForeningsintygPrintModel
    {
        public ForeningsintygDocument? Document { get; set; }

        public int? IssueId { get; set; }
        public DateTime? IssuedDate { get; set; }
        public string? IssuedByName { get; set; }
        public string MemberName { get; set; } = "";

        /// <summary>Utkast — vattenstämplas, får inte kunna misstas för ett undertecknat intyg.</summary>
        public bool IsDraft { get; set; }

        /// <summary>Raden finns men saknar snapshot (utfärdad före den fanns).</summary>
        public bool NotReprintable { get; set; }

        public bool AccessDenied { get; set; }

        // ── Hjälpare för utskriftsvyn ────────────────────────────────
        //
        // De bor HÄR och inte i ett @functions-block i vyn: lokala hjälpfunktioner i en Razor-vy är
        // en klammerfälla som fallerar med ett meddelandelöst UmbracoCompilationException, och
        // kodbasen har redan betalat för den lärdomen (ScheduleItem bär sina egna etiketter av
        // exakt samma skäl).

        /// <summary>Kryssets innehåll. Blanketten kryssas med X.</summary>
        public string Mark(bool on) => on ? "X" : "";

        /// <summary>
        /// Ett saknat värde ska SYNAS som en punktad lucka. En tom cell läses som "ifylld och tom",
        /// och skillnaden mot "vi vet inget" är precis den en handläggare behöver se.
        /// </summary>
        public string ValClass(string? value) => string.IsNullOrWhiteSpace(value) ? "val empty" : "val";
    }
}
