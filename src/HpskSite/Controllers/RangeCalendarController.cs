using System.Text;
using HpskSite.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Banans kalender som prenumeration (<c>/skjutbana/kalender.ics?t=…</c>), och den gamla
    /// sidadressen (<c>/skjutbana/kalender?r=</c>) som nu leder till delen Kalender på banans sida
    /// (<see cref="RangePageController"/>). Routad controller, ingen Umbraco-nod.
    /// </summary>
    public class RangeCalendarController : Controller
    {
        private readonly RangeCalendarService _calendar;
        private readonly IDataProtectionProvider _protection;
        private readonly ILogger<RangeCalendarController> _logger;

        public RangeCalendarController(RangeCalendarService calendar, IDataProtectionProvider protection, ILogger<RangeCalendarController> logger)
        {
            _calendar = calendar;
            _protection = protection;
            _logger = logger;
        }

        /// <summary>
        /// Den fristående kalendersidan finns inte längre: kalendern är en del på banans egen sida
        /// (2026-10-11). Adressen leder dit, så länkar i mejl och bokmärken fortsätter fungera.
        /// </summary>
        [HttpGet("skjutbana/kalender")]
        public IActionResult Index(int r)
            => r > 0 ? Redirect($"/skjutbana/{r}#vy=kalender") : Redirect("/skjutbanor");

        /// <summary>
        /// Prenumerationsfilen. Ingen inloggning — en kalenderapp kan inte logga in — så den signerade
        /// länken är behörigheten. Ett halvår bakåt och ett år framåt.
        /// </summary>
        [HttpGet("skjutbana/kalender.ics")]
        public IActionResult Ics(string? t)
        {
            int rangeId;
            try
            {
                var payload = _protection.CreateProtector(RangeCalendarApiController.IcsPurpose).Unprotect(t ?? "");
                if (!payload.StartsWith("range:", StringComparison.Ordinal) || !int.TryParse(payload[6..], out rangeId))
                    return NotFound();
            }
            catch
            {
                // Ogiltig eller manipulerad länk ser likadan ut som en okänd — skillnaden hjälper bara den som gissar.
                return NotFound();
            }

            var (range, _) = _calendar.GetRangeWithSections(rangeId);
            if (range == null) return NotFound();
            try
            {
                var items = _calendar.Build(rangeId, DateTime.Today.AddMonths(-6), DateTime.Today.AddYears(1));
                var ics = _calendar.BuildIcs(rangeId, range.Name, items, $"{Request.Scheme}://{Request.Host}");
                return File(Encoding.UTF8.GetBytes(ics), "text/calendar; charset=utf-8");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Banans kalenderfil kunde inte byggas (bana {RangeId})", rangeId);
                return StatusCode(500);
            }
        }
    }
}
