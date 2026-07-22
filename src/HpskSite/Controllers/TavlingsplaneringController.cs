using HpskSite.Models.Staffing;
using HpskSite.Services;
using HpskSite.Services.Staffing;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Dedicated per-competition planning page at /tavlingsplanering?c={competitionId} — the home for the
    /// preparatory work of putting an event on: **Förberedelser** (work-breakdown) + **Bemanning** (the
    /// day-of functionary roster). Deliberately SEPARATE from the operational day-of hub (the "Funktionärer"
    /// tab in competition management): this work relates to the competition rather than being part of running
    /// it, like the Fältkonfigurator. Reached from the "Planering" button in the comp-management header.
    ///
    /// Routed controller, no backoffice node needed (mirrors SightPictureController / StyrelseController).
    /// Auth is the same four-tier competition-staff gate the Staffing endpoints enforce; this page-level
    /// check just decides whether to render the workspace or a friendly access message.
    /// See Documentation/COMPETITION_STAFFING_SYSTEM.md.
    /// </summary>
    [Route("tavlingsplanering")]
    public class TavlingsplaneringController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly AdminAuthorizationService _auth;
        private readonly StaffingService _staffing;
        private readonly WorkBreakdownService _work;

        public TavlingsplaneringController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberManager memberManager,
            IMemberService memberService,
            AdminAuthorizationService auth,
            StaffingService staffing,
            WorkBreakdownService work)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberManager = memberManager;
            _memberService = memberService;
            _auth = auth;
            _staffing = staffing;
            _work = work;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int c)
        {
            // Master.cshtml (UmbracoViewPage) calls Model.Root()/.Url()/.Children — needs an
            // IPublishedContent Model. Pass the site root so the shared layout renders normally.
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-&-register/?tab=login&RedirectUrl={Uri.EscapeDataString($"/tavlingsplanering?c={c}")}");

            var competition = c > 0 ? ctx.Content.GetById(c) : null;
            var isCompetition = competition != null && competition.ContentType.Alias == "competition";

            ViewData["CompetitionId"] = c;
            ViewData["CompetitionExists"] = isCompetition;
            ViewData["CompetitionName"] = isCompetition ? (competition!.Value<string>("competitionName") ?? "Tävling") : "";
            ViewData["Discipline"] = isCompetition ? (competition!.Value("competitionType")?.ToString() ?? "Precision") : "Precision";
            ViewData["ManageUrl"] = $"/competitionmanagement?competitionId={c}";
            ViewData["CanAccess"] = isCompetition && await HasCompetitionAccessAsync(c);

            return View("Tavlingsplanering", rootNode);
        }

        /// <summary>Printable "vem gör vad"-blad — roster (Bemanning) + prep (Förberedelser) on one sheet.</summary>
        [HttpGet("blad")]
        public async Task<IActionResult> Blad(int c)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var currentMember = await _memberManager.GetCurrentMemberAsync();
            if (currentMember?.Email == null)
                return Redirect($"/login-&-register/?tab=login&RedirectUrl={Uri.EscapeDataString($"/tavlingsplanering/blad?c={c}")}");

            var competition = c > 0 ? ctx.Content.GetById(c) : null;
            var isCompetition = competition != null && competition.ContentType.Alias == "competition";
            var canAccess = isCompetition && await HasCompetitionAccessAsync(c);

            var model = new PlaneringBladModel
            {
                CompetitionId = c,
                Exists = isCompetition,
                CanAccess = canAccess,
                CompName = isCompetition ? (competition!.Value<string>("competitionName") ?? "Tävling") : "",
                Discipline = isCompetition ? (competition!.Value("competitionType")?.ToString() ?? "Precision") : "Precision",
            };

            if (canAccess)
            {
                var d = competition!.Value<DateTime?>("competitionDate");
                if (d.HasValue && d.Value != default)
                    model.CompDate = d.Value.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
                model.Roster = _staffing.BuildRoster(c, model.Discipline, canEdit: false);
                model.Work = _work.Build(c, canEdit: false);
            }
            return View("TavlingsplaneringBlad", model);
        }

        /// <summary>
        /// Four-tier competition-staff gate: site admin OR competition manager OR club admin for the
        /// competition's club OR skjutledare for that club; plus regional admin for region-hosted comps.
        /// Mirrors StaffingController / EventMessageController.
        /// </summary>
        private async Task<bool> HasCompetitionAccessAsync(int competitionId)
        {
            if (competitionId <= 0) return false;
            try
            {
                if (await _auth.IsCurrentUserAdminAsync()) return true;
                if (await _auth.IsCompetitionManager(competitionId)) return true;

                if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                    return false;
                var comp = ctx.Content.GetById(competitionId);
                if (comp == null) return false;

                var clubId = comp.Value<int>("clubId");
                if (clubId > 0)
                {
                    if (await _auth.IsClubAdminForClub(clubId)) return true;
                    if (await _auth.IsSkjutledareForClub(clubId)) return true;
                }
                else
                {
                    var regionCode = comp.Value<string>("regionalFederation") ?? "";
                    if (!string.IsNullOrEmpty(regionCode) && await _auth.IsRegionalAdminForRegion(regionCode))
                        return true;
                }
                return false;
            }
            catch { return false; }
        }
    }
}
