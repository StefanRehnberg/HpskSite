using System.Collections.Generic;
using System.Linq;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// ⚠️ Den här sviten finns för Tomelilla PK:s klubbmästerskap i R-fält 2026-09-12 (tävling
    /// 4900): R1 = 3 deltagare och R2 = 4, alltså BÅDA under gränsen — och noll förslag, eftersom
    /// klass 1 aldrig slås samman och det inte finns någon R3 att slå ihop R2 med. Ytan renderade
    /// det som "Alla klasser har 5 eller fler deltagare", vilket är falskt, och klubben drog
    /// slutsatsen att funktionen saknas i fältskytte.
    ///
    /// "Inga förslag" och "inga klasser under gränsen" är två olika svar. Testerna nedan pinnar
    /// att motorn kan skilja dem åt och att den säger VILKEN regel som stoppade varje klass.
    /// </summary>
    public class ClassMergingBlockReasonTests
    {
        private static readonly ClassMergingService Service = new();

        private static ClassInfo Class(ClassMergeAnalysis a, string name) =>
            a.Classes.Single(c => c.ClassName == name);

        [Fact]
        public void TomelillaRFalt_BothClassesBelowFive_ProduceNoSuggestionsButNamedReasons()
        {
            var counts = new Dictionary<string, int> { ["R1"] = 3, ["R2"] = 4 };

            var analysis = Service.AnalyzeFromCounts(counts, "Faltskytte");

            Assert.Empty(analysis.Suggestions);
            Assert.All(analysis.Classes, c => Assert.True(c.BelowThreshold));

            // Klass 1: den absoluta spärren (SHB D.2.3), namngiven.
            Assert.Contains("Klass 1", Class(analysis, "R1").MergeBlockReason);

            // Klass 2: regeln tillåter R3 — men R3 finns inte i den här tävlingen, och det är
            // vad texten måste säga. "Klass 1"-skälet vore fel svar här.
            var r2 = Class(analysis, "R2").MergeBlockReason;
            Assert.Contains("R3", r2);
            Assert.DoesNotContain("Klass 1", r2);
        }

        [Fact]
        public void ClassWithFiveOrMoreParticipants_HasNoBlockReason()
        {
            // Skälet besvarar "varför står den här klassen UNDER fem kvar?". En klass över
            // gränsen har ingen sådan fråga, och en text där vore brus.
            var counts = new Dictionary<string, int> { ["R1"] = 3, ["R2"] = 7 };

            var analysis = Service.AnalyzeFromCounts(counts, "Faltskytte");

            Assert.Equal("", Class(analysis, "R2").MergeBlockReason);
            Assert.NotEqual("", Class(analysis, "R1").MergeBlockReason);
        }

        [Fact]
        public void ClassWithASuggestion_HasNoBlockReason()
        {
            // B3 kan slås samman med B2 → raden bär ett förslag, inte ett skäl. Bär den båda
            // säger ytan emot sig själv i samma cell.
            var counts = new Dictionary<string, int> { ["B2"] = 4, ["B3"] = 3 };

            var analysis = Service.AnalyzeFromCounts(counts, "Faltskytte");

            Assert.NotEmpty(analysis.Suggestions);
            Assert.Equal("", Class(analysis, "B2").MergeBlockReason);
            Assert.Equal("", Class(analysis, "B3").MergeBlockReason);
        }

        [Fact]
        public void RClassesOutsideFaltAndMilsnabb_SayTheDisciplineRule()
        {
            // Samma klasser i en precisionstävling: R2↔R3 gäller inte där, och skälet ska säga
            // just det i stället för att skylla på en saknad målklass.
            var counts = new Dictionary<string, int> { ["R2"] = 3, ["R3"] = 2 };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            Assert.Empty(analysis.Suggestions);
            Assert.Contains("vapengrupp R", Class(analysis, "R2").MergeBlockReason);
        }

        [Fact]
        public void MagnumClasses_SayMagnumHasNoClassDivision()
        {
            var counts = new Dictionary<string, int> { ["M1"] = 2, ["M2"] = 3 };

            var analysis = Service.AnalyzeFromCounts(counts, "MagnumFalt");

            Assert.Empty(analysis.Suggestions);
            Assert.Contains("Magnum", Class(analysis, "M1").MergeBlockReason);
        }

        // ── Dam-klassen på nivå 1 ────────────────────────────────────────────────────────────
        // Fynd under samma genomgång: `IsClass1` svarade true även för "C1 Dam", så
        // BuildSuggestion returnerade null innan C/L-grenen hann köras och dess level==1-fall
        // var död kod. Specens §4-tabell ger uttryckligen "Dam C 1 → C 1" som prioritet 1.

        [Fact]
        public void DamClassLevel1_MergesIntoTheOpenClassAtTheSameLevel()
        {
            var counts = new Dictionary<string, int> { ["C1 Dam"] = 2, ["C1"] = 8 };

            var analysis = Service.AnalyzeFromCounts(counts, "Faltskytte");

            var suggestion = Assert.Single(analysis.Suggestions);
            Assert.Equal("C1 Dam", suggestion.SourceClass);
            Assert.Equal("C1", suggestion.DefaultTarget);
            Assert.False(suggestion.RequiresAdminChoice);
        }

        [Fact]
        public void DamClassLevel1_NeverCrossesToLevel2Or3()
        {
            // FR-102 gäller att KORSA nivå. Finns ingen öppen C1 får Dam C 1 stå kvar ensam —
            // aldrig ett förslag om C2.
            var counts = new Dictionary<string, int> { ["C1 Dam"] = 2, ["C2"] = 9, ["C3"] = 6 };

            var analysis = Service.AnalyzeFromCounts(counts, "Faltskytte");

            Assert.Empty(analysis.Suggestions);
            Assert.Contains("C1", Class(analysis, "C1 Dam").MergeBlockReason);
        }

        [Fact]
        public void OpenClass1_StillNeverMerges_EvenWhenItsDamCounterpartExists()
        {
            var counts = new Dictionary<string, int> { ["C1"] = 2, ["C1 Dam"] = 7 };

            var analysis = Service.AnalyzeFromCounts(counts, "Faltskytte");

            // Bara Dam-klassen kan flyttas, och den ligger över gränsen här — alltså inget
            // förslag, och den öppna klass 1 bär sin spärr.
            Assert.Empty(analysis.Suggestions);
            Assert.Contains("Klass 1", Class(analysis, "C1").MergeBlockReason);
        }
    }
}
