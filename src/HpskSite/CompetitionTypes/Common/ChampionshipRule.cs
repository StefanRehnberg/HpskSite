using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models.Competitions;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// EN regel för allt som beror på om och hur en tävling är ett mästerskap (utanför mästerskapet, S2).
    ///
    /// <para><b>Varför den finns.</b> Mästerskapsfrågorna var utspridda över ett femtiotal ställen som läste
    /// <c>competitionScope</c> var för sig — några normaliserade (<see cref="ChampionshipCategory"/>), några jämförde rått
    /// (<c>== "Svenskt Mästerskap"</c>, <c>ShouldSplitGroupC</c>). Nästa steg (vilka vapengrupper som är mästerskap, vilka som
    /// tävlar utanför) kan bara göras på ETT ställe. S2 flyttar därför alla läsare hit utan att ändra ett enda svar:
    /// golden-master mot <c>ref-C</c> ska vara byte-identisk.</para>
    ///
    /// <para><b>Medvetet ofullständig i S2:</b> metoderna svarar exakt som de gamla funktionerna gjorde, inklusive skillnaden
    /// mellan normaliserad och rå omfattning (<see cref="IsChampionship"/> mot <see cref="IsChampionshipRaw"/>). Skillnaden är
    /// noll i prod (inga arrayformer i dumpen 2026-11-04) men tas bort först när det är bevisat per tävling, inte här.</para>
    ///
    /// <para><b>Läs aldrig omfattningen på annat sätt.</b> <c>ChampionshipRuleGuardTests</c> fäller bygget på direktläsningar av
    /// <c>IsChampionshipScope</c>, <c>ChampionshipCategory.IsChampionship/SplitsGroupC</c>, <c>ShouldSplitGroupC</c>,
    /// <c>MedalGrouping.PerWeaponGroup/CanChoose</c> och rå jämförelser mot SM/landsdel utanför den här klassen. Frågan om NIVÅN
    /// (adress, märken, kalender) är en annan fråga och stannar på <c>competitionLevel</c>/<c>CompetitionScopeHelper</c>
    /// (märk raden <c>championshiprule-ok: nivå</c>).</para>
    /// </summary>
    /// <summary>Sorten mästerskap, för de ställen som behöver skilja nivåerna åt men inte får läsa omfattningen själva.</summary>
    public enum ChampionshipScopeKind { None, Sm, Landsdel, Krets, Klubb }

    /// <summary>Hur medaljerna indelas (arrangörens val, se <see cref="MedalGroupingModes"/>). <c>Class</c> tillämpas först i S1 (plan v2).</summary>
    public enum MedalGroupingKind { Shb, WeaponGroup, Class }

    public sealed class ChampionshipRule
    {
        /// <summary>Omfattningen som den är lagrad (rå).</summary>
        public string Scope { get; }

        /// <summary>Arrangörens lagrade val "en uppsättning medaljer per vapengrupp" (före nivåspärren).</summary>
        public bool StoredMedalsPerWeaponGroup { get; }

        /// <summary>Arrangörens lagrade indelning (<see cref="MedalGroupingModes"/>), normaliserad; null = följ den gamla boolen.</summary>
        public string? StoredGroupingMode { get; }

        /// <summary>Arrangörens lagrade antal medaljplatser (0–3, 0 = SHB:s reducering) före nivåspärren.</summary>
        public int StoredMedalPlaces { get; }

        /// <summary>Arrangörens lagrade avgörande av oavgjorda platser (<see cref="MedalTieRules"/>), normaliserad; null = särskjutning.</summary>
        public string? StoredTieRule { get; }

        private readonly string _discipline;
        private readonly IReadOnlyList<string> _weaponGroups;

        private ChampionshipRule(string? scope, bool storedMedalsPerWeaponGroup,
            string? discipline = null, string? weaponGroups = null, string? storedGroupingMode = null,
            int storedMedalPlaces = 0, string? storedTieRule = null)
        {
            Scope = scope ?? "";
            StoredMedalsPerWeaponGroup = storedMedalsPerWeaponGroup;
            StoredGroupingMode = MedalGroupingModes.Normalize(storedGroupingMode);
            StoredMedalPlaces = Math.Clamp(storedMedalPlaces, 0, 3);
            StoredTieRule = MedalTieRules.Normalize(storedTieRule);
            _discipline = CanonicalDiscipline(discipline);
            _weaponGroups = ChampionshipWeaponGroups.Parse(weaponGroups);
        }

        public static ChampionshipRule For(CompetitionInfo? competition) =>
            new(competition?.CompetitionScope, competition?.MedalsPerWeaponGroup ?? false,
                competition?.CompetitionType, competition?.ChampionshipWeaponGroups, competition?.MedalGroupingMode,
                competition?.MedalPlaces ?? 0, competition?.MedalTieRule);

        /// <summary>
        /// För de ställen som bara har omfattningen och arrangörens lagrade val (t.ex. en sparad artefakt, som bär sin egen indelning,
        /// sina platser och sin oavgjort-regel: etiketten följer artefakten, inte tävlingens nuvarande inställning).
        /// </summary>
        public static ChampionshipRule ForScope(string? scope, bool storedMedalsPerWeaponGroup = false,
            string? storedGroupingMode = null, int storedMedalPlaces = 0, string? storedTieRule = null) =>
            new(scope, storedMedalsPerWeaponGroup, storedGroupingMode: storedGroupingMode,
                storedMedalPlaces: storedMedalPlaces, storedTieRule: storedTieRule);

        // ── Är det ett mästerskap? ───────────────────────────────────────────────

        /// <summary>Är omfattningen ett mästerskap alls? Normaliserad (skalar av en JSON-array) — det de flesta ställen frågade.</summary>
        public bool IsChampionship => ChampionshipCategory.IsChampionship(Scope);

        /// <summary>
        /// Som <see cref="IsChampionship"/> men på den RÅA omfattningen (<c>IsChampionshipScope</c>). Några ställen (fältets bygge,
        /// granskningen, <c>CompetitionInfo.IsChampionship</c>) frågade så; skillnaden är noll så länge ingen omfattning är en JSON-array.
        /// </summary>
        public bool IsChampionshipRaw => CompetitionScopeHelper.IsChampionshipScope(Scope);

        /// <summary>SM eller landsdelsmästerskap, rått jämfört (Ordinal) — det ställen som kallade sig <c>isSmOrLdm</c> gjorde.</summary>
        public bool IsSmOrLandsdel =>
            Scope == CompetitionScopeHelper.SvensktMasterskap || Scope == CompetitionScopeHelper.Landsdelsmasterskap;

        // ── Medaljindelningen ────────────────────────────────────────────────────

        /// <summary>Får arrangören välja "en uppsättning medaljer per vapengrupp"? Bara klubb- och kretsmästerskap.</summary>
        public bool CanChooseMedalGrouping => MedalGrouping.CanChoose(Scope);

        /// <summary>
        /// Indelningen som GÄLLER, med nivåspärren tillämpad (bara klubb- och kretsmästerskap får välja; på SM, landsdel och utan mästerskap är det
        /// SHB:s indelning). Det nya fältet <c>medalGroupingMode</c> vinner över den gamla boolen; är det tomt gäller boolen, så en tävling utan
        /// det nya fältet beter sig exakt som förr.
        ///
        /// <para><c>class</c> (per klass) gäller medaljgruppen = resultatlistans grupp, alltså följer den sammanslagna klasser; den
        /// kopplas in i <see cref="MedalGroupMap.For(ChampionshipRule, IReadOnlyDictionary{string, string}?)"/>, som kräver sammanslagningarna
        /// som argument — därför får ingen läsa <c>GroupingMode == Class</c> och bygga en egen grupp.</para>
        /// </summary>
        public MedalGroupingKind GroupingMode
        {
            get
            {
                if (!MedalGrouping.CanChoose(Scope)) return MedalGroupingKind.Shb;
                var stored = StoredGroupingMode
                    ?? (StoredMedalsPerWeaponGroup ? MedalGroupingModes.WeaponGroup : MedalGroupingModes.Shb);
                return stored switch
                {
                    MedalGroupingModes.WeaponGroup => MedalGroupingKind.WeaponGroup,
                    MedalGroupingModes.Class => MedalGroupingKind.Class,
                    _ => MedalGroupingKind.Shb
                };
            }
        }

        /// <summary>Indelningen som GÄLLER som lagrat värde (<see cref="MedalGroupingModes"/>) — det som skrivs i artefakten och jämförs mot den.</summary>
        public string GroupingModeName => GroupingMode switch
        {
            MedalGroupingKind.WeaponGroup => MedalGroupingModes.WeaponGroup,
            MedalGroupingKind.Class => MedalGroupingModes.Class,
            _ => MedalGroupingModes.Shb
        };

        /// <summary>Antal medaljplatser som GÄLLER: 0 = SHB:s reducering efter deltagare, 1–3 = fast antal bästa. Nivåspärren tillämpad.</summary>
        public int MedalPlaces => MedalGrouping.CanChoose(Scope) ? StoredMedalPlaces : 0;

        /// <summary>Hur en oavgjord medaljplats avgörs (<see cref="MedalTieRules"/>) som GÄLLER: särskjutning (dagens beteende) om inget annat är valt eller spärren slår till.</summary>
        public string TieRule => MedalGrouping.CanChoose(Scope) ? (StoredTieRule ?? MedalTieRules.ShootOff) : MedalTieRules.ShootOff;

        /// <summary>Arrangörens val som det GÄLLER (med nivåspärren): en uppsättning medaljer per vapengrupp.</summary>
        public bool MedalsPerWeaponGroup => GroupingMode == MedalGroupingKind.WeaponGroup;

        // ── C-delningen — två regler som inte får förväxlas ──────────────────────

        /// <summary>
        /// Delas vapengrupp C i sina fem MÄSTERSKAPSKLASSER (öppen, Dam, Vet Y, Vet Ä, Junior)? Vid alla mästerskap, om inte
        /// arrangören valt bort delningen. (SHB C.3.6.4, C.3.4.1.) Tidigare <c>ChampionshipCategory.SplitsGroupC</c>.
        /// </summary>
        public bool SplitsChampionshipClassC => IsChampionship && GroupingMode == MedalGroupingKind.Shb;

        /// <summary>
        /// Delas vapengrupp C för STANDARDMEDALJBERÄKNINGEN? Bara vid SM och landsdelsmästerskap (SHB C.5.1.1.4). Rå jämförelse, exakt
        /// som <c>StandardMedalCalculationService.ShouldSplitGroupC</c>. Tidigare den metoden.
        /// </summary>
        public bool SplitsStandardMedalGroupC => IsSmOrLandsdel && AppliesTo("C");   // EN definition: samma jämförelse mot konstanterna (en tom omfattning är aldrig lika med någon av dem). AppliesTo("C") är sant utan lista, så utan lista är svaret oförändrat; med en lista utan C (mästerskap i andra grupper) delas C inte (SHB rad 3823-3826: delningen gäller "varje mästerskap i vapengrupp C").

        /// <summary>Vilken sorts mästerskap (normaliserad omfattning). <see cref="ChampionshipScopeKind.None"/> = inget mästerskap.</summary>
        public ChampionshipScopeKind Kind
        {
            get
            {
                var scope = CompetitionScopeHelper.NormalizeScopeValue(Scope);
                if (scope == CompetitionScopeHelper.SvensktMasterskap) return ChampionshipScopeKind.Sm;
                if (scope == CompetitionScopeHelper.Landsdelsmasterskap) return ChampionshipScopeKind.Landsdel;
                if (scope == CompetitionScopeHelper.Kretsmasterskap) return ChampionshipScopeKind.Krets;
                if (scope == CompetitionScopeHelper.Klubbmasterskap) return ChampionshipScopeKind.Klubb;
                return ChampionshipScopeKind.None;
            }
        }

        // ── Utanför mästerskapet och vilka vapengrupper som avgör mästerskapet (S4/S5) ───────────

        /// <summary>
        /// Gäller utanför-mästerskapet-beteendet den här tävlingen? Två villkor, båda: tävlingen är ett mästerskap, och grenen är fält
        /// eller MagnumFält (precision och springskytte ignorerar flaggan i första bygget; skidskytte ingår inte). Det finns ingen
        /// växel per tävling (Stefan 2026-11-06: "det är så det ska fungera"). Utan att någon markerats utanför och utan inställningen
        /// "vilka deltar" är alla tävlande och inget ändras mot förr — placeringarna blir bara serverns i stället för radnumret.
        /// </summary>
        public bool OutOfCompetitionApplies => IsChampionship && IsEnabledDiscipline(_discipline);

        /// <summary>
        /// Använder tävlingens GREN beteendet (sajtinställningen <c>OutOfCompetition:Disciplines</c>), oavsett om omfattningen är ett mästerskap?
        /// Redigera tävling frågar så, eftersom arrangören kan byta omfattning i samma dialog och inställningarna då ska dyka upp.
        /// </summary>
        public bool DisciplineEnabled => IsEnabledDiscipline(_discipline);

        /// <summary>
        /// Avgör tävlingen mästerskapet i den här vapengruppen ("A", "C", "R" … — <see cref="ChampionshipCategory.WeaponGroupFor"/>)?
        /// Sant när beteendet inte gäller eller ingen lista är satt (alla grupper), annars bara för listade grupper. Grupper där det är
        /// falskt skjuts som en vanlig tävling: placering och standardmedaljer, men ingen mästare och ingen mästerskapsmedalj.
        /// </summary>
        public bool AppliesTo(string? weaponGroup)
        {
            if (!OutOfCompetitionApplies || _weaponGroups.Count == 0) return true;
            var group = (weaponGroup ?? "").Trim();
            return _weaponGroups.Contains(group, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Som <see cref="AppliesTo(string?)"/> men för en skytteklass (Id eller visningsnamn).</summary>
        public bool AppliesToClass(string? shootingClassIdOrName) =>
            AppliesTo(ChampionshipCategory.WeaponGroupFor(shootingClassIdOrName));

        /// <summary>De listade vapengrupperna (tom = alla). Bara för visning och avtryck.</summary>
        public IReadOnlyList<string> WeaponGroups => _weaponGroups;

        /// <summary>
        /// Grenens id, med samma uppslag som resten av koden (id, namn, sist fuzzy) — äldre tävlingar kan ha lagrat "Fältskjutning" eller
        /// "Magnum Fält" i stället för id:t. Utan det räknades en sådan fälttävling inte som fält.
        /// </summary>
        private static string CanonicalDiscipline(string? discipline)
        {
            var t = (discipline ?? "").Trim();
            if (t.Length == 0) return "";
            var known = HpskSite.Models.CompetitionTypes.GetById(t)
                     ?? HpskSite.Models.CompetitionTypes.GetByName(t)
                     ?? HpskSite.Models.CompetitionTypes.GetFuzzy(t);
            return known?.Id ?? t;
        }

        /// <summary>Använder grenen "Utanför mästerskapet"? Listan är sajtinställningen <c>OutOfCompetition:Disciplines</c> (<see cref="OutOfCompetitionDisciplines"/>), förval fält.</summary>
        private static bool IsEnabledDiscipline(string discipline) => OutOfCompetitionDisciplines.Allows(discipline);
    }
}
