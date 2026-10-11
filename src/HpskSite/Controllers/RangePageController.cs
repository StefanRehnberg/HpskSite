using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Banans egen sida, <c>/skjutbana/{id}</c> (2026-10-11). Ersätter modalen över kartan på
    /// /skjutbanor: samma skal som Ekonomin och Min kurs, med banans delar i rälsen och delen i
    /// adressen (<c>#vy=…</c>). Routad, ingen Umbraco-nod. All data går genom de befintliga
    /// endpointsen på <see cref="ShootingRangeController"/> och <see cref="RangeCalendarApiController"/>,
    /// som prövar behörigheten själva — sidan bär bara id:t.
    /// </summary>
    public class RangePageController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public RangePageController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        [HttpGet("skjutbana/{id:int}")]
        public async Task<IActionResult> Index(int id)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            // Skjutbanedatabasen är för inloggade medlemmar, som tidigare.
            var member = await _memberManager.GetCurrentMemberAsync();
            if (member?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/skjutbana/{id}")}");

            ViewData["RangePageId"] = id;
            return View("RangePage", rootNode);
        }
    }
}
