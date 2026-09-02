using HpskSite.Services;
using HpskSite.Models.Firearms;
using HpskSite.Services.Firearms;
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
            ILogger<FirearmAdminController> logger)
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
                bookings = rows.Select(b => new
                {
                    b.Id, b.FirearmId, b.FirearmAlias, number = b.ClubWeaponNumber,
                    b.MemberId, b.MemberName,
                    from = b.FromTime.ToString("yyyy-MM-dd HH:mm"),
                    to = b.ToTime.ToString("yyyy-MM-dd HH:mm"),
                    status = b.Status, statusLabel = b.StatusLabel,
                    occasion = b.OccasionLabel, b.Note, b.IsActive, b.IsOut,
                    handedOutAt = b.HandedOutAt?.ToString("yyyy-MM-dd HH:mm"),
                    returnedAt = b.ReturnedAt?.ToString("yyyy-MM-dd HH:mm"),
                }),
            });
        }

        /// <summary>Registrerar utlämning eller återlämning, eller avbokar åt medlemmen.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetBookingState(int clubId, int bookingId, string action, string? reason)
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
                "handout" => _bookings.MarkHandedOut(bookingId, actor),
                "return" => _bookings.MarkReturned(bookingId, actor),
                "cancel" => _bookings.Cancel(bookingId, actor, actorIsClubStaff: true, reason),
                _ => "Okänd åtgärd.",
            };

            return error is null
                ? Json(new { success = true, activeCount = _bookings.CountActiveForClub(clubId) })
                : Json(new { success = false, message = error });
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
