using HpskSite.Services;
using HpskSite.Models.Firearms;
using HpskSite.Services.Firearms;
using Microsoft.AspNetCore.DataProtection;
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
    /// Administrationen av <b>vem som får läsa medlemmarnas vapeninnehav</b>.
    ///
    /// <para>Egen controller, inte en flik i <c>ClubAdminController</c> (19 endpoints): det här är
    /// den enda ytan där en klubb ger någon insyn i medlemmarnas skyddade uppgifter, och den ska gå
    /// att hitta och granska på ett ställe.</para>
    ///
    /// <para><b>⚠️ Ingen endpoint här lämnar ut en enda vapenuppgift.</b> Den utser och listar
    /// behörigheter. Den som kan utse kan inte nödvändigtvis läsa, och det är avsiktligt.</para>
    /// </summary>
    public class FirearmAdminController : SurfaceController
    {
        private readonly FirearmAuthorizationService _firearmAuth;
        private readonly AdminAuthorizationService _adminAuth;
        private readonly ClubService _clubService;
        private readonly FirearmService _firearms;
        private readonly ForeningsintygRequestService _requests;
        private readonly FirearmBookingService _bookings;
        private readonly Umbraco.Cms.Core.Security.IMemberManager _memberManager;
        private readonly Umbraco.Cms.Core.Services.IMemberService _memberService;
        private readonly ILogger<FirearmAdminController> _logger;

        /// <summary>
        /// Samma purpose som <c>FirearmController</c> — etiketten skapas här och läses där, så de
        /// MÅSTE vara samma sträng. Skiljer de sig blir varje utskriven etikett oläsbar, tyst.
        /// </summary>
        private readonly IDataProtector _labelProtector;
        private readonly LoanWeaponClubRules _clubRules;
        private readonly HpskSite.Services.TrainingGroupService _trainingGroups;
        private readonly Umbraco.Cms.Core.Services.IContentService _contentService;

        public FirearmAdminController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FirearmAuthorizationService firearmAuth,
            AdminAuthorizationService adminAuth,
            ClubService clubService,
            FirearmService firearms,
            ForeningsintygRequestService requests,
            FirearmBookingService bookings,
            Umbraco.Cms.Core.Security.IMemberManager memberManager,
            Umbraco.Cms.Core.Services.IMemberService memberService,
            ILogger<FirearmAdminController> logger,
            IDataProtectionProvider dataProtection,
            LoanWeaponClubRules clubRules,
            HpskSite.Services.TrainingGroupService trainingGroups,
            Umbraco.Cms.Core.Services.IContentService contentService)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _firearmAuth = firearmAuth;
            _adminAuth = adminAuth;
            _clubService = clubService;
            _firearms = firearms;
            _requests = requests;
            _bookings = bookings;
            _memberManager = memberManager;
            _memberService = memberService;
            _logger = logger;
            _labelProtector = dataProtection.CreateProtector("Firearm.LoanLabel.v1");
            _clubRules = clubRules;
            _trainingGroups = trainingGroups;
            _contentService = contentService;
        }

        /// <summary>
        /// Klubbens behörighetsläge: vilka som kan läsa, vilka som är valbara, och om rollen är
        /// obesatt.
        ///
        /// <para>Läsgrinden är den vanliga klubbadmingrinden — att SE vem som har behörigheten är
        /// klubbadministration, medan att ÄNDRA den kräver ett styrelseuppdrag. Klienten får
        /// <c>canAssign</c> och låser knapparna, men servern är auktoriteten.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetViewerState(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltig klubb" });

            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            try
            {
                var viewers = _firearmAuth.GetViewers(clubId);
                var candidates = _firearmAuth.GetBoardCandidates(clubId);
                var canAssign = await _firearmAuth.CanAssignViewersAsync(clubId);
                var activeCount = viewers.Count(v => !v.IsDormant);

                return Json(new
                {
                    success = true,
                    canAssign,
                    clubName = _clubService.GetClubNameById(clubId),
                    activeCount,

                    // Rollen är obesatt. Det är INTE ett fel — men klubben kan då inte utfärda ett
                    // föreningsintyg med vapenuppgifter, och det ska stå på skärmen innan någon
                    // upptäcker det den dag en medlem söker licens.
                    unstaffed = activeCount == 0,

                    // Ingen valbar person alls: styrelsen är tom eller inte inlagd. Ett annat problem
                    // än "obesatt", och med en annan åtgärd — därför ett eget fält.
                    noBoardRegistered = candidates.Count == 0,

                    // Ingen kvarvarande styrelsemedlem är också klubbadmin. Då kan klubben inte utse
                    // själv och behöver hjälp av sajtadmin. Rapporteras så att svaret "varför är
                    // knappen låst" finns på skärmen.
                    needsSiteAdminToAssign = !canAssign && candidates.Count > 0,

                    viewers = viewers.Select(v => new
                    {
                        v.MemberId, v.Name, v.IsDormant, v.TermExpired,
                        v.TermEndsDate, roleTitles = v.RoleTitles,
                    }),
                    candidates = candidates.Select(c => new
                    {
                        c.MemberId, c.Name, c.IsViewer, c.TermExpired,
                        c.TermEndsDate, roleTitles = c.RoleTitles,
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetViewerState failed for club {ClubId}", clubId);
                return Json(new { success = false, message = "Kunde inte läsa behörighetsläget" });
            }
        }

        /// <summary>
        /// Utser en styrelsemedlem till föreningsintygsansvarig.
        ///
        /// <para>Tar FORMULÄRFÄLT och inte en JSON-kropp, av ett praktiskt skäl: antiforgery-skyddet
        /// i den här kodbasen bygger genomgående på det dolda <c>__RequestVerificationToken</c>-fältet
        /// i en FormData-postning. En <c>[FromBody]</c>-variant hade krävt tokenet i en header och
        /// därmed en egen fetch-väg som ingen annan yta här använder.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignViewer(int clubId, int memberId)
        {
            if (clubId <= 0 || memberId <= 0)
                return Json(new { success = false, message = "Ogiltig begäran" });

            if (!await _firearmAuth.CanAssignViewersAsync(clubId))
                return Json(new
                {
                    success = false,
                    message = "Bara en klubbadministratör som också sitter i klubbens styrelse " +
                              "(eller pistol.nu:s administratör) kan ändra den här behörigheten.",
                });

            var (ok, message) = await _firearmAuth.AssignViewerAsync(clubId, memberId);

            if (ok)
                _logger.LogInformation(
                    "Firearm viewer assigned: club {ClubId}, member {MemberId}", clubId, memberId);

            return Json(new { success = ok, message, activeViewers = _firearmAuth.CountActiveViewers(clubId) });
        }

        /// <summary>
        /// Tar bort behörigheten. Används både för att återkalla och för att städa bort en vilande
        /// behörighet efter en avgång.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveViewer(int clubId, int memberId)
        {
            if (clubId <= 0 || memberId <= 0)
                return Json(new { success = false, message = "Ogiltig begäran" });

            if (!await _firearmAuth.CanAssignViewersAsync(clubId))
                return Json(new
                {
                    success = false,
                    message = "Bara en klubbadministratör som också sitter i klubbens styrelse " +
                              "(eller pistol.nu:s administratör) kan ändra den här behörigheten.",
                });

            var (ok, message) = _firearmAuth.RemoveViewer(clubId, memberId);
            var remaining = _firearmAuth.CountActiveViewers(clubId);

            if (ok)
                _logger.LogInformation(
                    "Firearm viewer removed: club {ClubId}, member {MemberId}, {Remaining} left",
                    clubId, memberId, remaining);

            // Klienten varnar när klubben blivit utan läsare. Att räkna om här och inte i klienten
            // gör att varningen bygger på serverns härledda svar, inte på en lista som kan vara
            // sekunder gammal.
            return Json(new { success = ok, message, activeViewers = remaining });
        }

        /// <summary>Klubbens egna vapen, maskerade. Klubbadmingrind — klubbvapen är inte personuppgifter.</summary>
        [HttpGet]
        public async Task<IActionResult> GetClubFirearms(int clubId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltig klubb" });
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            try
            {
                var rows = _firearms.GetForScope(FirearmScope.Club(clubId));
                return Json(new
                {
                    success = true,
                    options = new
                    {
                        // ⚠️ Samma källa som medlemsformuläret. Två handskrivna listor hade
                        // glidit isär, och klubbvapnen är licensbelagda på samma sätt.
                        weaponClasses = FirearmWeaponGroups.Options
                            .Select(o => new { id = o.Value, label = o.Label }),
                        vapentyper = HpskSite.Models.ForeningsintygDocument.AllaVapentyper,
                        statuses = FirearmStatus.All,
                        // ⚠️ Ur konstanterna, aldrig ur en lista i vyn — samma regel som
                        // medlemsformuläret. Annars kan klubbformuläret erbjuda ett förbund som
                        // intygets ruta inte känner igen.
                        forbund = HpskSite.Models.ForeningsintygDocument.AllaForbund,
                        disciplines = HpskSite.Models.ActivityDiscipline.All
                            .Select(d => new { id = d, label = HpskSite.Models.ActivityDiscipline.Label(d) }),
                    },
                    firearms = rows.Select(f => new
                    {
                        f.Id, f.Alias, f.WeaponClass, f.Vapentyp, f.AnnanVapentyp,
                        number = f.ClubWeaponNumber, f.IsLoanable,
                        status = string.IsNullOrWhiteSpace(f.Status) ? FirearmStatus.Tillgangligt : f.Status,
                        hasDetails = f.HasProtectedDetails,
                        // Klubbvapen är licensbelagda precis som medlemmarnas, och en licens som
                        // förfaller obemärkt tar vapnet ur bruk. Klartext, så listan kan färga
                        // raden utan att avkryptera något.
                        licenseExpiresOn = f.LicenseExpiresOn?.ToString("yyyy-MM-dd"),
                        daysUntilExpiry = f.LicenseExpiresOn.HasValue
                            ? (int?)(f.LicenseExpiresOn.Value.Date - DateTime.Now.Date).TotalDays
                            : null,
                        federations = f.Federations,
                        disciplines = f.Disciplines,
                    }),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetClubFirearms failed for club {ClubId}", clubId);
                return Json(new { success = false, message = "Kunde inte läsa klubbens vapen" });
            }
        }

        /// <summary>Skapar eller uppdaterar ett KLUBBVAPEN.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveClubFirearm(
            int clubId, int id, string? alias, string? weaponClass, string? vapentyp,
            int? number, bool isLoanable, string? status,
            string? annanVapentyp, string? licenseExpiresOn, string? federations, string? disciplines,
            string? writeDetails = null,
            string? fabrikat = null, string? modell = null, string? kaliber = null,
            string? piplangd = null, string? tillverkningsnummer = null,
            string? licensnummer = null, string? licensdatum = null, string? anteckning = null)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltig klubb" });
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var scope = FirearmScope.Club(clubId);
            var request = new FirearmWriteRequest
            {
                Alias = alias,
                WeaponClass = weaponClass,
                Vapentyp = vapentyp,
                AnnanVapentyp = annanVapentyp,
                AcquisitionStatus = FirearmAcquisitionStatus.Innehas,
                LicenseExpiresOn = ParseDate(licenseExpiresOn),
                ClubWeaponNumber = number,
                IsLoanable = isLoanable,
                Status = status,
                Federations = SplitList(federations),
                Disciplines = SplitList(disciplines),

                // ⚠️ NULL = "rör inte de skyddade uppgifterna", och det är inte samma sak som att
                // spara dem tomma. Formuläret hämtar dem inte automatiskt (en avkryptering ska vara
                // en handling, och den loggas), så en sparning utan hämtning måste lämna dem i fred
                // — annars raderar varje statusändring klubbens licensuppgifter.
                Details = IsTrue(writeDetails)
                    ? new FirearmDetails
                    {
                        Fabrikat = (fabrikat ?? "").Trim(),
                        Modell = (modell ?? "").Trim(),
                        Kaliber = (kaliber ?? "").Trim(),
                        Piplangd = (piplangd ?? "").Trim(),
                        Tillverkningsnummer = (tillverkningsnummer ?? "").Trim(),
                        Licensnummer = (licensnummer ?? "").Trim(),
                        Licensdatum = (licensdatum ?? "").Trim(),
                        Anteckning = (anteckning ?? "").Trim(),
                    }
                    : null,
            };

            if (id > 0)
            {
                // ⚠️ Ägandet prövas mot RADEN. Utan det kunde en klubbadmin skriva över en annan
                // klubbs vapen genom att posta dess id.
                var existing = _firearms.GetById(id);
                if (existing is null) return Json(new { success = false, message = "Vapnet hittades inte" });
                if (existing.Scope != scope)
                    return Json(new { success = false, message = "Vapnet tillhör inte den här klubben" });

                var err = _firearms.Update(id, request);
                return err is null
                    ? Json(new { success = true, message = "Vapnet är uppdaterat." })
                    : Json(new { success = false, message = err });
            }

            var (newId, createError) = _firearms.Create(scope, request);
            return newId > 0
                ? Json(new { success = createError is null, firearmId = newId, message = createError ?? "Vapnet är sparat." })
                : Json(new { success = false, message = createError ?? "Kunde inte spara." });
        }

        /// <summary>
        /// Lämnar ut ett KLUBBVAPENS skyddade uppgifter till klubbens formulär.
        ///
        /// <para><b>⚠️ Går genom <see cref="FirearmService.RevealDetailsAsync"/>, inte förbi den.</b>
        /// Där sitter grinden (<c>ResolveClubWeaponAccessAsync</c> = klubbadmin för vapnets klubb)
        /// OCH loggskrivningen, i samma metod. En controller som avkrypterade själv skulle kringgå
        /// båda.</para>
        ///
        /// <para><b>Klubbvapen kräver ingen särskild behörighet utöver klubbadmin</b> — de är
        /// föreningens egendom, inte personuppgifter. Det är därför den här grinden är bredare än
        /// medlemsvapnens, som kräver föreningsintygsansvarig med aktivt styrelseuppdrag.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevealClubFirearmDetails(int clubId, int firearmId)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltig klubb" });
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var row = _firearms.GetById(firearmId);
            if (row is null) return Json(new { success = false, message = "Vapnet hittades inte" });

            // ⚠️ Prövas mot RADEN. Utan det kunde en klubbadmin läsa en annan klubbs vapen — eller
            // en MEDLEMS vapen — genom att posta sitt eget clubId och ett främmande firearmId.
            if (row.Scope != FirearmScope.Club(clubId))
                return Json(new { success = false, message = "Vapnet tillhör inte den här klubben" });

            var (details, error) = await _firearms.RevealDetailsAsync(firearmId, "Klubbens vapenregister");
            if (error != null) return Json(new { success = false, message = error });

            return Json(new
            {
                success = true,
                details = new
                {
                    fabrikat = details?.Fabrikat ?? "",
                    modell = details?.Modell ?? "",
                    kaliber = details?.Kaliber ?? "",
                    piplangd = details?.Piplangd ?? "",
                    tillverkningsnummer = details?.Tillverkningsnummer ?? "",
                    licensnummer = details?.Licensnummer ?? "",
                    licensdatum = details?.Licensdatum ?? "",
                    anteckning = details?.Anteckning ?? "",
                },
            });
        }

        private static DateTime? ParseDate(string? v) =>
            DateTime.TryParse((v ?? "").Trim(), out var d) ? d.Date : null;

        /// <summary>
        /// Sanningsvärdet för <c>writeDetails</c>.
        ///
        /// <para><b>⚠️ PARAMETERN ÄR EN STRÄNG, INTE EN <c>bool</c>, och det är inte slarv.</b>
        /// ASP.NET Cores bool-bindning godtar bara <c>"true"</c>/<c>"false"</c> — <c>"1"</c>
        /// konverteras INTE, utan faller tyst tillbaka på default. Klubbformuläret skickade
        /// <c>"1"</c> (samma form som medlemsformuläret), parametern var deklarerad <c>bool</c>,
        /// och följden var att de krypterade uppgifterna aldrig skrevs medan sparningen
        /// rapporterade <c>success: true</c>. Hittat av sviten 2026-09-02.</para>
        ///
        /// <para>Godtar <c>"1"</c>, <c>"true"</c> och <c>"on"</c> så ingen framtida anropare fastnar
        /// i fällan från något av hållen.</para>
        /// </summary>
        private static bool IsTrue(string? v)
        {
            var t = (v ?? "").Trim();
            return t == "1"
                || t.Equals("true", StringComparison.OrdinalIgnoreCase)
                || t.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Kommaseparerad lista → <c>List</c>.
        ///
        /// <para><b>⚠️ RELATIONERNA SKRIVS ALLTID OM HELT.</b> <c>FirearmService.Update</c> gör
        /// <c>DELETE</c> + <c>WriteRelations</c>, och <c>WriteRelations</c> behandlar <c>null</c>
        /// som en tom lista — så <c>null</c> betyder <b>RENSA</b>, aldrig "rör inte". Det är
        /// motsatsen till hur <c>Details</c> fungerar, och skillnaden är lätt att läsa fel.</para>
        ///
        /// <para>Följden: <b>klubbformuläret måste skicka BÅDA fälten vid varje sparning</b>, även
        /// när inget är valt. Gör det inte det tappar vapnet sina förbund och grenar vid nästa
        /// statusändring — och förbundet är vad blankettens "antal vapen sedan tidigare" räknas i.
        /// (Fram till 2026-09-02 skickade klubbytan dem aldrig, vilket inte märktes eftersom den
        /// heller aldrig kunde sätta dem.)</para>
        /// </summary>
        private static List<string> SplitList(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .ToList();

        /// <summary>Gömmer ett klubbvapen. Raderar aldrig.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveClubFirearm(int clubId, int firearmId)
        {
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var existing = _firearms.GetById(firearmId);
            if (existing is null) return Json(new { success = false, message = "Vapnet hittades inte" });
            if (existing.Scope != FirearmScope.Club(clubId))
                return Json(new { success = false, message = "Vapnet tillhör inte den här klubben" });

            var err = _firearms.Deactivate(firearmId);
            return err is null
                ? Json(new { success = true, message = "Vapnet är borttaget ur listan." })
                : Json(new { success = false, message = err });
        }

        /// <summary>
        /// Klubbens inkorg för föreningsintygsförfrågningar.
        ///
        /// <para><b>⚠️ Bara klartextfälten följer med.</b> Vapnets skyddade uppgifter läses genom
        /// <c>Firearm/RevealDetails</c> — alltså genom grinden och med en loggrad — inte här.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetIntygRequests(int clubId, bool openOnly = false)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltig klubb" });
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var rows = _requests.GetForClub(clubId, openOnly);

            return Json(new
            {
                success = true,
                // Namnger vem som KAN läsa vapenuppgifterna. Kan den inloggade inte det ska inkorgen
                // säga vem som kan, i stället för att visa en knapp som nekas.
                viewerNames = _firearmAuth.GetViewers(clubId).Where(v => !v.IsDormant).Select(v => v.Name),
                requests = rows.Select(r => new
                {
                    r.Id, r.MemberId, r.MemberName, r.FirearmId, r.FirearmAlias,
                    r.FirearmWeaponClass, r.FirearmVapentyp,
                    kind = r.Kind, kindLabel = r.KindLabel,
                    r.Forbund, r.VapengruppSkytteform, r.MemberMessage,
                    status = r.Status, statusLabel = r.StatusLabel, r.IsOpen,
                    createdAt = r.CreatedAt.ToString("yyyy-MM-dd"),
                    r.HandlerNote,
                }),
            });
        }

        /// <summary>Klubbens statusändring på en förfrågan.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetIntygRequestStatus(int clubId, int requestId, string status, string? note)
        {
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var req = _requests.GetById(requestId);
            if (req is null) return Json(new { success = false, message = "Förfrågan hittades inte" });
            if (req.ClubId != clubId)
                return Json(new { success = false, message = "Förfrågan tillhör inte den här klubben" });

            var actor = await CurrentMemberIdAsync();
            var err = _requests.SetStatus(requestId, status, actor, note);
            return err is null
                ? Json(new { success = true, message = "Status ändrad.", openCount = _requests.CountOpenForClub(clubId) })
                : Json(new { success = false, message = err });
        }

        /// <summary>
        /// Får den inloggade hantera klubbens lånevapensbokningar — utlämning och återlämning?
        ///
        /// <para><b>Klubbadmin ELLER skjutledare.</b> Ingen ny behörighet: utlämningen sker på banan,
        /// och skjutledaren är den som faktiskt står där. En egen "lånevapenansvarig" hade gett
        /// mindre än vad de rollerna redan har, och dess felläge är en tyst utelåsning på
        /// tävlingsdagen.</para>
        /// </summary>
        private async Task<bool> CanHandleBookingsAsync(int clubId) =>
            await _adminAuth.IsClubAdminForClub(clubId)
            || await _adminAuth.IsSkjutledareForClub(clubId);

        /// <summary>Klubbens bokningar. Aktiva först — det är arbetsordningen vid disken.</summary>
        [HttpGet]
        public async Task<IActionResult> GetClubBookings(int clubId, bool activeOnly = false)
        {
            if (clubId <= 0) return Json(new { success = false, message = "Ogiltig klubb" });
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var rows = _bookings.GetForClub(clubId, activeOnly);
            return Json(new
            {
                success = true,
                activeCount = _bookings.CountActiveForClub(clubId),
                bookings = rows.Select(Project),
            });
        }

        /// <summary>Registrerar utlämning eller återlämning, eller avbokar åt medlemmen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBookingState(
            int clubId, int bookingId, string action, string? reason, int firearmId = 0)
        {
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var b = _bookings.GetById(bookingId);
            if (b is null) return Json(new { success = false, message = "Bokningen hittades inte" });
            // ⚠️ Bokningen måste tillhöra DEN här klubben. Utan kontrollen kunde en klubbadmin
            // hantera en annan klubbs utlämningar genom att posta dess boknings-id.
            if (b.ClubId != clubId)
                return Json(new { success = false, message = "Bokningen tillhör inte den här klubben" });

            var actor = await CurrentMemberIdAsync();
            var error = (action ?? "").Trim() switch
            {
                // ⚠️ Utlämningen bär VILKET vapen som gick ut. Skickas inget faller vi tillbaka
                // på önskemålet — men bara om det finns ett; en platsbokning utan vapen måste
                // få ett angivet, annars vet registret inte vad som är ute.
                "handout" => _bookings.MarkHandedOut(
                    bookingId, actor, firearmId > 0 ? firearmId : (b.FirearmId ?? 0)),
                "return" => _bookings.MarkReturned(bookingId, actor),
                "cancel" => _bookings.Cancel(bookingId, actor, actorIsClubStaff: true, reason),
                _ => "Okänd åtgärd.",
            };

            return error is null
                ? Json(new { success = true, activeCount = _bookings.CountActiveForClub(clubId) })
                : Json(new { success = false, message = error });
        }

        // ── Valvet ───────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Kvällens lån för ett tillfälle, plus de vapen som är lediga då.
        ///
        /// <para><b>⚠️ Den här ytan är byggd för en teknikrädd 74-åring med sex nybörjare bakom
        /// sig.</b> Den ska besvara en fråga — <em>hur många vapen tar jag ut, och till vem</em> —
        /// och kräva noll inskrivning. Allt annat hör någon annanstans.</para>
        ///
        /// <para><b>⚠️ Utan tillfälle svarar den för DAGEN.</b> Vapenansvarig ska inte behöva välja
        /// något för att se kvällen; ett tomt val vore en fråga till precis den person som helst
        /// inte vill svara på frågor i en app.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetVaultBoard(
            int clubId, string? occasionKind = null, int occasionId = 0, string? day = null)
        {
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var date = DateTime.TryParse((day ?? "").Trim(), out var d) ? d.Date : DateTime.Now.Date;
            var from = date;
            var to = date.AddDays(1).AddSeconds(-1);

            var loans = string.IsNullOrWhiteSpace(occasionKind)
                ? _bookings.GetForClub(clubId)
                    .Where(b => b.FromTime.Date <= date && b.ToTime.Date >= date)
                    .ToList()
                : _bookings.GetForOccasion(clubId, occasionKind, occasionId);

            var free = _bookings.AvailableInWindow(clubId, from, to);
            var loanable = _firearms.CountLoanable(clubId);

            return Json(new
            {
                success = true,
                day = date.ToString("yyyy-MM-dd"),
                loanable,
                // Antalet att ta ut ur valvet. Det är rubriken vapenansvarig läser, och den ska
                // stå färdigräknad — inte som en lista han själv ska räkna.
                toHandOut = loans.Count(b => b.Status == FirearmBookingStatus.Reserverad),
                out_ = loans.Count(b => b.IsOut),
                loans = loans.Select(Project),
                free = free.Select(f => new { f.Id, f.Alias, number = f.ClubWeaponNumber, f.WeaponClass }),
            });
        }

        /// <summary>
        /// Lämnar ut ett vapen ur valvet. <paramref name="firearmId"/> är det vapen som FAKTISKT
        /// går ut, oavsett vad medlemmen önskade.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> HandOutFromVault(int clubId, int bookingId, int firearmId)
        {
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var b = _bookings.GetById(bookingId);
            if (b is null) return Json(new { success = false, message = "Bokningen hittades inte" });
            if (b.ClubId != clubId)
                return Json(new { success = false, message = "Bokningen tillhör inte den här klubben" });

            var actor = await CurrentMemberIdAsync();
            var error = _bookings.MarkHandedOut(bookingId, actor, firearmId);
            return error is null
                ? Json(new { success = true, message = "Utlämnat." })
                : Json(new { success = false, message = error });
        }

        /// <summary>
        /// Stänger kvällen — allt aktivt på tillfället blir återlämnat i ett tryck.
        ///
        /// <para><b>⚠️ Det här är knappen som avgör om registret överlever.</b> Ingen skannar när
        /// de ska hem, och sex tryck klockan nio är exakt när registerföringen upphör. Den knyts
        /// till en ritual vapenansvarig redan har — att låsa valvet — och är fysiskt sann just då.</para>
        ///
        /// <para><paramml name="keepIds"/> är de lån som ligger kvar. Undantaget får kosta ett tryck.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CloseVaultEvening(
            int clubId, string? occasionKind, int occasionId, string? keepIds, string? day = null)
        {
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var actor = await CurrentMemberIdAsync();
            var keep = (keepIds ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(v => int.TryParse(v, out var i) ? i : 0)
                .Where(i => i > 0)
                .ToHashSet();

            // Utan tillfälle stänger vi dagens lån, ett i taget — samma regel, men urvalet kommer
            // från dagen i stället för från en nod.
            if (string.IsNullOrWhiteSpace(occasionKind))
            {
                var date = DateTime.TryParse((day ?? "").Trim(), out var dd) ? dd.Date : DateTime.Now.Date;
                var rows = _bookings.GetForClub(clubId, activeOnly: true)
                    .Where(b => b.FromTime.Date <= date && b.ToTime.Date >= date
                                && !b.LeavesTheClub && !keep.Contains(b.Id))
                    .ToList();

                var n = 0;
                foreach (var b in rows)
                    if (_bookings.MarkReturned(b.Id, actor) is null) n++;

                return Json(new { success = true, closed = n, message = Closed(n) });
            }

            var (closed, error) = _bookings.ReturnAllForOccasion(
                clubId, occasionKind, occasionId, actor, keep);

            return error is null
                ? Json(new { success = true, closed, message = Closed(closed) })
                : Json(new { success = false, message = error });
        }

        /// <summary>
        /// EN projektion av ett lån, delad av klubbens lista och valvskärmen.
        ///
        /// <para><b>⚠️ Två handskrivna projektioner av samma rad glider isär</b> — det är samma
        /// lärdom som tävlingslistan, som renderades på tre ytor tills de sa emot varandra. Lägg
        /// till ett fält HÄR, inte på en yta.</para>
        ///
        /// <para><b>⚠️ Önskat och tilldelat är SKILDA fält i svaret.</b> Vapenansvarig måste kunna
        /// se "önskade nr 7, fick nr 4" — och den som skjutit in nr 7 mot sig själv är precis den
        /// som märker om vi tappar skillnaden.</para>
        /// </summary>
        private static object Project(FirearmBooking b) => new
        {
            b.Id,
            b.MemberId, b.MemberName,

            // Det som gäller nu: tilldelat om det finns, annars önskat.
            firearmId = b.EffectiveFirearmId ?? 0,
            b.FirearmAlias,
            number = b.ClubWeaponNumber,

            // Önskemålet, separat. null = medlemmen tog vilket som helst.
            wishedFirearmId = b.FirearmId,
            wishedNumber = b.WishedWeaponNumber,
            b.WishedAlias,
            b.WantsSpecificFirearm,
            b.AssignmentDiffersFromWish,
            weaponClass = b.WeaponClass,

            from = b.FromTime.ToString("yyyy-MM-dd HH:mm"),
            to = b.ToTime.ToString("yyyy-MM-dd HH:mm"),
            status = b.Status, statusLabel = b.StatusLabel,
            occasion = b.OccasionDisplay, occasionKind = b.OccasionKind, b.OccasionId,
            b.Note, b.IsActive, b.IsOut,
            source = b.Source, sourceLabel = FirearmBookingSource.Label(b.Source),
            b.HandedOutBySelf,
            b.LeavesTheClub, b.AwaitsEscort, b.EscortMemberId, b.EscortName,
            handedOutAt = b.HandedOutAt?.ToString("yyyy-MM-dd HH:mm"),
            returnedAt = b.ReturnedAt?.ToString("yyyy-MM-dd HH:mm"),
        };

        /// <summary>Tolkar en tidpunkt. Tomt eller obegripligt ger <c>default</c>, som
        /// <c>FirearmBookingWindow</c> normaliserar till hela dagen.</summary>
        private static DateTime ParseWhen(string? value) =>
            DateTime.TryParse((value ?? "").Trim(), out var d) ? d : default;

        private static string Closed(int n) => n switch
        {
            0 => "Inget att återlämna.",
            1 => "Ett vapen är återlämnat.",
            _ => $"{n} vapen är återlämnade.",
        };

        /// <summary>
        /// Lägger in ett lån på plats, för den som dök upp utan att ha bokat.
        ///
        /// <para><b>⚠️ Det normala fallet på en träningskväll.</b> Nekar vi den som inte bokat har
        /// vi byggt precis den grind som kommer att kringgås — vapenansvarig lämnar ut ändå, och då
        /// ljuger registret. Lånet skapas och lämnas ut i samma handling.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> WalkInLoan(
            int clubId, int memberId, int firearmId, string? occasionKind, int occasionId)
        {
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });
            if (memberId <= 0) return Json(new { success = false, message = "Välj vem som lånar" });

            var kind = string.IsNullOrWhiteSpace(occasionKind)
                ? FirearmOccasionKind.Fritt : occasionKind!.Trim();

            var (bookingId, error) = _bookings.Create(new FirearmBookingRequest
            {
                MemberId = memberId,
                ClubId = clubId,
                FirearmId = firearmId > 0 ? firearmId : null,
                OccasionKind = kind,
                OccasionId = FirearmOccasionKind.HasNodeId(kind) ? occasionId : 0,
                From = DateTime.Now.Date,
                To = DateTime.Now.Date.AddDays(1).AddSeconds(-1),
                Source = FirearmBookingSource.Valv,
            });
            if (error is not null) return Json(new { success = false, message = error });

            var actor = await CurrentMemberIdAsync();
            var handoutError = _bookings.MarkHandedOut(bookingId, actor, firearmId);

            // ⚠️ Lånet FINNS även om utlämningen inte gick igenom. Att rapportera det som ett
            // misslyckande skulle få vapenansvarig att göra om det och skapa ett andra lån.
            return Json(new
            {
                success = true,
                bookingId,
                message = handoutError is null
                    ? "Utlämnat."
                    : "Lånet är inlagt, men utlämningen kunde inte registreras: " + handoutError,
            });
        }

        /// <summary>
        /// Tilldelar hela träningsgruppen lånevapen för ett tillfälle — kurstilldelning.
        ///
        /// <para><b>⚠️ INSTRUKTÖREN BOKAR, INTE NYBÖRJAREN.</b> En kurs med tio deltagare är
        /// inte tio personer som råkade vilja låna samma kväll: klubben har redan bestämt att de
        /// ska skjuta, och att kräva att var och en själv hittar bokningssidan är att flytta ett
        /// arbete instruktören gör en gång till tio personer som gör det fel. De som inte bokar
        /// dyker upp ändå, och då står vapnen reserverade för fel personer.</para>
        ///
        /// <para><b>⚠️ Tilldelningen går FÖRBI klubbens horisont.</b> Källan
        /// <c>Tilldelad</c> undantas i <c>FirearmBookingService.Create</c>, av det enkla skälet
        /// att en kursomgång planeras månader i förväg medan horisonten finns för att hindra
        /// enskilda från att lägga beslag på vapen hela säsongen. Samma regel på båda vore att
        /// göra kursplanering omöjlig för att skydda mot något kursen inte gör.</para>
        ///
        /// <para><b>⚠️ PLATSBOKNINGAR, inga namngivna vapen.</b> Vilket vapen var och en får
        /// avgörs i valvet av den som lämnar ut — det är där kunskapen finns om vem som är stor
        /// nog för vad. En förhandstilldelning av nummer hade bara blivit något att göra om.</para>
        ///
        /// <para>Svaret säger per person vad som hände. En sammanräkning duger inte: instruktören
        /// måste veta VEM som blev utan vapen, för det är den personen han behöver prata med.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AssignLoanWeaponsToGroup(
            int clubId, int trainingGroupId, string? occasionKind, int occasionId,
            string? occasionLabel, string? from, string? to)
        {
            if (!await CanHandleBookingsAsync(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var group = _trainingGroups.GetTrainingGroup(trainingGroupId);
            if (group is null)
                return Json(new { success = false, message = "Träningsgruppen hittades inte" });

            // ⚠️ Gruppen måste tillhöra den klubb behörigheten gäller för. Utan kontrollen kunde en
            // klubbadmin tilldela en ANNAN klubbs grupp sina egna vapen — och lånen hade hamnat på
            // personer hen inte har med att göra.
            if (_trainingGroups.GetTrainingGroupClubId(trainingGroupId) != clubId)
                return Json(new { success = false, message = "Gruppen tillhör inte den här klubben" });

            var kind = string.IsNullOrWhiteSpace(occasionKind)
                ? FirearmOccasionKind.Fritt : occasionKind!.Trim();

            var memberIds = _trainingGroups.GetGroupMemberIds(trainingGroupId);
            if (memberIds.Count == 0)
                return Json(new { success = false, message = "Gruppen har inga medlemmar" });

            var names = new Dictionary<int, string>();
            foreach (var id in memberIds)
            {
                var m = _memberService.GetById(id);
                names[id] = m is null
                    ? $"Medlem {id}"
                    : $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            }

            var results = new List<object>();
            int created = 0, skipped = 0;

            foreach (var memberId in memberIds)
            {
                var (bookingId, error) = _bookings.Create(new FirearmBookingRequest
                {
                    MemberId = memberId,
                    ClubId = clubId,
                    FirearmId = null,               // platsbokning — valvet avgör vilket vapen
                    OccasionKind = kind,
                    OccasionId = FirearmOccasionKind.HasNodeId(kind) ? occasionId : 0,
                    OccasionLabel = string.IsNullOrWhiteSpace(occasionLabel)
                        ? group.Name : occasionLabel,
                    From = ParseWhen(from),
                    To = ParseWhen(to),
                    Source = FirearmBookingSource.Tilldelad,
                });

                if (error is null) created++; else skipped++;
                results.Add(new
                {
                    memberId,
                    name = names.TryGetValue(memberId, out var n) ? n : $"Medlem {memberId}",
                    ok = error is null,
                    bookingId,
                    message = error,
                });
            }

            return Json(new
            {
                success = created > 0,
                created,
                skipped,
                message = created == 0
                    ? "Ingen kunde tilldelas ett vapen."
                    : skipped == 0
                        ? (created == 1 ? "En person har fått ett lånevapen." : $"{created} personer har fått lånevapen.")
                        : $"{created} fick lånevapen, {skipped} kunde inte tilldelas.",
                results,
            });
        }

        // ── Klubbens lånevapeninställningar ──────────────────────────────────────────────────────

        /// <summary>
        /// Klubbens regler för lånevapen, plus om egenskaperna alls finns på doctypen.
        ///
        /// <para><c>propertyExists</c> är inte en detalj: utan den kan gränssnittet inte skilja
        /// <em>"klubben har valt av"</em> från <em>"egenskapen finns inte"</em>, och switchen skulle
        /// se ut att fungera medan varje sparning tyst rann ut i sanden.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetLoanWeaponSettings(int clubId)
        {
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var r = _clubRules.For(clubId);
            return Json(new
            {
                success = true,
                allowExternal = r.AllowExternal,
                horizonDays = r.HorizonDays,
                allowExternalPropertyExists = r.AllowExternalPropertyExists,
                horizonPropertyExists = r.HorizonPropertyExists,
                allowExternalProperty = LoanWeaponClubRules.AllowExternalProperty,
                horizonProperty = LoanWeaponClubRules.HorizonProperty,
                loanable = _firearms.CountLoanable(clubId),
            });
        }

        /// <summary>
        /// Sparar klubbens regler.
        ///
        /// <para><b>⚠️ VÄGRAR när egenskapen saknas, i stället för att no-op:a.</b>
        /// <c>SetValue</c> på en saknad egenskap är tyst ignorerad — switchen hade sett ut att
        /// spara och återgått vid nästa laddning, vilket är den värsta sortens fel: ett som ser ut
        /// som att det fungerade. Meddelandet namnger egenskapen så en administratör vet vad hen
        /// ska lägga till.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveLoanWeaponSettings(
            int clubId, bool allowExternal, int horizonDays)
        {
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var club = _contentService.GetById(clubId);
            if (club is null) return Json(new { success = false, message = "Klubben hittades inte" });

            var missing = new List<string>();
            if (!club.HasProperty(LoanWeaponClubRules.AllowExternalProperty))
                missing.Add(LoanWeaponClubRules.AllowExternalProperty);
            if (!club.HasProperty(LoanWeaponClubRules.HorizonProperty))
                missing.Add(LoanWeaponClubRules.HorizonProperty);

            if (missing.Count > 0)
            {
                return Json(new
                {
                    success = false,
                    message = "Egenskaperna " + string.Join(" och ", missing.Select(m => $"'{m}'")) +
                              " saknas på klubbtypen i Umbraco. Be en administratör lägga till dem, " +
                              "annars kan inställningen inte sparas.",
                });
            }

            club.SetValue(LoanWeaponClubRules.AllowExternalProperty, allowExternal);
            club.SetValue(LoanWeaponClubRules.HorizonProperty, Math.Clamp(horizonDays, 0, 365));
            _contentService.Save(club);

            _logger.LogInformation(
                "Lånevapenregler sparade för klubb {ClubId}: extern {Ext}, horisont {Days} dagar.",
                clubId, allowExternal, horizonDays);

            return Json(new { success = true, message = "Inställningarna är sparade." });
        }

        // ── Vapenetiketterna ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// QR-koden för ETT vapens etikett, som PNG.
        ///
        /// <para><b>⚠️ Etiketten sitter på VAPNET, inte på hyllplatsen</b> (Stefans beslut
        /// 2026-09-02, och han har rätt): ett vapen som läggs tillbaka på fel plats gör en
        /// hylletikett direkt felaktig, medan vapnet är identiteten. Hyllplatsen är föränderligt
        /// tillstånd som ingen underhåller.</para>
        ///
        /// <para><b>⚠️ Token är INTE tidsbegränsad.</b> Etiketten är laminerad och sitter kvar i
        /// åratal — en kortlivad token hade gjort den obrukbar vid första skanningen. Kontrollen
        /// görs av bokningsfönstret: ingen bokning i dag, ingen utcheckning.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetFirearmLabelQr(int clubId, int firearmId, int size = 10)
        {
            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Content("Åtkomst nekad");

            var f = _firearms.GetById(firearmId);
            if (f is null || f.Scope != FirearmScope.Club(clubId))
                return Content("Vapnet hittades inte");

            var url = LabelUrl(firearmId);
            var png = QrPng(url, Math.Clamp(size, 4, 20));
            return png is null ? Content("Kunde inte skapa QR-kod") : File(png, "image/png");
        }

        /// <summary>Etikettens adress. Absolut, för den ska skannas från papper.</summary>
        private string LabelUrl(int firearmId)
        {
            var token = _labelProtector.Protect($"firearm:{firearmId}");
            var req = HttpContext.Request;
            return $"{req.Scheme}://{req.Host}/lanevapen/skanna?t={Uri.EscapeDataString(token)}";
        }

        private static byte[]? QrPng(string url, int pixelsPerModule)
        {
            try
            {
                var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);
                using var img = qr.GetGraphic(
                    pixelsPerModule: pixelsPerModule,
                    darkColor: SixLabors.ImageSharp.Color.Black,
                    lightColor: SixLabors.ImageSharp.Color.White,
                    drawQuietZones: true);
                using var ms = new System.IO.MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return ms.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }

        /// <summary>
        /// Vad som händer med vapenbehörigheten om den här styrelsemedlemmen tas bort. Läses av
        /// Styrelsen-fliken FÖRE borttagningen — efteråt är den härledda behörigheten redan borta och
        /// det går inte längre att se att den fanns.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRemovalImpact(int clubId, int memberId)
        {
            if (clubId <= 0 || memberId <= 0)
                return Json(new { success = false, message = "Ogiltig begäran" });

            if (!await _adminAuth.IsClubAdminForClub(clubId))
                return Json(new { success = false, message = "Åtkomst nekad" });

            var isViewer = _firearmAuth.IsFirearmViewerForClub(memberId, clubId);
            var activeNow = _firearmAuth.CountActiveViewers(clubId);

            return Json(new
            {
                success = true,
                isFirearmViewer = isViewer,
                // Blir klubben utan läsare av just den här borttagningen? Det är den enda siffran
                // som motiverar en varning — att en av tre tas bort är ingen händelse.
                wouldLeaveClubWithout = FirearmAccessRules.RemovalWouldLeaveClubWithoutViewer(isViewer, activeNow),
                activeViewers = activeNow,
            });
        }
    }

}
