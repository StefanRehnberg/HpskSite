using System.Globalization;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.CompetitionCalls;
using HpskSite.Services.Kretsgranskning;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Kretsens utlysningar (kretsutlysningen F2): kretsen utlyser en tävling, och utser sedan en klubb att arrangera den.
    /// Klubbarnas önskemål (F3) och <i>Skapa tävlingen</i> (F4) kommer ovanpå.
    ///
    /// <para><b>⚠️ Behörigheten prövas mot kretsen som SERVERNS data pekar ut</b> — utlysningens <c>RegionId</c> — aldrig mot ett
    /// kretsnummer klienten skickar (utom när en NY utlysning läggs till; då prövas just det numret). Regeln är
    /// <see cref="CompetitionCallAccess.CanManageCallsForRegion"/>: sajtadmin, kretsadmin, kretsstyrelsen och kretsens
    /// tävlingsansvarige.</para>
    ///
    /// <para>Orden i texterna: <b>utlysa · utse</b>. Aldrig "ansök" (klubb → krets) eller "tilldela".</para>
    /// </summary>
    [HpskSite.Services.Security.InCodeAuth("CompetitionCallAccess.CanManageCallsForRegion(utlysningens krets): sajtadmin, kretsadmin, kretsstyrelse, tävlingsansvarig")]
    public class CompetitionCallController : SurfaceController
    {
        private readonly CompetitionCallService _calls;
        private readonly CompetitionCallAccess _access;
        private readonly KretsCalendarService _krets;
        private readonly ILogger<CompetitionCallController> _logger;

        public CompetitionCallController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CompetitionCallService calls, CompetitionCallAccess access, KretsCalendarService krets,
            ILogger<CompetitionCallController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _calls = calls;
            _access = access;
            _krets = krets;
            _logger = logger;
        }

        private static IActionResult Deny() => new JsonResult(new { success = false, message = "Du har inte behörighet att hantera kretsens utlysningar." });

        /// <summary>Kretsens utlysningar ett år, med vad varje rad får göra (och varför inte).</summary>
        [HttpGet]
        public async Task<IActionResult> GetCalls(int regionId, int? year)
        {
            if (!await _access.CanManageCallsForRegion(regionId)) return Deny();
            try
            {
                var y = year ?? DateTime.Today.Year + 1;
                var list = _calls.ForRegion(regionId, y);
                var clubs = _krets.ClubsInRegion(regionId);
                var years = _calls.YearsFor(regionId);
                var thisYear = DateTime.Today.Year;
                for (var i = thisYear; i <= thisYear + 2; i++) if (!years.Contains(i)) years.Add(i);
                if (!years.Contains(y)) years.Add(y);
                return Json(new
                {
                    success = true,
                    year = y,
                    years = years.OrderByDescending(v => v),
                    clubs = clubs.OrderBy(c => c.Value, StringComparer.CurrentCultureIgnoreCase).Select(c => new { id = c.Key, name = c.Value }),
                    disciplines = HpskSite.Models.CompetitionTypes.All.Select(t => new { value = t.Id, label = t.Name }),
                    calls = list.Select(c => Dto(c, clubs))
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kretsens utlysningar kunde inte läsas (krets {Id}).", regionId);
                return Json(new { success = false, message = "Utlysningarna kunde inte läsas just nu. Försök igen om en stund." });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetEvents(int id)
        {
            var c = _calls.Get(id);
            if (c == null || !await _access.CanManageCallsForRegion(c.RegionId)) return Deny();
            return Json(new
            {
                success = true,
                events = _calls.Events(id).Select(e => new
                {
                    at = e.At.ToString("yyyy-MM-dd HH:mm"),
                    by = string.IsNullOrWhiteSpace(e.ByName) ? "Systemet" : e.ByName,
                    label = CompetitionCallEventKind.Label(e.Kind),
                    text = e.Text
                })
            });
        }

        /// <summary>
        /// Lägger till (id = 0) eller ändrar en utlysning. <c>expectedVersion</c> är versionen sidan läste; har någon annan
        /// ändrat sedan dess vägras sparningen. <c>publish=1</c> (bara en ny) utlyser den direkt.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveCall(int id, int regionId, int year, string? name, string? description, string? discipline,
            string? date, bool locked, string? wishDeadline, bool wishesVisible, int expectedVersion, bool publish)
        {
            int region = regionId;
            CompetitionCall? existing = null;
            if (id > 0)
            {
                existing = _calls.Get(id);
                if (existing == null) return Json(new { success = false, message = "Utlysningen hittades inte." });
                region = existing.RegionId;   // serverns kretsnummer, aldrig klientens
            }
            if (!await _access.CanManageCallsForRegion(region)) return Deny();

            if (!TryDate(date, out var d)) return Json(new { success = false, message = "Datumet kunde inte läsas. Skriv det som ÅÅÅÅ-MM-DD." });
            if (!TryDate(wishDeadline, out var wd)) return Json(new { success = false, message = "Sista dag för önskemål kunde inte läsas. Skriv den som ÅÅÅÅ-MM-DD." });

            var edit = new CompetitionCall
            {
                RegionId = region, Year = year, Name = name ?? "", Description = description, Discipline = ValidDiscipline(discipline),
                CompetitionDate = d, LockedByKrets = locked && d.HasValue, WishDeadline = wd, WishesVisibleToClubs = wishesVisible,
                SeriesId = existing?.SeriesId, ChampionshipScope = existing?.ChampionshipScope,
            };
            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            var r = id > 0
                ? _calls.Update(id, edit, expectedVersion, actor, actorName)
                : _calls.Create(edit, publish, actor, actorName);
            return Result(r, id > 0 ? "Ändringarna är sparade." : (publish ? "Tävlingen är utlyst." : "Utlysningen är tillagd."));
        }

        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> PublishCall(int id) => Act(id, (c, a, n) => _calls.Publish(id, a, n), "Tävlingen är utlyst – klubbarna kan nu önska den.");

        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> WithdrawCall(int id) => Act(id, (c, a, n) => _calls.Withdraw(id, a, n), "Utlysningen är tillbaka i planering.");

        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> UnawardCall(int id) => Act(id, (c, a, n) => _calls.Unaward(id, a, n), "Utnämningen är tillbakatagen.");

        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> CancelCall(int id) => Act(id, (c, a, n) => _calls.Cancel(id, a, n), "Utlysningen är inställd.");

        /// <summary>Utse en klubb. Klubben måste höra till utlysningens krets (kretsens klubblista, serverns data).</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> AwardCall(int id, int clubId) => Act(id, (c, a, n) =>
        {
            if (clubId <= 0) return new(null, "Välj en klubb.");
            var clubs = _krets.ClubsInRegion(c.RegionId);
            if (!clubs.TryGetValue(clubId, out var clubName)) return new(null, "Klubben hör inte till kretsen.");
            return _calls.Award(id, clubId, clubName, a, n);
        }, "Klubben är utsedd.");

        // ── Internt ─────────────────────────────────────────────────────────────────────────

        private async Task<IActionResult> Act(int id, Func<CompetitionCall, int, string, CompetitionCallService.CallResult> action, string done)
        {
            var c = _calls.Get(id);
            if (c == null) return Json(new { success = false, message = "Utlysningen hittades inte." });
            if (!await _access.CanManageCallsForRegion(c.RegionId)) return Deny();
            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            try { return Result(action(c, actor, actorName), done); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utlysning {Id}: handlingen kunde inte genomföras.", id);
                return Json(new { success = false, message = "Något gick fel. Ladda om sidan och försök igen." });
            }
        }

        private IActionResult Result(CompetitionCallService.CallResult r, string done)
        {
            if (r.Call == null) return Json(new { success = false, message = r.Error ?? CompetitionCallService.LostRace });
            return Json(new { success = true, message = done, call = Dto(r.Call, _krets.ClubsInRegion(r.Call.RegionId)) });
        }

        private object Dto(CompetitionCall c, Dictionary<int, string> clubs)
        {
            var wishes = c.Status == CompetitionCallStatus.Utlyst ? _calls.ActiveWishCount(c.Id) : 0;
            string? Why(string a) => CompetitionCallRules.WhyNot(a, c.Status, c.CompetitionId, wishes);
            return new
            {
                id = c.Id,
                regionId = c.RegionId,
                year = c.Year,
                name = c.Name,
                description = c.Description,
                discipline = c.Discipline,
                disciplineLabel = DisciplineLabel(c.Discipline),
                date = c.CompetitionDate?.ToString("yyyy-MM-dd"),
                weekday = c.CompetitionDate.HasValue ? CompetitionCallRules.WeekdaySv(c.CompetitionDate.Value) : null,
                locked = c.LockedByKrets,
                wishDeadline = c.WishDeadline?.ToString("yyyy-MM-dd"),
                wishesVisible = c.WishesVisibleToClubs,
                status = c.Status,
                statusLabel = CompetitionCallStatus.Label(c.Status),
                awardedClubId = c.AwardedClubId,
                awardedClub = c.AwardedClubId is int cid ? (clubs.TryGetValue(cid, out var n) ? n : "Okänd klubb") : null,
                competitionId = c.CompetitionId,
                version = c.Version,
                activeWishes = wishes,
                can = new
                {
                    edit = CompetitionCallRules.CanEdit(c.Status),
                    publish = CompetitionCallRules.CanPublish(c.Status),
                    withdraw = CompetitionCallRules.CanWithdraw(c.Status, wishes),
                    award = CompetitionCallRules.CanAward(c.Status),
                    unaward = CompetitionCallRules.CanUnaward(c.Status, c.CompetitionId),
                    cancel = CompetitionCallRules.CanCancel(c.Status, c.CompetitionId),
                },
                why = new { edit = Why("edit"), publish = Why("publish"), withdraw = Why("withdraw"), award = Why("award"), unaward = Why("unaward"), cancel = Why("cancel") },
            };
        }

        public static string DisciplineLabel(string? id) =>
            string.IsNullOrWhiteSpace(id) ? "" : (HpskSite.Models.CompetitionTypes.GetFuzzy(id)?.Name ?? id);

        /// <summary>Grenen om den är en känd gren, annars null (ett fritt värde från klienten sparas aldrig).</summary>
        private static string? ValidDiscipline(string? id) =>
            string.IsNullOrWhiteSpace(id) ? null : HpskSite.Models.CompetitionTypes.All.FirstOrDefault(t => t.Id == id)?.Id;

        private static bool TryDate(string? s, out DateTime? date)
        {
            date = null;
            if (string.IsNullOrWhiteSpace(s)) return true;
            if (!DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
            date = d.Date;
            return true;
        }
    }
}
