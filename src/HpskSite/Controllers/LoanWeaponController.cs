using HpskSite.Models.Firearms;
using HpskSite.Services;
using HpskSite.Services.Firearms;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// <c>/lanevapen?club=</c> — medlemmens vy av klubbens lånevapen.
    ///
    /// <para><b>Routad MVC-controller, ingen Umbraco-nod</b> — samma mönster som
    /// <c>StyrelseController</c>. Vyn får en egen modell och renderar chromeless, så
    /// <c>Master.cshtml</c>:s rot-anrop aldrig blir ett problem.</para>
    ///
    /// <para><b>⚠️ Tre kolumner, och inte fler</b> (Stefans beslut 2026-09-02): <b>nummer · alias ·
    /// tillgängligt</b>. En nybörjare som ska boka behöver aldrig veta annat, och allt utöver det är
    /// uppgifter om föreningens vapen som inte hör på en medlemsnära yta. Bokningen (punkt 6) lade
    /// till en knapp, inte fler uppgifter.</para>
    ///
    /// <para><b>Sidan hämtar inte vapenlistan själv.</b> Tillgänglighet finns bara för ett bestämt
    /// tidsfönster, som väljs på sidan, så listan kommer från
    /// <c>LoanWeaponApi/GetAvailability</c>.</para>
    /// </summary>
    [Route("lanevapen")]
    public class LoanWeaponController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ClubService _clubService;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;

        public LoanWeaponController(
            IUmbracoContextAccessor umbracoContextAccessor,
            ClubService clubService,
            IMemberManager memberManager,
            IMemberService memberService,
            MemberClubService memberClubs)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _clubService = clubService;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int club = 0)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var model = new LoanWeaponPageModel();

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
            {
                // ⚠️ Login-URL:en är /login-register (INTE /login-&-register), och parametern heter
                // returnUrl. Hela målet URL-kodas så adressen får ett enda '?' — en dubbel-?-URL
                // 404:ar på prods IIS även om Kestrel tolererar den.
                model.RequiresLogin = true;
                model.LoginUrl = "/login-register/?tab=login&returnUrl=" +
                                 Uri.EscapeDataString(club > 0 ? $"/lanevapen?club={club}" : "/lanevapen");
                return View("LoanWeapons", model);
            }

            var member = _memberService.GetByEmail(current.Email);
            var myClubs = _memberClubs.GetAllClubIds(member);

            model.Clubs = myClubs
                .Select(id => new LoanWeaponClub { ClubId = id, Name = _clubService.GetClubNameById(id) ?? "" })
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .OrderBy(c => c.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true))
                .ToList();

            // Utan ?club väljs medlemmens första klubb. En tom sida med en väljare ingen fyllt i är
            // sämre än en sida som visar något och låter en byta.
            var clubId = club > 0 ? club : model.Clubs.FirstOrDefault()?.ClubId ?? 0;
            model.SelectedClubId = clubId;

            if (clubId <= 0)
            {
                model.Message = "Du är inte medlem i någon klubb, så det finns inga lånevapen att visa.";
                return View("LoanWeapons", model);
            }

            // ⚠️ Medlemskapet ÄR grinden. En klubbs lånevapen är för klubbens egna medlemmar — det är
            // den carve-out som gör funktionen förenlig med "inget publikt bokningssystem".
            if (!myClubs.Contains(clubId))
            {
                model.Message = "Du kan bara se lånevapen i en klubb du är medlem i.";
                return View("LoanWeapons", model);
            }

            model.ClubName = _clubService.GetClubNameById(clubId) ?? $"Klubb {clubId}";

            // ⚠️ Vapenlistan byggs INTE här. Tillgänglighet finns bara för ett bestämt tidsfönster,
            // och fönstret väljs på sidan — så listan hämtas av `LoanWeaponApi/GetAvailability`.
            // En serverside-lista här skulle behöva svara på "är vapnet ledigt?" utan att veta när,
            // vilket är den enda formen av svar som är säkert fel.
            return View("LoanWeapons", model);
        }

        /// <summary>
        /// <c>/v/{code}</c> — skytten skannar etiketten på vapnet.
        ///
        /// <para><b>⚠️ ADRESSEN ÄR KORT FÖR ATT ETIKETTEN SKA BLI LITEN.</b> Varje tecken i den
        /// här sökvägen är tecken som ska rymmas i en QR-kod på ett vapen. Föregångaren
        /// <c>/lanevapen/skanna?t=&lt;skyddad token&gt;</c> var ~150 tecken och gav en kod på 53×53
        /// moduler, alltså en etikett som inte fick plats någonstans på ett vapen utan att bli
        /// oläsbar. <b>Förläng den inte, och gör den aldrig till en frågesträng igen</b> — det
        /// vore att ta tillbaka hela krympningen utan att någon märker det förrän en klubb har
        /// laminerat femtio etiketter.</para>
        ///
        /// <para><b>⚠️ <c>~/</c> bryter medvetet ur controllerns <c>[Route("lanevapen")]</c>.</b>
        /// Sidan hör hemma i lånevapenflödet men får inte bära dess prefix i adressen.</para>
        ///
        /// <para><b>Renderar bara skalet.</b> Inloggningskravet, vapnet och vad som är möjligt
        /// avgörs av <c>Firearm/GetScanState</c>, så sidan kan visas även för den som inte är
        /// inloggad och då erbjuda inloggning med rätt returnUrl.</para>
        /// </summary>
        [HttpGet("~/v/{code?}")]
        public IActionResult Scan(string? code)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            return View("LoanWeaponScan", new LoanScanPageModel { Code = code });
        }
    }

    public class LoanScanPageModel
    {
        /// <summary>
        /// Etikettens kod, oslagen.
        ///
        /// <para><b>⚠️ Sidan slår INTE upp den serverside.</b> Den skickas vidare till
        /// <c>Firearm/GetScanState</c>, som svarar utan att skriva något. En QR som öppnas av
        /// misstag i en kameraförhandsvisning får inte lämna ut ett vapen — och hade sidan slagit
        /// upp koden och registrerat något vid rendering vore just det vad som hände.</para>
        /// </summary>
        public string? Code { get; set; }
    }

    public class LoanWeaponPageModel
    {
        public bool RequiresLogin { get; set; }
        public string? LoginUrl { get; set; }
        public string? Message { get; set; }
        public int SelectedClubId { get; set; }
        public string? ClubName { get; set; }
        public List<LoanWeaponClub> Clubs { get; set; } = new();
    }

    public class LoanWeaponClub
    {
        public int ClubId { get; set; }
        public string Name { get; set; } = "";
    }

}
