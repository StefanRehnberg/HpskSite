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

    public sealed class ChampionshipRule
    {
        /// <summary>Omfattningen som den är lagrad (rå).</summary>
        public string Scope { get; }

        /// <summary>Arrangörens lagrade val "en uppsättning medaljer per vapengrupp" (före nivåspärren).</summary>
        public bool StoredMedalsPerWeaponGroup { get; }

        private readonly string _discipline;
        private readonly IReadOnlyList<string> _weaponGroups;

        private ChampionshipRule(string? scope, bool storedMedalsPerWeaponGroup,
            string? discipline = null, string? weaponGroups = null)
        {
            Scope = scope ?? "";
            StoredMedalsPerWeaponGroup = storedMedalsPerWeaponGroup;
            _discipline = CanonicalDiscipline(discipline);
            _weaponGroups = ChampionshipWeaponGroups.Parse(weaponGroups);
        }

        public static ChampionshipRule For(CompetitionInfo? competition) =>
            new(competition?.CompetitionScope, competition?.MedalsPerWeaponGroup ?? false,
                competition?.CompetitionType, competition?.ChampionshipWeaponGroups);

        /// <summary>För de ställen som bara har omfattningen och arrangörens lagrade val (t.ex. en sparad artefakt).</summary>
        public static ChampionshipRule ForScope(string? scope, bool storedMedalsPerWeaponGroup = false) =>
            new(scope, storedMedalsPerWeaponGroup);

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

        /// <summary>Arrangörens val som det GÄLLER (med nivåspärren).</summary>
        public bool MedalsPerWeaponGroup => MedalGrouping.PerWeaponGroup(Scope, StoredMedalsPerWeaponGroup);

        // ── C-delningen — två regler som inte får förväxlas ──────────────────────

        /// <summary>
        /// Delas vapengrupp C i sina fem MÄSTERSKAPSKLASSER (öppen, Dam, Vet Y, Vet Ä, Junior)? Vid alla mästerskap, om inte
        /// arrangören valt bort delningen. (SHB C.3.6.4, C.3.4.1.) Tidigare <c>ChampionshipCategory.SplitsGroupC</c>.
        /// </summary>
        public bool SplitsChampionshipClassC => ChampionshipCategory.SplitsGroupC(Scope, StoredMedalsPerWeaponGroup);

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
        public bool OutOfCompetitionApplies => IsChampionship && IsFaltFamily(_discipline);

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

        private static bool IsFaltFamily(string discipline) =>
            discipline is "Faltskytte" or "MagnumFalt";
    }
}
