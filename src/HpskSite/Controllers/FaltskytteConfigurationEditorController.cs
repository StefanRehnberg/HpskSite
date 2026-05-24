using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// MVC controller for the standalone Fältskytte configuration editor page.
    /// Surface controllers can't easily host parameterized URLs, so this is a
    /// regular Controller with attribute routing.
    /// </summary>
    [Route("faltkonfig/{id:int}/redigera")]
    public class FaltskytteConfigurationEditorController : Controller
    {
        private readonly FaltskytteConfigurationService _configService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public FaltskytteConfigurationEditorController(
            FaltskytteConfigurationService configService,
            IUmbracoContextAccessor umbracoContextAccessor)
        {
            _configService = configService;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        [HttpGet("")]
        public async Task<IActionResult> Edit(int id)
        {
            var config = await _configService.GetByIdAsync(id);
            if (config == null) return NotFound();

            // Master.cshtml inherits UmbracoViewPage and calls Model.Root() / .Url()
            // / .Children — all of which require an IPublishedContent Model.
            // Locate the /faltkonfig hub node and pass it through so the layout works.
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco context unavailable.");

            var hubNode = ctx.Content.GetAtRoot()
                .SelectMany(r => r.DescendantsOrSelf())
                .FirstOrDefault(c => c.ContentType.Alias == "faltskytteConfigurationHub");

            if (hubNode == null)
            {
                return StatusCode(500,
                    "Hub-noden saknas. Skapa en publicerad innehållssida med doctype 'faltskytteConfigurationHub' under Home.");
            }

            ViewBag.ConfigurationId = id;
            ViewBag.ConfigurationName = config.Name;
            return View("FaltskytteConfigurationEditor", hubNode);
        }
    }
}
