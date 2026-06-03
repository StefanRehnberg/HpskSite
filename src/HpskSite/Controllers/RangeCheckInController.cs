using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// QR check-in/out page (/skjutbana/incheckning?r=&lt;rangeId&gt;). A shooter scans the QR posted at
    /// the range, lands here, and (once logged in) checks in — then on the way out scans again and
    /// enters how many shots they fired. All work is done client-side against the ShootingRange surface
    /// endpoints (CheckInStatus / CheckIn / CheckOut), which re-check membership server-side.
    /// Routed MVC controller — no Umbraco node required (same pattern as /marken/verifiera, /patrullista).
    /// </summary>
    [Route("skjutbana/incheckning")]
    public class RangeCheckInController : Controller
    {
        [HttpGet("")]
        public IActionResult Index(int r)
        {
            return View("~/Views/RangeCheckIn.cshtml", r);
        }
    }
}
