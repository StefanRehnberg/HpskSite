using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Medlemmarnas motionssida: <c>/motion?klubb=ID</c> (motioner till klubbens årsmöte) och
    /// <c>/motion?krets=ID</c> (klubbarnas motioner till kretsårsmötet). Routad controller, ingen
    /// Umbraco-nod — samma mönster som /styrelse och /min-kurs.
    ///
    /// <para>Sidan är EN dörr: medlemmen lämnar en motion, ser föreningens motioner med namn
    /// (Stefan 2026-10-08: motionerna syns för alla medlemmar), kan sätta sitt namn på en motion
    /// eller ge tummen upp eller ner, och följer sin egen motion till årsmötets beslut. Allt läses
    /// och skrivs genom <see cref="BoardWorkController"/>; här finns bara sidan.</para>
    /// </summary>
    [Route("motion")]
    public class MotionController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;

        public MotionController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int? klubb, int? krets)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var self = $"/motion?{(krets.HasValue ? "krets=" + krets : "klubb=" + klubb)}";
            var member = await _memberManager.GetCurrentMemberAsync();
            if (member?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString(self)}");

            int ownerType = krets.HasValue ? DocumentOwnerType.Region : DocumentOwnerType.Club;
            int ownerId = krets ?? klubb ?? 0;
            string backUrl = "/";
            try
            {
                var node = ownerId > 0 ? ctx.Content.GetById(ownerId) : null;
                if (node != null) backUrl = node.Url();
            }
            catch { /* ← tillbaka faller på startsidan */ }

            ViewData["MotionOwnerType"] = ownerType;
            ViewData["MotionOwnerId"] = ownerId;
            ViewData["MotionBackUrl"] = backUrl;
            return View("Motion", rootNode);
        }
    }
}
