using HpskSite.Services;
using HpskSite.Services.Firearms;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// <c>/valvet</c> — vapenansvarigs yta när hen står i valvet.
    ///
    /// <para><b>⚠️ DEN HÄR SIDAN ÄR BYGGD FÖR EN TEKNIKRÄDD 74-ÅRING</b> med sex nybörjare bakom
    /// sig klockan kvart i sex. Hen är inte rädd för appen — hen är rädd för att trycka fel och
    /// inte kunna ångra det. Följden styr hela ytan: stora bokstäver, <b>ingenting att skriva</b>,
    /// och allt går att backa.</para>
    ///
    /// <para><b>⚠️ SIDAN GRINDAR ALDRIG EN FYSISK UTLÄMNING.</b> Står en nybörjare framför
    /// vapenansvarig och skärmen säger nej, lämnas vapnet ut ändå — och då ljuger registret, vilket
    /// är värre än inget, för nu tror vi att vi vet var vapnen är. Den som inte bokat får därför ett
    /// lån skapat på plats i stället för ett avslag.</para>
    ///
    /// <para><b>Egen sida, inte en flik i klubbadministrationen.</b> Den öppnas på en telefon i
    /// valvet och ska inte kräva att någon navigerar i en adminpanel med fjorton rälsposter.</para>
    ///
    /// <para>Routad MVC-controller, ingen Umbraco-nod — samma mönster som
    /// <see cref="LoanWeaponController"/>. Listan hämtas av <c>FirearmAdmin/GetVaultBoard</c>, så
    /// sidan aldrig visar ett läge som är en sidladdning gammalt.</para>
    /// </summary>
    [Route("valvet")]
    public class VaultController : Controller
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ClubService _clubService;
        private readonly AdminAuthorizationService _adminAuth;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;
        private readonly FirearmService _firearms;
        private readonly IDataProtector _labelProtector;

        public VaultController(
            IUmbracoContextAccessor umbracoContextAccessor,
            ClubService clubService,
            AdminAuthorizationService adminAuth,
            IMemberManager memberManager,
            IMemberService memberService,
            MemberClubService memberClubs,
            FirearmService firearms,
            IDataProtectionProvider dataProtection)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _clubService = clubService;
            _adminAuth = adminAuth;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
            _firearms = firearms;
            _labelProtector = dataProtection.CreateProtector("Firearm.LoanLabel.v1");
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int club = 0, string? day = null)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var model = new VaultPageModel
            {
                Day = DateTime.TryParse((day ?? "").Trim(), out var d) ? d.Date : DateTime.Now.Date,
            };

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null)
            {
                // ⚠️ Login-URL:en är /login-register (INTE /login-&-register), och hela målet
                // URL-kodas så adressen får ett enda '?' — en dubbel-?-URL 404:ar på prods IIS
                // även om Kestrel tolererar den.
                model.RequiresLogin = true;
                model.LoginUrl = "/login-register/?tab=login&returnUrl=" +
                                 Uri.EscapeDataString(club > 0 ? $"/valvet?club={club}" : "/valvet");
                return View("Vault", model);
            }

            var member = _memberService.GetByEmail(current.Email);

            // ⚠️ Grinden är klubbadmin ELLER skjutledare — samma som utlämningen i klubbpanelen.
            // Skjutledaren är den som står på banan, och en egen "lånevapenansvarig" hade gett
            // mindre än vad de rollerna redan har, med en tyst utelåsning på träningskvällen som
            // felläge.
            var candidates = new List<int>();
            foreach (var id in _memberClubs.GetAllClubIds(member))
            {
                if (await _adminAuth.IsClubAdminForClub(id) || await _adminAuth.IsSkjutledareForClub(id))
                    candidates.Add(id);
            }

            // Sajtadmin kommer åt sin egen klubbs valv som alla andra — det finns ingen anledning
            // att kunna öppna en främmande klubbs valv, och listan är personuppgifter om vem som
            // lånar vad.
            model.Clubs = candidates
                .Select(id => new VaultClub { ClubId = id, Name = _clubService.GetClubNameById(id) ?? "" })
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .OrderBy(c => c.Name, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true))
                .ToList();

            if (model.Clubs.Count == 0)
            {
                model.Message = "Valvet är för klubbens vapenansvariga. Är du det ska du vara " +
                                "klubbadministratör eller skjutledare i klubben.";
                return View("Vault", model);
            }

            var clubId = club > 0 ? club : model.Clubs[0].ClubId;
            if (!candidates.Contains(clubId))
            {
                model.Message = "Du hanterar inte lånevapen i den klubben.";
                return View("Vault", model);
            }

            model.SelectedClubId = clubId;
            model.ClubName = _clubService.GetClubNameById(clubId) ?? $"Klubb {clubId}";
            model.LoanableCount = _firearms.CountLoanable(clubId);

            return View("Vault", model);
        }

        /// <summary>
        /// <c>/valvet/etiketter?club=</c> — utskriftsarket med en QR-etikett per lånevapen.
        ///
        /// <para><b>⚠️ Etiketten sitter på VAPNET, inte på hyllplatsen.</b> Ett vapen som läggs
        /// tillbaka på fel plats gör en hylletikett direkt felaktig — hyllplatsen är föränderligt
        /// tillstånd som ingen underhåller, vapnet är identiteten.</para>
        ///
        /// <para>Numret trycks stort intill koden, eftersom numret är skyttens ord för vapnet
        /// (<em>"jag har alltid nr 7"</em>) och etiketten också ska gå att läsa utan telefon.</para>
        /// </summary>
        /// <summary>
        /// Etikettens adress. <b>⚠️ SAMMA protector-purpose</b> som <c>FirearmController</c> och
        /// <c>FirearmAdminController</c> — skiljer strängarna sig blir varje utskriven etikett
        /// oläsbar, tyst.
        /// </summary>
        private string LabelUrl(int firearmId)
        {
            var token = _labelProtector.Protect($"firearm:{firearmId}");
            var req = HttpContext.Request;
            return $"{req.Scheme}://{req.Host}/lanevapen/skanna?t={Uri.EscapeDataString(token)}";
        }

        [HttpGet("etiketter")]
        public async Task<IActionResult> Labels(int club = 0)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return Redirect("/login-register/?tab=login");

            var member = _memberService.GetByEmail(current.Email);
            var mine = _memberClubs.GetAllClubIds(member);
            var clubId = club > 0 ? club : mine.FirstOrDefault();

            // Etikettarket är klubbadministration — det är en engångsutskrift, inte något
            // skjutledaren gör på banan.
            if (clubId <= 0 || !await _adminAuth.IsClubAdminForClub(clubId))
                return Content("Åtkomst nekad");

            var model = new VaultLabelsModel
            {
                ClubId = clubId,
                ClubName = _clubService.GetClubNameById(clubId) ?? $"Klubb {clubId}",
                Firearms = _firearms.GetForScope(Models.Firearms.FirearmScope.Club(clubId))
                    .Where(f => f.IsLoanable)
                    .OrderBy(f => f.ClubWeaponNumber ?? int.MaxValue)
                    .ThenBy(f => f.Alias, StringComparer.Ordinal)
                    .Select(f => new VaultLabel
                    {
                        FirearmId = f.Id,
                        Number = f.ClubWeaponNumber,
                        Alias = f.Alias,
                        WeaponClass = f.WeaponClass,
                        // ⚠️ Adressen bärs som text vid sidan av bilden, osynlig i utskriften —
                        // samma mönster som Fältskyttes stationsaffisch. Det är enda sättet att
                        // felsöka en QR som inte fungerar, och det enda sättet en verifiering kan
                        // följa hela skanningsvägen (token finns annars bara inne i PNG:en).
                        Url = LabelUrl(f.Id),
                    })
                    .ToList(),
            };

            return View("VaultLabels", model);
        }
    }

    public class VaultLabelsModel
    {
        public int ClubId { get; set; }
        public string ClubName { get; set; } = "";
        public List<VaultLabel> Firearms { get; set; } = new();
    }

    public class VaultLabel
    {
        public int FirearmId { get; set; }
        public int? Number { get; set; }
        public string Alias { get; set; } = "";
        public string? WeaponClass { get; set; }
        public string Url { get; set; } = "";
    }

    public class VaultPageModel
    {
        public bool RequiresLogin { get; set; }
        public string? LoginUrl { get; set; }
        public string? Message { get; set; }
        public int SelectedClubId { get; set; }
        public string? ClubName { get; set; }
        public int LoanableCount { get; set; }
        public DateTime Day { get; set; }
        public List<VaultClub> Clubs { get; set; } = new();
    }

    public class VaultClub
    {
        public int ClubId { get; set; }
        public string Name { get; set; } = "";
    }
}
