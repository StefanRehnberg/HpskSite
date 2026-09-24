using HpskSite.Models;
using HpskSite.Models.Ledger;
using HpskSite.Services;
using HpskSite.Services.Ledger;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kassörens arbetsyta på <c>/ekonomi?type=0&amp;id=123</c> — en EGEN SIDA, inte en flik.
    ///
    /// <para><b>⚠️⚠️ BYGGD MOT <c>Ekonomi Exempel/Ekonomi_Wireframes.pdf</c></b> (sex sidor, skickade
    /// till Fredrik, Varbergs PK för synpunkter). Titta på den innan något ändras här. Skissens
    /// rail — Översikt · Bokför · Avstämning · Rapport · Avgifter · Bokslut — bär sin egen fotnot:
    /// <i>"Menyn är kassörens år, inte databasens tabeller."</i> Sex sådana poster får inte plats
    /// som en flik bland arton i klubbens adminpanel, och det är därför den här sidan finns.</para>
    ///
    /// <para><b>⚠️ Fyra av sex railposter har ingen motor än</b> (Bokför, Avstämning = P11,
    /// Rapport = P8, Bokslut = P7). De ligger ändå i railen och säger vad de ska göra. <b>Fyll dem
    /// ALDRIG med påhittade siffror</b> — skissens egen poäng i panel 2 är att noll fel ska visas
    /// lika stort som fel, och en uppdiktad nolla är raka motsatsen till det.</para>
    ///
    /// <para>Samma mönster som <c>/styrelse?type=0&amp;id=</c>: routad MVC, ingen Umbraco-nod,
    /// egen chrome. Data hämtas av sidan själv från <c>EkonomiAdminController</c>s endpoints, så
    /// det finns exakt en väg till varje uppgift.</para>
    /// </summary>
    [Route("ekonomi")]
    public class EkonomiController : Controller
    {
        private readonly LedgerAccessService _access;
        private readonly LedgerSetupService _setup;
        private readonly LedgerSandboxService _sandbox;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public EkonomiController(
            LedgerAccessService access,
            LedgerSetupService setup,
            LedgerSandboxService sandbox,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager)
        {
            _access = access;
            _setup = setup;
            _sandbox = sandbox;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        /// <param name="type">Ägarens typ ur <see cref="DocumentOwnerType"/>.</param>
        /// <param name="id">Föreningens NOD-id.</param>
        /// <param name="issuer">
        /// Vilken utställare arbetet sker i. Utelämnad = den levande.
        /// <para><b>⚠️ Ligger i URL:en med flit.</b> Vilken liggare man skriver i är för viktigt
        /// för att gömmas i en session: en sparad flik, en delad länk och en skärmdump ska alla
        /// säga vilken det var. En osynlig sessionsväxel hade gjort "vilken bokföring hamnade det
        /// i?" till en fråga ingen kan svara på i efterhand.</para>
        /// </param>
        [HttpGet("")]
        public async Task<IActionResult> Index(int type = 0, int id = 0, int? issuer = null)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null)
                return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/ekonomi?type={type}&id={id}")}");

            if (id <= 0) return NotFound();

            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            var node = ctx?.Content?.GetById(id);
            if (node is null) return NotFound();

            // ⚠️⚠️ SAMMA UPPSLAG SOM ENDPOINTARNA (LedgerAccessService). Skrevs grinden här för
            // hand skulle sidan och API:et förr eller senare svara olika — antingen en sida full
            // av knappar som nekas, eller en endpoint som släpper in någon sidan gömde.
            var access = await _access.ResolveAsync(type, id);
            if (!access.CanRead) return Forbid();

            var name = access.OwnerName;

            // ── Vilken utställare arbetar vi i? ────────────────────────────────────────────
            // ⚠️⚠️ EN LÄSNING SOM SKRIVER — och det är ett val, inte ett förbiseende.
            // EnsureLive skapar föreningens utställarrad om den saknas, alltså även när sidan
            // öppnas av en styrelseledamot som bara läser. Raden är REGISTRET som liggaren hänger
            // på: den bär ingen bokföring, den är idempotent, och dess id är deterministiskt.
            // Alternativet — att låta en läsare möta en sida utan utställare — hade krävt en
            // null-gren i varje läsande endpoint och gett ett sämre tomt läge.
            // ⚠️ Följden att känna till: `LedgerIssuer` är därför INTE ett mått på "föreningar som
            // använder ekonomin". Det måttet är LedgerIssuerSettings, som bara en kassör kan skapa.
            var live = _sandbox.EnsureLive(type, id);
            var active = live;

            // ⚠️ EN LÄSANDE STYRELSELEDAMOT FÅR INGEN SANDLÅDA. Den är kassörens kladdpapper, och
            // en ledamot som läser testsiffror utan att veta att de är påhittade är precis den
            // förväxling den lila ramen finns för att förhindra. De kan inte skapa en heller, så
            // den enda vägen hit vore ett gissat nummer i frågesträngen.
            var mayUseSandbox = access.CanWrite;

            // ⚠️ INTE `issuer is > 0`. Sandlådor har NEGATIVA id, och den kontrollen slängde
            // tyst bort varje sandlådeval — sidan visade den levande liggaren medan URL:en sa
            // sandlåda. Fångat av sviten samma dag rymden byttes.
            if (mayUseSandbox && issuer is not null && issuer != 0 && issuer != live.Id)
            {
                var chosen = _sandbox.GetById(issuer.Value);

                // ⚠️ Utställaren måste tillhöra DEN HÄR föreningen. Utan kontrollen räcker ett
                // gissat nummer i frågesträngen för att stå i en annan klubbs sandlåda — och
                // behörigheten längre in skulle då pröva fel ägare.
                if (chosen is not null && chosen.OwnerType == type && chosen.OwnerId == id && chosen.IsActive)
                    active = chosen;
            }

            var status = _setup.GetStatus(active.OwnerType, active.Id);

            ViewData["EkonomiData"] = new EkonomiPageModel
            {
                IssuerType = active.OwnerType,
                // ⚠️ UTSTÄLLARENS id, inte nodens. För den levande är de samma tal — det är
                // migreringsknepet som gjorde att inga oföränderliga rader behövde röras — men
                // lita aldrig på likheten i kod.
                IssuerId = active.Id,
                OwnerId = id,
                IsSandbox = active.IsSandbox,
                SandboxLabel = active.Label,
                SandboxCreatedUtc = active.IsSandbox ? active.CreatedUtc : null,
                // Läsaren ser bara den riktiga liggaren — inget att välja mellan, ingen växlare.
                Issuers = mayUseSandbox
                    ? _sandbox.ListForOwner(type, id)
                    : new List<HpskSite.Models.Ledger.LedgerIssuer> { live },
                CanWrite = access.CanWrite,
                AccessBasis = access.Basis,
                CanManageWriteGrants = access.CanManageWriteGrants,
                IsVatRegistered = status.IsVatRegistered,
                TreasurerName = access.TreasurerName,
                IssuerName = name,
                // Tillbakalänken: kassören ska inte behöva bläddra sig hem.
                BackUrl = node.Url(),
                Shape = status.Shape,
                IsSetUp = status.IsSetUp,
                CurrentYear = status.FiscalYears.FirstOrDefault()?.Year ?? DateTime.Today.Year
            };

            // ⚠️ Rotnoden skickas som Model för att `Master.cshtml` ska kunna rendera sajtens ram;
            // sidans egna data går via ViewData. Exakt samma recept som /styrelse — och det är
            // också skälet att ytan har sajtens header: en ARBETSYTA behåller chromet, bara
            // utskrifter (protokoll, kvitto) tappar den.
            var rootNode = ctx?.Content?.GetAtRoot().FirstOrDefault();
            if (rootNode is null) return StatusCode(500, "Ingen rotnod hittades.");

            return View("Ekonomi", rootNode);
        }
    }

    /// <summary>Det sidan behöver innan den ritar något. Resten hämtar den själv.</summary>
    public class EkonomiPageModel
    {
        public int IssuerType { get; set; }

        /// <summary>Utställarens id — det bokföringen skrivs mot. <b>Inte nodens id.</b></summary>
        public int IssuerId { get; set; }

        /// <summary>Föreningens nod-id. Behövs för länkar tillbaka och för att byta utställare.</summary>
        public int OwnerId { get; set; }

        /// <summary>
        /// ⚠️⚠️ Arbetar vi i en sandlåda? Styr sidans märkning. <b>Allt som produceras i en
        /// sandlåda måste bära den synligt</b> — ett sandlådekvitto som ser äkta ut är en
        /// handling som ljuger.
        /// </summary>
        public bool IsSandbox { get; set; }

        public string? SandboxLabel { get; set; }

        public DateTime? SandboxCreatedUtc { get; set; }

        /// <summary>Föreningens aktiva utställare — den levande först, sedan sandlådan.</summary>
        public List<HpskSite.Models.Ledger.LedgerIssuer> Issuers { get; set; } = new();

        public string IssuerName { get; set; } = "";

        /// <summary>
        /// Får den inloggade bokföra, eller bara läsa?
        ///
        /// <para><b>⚠️ Styrelsen läser, kassören skriver</b> — se <see cref="LedgerAccessService"/>.
        /// Ytan får INTE rendera en skrivkontroll när det här är falskt; en knapp som nekas är
        /// sämre än ingen knapp. Den riktiga gränsen ligger ändå i endpointarna, men en sida som
        /// erbjuder något den inte kan leverera läses som trasig.</para>
        ///
        /// <para><b>⚠️ Och frånvaron måste FÖRKLARAS.</b> En yta där knapparna bara saknas läses
        /// som ett fel; därför står det skrivet vem som bokför.</para>
        /// </summary>
        public bool CanWrite { get; set; } = true;

        /// <summary>
        /// VARFÖR den inloggade får det hen får — styr rälsens roll och förklaringen. Se
        /// <see cref="HpskSite.Services.Ledger.LedgerAccessBasis"/>.
        /// </summary>
        public HpskSite.Services.Ledger.LedgerAccessBasis AccessBasis { get; set; }

        /// <summary>
        /// Får den inloggade ge andra rätten att arbeta med ekonomin? ⚠️ Kan vara sann för en
        /// LÄSARE (ordföranden), så kontrollerna på Behörigheter får aldrig gatas på CanWrite.
        /// </summary>
        public bool CanManageWriteGrants { get; set; }

        /// <summary>Momsregistrerad — styr om fliken Moms (deklarationens underlag) finns.</summary>
        public bool IsVatRegistered { get; set; }

        /// <summary>Kassörens namn när den inloggade läser. Tomt = ingen kassör registrerad.</summary>
        public string TreasurerName { get; set; } = "";

        public string BackUrl { get; set; } = "/";

        /// <summary>Ur <see cref="LedgerIssuerShape"/>. Tomt = föreningen har inte valt.</summary>
        public string Shape { get; set; } = "";

        public bool IsSetUp { get; set; }

        public int CurrentYear { get; set; }

        /// <summary>Brickan uppe till höger, som i skissen: "Hel bokföring" / "Avgifter och export".</summary>
        public string ShapeLabel => Shape switch
        {
            LedgerIssuerShape.FullLedger => "Hel bokföring",
            LedgerIssuerShape.FeesAndExport => "Avgifter och export",
            LedgerIssuerShape.FeesOnly => "Avgifter och kontroll",
            _ => "Inte uppsatt"
        };

        public bool KeepsBooks => LedgerIssuerShape.KeepsBooks(Shape);
    }
}
