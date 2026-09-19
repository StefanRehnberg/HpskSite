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
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Sign-up and attendance for club and krets events (<c>clubSimpleEvent</c>, which both scopes
    /// share). Kept out of <see cref="ClubController"/>, which owns the event CONTENT — creating and
    /// editing an event is the arrangör's act, signing up and being ticked off is everyone else's.
    ///
    /// ⚠️ Three doctype properties are operator-added (<c>isMandatory</c>,
    /// <c>registrationDeadline</c>, <c>eventPrices</c>). Reading a missing property is harmless
    /// (default), but <c>SetValue</c> on one is a SILENT no-op — so the write endpoints report the
    /// missing property instead of reporting a save that never happened.
    /// </summary>
    public class ClubEventController : SurfaceController
    {
        private readonly IMemberManager _memberManager;
        private readonly IMemberService _memberService;
        private readonly HpskSite.Services.Firearms.FirearmBookingService _bookings;
        private readonly HpskSite.Services.Firearms.FirearmService _firearms;
        private readonly HpskSite.Services.Firearms.LoanWeaponClubRules _loanRules;
        private readonly ClubEventParticipationService _participation;
        private readonly MemberClubService _memberClubs;
        private readonly ILogger<ClubEventController> _logger;
        private readonly ITimeLimitedDataProtector _attendanceProtector;
        private readonly AdminAuthorizationService _auth;

        // ⚠️ Betalningarna går genom LIGGAREN, aldrig genom en egen EventPayment-tabell. Ramen är
        // uttrycklig: en verifikationsliggare som bär klubbens hela ekonomi, med betalningsraden
        // som källdokument och en verifikation under den. En tabell vid sidan om hade betytt att
        // evenemangsintäkterna aldrig kom med i bokslutet.
        private readonly HpskSite.Services.Ledger.LedgerPaymentService _payments;
        private readonly HpskSite.Services.Ledger.LedgerIssuerResolver _issuers;
        private readonly HpskSite.Services.Ledger.LedgerPostingService _posting;

        public ClubEventController(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger profilingLogger,
            IPublishedUrlProvider publishedUrlProvider,
            IMemberManager memberManager,
            IMemberService memberService,
            HpskSite.Services.Firearms.FirearmBookingService bookings,
            HpskSite.Services.Firearms.FirearmService firearms,
            HpskSite.Services.Firearms.LoanWeaponClubRules loanRules,
            ClubEventParticipationService participation,
            MemberClubService memberClubs,
            ILogger<ClubEventController> logger,
            IDataProtectionProvider dataProtectionProvider,
            AdminAuthorizationService auth,
            HpskSite.Services.Ledger.LedgerPaymentService payments,
            HpskSite.Services.Ledger.LedgerIssuerResolver issuers,
            HpskSite.Services.Ledger.LedgerPostingService posting)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _auth = auth;
            _memberManager = memberManager;
            _memberService = memberService;
            _bookings = bookings;
            _firearms = firearms;
            _loanRules = loanRules;
            _participation = participation;
            _memberClubs = memberClubs;
            _logger = logger;
            _attendanceProtector = dataProtectionProvider
                .CreateProtector("ClubEvent.AttendanceQr.v1").ToTimeLimitedDataProtector();
            _payments = payments;
            _issuers = issuers;
            _posting = posting;
        }

        /// <summary>
        /// Sällskapet med betalningarna inräknade.
        ///
        /// <para>⚠️ Betalningarna läses EN gång per anrop och skickas in i <c>BuildParty</c>, som är
        /// ren. Ett uppslag per rad hade blivit en fråga per gäst, och en hämtning inuti den rena
        /// metoden hade gjort reglerna omätbara utan en riktig liggare.</para>
        ///
        /// <para>⚠️ Ett fel i liggaren får INTE ta ner anmälningskortet. Saldot blir då noll
        /// betalt, alltså "inte betald" — det är åt det säkra hållet: en obetald anmälan syns och
        /// kan rättas, en felaktigt betald syns inte.</para>
        /// </summary>
        private ClubEventParty BuildPartyWithPayments(ClubEventRoster roster, int memberId, int eventId)
        {
            List<HpskSite.Models.Ledger.LedgerPayment>? payments = null;
            try
            {
                payments = _payments.ForSource(HpskSite.Models.Ledger.LedgerSourceType.Event, eventId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Kunde inte läsa betalningar för evenemang {EventId}.", eventId);
            }
            return ClubEventParticipationService.BuildParty(roster, memberId, payments);
        }

        private async Task<int> CurrentMemberIdAsync()
        {
            var m = await _memberManager.GetCurrentMemberAsync();
            return m == null || !int.TryParse(m.Id, out var id) ? 0 : id;
        }

        // ── Member-facing ─────────────────────────────────────────────

        /// <summary>
        /// Everything the event page needs to render the sign-up block: the event's own settings,
        /// how many are signed up, and where the current member stands.
        /// GET /umbraco/surface/ClubEvent/GetSignupState?eventId=1234
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetSignupState(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            var member = me > 0 ? _memberService.GetById(me) : null;
            var roster = await _participation.BuildRosterAsync(ctx);
            bool canManage = me > 0 && await _participation.CanManageAsync(ctx, me);
            // ⚠️⚠️ `me > 0` OCH `!IsGuest` — BÅDA behövs, och utan dem läcker kortet.
            // En gästrad bär MemberId = 0. En utloggad besökare har också me = 0. Enbart
            // `r.MemberId == me` hade alltså matchat FÖRSTA GÄSTEN på evenemanget och visat hens
            // namn, notering och pris som "din anmälan" för vem som helst som öppnade sidan.
            var mine = me > 0
                ? roster.Rows.FirstOrDefault(r => !r.IsGuest && r.MemberId == me)
                : null;

            // Medlemmens sällskap: hen själv plus de hen tagit med. Summan läses ur radernas
            // snapshots, aldrig ur dagens prislista.
            var party = mine != null ? BuildPartyWithPayments(roster, me, ctx.EventId) : null;

            // The roster is visible to the owning club's own members (it is their club's event) and
            // to functionaries. Everyone else sees the COUNT — that is what tells a visitor whether
            // there is room, without publishing who is going.
            bool eligible = _participation.IsEligible(ctx, member);
            // ⚠️⚠️ LISTAN FÖLJER INTE ANMÄLNINGSRÄTTEN. Fram till att publiknivån blev valbar var
            // de två samma sak, och den likheten var en tillfällighet: att arrangören öppnar
            // anmälan för hela landet är ett beslut om vem som får KOMMA, inte om att publicera
            // namnen på alla som kommer. `CanSeeRoster` kapar nivån vid kretsen.
            bool showRoster = canManage || _participation.CanSeeRoster(ctx, member);

            return Json(new
            {
                success = true,
                canManage,
                loggedIn = me > 0,
                eligible,
                signupOpen = ClubEventParticipationService.IsSignupOpen(ctx),
                // ⚠️ EGEN flagga, inte `signupOpen`. Avbokning är öppen till evenemangsdagens slut
                // även efter sista anmälningsdag — annars slutar den som inte kan komma säga till,
                // och listan påstår att hen är väntad.
                cancelOpen = ClubEventParticipationService.IsCancelOpen(ctx),
                @event = new
                {
                    id = ctx.EventId,
                    name = ctx.EventName,
                    date = ctx.EventDate?.ToString("yyyy-MM-dd HH:mm"),
                    registrationRequired = ctx.RegistrationRequired,
                    registrationUrl = ctx.RegistrationUrl,
                    maxParticipants = ctx.MaxParticipants,
                    isMandatory = ctx.IsMandatory,
                    // Sista anmälningsdag måste följa med ÄVEN när fönstret ännu är öppet — det är
                    // beskedet som får någon att anmäla sig i tid. Ett kort som bara säger "stängd"
                    // efteråt är för sent för precis den det gällde.
                    registrationDeadline = ctx.RegistrationDeadline?.ToString("yyyy-MM-dd"),
                    // ⚠️ Prisraderna, inte ett tal. Ett evenemang kan ha flera priser, och `fee`
                    // som ett enda belopp hade tvingat klienten att välja en rad åt medlemmen.
                    prices = ctx.Prices.Rows,
                    pricesUnreadable = ctx.Prices.Unreadable,
                    ownerName = ctx.OwnerName,
                    isRegion = ctx.IsRegionOwned
                },
                counts = new
                {
                    signedUp = roster.SignedUp,
                    seated = roster.Seated,
                    reserves = roster.Reserves,
                    seatsLeft = roster.SeatsLeft,
                    present = roster.Present
                },
                me = mine == null ? null : new
                {
                    signedUp = mine.SignedUpAt != null && !mine.Cancelled,
                    cancelled = mine.Cancelled,
                    isReserve = mine.IsReserve,
                    note = mine.Note,
                    attendanceStatus = mine.AttendanceStatus,
                    // Vad hen valde och vad det kostade DÅ. Snapshot — höjs priset efteråt står
                    // medlemmen kvar på det hen sa ja till, och kortet ska visa just det.
                    feePriceId = mine.FeePriceId,
                    feeLabel = mine.FeeLabel,
                    feeAmount = mine.FeeAmount
                },
                // Sällskapet — medlemmens egna gäster och vad de tillsammans kostar. Bara till den
                // det gäller: gästernas namn är inte allmän information, och `party` byggs bara när
                // `mine` finns.
                // Kan evenemanget ta betalt alls? Kräver BÅDE ett Swish-nummer och en avgift —
                // `SwishFromOwner` säger att numret kom från klubben, så arrangörens skärm slipper
                // visa ett tomt fält som läses som att betalning saknas.
                payment = new
                {
                    swishAvailable = ctx.CanTakeSwish,
                    swishFromOwner = ctx.SwishFromOwner,
                    // ⚠️ Numret lämnas ALDRIG ut till en utloggad. Det är inte hemligt, men ett
                    // publikt fält är en inbjudan att skrapa, och den som ska betala är inloggad.
                    swishNumber = me > 0 ? ctx.SwishNumber : "",
                },
                party = party == null ? null : new
                {
                    people = party.People,
                    total = party.Total,
                    missingPrice = party.MissingPrice,
                    maxGuests = ClubEvents.MaxGuestsPerMember,
                    // ⚠️ TRE TAL, inte ett. "Bekräftat" är pengar, "påstått" är ett påstående, och
                    // att slå ihop dem gör arrangörens avprickningslista oanvändbar som kontroll.
                    confirmedPaid = party.ConfirmedPaid,
                    claimedPaid = party.ClaimedPaid,
                    // ⚠️⚠️ TVÅ SKILDA FRÅGOR, och kortet använder olika svar på dem:
                    //   isPaymentPresented — får personen stå på listan? (Swish-koden har visats)
                    //   outstanding        — vad väntar arrangören fortfarande på? (bara bekräftat)
                    // Drevs båda av samma tal vore antingen varje kvällsanmälan ogiltig till dagen
                    // efter, eller så vore avprickningslistan blind för dem som aldrig betalade.
                    amountPresented = party.AmountPresented,
                    isPaymentPresented = party.IsPaymentPresented,
                    outstanding = party.Outstanding,
                    isSettled = party.IsSettled,
                    remainingToRequest = party.RemainingToRequest,
                    awaitingConfirmation = party.AwaitingConfirmation,
                    guests = party.Guests.Select(g => new
                    {
                        id = g.Id,
                        name = g.Name,
                        isReserve = g.IsReserve,
                        feeLabel = g.FeeLabel,
                        feeAmount = g.FeeAmount
                    })
                },
                roster = showRoster
                    ? roster.Rows.Where(r => !r.Cancelled && !r.IsWalkIn).Select(r => new
                    {
                        memberId = r.MemberId,
                        name = r.Name,
                        // ⚠️ Gästen ska SYNAS som gäst på listan. Ett namn utan konto bland
                        // medlemsnamnen ser ut som en medlem vi inte hittar, och funktionären som
                        // letar efter hen i medlemsregistret letar förgäves.
                        isGuest = r.IsGuest,
                        isReserve = r.IsReserve,
                        signedUpAt = r.SignedUpAt?.ToString("yyyy-MM-dd HH:mm")
                    })
                    : null,
                loanWeapons = BuildLoanWeaponState(ctx, me, canManage),
            });
        }

        /// <summary>POST /umbraco/surface/ClubEvent/SignUp</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        /// <summary>
        /// Lånevapenläget för ett tillfälle: erbjuds de, hur många är kvar, och vad brukar just
        /// den här medlemmen få.
        ///
        /// <para><b>⚠️ Bara KLUBBENS händelser.</b> Lånevapnen tillhör en klubb; en krets har inga.
        /// Ett kretsevenemang på någon annans bana är i praktiken ett lån utanför klubben, med
        /// egna regler — inte något den här kryssrutan ska försöka lösa.</para>
        ///
        /// <para><b>⚠️ <c>usualNumber</c> är vad som gör att skytten aldrig behöver lära sig
        /// något.</b> Övergången från "vilket som helst" till "just mitt vapen" sker utan att någon
        /// bestämmer den — hen börjar bara bry sig — så formuläret minns i stället för att fråga
        /// vilket läge man är i.</para>
        ///
        /// <para><b>⚠️ Läget måste kunna läsas EFTER anmälan.</b> <c>myBookingId</c> och
        /// <c>bookedForEvent</c> gäller den här händelsen, inte dagen: skytten ska kunna se om hen
        /// kryssade i rutan, och vapenansvarig hur många vapen som ska plockas fram. Dagsfönstrets
        /// <c>occupied</c> svarar på en annan fråga (finns det plats kvar) och duger inte till
        /// någondera.</para>
        /// </summary>
        private object? BuildLoanWeaponState(ClubEventContext ctx, int memberId, bool canManage)
        {
            if (ctx.IsRegionOwned) return null;

            var clubId = ctx.OwnerId;
            if (clubId <= 0) return null;

            var (offered, propertyExists) = _loanRules.EventOffersLoanWeapons(ctx.EventId);
            var loanable = _firearms.CountLoanable(clubId);

            // Erbjuds de inte, eller har klubben inga vapen, finns inget att visa. En kryssruta
            // utan vapen bakom är en fråga utan svarsalternativ.
            if (!offered || loanable == 0)
                return new { offered = false, propertyExists, loanable };

            var day = (ctx.EventDate ?? DateTime.Now).Date;
            var from = day;
            var to = day.AddDays(1).AddSeconds(-1);

            // Lånen som hör till DEN HÄR händelsen. En läsning, två svar: skyttens eget lån och
            // vapenansvarigs plocklista.
            var forEvent = _bookings
                .GetForOccasion(clubId, HpskSite.Services.Firearms.FirearmOccasionKind.Event, ctx.EventId)
                .Where(b => b.IsActive)
                .ToList();

            var mine = memberId > 0
                ? forEvent.FirstOrDefault(b => b.MemberId == memberId)
                : null;

            var taken = _bookings.BookedFirearmIds(clubId, from, to);
            var usual = memberId > 0 ? _bookings.UsualFirearmFor(memberId, clubId) : null;

            return new
            {
                offered = true,
                propertyExists,
                loanable,
                occupied = _bookings.CountOccupiedInWindow(clubId, from, to),
                clubId,
                myBookingId = mine?.Id ?? 0,
                myNumber = mine?.ClubWeaponNumber,
                myWishNumber = mine?.WishedWeaponNumber,
                // Siffran vapenansvarig plockar efter: bokade lånevapen på just det här tillfället.
                bookedForEvent = forEvent.Count,
                // ⚠️ VEM som lånar visas bara för den som håller i tillfället. Anmälningslistan är
                // öppen för klubbens medlemmar, och att där skylta med vem som inte har eget vapen
                // är en annan uppgift än den listan finns för.
                bookings = canManage
                    ? forEvent.Select(b => new
                    {
                        id = b.Id,
                        memberName = b.MemberName,
                        // Numret som GÄLLER nu: tilldelat om det finns, annars önskat. Tomt =
                        // platsbokning, ett vapen vilket som helst — det avgörs i valvet.
                        number = b.ClubWeaponNumber,
                        statusLabel = b.StatusLabel,
                    }).ToList()
                    : null,
                usualFirearmId = usual ?? 0,
                usualNumber = usual is int u ? _firearms.GetById(u)?.ClubWeaponNumber : null,
                // ⚠️ Är det vanliga vapnet redan taget den kvällen ska det sägas VID BOKNINGEN,
                // inte i valvet — det är då skytten bestämmer om hen ska komma.
                usualTaken = usual is int u2 && taken.Contains(u2),
                weapons = _bookings.AvailableInWindow(clubId, from, to)
                    .Select(f => new { f.Id, number = f.ClubWeaponNumber, f.Alias, f.WeaponClass }),
            };
        }

        public async Task<IActionResult> SignUp([FromBody] SignUpRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad för att anmäla dig." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });
            if (!ctx.RegistrationRequired) return Json(new { success = false, message = "Det här evenemanget har ingen anmälan." });
            if (!ClubEventParticipationService.IsSignupOpen(ctx)) return Json(new { success = false, message = "Anmälan är stängd." });

            var member = _memberService.GetById(me);
            if (!_participation.IsEligible(ctx, member))
                return Json(new { success = false, message = NotEligibleMessage(ctx) });

            // ⚠️ PRISVALET GRINDAS FORE anmalan skrivs. Har evenemanget flera priser och inget
            // giltigt valts skulle SignUpAsync satta beloppet till null, och deltagaren stod som
            // anmald utan att nagon vet vad hen ska betala. Ett halvt atagande ar varre an inget.
            var priceError = ClubEventParticipationService.PriceChoiceError(ctx, request?.PriceId);
            if (priceError != null) return Json(new { success = false, message = priceError });

            var (ok, msg, isReserve) = await _participation.SignUpAsync(ctx, me, request?.Note, me, request?.PriceId);
            if (!ok) return Json(new { success = false, message = msg });

            // ── Lånevapnet, som en del av SAMMA handling ──────────────────────────────────────
            // ⚠️ Anmälan och lånet är en handling för nybörjaren. Skiljer vi dem åt får vi personer
            // som är anmälda utan vapen och vapen bokade av folk som inte kommer — precis det
            // vapenansvarig inte kan lösa i valvet.
            string? loanMessage = null;
            if (request?.LoanWeapon == true && !ctx.IsRegionOwned && ctx.OwnerId > 0)
            {
                var day = (ctx.EventDate ?? DateTime.Now).Date;
                var (_, loanError) = _bookings.Create(new HpskSite.Services.Firearms.FirearmBookingRequest
                {
                    MemberId = me,
                    ClubId = ctx.OwnerId,
                    FirearmId = request.LoanFirearmId > 0 ? request.LoanFirearmId : null,
                    OccasionKind = HpskSite.Services.Firearms.FirearmOccasionKind.Event,
                    OccasionId = ctx.EventId,
                    From = day,
                    To = day.AddDays(1).AddSeconds(-1),
                    Source = HpskSite.Services.Firearms.FirearmBookingSource.Web,
                });

                // ⚠️ ANMÄLAN RULLAS INTE TILLBAKA om vapnet inte gick att boka. Att vara anmäld
                // utan vapen är bättre än att inte vara anmäld — och skytten måste få veta vilket
                // av de två som blev av, inte ett samlat "det gick inte".
                loanMessage = loanError is null
                    ? (request.LoanFirearmId > 0
                        ? "Vapnet är reserverat."
                        : "Ett vapen är reserverat åt dig.")
                    : "⚠️ Du är anmäld, men vapnet kunde inte bokas: " + loanError;
            }

            return Json(new
            {
                success = true,
                isReserve,
                loanMessage,
                message = isReserve
                    ? "Du står som reserv — vi hör av oss om en plats blir ledig."
                    : "Du är anmäld."
            });
        }

        /// <summary>POST /umbraco/surface/ClubEvent/Cancel</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Cancel([FromBody] SignUpRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            // A member may withdraw themselves; a functionary may withdraw anyone on their event.
            int target = request?.MemberId > 0 ? request.MemberId : me;
            bool canManage = await _participation.CanManageAsync(ctx, me);
            if (target != me && !canManage)
                return Json(new { success = false, message = "Åtkomst nekad." });

            var (ok, msg, guestsCancelled) = await _participation.CancelAsync(ctx.EventId, target, me);
            if (!ok) return Json(new { success = false, message = msg });

            // ⚠️ AVBOKNINGEN MÅSTE SLÄPPA VAPNET. Anmälan och lånet gjordes som EN handling, och
            // överlever lånet avbokningen står vapenansvarig med ett framplockat vapen till någon
            // som inte kommer — samtidigt som platsen är tagen från någon som gör det.
            var loanMessage = ReleaseLoanOnCancel(ctx, target, me, canManage);

            // ⚠️ SÄG ATT GÄSTERNA FÖLJDE MED. Avbokningen tar hela sällskapet, och ett blankt
            // "Anmälan avbokad" hade lämnat Hugo i tron att frun och sonen står kvar på listan.
            // Det upptäcks i så fall först på plats, av arrangören, med fel antal stolar.
            var message = guestsCancelled switch
            {
                0 => "Anmälan avbokad.",
                1 => "Anmälan avbokad, och din gäst är avanmäld.",
                _ => $"Anmälan avbokad, och dina {guestsCancelled} gäster är avanmälda."
            };

            return Json(new { success = true, loanMessage, message });
        }

        /// <summary>
        /// POST /umbraco/surface/ClubEvent/AddGuest — anmäl en anhörig eller gäst utan konto.
        ///
        /// <para><b>⚠️ Behörigheten är samma grind som medlemmens egen anmälan</b>, och det är
        /// avsiktligt: gästen tar en plats på evenemanget precis som medlemmen. Vore grinden lösare
        /// här kunde en medlem som själv är utestängd ändå fylla lokalen med gäster.</para>
        ///
        /// <para>⚠️ <c>IsEligible</c> frågas om MEDLEMMEN, aldrig om gästen. Gästen har per
        /// definition ingen klubbtillhörighet — det är hela skälet att hen är gäst.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddGuest([FromBody] GuestRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad för att anmäla en gäst." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });
            if (!ctx.RegistrationRequired) return Json(new { success = false, message = "Det här evenemanget har ingen anmälan." });
            if (!ClubEventParticipationService.IsSignupOpen(ctx)) return Json(new { success = false, message = "Anmälan är stängd." });

            var member = _memberService.GetById(me);
            if (!_participation.IsEligible(ctx, member))
                return Json(new { success = false, message = NotEligibleMessage(ctx) });

            // ⚠️⚠️ VEM GÄSTEN HÖR TILL är normalt den inloggade — men i disken är det en ANNAN
            // medlem. Utan den här vägen kan en funktionär inte lägga till frun som dyker upp med
            // Hugo på uppropet, vilket är precis den situation gästbegreppet finns för.
            //
            // ⚠️ Att peka ut någon annan som ansvarig kräver att man får administrera evenemanget.
            // Annars kunde vem som helst hänga en gäst — och därmed en avgift — på en främling.
            int host = me;
            if (request?.GuestOfMemberId > 0 && request.GuestOfMemberId != me)
            {
                if (!await _participation.CanManageAsync(ctx, me))
                    return Json(new { success = false, message = "Du kan bara anmäla dina egna gäster." });
                host = request.GuestOfMemberId;
            }

            var (ok, msg, isReserve) = await _participation.AddGuestAsync(
                ctx, host, request?.Name, request?.PriceId, me);
            if (!ok) return Json(new { success = false, message = msg });

            var name = request!.Name!.Trim();
            return Json(new
            {
                success = true,
                isReserve,
                message = isReserve
                    ? $"{name} står som reserv — vi hör av oss om en plats blir ledig."
                    : $"{name} är anmäld."
            });
        }

        /// <summary>
        /// POST /umbraco/surface/ClubEvent/CancelGuest — avboka EN gäst, utan att röra medlemmens
        /// egen anmälan.
        ///
        /// <para>⚠️ Ingen anmälningsfönster-kontroll här. Stängd anmälan får hindra att någon
        /// <i>tillkommer</i>, aldrig att någon lämnar återbud — en kvarstående plats för någon som
        /// inte kommer är sämre för arrangören än en sen avbokning.</para>
        ///
        /// <para>Ägarskapet prövas i tjänsten, som äger regeln.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CancelGuest([FromBody] GuestRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            var (ok, msg) = await _participation.CancelGuestAsync(ctx, request?.ParticipantId ?? 0, me);
            return Json(ok
                ? new { success = true, message = "Gästen är avanmäld." }
                : new { success = false, message = msg ?? "Avbokningen gick inte igenom." });
        }

        /// <summary>
        /// Släpper medlemmens lånevapen på tillfället när anmälan avbokas.
        ///
        /// <para><b>⚠️ Ett UTLÄMNAT vapen släpps inte.</b> Det ligger fysiskt hos medlemmen och
        /// måste återlämnas i valvet — försvinner bokningen tappar klubben spåret till vem som har
        /// det. Då säger beskedet det i stället.</para>
        /// </summary>
        private string? ReleaseLoanOnCancel(ClubEventContext ctx, int memberId, int actorId, bool actorIsStaff)
        {
            if (ctx.IsRegionOwned || ctx.OwnerId <= 0) return null;

            var mine = _bookings
                .GetForOccasion(ctx.OwnerId, HpskSite.Services.Firearms.FirearmOccasionKind.Event, ctx.EventId)
                .FirstOrDefault(b => b.MemberId == memberId && b.IsActive);
            if (mine == null) return null;

            if (mine.IsOut)
                return "⚠️ Anmälan är avbokad, men lånevapnet är utlämnat — lämna tillbaka det i valvet.";

            var error = _bookings.Cancel(mine.Id, actorId, actorIsStaff, "Anmälan till evenemanget avbokad.");
            return error is null
                ? "Lånevapnet är avbokat."
                : "⚠️ Anmälan är avbokad, men lånevapnet gick inte att släppa: " + error;
        }

        /// <summary>
        /// Lägger till eller tar bort lånevapnet EFTER anmälan.
        /// POST /umbraco/surface/ClubEvent/SetLoanWeapon
        ///
        /// <para><b>⚠️ Följer <c>cancelOpen</c>, inte <c>signupOpen</c>.</b> Att man inser att man
        /// behöver låna — eller att man inte gör det — händer typiskt efter sista anmälningsdag,
        /// och en spärr där hade bara gett vapenansvarig fel siffra att plocka efter.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetLoanWeapon([FromBody] SignUpRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });
            if (ctx.IsRegionOwned || ctx.OwnerId <= 0)
                return Json(new { success = false, message = "Lånevapen hör till en klubb, inte till kretsens evenemang." });
            if (!ClubEventParticipationService.IsCancelOpen(ctx))
                return Json(new { success = false, message = "Evenemanget har passerat." });

            // Lånet hänger på anmälan. Ett vapen bokat av någon som inte står på listan är precis
            // det vapenansvarig inte kan reda ut i valvet.
            var roster = await _participation.BuildRosterAsync(ctx);
            // ⚠️ !IsGuest — en gästrad bär MemberId = 0, och utan filtret hade en utloggad (me = 0)
            // kunnat boka lånevapen på första gästens anmälan. Samma fälla som i GetSignupState.
            var mineRow = roster.Rows.FirstOrDefault(r => !r.IsGuest && r.MemberId == me);
            if (mineRow == null || mineRow.SignedUpAt == null || mineRow.Cancelled)
                return Json(new { success = false, message = "Anmäl dig först, så kan du boka lånevapen." });

            var existing = _bookings
                .GetForOccasion(ctx.OwnerId, HpskSite.Services.Firearms.FirearmOccasionKind.Event, ctx.EventId)
                .FirstOrDefault(b => b.MemberId == me && b.IsActive);

            if (request?.LoanWeapon != true)
            {
                if (existing == null) return Json(new { success = true, loanMessage = "Du har inget lånevapen bokat." });
                if (existing.IsOut)
                    return Json(new { success = false, message = "Vapnet är utlämnat och måste återlämnas i valvet." });

                var cancelError = _bookings.Cancel(existing.Id, me, false, "Behövde inget lånevapen.");
                return cancelError is null
                    ? Json(new { success = true, loanMessage = "Lånevapnet är avbokat." })
                    : Json(new { success = false, message = cancelError });
            }

            if (existing != null)
                return Json(new { success = true, loanMessage = "Du har redan ett lånevapen bokat." });

            var day = (ctx.EventDate ?? DateTime.Now).Date;
            var (_, error) = _bookings.Create(new HpskSite.Services.Firearms.FirearmBookingRequest
            {
                MemberId = me,
                ClubId = ctx.OwnerId,
                FirearmId = request.LoanFirearmId > 0 ? request.LoanFirearmId : null,
                OccasionKind = HpskSite.Services.Firearms.FirearmOccasionKind.Event,
                OccasionId = ctx.EventId,
                From = day,
                To = day.AddDays(1).AddSeconds(-1),
                Source = HpskSite.Services.Firearms.FirearmBookingSource.Web,
            });

            return error is null
                ? Json(new
                {
                    success = true,
                    loanMessage = request.LoanFirearmId > 0 ? "Vapnet är reserverat." : "Ett vapen är reserverat åt dig.",
                })
                : Json(new { success = false, message = error });
        }

        // ── Functionary: roll-call ────────────────────────────────────

        /// <summary>
        /// The full roster for the roll-call screen: signed up, reserves, walk-ins and cancellations,
        /// each with its attendance state.
        /// GET /umbraco/surface/ClubEvent/GetRoster?eventId=1234
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetRoster(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var roster = await _participation.BuildRosterAsync(ctx);
            return Json(new
            {
                success = true,
                @event = new
                {
                    id = ctx.EventId,
                    name = ctx.EventName,
                    date = ctx.EventDate?.ToString("yyyy-MM-dd HH:mm"),
                    isMandatory = ctx.IsMandatory,
                    maxParticipants = ctx.MaxParticipants,
                    registrationRequired = ctx.RegistrationRequired,
                    // ⚠️ Prisraderna behövs för att funktionären ska kunna lägga till en gäst i
                    // disken — samma regel som medlemmens: ett enda pris väljer sig självt, flera
                    // kräver ett val. Att gissa hade satt ett belopp ingen pekat på, och det är
                    // MEDLEMMEN som faktureras.
                    prices = ctx.Prices.Rows,
                    ownerName = ctx.OwnerName
                },
                counts = new
                {
                    signedUp = roster.SignedUp,
                    seated = roster.Seated,
                    reserves = roster.Reserves,
                    cancelled = roster.Cancelled,
                    present = roster.Present,
                    notRecorded = roster.NotRecorded,
                    seatsLeft = roster.SeatsLeft
                },
                rows = roster.Rows.Select(r => new
                {
                    // ⚠️ Radens id är avprickningens adress för BÅDA slagen. En gäst har inget
                    // medlems-id, och en väg per slag hade kunnat glida isär.
                    id = r.Id,
                    memberId = r.MemberId,
                    isGuest = r.IsGuest,
                    guestOfMemberId = r.GuestOfMemberId,
                    name = r.Name,
                    signedUpAt = r.SignedUpAt?.ToString("yyyy-MM-dd HH:mm"),
                    cancelled = r.Cancelled,
                    isReserve = r.IsReserve,
                    isWalkIn = r.IsWalkIn,
                    note = r.Note,
                    attendanceStatus = r.AttendanceStatus,
                    attendanceLabel = ClubEvents.AttendanceDisplay(r.AttendanceStatus),
                    attendanceNote = r.AttendanceNote,
                    selfRegistered = r.SelfRegistered,
                    fee = r.FeeAmount
                })
            });
        }

        /// <summary>
        /// Tick someone off — or clear the tick. <c>status</c> null puts the row back to
        /// "ej registrerad", which is a real third state: a mandatory event whose roll-call was
        /// never taken must not read as everyone having stayed away.
        /// POST /umbraco/surface/ClubEvent/SetAttendance
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SetAttendance([FromBody] AttendanceRequest request)
        {
            // ⚠️ ParticipantId ELLER MemberId. En gäst har inget medlems-id att pekas ut med, så
            // ett krav på MemberId > 0 hade gjort frun och sonen omöjliga att pricka av.
            if (request == null || request.EventId <= 0 || (request.MemberId <= 0 && request.ParticipantId <= 0))
                return Json(new { success = false, message = "Ogiltig begäran — evenemang och deltagare måste anges." });

            var ctx = _participation.GetEventContext(request.EventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var status = string.IsNullOrWhiteSpace(request.Status) ? null : request.Status.Trim();

            // ⚠️ Radens id vinner när det finns. Uppropet skickar det för VARJE rad, så medlemmar
            // och gäster prickas av på exakt samma väg — två vägar hade kunnat glida isär, och
            // avprickningen är på väg att bli underlag till ett Föreningsintyg.
            var (ok, msg) = request.ParticipantId > 0
                ? await _participation.SetAttendanceForRowAsync(
                    request.EventId, request.ParticipantId, status, request.Note, me)
                : await _participation.SetAttendanceAsync(
                    request.EventId, request.MemberId, status, request.Note, me);

            return Json(new { success = ok, message = msg, label = ClubEvents.AttendanceDisplay(status) });
        }

        /// <summary>
        /// Members who could be added at the door — the owning club's members (or, for a krets
        /// event, the krets's) minus those already on the list.
        /// GET /umbraco/surface/ClubEvent/SearchAddableMembers?eventId=1234&amp;q=and
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> SearchAddableMembers(int eventId, string? q)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var already = (await _participation.GetParticipantsAsync(eventId)).Select(p => p.MemberId).ToHashSet();
            var term = (q ?? "").Trim();

            // Resolve the eligible clubs ONCE — the per-member overload would re-read the krets's
            // club list for every member in the register.
            var eligibleClubs = _participation.GetEligibleClubIds(ctx);

            // ⚠️ På de breda publiknivåerna är klubbmängden TOM med flit (de går inte att uttrycka
            // som en klubblista), och en rak filtrering hade då gett noll träffar — alltså en
            // dörrlista som ser trasig ut på exakt de händelser som bjudit in flest. Där är varje
            // medlem en giltig träff, och sökrutan är det som avgränsar.
            bool anyMemberQualifies = HpskSite.Models.EventAudience
                .IsAtLeastAsWideAs(ctx.Audience, HpskSite.Models.EventAudience.AllMembers);

            var results = new List<object>();
            foreach (var member in _memberService.GetAll(0, int.MaxValue, out _))
            {
                if (already.Contains(member.Id)) continue;
                if (!anyMemberQualifies && !_participation.IsEligible(eligibleClubs, member)) continue;
                if (term.Length > 0 && (member.Name ?? "").IndexOf(term, StringComparison.OrdinalIgnoreCase) < 0) continue;
                results.Add(new { memberId = member.Id, name = member.Name ?? $"Medlem {member.Id}" });
                if (results.Count >= 50) break;
            }

            return Json(new { success = true, members = results });
        }


        // ── Närvaro via QR-kod ────────────────────────────────────────

        /// <summary>
        /// How long a printed attendance QR stays valid. NOT a fixed short window like the Märken
        /// verify token: this one is printed and taped to a wall before the event, so a 30-minute
        /// lifetime would make it useless. It is instead tied to the EVENT — valid until the end of
        /// the event day plus a margin — so a photographed code cannot be redeemed next month.
        /// </summary>
        private static TimeSpan AttendanceTokenLifetime(ClubEventContext ctx)
        {
            var last = ctx.EventEndDate ?? ctx.EventDate;
            if (last == null) return TimeSpan.FromDays(2);
            var until = last.Value.Date.AddDays(1).AddHours(12);
            var span = until - DateTime.Now;
            return span < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1)
                 : span > TimeSpan.FromDays(120) ? TimeSpan.FromDays(120)
                 : span;
        }

        /// <summary>
        /// ⚠️ Second gate, on purpose. The token's lifetime says "not next month"; this says "not the
        /// day before either". Self-registration is only accepted while the event is actually
        /// happening — from 12 h before it starts until 12 h after it ends. An undated event has no
        /// window to check against and is therefore accepted whenever the token is still alive.
        /// </summary>
        private static bool IsCheckInWindowOpen(ClubEventContext ctx, DateTime? now = null)
        {
            if (ctx.EventDate == null) return true;
            var t = now ?? DateTime.Now;
            var from = ctx.EventDate.Value.AddHours(-12);
            var to = (ctx.EventEndDate ?? ctx.EventDate).Value.Date.AddDays(1).AddHours(12);
            return t >= from && t <= to;
        }

        private byte[]? QrPng(string url)
        {
            try
            {
                var gen = new QRCoder.QRCodeGenerator();
                using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.Q);
                var qr = new QRCoder.QRCode(data);
                using var img = qr.GetGraphic(
                    pixelsPerModule: 10,
                    darkColor: SixLabors.ImageSharp.Color.Black,
                    lightColor: SixLabors.ImageSharp.Color.White,
                    drawQuietZones: true);
                using var ms = new System.IO.MemoryStream();
                img.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
                return ms.ToArray();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Kunde inte generera QR-kod för närvaro");
                return null;
            }
        }

        private string BuildCheckInUrl(int eventId, ClubEventContext ctx)
        {
            var token = _attendanceProtector.Protect(eventId.ToString(), AttendanceTokenLifetime(ctx));
            return $"{Request.Scheme}://{Request.Host}/evenemang/narvaro?t={Uri.EscapeDataString(token)}";
        }

        /// <summary>
        /// Printable poster with the attendance QR. Staff-gated — the code IS the check-in, so it is
        /// the arrangör who decides it exists.
        /// GET /umbraco/surface/ClubEvent/PrintAttendanceQr?eventId=1234
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> PrintAttendanceQr(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Content("Evenemanget hittades inte.");

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me)) return Content("Åtkomst nekad.");

            var url = BuildCheckInUrl(eventId, ctx);
            var png = QrPng(url);
            if (png == null) return Content("Kunde inte generera QR-koden.");

            string Enc(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");
            var img = "data:image/png;base64," + Convert.ToBase64String(png);
            var when = ctx.EventDate?.ToString("dddd d MMMM yyyy, HH:mm",
                new System.Globalization.CultureInfo("sv-SE")) ?? "";

            var sb = new System.Text.StringBuilder();
            sb.Append("<!DOCTYPE html><html lang='sv'><head><meta charset='utf-8'>");
            sb.Append("<title>Närvaro – ").Append(Enc(ctx.EventName)).Append("</title>");
            sb.Append("<style>body{font-family:Arial,Helvetica,sans-serif;margin:2rem;color:#111;text-align:center}");
            sb.Append("h1{font-size:2rem;margin:.2rem 0}h2{font-size:1.2rem;font-weight:normal;color:#444;margin:.2rem 0 1.2rem}");
            // Skarpa kanter på modulerna när bilden skalas upp — en mjukskalad QR blir svårläst.
            sb.Append("img{width:11cm;height:11cm;image-rendering:pixelated;border:1px solid #ddd}");
            sb.Append(".steps{max-width:16cm;margin:1.2rem auto 0;text-align:left;font-size:1.05rem;line-height:1.6}");
            sb.Append(".muted{color:#666;font-size:.85rem;margin-top:1.5rem}");
            sb.Append("@media print{button{display:none}}</style></head><body>");
            sb.Append("<button onclick='window.print()'>Skriv ut</button>");
            sb.Append("<h1>Registrera din närvaro</h1>");
            sb.Append("<h2>").Append(Enc(ctx.EventName));
            if (!string.IsNullOrEmpty(when)) sb.Append("<br>").Append(Enc(when));
            sb.Append("</h2>");
            // data-checkin-url gör vad koden pekar på läsbart utan att skräpa ner affischen: det är
            // enda sättet att felsöka en QR som inte fungerar, och det är vad verifieringssviten
            // följer för att kunna prova hela skanningsvägen. Ingen hemlighet läcker — den som har
            // affischen framför sig har redan koden.
            sb.Append("<img alt='QR-kod för närvaroregistrering' data-checkin-url='")
              .Append(Enc(url)).Append("' src='").Append(img).Append("'>");
            sb.Append("<div class='steps'><ol>");
            sb.Append("<li>Skanna koden med telefonens kamera.</li>");
            sb.Append("<li>Logga in på pistol.nu om du inte redan är det.</li>");
            sb.Append("<li>Tryck <strong>Registrera min närvaro</strong>.</li>");
            sb.Append("</ol></div>");
            if (ctx.IsMandatory)
            {
                sb.Append("<p class='muted'><strong>Obligatoriskt evenemang.</strong> Närvaron är underlag för klubbens beslut om Föreningsintyg.</p>");
            }
            sb.Append("<p class='muted'>Koden gäller bara i anslutning till evenemanget. Går det inte — säg till en funktionär, som kan pricka av dig för hand.</p>");
            sb.Append("</body></html>");

            return Content(sb.ToString(), "text/html; charset=utf-8");
        }

        /// <summary>
        /// What the scanned page needs before the member presses the button: which event this is, and
        /// whether they may register at all. Deliberately does NOT register anything — a QR opened by
        /// accident in a camera preview must not tick someone off.
        /// GET /umbraco/surface/ClubEvent/GetCheckInState?t=...
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetCheckInState(string? t)
        {
            var ctx = ResolveCheckInToken(t, out var tokenError);
            if (ctx == null) return Json(new { success = false, message = tokenError });

            int me = await CurrentMemberIdAsync();
            var member = me > 0 ? _memberService.GetById(me) : null;
            var existing = me > 0 ? await _participation.GetParticipantAsync(ctx.EventId, me) : null;

            return Json(new
            {
                success = true,
                loggedIn = me > 0,
                eligible = _participation.IsEligible(ctx, member),
                windowOpen = IsCheckInWindowOpen(ctx),
                alreadyPresent = existing?.AttendanceStatus == ClubEvents.AttendancePresent,
                signedUp = existing?.SignedUpAt != null && existing.CancelledAt == null,
                @event = new
                {
                    id = ctx.EventId,
                    name = ctx.EventName,
                    date = ctx.EventDate?.ToString("yyyy-MM-dd HH:mm"),
                    venue = ctx.Venue,
                    isMandatory = ctx.IsMandatory,
                    ownerName = ctx.OwnerName
                }
            });
        }

        /// <summary>
        /// The member registers their own attendance by scanning the poster.
        /// POST /umbraco/surface/ClubEvent/SelfCheckIn
        ///
        /// ⚠️ A self-scan is NOT the same evidence as a functionary's roll-call — the poster can be
        /// photographed and passed on. The row therefore records the member as their own recorder,
        /// and the roster labels it "självregistrerad" so the board can tell the two apart when the
        /// attendance is used for a Föreningsintyg.
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> SelfCheckIn([FromBody] CheckInRequest request)
        {
            var ctx = ResolveCheckInToken(request?.Token, out var tokenError);
            if (ctx == null) return Json(new { success = false, message = tokenError });

            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad för att registrera närvaro." });

            if (!IsCheckInWindowOpen(ctx))
                return Json(new { success = false, message = "Koden gäller bara i anslutning till evenemanget. Be en funktionär pricka av dig." });

            var member = _memberService.GetById(me);
            if (!_participation.IsEligible(ctx, member))
                return Json(new
                {
                    success = false,
                    message = ctx.IsRegionOwned
                        ? "Närvaroregistrering är öppen för medlemmar i kretsens klubbar."
                        : $"Närvaroregistrering är öppen för medlemmar i {ctx.OwnerName}."
                });

            var (ok, msg) = await _participation.SetAttendanceAsync(
                ctx.EventId, me, ClubEvents.AttendancePresent, null, me);

            return Json(new { success = ok, message = ok ? "Din närvaro är registrerad." : msg });
        }

        // ── Betalning ─────────────────────────────────────────────────
        //
        // ⚠️⚠️ TRE STEG, OCH DE FÅR ALDRIG SLÅS IHOP:
        //   Request       — det som ska betalas finns. Inga pengar har rört sig.
        //   RegisterClaim — betalaren SÄGER att hen betalat. Fortfarande inga pengar.
        //   Confirm       — arrangören har sett pengarna. NU blir det kvitto och bokföring.
        //
        // Vi har ingen Swish-API och ingen callback, så steg två är allt vi vet tills en människa
        // tittat i appen. Ett kvitto vid QR-visning hade varit en urkund på en betalning som
        // kanske aldrig gjordes.

        /// <summary>
        /// POST /umbraco/surface/ClubEvent/StartPayment — begär betalning för HELA sällskapet.
        ///
        /// <para>⚠️ Beloppet räknas på servern ur sällskapets skuld, aldrig ur klienten. Ett postat
        /// belopp hade låtit vem som helst anmäla sig för en krona.</para>
        ///
        /// <para>⚠️ Idempotent i praktiken: finns redan en obetald begäran återanvänds den i
        /// stället för att en andra rad skapas. Två rader hade dubblat skulden och gjort
        /// arrangörens lista obegriplig.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartPayment([FromBody] SignUpRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });
            if (!ctx.CanTakeSwish)
                return Json(new { success = false, message = "Evenemanget kan inte ta betalt — kontakta arrangören." });

            // ⚠️ Numret valideras FÖRE en betalningsrad skapas. Ett felskrivet klubbnummer får
            // SwishQrCodeGenerator att kasta, och då hade raden legat kvar som en skuld utan väg
            // att betala den. Säg i stället vad som är fel, med en gång.
            if (!SwishQrCodeGenerator.IsValidSwishNumber(ctx.SwishNumber))
                return Json(new
                {
                    success = false,
                    message = "Swish-numret hos arrangören ser inte giltigt ut — kontakta klubben.",
                });

            var roster = await _participation.BuildRosterAsync(ctx);
            var party = BuildPartyWithPayments(roster, me, ctx.EventId);
            if (party.Self == null)
                return Json(new { success = false, message = "Du är inte anmäld till evenemanget." });
            if (party.RemainingToRequest <= 0)
                return Json(new { success = false, message = "Det finns inget kvar att betala." });

            var issuer = _issuers.ResolveForEvent(ctx.EventId);
            if (issuer == null)
                return Json(new { success = false, message = "Arrangören går inte att avgöra — kontakta klubben." });

            // ⚠⚠ INGEN BOKFÖRINGSGRIND HÄR, och det är en rättelse. Fram till 2026-09-19 frågade
            // den här raden liggaren om räkenskapsåret och vägrade visa Swish-koden när det saknades.
            // Resonemanget var att `Confirm` ändå skulle vägra — men det gör den inte längre:
            // bokföring är opt-in (se LedgerPostingService.DecidePosting), och de flesta klubbar
            // bokför någon annanstans.
            //
            // Och även om de inte gjorde det: en grind här stoppar inte pengarna, bara
            // REGISTRERINGEN av dem. Klubben swishar vid sidan om och vi vet ingenting. En medlem
            // som ska betala har dessutom inget med föreningens bokföring att göra — att möta hen
            // med "lägg upp räkenskapsåret först" är att visa någon annans problem för fel person.

            // Mina öppna begäranden: varken påstådda, bekräftade eller makulerade.
            var open = _payments
                .ForSource(HpskSite.Models.Ledger.LedgerSourceType.Event, ctx.EventId)
                .Where(p => p.PayerMemberId == me
                            && p.VoidedUtc is null && p.ConfirmedUtc is null && p.ClaimedUtc is null)
                .ToList();

            // ⚠️ Återanvänd den som redan gäller rätt belopp. En ny rad per klick hade blivit fem
            // rader för fem otåliga tryck, och arrangörens lista hade sett fem gånger för lång ut.
            var existing = open.FirstOrDefault(p => p.Amount == party.RemainingToRequest);

            // ⚠️⚠️ OCH MAKULERA DE SOM GÄLLER FEL BELOPP. Lägger Hugo till en gäst efter att koden
            // visats växer skulden från 270 till 450 — utan det här ligger BÅDA kvar, och
            // `Completeness` rapporterar två förväntade betalningar på 720 för ett sällskap som är
            // skyldigt 450. En verifikationsliggare raderar ingenting, så raden makuleras med ett
            // skäl i stället för att tas bort.
            foreach (var stale in open.Where(p => p.Amount != party.RemainingToRequest))
                _payments.Void(stale.Id, me, "Sällskapet ändrades — ersatt av en ny begäran.");

            int paymentId;
            if (existing != null) paymentId = existing.Id;
            else
            {
                var member = _memberService.GetById(me);
                var id = _payments.Request(new HpskSite.Models.Ledger.LedgerPayment
                {
                    IssuerType = issuer.Value.Type,
                    IssuerId = issuer.Value.Id,
                    SourceType = HpskSite.Models.Ledger.LedgerSourceType.Event,
                    SourceId = ctx.EventId,
                    PayerMemberId = me,
                    PayerName = member?.Name ?? $"Medlem {me}",
                    Amount = party.RemainingToRequest,
                    Method = HpskSite.Models.Ledger.LedgerPaymentMethod.Swish,
                });
                if (id is null)
                    return Json(new { success = false, message = "Betalningen kunde inte skapas." });
                paymentId = id.Value;
            }

            return Json(new
            {
                success = true,
                paymentId,
                amount = party.RemainingToRequest,
                swishNumber = ctx.SwishNumber,
                reference = PaymentReference(ctx, paymentId),
                // ⚠️⚠️ DJUPLÄNKEN OCH QR-KODEN ÄR OLIKA PAYLOADS. `swish://payment?data=` vill ha
                // JSON; QR-koden vill ha C-formatet. Byter man plats på dem svarar appen
                // "Felaktig länk" — det står i SwishQrCodeGenerator och har redan kostat en gång.
                // Djuplänken är för den som betalar PÅ telefonen; QR:en för den som har appen i en
                // annan enhet.
                appUrl = SwishQrCodeGenerator.GetSwishAppUrl(
                    ctx.SwishNumber, SwishAmount(party.RemainingToRequest), PaymentReference(ctx, paymentId)),
            });
        }

        // Formatreglerna bor i EventPaymentFormat — se den klassen för varför de inte är privata
        // metoder här. Båda har redan kraschat en gång.
        private static string SwishAmount(decimal amount) => EventPaymentFormat.Amount(amount);

        private static string PaymentReference(ClubEventContext ctx, int paymentId)
            => EventPaymentFormat.Reference(ctx.EventName, paymentId);

        /// <summary>
        /// GET /umbraco/surface/ClubEvent/GetPaymentQr — Swish-QR:en för en betalning.
        ///
        /// <para>⚠️ Bilden byggs på SERVERN ur betalningens egen rad. En QR som klienten satte ihop
        /// av nummer och belopp hade gått att ändra i webbläsaren, och pengarna hamnat någon
        /// annanstans utan att något sa ifrån.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPaymentQr(int eventId, int paymentId)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return NotFound();

            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null || !ctx.CanTakeSwish) return NotFound();

            var row = _payments
                .ForSource(HpskSite.Models.Ledger.LedgerSourceType.Event, eventId)
                .FirstOrDefault(p => p.Id == paymentId);
            if (row == null) return NotFound();

            // ⚠️ Betalaren, eller en funktionär. Utan kontrollen kunde vem som helst hämta en QR
            // för någon annans betalning — ofarligt i sig, men det är en annans belopp och namn.
            if (row.PayerMemberId != me && !await _participation.CanManageAsync(ctx, me))
                return NotFound();

            try
            {
                var png = SwishQrCodeGenerator.GeneratePng(
                    ctx.SwishNumber, SwishAmount(row.Amount), PaymentReference(ctx, row.Id));
                return File(png, "image/png");
            }
            catch (ArgumentException ex)
            {
                // Ett ogiltigt klubbnummer är ett KONFIGURATIONSFEL, inte ett fel i begäran.
                _logger.LogWarning(ex,
                    "Swish-QR kunde inte skapas för evenemang {EventId}: numret {Number} är ogiltigt.",
                    eventId, ctx.SwishNumber);
                return NotFound();
            }
        }

        /// <summary>
        /// POST /umbraco/surface/ClubEvent/ClaimPayment — "jag har betalat".
        ///
        /// <para><b>⚠️⚠️ DET HÄR ÄR INTE PENGAR</b> och får aldrig utfärda ett kvitto eller bokföra.
        /// Se <c>LedgerPaymentService.RegisterClaim</c>.</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ClaimPayment([FromBody] PaymentRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            // ⚠️ Betalningen måste tillhöra DET HÄR evenemanget OCH den inloggade. Utan båda
            // kontrollerna kunde ett postat id kvittera någon annans betalning.
            var mine = _payments
                .ForSource(HpskSite.Models.Ledger.LedgerSourceType.Event, ctx.EventId)
                .FirstOrDefault(p => p.Id == (request?.PaymentId ?? 0) && p.PayerMemberId == me);
            if (mine == null) return Json(new { success = false, message = "Betalningen hittades inte." });

            var ok = _payments.RegisterClaim(mine.Id, me);
            return Json(ok
                ? new { success = true, message = "Tack! Arrangören stämmer av betalningen." }
                : new { success = false, message = "Betalningen är redan kvitterad eller avslutad." });
        }

        /// <summary>
        /// POST /umbraco/surface/ClubEvent/ConfirmPayment — arrangören har sett pengarna.
        ///
        /// <para><b>Nu</b> blir det pengar, kvitto och verifikation. Grinden är
        /// <c>CanManageAsync</c>: den som håller uppropet är den som ser Swish-appen.</para>
        ///
        /// <para>⚠️ Bokföringen kan VÄGRA (stängt räkenskapsår, saknad kontoroll), och då ska
        /// ingenting ha hänt — varken kvitto eller bekräftad betalning. Ordningen ligger i
        /// <c>LedgerPaymentService.Confirm</c>; här handlar det bara om att svara ärligt om vad som
        /// gick fel, i stället för ett "kunde inte spara".</para>
        /// </summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ConfirmPayment([FromBody] PaymentRequest request)
        {
            int me = await CurrentMemberIdAsync();
            if (me <= 0) return Json(new { success = false, message = "Du måste vara inloggad." });

            var ctx = _participation.GetEventContext(request?.EventId ?? 0);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            // ⚠️ Betalningen måste tillhöra DET HÄR evenemanget — annars kunde ett postat id
            // bekräfta en betalning i en annan klubbs liggare.
            var row = _payments
                .ForSource(HpskSite.Models.Ledger.LedgerSourceType.Event, ctx.EventId)
                .FirstOrDefault(p => p.Id == (request?.PaymentId ?? 0));
            if (row == null) return Json(new { success = false, message = "Betalningen hittades inte." });

            DateTime? when = null;
            if (!string.IsNullOrWhiteSpace(request?.PaymentDate))
            {
                if (!DateTime.TryParse(request.PaymentDate, out var parsed))
                    return Json(new { success = false, message = "Betalningsdatumet gick inte att läsa. Ingenting bokfördes." });
                when = parsed.Date;
            }

            var result = _payments.Confirm(row.Id, me, when, request?.ActualAmount);
            if (result.Error != null)
                return Json(new { success = false, message = result.Error });

            // ⚠⚠ SÄG VAD SOM FAKTISKT HÄNDE. "Betalningen är bokförd" var osant för varje
            // förening som inte bokför hos oss — pengarna är mottagna och kvitterade, men ingen
            // verifikation skrevs. Ett kvitto på mottagen betalning är fullt giltigt ändå.
            // ⚠️ SkipReason != null betyder att någon som RÄKNAR med bokföring inte fick den.
            // Det måste synas, annars är det en utebliven verifikation ingen upptäcker.
            var message = result.JournalEntryId.HasValue
                ? "Betalningen är mottagen och bokförd."
                : result.PostingSkippedReason == null
                    ? "Betalningen är mottagen och kvitterad."
                    : "Betalningen är mottagen och kvitterad, men INTE bokförd: "
                      + result.PostingSkippedReason
                      + " Den ligger kvar i listan över betalningar att bokföra.";

            return Json(new
            {
                success = true,
                message,
                posted = result.JournalEntryId.HasValue,
                postingSkippedReason = result.PostingSkippedReason,
            });
        }

        /// <summary>
        /// GET /umbraco/surface/ClubEvent/GetPayments — arrangörens avprickningslista.
        ///
        /// <para>⚠️ Den ersätter de Pending-fakturor som är arbetslistan idag, och därför måste den
        /// skilja <b>påstådd</b> från <b>bekräftad</b>. Slås de ihop kan arrangören inte se vilka
        /// som faktiskt betalat, och listan är inte längre en kontroll.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetPayments(int eventId)
        {
            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) return Json(new { success = false, message = "Evenemanget hittades inte." });

            int me = await CurrentMemberIdAsync();
            if (!await _participation.CanManageAsync(ctx, me))
                return Json(new { success = false, message = "Åtkomst nekad." });

            var rows = _payments.ForSource(HpskSite.Models.Ledger.LedgerSourceType.Event, eventId);
            var (expected, settled, outstanding) =
                _payments.Completeness(HpskSite.Models.Ledger.LedgerSourceType.Event, eventId);

            return Json(new
            {
                success = true,
                expected,
                settled,
                outstanding,
                payments = rows.Select(p => new
                {
                    id = p.Id,
                    payerMemberId = p.PayerMemberId,
                    payerName = p.PayerName,
                    amount = p.Amount,
                    actualAmount = p.ActualAmount,
                    // ⚠️ Tre skilda tillstånd, aldrig en boolean. "Väntar" och "påstådd" är olika
                    // arbetsuppgifter för arrangören: den ena ska påminnas, den andra stämmas av.
                    claimed = p.ClaimedUtc,
                    confirmed = p.ConfirmedUtc,
                    voided = p.VoidedUtc,
                    receiptId = p.ReceiptId,
                })
            });
        }

        /// <summary>
        /// Beskedet när någon inte får anmäla sig.
        ///
        /// <para>⚠️ EN plats, för meddelandet låg i två kopior som båda påstod "medlemmar i
        /// {klubben}" — vilket blev direkt osant i samma stund publiknivån gick att ändra. Ett
        /// felmeddelande som beskriver en regel som inte längre gäller skickar arrangören att leta
        /// efter fel sak.</para>
        /// </summary>
        private static string NotEligibleMessage(ClubEventContext ctx)
            => "Anmälan är öppen för "
             + HpskSite.Models.EventAudience.Phrase(ctx.Audience, ctx.OwnerName, ctx.IsRegionOwned)
             + ".";

        /// <summary>Decodes the poster token. A dead token is the common case (the event has passed),
        /// so it gets its own message rather than a bare "ogiltig länk".</summary>
        private ClubEventContext? ResolveCheckInToken(string? token, out string message)
        {
            message = "";
            if (string.IsNullOrWhiteSpace(token)) { message = "Länken saknar kod."; return null; }

            string payload;
            try
            {
                payload = _attendanceProtector.Unprotect(token);
            }
            catch
            {
                message = "Koden är inte längre giltig. Be en funktionär pricka av dig.";
                return null;
            }

            if (!int.TryParse(payload, out var eventId)) { message = "Ogiltig kod."; return null; }

            var ctx = _participation.GetEventContext(eventId);
            if (ctx == null) { message = "Evenemanget hittades inte."; return null; }
            return ctx;
        }

        public class CheckInRequest
        {
            public string? Token { get; set; }
        }

        // ── Request DTOs ──────────────────────────────────────────────
        public class SignUpRequest
        {
            public int EventId { get; set; }

            /// <summary>Kryssade medlemmen "jag behöver låna klubbvapen"?</summary>
            public bool LoanWeapon { get; set; }

            /// <summary>
            /// Id på den prisrad medlemmen valde. Tomt när evenemanget saknar avgift eller bara har
            /// ett pris — ett enda pris väljer sig självt.
            /// </summary>
            public string? PriceId { get; set; }

            /// <summary>Önskat vapen. <b>0 = vilket som helst</b> — nybörjarens svar.</summary>
            public int LoanFirearmId { get; set; }

            /// <summary>Only honoured for a functionary cancelling on someone's behalf.</summary>
            public int MemberId { get; set; }
            public string? Note { get; set; }
        }

        /// <summary>En anhörig eller gäst utan konto, anmäld på den inloggade medlemmens ansvar.</summary>
        public class GuestRequest
        {
            public int EventId { get; set; }

            /// <summary>Gästens namn. Det enda vi lagrar om personen — den ansvariga medlemmen är
            /// kontaktvägen.</summary>
            public string? Name { get; set; }

            /// <summary>Prisraden gästen hör till. <b>Krävs när evenemanget har flera priser</b> —
            /// det är just då "Vuxen / Barn 7-15 / Under 7 år" betyder något.</summary>
            public string? PriceId { get; set; }

            /// <summary>Radens id vid avbokning av en enskild gäst. Ett namn duger inte: två gäster
            /// kan heta likadant.</summary>
            public int ParticipantId { get; set; }

            /// <summary>
            /// Medlemmen gästen hör till. Tomt = den inloggade, vilket är normalfallet.
            ///
            /// <para>⚠️ Att peka ut NÅGON ANNAN kräver att man får administrera evenemanget — det
            /// är funktionären i disken som lägger till frun som kom med Hugo. Utan grinden kunde
            /// vem som helst hänga en gäst, och därmed en avgift, på en främling.</para>
            /// </summary>
            public int GuestOfMemberId { get; set; }
        }

        /// <summary>Betalningssteget. <b>Inget belopp</b> — det räknas alltid på servern, annars
        /// hade vem som helst kunnat anmäla sig för en krona.</summary>
        public class PaymentRequest
        {
            public int EventId { get; set; }
            public int PaymentId { get; set; }

            /// <summary>Arrangörens bekräftelse: vad som faktiskt kom in, när det skiljer sig.
            /// Null = ingen avvikelse.</summary>
            public decimal? ActualAmount { get; set; }

            /// <summary>Betalningsdagen, när arrangören bokför i efterhand.</summary>
            public string? PaymentDate { get; set; }
        }

        public class AttendanceRequest
        {
            public int EventId { get; set; }
            public int MemberId { get; set; }

            /// <summary>
            /// Deltagarradens id. <b>Enda sättet att peka ut en gäst</b>, som inte har något
            /// medlems-id — och det uppropet skickar för varje rad, så medlemmar och gäster går
            /// samma väg. Vinner över <see cref="MemberId"/> när det är satt.
            /// </summary>
            public int ParticipantId { get; set; }

            /// <summary>Present / Absent / Excused, or empty to clear.</summary>
            public string? Status { get; set; }
            public string? Note { get; set; }
        }

        // ═════════════════════════════════════════════════════════════════════════════════════
        // ENGÅNGSMIGRERING: fritextavgiften (feeAmount) → det debiterbara talet (eventFee)
        // ═════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// <c>GET /umbraco/surface/ClubEvent/MigrateEventFee</c> — flyttar den gamla fritextade
        /// <c>feeAmount</c> till den numeriska <c>eventFee</c>. Sajtadmin.
        ///
        /// <para><b>⚠️⚠️ TORRKÖRNING SOM STANDARD.</b> Utan <c>?apply=true</c> skrivs ingenting —
        /// den rapporterar bara vad som SKULLE hända. Prod-datat går inte att läsa i förväg, och
        /// fältets platshållare har i åratal inbjudit till "100 kr" och "Gratis för juniorer".
        /// Läs listan över otolkbara värden INNAN du kör skarpt, och innan någon raderar
        /// <c>feeAmount</c> — en borttagen doctype-egenskap tar sitt data med sig, oåterkalleligt.</para>
        ///
        /// <para><b>⚠️ GÅR VIA <see cref="IContentService"/>, ALDRIG VIA SQL.</b> En direktskrivning
        /// i <c>umbracoPropertyData</c> uppdaterar inte den publicerade cachen, så appen hade
        /// fortsatt servera de gamla värdena tills någon publicerade om varje nod — och ingenting
        /// hade sagt ifrån.</para>
        ///
        /// <para><b>⚠️ PUBLICERAR BARA DET SOM REDAN VAR PUBLICERAT.</b> Att publicera ett utkast
        /// som sidoeffekt av en migrering gör ett opublicerat evenemang publikt. Samma regel som
        /// <c>RegistrationClubPropagationService</c> följer.</para>
        ///
        /// <para>⚠️ Skriver aldrig över ett <c>eventFee</c> som redan har ett värde — den som satt
        /// det för hand har sett båda fälten och valt.</para>
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> MigrateEventFee(bool apply = false)
        {
            if (!await _auth.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Endast sajtadministratorer." });

            var type = Services.ContentTypeService?.Get(ClubEvents.EventAlias);
            if (type == null)
                return Json(new { success = false, message = $"Doctypen {ClubEvents.EventAlias} hittades inte." });

            // Egenskapen maste finnas INNAN nagot skrivs. SetValue pa en saknad egenskap ar en TYST
            // no-op, sa utan kontrollen rapporterar migreringen lyckat och har inte gjort nagot.
            if (!type.PropertyTypeExists(EventPrices.Property))
                return Json(new
                {
                    success = false,
                    message = $"Doctypen saknar egenskapen '{EventPrices.Property}' (Textarea). "
                            + "Lagg till den i backoffice forst - annars skriver migreringen ingenting."
                });

            var hasLegacyFee = type.PropertyTypeExists(ClubEvents.FeeProperty);

            var migrated = new List<object>();
            var needsHuman = new List<object>();
            var skipped = 0;
            var pageIndex = 0L;
            long total;

            do
            {
                var page = Services.ContentService!.GetPagedOfType(type.Id, pageIndex, 200, out total, null);
                foreach (var node in page)
                {
                    // Redan migrerat? Ror det aldrig - listan kan ha redigerats for hand efterat.
                    var current = EventPrices.Parse(node.GetValue<string>(EventPrices.Property));
                    if (current.Rows.Count > 0) { skipped++; continue; }
                    if (current.Unreadable)
                    {
                        needsHuman.Add(new { id = node.Id, name = node.Name, raw = "(eventPrices)",
                            reason = "Prisraderna gar inte att lasa - kontrollera innehallet for hand." });
                        continue;
                    }

                    decimal amount;
                    string source;

                    // PRECEDENS: eventFee forst. Den sattes av forra migreringen ur samma fritext
                    // och ar redan ett rent tal, sa den behover ingen tolkning.
                    var legacyFee = hasLegacyFee ? node.GetValue<decimal?>(ClubEvents.FeeProperty) : null;
                    var raw = node.GetValue<string>("feeAmount");

                    if (legacyFee.HasValue)
                    {
                        amount = legacyFee.Value;
                        source = $"eventFee {legacyFee.Value:0.##}";
                    }
                    else
                    {
                        var parsed = EventFeeMigration.Parse(raw);
                        if (parsed.Outcome == EventFeeMigration.FeeParse.Empty) { skipped++; continue; }
                        if (parsed.Outcome == EventFeeMigration.FeeParse.Unparseable)
                        {
                            needsHuman.Add(new { id = node.Id, name = node.Name, raw, reason = parsed.Reason });
                            continue;
                        }
                        amount = parsed.Amount;
                        source = $"feeAmount \"{raw}\"";
                    }

                    // EN rad, med en neutral etikett. Migreringen hittar ALDRIG pa kategorier -
                    // "Vuxen"/"Barn" ar arrangorens beslut, och att gissa dem ur ett enda tal vore
                    // att uppfinna en prissattning som ingen bestamt.
                    var rows = new[] { new EventPrice("avgift", "Avgift", amount) };

                    migrated.Add(new { id = node.Id, name = node.Name, source, amount });

                    if (!apply) continue;

                    node.SetValue(EventPrices.Property, EventPrices.Serialize(rows));

                    // Publicerat -> spara och publicera om. Utkast -> spara bara: att publicera ett
                    // utkast som sidoeffekt av en migrering gor ett opublicerat evenemang publikt.
                    // SaveAndPublish finns inte i Umbraco 16 - det ar Save + Publish(node, ["*"], -1).
                    Services.ContentService.Save(node);
                    if (node.Published) Services.ContentService.Publish(node, new[] { "*" }, -1);
                }
                pageIndex++;
            }
            while (pageIndex * 200 < total);

            _logger.LogInformation(
                "MigrateEventFee ({Lage}): {Migrerade} flyttade, {Handpalaggning} kraver handpalaggning, {Hoppade} ororda.",
                apply ? "SKARPT" : "torrkorning", migrated.Count, needsHuman.Count, skipped);

            return Json(new
            {
                success = true,
                applied = apply,
                message = apply
                    ? $"{migrated.Count} avgifter flyttade till prisrader. {needsHuman.Count} kraver handpalaggning."
                    : $"TORRKORNING - ingenting skrevs. {migrated.Count} skulle flyttas, "
                      + $"{needsHuman.Count} kraver handpalaggning. Kor om med ?apply=true nar listan ser ratt ut.",
                migrated,
                needsHuman,
                skipped
            });
        }
    }
}
