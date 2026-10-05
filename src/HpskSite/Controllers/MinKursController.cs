using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Fas D: <c>/min-kurs</c> — instruktörens yta för kursen (träningsgruppen). Mobil först: dagens
    /// tillfälle överst med närvaro och serier per deltagare, anteckningen från förra tillfället, och
    /// kursöversikten (deltagare × tillfällen) längre ned.
    ///
    /// <para>Routad, ingen Umbraco-nod — samma mönster som <c>/styrelse</c>: rotnoden som modell så
    /// att layoutens <c>Model.Root()</c> fungerar. All data hämtas av sidan genom
    /// <c>TrainingCourse/*</c>, som grindar varje anrop själv; sidan visar inget utan dem.</para>
    /// </summary>
    [Route("min-kurs")]
    public class MinKursController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public MinKursController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int? g)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
                return Redirect("/login-register/?tab=login&returnUrl=" + Uri.EscapeDataString("/min-kurs" + (g is > 0 ? $"?g={g}" : "")));

            ViewData["MinKursGroupId"] = g ?? 0;
            return View("MinKurs", root);
        }
    }
}
