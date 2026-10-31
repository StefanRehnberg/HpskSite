using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Patrullgeneratorn för fält (den avancerade vägen): en egen sida under tävlingen, <c>/patrullgenerator/{id}</c>. Sidan är en arbetsyta där tävlingsledaren ändrar
    /// inställningar, ser utfallet (förhandsvisning, skriver ingenting), jämför två förslag (A/B) och först därefter tillämpar. Data och skrivning går genom
    /// <c>StaffingController</c>s GetPatrolPlan / ApplyPatrolPlan / UndoPatrolPlan (en väg till varje uppgift; rätten prövas där, inloggad först, sedan tävlingens personal).
    /// Den enkla vägen ("Skapa patruller") finns kvar på startlistfliken — generatorn är ett avancerat komplement (Stefan 2026-10-30).
    /// </summary>
    [Route("patrullgenerator/{competitionId:int}")]
    public class PatrolGeneratorController : Controller
    {
        private readonly AdminAuthorizationService _auth;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly IMemberManager _memberManager;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public PatrolGeneratorController(AdminAuthorizationService auth, HpskSite.Services.Competitions.ICompetitionStore competitions, IMemberManager memberManager, IUmbracoContextAccessor umbracoContextAccessor)
        {
            _auth = auth; _competitions = competitions; _memberManager = memberManager; _umbracoContextAccessor = umbracoContextAccessor;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int competitionId)
        {
            // Inloggad först (authn före authz), sedan tävlingens personal.
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null) return Redirect($"/login-register?returnUrl={Uri.EscapeDataString($"/patrullgenerator/{competitionId}")}");
            var competition = _competitions.GetSaved(competitionId);
            if (competition == null) return NotFound();
            if ((competition.CompetitionType ?? "") is not ("Faltskytte" or "MagnumFalt")) return NotFound();
            if (!await _auth.HasCompetitionStaffAccessAsync(competitionId, AdminAuthorizationService.CompetitionRead.Saved)) return Forbid();
            ViewData["PatrolGen"] = new PatrolGeneratorPageModel
            {
                CompetitionId = competitionId,
                Name = competition.CompetitionName ?? competition.Name ?? "",
                Date = competition.CompetitionDate?.Date,
                Published = competition.FaltskyttePatrolsPublished,
            };
            // Rotnoden är Model så att Master.cshtml kan rendera sajtens ram (samma recept som /ekonomi och /styrelse); sidans data går via ViewData.
            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            var rootNode = ctx?.Content?.GetAtRoot().FirstOrDefault();
            if (rootNode is null) return StatusCode(500, "Ingen rotnod hittades.");
            return View("~/Views/PatrolGenerator.cshtml", rootNode);
        }

        /// <summary>
        /// Övningsläget: exakt samma vy och samma JS som en riktig tävling, men utan tävlings-id. Öppet för inloggade medlemmar. Sidan har ingen väg att skriva:
        /// Tillämpa/Ångra/lås kräver ett tävlings-id och tävlingens personalbehörighet (StaffingController), och knapparna finns inte i övningens sida.
        /// </summary>
        [HttpGet("~/patrullgenerator/lab")]
        public async Task<IActionResult> Practice()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current is null) return Redirect($"/login-register?returnUrl={Uri.EscapeDataString("/patrullgenerator/lab")}");
            ViewData["PatrolGen"] = new PatrolGeneratorPageModel { Practice = true, CompetitionId = 0, Name = "Övning", Date = DateTime.Today, Published = false };
            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            var rootNode = ctx?.Content?.GetAtRoot().FirstOrDefault();
            if (rootNode is null) return StatusCode(500, "Ingen rotnod hittades.");
            return View("~/Views/PatrolGenerator.cshtml", rootNode);
        }
    }

    public sealed class PatrolGeneratorPageModel
    {
        /// <summary>Övning: samma sida och samma kod, men underlaget är ett scenario med påhittade skyttar. Inget tävlings-id, ingen skrivväg.</summary>
        public bool Practice { get; init; }
        public int CompetitionId { get; init; }
        public string Name { get; init; } = "";
        public DateTime? Date { get; init; }
        public bool Published { get; init; }
    }
}
