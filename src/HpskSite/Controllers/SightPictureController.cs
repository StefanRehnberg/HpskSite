using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// MVC controller for the standalone "Siktbild" (sight picture) training simulator.
    /// It is a purely front-end illustration — no DB, no content management — so a routed
    /// Controller is cleaner than an Umbraco content node + doctype (works on deploy, no
    /// backoffice setup). Same pattern as <see cref="FaltskytteConfigurationEditorController"/>.
    /// </summary>
    [Route("siktbild")]
    public class SightPictureController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public SightPictureController(IUmbracoContextAccessor umbracoContextAccessor)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        [HttpGet("")]
        public IActionResult Index()
        {
            // Master.cshtml inherits UmbracoViewPage and calls Model.Root() / .Url() / .Children,
            // all of which require an IPublishedContent Model. Pass the site root (Home) node so
            // the shared layout renders normally.
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco context unavailable.");

            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null)
                return StatusCode(500, "Ingen rotnod hittades.");

            return View("SightPicture", rootNode);
        }
    }
}
