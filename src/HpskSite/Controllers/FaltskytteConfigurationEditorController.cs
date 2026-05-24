using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;

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

        public FaltskytteConfigurationEditorController(FaltskytteConfigurationService configService)
        {
            _configService = configService;
        }

        [HttpGet("")]
        public async Task<IActionResult> Edit(int id)
        {
            var config = await _configService.GetByIdAsync(id);
            if (config == null) return NotFound();

            // Browse-level auth is handled by the view (login gate + CanView check
            // via JS API call). We still set a basic Found gate here so a deleted
            // id surfaces a clean 404 rather than an empty editor.

            ViewBag.ConfigurationId = id;
            ViewBag.ConfigurationName = config.Name;
            return View("FaltskytteConfigurationEditor");
        }
    }
}
