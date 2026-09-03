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

        public VaultController(
            IUmbracoContextAccessor umbracoContextAccessor,
            ClubService clubService,
            AdminAuthorizationService adminAuth,
            IMemberManager memberManager,
            IMemberService memberService,
            MemberClubService memberClubs,
            FirearmService firearms)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _clubService = clubService;
            _adminAuth = adminAuth;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
            _firearms = firearms;
        }

        [HttpGet("")]
        public async Task<IActionResult> Index(int club = 0, int clubId = 0, string? day = null)
        {
            // ⚠️ BÅDA NAMNEN. Rapporterat från prod 2026-09-02: klubbpanelens länk skickade
            // `clubId` medan routen bara läste `club`, så parametern ignorerades och sidan föll
            // tillbaka på medlemmens PRIMÄRA klubb — en styrelsemedlem i Falkenberg med Varberg
            // som primärklubb landade i Varbergs valv. Felläget var tyst: sidan visade en riktig
            // klubb med en riktig lista, bara inte den man kom från. Resten av kodbasen använder
            // `clubId`, alltså tar den här sidan emot båda i stället för att vara den enda som
            // heter något annat.
            if (club <= 0) club = clubId;
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

            // ⚠️ EN ENDA KLUBB VÄLJS ÅT ANVÄNDAREN, aldrig en av flera. Att gissa när det finns
            // två kandidater ger en sida som ser riktig ut men visar fel klubbs lån — och den som
            // står i valvet upptäcker det först när namnen inte stämmer. Har hen flera klubbar och
            // ingen är angiven frågar sidan i stället.
            var selected = club > 0
                ? club
                : (model.Clubs.Count == 1 ? model.Clubs[0].ClubId : 0);

            if (selected > 0 && !candidates.Contains(selected))
            {
                model.Message = "Du hanterar inte lånevapen i den klubben.";
                return View("Vault", model);
            }

            model.SelectedClubId = selected;
            if (selected > 0)
            {
                model.ClubName = _clubService.GetClubNameById(selected) ?? $"Klubb {selected}";
                model.LoanableCount = _firearms.CountLoanable(selected);
            }

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
        /// Etikettens adress. Tom sträng om vapnet inte kunde få en kod.
        ///
        /// <para><b>⚠️ Adressen byggs på ETT ställe</b>, <see cref="FirearmLabelCode.Url"/>. Den är
        /// versal i sin helhet för att falla inom QR:ens alfanumeriska läge, och en avvikande
        /// stavning här hade gett en större kod på just den utskrift som ska bli mindre.</para>
        /// </summary>
        private string LabelUrl(int firearmId)
        {
            var code = _firearms.EnsureLabelCode(firearmId);
            if (string.IsNullOrWhiteSpace(code)) return "";

            var req = HttpContext.Request;
            return FirearmLabelCode.Url(req.Scheme, req.Host.Value ?? "", code);
        }

        [HttpGet("etiketter")]
        public async Task<IActionResult> Labels(int club = 0, int clubId = 0, int firearm = 0)
        {
            if (club <= 0) club = clubId;
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return Redirect("/login-register/?tab=login");

            var member = _memberService.GetByEmail(current.Email);
            var mine = _memberClubs.GetAllClubIds(member);

            // ⚠️ INGEN FALLBACK PÅ PRIMÄRKLUBBEN. `mine.FirstOrDefault()` gav rubriken
            // "Vapenetiketter — Varbergs Pistolklubb" åt någon som tryckte på knappen inne i
            // Falkenbergs klubbadministration, alltså ett ark med FEL klubbs vapen att klistra på
            // rätt klubbs vapen. En saknad klubb ska vara ett fel, inte en gissning.
            var target = club > 0 ? club : (mine.Count == 1 ? mine[0] : 0);

            // Etikettarket är klubbadministration — det är en engångsutskrift, inte något
            // skjutledaren gör på banan.
            if (target <= 0)
                return Content("Ingen klubb angiven. Öppna etiketterna från klubbens vapenflik.");
            if (!await _adminAuth.IsClubAdminForClub(target))
                return Content("Åtkomst nekad");

            // ⚠️ EN KLUBB SOM INTE FINNS SKA VÄGRAS, inte döpas till "Klubb 1". Funnet av sviten
            // 2026-09-02: `?club=1` gav ett ark med rubriken "Vapenetiketter — Klubb 1" och noll
            // vapen, alltså en sida som ser fullt legitim ut för något som inte existerar. Det är
            // samma form som prodbuggen — en trovärdig sida för fel klubb — och `??`-fallbacken
            // var det som gjorde den trovärdig. En sajtadministratör är dessutom klubbadmin
            // överallt, så behörighetskontrollen ovan stoppar den inte.
            var labelClubName = _clubService.GetClubNameById(target);
            if (string.IsNullOrWhiteSpace(labelClubName))
                return Content("Klubben hittades inte.");

            var model = new VaultLabelsModel
            {
                ClubId = target,
                ClubName = labelClubName,
                // ⚠️ ETT VAPEN I TAGET ÄR NORMALFALLET, inte hela arket. Rapporterat från prod
                // 2026-09-02: man lägger in ett nytt lånevapen och behöver EN etikett — hela arket
                // är en engångsutskrift när klubben börjar. Därför tar sidan emot `firearm`, och
                // knappen sitter på vapnets egen rad i klubbpanelen.
                SingleFirearmId = firearm,
                Firearms = _firearms.GetForScope(Models.Firearms.FirearmScope.Club(target))
                    .Where(f => f.IsLoanable)
                    .Where(f => firearm <= 0 || f.Id == firearm)
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
                        // följa hela skanningsvägen (koden finns annars bara inne i bilden).
                        //
                        // ⚠️ Den får INTE tryckas synligt på etiketten. Koden är hemligheten som
                        // gör att man måste stå framför vapnet för att kunna checka ut det — läsbar
                        // på ett fotografi av valvet vore den ingen hemlighet alls.
                        Url = LabelUrl(f.Id),
                    })
                    .ToList(),
            };

            return View("VaultLabels", model);
        }

        /// <summary>
        /// <c>/valvet/affisch?club=</c> — A4:an som tejpas på insidan av valvdörren.
        ///
        /// <para><b>⚠️ Det här är hur vapenansvarig hittar valvet alls.</b> Gunnar är 74, är
        /// skjutledare och inte klubbadministratör, och kommer aldrig att navigera in i en
        /// klubbpanel för att leta en knapp. Han riktar kameran mot väggen. Första versionen la
        /// knappen där han aldrig är — en funktion ingen hittar är inte byggd.</para>
        /// </summary>
        [HttpGet("affisch")]
        public async Task<IActionResult> Poster(int club = 0, int clubId = 0)
        {
            if (club <= 0) club = clubId;

            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email == null) return Redirect("/login-register/?tab=login");

            var member = _memberService.GetByEmail(current.Email);
            var mine = _memberClubs.GetAllClubIds(member);
            var target = club > 0 ? club : (mine.Count == 1 ? mine[0] : 0);

            // Samma grind som etikettarket: att sätta upp affischen är klubbadministration.
            if (target <= 0)
                return Content("Ingen klubb angiven. Öppna affischen från klubbens vapenflik.");
            if (!await _adminAuth.IsClubAdminForClub(target))
                return Content("Åtkomst nekad");

            // Samma skäl som etikettarket: en affisch för "Klubb 1" är en affisch någon tejpar upp.
            var posterClubName = _clubService.GetClubNameById(target);
            if (string.IsNullOrWhiteSpace(posterClubName))
                return Content("Klubben hittades inte.");

            var req = HttpContext.Request;
            return View("VaultPoster", new VaultPosterModel
            {
                ClubId = target,
                ClubName = posterClubName,
                VaultUrl = $"{req.Scheme}://{req.Host}/valvet?club={target}",
            });
        }
    }

    public class VaultPosterModel
    {
        public int ClubId { get; set; }
        public string ClubName { get; set; } = "";
        public string VaultUrl { get; set; } = "";
    }

    public class VaultLabelsModel
    {
        public int ClubId { get; set; }
        public string ClubName { get; set; } = "";

        /// <summary>&gt; 0 när sidan begärdes för ETT vapen. Styr rubriken, inte urvalet.</summary>
        public int SingleFirearmId { get; set; }

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
