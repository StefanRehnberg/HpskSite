using System.Globalization;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Models.Competitions;
using HpskSite.Services;
using HpskSite.Services.CompetitionCalls;
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
    /// Klubbprogrammet (F7): medlemmar <b>önskar</b> bli tävlingsledare för klubbens tävlingar, klubbens admin <b>utser</b>. Orden: önska · utse · tävlingsledare.
    ///
    /// <para><b>⚠️ Två sidor, två behörigheter.</b> Medlemmens ändpunkter (<c>Mine</c>, <c>Wish</c>, <c>Unwish</c>, <c>Decline</c>) kräver att medlemmen är AKTIV i
    /// tävlingens klubb och rör bara medlemmens EGEN rad. Adminens (<c>ForClub</c>, <c>SearchMembers</c>, <c>Appoint</c>, <c>RemoveManager</c>, <c>Close</c>,
    /// <c>Reopen</c>) kräver <see cref="CompetitionCallAccess.CanManageClubProgram"/> för klubben i anropet, och tävlingen måste tillhöra den klubben
    /// (IDOR). Klubbens id kommer aldrig från tävlingen blint — varje id prövas mot servern.</para>
    ///
    /// <para><b>⚠️ Medlemmens DTO kan strukturellt inte bära andra:</b> bara medlemmens eget läge. Ingen lista över dem som önskat, inget antal,
    /// inga namn på tävlingsledare (Stefan 2026-11-20: namnen hålls på adminsidorna tills tävlingen är officiell).</para>
    /// </summary>
    [HpskSite.Services.Security.InCodeAuth("Medlem aktiv i tävlingens klubb (egen rad) / CompetitionCallAccess.CanManageClubProgram(klubben i anropet) + tävlingen tillhör klubben")]
    public class CompetitionManagerController : SurfaceController
    {
        private readonly CompetitionManagerService _svc;
        private readonly CompetitionManagerNotifier _notifier;
        private readonly CompetitionCallAccess _access;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly ClubRosterService _roster;
        private readonly MemberDirectoryService _directory;
        private readonly ClubService _clubs;
        private readonly HpskSite.Services.Competitions.CompetitionLinks _links;
        private readonly ILogger<CompetitionManagerController> _logger;

        public CompetitionManagerController(
            IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            CompetitionManagerService svc, CompetitionManagerNotifier notifier, CompetitionCallAccess access,
            HpskSite.Services.Competitions.ICompetitionStore competitions, ClubRosterService roster, MemberDirectoryService directory,
            ClubService clubs, HpskSite.Services.Competitions.CompetitionLinks links, ILogger<CompetitionManagerController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _svc = svc; _notifier = notifier; _access = access; _competitions = competitions; _roster = roster; _directory = directory; _clubs = clubs; _links = links; _logger = logger;
        }

        /// <summary>
        /// Tävlingsledare som bara finns i tävlingens egen lista (<c>competitionManagers</c>, satt med den gamla väljaren) och ännu inte i någon roster
        /// (ingen planering) räknas också — annars visade en tävling med tävlingsledare "Saknas".
        /// </summary>
        private static Dictionary<int, List<int>> MergeLegacy(Dictionary<int, List<int>> fromRoster, IEnumerable<CompetitionInfo> comps)
        {
            foreach (var c in comps)
                if (fromRoster.TryGetValue(c.Id, out var l))
                    foreach (var m in c.ManagerIds) if (m > 0 && !l.Contains(m)) l.Add(m);
            return fromRoster;
        }

        private static IActionResult No(string msg) => new JsonResult(new { success = false, message = msg });
        private static IActionResult Gone() => No("Tävlingen hittades inte.");
        private string ClubName(int clubId) => _clubs.GetClubById(clubId)?.Name ?? "klubben";

        // ═══════════════ Medlemmens sida ═══════════════

        /// <summary>Raderna i "Vi söker tävlingsledare": klubbarnas kommande tävlingar där medlemmen är med. Bara medlemmens eget läge.</summary>
        [HttpGet]
        public async Task<IActionResult> Mine()
        {
            var me = await _access.CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = true, rows = Array.Empty<object>() });
            try
            {
                var myClubs = _svc.ClubsOfMember(me);
                if (myClubs.Count == 0) return Json(new { success = true, rows = Array.Empty<object>() });
                var today = DateTime.Today;
                var comps = _competitions.List(includeDrafts: true)   // draft-list-ok: bara klubbens egna medlemmar, bara namn och datum (klubbprogrammet planeras före publicering)
                    .Where(c => c.ClubId > 0 && myClubs.Contains(c.ClubId) && (c.CompetitionDate == null || c.CompetitionDate.Value.Date >= today.AddDays(-1)))
                    .ToList();
                // Rader för stängda roller visas bara för den som önskat (filled-not-you) eller är utsedd — kollas nedan; hämta därför även passerade? Nej: bara kommande.
                var ids = comps.Select(c => c.Id).ToList();
                var closed = _svc.ClosedAmong(ids);
                var managers = MergeLegacy(_svc.ManagersByCompetition(ids), comps);
                var allWishRows = _svc.WishesFor(ids);
                var mine = _svc.WishesOfMember(me, ids).ToDictionary(w => w.CompetitionId);
                var rows = new List<object>();
                foreach (var c in comps.OrderBy(c => c.CompetitionDate ?? DateTime.MaxValue))
                {
                    var isClosed = closed.Contains(c.Id);
                    var iAm = managers[c.Id].Contains(me);
                    var seeking = CompetitionManagerRules.IsSeeking(isClosed, c.CompetitionDate, today, managers[c.Id].Count > 0, allWishRows.Any(w => w.CompetitionId == c.Id));
                    mine.TryGetValue(c.Id, out var w);
                    var state = CompetitionManagerRules.MemberState(seeking, isClosed, w?.Status, iAm);
                    if (state == null) continue;
                    rows.Add(new
                    {
                        competitionId = c.Id,
                        name = c.DisplayName,
                        date = c.CompetitionDate?.ToString("yyyy-MM-dd"),
                        weekday = c.CompetitionDate.HasValue ? CompetitionCallRules.WeekdaySv(c.CompetitionDate.Value) : null,
                        club = ClubName(c.ClubId),
                        state,
                    });
                }
                return Json(new { success = true, rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlem {Member}: raderna för tävlingsledare kunde inte läsas.", me);
                return Json(new { success = false, message = "Det gick inte att läsa just nu. Försök igen om en stund." });
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Wish(int competitionId)
        {
            var me = await _access.CurrentMemberIdAsync();
            var c = me > 0 ? _competitions.Get(competitionId) : null;
            // Samma svar för "finns inte" och "inte med i klubben": ett id får inte avslöja en annan klubbs tävling.
            if (c == null || c.ClubId <= 0 || !_roster.IsActiveInClub(me, c.ClubId)) return Gone();
            var ids = new[] { c.Id };
            var managers = MergeLegacy(_svc.ManagersByCompetition(ids), new[] { c })[c.Id];
            var seeking = CompetitionManagerRules.IsSeeking(_svc.ClosedAmong(ids).Contains(c.Id), c.CompetitionDate, DateTime.Today, managers.Count > 0, _svc.WishesFor(ids).Count > 0);
            var why = CompetitionManagerRules.WhyNotWish(seeking, _svc.ClosedAmong(ids).Contains(c.Id), true, managers.Contains(me));
            if (why != null) return No(why);
            try
            {
                var created = _svc.Wish(c.Id, me);
                if (created) await _notifier.WishedAsync(c, ClubName(c.ClubId), await _access.CurrentNameAsync(), Request.PublicBase());
                return Json(new { success = true, message = "Ditt önskemål är registrerat. Klubben utser tävlingsledare." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tävling {Comp}, medlem {Member}: önskemålet kunde inte sparas.", c.Id, me);
                return No("Något gick fel. Ladda om sidan och försök igen.");
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Unwish(int competitionId)
        {
            var me = await _access.CurrentMemberIdAsync();
            if (me <= 0) return Gone();
            _svc.Unwish(competitionId, me);   // rör bara medlemmens EGEN aktiva rad
            return Json(new { success = true, message = "Önskemålet är ångrat." });
        }

        /// <summary>Tacka nej som inloggad medlem (knappen på raden). Rör bara medlemmens egen tävlingsledarrad.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Decline(int competitionId)
        {
            var me = await _access.CurrentMemberIdAsync();
            var c = me > 0 ? _competitions.Get(competitionId) : null;
            if (c == null) return Gone();
            var result = await DeclineCore(c, me, await _access.CurrentNameAsync(), Request.PublicBase());
            return result ? Json(new { success = true, message = "Du är inte längre tävlingsledare. Klubbens admin får veta." })
                          : No("Du är inte tävlingsledare för den här tävlingen.");
        }

        /// <summary>Delad av knappen och mejllänken (<see cref="TavlingsledareController"/>). Falskt = ingen rad att ta bort (redan avklarat).</summary>
        internal static async Task<bool> DeclineShared(CompetitionManagerService svc, CompetitionManagerNotifier notifier, CompetitionInfo c,
            string clubName, int memberId, string memberName, string publicBase)
        {
            if (!svc.RemoveManager(c.Id, memberId)) return false;
            await notifier.DeclinedAsync(c, clubName, memberName, svc.ManagerCount(c.Id) == 0, publicBase);
            return true;
        }

        private Task<bool> DeclineCore(CompetitionInfo c, int me, string name, string publicBase) =>
            DeclineShared(_svc, _notifier, c, ClubName(c.ClubId), me, name, publicBase);

        // ═══════════════ Klubbadminens sida ═══════════════

        /// <summary>Klubbens tävlingar ett år med tävlingsledare, önskemål och läge. Bara klubbens admin.</summary>
        [HttpGet]
        public async Task<IActionResult> ForClub(int clubId, int? year)
        {
            if (!await _access.CanManageClubProgram(clubId)) return No("Du har inte behörighet att hantera klubbens tävlingsledare.");
            try
            {
                var all = _competitions.List(includeDrafts: true).Where(c => c.ClubId == clubId).ToList();   // draft-list-ok: klubbens egen admin, behörigheten prövad för klubben ovan
                var years = all.Where(c => c.CompetitionDate.HasValue).Select(c => c.CompetitionDate!.Value.Year)
                               .Append(DateTime.Today.Year).Append(DateTime.Today.Year + 1).Distinct().OrderBy(y => y).ToList();
                var y = year is int yy && years.Contains(yy) ? yy
                        : (all.Any(c => c.CompetitionDate?.Year == DateTime.Today.Year + 1) ? DateTime.Today.Year + 1 : DateTime.Today.Year);
                var inYear = all.Where(c => c.CompetitionDate?.Year == y).OrderBy(c => c.CompetitionDate).ToList();
                var ids = inYear.Select(c => c.Id).ToList();
                var closed = _svc.ClosedAmong(ids);
                var managers = MergeLegacy(_svc.ManagersByCompetition(ids), inYear);
                var wishes = _svc.WishesFor(ids);
                var names = _roster.Active(clubId).GroupBy(r => r.MemberId).ToDictionary(g => g.Key, g => g.First().Name);
                string N(int m) => names.TryGetValue(m, out var n) ? n : "Tidigare medlem";
                var today = DateTime.Today;
                var rows = inYear.Select(c =>
                {
                    var wl = wishes.Where(w => w.CompetitionId == c.Id).ToList();
                    var isClosed = closed.Contains(c.Id);
                    return new
                    {
                        competitionId = c.Id,
                        name = c.DisplayName,
                        date = c.CompetitionDate?.ToString("yyyy-MM-dd"),
                        weekday = c.CompetitionDate.HasValue ? CompetitionCallRules.WeekdaySv(c.CompetitionDate.Value) : null,
                        isDraft = c.IsDraft,
                        url = _links.Url(c.Id),
                        state = CompetitionManagerRules.AdminState(managers[c.Id].Count, isClosed, wl.Count(w => w.Status == CompetitionManagerWishStatus.Aktiv)),
                        seeking = CompetitionManagerRules.IsSeeking(isClosed, c.CompetitionDate, today, managers[c.Id].Count > 0, wl.Count > 0),
                        closed = isClosed,
                        managers = managers[c.Id].Select(m => new { memberId = m, name = N(m) }),
                        wishes = wl.Where(w => w.Status == CompetitionManagerWishStatus.Aktiv)
                                   .Select(w => new { memberId = w.MemberId, name = N(w.MemberId), at = w.CreatedAt.ToString("yyyy-MM-dd") }),
                    };
                }).ToList();
                return Json(new { success = true, year = y, years, rows });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Klubb {Club}: tävlingsledarlistan kunde inte läsas.", clubId);
                return No("Listan kunde inte läsas just nu. Försök igen om en stund.");
            }
        }

        /// <summary>Sök bland klubbens aktiva medlemmar (för "Utse någon annan"). Bara namn och id lämnar kontrollern.</summary>
        [HttpGet]
        public async Task<IActionResult> SearchMembers(int clubId, string? q)
        {
            if (!await _access.CanManageClubProgram(clubId)) return No("Du har inte behörighet.");
            var hits = _directory.Search(q, 15, clubId);
            return Json(new { success = true, members = hits.Select(h => new { memberId = h.MemberId, name = h.Name }) });
        }

        private async Task<(CompetitionInfo? Comp, IActionResult? Error)> AdminCompetition(int clubId, int competitionId)
        {
            if (!await _access.CanManageClubProgram(clubId)) return (null, No("Du har inte behörighet att hantera klubbens tävlingsledare."));
            var c = _competitions.Get(competitionId);
            // En tävling i en annan klubb får samma svar som en som inte finns.
            if (c == null || c.ClubId != clubId) return (null, Gone());
            return (c, null);
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Appoint(int clubId, int competitionId, int[]? memberIds)
        {
            var (c, err) = await AdminCompetition(clubId, competitionId);
            if (c == null) return err!;
            var ids = (memberIds ?? Array.Empty<int>()).Where(i => i > 0).Distinct().ToList();
            // Bara aktiva medlemmar i klubben (servern avgör, inte klienten).
            var foreign = ids.Where(m => !_roster.IsActiveInClub(m, clubId)).ToList();
            if (foreign.Count > 0) return No("Bara aktiva medlemmar i klubben kan utses.");
            var by = await _access.CurrentMemberIdAsync();
            try
            {
                var r = _svc.Appoint(c.Id, ids, by);
                if (r.Error != null) return No(r.Error);
                var unreached = 0;
                foreach (var m in r.Appointed)
                    if (await _notifier.AppointedAsync(c, ClubName(clubId), m, Request.PublicBase()) == 0) unreached++;
                return Json(new
                {
                    success = true,
                    appointed = r.Appointed.Count,
                    message = r.Appointed.Count == 0 ? "De var redan tävlingsledare." :
                        (unreached == 0 ? "Utsedda. De har fått ett mejl." : $"Utsedda, men {unreached} nåddes inte av mejlet (saknar e-postadress eller fel vid utskick). Hör av dig till dem.")
                        + (r.Warning != null ? " " + r.Warning : ""),
                    warning = r.Warning,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Tävling {Comp}: utseendet kunde inte sparas.", c.Id);
                return No("Något gick fel. Ladda om sidan och försök igen.");
            }
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveManager(int clubId, int competitionId, int memberId)
        {
            var (c, err) = await AdminCompetition(clubId, competitionId);
            if (c == null) return err!;
            _svc.RemoveManager(c.Id, memberId);
            return Json(new { success = true, message = "Tävlingsledaren är borttagen." });
        }

        /// <summary>"Klart, tävlingsledare tillsatt": stänger rollen, och de som önskat utan att utses får ett besked (utan namn).</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Close(int clubId, int competitionId)
        {
            var (c, err) = await AdminCompetition(clubId, competitionId);
            if (c == null) return err!;
            var by = await _access.CurrentMemberIdAsync();
            var notice = _svc.Close(c.Id, by);
            if (notice == null) return Json(new { success = true, message = "Rollen var redan stängd." });
            var sent = await _notifier.FilledAsync(c, ClubName(clubId), notice, Request.PublicBase());
            return Json(new { success = true, message = notice.Count == 0 ? "Klart. Rutan försvinner för medlemmarna." : $"Klart. {sent} av {notice.Count} som önskat har fått beskedet." });
        }

        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> Reopen(int clubId, int competitionId)
        {
            var (c, err) = await AdminCompetition(clubId, competitionId);
            if (c == null) return err!;
            _svc.Reopen(c.Id);
            return Json(new { success = true, message = "Rollen är öppen igen." });
        }
    }
}
