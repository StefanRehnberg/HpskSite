namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// <b>EN avbildning från skyttens klass till medaljgruppen</b> — det som avgör vilka skyttar som tävlar om samma uppsättning medaljer,
    /// vilka som särskjuts mot varandra, vilka finalklasser som finns och hur hederspriserna grupperas.
    ///
    /// <para>Ersätter <c>ChampionshipCategory.For(klass, bool splitGroupC)</c> hos alla anropare (plan v2, S0). Skälet: en bool kan inte
    /// uttrycka arrangörens tre val (SHB:s mästerskapsklasser, en uppsättning per vapengrupp, per klass), och <c>per klass</c> måste följa
    /// resultatlistans grupper — sammanslagna klasser ("C2+Dam") är EN grupp — vilket ingen funktion som bara ser klassen kan göra.
    /// Byggarna, finalernas klassuppslag, Nästa steg, medaljpanelen, prisutdelningen och hederspriserna bygger därför ALLA sin grupp
    /// genom den här klassen, så att de aldrig kan säga olika saker om samma medalj (förväxlingen mellan mästerskapskategori och
    /// skicklighetsklass har uppstått fyra gånger i kodbasen, se <see cref="ChampionshipCategory"/>).</para>
    ///
    /// <para>S0: avbildningen är neutral — den svarar exakt som <c>ChampionshipCategory.For</c> gjorde för varje tävling. Läget per klass
    /// (och resultatlistans sammanslagningar) kopplas in här i S1, på ett enda ställe.</para>
    ///
    /// <para>⚠️ Anropa aldrig <c>ChampionshipCategory.For</c>/<c>BuildLookup</c> direkt utanför den här klassen
    /// (<c>ChampionshipRuleGuardTests</c> fäller bygget). <c>WeaponGroupFor</c> är en annan axel (skjutsessionen) och rörs inte.</para>
    /// </summary>
    public sealed class MedalGroupMap
    {
        private readonly bool _splitGroupC;

        private MedalGroupMap(bool splitGroupC) { _splitGroupC = splitGroupC; }

        /// <summary>SHB:s mästerskapsklasser: A1–A3 är "A", B1–B3 är "B", och vapengrupp C delas i öppen, Dam, Vet Y, Vet Ä och Junior.</summary>
        public static readonly MedalGroupMap Shb = new(splitGroupC: true);

        /// <summary>En uppsättning medaljer per vapengrupp ("totalen"): A, B, C.</summary>
        public static readonly MedalGroupMap WeaponGroup = new(splitGroupC: false);

        /// <summary>Avbildningen som tävlingens regel ger (indelningen som GÄLLER, med nivåspärren tillämpad).</summary>
        public static MedalGroupMap For(ChampionshipRule rule) =>
            rule.SplitsChampionshipClassC ? Shb : WeaponGroup;

        /// <summary>Gruppnamnet för en skytteklass (id- eller namnform). Tom sträng för en tom klass; en okänd klass returneras oförändrad.</summary>
        public string GroupFor(string? shootingClassIdOrName) =>
            ChampionshipCategory.For(shootingClassIdOrName, _splitGroupC);

        /// <summary>
        /// Uppslagstabell klass → grupp i den form <c>PrecisionFinalsQualificationService.BuildFullClassRankings</c> väntar sig
        /// (nycklarna är klassernas visningsnamn).
        /// </summary>
        public Dictionary<string, string> BuildLookup(IEnumerable<string> shootingClasses) =>
            ChampionshipCategory.BuildLookup(shootingClasses, _splitGroupC);
    }
}
