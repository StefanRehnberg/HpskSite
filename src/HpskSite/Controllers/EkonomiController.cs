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
        private readonly AdminAuthorizationService _auth;
        private readonly LedgerSetupService _setup;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public EkonomiController(
            AdminAuthorizationService auth,
            LedgerSetupService setup,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager)
        {
            _auth = auth;
            _setup = setup;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int type = 0, int id = 0)
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null)
                return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/ekonomi?type={type}&id={id}")}");

            if (id <= 0) return NotFound();

            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            var node = ctx?.Content?.GetById(id);
            if (node is null) return NotFound();

            // ⚠️ Behörigheten läses ur NODEN, aldrig ur frågesträngen. En kretskod som fick komma
            // från anroparen hade gjort grinden till en fråga om vilken sträng någon skrev.
            string name;
            bool ok;

            if (type == DocumentOwnerType.Club)
            {
                ok = await _auth.IsClubAdminForClub(id);
                name = node.Value<string>("clubName") ?? node.Name ?? "";
            }
            else if (type == DocumentOwnerType.Region)
            {
                var regionCode = node.Value<string>("regionCode") ?? "";
                ok = !string.IsNullOrWhiteSpace(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode);
                name = node.Name ?? "";
            }
            else
            {
                return NotFound();
            }

            if (!ok) return Forbid();

            var status = _setup.GetStatus(type, id);

            return View("~/Views/Ekonomi.cshtml", new EkonomiPageModel
            {
                IssuerType = type,
                IssuerId = id,
                IssuerName = name,
                // Tillbakalänken: kassören ska inte behöva bläddra sig hem.
                BackUrl = node.Url(),
                Shape = status.Shape,
                IsSetUp = status.IsSetUp,
                CurrentYear = status.FiscalYears.FirstOrDefault()?.Year ?? DateTime.Today.Year
            });
        }
    }

    /// <summary>Det sidan behöver innan den ritar något. Resten hämtar den själv.</summary>
    public class EkonomiPageModel
    {
        public int IssuerType { get; set; }

        public int IssuerId { get; set; }

        public string IssuerName { get; set; } = "";

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
