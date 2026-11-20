using System.Globalization;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services;
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
    /// Klubbens sida av kretsens utlysning (kretsutlysningen F3): klubben ser vad kretsen utlyst, önskar att få arrangera och ångrar sig.
    ///
    /// <para><b>⚠️ Behörigheten prövas mot KLUBBEN i anropet</b> (<c>IsClubAdminForClub</c>), och <b>utlysningen måste tillhöra klubbens krets</b>
    /// (<c>RegionIdForClub</c>, serverns data) — annars kunde en klubbadmin önska åt en annan krets eller en annan klubb. Kretsens
    /// egna handlingar (utse, registrera åt en klubb) ligger i <see cref="CompetitionCallController"/>.</para>
    ///
    /// <para><b>⚠️ Andra klubbar syns aldrig med kontaktuppgifter.</b> En annan klubbs önskemål är namn, förslag till dag och meddelande — och
    /// bara när kretsen valt att visa dem. Kontaktperson, e-post och telefon lämnar aldrig den här kontrollern.</para>
    /// Orden: <b>utlysa · önska · utse</b>.
    /// </summary>
    [HpskSite.Services.Security.InCodeAuth("AdminAuthorizationService.IsClubAdminForClub(klubben i anropet) + utlysningen tillhör klubbens krets")]
    public class CompetitionCallClubController : SurfaceController
    {
        private readonly CompetitionCallService _calls;
        private readonly CompetitionCallAccess _access;
        private readonly KretsCalendarService _krets;
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Hosting.DatabaseLock _lock;
        private readonly CompetitionCallNotifier _notifier;
        private readonly AppCaches _appCaches;
        private readonly ILogger<CompetitionCallClubController> _logger;

        public CompetitionCallClubController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CompetitionCallService calls, CompetitionCallAccess access, KretsCalendarService krets,
            AdminAuthorizationService auth, HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Hosting.DatabaseLock dbLock, CompetitionCallNotifier notifier, ILogger<CompetitionCallClubController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _calls = calls; _access = access; _krets = krets; _auth = auth; _logger = logger;
            _competitions = competitions; _lock = dbLock; _notifier = notifier; _appCaches = appCaches;
        }

        private static IActionResult Deny() => new JsonResult(new { success = false, message = "Du har inte behörighet att hantera klubbens önskemål." });

        /// <summary>Det kretsen utlyst för klubbens krets ett år, med klubbens läge på varje rad.</summary>
        [HttpGet]
        public async Task<IActionResult> GetForClub(int clubId, int? year)
        {
            if (clubId <= 0 || !await _auth.IsClubAdminForClub(clubId)) return Deny();
            try
            {
                var regionId = _krets.RegionIdForClub(clubId);
                if (regionId <= 0) return Json(new { success = true, calls = Array.Empty<object>(), year = year ?? DateTime.Today.Year + 1, years = Array.Empty<int>() });
                // Åren som har något utlyst. Utan val: närmaste kommande år med utlysningar (annars det senaste), så kortet aldrig
                // gömmer en utlysning bara för att standardåret är tomt.
                var years = _calls.VisibleYearsIn(regionId);
                var y = year ?? (years.FirstOrDefault(v => v >= DateTime.Today.Year) is var up && up > 0 ? up
                                 : (years.Count > 0 ? years[^1] : DateTime.Today.Year));
                var clubs = _krets.ClubsInRegion(regionId);
                var list = _calls.VisibleToClubsIn(regionId, y);
                var mine = _calls.ActiveWishesOf(clubId, list.Select(c => c.Id).ToList());
                return Json(new
                {
                    success = true,
                    year = y,
                    years = years.OrderBy(v => v),
                    regionName = _krets.Region(regionId)?.Name ?? "",
                    calls = list.Select(c => Dto(c, clubId, clubs, mine.GetValueOrDefault(c.Id)))
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Klubb {Club}: utlysningarna kunde inte läsas.", clubId);
                return Json(new { success = false, message = "Utlysningarna kunde inte läsas just nu. Försök igen om en stund." });
            }
        }

        /// <summary>Önska att få arrangera. Kontaktperson = den som trycker (klubbens konto, inget fritt fält).</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Wish(int clubId, int id, string? proposedDate, string? note)
        {
            if (clubId <= 0 || !await _auth.IsClubAdminForClub(clubId)) return Deny();
            var c = _calls.Get(id);
            var regionId = _krets.RegionIdForClub(clubId);
            // Utlysningen måste tillhöra klubbens EGEN krets — ett id från en annan krets får samma svar som ett som inte finns.
            if (c == null || regionId <= 0 || c.RegionId != regionId || !CompetitionCallRules.VisibleToClubs(c.Status))
                return Json(new { success = false, message = "Utlysningen hittades inte." });
            if (!TryDate(proposedDate, out var d)) return Json(new { success = false, message = "Datumet kunde inte läsas. Skriv det som ÅÅÅÅ-MM-DD." });
            var clubs = _krets.ClubsInRegion(regionId);
            var clubName = clubs.GetValueOrDefault(clubId) ?? "";
            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            try
            {
                var r = _calls.Express(id, clubId, d, note, actor, actor, actorName, clubName, onBehalf: false);
                if (r.Wish == null) return Json(new { success = false, message = r.Error ?? CompetitionCallService.LostRace });
                return Json(new { success = true, message = "Ert önskemål är registrerat. Kretsen utser en klubb.", call = Dto(_calls.Get(id)!, clubId, clubs, r.Wish) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Utlysning {Id}, klubb {Club}: önskemålet kunde inte sparas.", id, clubId);
                return Json(new { success = false, message = "Något gick fel. Ladda om sidan och försök igen." });
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Unwish(int clubId, int id)
        {
            if (clubId <= 0 || !await _auth.IsClubAdminForClub(clubId)) return Deny();
            var c = _calls.Get(id);
            var regionId = _krets.RegionIdForClub(clubId);
            if (c == null || regionId <= 0 || c.RegionId != regionId)
                return Json(new { success = false, message = "Utlysningen hittades inte." });
            var clubs = _krets.ClubsInRegion(regionId);
            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            var r = _calls.WithdrawWish(id, clubId, actor, actorName, clubs.GetValueOrDefault(clubId) ?? "");
            if (r.Wish == null) return Json(new { success = false, message = r.Error ?? CompetitionCallService.LostRace });
            return Json(new { success = true, message = "Önskemålet är ångrat.", call = Dto(_calls.Get(id)!, clubId, clubs, null) });
        }

        /// <summary>
        /// <b>Skapa tävlingen ur utlysningen</b> (F4). Bara den utsedda klubbens admin, bara när utlysningen är Tilldelad och ingen
        /// tävling finns. Skapar EN dold rad (<c>IsDraft</c>, värdklubb, serie, datum och namn ur utlysningen) och kopplar den via
        /// <c>CompetitionCall.CompetitionId</c> (tävlingen bär ingen egen koppling — omvänt uppslag).
        /// <para>⚠️ Dubbelklick/två flikar: <c>DatabaseLock call-create:{id}</c> + det villkorade anspråket (<c>CreatingAt</c>). Misslyckas
        /// skapandet släpps anspråket; misslyckas kopplingen EFTER att tävlingen skapats, tas den dolda raden bort igen så att inget
        /// föräldralöst utkast blir kvar.</para>
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateCompetition(int clubId, int id)
        {
            if (clubId <= 0 || !await _auth.IsClubAdminForClub(clubId)) return Deny();
            var c = _calls.Get(id);
            var regionId = _krets.RegionIdForClub(clubId);
            if (c == null || regionId <= 0 || c.RegionId != regionId || c.AwardedClubId != clubId)
                return Json(new { success = false, message = "Utlysningen hittades inte." });
            var why = CompetitionCallRules.WhyNotCreate(c.Status, c.CompetitionId, c.Discipline);
            if (why != null) return Json(new { success = false, message = why });

            await using var lease = await _lock.TryAcquireAsync($"call-create:{id}", TimeSpan.FromSeconds(5));
            if (lease == null) return Json(new { success = false, message = "Tävlingen håller på att skapas. Vänta en stund och ladda om sidan." });
            if (!_calls.ClaimCreation(id, clubId))
                return Json(new { success = false, message = "Tävlingen håller på att skapas, eller är redan skapad. Ladda om sidan." });

            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            int createdId = 0;
            try
            {
                var created = _competitions.Create(c.SeriesId, c.Name, comp =>
                {
                    comp.IsVisible = true;
                    comp.IsDraft = true;   // dold tills klubben trycker Publicera (F1)
                    comp.ClubId = clubId;
                    comp.CompetitionType = c.Discipline;
                    comp.CompetitionName = c.Name;
                    comp.CompetitionDate = c.CompetitionDate;   // kopieras en gång; därefter är tävlingen enda källan (N6)
                    comp.CompetitionLevel = HpskSite.CompetitionTypes.Common.CompetitionLevel.Krets;
                    comp.Description = c.Description;
                });
                if (created.PublishProblem != null || created.Record == null)
                    throw new InvalidOperationException(created.PublishProblem ?? "Tävlingen skapades inte.");
                createdId = created.Record.Id;
                if (!_calls.CompleteCreation(id, clubId, createdId, actor, actorName))
                {
                    // Utnämningen togs tillbaka under tiden: ingen föräldralös dold tävling.
                    _competitions.Trash(createdId);
                    return Json(new { success = false, message = "Kretsen ändrade utlysningen medan tävlingen skapades. Ladda om sidan." });
                }
            }
            catch (Exception ex) when (ex is not HpskSite.Services.Competitions.CompetitionWriteFrozenException)
            {
                _logger.LogError(ex, "Utlysning {Id}, klubb {Club}: tävlingen kunde inte skapas.", id, clubId);
                if (createdId > 0) { try { _competitions.Trash(createdId); } catch (Exception t) { _logger.LogError(t, "Utlysning {Id}: den dolda tävlingen {Comp} kunde inte tas bort.", id, createdId); } }
                _calls.ReleaseCreation(id);
                return Json(new { success = false, message = "Tävlingen kunde inte skapas. Försök igen om en stund." });
            }
            InvalidateCompetitionLists();
            return Json(new
            {
                success = true,
                message = "Tävlingen är skapad och dold. Bygg klart den under klubbens tävlingar och tryck Publicera när den är klar.",
                call = Dto(_calls.Get(id)!, clubId, _krets.ClubsInRegion(regionId), null)
            });
        }

        /// <summary>
        /// Klubben föreslår ett annat datum (beslut 6). Ändrar ingenting — en händelserad och en notis till kretsen. Bara den utsedda klubben,
        /// och bara när kretsen har fastställt datumet (annars ändrar klubben datumet själv i tävlingen).
        /// </summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> ProposeDate(int clubId, int id, string? proposedDate, string? note)
        {
            if (clubId <= 0 || !await _auth.IsClubAdminForClub(clubId)) return Deny();
            var c = _calls.Get(id);
            var regionId = _krets.RegionIdForClub(clubId);
            if (c == null || regionId <= 0 || c.RegionId != regionId || c.AwardedClubId != clubId)
                return Json(new { success = false, message = "Utlysningen hittades inte." });
            if (c.Status is not (CompetitionCallStatus.Tilldelad or CompetitionCallStatus.Skapad))
                return Json(new { success = false, message = "Bara en utsedd klubb kan föreslå ett annat datum." });
            if (!TryDate(proposedDate, out var d) || d == null) return Json(new { success = false, message = "Skriv datumet som ÅÅÅÅ-MM-DD." });
            if (d.Value.Year != c.Year) return Json(new { success = false, message = $"Förslaget ligger inte i {c.Year}." });
            if (note != null && note.Trim().Length > CompetitionCallRules.MaxWishNote)
                return Json(new { success = false, message = $"Meddelandet får vara högst {CompetitionCallRules.MaxWishNote} tecken." });
            var clubName = _krets.ClubsInRegion(regionId).GetValueOrDefault(clubId) ?? "";
            var actor = await _access.CurrentMemberIdAsync();
            var actorName = await _access.CurrentNameAsync();
            _calls.RecordDateProposal(id, d.Value, note, actor, actorName, clubName);
            var mailed = await _notifier.DateProposedAsync(c, clubName, d.Value, note, Request.PublicBase());
            return Json(new { success = true, message = mailed > 0 ? "Ert förslag är skickat till kretsen." : "Ert förslag är sparat hos kretsen." });
        }

        private void InvalidateCompetitionLists()
        {
            try
            {
                _appCaches.RuntimeCache.ClearByRegex("^admin_competitions_list_");
                HpskSite.Services.CompetitionMenuService.Invalidate(_appCaches);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Tävlingslistornas cache kunde inte tömmas efter skapandet."); }
        }

        // ── Internt ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Klubbens bild av en utlysning. ⚠️ Egen DTO med få fält — aldrig kretsens (<see cref="CompetitionCallController"/>) som bär kontaktpersoner.
        /// Utsedd klubb syns som rubrik för alla klubbar i kretsen.
        /// </summary>
        private object Dto(CompetitionCall c, int clubId, Dictionary<int, string> clubs, CompetitionCallWish? mine)
        {
            var state = CompetitionCallRules.ClubState(c.Status, c.AwardedClubId, clubId, mine != null);
            // Förklaringen när "Vi vill arrangera" är avstängd (t.ex. sista dagen har passerat); annars null.
            var open = state == "open" ? CompetitionCallRules.WhyNotWish(c.Status, c.WishDeadline, DateTime.Today, false) : null;
            var others = c.Status == CompetitionCallStatus.Utlyst && c.WishesVisibleToClubs
                ? _calls.Wishes(c.Id).Where(w => w.Status == "Aktiv" && w.ClubId != clubId).Select(w => new
                {
                    club = clubs.TryGetValue(w.ClubId, out var n) ? n : "Okänd klubb",
                    proposedDate = w.ProposedDate?.ToString("yyyy-MM-dd"),
                    note = w.Note
                }).Cast<object>().ToList()
                : new List<object>();
            return new
            {
                id = c.Id,
                name = c.Name,
                description = c.Description,
                disciplineLabel = CompetitionCallController.DisciplineLabel(c.Discipline),
                date = c.CompetitionDate?.ToString("yyyy-MM-dd"),
                weekday = c.CompetitionDate.HasValue ? CompetitionCallRules.WeekdaySv(c.CompetitionDate.Value) : null,
                locked = c.LockedByKrets,
                wishDeadline = c.WishDeadline?.ToString("yyyy-MM-dd"),
                status = c.Status,
                statusLabel = CompetitionCallStatus.Label(c.Status),
                state,
                awardedClub = c.AwardedClubId is int a ? (clubs.TryGetValue(a, out var an) ? an : "Okänd klubb") : null,
                canWish = state == "open" && open == null,
                whyNotWish = open,
                canUnwish = state == "wished",
                // F4: den utsedda klubben skapar tävlingen; efter det föreslår den bara datum när kretsen fastställt det.
                canCreate = state == "awarded-me" && CompetitionCallRules.WhyNotCreate(c.Status, c.CompetitionId, c.Discipline) == null,
                whyNotCreate = state == "awarded-me" && c.Status == CompetitionCallStatus.Tilldelad ? CompetitionCallRules.WhyNotCreate(c.Status, c.CompetitionId, c.Discipline) : null,
                created = state == "awarded-me" && c.Status == CompetitionCallStatus.Skapad,
                canProposeDate = state == "awarded-me" && c.LockedByKrets,
                mine = mine == null ? null : new { proposedDate = mine.ProposedDate?.ToString("yyyy-MM-dd"), note = mine.Note, byKrets = mine.OnBehalf },
                others
            };
        }

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
