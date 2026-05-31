using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// QR deep-link verify page (/marken/verifiera?t=...). A shooter shows the QR from the entry
    /// modal; a board member / Skjutledare scans it on their own phone, lands here, and (once logged
    /// in + authorized for the series' club) sees the submitted series and Godkänn / Avvisa buttons.
    /// All work is done client-side against the MarkenController surface endpoints
    /// (GetSerieForVerify / VerifySeries / RejectSeries) which re-check authority server-side.
    /// Routed MVC controller — no Umbraco node required (same pattern as /patrullista).
    /// </summary>
    [Route("marken/verifiera")]
    public class MarkenVerifyController : Controller
    {
        [HttpGet("")]
        public IActionResult Index(string? t)
        {
            return View("~/Views/MarkenVerify.cshtml", t ?? "");
        }
    }
}
