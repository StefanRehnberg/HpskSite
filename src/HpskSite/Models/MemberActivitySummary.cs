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

        public const string SourceKindCompetition = "comp";
        public const string SourceKindTraining = "training";
        public const string SourceKindEvent = "event";

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
        /// i tre vapenklasser till samma tävling har deltagit i EN tävling.</summary>
        public int Competitions { get; set; }

        /// <summary>Obligatoriska evenemang under året som medlemmen var närvarande på.</summary>
        public int MandatoryEventsAttended { get; set; }

        /// <summary>Obligatoriska evenemang under året där närvaro INTE är registrerad — frånvarande,
        /// giltig frånvaro, eller upprop som aldrig togs. Styrelsens intygsbeslut hänger på den här.</summary>
        public int MandatoryEventsMissed { get; set; }

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
            int memberId, string memberName, int year, IEnumerable<MemberActivityEntry> entries)
        {
            var all = entries.OrderByDescending(e => e.Date).ThenBy(e => e.Title).ToList();
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
                MandatoryEventsAttended = counted.Count(e => e.Kind == ActivityKind.Event && e.IsMandatoryEvent),
                MandatoryEventsMissed = all.Count(e =>
                    e.Kind == ActivityKind.Event && e.IsMandatoryEvent && !e.CountsAsActivity)
            };

            summary.Warnings = BuildWarnings(all, counted);
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

            int selfReportedTraining = counted.Count(e =>
                (e.Kind == ActivityKind.Training || e.Kind == ActivityKind.Practice)
                && e.Evidence == ActivityEvidence.SelfReported);
            if (selfReportedTraining > 0)
                w.Add($"{selfReportedTraining} träningspass är självrapporterade och inte intygade av " +
                      "någon funktionär. Funktionärsverifiering av träning är inte byggd ännu.");

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
    }
}
