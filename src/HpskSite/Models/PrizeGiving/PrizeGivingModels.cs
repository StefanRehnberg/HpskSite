using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.Models.PrizeGiving
{
    /// <summary>
    /// Allt prisutdelningsfunktionären behöver, för EN tävling och eventuellt EN vapengrupp.
    ///
    /// SHB C.4.3.1.11: *"För att vinna tid kan prisutdelningen vid större tävlingar försiggå
    /// vid flera bord samtidigt, exempelvis ett för varje vapengrupp."* Därför är
    /// <see cref="SelectedWeaponGroup"/> en förstklassig del av modellen och inte en filterfiness:
    /// varje bord öppnar sin egen adress och ser bara sina grupper.
    /// </summary>
    public class PrizeGivingModel
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public string CompetitionType { get; set; } = "";
        public bool IsChampionship { get; set; }

        /// <summary>Vald vapengrupp, eller "" för alla. Kommer från ?grupp= i adressen.</summary>
        public string SelectedWeaponGroup { get; set; } = "";

        /// <summary>Vapengrupper som finns i tävlingen — underlaget för bordsväljaren.</summary>
        public List<string> AvailableWeaponGroups { get; set; } = new();

        /// <summary>
        /// När resultatlistan senast räknades om. Visas alltid och prominent.
        ///
        /// ⚠️ Sidan läser den SPARADE resultatartefakten och räknar inte om något. Det är rätt
        /// för en prisutdelning — C.4.3.1.11 kräver *"noga kontrollerade"* resultat före
        /// ceremonin, alltså ska medaljerna spegla exakt den lista arrangören kontrollerat och
        /// publicerat, inte ett nytt utfall som råkar räknas fram medan funktionären står vid
        /// bordet. Priset är att en gammal artefakt ger en gammal prislista, och därför är
        /// tidsstämpeln inte gömd i en fot utan en del av sidhuvudet.
        /// </summary>
        public DateTime? ResultsUpdatedAt { get; set; }

        /// <summary>False när tävlingen inte har någon resultatlista alls.</summary>
        public bool HasResultList { get; set; }

        /// <summary>
        /// False när resultatartefakten är äldre än medaljberäkningen och alltså inte innehåller
        /// några medaljörer. Skiljer "inga medaljer" från "vet inte" — se
        /// <see cref="PrecisionFinalResults.MedalAwardsComputed"/>.
        /// </summary>
        public bool MedalsComputed { get; set; }

        /// <summary>
        /// Enheten för resultatets huvudtal, som den skrivs intill siffran: "p" för
        /// precisionsfamiljen, "träff" för normalfält, "p" för poängfält och magnumfält.
        ///
        /// ⚠️ ETIKETTEN ÄR INTE KOSMETIK. Ett fältskytteresultat på 32 lästes som "32 p" på en
        /// sida som räknar träffar — och en funktionär som läser upp fel enhet vid bordet låter
        /// som om hen läser fel resultat.
        /// </summary>
        public string ScoreUnit { get; set; } = "p";

        /// <summary>Enheten för andrahandstalet: "X" (innertior), "fig" (figurer) eller
        /// "pmål" (poängmålssumman).</summary>
        public string SecondaryUnit { get; set; } = "X";

        /// <summary>
        /// Lagets andrahandstal, som kan skilja sig från individens.
        ///
        /// ⚠️ I POÄNGFÄLT ÄR DE OLIKA. Individen särskiljs på poängmål (SHB D.6.11.2.1.2),
        /// laget på sammanlagda träffade figurer (D.6.11.2.2.2 punkt 1). Att låta lagkortet
        /// ärva individens etikett skrev alltså "pmål" över en figursiffra.
        /// </summary>
        public string TeamSecondaryUnit { get; set; } = "X";

        /// <summary>
        /// Lagens huvudtal-enhet.
        ///
        /// ⚠️ SKILD FRÅN <see cref="ScoreUnit"/> MED FLIT. Individens tal läses ur den sparade
        /// resultatartefakten och bär DEN listans enhet; lagens tal räknas fram live vid varje
        /// sidladdning och bär tävlingens nuvarande. Är artefakten räknad före ett byte mellan
        /// normalfält och poängfält är de två olika — och då ska korten säga olika saker, för
        /// talen ÄR räknade olika. Sidan larmar samtidigt om att listan behöver räknas om.
        /// </summary>
        public string TeamScoreUnit { get; set; } = "p";

        public List<PrecisionMedalCategoryAwards> Individual { get; set; } = new();
        public List<PrizeTeamGroup> Teams { get; set; } = new();
        public PrizeHonorarySection Honorary { get; set; } = new();

        /// <summary>
        /// Sådant som gör ceremonin osäker och som funktionären måste få se innan den börjar:
        /// ofullständiga resultat, opublicerad lista, oavgjorda medaljstrider.
        /// </summary>
        public List<string> Warnings { get; set; } = new();

        /// <summary>True när den inloggade får ändra hedersprisfördelningen.</summary>
        public bool CanEdit { get; set; }

        /// <summary>Totalt antal medaljer att dela ut i det som visas — sanity-siffran vid bordet.</summary>
        public int TotalMedals =>
            Individual.Sum(c => c.Awards.Count) + Teams.Sum(t => t.Awards.Count);

        /// <summary>Antal medaljplatser som inte kan delas ut på grund av oavgjord särskjutning.</summary>
        public int TotalUnresolved =>
            Individual.Sum(c => c.Unresolved.Count) + Teams.Sum(t => t.Unresolved.Count);
    }

    /// <summary>Lagmedaljerna i EN lagklass.</summary>
    public class PrizeTeamGroup
    {
        /// <summary>"A", "B", "C Öppen", "C Dam", "C Vet", "C Jun", "R" ... — lagmedaljgrupperna
        /// räknas upp i SHB C.3.6.5.1.</summary>
        public string TeamClass { get; set; } = "";

        public string WeaponGroup { get; set; } = "";

        /// <summary>Antal STARTANDE lag i gruppen. C.3.6.5.1: *"Om antalet startande lag i
        /// vapengrupp eller klass är mindre än fem reduceras antalet mästerskapstecken i samma
        /// ordning som i individuell tävling."*</summary>
        public int StartingTeams { get; set; }

        public int MedalCount { get; set; }
        public string MedalCountText { get; set; } = "";
        public bool Reduced { get; set; }

        public List<PrizeTeamAward> Awards { get; set; } = new();
        public List<string> Unresolved { get; set; } = new();

        /// <summary>True för stafettlag — de poängsätts på en klocka och ska inte presenteras
        /// som ett vanligt lagresultat.</summary>
        public bool IsRelay { get; set; }
    }

    public class PrizeTeamAward
    {
        public int Place { get; set; }
        public string Medal { get; set; } = "";
        public string TeamName { get; set; } = "";
        public string ClubName { get; set; } = "";
        public int TotalScore { get; set; }
        public int XCount { get; set; }

        /// <summary>Lagmedlemmarna. Medaljerna delas ut till skyttarna, så namnen måste stå på
        /// kortet — annars vet funktionären inte hur många medaljer laget ska ha.</summary>
        public List<string> Members { get; set; } = new();
    }

    /// <summary>
    /// Hederspriserna. SHB C.3.4.2: *"Arrangör avgör om hederspriser utgår. Om så sker skall
    /// dessa tillfalla minst en fjärdedel av de i tävlingen deltagande."*
    /// </summary>
    public class PrizeHonorarySection
    {
        /// <summary>Tävlingens <c>isAwardingHonoraryAward</c>. False → sektionen visas inte.</summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// True när doctype-egenskapen <c>isAwardingHonoraryAward</c> inte finns på tävlingen.
        ///
        /// ⚠️ Läsvägen degraderar till "utgår inte" och sidan säger VARFÖR, i stället för att
        /// tiga. Egenskapen skapas i backoffice, och en saknad egenskap som bara ser ut som ett
        /// nej är den sortens fel som tar en helg att hitta.
        /// </summary>
        public bool PropertyMissing { get; set; }

        /// <summary>Distinkta deltagare i tävlingen — underlaget för fjärdedelen.</summary>
        public int ParticipantCount { get; set; }

        /// <summary>Golvet: minst en fjärdedel av deltagarna, uppåt avrundat.</summary>
        public int Minimum { get; set; }

        /// <summary>Summan av det som faktiskt är fördelat per kategori.</summary>
        public int Assigned => Categories.Sum(c => c.Count);

        /// <summary>True när fördelningen når golvet.</summary>
        public bool MeetsMinimum => Assigned >= Minimum;

        /// <summary>True när fördelningen är arrangörens egen och inte systemets förslag.</summary>
        public bool IsCustomised { get; set; }

        public List<PrizeHonoraryCategory> Categories { get; set; } = new();
    }

    public class PrizeHonoraryCategory
    {
        public string CategoryName { get; set; } = "";
        public string WeaponGroup { get; set; } = "";
        public int Participants { get; set; }

        /// <summary>Systemets förslag — deltagarna i kategorin delat på fyra, uppåt avrundat.</summary>
        public int ProposedCount { get; set; }

        /// <summary>Det som gäller: arrangörens siffra om den är satt, annars förslaget.</summary>
        public int Count { get; set; }

        /// <summary>
        /// Kategorins deltagare i resultatordning, så många att fördelningen kan ändras uppåt
        /// vid bordet utan att sidan behöver läsas om.
        /// </summary>
        public List<PrizeHonoraryCandidate> Candidates { get; set; } = new();
    }

    public class PrizeHonoraryCandidate
    {
        /// <summary>1-baserad plats i kategorin.</summary>
        public int Order { get; set; }

        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public int TotalScore { get; set; }
        public int XCount { get; set; }

        /// <summary>
        /// Satt när skytten står lika med någon annan PRECIS VID GRÄNSEN för sista
        /// hederspriset, och ordningen alltså måste avgöras.
        ///
        /// SHB D.6.11.2: *"Resultatet av särskjutning avgör även i vilken ordning hederspriser
        /// skall fördelas mellan de skyttar som deltar i särskjutningen"* och *"Lottning skall
        /// fortsättningsvis användas endast som grund för fördelning av eventuella hederspris."*
        ///
        /// ⚠️ Flaggas BARA vid gränsen. Två skyttar som står lika mitt i listan får båda ett
        /// pris och ordningen mellan dem saknar betydelse — att be funktionären lotta där vore
        /// att skapa arbete av ingenting.
        /// </summary>
        public string? TieNote { get; set; }
    }
}
