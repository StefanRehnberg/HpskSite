using HpskSite.Services;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// <c>/evenemang/deltagare</c> — arrangörens yta för ett klubb- eller kretsevenemang.
    ///
    /// <para><b>⚠️ TVÅ HÅLLNINGAR, EN SIDA.</b> Före evenemanget sitter arrangören vid en laptop
    /// och vill veta vilka som kommer, läsa noteringarna som kommit in med anmälningarna och
    /// bocka av mottagna Swish-betalningar mot bankappen. Under evenemanget står någon med en
    /// telefon eller platta och prickar närvaro, tar emot en sen anmälan och en betalning på
    /// plats. Det är samma lista — men den ena posturen sitter ned och läser, den andra står upp
    /// och trycker. Därför en SIDA och inte en modal: den går att bokmärka, tål en omladdning och
    /// överlever att skärmen slocknar. Samma skäl som <c>/station</c>, <c>/valvet</c>,
    /// <c>/skjutledare</c> och <c>/patrullista</c> alla är egna sidor.</para>
    ///
    /// <para><b>⚠️ DEN ERSÄTTER UPPROPSMODALEN, den läggs inte bredvid.</b> Två renderare över
    /// samma lista är dual-renderer-fällan som redan bitit startlistorna: den ena hinner få ett
    /// fält den andra inte har, och ingen märker vilken som är inaktuell.</para>
    ///
    /// <para><b>⚠️ INGEN EGEN DATAVÄG.</b> Sidan läser <c>ClubEvent/GetRoster</c> och skriver
    /// genom samma endpoints som medlemmens egen sida och disken redan använder. En egen
    /// läsväg hade varit en andra sanning om vem som är anmäld och vad hen är skyldig.</para>
    ///
    /// <para><b>⚠️ Renderas med sajtens vanliga layout</b>, inte chromeless. En första utsåga
    /// lånade <see cref="VaultController"/>s kiosksida — men den är byggd för en telefon i ett
    /// vapenvalv där sidhuvudet bara är i vägen. Den här ytan sitter arrangören vid, och utan
    /// Master.cshtml följer varken menyn, sidhuvudet eller <c>theme.css</c> med — alltså ingen
    /// dark mode, och en sida som ser ut att tillhöra någon annan sajt. Mönstret är
    /// <c>StyrelseController</c>s: rotnoden går in som modell så layoutens egna
    /// <c>Model.Root()</c>-anrop fungerar, och sidans data följer med i ViewData.</para>
    ///
    /// <para>Routad MVC-controller, ingen Umbraco-nod — samma mönster som <see cref="VaultController"/>.
    /// Behörigheten är evenemangets egen (<c>CanManageAsync</c>): klubbadmin, styrelse eller
    /// skjutledare, och för en kretshändelse kretsens motsvarighet. Sidan visar inget själv —
    /// varje endpoint grindar om.</para>
    /// </summary>
    [Route("evenemang/deltagare")]
    public class ClubEventDeskController : Controller
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly ClubEventParticipationService _participation;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;

        public ClubEventDeskController(
            IMemberManager memberManager,
            IMemberService memberService,
            ClubEventParticipationService participation,
            IUmbracoContextAccessor umbracoContextAccessor)
        {
            _memberManager = memberManager;
            _memberService = memberService;
            _participation = participation;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int e = 0, int eventId = 0, int id = 0)
        {
            // ⚠️ TRE NAMN PÅ SAMMA SAK, och det är inte slarv. /valvet läste bara `club` medan
            // klubbpanelens länk skickade `clubId`, och parametern ignorerades TYST — sidan föll
            // tillbaka på ett annat värde och visade en riktig lista för fel klubb. Att ta emot
            // alla tre kostar en rad och gör den felkällan omöjlig här.
            if (e <= 0) e = eventId;
            if (e <= 0) e = id;

            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var uctx) || uctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");
            var rootNode = uctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null) return StatusCode(500, "Ingen rotnod hittades.");

            var model = new EventDeskPageModel { EventId = e };
            IActionResult Render()
            {
                ViewData["EventDeskData"] = model;
                return View("ClubEventDesk", rootNode);
            }

            if (e <= 0)
            {
                model.Error = "Länken saknar vilket evenemang det gäller.";
                return Render();
            }

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
            {
                // ⚠️ Login-URL:en är /login-register (INTE /login-&-register), och målet URL-kodas
                // så adressen får ett enda '?' — en dubbel-?-URL 404:ar på prods IIS även om
                // Kestrel tolererar den.
                model.RequiresLogin = true;
                model.LoginUrl = "/login-register/?tab=login&returnUrl="
                               + Uri.EscapeDataString($"/evenemang/deltagare?e={e}");
                return Render();
            }

            var ctx = _participation.GetEventContext(e);
            if (ctx == null)
            {
                model.Error = "Evenemanget hittades inte.";
                return Render();
            }

            var member = _memberService.GetByEmail(current.Email);
            if (member == null || !await _participation.CanManageAsync(ctx, member.Id))
            {
                model.Error = "Du har inte behörighet att se deltagarlistan för det här evenemanget.";
                return Render();
            }

            model.EventName = ctx.EventName;
            model.EventDate = ctx.EventDate;
            model.OwnerName = ctx.OwnerName;
            model.CanManage = true;
            return Render();
        }
    }

    public class EventDeskPageModel
    {
        public int EventId { get; set; }
        public string EventName { get; set; } = "";
        public DateTime? EventDate { get; set; }
        public string OwnerName { get; set; } = "";
        public bool CanManage { get; set; }
        public bool RequiresLogin { get; set; }
        public string LoginUrl { get; set; } = "";
        public string? Error { get; set; }
    }
}
