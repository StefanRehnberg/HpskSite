using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Landningssidan för närvaro-QR:en (<c>/evenemang/narvaro?t=...</c>). En deltagare skannar
    /// affischen på plats, landar här och registrerar sin egen närvaro.
    ///
    /// Routad MVC-controller — ingen Umbraco-nod behövs (samma mönster som /marken/verifiera och
    /// /patrullista). Allt arbete görs mot <see cref="ClubEventController"/>s endpoints, som gör om
    /// varje kontroll på servern: sidan är chromelös och bär ingen behörighet i sig.
    /// </summary>
    [Route("evenemang/narvaro")]
    public class ClubEventCheckInController : Controller
    {
        [HttpGet("")]
        public IActionResult Index(string? t)
        {
            return View("~/Views/ClubEventCheckIn.cshtml", t ?? "");
        }
    }
}
