using HpskSite.Models;

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
    /// <para><b>Per klass (S1):</b> gruppen är resultatlistans grupp, så avbildningen får sammanslagningarna (klass → sammanslagen grupp)
    /// som argument; en klass utan sammanslagning är sin egen grupp. Ingen anropare bygger en sådan avbildning själv — de ger
    /// <see cref="For(ChampionshipRule, IReadOnlyDictionary{string, string}?)"/> den sammanslagningstabell de redan har.</para>
    ///
    /// <para>⚠️ Anropa aldrig <c>ChampionshipCategory.For</c>/<c>BuildLookup</c> direkt utanför den här klassen
    /// (<c>ChampionshipRuleGuardTests</c> fäller bygget). <c>WeaponGroupFor</c> är en annan axel (skjutsessionen) och rörs inte.</para>
    /// </summary>
    public sealed class MedalGroupMap
    {
        private readonly bool _splitGroupC;
        private readonly IReadOnlyDictionary<string, string>? _classMerges;

        private MedalGroupMap(MedalGroupingKind kind, bool splitGroupC, IReadOnlyDictionary<string, string>? classMerges = null)
        {
            Kind = kind;
            _splitGroupC = splitGroupC;
            _classMerges = classMerges;
        }

        /// <summary>Vilken indelning avbildningen är.</summary>
        public MedalGroupingKind Kind { get; }

        /// <summary>SHB:s mästerskapsklasser: A1–A3 är "A", B1–B3 är "B", och vapengrupp C delas i öppen, Dam, Vet Y, Vet Ä och Junior.</summary>
        public static readonly MedalGroupMap Shb = new(MedalGroupingKind.Shb, splitGroupC: true);

        /// <summary>En uppsättning medaljer per vapengrupp ("totalen"): A, B, C.</summary>
        public static readonly MedalGroupMap WeaponGroup = new(MedalGroupingKind.WeaponGroup, splitGroupC: false);

        /// <summary>Per klass utan några sammanslagningar: varje skicklighetsklass är sin egen grupp.</summary>
        public static MedalGroupMap PerClass(IReadOnlyDictionary<string, string>? classMerges = null) =>
            new(MedalGroupingKind.Class, splitGroupC: false, classMerges);

        /// <summary>
        /// Avbildningen som tävlingens regel ger (indelningen som GÄLLER, med nivåspärren tillämpad). <paramref name="classMerges"/> är
        /// resultatlistans sammanslagningar (klass → sammanslagen grupp, nycklar i klassens kanoniska namn) och används bara för per klass.
        /// </summary>
        public static MedalGroupMap For(ChampionshipRule rule, IReadOnlyDictionary<string, string>? classMerges = null) =>
            rule.GroupingMode switch
            {
                MedalGroupingKind.Class => PerClass(classMerges),
                MedalGroupingKind.WeaponGroup => WeaponGroup,
                _ => rule.SplitsChampionshipClassC ? Shb : WeaponGroup
            };

        /// <summary>
        /// Gruppnamnet för en skytteklass (id- eller namnform). Tom sträng för en tom klass. SHB och vapengrupp: en okänd klass returneras
        /// oförändrad. Per klass: klassens kanoniska namn, eller den sammanslagna gruppens namn när klassen slagits ihop med andra.
        /// </summary>
        public string GroupFor(string? shootingClassIdOrName)
        {
            if (Kind != MedalGroupingKind.Class)
                return ChampionshipCategory.For(shootingClassIdOrName, _splitGroupC);

            if (string.IsNullOrWhiteSpace(shootingClassIdOrName)) return "";
            var canonical = ShootingClasses.ToCanonicalName(shootingClassIdOrName);
            if (canonical.Length == 0) canonical = shootingClassIdOrName.Trim();
            return _classMerges != null && _classMerges.TryGetValue(canonical, out var merged) ? merged : canonical;
        }

        /// <summary>
        /// Uppslagstabell klass → grupp i den form <c>PrecisionFinalsQualificationService.BuildFullClassRankings</c> väntar sig
        /// (nycklarna är klassernas visningsnamn).
        /// </summary>
        public Dictionary<string, string> BuildLookup(IEnumerable<string> shootingClasses)
        {
            if (Kind != MedalGroupingKind.Class)
                return ChampionshipCategory.BuildLookup(shootingClasses, _splitGroupC);

            var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in shootingClasses ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                var key = ShootingClasses.ToCanonicalName(raw);
                if (key.Length == 0) continue;
                lookup[key] = GroupFor(raw);
            }
            return lookup;
        }
    }
}
