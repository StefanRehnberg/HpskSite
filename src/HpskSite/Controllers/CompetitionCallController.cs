using System.Globalization;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.CompetitionCalls;
using HpskSite.Services.Hosting;
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
        private readonly CompetitionCallNotifier _notifier;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.ICompetitionSeriesStore _series;
        private readonly HpskSite.Services.Registrations.ICompetitionRegistrationStore _registrations;
        private readonly ILogger<CompetitionCallController> _logger;

        public CompetitionCallController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CompetitionCallService calls, CompetitionCallAccess access, KretsCalendarService krets,
            CompetitionCallNotifier notifier, HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Registrations.ICompetitionRegistrationStore registrations, ILogger<CompetitionCallController> logger,
            HpskSite.Services.Competitions.ICompetitionSeriesStore series)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _series = series;
            _competitions = competitions;
            _registrations = registrations;
            _calls = calls;
            _access = access;
            _krets = krets;
            _notifier = notifier;
            _logger = logger;
        }

        private static IActionResult Deny() => new JsonResult(new { success = false, message = "Du har inte behörighet att hantera kretsens utlysningar." });

        /// <summary>
        /// Serierna kretsen får lägga en utlysning i: de som har kretsen som värd (<c>regionalFederation</c>) eller hör till en klubb i kretsen.
        /// ⚠️ En utlysning kan bara kopplas till en serie som tillhör kretsen — aldrig en grannkrets serie, hur id:t än kom hit.
        /// </summary>
        private List<HpskSite.Models.Competitions.CompetitionSeriesInfo> SeriesOfRegion(int regionId)
        {
            var code = _krets.Region(regionId)?.Code;
            if (string.IsNullOrEmpty(code)) return new();
            var clubs = _krets.ClubsInRegion(regionId);
            return _series.List()
                .Where(s => string.Equals(s.RegionalFederation, code, StringComparison.OrdinalIgnoreCase) || (s.ClubId > 0 && clubs.ContainsKey(s.ClubId)))
                .OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

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
                    series = SeriesOfRegion(regionId).Select(s => new { id = s.Id, name = s.Name }),
                    calls = list.Select(c => Dto(c, clubs)),
                    // Gult kort "Att behandla": utsedda klubbar som inte kunde aviseras (ingen administratör, eller mejlet gick inte).
                    // Samt (F6): utsedd klubb som inte skapat tävlingen efter 14 dagar, och önskemål som väntar på besked efter sista dagen.
                    attention = AttentionRows(regionId, clubs)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kretsens utlysningar kunde inte läsas (krets {Id}).", regionId);
                return Json(new { success = false, message = "Utlysningarna kunde inte läsas just nu. Försök igen om en stund." });
            }
        }

        /// <summary>
        /// Raderna i det gula kortet. <c>reason</c>: <c>nomail</c> (klubben nåddes inte av utseendet), <c>create</c> (utsedd, ingen tävling efter
        /// 14 dagar — samma regel som påminnelsen), <c>award</c> (sista dag för önskemål har gått, ingen utsedd). En utlysning står en gång, med
        /// det mest brådskande skälet.
        /// </summary>
        private List<object> AttentionRows(int regionId, Dictionary<int, string> clubs)
        {
            var today = DateTime.Today;
            string Club(int? id) => id is int cid && clubs.TryGetValue(cid, out var cn) ? cn : "Okänd klubb";
            var rows = new List<object>();
            var seen = new HashSet<int>();
            foreach (var c in _calls.AwardedWithoutNotice(regionId))
                if (seen.Add(c.Id)) rows.Add(new { id = c.Id, name = c.Name, club = Club(c.AwardedClubId), date = c.CompetitionDate?.ToString("yyyy-MM-dd"), reason = "nomail" });
            foreach (var c in _calls.OpenForReminders(regionId))
            {
                if (CompetitionCallRules.NeedsCreate(c, today) && seen.Add(c.Id))
                    rows.Add(new { id = c.Id, name = c.Name, club = Club(c.AwardedClubId), date = c.CompetitionDate?.ToString("yyyy-MM-dd"), reason = "create" });
                else if (c.Status == CompetitionCallStatus.Utlyst && CompetitionCallRules.NeedsAward(c, _calls.ActiveWishCount(c.Id), today) && seen.Add(c.Id))
                    rows.Add(new { id = c.Id, name = c.Name, club = "", date = c.CompetitionDate?.ToString("yyyy-MM-dd"), reason = "award" });
            }
            return rows;
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
            string? date, bool locked, string? wishDeadline, bool wishesVisible, int expectedVersion, bool publish, int? seriesId = null)
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

            // Serien: utelämnad = behåll det som finns, 0 = ingen serie, annars måste serien tillhöra utlysningens EGEN krets.
            int? series = existing?.SeriesId;
            if (seriesId.HasValue)
            {
                if (seriesId.Value <= 0) series = null;
                else if (SeriesOfRegion(region).Any(s => s.Id == seriesId.Value)) series = seriesId.Value;
                else return Json(new { success = false, message = "Serien hittades inte i kretsen." });
            }

            var edit = new CompetitionCall
            {
                RegionId = region, Year = year, Name = name ?? "", Description = description, Discipline = ValidDiscipline(discipline),
                CompetitionDate = d, LockedByKrets = locked && d.HasValue, WishDeadline = wd, WishesVisibleToClubs = wishesVisible,
                SeriesId = series, ChampionshipScope = existing?.ChampionshipScope,
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
        public async Task<IActionResult> AwardCall(int id, int clubId)
        {
            CompetitionCallService.CallResult? awarded = null;
            string clubNameOut = "";
            int actorOut = 0; string actorNameOut = "";
            var result = await Act(id, (c, a, n) =>
            {
                if (clubId <= 0) return new(null, "Välj en klubb.");
                var clubs = _krets.ClubsInRegion(c.RegionId);
                if (!clubs.TryGetValue(clubId, out var clubName)) return new(null, "Klubben hör inte till kretsen.");
                var r = _calls.Award(id, clubId, clubName, a, n);
                if (r.Call != null) { awarded = r; clubNameOut = clubName; actorOut = a; actorNameOut = n; }
                return r;
            }, "Klubben är utsedd.");
            // Utseendet är registrerat. Mejlet skickas EFTER det, och ett mejlfel ändrar inte utseendet.
            if (awarded?.Call != null)
                await _notifier.AwardedAsync(awarded.Value.Call!, clubNameOut, Request.PublicBase(), actorOut, actorNameOut);
            return result;
        }

        /// <summary>
        /// Kretsen skriver in ett önskemål åt en klubb som svarat per mejl (märkt "Registrerat av kretsen"). Bara medan utlysningen är utlyst;
        /// sista dagen för önskemål gäller inte här. Klubben måste höra till utlysningens krets.
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> WishForClub(int id, int clubId, string? proposedDate, string? note)
        {
            var c = _calls.Get(id);
            if (c == null) return Json(new { success = false, message = "Utlysningen hittades inte." });
            if (!await _access.CanManageCallsForRegion(c.RegionId)) return Deny();
            var clubs = _krets.ClubsInRegion(c.RegionId);
            if (clubId <= 0 || !clubs.TryGetValue(clubId, out var clubName)) return Json(new { success = false, message = "Klubben hör inte till kretsen." });
            if (!TryDate(proposedDate, out var d)) return Json(new { success = false, message = "Datumet kunde inte läsas. Skriv det som ÅÅÅÅ-MM-DD." });
            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            var r = _calls.Express(id, clubId, d, note, 0, actor, actorName, clubName, onBehalf: true);
            if (r.Wish == null) return Json(new { success = false, message = r.Error ?? CompetitionCallService.LostRace });
            return Json(new { success = true, message = $"Önskemålet från {clubName} är registrerat.", call = Dto(_calls.Get(id)!, clubs) });
        }

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
            var wishList = _calls.Wishes(c.Id).Where(w => w.Status == "Aktiv").ToList();
            var wishes = c.Status == CompetitionCallStatus.Utlyst ? wishList.Count : 0;
            // Kretsen ser kontaktpersonen (klubbarna gör det aldrig). Namnet slås upp här; en gallrad medlem blir "Borttagen medlem".
            string? ContactName(int? memberId) =>
                memberId is int mid && mid > 0 ? (Services.MemberService.GetById(mid)?.Name ?? "Borttagen medlem") : null;
            string? Why(string a) => CompetitionCallRules.WhyNot(a, c.Status, c.CompetitionId, wishes);
            return new
            {
                id = c.Id,
                regionId = c.RegionId,
                year = c.Year,
                name = c.Name,
                description = c.Description,
                discipline = c.Discipline,
                seriesId = c.SeriesId,
                seriesName = c.SeriesId is int sid ? _series.Get(sid)?.Name : null,
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
                unawardWarning = UnawardWarningFor(c),
                activeWishes = wishes,
                wishes = wishList.Select(w => new
                {
                    clubId = w.ClubId,
                    club = clubs.TryGetValue(w.ClubId, out var wn) ? wn : "Okänd klubb",
                    proposedDate = w.ProposedDate?.ToString("yyyy-MM-dd"),
                    note = w.Note,
                    onBehalf = w.OnBehalf,
                    contact = ContactName(w.ContactMemberId),
                    at = w.CreatedAt.ToString("yyyy-MM-dd")
                }),
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

        /// <summary>Varningen före ett byte av klubb när tävlingen redan är skapad (annars null). Ett läsfel hindrar aldrig kretsen — varningen blir då generell.</summary>
        private string? UnawardWarningFor(CompetitionCall c)
        {
            if (!(c.CompetitionId > 0)) return null;
            var published = false; var regs = 0;
            try
            {
                var comp = _competitions.Get(c.CompetitionId!.Value);
                published = comp != null && !comp.IsDraft;
                regs = _registrations.CountByCompetition(new[] { c.CompetitionId.Value }).GetValueOrDefault(c.CompetitionId.Value);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Utlysning {Id}: tävlingens läge kunde inte läsas för varningen.", c.Id); }
            return CompetitionCallRules.UnawardWarning(c.CompetitionId, published, regs);
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
