namespace HpskSite.Models
{
    /// <summary>
    /// Hur starkt underlaget för EN aktivitetspost är. Ordnat från svagast till starkast, och
    /// ordningen är avsiktlig — den används för att sortera och för att visa "starkaste underlaget"
    /// i en sammanställning.
    ///
    /// <b>Varför detta finns:</b> en aktivitetssammanställning är underlag för ett Föreningsintyg,
    /// alltså för en myndighetsprövning. Ett intyg som räknar självrapporterad träning och
    /// funktionärsprickad närvaro i samma siffra är värdelöst — den som läser intyget kan inte veta
    /// vad siffran betyder. Därför bär VARJE post sin sort, och sammanställningen redovisar alltid
    /// fördelningen, aldrig bara en totalsumma.
    /// </summary>
    public enum ActivityEvidence
    {
        /// <summary>
        /// Inget underlag alls. Gäller bara poster som inte räknas — främst ett evenemang där
        /// uppropet aldrig togs. En sådan rad får inte bära en etikett som antyder att någon
        /// registrerat något, för det är precis vad som inte har hänt.
        /// </summary>
        None = -1,

        /// <summary>
        /// Medlemmen har skrivit in posten själv och ingen funktionär har intygat något.
        /// Gäller ALL träningslogg idag — funktionärsverifiering av träningspass är inte byggd
        /// (se sammanställningens <see cref="MemberActivitySummary.Warnings"/>).
        /// </summary>
        SelfReported = 0,

        /// <summary>
        /// Anmälan finns, men inget inskrivet resultat och ingen DNS — deltagandet är alltså inte
        /// styrkt av arrangören, bara av medlemmens egen anmälan.
        /// </summary>
        RegisteredOnly = 1,

        /// <summary>
        /// Medlemmen registrerade sin egen närvaro på plats, typiskt genom att skanna
        /// evenemangets QR-kod. Starkare än ren självrapportering (det skedde på plats och vid
        /// rätt tid) men svagare än ett upprop — ingen funktionär har sett personen.
        /// </summary>
        SelfRegistered = 2,

        /// <summary>En funktionär har prickat av närvaron i uppropet.</summary>
        FunctionaryRecorded = 3,

        /// <summary>
        /// Officiellt inskrivet tävlingsresultat. Det starkaste underlaget vi har: en funktionär
        /// har skrivit in poäng för personen i en tävling som klubben eller kretsen arrangerade.
        /// </summary>
        OfficialResult = 4
    }

    /// <summary>Vilken sorts verksamhet posten är. Skild från <see cref="ActivityEvidence"/>:
    /// sorten är VAD, underlaget är HUR VÄL VI VET.</summary>
    public enum ActivityKind
    {
        /// <summary>Poängsatt träningspass ur träningsloggen.</summary>
        Training = 0,

        /// <summary>0-poäng träning (vittavla / fri övning) — övning utan poäng, men verksamhet.</summary>
        Practice = 1,

        /// <summary>Tävling, egen eller extern.</summary>
        Competition = 2,

        /// <summary>
        /// Träningsmatch. <b>Egen sort, inte en tävling</b> (Stefans besked 2026-09-01) — en
        /// träningsmatch är klubbens interna uppgörelse och hör inte i siffran en styrelse eller en
        /// handläggare läser som "tävlingar".
        ///
        /// ⚠️ Avgörs på <c>TrainingScores.TrainingMatchId</c>, som slår <c>IsCompetition</c>. Raderna
        /// bär båda i verkligheten: mätt i dev har fixturmedlemmen 2026 nio rader med
        /// <c>IsCompetition = 1</c>, varav TVÅ är träningsmatcher, plus sex matcher utan flaggan. Läses
        /// flaggan först hamnar de två i tävlingssiffran.
        /// </summary>
        TrainingMatch = 4,

        /// <summary>
        /// Incheckning på klubbens skjutbana (QR-skanning på plats). Räknas bara när klubben slagit
        /// på det, och bara när den inte beskriver samma tillfälle som en annan post — se
        /// <see cref="MemberActivitySummary.MarkRedundantCheckIns"/>.
        /// </summary>
        RangeCheckIn = 5,

        /// <summary>Klubbens eller kretsens evenemang (städdag, möte, socialt, träningskväll …).</summary>
        Event = 3
    }

    /// <summary>
    /// En rad i aktivitetssammanställningen: ett tillfälle, dess sort och dess underlag.
    /// </summary>
    public class MemberActivityEntry
    {
        /// <summary>Verksamhetens datum — aldrig radens tidsstämpel. Ett upprop taget i januari
        /// för en decemberhändelse är decemberaktivitet.</summary>
        public DateTime Date { get; set; }

        public ActivityKind Kind { get; set; }
        public ActivityEvidence Evidence { get; set; }

        /// <summary>Vad det var, i klartext: tävlingens eller evenemangets namn, eller
        /// träningspassets vapenklass och disciplin.</summary>
        public string Title { get; set; } = "";

        /// <summary>Kompletterande text på samma rad (poäng, serier, plats, frånvaroskäl).</summary>
        public string? Detail { get; set; }

        /// <summary>
        /// Räknas posten som ett aktivitetstillfälle? <b>false betyder inte att raden är ointressant</b>
        /// — en giltig frånvaro på ett obligatoriskt evenemang och ett upprop som aldrig togs måste
        /// SYNAS i sammanställningen, annars ser en lucka ut som frånvaro utan förklaring. Se
        /// <see cref="NotCountedReason"/>.
        /// </summary>
        public bool CountsAsActivity { get; set; } = true;

        /// <summary>Varför posten inte räknas, i klartext. Null när den räknas.</summary>
        public string? NotCountedReason { get; set; }

        /// <summary>
        /// Vapengrupper posten hör till, som gruppKODER ("A", "A_Opt", "B", "C", "R", "M", "L").
        ///
        /// <b>Varför en LISTA och inte ett värde:</b> en tävling kan innehålla flera vapenklasser för
        /// samma skytt — dev-data har anmälningar med både A1 och L_Vet_A på samma tävling — och den
        /// tävlingen är aktivitet i båda grupperna.
        ///
        /// <b>Tom lista = posten hör inte till någon vapengrupp.</b> Gäller evenemang: en städdag är
        /// klubbverksamhet, inte skjutande i en vapengrupp. Ett vapengruppsfilter måste därför säga
        /// att sådana poster faller bort, inte tysta räkna bort dem.
        /// </summary>
        public List<string> WeaponGroups { get; set; } = new();

        /// <summary>Id i källan (träningsrad, tävlingsnod, evenemangsnod) för länkning.</summary>
        public int SourceId { get; set; }

        /// <summary>
        /// Vilken källa <see cref="SourceId"/> är ett id i — <c>"comp"</c> (tävlingsnod),
        /// <c>"training"</c> (rad i TrainingScores) eller <c>"event"</c> (evenemangsnod).
        ///
        /// <b>⚠️ Detta är inte metadata, det är halva nyckeln.</b> En SJÄLVRAPPORTERAD extern tävling
        /// bär träningsradens id medan en av våra egna bär tävlingsnodens — två oberoende
        /// identitetsserier där samma heltal betyder olika saker. Räknas distinkta tävlingar på
        /// <see cref="SourceId"/> ensamt viker en kollision ihop två skilda tävlingar till en och
        /// underrapporterar i ett intyg, tyst. Samma lärdom som <c>SourceTable</c> i
        /// märkessynken.
        /// </summary>
        public string SourceKind { get; set; } = "";

        /// <summary>Den sammansatta källnyckeln. Räkna alltid distinkt på DEN, aldrig på id:t.</summary>
        public string SourceKey => $"{SourceKind}:{SourceId}";

        /// <summary>
        /// Källnyckeln för den post detta är samma tillfälle som. Sätts på en incheckning som
        /// beskriver ett tillfälle en annan post redan bär, så gränssnittet kan peka på vilken.
        /// </summary>
        public string? SameOccasionAs { get; set; }

        public const string SourceKindCompetition = "comp";
        public const string SourceKindTraining = "training";
        public const string SourceKindEvent = "event";
        public const string SourceKindRangeCheckIn = "checkin";

        /// <summary>Sant för evenemang som klubben märkt som obligatoriska — styrelsens
        /// intygsbeslut vilar särskilt på dessa.</summary>
        public bool IsMandatoryEvent { get; set; }

        /// <summary>Svensk etikett för underlagets styrka, samma språk på varje yta.</summary>
        public string EvidenceLabel => EvidenceDisplay(Evidence);

        /// <summary>Svensk etikett för sorten.</summary>
        public string KindLabel => KindDisplay(Kind);

        public static string EvidenceDisplay(ActivityEvidence e) => e switch
        {
            ActivityEvidence.None => "Inget underlag",
            ActivityEvidence.SelfReported => "Självrapporterad",
            ActivityEvidence.RegisteredOnly => "Anmäld, resultat saknas",
            ActivityEvidence.SelfRegistered => "Självregistrerad på plats",
            ActivityEvidence.FunctionaryRecorded => "Funktionärsregistrerad",
            ActivityEvidence.OfficialResult => "Officiellt resultat",
            _ => ""
        };

        public static string KindDisplay(ActivityKind k) => k switch
        {
            ActivityKind.Training => "Träning",
            ActivityKind.Practice => "0-poäng träning",
            ActivityKind.Competition => "Tävling",
            ActivityKind.TrainingMatch => "Träningsmatch",
            ActivityKind.RangeCheckIn => "Incheckning på banan",
            ActivityKind.Event => "Evenemang",
            _ => ""
        };
    }

    /// <summary>
    /// ETT svar per (medlem, år): all verksamhet vi känner till, varje post med sin sort och sitt
    /// underlag, plus de summor ett Föreningsintyg behöver.
    ///
    /// <b>Byggd som ett enda svar med flit.</b> Både Min sida och intygsgenereringen läser den här
    /// sammanställningen. Två läsvägar över samma sak blir förr eller senare två svar som säger
    /// emot varandra — och här är svaret ett myndighetsunderlag, så det får inte hända.
    /// </summary>
    public class MemberActivitySummary
    {
        public int MemberId { get; set; }
        public string MemberName { get; set; } = "";

        /// <summary>Verksamhetsåret. Kalenderår — inget brutet räkenskapsår, för det är kalenderår
        /// polismyndigheten och klubbarna talar om.</summary>
        public int Year { get; set; }

        /// <summary>Alla poster, nyast först.</summary>
        public List<MemberActivityEntry> Entries { get; set; } = new();

        /// <summary>
        /// Antal aktivitetsDAGAR, inte antal poster. <b>Det är den här siffran ett intyg ska bära.</b>
        /// Två träningspass med olika vapen samma kväll är ett besök på banan, inte två — och en
        /// tvådagarstävling är en tävling men två dagar. Antal poster finns kvar i
        /// <see cref="CountedEntries"/> för den som vill se båda.
        /// </summary>
        public int ActivityDays { get; set; }

        /// <summary>Antal poster som räknas (<see cref="MemberActivityEntry.CountsAsActivity"/>).</summary>
        public int CountedEntries { get; set; }

        /// <summary>Antal poster per sort — bara de som räknas.</summary>
        public Dictionary<ActivityKind, int> ByKind { get; set; } = new();

        /// <summary>Antal poster per underlagsstyrka — bara de som räknas. Den här fördelningen är
        /// hela poängen; en total utan den går inte att tolka.</summary>
        public Dictionary<ActivityEvidence, int> ByEvidence { get; set; } = new();

        /// <summary>Distinkta tävlingar (inte anmälningsrader) som räknas. En skytt som anmält sig
        /// i tre vapenklasser till samma tävling har deltagit i EN tävling.
        /// <b>Träningsmatcher ingår INTE</b> — de har sin egen siffra.</summary>
        public int Competitions { get; set; }

        /// <summary>Träningsmatcher som räknas. Egen siffra, aldrig inbakad i
        /// <see cref="Competitions"/>.</summary>
        public int TrainingMatches { get; set; }

        /// <summary>Obligatoriska evenemang under året som medlemmen var närvarande på.</summary>
        public int MandatoryEventsAttended { get; set; }

        /// <summary>Obligatoriska evenemang under året där närvaro INTE är registrerad — frånvarande,
        /// giltig frånvaro, eller upprop som aldrig togs. Styrelsens intygsbeslut hänger på den här.</summary>
        public int MandatoryEventsMissed { get; set; }

        /// <summary>
        /// Vapengrupper medlemmen har verksamhet i under året — <b>före filtrering</b>, så väljaren
        /// inte tappar alternativ i samma stund ett filter läggs på.
        /// </summary>
        public List<string> WeaponGroupsAvailable { get; set; } = new();

        /// <summary>De vapengrupper som faktiskt filtrerades på. Tom = ingen filtrering.</summary>
        public List<string> WeaponGroupFilter { get; set; } = new();

        /// <summary>
        /// Antal poster ett aktivt filter uteslöt <b>för att de saknar vapengrupp</b> — i praktiken
        /// evenemangen. Finns för att gränssnittet ska kunna SÄGA det: en aktivitetssiffra som tappat
        /// tio evenemang utan att någon nämner det är en siffra som betyder något annat än läsaren tror.
        /// </summary>
        public int ExcludedWithoutWeaponGroup { get; set; }

        /// <summary>
        /// Sådant den som läser sammanställningen MÅSTE veta för att inte övertolka den — att ingen
        /// träning är verifierad, att upprop saknas, att resultat inte skrivits in. Tomt är ett
        /// giltigt svar, men en tom lista när det finns svagt underlag är en bugg.
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>
        /// Räknar ihop en färdig postlista. Ren funktion — inga databasanrop — så aggregeringen kan
        /// A/B-testas utan Umbraco.
        /// </summary>
        public static MemberActivitySummary From(
            int memberId, string memberName, int year, IEnumerable<MemberActivityEntry> entries,
            IEnumerable<string>? weaponGroupFilter = null)
        {
            var everything = entries.ToList();

            // Väljarens alternativ tas FÖRE filtreringen — annars försvinner de grupper man just
            // filtrerade bort ur listan och man kan inte välja tillbaka dem.
            var available = everything
                .SelectMany(e => e.WeaponGroups)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct()
                .OrderBy(g => g, StringComparer.Ordinal)
                .ToList();

            var filter = (weaponGroupFilter ?? Enumerable.Empty<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            int excludedWithoutGroup = 0;
            if (filter.Count > 0)
            {
                excludedWithoutGroup = everything.Count(e => e.WeaponGroups.Count == 0);
                everything = everything
                    .Where(e => e.WeaponGroups.Any(g => filter.Contains(g, StringComparer.OrdinalIgnoreCase)))
                    .ToList();
            }

            // ⚠️ MÅSTE ske innan något räknas: en incheckning som beskriver samma tillfälle som en
            // annan post får inte bli en egen post.
            MarkRedundantCheckIns(everything);

            var all = everything.OrderByDescending(e => e.Date).ThenBy(e => e.Title).ToList();
            var counted = all.Where(e => e.CountsAsActivity).ToList();

            var summary = new MemberActivitySummary
            {
                MemberId = memberId,
                MemberName = memberName,
                Year = year,
                Entries = all,
                CountedEntries = counted.Count,
                ActivityDays = counted.Select(e => e.Date.Date).Distinct().Count(),
                ByKind = counted.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()),
                ByEvidence = counted.GroupBy(e => e.Evidence).ToDictionary(g => g.Key, g => g.Count()),
                // Distinkt på den SAMMANSATTA nyckeln. Se SourceKind: en extern tävlings id kommer
                // ur en annan identitetsserie än en egen tävlings, och de kan kollidera.
                Competitions = counted.Where(e => e.Kind == ActivityKind.Competition)
                                      .Select(e => e.SourceKey).Distinct().Count(),
                TrainingMatches = counted.Count(e => e.Kind == ActivityKind.TrainingMatch),
                MandatoryEventsAttended = counted.Count(e => e.Kind == ActivityKind.Event && e.IsMandatoryEvent),
                MandatoryEventsMissed = all.Count(e =>
                    e.Kind == ActivityKind.Event && e.IsMandatoryEvent && !e.CountsAsActivity),
                WeaponGroupsAvailable = available,
                WeaponGroupFilter = filter,
                ExcludedWithoutWeaponGroup = excludedWithoutGroup
            };

            summary.Warnings = BuildWarnings(all, counted);

            // Ett filter som gömmer poster måste SÄGA det. Utan raden läser man aktivitetsdagarna som
            // medlemmens hela verksamhet, när de i själva verket är verksamheten i en vapengrupp.
            if (filter.Count > 0)
            {
                var msg = $"Filtrerat på vapengrupp {string.Join(", ", filter)}.";
                if (excludedWithoutGroup > 0)
                    msg += $" {excludedWithoutGroup} poster utan vapengrupp (evenemang) visas inte och " +
                           "räknas inte in i siffrorna.";
                summary.Warnings.Insert(0, msg);
            }

            return summary;
        }

        /// <summary>
        /// Varningarna. Varje varning motsvarar en konkret svaghet i underlaget som finns i listan —
        /// ingen varning skrivs "för säkerhets skull", och ingen svaghet får sakna varning.
        /// </summary>
        private static List<string> BuildWarnings(
            List<MemberActivityEntry> all, List<MemberActivityEntry> counted)
        {
            var w = new List<string>();

            // ⚠️ VARNINGARNA MÅSTE GÅ IHOP MED BRICKAN "Självrapporterad: N".
            //
            // Första utsågan varnade bara om träningspassen, medan brickan räknade ALLA
            // självrapporterade poster. Rapporterat 2026-09-01: brickan sa 86 och varningen 68, vilket
            // läser som att systemet räknar fel. Båda var sanna — 86 = 68 träningsposter + 18
            // egenrapporterade tävlingsresultat — men bara den ena stod på skärmen.
            //
            // Därför delas de självrapporterade posterna nu i sina två sorter, och delarna summerar
            // synligt till brickans tal. Lägg aldrig till en underlagssort utan att den täcks här.
            int selfReportedTraining = counted.Count(e =>
                (e.Kind == ActivityKind.Training || e.Kind == ActivityKind.Practice
                 || e.Kind == ActivityKind.TrainingMatch)
                && e.Evidence == ActivityEvidence.SelfReported);
            if (selfReportedTraining > 0)
                w.Add($"{selfReportedTraining} poster från träningsloggen (träning, 0-poängspass och " +
                      "träningsmatcher) är självrapporterade och inte intygade av någon funktionär.");

            int selfReportedCompetitions = counted.Count(e =>
                e.Kind == ActivityKind.Competition && e.Evidence == ActivityEvidence.SelfReported);
            if (selfReportedCompetitions > 0)
                w.Add($"{selfReportedCompetitions} tävlingsresultat är egenrapporterade i " +
                      "träningsloggen — de är inte inskrivna av en arrangör.");

            int registeredOnly = counted.Count(e => e.Evidence == ActivityEvidence.RegisteredOnly);
            if (registeredOnly > 0)
                w.Add($"{registeredOnly} tävlingar har anmälan men inget inskrivet resultat — " +
                      "deltagandet är inte styrkt av arrangören.");

            int selfRegistered = counted.Count(e => e.Evidence == ActivityEvidence.SelfRegistered);
            if (selfRegistered > 0)
                w.Add($"{selfRegistered} närvaroposter är självregistrerade på plats (QR) och inte " +
                      "prickade av en funktionär.");

            int noRollCall = all.Count(e =>
                e.Kind == ActivityKind.Event && !e.CountsAsActivity
                && e.NotCountedReason == NotRecordedReason);
            if (noRollCall > 0)
                w.Add($"{noRollCall} evenemang har anmälan men inget upprop — de räknas inte, och " +
                      "det betyder inte att medlemmen var frånvarande.");

            return w;
        }

        /// <summary>Skälstext för "uppropet togs aldrig". Konstant eftersom varningsbyggaren
        /// räknar just den här sorten och en omformulering annars tystar varningen.</summary>
        public const string NotRecordedReason = "Uppropet togs aldrig — närvaron är okänd";

        /// <summary>
        /// Doctype-egenskapen på <c>club</c> som slår på "incheckning på banan räknas som aktivitet"
        /// (True/False, valfri, standard AV).
        ///
        /// Standard av med flit: en deploy ska inte ändra någon klubbs aktivitetssiffror i
        /// deploy-ögonblicket, allra minst siffror som är underlag för ett intyg.
        ///
        /// ⚠️ <b>Utan egenskapen är <c>SetValue</c> en TYST no-op</b> — switchen hade sett ut att
        /// fungera och återgått vid nästa laddning. Skrivvägen vägrar därför och namnger egenskapen.
        /// </summary>
        public const string ClubActivityFromRangeCheckInProperty = "activityFromRangeCheckIn";

        /// <summary>Skälstexten för en incheckning som beskriver ett tillfälle en annan post redan bär.</summary>
        public const string RedundantCheckInReason = "Samma tillfälle som en annan post samma dag";

        /// <summary>Skälstexten för en andra incheckning samma dag.</summary>
        public const string DuplicateCheckInReason = "Redan incheckad samma dag";

        /// <summary>
        /// Nollar räkningen för incheckningar som beskriver ett tillfälle någon annan post redan bär.
        ///
        /// <b>Problemet:</b> någon skannar QR-koden på banan, skjuter en träningsmatch och loggar den.
        /// Det är ETT tillfälle men två poster. Slår klubben på incheckning som aktivitet blir varje
        /// sådan dag dubbelräknad i postantalet.
        ///
        /// <b>Tre lager, i den ordningen:</b>
        /// <list type="number">
        /// <item><see cref="ActivityDays"/> är redan immun — den räknar distinkta DATUM, så en
        /// incheckning och en tävling samma dag är en dag. Det var skälet att rubriksiffran blev dagar
        /// och inte poster.</item>
        /// <item><b>Explicit länk vinner.</b> <c>RangeActivitySession.LinkedCompetitionId</c> /
        /// <c>LinkedTrainingScoreId</c> pekar ut vilket tillfälle passet hör till. ⚠️ Kolumnerna finns
        /// men <b>ingenting skriver dem i dag</b> — lagret är förberett, inte i drift, och tjänsten
        /// sätter <see cref="MemberActivityEntry.SameOccasionAs"/> när de börjar fyllas.</item>
        /// <item><b>Samma dag</b> är den regel som faktiskt arbetar: en incheckning räknas bara när den
        /// är dagens enda räknade post.</item>
        /// </list>
        ///
        /// <b>Raden försvinner aldrig.</b> Den visas med skälet utskrivet — en incheckning som tystnat
        /// bort ser ut som saknad data, och det är just incheckningen som bevisar att medlemmen var på
        /// banan den dagen.
        ///
        /// <b>Incheckningen är alltid den som viker.</b> Ett inskrivet tävlingsresultat eller ett
        /// loggat träningspass säger mer om vad som gjordes än en QR-skanning gör.
        /// </summary>
        public static void MarkRedundantCheckIns(List<MemberActivityEntry> entries)
        {
            var checkIns = entries.Where(e => e.Kind == ActivityKind.RangeCheckIn).ToList();
            if (checkIns.Count == 0) return;

            foreach (var byDate in checkIns.GroupBy(e => e.Date.Date))
            {
                // Räknade poster den dagen som INTE är incheckningar. En DNS-tävling räknas inte, och
                // ska därför inte kunna knuffa bort incheckningen — den dagen var medlemmen ändå på
                // banan, och det är allt vi vet.
                var other = entries.FirstOrDefault(e =>
                    e.Kind != ActivityKind.RangeCheckIn &&
                    e.Date.Date == byDate.Key &&
                    e.CountsAsActivity);

                var ordered = byDate.OrderBy(e => e.SourceId).ToList();

                if (other != null)
                {
                    foreach (var c in ordered)
                    {
                        c.CountsAsActivity = false;
                        c.NotCountedReason = $"{RedundantCheckInReason}: {other.KindLabel.ToLowerInvariant()}";
                        c.SameOccasionAs = other.SourceKey;
                    }
                    continue;
                }

                // Ingen annan post den dagen — den FÖRSTA incheckningen räknas, resten är dubbletter
                // av samma besök (in och ut och in igen).
                for (int i = 1; i < ordered.Count; i++)
                {
                    ordered[i].CountsAsActivity = false;
                    ordered[i].NotCountedReason = DuplicateCheckInReason;
                    ordered[i].SameOccasionAs = ordered[0].SourceKey;
                }
            }
        }

        // ── Räknereglerna, utbrutna ──────────────────────────────────
        //
        // ⚠️ DE BOR HÄR för att detaljvyn och medlemslistans badge ska räkna LIKA. Badgen kan inte
        // bygga hela sammanställningen per medlem (tre källor × hela klubbens roster är den sortens
        // sida som tog tolv sekunder att ladda), så den har en egen bulkväg — och två uppsättningar
        // villkor blir förr eller senare två svar på samma fråga, mitt på ett myndighetsunderlag.

        /// <summary>
        /// Räknas en tävling som aktivitet? Ja, utom när skytten var anmäld men aldrig startade.
        /// DNS spelar bara roll när inget resultat finns: har skytten resultatrader har hen skjutit,
        /// även om en klass markerats som ej start.
        /// </summary>
        public static bool CompetitionCounts(bool hasResult, bool hasDns) => hasResult || !hasDns;

        /// <summary>
        /// Räknas en evenemangsrad som aktivitet? Bara vid registrerad närvaro. Ett upprop som aldrig
        /// togs är frånvaron av en uppgift, inte frånvaro — och giltig frånvaro är inte verksamhet.
        /// </summary>
        public static bool EventCounts(string? attendanceStatus) =>
            attendanceStatus == ClubEvents.AttendancePresent;
    }
}
