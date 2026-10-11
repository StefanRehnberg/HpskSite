using System.Text;
using HpskSite.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Banans kalender som sida (<c>/skjutbana/kalender?r=ID</c>) och som prenumeration
    /// (<c>/skjutbana/kalender.ics?t=…</c>). Routad controller, ingen Umbraco-nod — samma mönster som
    /// /skjutbana/incheckning och /motion. Sidan läser och skriver genom
    /// <see cref="RangeCalendarApiController"/>.
    /// </summary>
    public class RangeCalendarController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly RangeCalendarService _calendar;
        private readonly IDataProtectionProvider _protection;
        private readonly ILogger<RangeCalendarController> _logger;

        public RangeCalendarController(IUmbracoContextAccessor umbracoContextAccessor, IMemberManager memberManager,
            RangeCalendarService calendar, IDataProtectionProvider protection, ILogger<RangeCalendarController> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _calendar = calendar;
            _protection = protection;
            _logger = logger;
        }

        [HttpGet("skjutbana/kalender")]
        public async Task<IActionResult> Index(int r)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var member = await _memberManager.GetCurrentMemberAsync();
            if (member?.Email == null)
                return Redirect($"/login-register/?tab=login&returnUrl={Uri.EscapeDataString($"/skjutbana/kalender?r={r}")}");

            ViewData["RangeCalendarRangeId"] = r;
            return View("RangeCalendar", rootNode);
        }

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
