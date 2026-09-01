using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services
{
    /// <summary>
    /// Bygger ett <see cref="ForeningsintygDocument"/> — utkastet till Polisens blankett
    /// Föreningsintyg PM 551.24 — genom att fylla i REGISTERFÄLTEN ur medlemsregistret, klubben,
    /// styrelseregistret och märkesliggaren.
    ///
    /// <b>Tjänsten fyller bara i. Den kryssar ingenting.</b> §5/§6, vapenuppgifterna,
    /// behovsraderna och skjutskicklighetsdatumet är styrelsens juridiska intygande och skrivs av
    /// den som utfärdar. Aktivitetssammanställningen visas som UNDERLAG intill de rutorna — den
    /// sätter dem aldrig. Skälet är inte försiktighet: en stor del av vårt underlag är
    /// självrapporterat, vilket sammanställningen själv varnar om.
    ///
    /// <b>Ingen återskrivning till registret.</b> Personuppgifterna ligger på det delade
    /// inloggningskontot och en medlem kan tillhöra flera klubbar — en klubbs intygsutfärdande får
    /// inte mutera data en annan klubb förlitar sig på. Saknas ett fält skrivs det ut som en lucka
    /// och listas i <see cref="ForeningsintygDocument.SaknadeRegisterfalt"/>; det är medlemmens eget
    /// ansvar att fylla i, och profilen markerar vilka fält det gäller.
    /// </summary>
    public class ForeningsintygDocumentService
    {
        private readonly IMemberService _memberService;
        private readonly IContentService _contentService;
        private readonly ClubMembershipService _clubMemberships;
        private readonly BoardRoleService _boardRoles;
        private readonly MarkenLedgerService _marken;
        private readonly StandardMedalLedgerService _standardMedals;
        private readonly ILogger<ForeningsintygDocumentService> _logger;

        public ForeningsintygDocumentService(
            IMemberService memberService,
            IContentService contentService,
            ClubMembershipService clubMemberships,
            BoardRoleService boardRoles,
            MarkenLedgerService marken,
            StandardMedalLedgerService standardMedals,
            ILogger<ForeningsintygDocumentService> logger)
        {
            _memberService = memberService;
            _contentService = contentService;
            _clubMemberships = clubMemberships;
            _boardRoles = boardRoles;
            _marken = marken;
            _standardMedals = standardMedals;
            _logger = logger;
        }

        /// <summary>
        /// Ett utkast för (medlem, klubb). <paramref name="activityYear"/> är bara vilket år
        /// aktivitetsunderlaget visas för — det påverkar inga fält på blanketten.
        /// </summary>
        public async Task<ForeningsintygDocument?> BuildDraftAsync(int memberId, int clubId, int activityYear)
        {
            var member = _memberService.GetById(memberId);
            if (member == null) return null;

            var doc = new ForeningsintygDocument
            {
                MemberId = memberId,
                ClubId = clubId,
                ActivityYear = activityYear,
                UnderskriftDatum = DateTime.Today.ToString("yyyy-MM-dd")
            };

            FillPersonal(doc, member);
            FillClub(doc, clubId);
            FillMembershipStart(doc, memberId, clubId);
            FillSignatory(doc, clubId);
            await FillMarkenAsync(doc, memberId);

            doc.SaknadeRegisterfalt = MissingRegisterFields(member, doc);
            return doc;
        }

        // ── Personuppgifter ──────────────────────────────────────────

        private static void FillPersonal(ForeningsintygDocument doc, IMember member)
        {
            doc.Efternamn = Val(member, "lastName");
            doc.Tilltalsnamn = Val(member, "firstName");
            doc.Personnummer = Val(member, "personNumber");
            doc.Adress = Val(member, "address");
            doc.Postnummer = Val(member, "postalCode");
            doc.Ort = Val(member, "city");
            doc.EPostadress = member.Email ?? "";
            // ⚠️ phoneNumber ÄR mobilnumret i den här doctypen och landlinePhone är den fasta.
            // Det finns ingen mobilePhone-egenskap; ett felstavat alias är en tyst no-op.
            doc.TelefonMobil = Val(member, "phoneNumber");
            doc.Telefon = Val(member, "landlinePhone");
        }

        // ── Klubben ──────────────────────────────────────────────────

        /// <summary>
        /// ⚠️ <b>Läser klubbNODEN, inte <c>ClubService</c>.</b> <c>ClubService.GetClubById</c>
        /// returnerar ett tunt <c>ClubInfo</c> utan <c>orgNumber</c>, <c>address</c>,
        /// <c>postalCode</c> och <c>contactPhone</c> — alltså precis de fält blanketten kräver.
        /// Husregeln "använd alltid ClubService för klubbuppslag" gäller NAMNUPPSLAG, inte
        /// adressblocket.
        /// </summary>
        private void FillClub(ForeningsintygDocument doc, int clubId)
        {
            var club = clubId > 0 ? _contentService.GetById(clubId) : null;
            if (club == null || club.ContentType.Alias != "club")
            {
                _logger.LogWarning("Föreningsintyg: klubb {ClubId} kunde inte läsas som en club-nod", clubId);
                return;
            }

            doc.Skytteforening = club.GetValue<string>("clubName") ?? club.Name ?? "";
            doc.Organisationsnummer = club.GetValue<string>("orgNumber") ?? "";
            // Ort på underskriftsraden är ett FÖRSLAG — styrelsen kan skriva under någon annanstans.
            doc.UnderskriftOrt = club.GetValue<string>("city") ?? "";
        }

        // ── Medlem sedan ─────────────────────────────────────────────

        /// <summary>
        /// <c>ClubMembership.MemberSince</c> för (medlem, denna klubb) är auktoritativ — "medlem
        /// sedan" är ett faktum om ett KLUBBMEDLEMSKAP, inte om det delade inloggningskontot, och
        /// intyget utfärdas alltid av en klubb. Den äldre medlemsegenskapen <c>memberSince</c>
        /// skrivs fortfarande på flera ställen och används därför som reserv.
        /// </summary>
        private void FillMembershipStart(ForeningsintygDocument doc, int memberId, int clubId)
        {
            try
            {
                var membership = clubId > 0 ? _clubMemberships.Get(memberId, clubId) : null;
                if (membership?.MemberSince != null)
                {
                    doc.MedlemSedan = membership.MemberSince.Value.ToString("yyyy-MM-dd");
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Föreningsintyg: kunde inte läsa ClubMembership för {MemberId}/{ClubId}", memberId, clubId);
            }

            var member = _memberService.GetById(memberId);
            var fallback = member?.GetValue<DateTime?>("memberSince");
            if (fallback != null && fallback.Value != default)
                doc.MedlemSedan = fallback.Value.ToString("yyyy-MM-dd");
        }

        // ── Underskrift ──────────────────────────────────────────────

        /// <summary>
        /// Ordföranden föreslås som undertecknare — blanketten säger att intyget skrivs under av
        /// ordföranden eller den i styrelsen som utsetts, så det är ett förslag och inte ett beslut.
        ///
        /// ⚠️ <c>BoardRoleService</c> resolvar bara NAMNET; e-post och telefon finns inte där. Måste
        /// hämtas per medlem via <c>IMemberService</c>, annars står blankettens kontaktrader tomma.
        /// </summary>
        private void FillSignatory(ForeningsintygDocument doc, int clubId)
        {
            if (clubId <= 0) return;

            BoardRole? signatory = null;
            try
            {
                var board = _boardRoles.GetBoardMembers(DocumentOwnerType.Club, clubId, boardOnly: true);
                signatory = board.FirstOrDefault(r => r.RoleKey == BoardRoleDefinitions.RoleOrdforande)
                            ?? board.FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Föreningsintyg: kunde inte läsa styrelsen för klubb {ClubId}", clubId);
            }
            if (signatory == null) return;

            doc.BefattningFunktion = signatory.DisplayTitle;

            var person = signatory.MemberId > 0 ? _memberService.GetById(signatory.MemberId) : null;
            if (person == null)
            {
                doc.Namnfortydligande = signatory.MemberName ?? "";
                return;
            }

            var name = $"{Val(person, "firstName")} {Val(person, "lastName")}".Trim();
            doc.Namnfortydligande = string.IsNullOrWhiteSpace(name) ? (person.Name ?? "") : name;
            doc.UnderskriftEPost = person.Email ?? "";
            doc.UnderskriftTelefonMobil = Val(person, "phoneNumber");
            doc.UnderskriftTelefon = Val(person, "landlinePhone");
        }

        // ── Skjutskicklighet ─────────────────────────────────────────

        /// <summary>
        /// Guldmärkeskrysset FÖRESLÅS ur märkesliggaren — det är den enda lagringen; det finns ingen
        /// guldmärkesegenskap på medlemmen.
        ///
        /// ⚠️ <b>Datumet förifylls medvetet INTE.</b> Blanketten vill ha "datum för godkänt
        /// skjutprov", men <c>AwardBadge</c> stämplar <c>AchievedDate</c> med dagens datum även för
        /// ett märke från 1998 — datumet är en bokföringsstämpel och ÅRET är fakta. Året lämnas
        /// därför som underlag till intygaren och datumfältet skrivs in för hand.
        /// </summary>
        private async Task FillMarkenAsync(ForeningsintygDocument doc, int memberId)
        {
            try
            {
                var badges = await _marken.GetBadgesForMemberAsync(memberId, Marken.FamilyPistolskytte);
                var guld = badges.FirstOrDefault(b => b.Level == Marken.LevelGuld
                                                      && b.Status == Marken.StatusVerified);
                if (guld == null) return;

                doc.GuldmarkeSpsf = true;
                doc.GuldmarkeNummer = guld.UniqueNumber ?? "";
                doc.GuldmarkeAr = guld.AchievedYear > 0 ? guld.AchievedYear : null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Föreningsintyg: kunde inte läsa märkesliggaren för {MemberId}", memberId);
            }

            try { doc.SkjutprovForslag = await DeriveSkjutprovAsync(memberId); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Föreningsintyg: kunde inte härleda skjutprovsdatum för {MemberId}", memberId);
            }
        }

        /// <summary>
        /// Underlaget till blankettens "Datum för godkänt skjutprov": den dag guldmärkets fordringar
        /// SENAST uppfylldes, härledd ur guldfodringen. Se <see cref="SkjutprovCandidate"/> för varför
        /// det inte är märkets datum.
        ///
        /// ⚠️ <b>Läser INTE <c>MemberBadgeQualification.Part1Date</c>/<c>Part2Date</c>.</b> De sätts med
        /// <c>??= DateTime.Now</c> när omräkningen körs och är alltså bokföringsstämplar, inte
        /// seriedatum — samma klass av fel som <c>MemberBadge.AchievedDate</c>. Mätt i dev: en rad bär
        /// <c>Part1Date = 2026-05-31</c> medan de kvalificerande guldserierna börjar 2026-03-15 och det
        /// finns kvalificerande serier så sent som 2026-06-07. Datumet härleds därför ur
        /// <c>MarkenSeries.SeriesDate</c>, som är skjutdagen.
        /// </summary>
        private async Task<SkjutprovCandidate> DeriveSkjutprovAsync(int memberId)
        {
            // Det senaste uppfyllda guldfodringsåret. Bara Verified räknas — en ogranskad
            // kvalifikation får inte belägga ett skjutprov på ett myndighetsintyg.
            var quals = await _marken.GetQualificationsForMemberAsync(memberId, Marken.FamilyPistolskytte);
            var latest = quals
                .Where(q => q.Fulfilled && q.Status == Marken.StatusVerified)
                .OrderByDescending(q => q.Year)
                .FirstOrDefault();

            if (latest == null)
            {
                return SkjutprovCandidate.Derive(
                    null, Array.Empty<DateTime>(), Marken.GuldfodringPrecisionSeriesRequired,
                    null, Array.Empty<DateTime>(), Marken.GuldfodringSpeedSeriesRequired,
                    null, null, DateTime.Today);
            }

            int year = latest.Year;

            var precision = await _marken.GetVerifiedQualifyingPrecisionAsync(memberId, year);

            // ⚠️ TILLÄMPNINGSserier, inte varje Speed-serie. Del 2 definieras av SHB 5.1.1.1 pt 2 som
            // tre tillämpningsserier mot B 100 eller 1/6 C 30; en snabbpistolserie är Elits bevis.
            // Samma scoping som MarkenCandidateService — läses den bredare blir datumet fel dag.
            var allSeries = await _marken.GetSeriesForMemberAsync(memberId, year, Marken.FamilyPistolskytte);
            var tillampning = allSeries
                .Where(s => Marken.SeriesDiscipline(s.BadgeFamily, s.SeriesType, s.Target) == Marken.DisciplineTillampning
                            && s.Status == Marken.StatusVerified
                            && string.Equals(s.ClaimedLevel, Marken.LevelGuld, StringComparison.OrdinalIgnoreCase))
                .Select(s => s.SeriesDate)
                .ToList();

            DateTime? medalDate = null;
            if (latest.Part2Source == Marken.PartSourceStandardMedal)
            {
                var awards = await _standardMedals.GetAwardsForMemberAsync(memberId, year);
                var falt = awards.FirstOrDefault(a =>
                    string.Equals(a.Discipline, StandardMedals.Faltskytte, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a.Discipline, StandardMedals.MagnumFalt, StringComparison.OrdinalIgnoreCase));
                medalDate = falt?.CompetitionDate;
            }

            return SkjutprovCandidate.Derive(
                year,
                precision.Select(s => s.SeriesDate),
                Marken.GuldfodringPrecisionSeriesRequired,
                latest.Part2Source,
                tillampning,
                Marken.GuldfodringSpeedSeriesRequired,
                medalDate,
                latest.Part2Note,
                DateTime.Today);
        }

        // ── Luckor ───────────────────────────────────────────────────

        /// <summary>
        /// Registerfält utan användbart värde, med blankettens egen etikett.
        ///
        /// <b>Detta är ingen spärr.</b> Ett intyg kan utfärdas med luckor; listan finns för att den
        /// som skriver under ska se dem INNAN hen skriver under, i stället för att upptäcka dem när
        /// Polisen avvisar intyget. Klubbens uppgifter räknas med, eftersom ett saknat
        /// organisationsnummer inte är medlemmens fel men lika förödande för intyget.
        /// </summary>
        private static List<string> MissingRegisterFields(IMember member, ForeningsintygDocument doc)
        {
            var missing = new List<string>();

            foreach (var field in ForeningsintygFields.Personal)
            {
                if (!field.Required) continue;
                var value = field.Alias == ForeningsintygField.NativeEmail
                    ? member.Email
                    : Val(member, field.Alias);
                if (!ForeningsintygFields.HasUsableValue(field.Alias, value))
                    missing.Add(field.FormLabel);
            }

            if (string.IsNullOrWhiteSpace(doc.Organisationsnummer)) missing.Add("Organisationsnummer (klubben)");
            if (string.IsNullOrWhiteSpace(doc.Skytteforening)) missing.Add("Skytteförening (klubben)");
            if (string.IsNullOrWhiteSpace(doc.MedlemSedan)) missing.Add("Har varit medlem kontinuerligt sedan datum");
            if (string.IsNullOrWhiteSpace(doc.Namnfortydligande)) missing.Add("Namnförtydligande (styrelsen)");

            return missing;
        }

        private static string Val(IMember member, string alias)
        {
            try { return member.GetValue<string>(alias) ?? ""; }
            catch { return ""; }
        }
    }
}
