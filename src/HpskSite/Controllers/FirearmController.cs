using HpskSite.Models;
using HpskSite.Models.Firearms;
using HpskSite.Services;
using HpskSite.Services.Firearms;
using Microsoft.AspNetCore.DataProtection;
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

        /// <summary>
        /// Skyddar vapenetikettens token.
        ///
        /// <para><b>⚠️ INTE tidsbegränsad, och det är ett val.</b> Etiketten sitter laminerad på
        /// vapnet i valvet i åratal — en 30-minuterstoken som i Märken hade gjort den oanvändbar
        /// vid första skanningen. Det är <em>bokningsfönstret</em> som gör kontrollen: ingen
        /// bokning i dag, ingen utcheckning. Token bär bara vilket vapen etiketten sitter på, och
        /// den ska inte gå att gissa fram för ett annat vapen.</para>
        /// </summary>
        private readonly IDataProtector _labelProtector;

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
            ILogger<FirearmController> logger,
            IDataProtectionProvider dataProtection)
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
            _labelProtector = dataProtection.CreateProtector("Firearm.LoanLabel.v1");
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
                    // Ägarens EGET id, på ägarens egen endpoint. Ingen exponering — och det är
                    // vad som gör att en yta kan referera till sig själv (t.ex. hämta sina egna
                    // intygsförfrågningar) utan att först behöva slå upp vem den inloggade är.
                    memberId,
                    viewers,
                    lastForeignRead = _accessLog.LastForeignReadFor(memberId)?.ToString("yyyy-MM-dd HH:mm"),
                    // Valmängderna kommer ur konstanterna, aldrig ur en lista i vyn — annars kan
                    // formuläret erbjuda ett förbund intygets ruta inte känner igen.
                    options = new
                    {
                        // ⚠️ FirearmWeaponGroups, inte Enum.GetNames<WeaponClass>() — annars
                        // erbjuds bara gruppkoden "M" och ett magnumvapen går inte att beskriva.
                        weaponClasses = FirearmWeaponGroups.Options
                            .Select(o => new { id = o.Value, label = o.Label }),
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
        public async Task<IActionResult> SetUsage(
            string sourceKind, int sourceId, int firearmId, string? occurredOn, string? sourceClass)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var when = DateTime.TryParse((occurredOn ?? "").Trim(), out var d) ? d : DateTime.Today;
            var error = _usage.SetUsage(memberId, sourceKind, sourceId, firearmId, when, sourceClass);

            return error is null
                ? Json(new { success = true, message = firearmId > 0 ? "Vapnet är angivet." : "Vapnet är borttaget." })
                : Json(new { success = false, message = error });
        }

        /// <summary>
        /// Taggningsytans enda läsning: vilka vapen medlemmen kan välja, och vad som redan är valt.
        ///
        /// <para>Båda i ETT svar, för att de alltid behövs tillsammans — en väljare utan de gjorda
        /// valen visar tomt på rader som redan är taggade, och tvärtom.</para>
        ///
        /// <para><b>⚠️ Bär inga vapenuppgifter.</b> Bara id, namn och vapengrupp — alltså klartexten
        /// som redan står på medlemmens egna kort. Ingen avkryptering, ingen loggrad.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyUsage()
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            // Bara vapen som går att välja: aktiva, och inte utgallrade ur registret.
            var mine = _firearms.GetForScope(FirearmScope.Member(memberId))
                .Where(f => f.AcquisitionStatus != FirearmAcquisitionStatus.Avvecklat)
                .Select(f => new { f.Id, f.Alias, f.WeaponClass })
                .ToList();

            return Json(new
            {
                success = true,
                firearms = mine,
                usage = _usage.UsageBySourceForMember(memberId),
            });
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
            int firearmId, string? occasionKind, int occasionId, string? from, string? to, string? note,
            int clubId = 0, string? weaponClass = null, string? occasionLabel = null,
            int escortMemberId = 0)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            // ⚠️ firearmId = 0 betyder "vilket vapen som helst", och då MÅSTE klubben skickas —
            // det finns inget vapen att härleda den ur. Klienten kan aldrig påverka vilken klubb
            // ett NAMNGIVET vapen tillhör; tjänsten tar den ur vapnet i så fall.
            var (bookingId, error) = _bookings.Create(new FirearmBookingRequest
            {
                MemberId = memberId,
                ClubId = clubId,
                FirearmId = firearmId > 0 ? firearmId : null,
                WeaponClass = weaponClass,
                OccasionKind = string.IsNullOrWhiteSpace(occasionKind)
                    ? FirearmOccasionKind.Fritt : occasionKind,
                OccasionId = occasionId,
                OccasionLabel = occasionLabel,
                From = ParseWhen(from),
                To = ParseWhen(to),
                Note = note,
                EscortMemberId = escortMemberId > 0 ? escortMemberId : null,
                Source = FirearmBookingSource.Web,
            });

            if (error is not null) return Json(new { success = false, message = error });

            var booking = _bookings.GetById(bookingId);

            // ⚠️ Numret läses ur VAPNET, inte ur bokningen. GetById gör ingen join, så
            // ClubWeaponNumber är null där — och då blev beskedet "ett vapen är reserverat" även
            // för den som bad om nr 7. Det är exakt det löfte som avgör om hen kommer alls: ett
            // besked hen kan resa på, inte ett kanske.
            int? number = firearmId > 0 ? _firearms.GetById(firearmId)?.ClubWeaponNumber : null;

            return Json(new
            {
                success = true,
                bookingId,
                number,
                // För en platsbokning ska det INTE stå ett nummer — vilket vapen det blir avgörs
                // i valvet, och ett nummer här vore ett löfte vi inte håller.
                message = number.HasValue
                    ? $"Klart — du får nr {number}."
                    : "Klart — ett vapen är reserverat åt dig.",
                awaitsEscort = booking?.AwaitsEscort == true,
            });
        }

        /// <summary>
        /// Vapnet medlemmen brukar få i en klubb, för förvalet <em>"nr 7, som förra gången"</em>.
        ///
        /// <para><b>Formuläret minns, så skytten aldrig behöver lära sig något.</b> Övergången
        /// mellan "vilket som helst" och "just mitt vapen" sker utan att någon bestämmer den — hen
        /// bara börjar bry sig — och en fråga om vilket läge man är i vore den obesvarbara frågan
        /// igen, i nya kläder.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetMyUsualLoanWeapon(int clubId)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var id = _bookings.UsualFirearmFor(memberId, clubId);
            if (id is null) return Json(new { success = true, firearmId = 0 });

            var f = _firearms.GetById(id.Value);
            return Json(new
            {
                success = true,
                firearmId = id.Value,
                number = f?.ClubWeaponNumber,
                alias = f?.Alias,
                // Går det ens att låna just nu? Ett förval som pekar på ett servicevapen är
                // sämre än inget förval.
                stillLoanable = f is not null && f.IsLoanable && f.IsActive
                                && f.Status is not (FirearmStatus.Service or FirearmStatus.Utgallrat),
            });
        }

        // ── Skanningen ───────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Vad en skanning av en vapenetikett betyder för den inloggade — <b>utan att skriva
        /// något</b>.
        ///
        /// <para><b>⚠️ Läsning och handling är SKILDA anrop, med flit.</b> En QR som öppnas av
        /// misstag i en kameraförhandsvisning får inte lämna ut ett vapen. Den här svarar på vad
        /// som är möjligt; sidan frågar, och först då skrivs något.</para>
        ///
        /// <para><b>⚠️ Skanningen är ingen grind.</b> Har medlemmen ingen bokning erbjuder svaret
        /// att skapa lånet i stället för att neka — dyker någon upp på en träningskväll vill
        /// vapenansvarig låna ut ändå, och en spärr här hade kringgåtts med ett register som
        /// ljuger som följd.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetScanState(string? t)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0)
                return Json(new { success = false, requiresLogin = true, message = "Du måste vara inloggad." });

            var firearmId = UnprotectLabel(t);
            if (firearmId <= 0)
                return Json(new { success = false, message = "Ogiltig kod. Skanna etiketten på vapnet igen." });

            var scan = _bookings.ResolveScan(memberId, firearmId, DateTime.Now);
            var f = scan.Firearm;

            return Json(new
            {
                success = scan.Action != FirearmScanAction.Refused,
                action = scan.Action.ToString(),
                message = scan.Message,
                firearmId = f?.Id ?? 0,
                number = f?.ClubWeaponNumber,
                alias = f?.Alias,
                weaponClass = f?.WeaponClass,
                clubId = scan.ClubId,
                bookingId = scan.Booking?.Id ?? 0,

                // ⚠️ HÄR LIGGER HALVA VÄRDET I SKANNINGEN: den vet två saker vapenansvarig inte
                // vet. Att skytten önskade ett annat vapen — och att det skannade vapnet är bokat
                // av någon ANNAN i kväll. Utan det andra tar den ena skytten den andres vapen, och
                // den andre kommer till en tom hylla.
                wishedNumber = scan.WishedFirearmId is int w
                    ? _firearms.GetById(w)?.ClubWeaponNumber : null,
                claimedByOther = scan.ClaimedByOther is not null,
                claimedByName = scan.ClaimedByOther is null ? null : NameOf(scan.ClaimedByOther.MemberId),

                // ⚠️ Skytten måste få veta VEM som registrerar återlämningen, annars läser hen
                // "du kan inte" som "systemet är trasigt" och struntar i det nästa gång.
                returnedByStaffOnly = scan.Action == FirearmScanAction.OutToYou,
            });
        }

        /// <summary>
        /// Registrerar utlämningen från en skanning — och skapar lånet först om det inte fanns.
        ///
        /// <para><b>⚠️ Utlämningen registreras med medlemmen som aktör</b>, vilket gör
        /// <c>HandedOutBySelf</c> sant av sig själv. En skanning är svagare bevis än en
        /// funktionärs tryck, och de två måste gå att skilja åt i efterhand.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ScanHandOut(string? t, bool accepted = false)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var firearmId = UnprotectLabel(t);
            if (firearmId <= 0) return Json(new { success = false, message = "Ogiltig kod." });

            var scan = _bookings.ResolveScan(memberId, firearmId, DateTime.Now);

            if (scan.Action == FirearmScanAction.Refused)
                return Json(new { success = false, message = scan.Message });

            if (scan.Action == FirearmScanAction.OutToYou)
                return Json(new { success = false, message = "Vapnet är redan utlämnat till dig." });

            // ⚠️ Krockvarningen kräver ett medvetet ja. Utan det tar den som skannar först den
            // andres vapen med ett enda svep, och varningen hade varit dekoration.
            if (scan.ClaimedByOther is not null && !accepted)
                return Json(new { success = false, needsConfirm = true, message = "Bekräfta att du tar vapnet ändå." });

            var bookingId = scan.Booking?.Id ?? 0;

            if (scan.Action == FirearmScanAction.Offer)
            {
                var (newId, createError) = _bookings.Create(new FirearmBookingRequest
                {
                    MemberId = memberId,
                    ClubId = scan.ClubId,
                    FirearmId = firearmId,
                    OccasionKind = FirearmOccasionKind.Fritt,
                    From = DateTime.Now.Date,
                    To = DateTime.Now.Date.AddDays(1).AddSeconds(-1),
                    Source = FirearmBookingSource.Skanning,
                });
                if (createError is not null) return Json(new { success = false, message = createError });
                bookingId = newId;
            }

            var error = _bookings.MarkHandedOut(bookingId, memberId, firearmId);
            if (error is not null) return Json(new { success = false, message = error });

            var f = scan.Firearm;
            return Json(new
            {
                success = true,
                bookingId,
                message = f?.ClubWeaponNumber is int nr
                    ? $"Nr {nr} är utcheckat till dig."
                    : "Vapnet är utcheckat till dig.",
            });
        }

        // ⚠️⚠️ HÄR LÅG `ScanReturn`, OCH DEN ÄR BORTTAGEN MED FLIT (2026-09-02).
        //
        // Den stängde lånet när skytten skannade samma etikett en andra gång. Det betyder att den
        // enda person som har intresse av att lånet ser stängt ut också var den som kunde stänga
        // det — hen kunde skanna vid bilen och åka hem, och registret hade sagt att vapnet står i
        // skåpet. Skadan är inte att skanningen möjliggör stölden (inget hindrar någon från att
        // bära ut ett vapen, och den som inte skannar alls lämnar ett ÖPPET lån som syns) utan att
        // den DÖLJER den. För ett vapenregister är ett falskt "återlämnat" det enda felet som inte
        // får finnas: ett kvarglömt öppet lån är billigt, ett felaktigt stängt gör hela registret
        // oanvändbart som underlag.
        //
        // Regeln som ersatte den: EN SKANNING FÅR BARA ÖKA DET MAN SVARAR FÖR, ALDRIG MINSKA DET.
        // Utlämning via egen skanning består därför — den lägger ansvar PÅ skytten, och en lögn där
        // är mot hens eget intresse. Återlämningen registreras av den som TAR EMOT vapnet:
        // vapenansvarigs "Tillbaka" på raden i valvet, eller "Kvällen är klar" när hen låser.
        //
        // ⚠️ ÅTERSKAPA DEN INTE. Behövs ett sätt för skytten att SÄGA att vapnet är lämnat (t.ex.
        // när vapenansvarig gått hem) ska det vara ett eget tillstånd som fortsätter blockera
        // vapnet till någon bekräftar — aldrig en stängning.

        /// <summary>
        /// Vapen-id ur etikettens token, eller 0.
        ///
        /// <para>Ett ogiltigt eller manipulerat värde ger 0 i stället för ett undantag — en trasig
        /// QR ska ge ett läsbart besked, inte en femhundra.</para>
        /// </summary>
        private int UnprotectLabel(string? token)
        {
            if (string.IsNullOrWhiteSpace(token)) return 0;
            try
            {
                var raw = _labelProtector.Unprotect(token.Trim());
                return raw.StartsWith("firearm:", StringComparison.Ordinal)
                       && int.TryParse(raw["firearm:".Length..], out var id)
                    ? id : 0;
            }
            catch
            {
                return 0;
            }
        }

        private string? NameOf(int memberId)
        {
            var m = _memberService.GetById(memberId);
            if (m is null) return null;
            var n = $"{m.GetValue<string>("firstName")} {m.GetValue<string>("lastName")}".Trim();
            return string.IsNullOrWhiteSpace(n) ? m.Name : n;
        }

        /// <summary>Den medföljande accepterar ansvaret för ett externt lån.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AcceptLoanEscort(int bookingId)
        {
            var memberId = await CurrentMemberIdAsync();
            if (memberId <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var error = _bookings.AcceptEscort(bookingId, memberId);
            return error is null
                ? Json(new { success = true, message = "Du har accepterat ansvaret för vapnet." })
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
                    occasion = b.OccasionDisplay, b.Note, b.IsActive, b.IsOut,
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
