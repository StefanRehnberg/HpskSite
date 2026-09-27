using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>Vad sidan /allt behöver utöver den statiska kartan.</summary>
    public class OmfattningViewModel
    {
        /// <summary>Sidans egen absoluta adress, alltså det QR-koden pekar på.</summary>
        public string PageUrl { get; init; } = "";

        /// <summary>Adressen som den visas under koden, utan schema.</summary>
        public string DisplayUrl { get; init; } = "";

        /// <summary>QR-koden som SVG, eller null om den inte gick att bygga.</summary>
        public string? QrSvg { get; init; }

        public bool IsLoggedIn { get; init; }
        public string LoginUrl { get; init; } = "/login-register";
    }

    /// <summary>
    /// /allt — den zoombara kartan över allt som ryms i pistol.nu.
    ///
    /// <para>Routad controller utan Umbraco-nod, samma mönster som /siktbild: innehållet är fast och
    /// bor i <c>wwwroot/js/omfattning-data.js</c>, så en doctype och en backoffice-nod hade bara varit
    /// ännu en sak att glömma vid deploy.</para>
    ///
    /// <para><b>⚠️ Sidan är PUBLIK med flit.</b> Den ska kunna visas för en kompis på skjutbanan via
    /// QR-koden, och den kompisen har inget konto. Den visar funktioner, aldrig data.</para>
    ///
    /// <para><b>⚠️ Adressen är kort med flit.</b> Varje tecken i den är moduler i QR-koden, och koden
    /// ska gå att skanna från en telefonskärm på armlängds avstånd. Byt inte till en längre slug.</para>
    /// </summary>
    [Route("allt")]
    public class OmfattningController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public OmfattningController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index()
        {
            // Adressen byggs ur förfrågan, så att koden pekar rätt i dev och i prod.
            var host = Request.Host.Value;
            var pageUrl = $"{Request.Scheme}://{host}/allt";

            // Samma uppslag av inloggningssidan som Master.cshtml och Om-sidan gör.
            var loginUrl = "/login-register";
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                var loginPage = root?.Children?.FirstOrDefault(x => x.Name == "Login & Register")
                                ?? root?.Children?.FirstOrDefault(x => x.Name.ToLower().Contains("login"));
                var url = loginPage?.Url();
                if (!string.IsNullOrEmpty(url)) loginUrl = url;
            }

            var model = new OmfattningViewModel
            {
                PageUrl = pageUrl,
                DisplayUrl = $"{host}/allt",
                QrSvg = QrSvg(pageUrl),
                IsLoggedIn = await _memberManager.GetCurrentMemberAsync() != null,
                LoginUrl = loginUrl,
            };

            return View("Omfattning", model);
        }

        /// <summary>
        /// QR-koden som SVG med viewBox, så den skalar rent till vilken storlek som helst — samma
        /// form som vapenetiketterna (<c>FirearmAdminController.QrSvg</c>). ECC-nivå M räcker: koden
        /// visas på en skärm, inte på metall i ett valv.
        /// </summary>
        private static string? QrSvg(string url)
        {
            try
            {
                var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
                var side = data.ModuleMatrix.Count * 10;
                return new QRCoder.SvgQRCode(data).GetGraphic(
                    new SixLabors.ImageSharp.Size(side, side),
                    darkColorHex: "#000000",
                    lightColorHex: "#FFFFFF",
                    drawQuietZones: true,
                    sizingMode: QRCoder.SvgQRCode.SizingMode.ViewBoxAttribute);
            }
            catch
            {
                return null;
            }
        }
    }
}
