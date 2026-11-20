using HpskSite.Services;
using HpskSite.Services.CompetitionCalls;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// "Jag kan inte, tacka nej" ur mejlet till en utsedd tävlingsledare (klubbprogrammet F7, Stefans beslut 2026-11-19). UTAN inloggning.
    ///
    /// <para><b>⚠️ GET ändrar aldrig något</b> — bara en bekräftelsesida (mejlskannrar och förhandsvisningar hämtar länkar). Själva avböjandet är en POST med
    /// antiförfalskningsnyckel. Länken är signerad och tidsbegränsad (<see cref="CompetitionManagerLinkService"/>), gäller bara den utsedde personens EGEN
    /// tävlingsledarrad och kan bara tacka nej — den som redan tackat nej (raden finns inte längre) får "redan avklarat". Klubbens admin får ett mejl.</para>
    /// </summary>
    [Route("tavlingsledare")]
    [HpskSite.Services.Security.InCodeAuth("Signerad, tidsbegränsad mejllänk (CompetitionManagerLinkService) — bara den utsedde personens egen rad, bara tacka nej")]
    public class TavlingsledareController : Controller
    {
        private readonly IUmbracoContextAccessor _umbraco;
        private readonly CompetitionManagerLinkService _links;
        private readonly CompetitionManagerService _svc;
        private readonly CompetitionManagerNotifier _notifier;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly ClubService _clubs;
        private readonly IMemberService _members;

        public TavlingsledareController(IUmbracoContextAccessor umbraco, CompetitionManagerLinkService links, CompetitionManagerService svc,
            CompetitionManagerNotifier notifier, HpskSite.Services.Competitions.ICompetitionStore competitions, ClubService clubs, IMemberService members)
        {
            _umbraco = umbraco; _links = links; _svc = svc; _notifier = notifier; _competitions = competitions; _clubs = clubs; _members = members;
        }

        [HttpGet("tacka-nej")]
        public IActionResult TackaNej(string? t) => Render(t, done: false);

        [HttpPost("tacka-nej"), ValidateAntiForgeryToken]
        public async Task<IActionResult> TackaNejPost(string? t)
        {
            var target = _links.Parse(t);
            var comp = target == null ? null : _competitions.Get(target.Value.CompetitionId);
            if (target == null || comp == null) return Render(t, done: false);
            var m = _members.GetById(target.Value.MemberId);
            var name = $"{m?.GetValue<string>("firstName")} {m?.GetValue<string>("lastName")}".Trim();
            var ok = await CompetitionManagerController.DeclineShared(_svc, _notifier, comp, _clubs.GetClubById(comp.ClubId)?.Name ?? "klubben",
                target.Value.MemberId, name.Length > 0 ? name : (m?.Name ?? "En medlem"), Request.PublicBase());
            ViewData["MgrResult"] = ok ? "done" : "already";
            return Render(t, done: true);
        }

        private IActionResult Render(string? t, bool done)
        {
            if (!_umbraco.TryGetUmbracoContext(out var ctx) || ctx.Content == null) return StatusCode(500, "Umbraco-kontext saknas.");
            var root = ctx.Content.GetAtRoot().FirstOrDefault();
            if (root == null) return StatusCode(500, "Ingen rotnod hittades.");
            var target = _links.Parse(t);
            var comp = target == null ? null : _competitions.Get(target.Value.CompetitionId);
            if (comp != null)
            {
                ViewData["MgrToken"] = t;
                ViewData["MgrCompetition"] = comp.DisplayName;
                ViewData["MgrDate"] = comp.CompetitionDate?.ToString("d MMMM yyyy");
                ViewData["MgrClub"] = _clubs.GetClubById(comp.ClubId)?.Name ?? "";
                if (!done) ViewData["MgrResult"] = _svc.ManagersByCompetition(new[] { comp.Id })[comp.Id].Contains(target!.Value.MemberId) ? "ask" : "already";
            }
            Response.Headers["Referrer-Policy"] = "no-referrer";   // token i adressen läcker inte vidare
            return View("TavlingsledareTackaNej", root);
        }
    }
}
