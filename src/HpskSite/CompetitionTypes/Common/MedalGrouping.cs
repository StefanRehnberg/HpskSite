using HpskSite.CompetitionTypes.Common.Utilities;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.CompetitionTypes.Common
{
    /// <summary>
    /// Arrangörens val: delas vapengrupp C i sina mästerskapsklasser, eller delas EN uppsättning
    /// medaljer ut per vapengrupp?
    ///
    /// ⚠️⚠️ VARFÖR VALET FINNS. Vid ett klubb- eller kretsmästerskap slår arrangören ofta ihop
    /// allt och ger guld till den bäste. Fram till nu gick det inte: kategorierna delades alltid
    /// vid ett mästerskap, så en tävling där resultatlistan visade EN sammanslagen grupp gav
    /// medaljerna per kategori ändå. Mätt på dev 5591 (klubbmästerskap i Nationell Helmatch, 8
    /// skyttar i vapengrupp C, alla klasser sammanslagna till C2): resultatlistan satte Andy
    /// Haard först på 582 p, medan prisutdelningen gav **guld till tvåan** (558 p) och ingenting
    /// alls till segraren — hans kategori, C Vet Y, hade två deltagare och därmed noll medaljer.
    /// Två ytor på samma tävling som sa emot varandra.
    ///
    /// ⚠️ VALET ÄR INTE "följ klassammanslagningen". Det formulerades så först, och det är fel
    /// fråga: vill arrangören ha ett guld till bäste skytt ska det gälla även utan sammanslagning,
    /// och tvärtom. Sammanslagningen (SHB F.2.3, regeln om färre än fem deltagare) är en annan
    /// mekanism på en annan axel — den ändrar hur resultatlistan GRUPPERAS. Se
    /// <see cref="ChampionshipCategory"/>, som beskriver den förväxlingen i detalj; den har
    /// uppstått fyra gånger i den här kodbasen.
    ///
    /// **Stöd i SHB.** C.3.4.1 låter reduceringen gälla *"vid landsdels-, krets- och
    /// klubbmästerskap då respektive styrelse bestämt att deltagarantalet FÖR EGEN KLASS får vara
    /// färre än 5"* — alltså förutsätter regeln ett beslut av styrelsen om att köra egna klasser.
    /// Odelad vapengrupp är dessutom ett erkänt läge: C.5.1.1.4 delar C-mästerskapen bara vid SM
    /// och landsdelsmästerskap för standardmedaljerna.
    /// </summary>
    public static class MedalGrouping
    {
        /// <summary>
        /// Egenskapen på doctypen <c>competition</c>. Sant = EN uppsättning medaljer per
        /// vapengrupp; falskt (och standard) = dagens uppdelning i mästerskapsklasser.
        ///
        /// ⚠️ Saknas egenskapen svarar <c>GetValue&lt;bool&gt;</c> falskt, alltså exakt dagens
        /// beteende — en deploy utan operatörssteget ändrar ingenting. SKRIVvägen måste däremot
        /// vägra och NAMNGE egenskapen: <c>SetValue</c> på en saknad egenskap är en tyst no-op,
        /// och en kryssruta som ser ut att spara och är borta vid nästa laddning är värre än en
        /// låst.
        /// </summary>
        public const string PropertyAlias = "medalsPerWeaponGroup";

        /// <summary>
        /// Får arrangören välja på den här nivån? **Bara klubb- och kretsmästerskap.**
        ///
        /// ⚠️ Vid SM och landsdelsmästerskap är de fem C-mästerskapen (öppen, Dam, Vet Y, Vet Ä,
        /// Junior) vad förbundet delar ut, och rekord noteras i dem (C.3.7.1.1). Ett felklick där
        /// skulle ta bort fyra mästerskap, så valet erbjuds inte — ytan renderar rutan låst med
        /// skälet intill, och läsvägen ignorerar ett värde som ändå råkar ligga där.
        ///
        /// Utanför ett mästerskap finns inga mästerskapsmedaljer alls, så frågan ställs inte.
        /// </summary>
        public static bool CanChoose(string? competitionScope)
        {
            var scope = ChampionshipCategory.NormalizeScope(competitionScope);
            return scope is CompetitionScopeHelper.Klubbmasterskap
                          or CompetitionScopeHelper.Kretsmasterskap;
        }

        /// <summary>
        /// Arrangörens val, som det GÄLLER — alltså med nivåspärren tillämpad. Ett värde satt
        /// innan tävlingen blev ett SM slår aldrig igenom.
        /// </summary>
        public static bool PerWeaponGroup(string? competitionScope, bool storedValue) =>
            storedValue && CanChoose(competitionScope);

        /// <summary>Läser valet ur en sparad (icke publicerad) nod.</summary>
        public static bool PerWeaponGroup(IContent? competition)
        {
            if (competition == null) return false;
            return PerWeaponGroup(
                competition.GetValue<string>("competitionScope"),
                competition.GetValue<bool>(PropertyAlias));
        }

        /// <summary>
        /// Läser valet ur den publicerade cachen.
        ///
        /// ⚠️ Omfattningen läses OTYPAT. <c>competitionScope</c> kan vara en FlexibleDropdown,
        /// vars värdekonverterare kastar på <c>Value&lt;string&gt;()</c> — samma försiktighet som
        /// <c>CompetitionUrlProvider.ReadScopeValue</c>. <see cref="ChampionshipCategory"/> skalar
        /// av en eventuell JSON-array.
        /// </summary>
        public static bool PerWeaponGroup(IPublishedContent? competition)
        {
            if (competition == null) return false;
            // ⚠️ RÄTTAT 2026-09-20: den otypade läsningen här KASTADE på en tävling vars
            // omfattning lagrats som ren sträng — FlexibleDropdownens konverterare kör även på
            // Value(), inte bara på Value<string>(). CompetitionScopeHelper.ReadScope läser
            // råvärdet förbi konverteraren och normaliserar en JSON-inpackad array.
            return PerWeaponGroup(
                CompetitionScopeHelper.ReadScope(competition),
                competition.Value<bool>(PropertyAlias));
        }

        /// <summary>
        /// Klartext för ytan. Prisutdelningen och medaljpanelen skriver ut vilken indelning
        /// listan räknades i — utan den meningen går det inte att se om ett saknat damguld är
        /// arrangörens val eller ett fel.
        /// </summary>
        public static string Describe(bool perWeaponGroup) => perWeaponGroup
            ? "En uppsättning medaljer per vapengrupp — bäste skytt i vapengruppen får guld."
            : "Medaljer per mästerskapsklass — vapengrupp C delas i öppen, Dam, Vet Y, Vet Ä och Junior.";
    }
}
