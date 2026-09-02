using HpskSite.Models;
using HpskSite.Models.Firearms;
using HpskSite.Services;
using HpskSite.Services.Firearms;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Security;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Web.Website.Controllers;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Medlemmens eget vapenregister.
    ///
    /// <para><b>⚠️ Ingen endpoint här rör <c>FirearmProtector</c> direkt.</b> Allt går via
    /// <see cref="FirearmService"/>, där behörigheten prövas och läsningen loggas i samma metod som
    /// lämnar ut klartexten. En controller som avkrypterade själv skulle kunna kringgå båda.</para>
    ///
    /// <para><b>⚠️ Ägaren tas ALLTID från den inloggade medlemmen, aldrig från begäran.</b> Kunde
    /// klienten skicka ett <c>memberId</c> vore registret ett verktyg för att skriva vapen på andra
    /// personer — och för att läsa dem.</para>
    /// </summary>
    public class FirearmController : SurfaceController
    {
        private readonly FirearmService _firearms;
        private readonly FirearmAccessLogService _accessLog;
        private readonly FirearmAuthorizationService _auth;
        private readonly FirearmUsageService _usage;
        private readonly ForeningsintygRequestService _requests;
        private readonly FirearmBookingService _bookings;
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly MemberClubService _memberClubs;
        private readonly ClubService _clubService;
        private readonly ILogger<FirearmController> _logger;

        public FirearmController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            FirearmService firearms,
            FirearmAccessLogService accessLog,
            FirearmAuthorizationService auth,
            FirearmUsageService usage,
            ForeningsintygRequestService requests,
            FirearmBookingService bookings,
            IMemberManager memberManager,
            IMemberService memberService,
            MemberClubService memberClubs,
            ClubService clubService,
            ILogger<FirearmController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _firearms = firearms;
            _accessLog = accessLog;
            _auth = auth;
            _usage = usage;
            _requests = requests;
            _bookings = bookings;
            _memberManager = memberManager;
            _memberService = memberService;
            _memberClubs = memberClubs;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>
        /// Medlemmens vapen, <b>maskerade</b> — klartextkolumner och relationer, men inga skyddade
        /// uppgifter. Bär också förtroenderadens innehåll: vem som kan läsa och när det senast
        /// skedde.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyFirearms()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            try
            {
                var scope = FirearmScope.Member(memberId);
                var rows = _firearms.GetForScope(scope);
                var usageCounts = _usage.CountsForMember(memberId);
                var myRequests = _requests.GetForMember(memberId);

                // Förtroenderaden: namnge den som kan läsa, och när det senast hände. Det är priset
                // för att löftet skrevs om — medlemmen har inte längre ensamrätt, och transparens är
                // det enda vi kan ge tillbaka.
                var viewers = new List<object>();
                var member = _memberService.GetById(memberId);
                foreach (var clubId in _memberClubs.GetAllClubIds(member))
                {
                    // Klubbnamnet följer med: raden namnger vem som kan läsa, och "Anna Svensson
                    // (Vetlanda PK)" säger något helt annat än "Anna Svensson" för en medlem som
                    // tillhör två klubbar. Samma lista driver dessutom klubbväljaren i
                    // intygsförfrågan, som behöver namnet.
                    var clubName = _clubService.GetClubNameById(clubId);
                    foreach (var v in _auth.GetViewers(clubId).Where(x => !x.IsDormant))
                        viewers.Add(new { v.MemberId, v.Name, clubId, clubName });
                }

                return Json(new
                {
                    success = true,
                    canWrite = true,
                    viewers,
                    lastForeignRead = _accessLog.LastForeignReadFor(memberId)?.ToString("yyyy-MM-dd HH:mm"),
                    // Valmängderna kommer ur konstanterna, aldrig ur en lista i vyn — annars kan
                    // formuläret erbjuda ett förbund intygets ruta inte känner igen.
                    options = new
                    {
                        weaponClasses = Enum.GetNames<WeaponClass>(),
                        vapentyper = ForeningsintygDocument.AllaVapentyper,
                        forbund = ForeningsintygDocument.AllaForbund,
                        statuses = FirearmAcquisitionStatus.All
                            .Select(s => new { id = s, label = FirearmAcquisitionStatus.Label(s) }),
                        disciplines = ActivityDiscipline.All
                            .Select(d => new { id = d, label = ActivityDiscipline.Label(d) }),
                    },
                    firearms = rows.Select(f => Project(f, usageCounts, myRequests)),
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetMyFirearms failed for member {MemberId}", memberId);
                return Json(new { success = false, message = "Kunde inte läsa vapenregistret." });
            }
        }

        /// <summary>
        /// Lämnar ut ett vapens SKYDDADE uppgifter. Behörighet och loggning sker i tjänsten.
        ///
        /// <para><b>POST och inte GET, med flit.</b> Anropet har en sidoeffekt — det skriver en rad i
        /// läsloggen — och en GET som loggar skulle kunna utlösas av en förhandsvisning eller en
        /// prefetch, alltså en läsning medlemmen aldrig gjorde.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RevealDetails(int firearmId)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (details, error) = await _firearms.RevealDetailsAsync(firearmId);
            if (error is not null) return Json(new { success = false, message = error });

            return Json(new { success = true, details });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SaveFirearm([FromForm] FirearmFormRequest form)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            if (form is null) return Json(new { success = false, message = "Ogiltig begäran." });

            var scope = FirearmScope.Member(memberId);
            var request = form.ToWriteRequest();

            try
            {
                if (form.Id > 0)
                {
                    // ⚠️ Äganderätten kontrolleras mot RADEN, inte mot begäran. Utan det kunde en
                    // inloggad medlem skriva över någon annans vapen genom att posta dess id.
                    var existing = _firearms.GetById(form.Id);
                    if (existing is null)
                        return Json(new { success = false, message = "Vapnet hittades inte." });
                    if (existing.Scope != scope)
                        return Json(new { success = false, message = "Du kan bara ändra dina egna vapen." });

                    var error = _firearms.Update(form.Id, request);
                    if (error is not null) return Json(new { success = false, message = error });

                    return Json(new { success = true, firearmId = form.Id, message = "Vapnet är uppdaterat." });
                }

                var (newId, createError) = _firearms.Create(scope, request);
                if (newId <= 0) return Json(new { success = false, message = createError ?? "Kunde inte spara." });

                // Ett id OCH ett fel: raden finns men krypteringen fallerade. Det ska sägas, inte
                // rapporteras som en lyckad sparning.
                return Json(new
                {
                    success = createError is null,
                    firearmId = newId,
                    message = createError ?? "Vapnet är sparat.",
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SaveFirearm failed for member {MemberId}", memberId);
                return Json(new { success = false, message = "Ett fel uppstod vid sparandet." });
            }
        }

        /// <summary>Gömmer ett vapen. Raderar aldrig — se <see cref="FirearmService.Deactivate"/>.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RemoveFirearm(int firearmId)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var existing = _firearms.GetById(firearmId);
            if (existing is null) return Json(new { success = false, message = "Vapnet hittades inte." });
            if (existing.Scope != FirearmScope.Member(memberId))
                return Json(new { success = false, message = "Du kan bara ta bort dina egna vapen." });

            var error = _firearms.Deactivate(firearmId);
            return error is null
                ? Json(new { success = true, message = "Vapnet är borttaget ur listan." })
                : Json(new { success = false, message = error });
        }

        /// <summary>Medlemmens egen läslogg — "vem har läst mina uppgifter".</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyAccessLog(bool includeOwn = false)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var rows = _accessLog.GetForSubject(memberId, includeOwn);
            return Json(new
            {
                success = true,
                entries = rows.Select(r => new
                {
                    at = r.OccurredAt.ToString("yyyy-MM-dd HH:mm"),
                    reader = r.ReaderName ?? $"Medlem {r.ReaderMemberId}",
                    club = r.ReaderClubName,
                    reason = r.ReasonLabel,
                    isSelf = r.ReaderMemberId == memberId,
                    r.FirearmId,
                }),
            });
        }

        /// <summary>
        /// Anger vilket vapen som användes vid ett tillfälle. <c>firearmId = 0</c> tar bort taggningen.
        ///
        /// <para><b>⚠️ Det här är skyttens EGEN anteckning, inte ett intygspåstående.</b> Kravet
        /// "tränat två gånger med vapnet" är struket — en förstagångssökande tränade med lånevapen,
        /// och den historiken kan aldrig finnas.</para>
        ///
        /// <para><b>⚠️ Aldrig från funktionärens sifferpanel.</b> Tävlingsresultat matas in av
        /// funktionärer, som inte vet vilket vapen skytten använde. Tävlingstaggningen är därför en
        /// efterhandsåtgärd här, och den delade <c>.sp-*</c>-komponenten behöver inte öppnas.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetUsage(string sourceKind, int sourceId, int firearmId, string? occurredOn)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var when = DateTime.TryParse((occurredOn ?? "").Trim(), out var d) ? d : DateTime.Today;
            var error = _usage.SetUsage(memberId, sourceKind, sourceId, firearmId, when);

            return error is null
                ? Json(new { success = true, message = firearmId > 0 ? "Vapnet är angivet." : "Vapnet är borttaget." })
                : Json(new { success = false, message = error });
        }

        /// <summary>Medlemmens taggningar per tillfälle — så resultatlistan kan visa vad som valts.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyUsage()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            return Json(new { success = true, usage = _usage.UsageBySourceForMember(memberId) });
        }

        /// <summary>
        /// Begär ett föreningsintyg av klubben.
        ///
        /// <para>Förfrågan bär INGA vapenuppgifter — den pekar på vapnet, och utfärdaren läser
        /// uppgifterna genom grinden med en loggrad. Kopierades de hit vore de en andra,
        /// okrypterad kopia av precis det registret finns för att skydda.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> RequestIntyg(
            int clubId, int firearmId, string kind, string? forbund, string? vapengrupp, string? message)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            // ⚠️ Klubben måste vara EN AV MEDLEMMENS. Utan kontrollen kunde en medlem lägga en
            // förfrågan i en klubb hen inte tillhör, och den klubbens styrelse skulle då se ett
            // ärende de inte kan hantera.
            var member = _memberService.GetById(memberId);
            if (!_memberClubs.GetAllClubIds(member).Contains(clubId))
                return Json(new { success = false, message = "Du kan bara begära intyg av en klubb du är medlem i." });

            var (requestId, error) = _requests.Create(
                memberId, clubId, kind, firearmId, forbund ?? "", vapengrupp, message);

            return error is null
                ? Json(new { success = true, requestId, message = "Förfrågan är skickad till klubben." })
                : Json(new { success = false, message = error });
        }

        /// <summary>Medlemmens egna förfrågningar, med klubbens svar.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyIntygRequests()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            return Json(new
            {
                success = true,
                requests = _requests.GetForMember(memberId).Select(r => new
                {
                    r.Id, r.FirearmId, r.FirearmAlias, kind = r.KindLabel,
                    status = r.StatusLabel, r.IsOpen, r.Forbund, r.HandlerNote,
                    createdAt = r.CreatedAt.ToString("yyyy-MM-dd"),
                }),
            });
        }

        // ── Lånevapensbokning (punkt 6) ──────────────────────────────────────────────────────────

        /// <summary>
        /// Bokar ett av klubbens lånevapen.
        ///
        /// <para><b>⚠️ Klubben tas från VAPNET, aldrig från begäran.</b> Vapnet vet vem som äger det,
        /// och att låta klienten skicka ett klubb-id vore ett sätt att boka i en klubb man inte
        /// tillhör. Tjänsten kontrollerar medlemskapet mot vapnets egen klubb.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> BookLoanWeapon(
            int firearmId, string? occasionKind, int occasionId, string? from, string? to, string? note)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var (bookingId, error) = _bookings.Create(
                memberId, firearmId,
                string.IsNullOrWhiteSpace(occasionKind) ? FirearmOccasionKind.Fritt : occasionKind,
                occasionId, ParseWhen(from), ParseWhen(to), note);

            return error is null
                ? Json(new { success = true, bookingId, message = "Vapnet är bokat." })
                : Json(new { success = false, message = error });
        }

        /// <summary>Avbokar en egen bokning.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelLoanBooking(int bookingId, string? reason)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            // actorIsClubStaff = false: den här endpointen är medlemmens egen. Klubbens väg går via
            // FirearmAdmin, där rollen prövas — att skicka en flagga härifrån vore att låta
            // klienten påstå sin egen behörighet.
            var error = _bookings.Cancel(bookingId, memberId, actorIsClubStaff: false, reason);
            return error is null
                ? Json(new { success = true, message = "Bokningen är avbokad." })
                : Json(new { success = false, message = error });
        }

        /// <summary>Medlemmens egna bokningar.</summary>
        [HttpGet]
        public async Task<IActionResult> GetMyLoanBookings()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            return Json(new
            {
                success = true,
                bookings = _bookings.GetForMember(memberId).Select(b => new
                {
                    b.Id, b.FirearmId, b.FirearmAlias, number = b.ClubWeaponNumber,
                    from = b.FromTime.ToString("yyyy-MM-dd HH:mm"),
                    to = b.ToTime.ToString("yyyy-MM-dd HH:mm"),
                    status = b.Status, statusLabel = b.StatusLabel,
                    occasion = b.OccasionLabel, b.Note, b.IsActive, b.IsOut,
                }),
            });
        }

        /// <summary>
        /// "yyyy-MM-dd" eller "yyyy-MM-dd HH:mm". ⚠️ Tomt ger <c>default</c> och inte
        /// <c>DateTime.Now</c> — tjänsten normaliserar fönstret och ska få se att inget angetts.
        /// </summary>
        private static DateTime ParseWhen(string? value) =>
            DateTime.TryParse((value ?? "").Trim(), out var d) ? d : default;

        // ── Internt ──────────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Projektionen. <b>⚠️ Bär ALDRIG <c>EncryptedDetails</c></b> — inte ens som base64. Ett
        /// chiffer i en payload är inte läsbart, men det är en kopia av hemligheten som ligger i
        /// varje webbläsarcache och i varje HAR-fil en supportärende bifogar.
        /// </summary>
        private static object Project(
            Firearm f,
            Dictionary<int, int> usageCounts,
            List<ForeningsintygRequest> requests) => new
        {
            f.Id,
            f.Alias,
            f.WeaponClass,
            f.Vapentyp,
            f.AnnanVapentyp,
            vapentypDisplay = f.VapentypDisplay,
            f.AcquisitionStatus,
            statusLabel = FirearmAcquisitionStatus.Label(f.AcquisitionStatus),
            licenseExpiresOn = f.LicenseExpiresOn?.ToString("yyyy-MM-dd"),
            daysUntilExpiry = f.DaysUntilLicenseExpiry,
            federations = f.Federations,
            disciplines = f.Disciplines,
            disciplineLabels = f.Disciplines.Select(ActivityDiscipline.Label),
            // Avgör om kortet ska visa en avmaskeringsknapp alls. Ett vapen utan skyddade
            // uppgifter ska inte erbjuda en knapp som visar ett tomt formulär.
            hasDetails = f.HasProtectedDetails,

            // "Använt vid N tillfällen" — det som gör att man orkar fylla i registret.
            usageCount = usageCounts.TryGetValue(f.Id, out var n) ? n : 0,

            // ⚠️ En ÖPPEN förfrågan visas på kortet, så medlemmen inte begär samma intyg två gånger
            // och undrar varför klubben inte svarar. Servern vägrar dubbletten ändå, men ett nej i
            // efterhand är sämre än en knapp som redan säger "begärd".
            openRequest = requests
                .Where(r => r.FirearmId == f.Id && r.IsOpen)
                .Select(r => new { r.Id, kind = r.KindLabel, status = r.StatusLabel, r.Forbund })
                .FirstOrDefault(),
        };

        private async Task<int> CurrentMemberIdAsync()
        {
            var current = await _memberManager.GetCurrentMemberAsync();
            if (current?.Email is null) return 0;
            return _memberService.GetByEmail(current.Email)?.Id ?? 0;
        }
    }

    /// <summary>
    /// Formulärets form. Listorna kommer in som kommaseparerade strängar, eftersom en FormData-post
    /// är vad resten av kodbasen använder och antiforgery-skyddet bygger på det dolda fältet.
    /// </summary>
    public class FirearmFormRequest
    {
        public int Id { get; set; }
        public string? Alias { get; set; }
        public string? WeaponClass { get; set; }
        public string? Vapentyp { get; set; }
        public string? AnnanVapentyp { get; set; }
        public string? AcquisitionStatus { get; set; }
        public string? LicenseExpiresOn { get; set; }
        public string? Federations { get; set; }
        public string? Disciplines { get; set; }

        /// <summary>
        /// ⚠️ <c>"1"</c> betyder att de skyddade fälten SKICKAS MED och ska skrivas. Allt annat
        /// betyder "rör dem inte" — det är vad en sparning från ett formulär med maskerade fält
        /// gör. Utan flaggan hade varje ändring av ett alias raderat fabrikat och kaliber.
        /// </summary>
        public string? WriteDetails { get; set; }

        public string? Fabrikat { get; set; }
        public string? Modell { get; set; }
        public string? Kaliber { get; set; }
        public string? Piplangd { get; set; }
        public string? Tillverkningsnummer { get; set; }
        public string? Licensnummer { get; set; }
        public string? Licensdatum { get; set; }
        public string? Anteckning { get; set; }

        public FirearmWriteRequest ToWriteRequest() => new()
        {
            Alias = Alias,
            WeaponClass = WeaponClass,
            Vapentyp = Vapentyp,
            AnnanVapentyp = AnnanVapentyp,
            AcquisitionStatus = AcquisitionStatus,
            LicenseExpiresOn = ParseDate(LicenseExpiresOn),
            Federations = Split(Federations),
            Disciplines = Split(Disciplines),
            Details = WriteDetails == "1"
                ? new FirearmDetails
                {
                    Fabrikat = Fabrikat ?? "",
                    Modell = Modell ?? "",
                    Kaliber = Kaliber ?? "",
                    Piplangd = Piplangd ?? "",
                    Tillverkningsnummer = Tillverkningsnummer ?? "",
                    Licensnummer = Licensnummer ?? "",
                    Licensdatum = Licensdatum ?? "",
                    Anteckning = Anteckning ?? "",
                }
                : null,
        };

        private static List<string> Split(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .ToList();

        /// <summary>
        /// "yyyy-MM-dd" ur flatpickr. ⚠️ Tomt ger null och inte <c>DateTime.MinValue</c> — ett
        /// nolldatum hade sparats som år 1 och lästs av påminnelsetjänsten som en förfallen licens.
        /// </summary>
        private static DateTime? ParseDate(string? value) =>
            DateTime.TryParse((value ?? "").Trim(), out var d) ? d.Date : null;
    }
}
