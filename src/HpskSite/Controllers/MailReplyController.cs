using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using HpskSite.Services;
using HpskSite.Services.Firearms;
using HpskSite.Services.Mail;
using Umbraco.Cms.Core.Services;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Den publika, token-adresserade svarssidan — <c>/svara/{token}</c>.
    ///
    /// <para><b>⚠️ INGEN INLOGGNING, med flit.</b> Samma förtroendemodell som
    /// <c>/medlemsavgift/{token}</c> och <c>/evenemang/narvaro</c>: en äldre medlem ska kunna svara
    /// ur mejlet utan att först hitta sitt lösenord. Det är hela poängen med alternativet — en
    /// svarsväg med lägre friktion än att trycka Svara i mejlklienten. Kräver den inloggning
    /// förlorar den mot mejlsvaret, och då är vi tillbaka där vi började.</para>
    ///
    /// <para><b>⚠️⚠️ SIDAN ÄR EN INMATNINGSYTA, ALDRIG EN LÄSYTA.</b> Den som har länken kan svara
    /// i medlemmens namn, så sidan visar bara det medlemmen REDAN fått i sitt mejl: vad klubben bad
    /// om, och vilket vapen ärendet gäller (aliaset — medlemmens eget klartextnamn). <b>Skriv
    /// aldrig en vapenuppgift, ett personnummer eller någon annan känslig uppgift här.</b> Ska
    /// något mer visas hör det bakom inloggning på Min sida.</para>
    ///
    /// <para>Routad MVC-controller utan Umbraco-nod — samma mönster som <c>ReceiptController</c> och
    /// <c>MembershipFeeController</c>.</para>
    /// </summary>
    [Route("svara")]
    public class MailReplyController : Controller
    {
        private readonly MailReplyLinkService _links;
        private readonly MailReplyService _replies;
        private readonly ForeningsintygRequestService _intygRequests;
        private readonly ForeningsintygNotificationService _intygNotifications;
        private readonly ClubService _clubs;
        private readonly IMemberService _members;
        private readonly ILogger<MailReplyController> _logger;

        public MailReplyController(
            MailReplyLinkService links,
            MailReplyService replies,
            ForeningsintygRequestService intygRequests,
            ForeningsintygNotificationService intygNotifications,
            ClubService clubs,
            IMemberService members,
            ILogger<MailReplyController> logger)
        {
            _links = links;
            _replies = replies;
            _intygRequests = intygRequests;
            _intygNotifications = intygNotifications;
            _clubs = clubs;
            _members = members;
            _logger = logger;
        }

        private const string ViewPath = "~/Views/MailReply.cshtml";

        [HttpGet("{token}")]
        public IActionResult Index(string token)
        {
            var target = _links.Parse(token);
            if (target is null) return View(ViewPath, MailReplyPageModel.Invalid());

            var model = BuildModel(token, target);
            return View(ViewPath, model);
        }

        /// <summary>
        /// Tar emot svaret.
        ///
        /// <para><b>⚠️ <c>IgnoreAntiforgeryToken</c>: sidan är publik och har ingen inloggad
        /// session att binda en token till.</b> Skyddet är den signerade, tidsbegränsade länken.
        /// Samma avvägning som betalsidans <c>MarkSent</c>.</para>
        ///
        /// <para><b>⚠️ PRG.</b> Svaret sparas och sedan REDIRIGERAS det till samma adress, så en
        /// omladdning inte postar en andra gång. Dubblettspärren i tjänsten är bältet; det här är
        /// hängslet — och det som gör att sidan efter en omladdning visar kvittot i stället för ett
        /// tomt formulär.</para>
        /// </summary>
        [HttpPost("{token}")]
        [IgnoreAntiforgeryToken]
        public async Task<IActionResult> Submit(string token, string? body)
        {
            var target = _links.Parse(token);
            if (target is null) return View(ViewPath, MailReplyPageModel.Invalid());

            var model = BuildModel(token, target);

            // ⚠️ Ett avgjort ärende tar inte emot svar. Utan kontrollen kan medlemmen skriva in
            // sig i en förfrågan som redan är utfärdad eller avslagen, och ingen tittar där igen.
            if (!model.AcceptsReply)
                return View(ViewPath, model);

            var result = _replies.Add(
                target.ThreadKind, target.ThreadRefId, target.ClubId, target.MemberId, body ?? "");

            if (!result.Ok)
            {
                // ⚠️⚠️ ETT TAPPAT SVAR FÅR INTE VARA TYST MOT KLUBBEN. Det är samma tystnad hela
                // funktionen byggdes för att ta bort, bara spegelvänd: medlemmen VET att hen svarat
                // och klubben tror att hen tiger. Hände i prod 2026-09-09 (okörd migrering).
                //
                // ⚠️ Bara på SPARFEL, aldrig på ett valideringsfel. "Skriv ditt svar först" är
                // medlemmens att åtgärda och ska inte mejla någon — annars blir larmet brus, och
                // ett larm som alltid lyser slutar betyda något.
                if (result.SaveFailed)
                    await AfterFailedReplyAsync(target, (body ?? "").Trim());

                model.Error = result.Error;
                model.Draft = body ?? "";
                return View(ViewPath, model);
            }

            // Id == 0 utan fel = dubbelpostning, redan sparad. Behandla som lyckad.
            if (result.Id > 0)
                await AfterReplyAsync(target, (body ?? "").Trim());

            return Redirect($"/svara/{Uri.EscapeDataString(token)}?sparat=1");
        }

        /// <summary>
        /// Vad ett sparat svar BETYDER för ärendet.
        ///
        /// <para><b>⚠️ HÄR, aldrig i <c>MailReplyService</c>.</b> Tjänsten lagrar svaret och svarar
        /// på "finns det ett svar" — vad det gör med tillståndet äger varje yta själv. Ett
        /// gemensamt "ärendet har svar"-tillstånd över olika tillståndsmaskiner hade tvingat in fel
        /// semantik i den ena av dem.</para>
        ///
        /// <para><b>⚠️ Statusen ändras INTE.</b> Förfrågan står kvar som <c>UnderBehandling</c>,
        /// alltså öppen och räknad i klubbens bricka — vilket är rätt: det är fortfarande klubbens
        /// ärende. Att medlemmen svarat är HÄRLETT ur svarsraden (se
        /// <c>ForeningsintygRequestService</c>-läsningen i inkorgen), inte en fjärde status. Ett nytt
        /// statusvärde hade behövt en migrering och gjort <c>Open</c>/etiketterna tvetydiga.</para>
        /// </summary>
        private async Task AfterReplyAsync(MailReplyTarget target, string body)
        {
            if (target.ThreadKind != MailThreadKind.Foreningsintyg) return;

            try
            {
                var req = _intygRequests.GetById(target.ThreadRefId);
                if (req is null)
                {
                    _logger.LogWarning(
                        "Svar sparat på föreningsintygsförfrågan {Id} som inte kunde läsas — ingen avisering.",
                        target.ThreadRefId);
                    return;
                }

                await _intygNotifications.NotifyHandlerOfMemberReplyAsync(req, body);
            }
            catch (Exception ex)
            {
                // ⚠️ Sväljer. Svaret ÄR sparat; ett aviseringsfel får aldrig visa medlemmen ett
                // felmeddelande som antyder att svaret inte togs emot.
                _logger.LogError(ex, "Kunde inte avisera om svar på förfrågan {Id}.", target.ThreadRefId);
            }
        }

        /// <summary>
        /// Svaret gick inte att spara — se till att klubben ändå får veta, och att medlemmens text
        /// inte försvinner.
        ///
        /// <para><b>⚠️ Sväljer allt.</b> Vi står redan i en felhantering; ett fel HÄR får inte
        /// ersätta det felmeddelande medlemmen ska se med ett gult undantag.</para>
        /// </summary>
        private async Task AfterFailedReplyAsync(MailReplyTarget target, string body)
        {
            if (target.ThreadKind != MailThreadKind.Foreningsintyg) return;

            try
            {
                var req = _intygRequests.GetById(target.ThreadRefId);
                if (req is null)
                {
                    _logger.LogError(
                        "Medlemssvar på föreningsintygsförfrågan {Id} kunde varken sparas eller "
                        + "aviseras — förfrågan gick inte att läsa. Svaret var: {Body}",
                        target.ThreadRefId, body);
                    return;
                }

                await _intygNotifications.NotifyHandlerOfFailedReplyAsync(req, body);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Kunde inte avisera om TAPPAT svar på förfrågan {Id}. Svaret var: {Body}",
                    target.ThreadRefId, body);
            }
        }

        /// <summary>
        /// Bygger sidans modell. Kind-specifikt — en ny trådtyp lägger till en gren här.
        /// </summary>
        private MailReplyPageModel BuildModel(string token, MailReplyTarget target)
        {
            var model = new MailReplyPageModel
            {
                Found = true,
                Token = token,
                Saved = Request.Query.ContainsKey("sparat"),
                ClubName = _clubs.GetClubById(target.ClubId)?.Name ?? "Klubben",
                MemberName = ResolveMemberName(target.MemberId),
            };

            if (target.ThreadKind == MailThreadKind.Foreningsintyg)
            {
                model.Heading = "Svara klubben om ditt föreningsintyg";

                try
                {
                    var req = _intygRequests.GetById(target.ThreadRefId);
                    if (req is null || req.MemberId != target.MemberId)
                    {
                        // ⚠️ Ärendet finns inte, eller hör inte till medlemmen i länken. Behandlas
                        // som en ogiltig länk — skillnaden är bara användbar för den som gissar.
                        return MailReplyPageModel.Invalid();
                    }

                    model.FirearmLabel = string.IsNullOrWhiteSpace(req.FirearmAlias)
                        ? "vapnet i din förfrågan"
                        : req.FirearmAlias!;
                    model.RequestNote = req.HandlerNote ?? "";
                    model.AcceptsReply = req.IsOpen;
                    model.ClosedExplanation = req.IsOpen
                        ? ""
                        : $"Förfrågan är avgjord ({req.StatusLabel.ToLowerInvariant()}), så den tar inte "
                          + "emot fler svar. Hör av dig till klubben om något är oklart.";
                }
                catch (Exception ex)
                {
                    // Kan ärendet inte läsas går det inte att lova att svaret hamnar rätt.
                    _logger.LogError(ex, "Kunde inte läsa föreningsintygsförfrågan {Id} för svarssidan.",
                        target.ThreadRefId);
                    return MailReplyPageModel.Invalid();
                }
            }

            return model;
        }

        private string ResolveMemberName(int memberId)
        {
            try
            {
                var m = _members.GetById(memberId);
                if (m is null) return "";
                var name = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
                return name.Length > 0 ? name : (m.Name ?? "");
            }
            catch
            {
                return "";
            }
        }
    }

    /// <summary>Typad modell för den chromelösa <c>/svara/{token}</c>-sidan.</summary>
    public class MailReplyPageModel
    {
        public bool Found { get; set; }
        public string Token { get; set; } = "";
        public string Heading { get; set; } = "Svara klubben";
        public string ClubName { get; set; } = "";
        public string MemberName { get; set; } = "";

        /// <summary>Vapnets alias. Redan känt av medlemmen ur mejlet.</summary>
        public string FirearmLabel { get; set; } = "";

        /// <summary>Det klubben bad om — samma text som mejlet bar.</summary>
        public string RequestNote { get; set; } = "";

        /// <summary>Falskt när ärendet är avgjort; då renderas <see cref="ClosedExplanation"/>.</summary>
        public bool AcceptsReply { get; set; } = true;

        public string ClosedExplanation { get; set; } = "";

        /// <summary>Sant efter PRG-redirect — kvittot visas i stället för formuläret.</summary>
        public bool Saved { get; set; }

        public string? Error { get; set; }

        /// <summary>Behålls i textfältet när sparningen nekades, så inget skrivet går förlorat.</summary>
        public string Draft { get; set; } = "";

        public int MaxLength => MailReplyService.MaxBodyLength;

        public static MailReplyPageModel Invalid() => new MailReplyPageModel { Found = false };
    }
}
