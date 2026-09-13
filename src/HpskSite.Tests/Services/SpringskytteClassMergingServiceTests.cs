using System.Collections.Generic;
using System.Linq;
using HpskSite.CompetitionTypes.Springskytte.Models;
using HpskSite.CompetitionTypes.Springskytte.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Springskyttets klassammanslagning, SHB 2026 L.2.3.1 + L.2.12.1.
    ///
    /// ⚠️ Regeln är en ANNAN än precisionsfamiljens: här finns inga kompetensklasser (L.2.5–7
    /// "Ej tillämpligt") och alltså ingen klass 1-spärr — i stället slås ÅLDERSklasser samman,
    /// och de sex ungdoms-/juniorklasserna är undantagna. Testerna pinnar den skillnaden, så en
    /// framtida "förenkling" som pekar springskyttet mot precisionens motor faller här.
    /// </summary>
    public class SpringskytteClassMergingServiceTests
    {
        private static readonly SpringskytteClassMergingService Service = new();

        private static SpringskytteClassCount C(string weapon, string age, int n) =>
            new() { WeaponClass = weapon, AgeGenderClass = age, ParticipantCount = n };

        private static SpringskytteClassInfo Cls(SpringskytteMergeAnalysis a, string weapon, string age) =>
            a.Classes.Single(c => c.WeaponClass == weapon && c.AgeGenderClass == age);

        [Fact]
        public void AgeClassesBelowFive_MayMergeWithinTheSameWeaponClass()
        {
            var analysis = Service.Analyze(new[] { C("C", "H 50", 2), C("C", "H 35", 3) });

            Assert.Equal(2, analysis.Suggestions.Count);
            var s = analysis.Suggestions.Single(x => x.SourceClass == "H 50");
            Assert.Contains("C|H 35", s.PossibleTargets);
            // SHB ger rätten till tävlingsledningen — aldrig ett förvalt mål som läser som
            // förbundets rekommendation.
            Assert.True(s.RequiresAdminChoice);
        }

        [Fact]
        public void MergeNeverCrossesWeaponClass()
        {
            // Springskyttet publicerar per vapengrupp; en A-klass och en C-klass är två lopp.
            var analysis = Service.Analyze(new[] { C("A", "H 50", 2), C("C", "H 35", 3) });

            Assert.All(analysis.Suggestions, s => Assert.Empty(s.PossibleTargets.Where(t => !t.StartsWith(s.WeaponClass + "|"))));
            Assert.Empty(analysis.Suggestions);   // varsin ensam klass i sin vapengrupp
            Assert.Contains("vapengrupp A", Cls(analysis, "A", "H 50").MergeBlockReason);
        }

        [Theory]
        [InlineData("D jun")]
        [InlineData("H jun")]
        [InlineData("D 15")]
        [InlineData("H 15")]
        [InlineData("D 18")]
        [InlineData("H 18")]
        public void YouthAndJuniorClasses_AreNeverMerged(string protectedClass)
        {
            var analysis = Service.Analyze(new[] { C("C", protectedClass, 2), C("C", "H 21", 3), C("C", "D 21", 4) });

            // Varken som källa …
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == protectedClass);
            Assert.Contains("aldrig", Cls(analysis, "C", protectedClass).MergeBlockReason);

            // … eller som mål.
            var key = SpringskytteClassMergingService.MakeKey("C", protectedClass);
            Assert.All(analysis.Suggestions, s => Assert.DoesNotContain(key, s.PossibleTargets));
        }

        [Fact]
        public void SupportHandClasses_OnlyMergeWithOtherSupportHandClasses()
        {
            // L.2.12.1: 65 och 70 får använda stödhand på alla stationer. "Samma förutsättningar"
            // i L.2.3.1 går därför vid 65.
            var analysis = Service.Analyze(new[] { C("C", "H 65", 2), C("C", "H 70", 1), C("C", "H 50", 3) });

            var s65 = analysis.Suggestions.Single(x => x.SourceClass == "H 65");
            Assert.Equal(new[] { "C|H 70" }, s65.PossibleTargets);

            // H 50 är ensam på sin sida av stödhandsgränsen → inget förslag, och skälet ska
            // peka på förutsättningen och inte på att klassen skulle vara för liten.
            Assert.DoesNotContain(analysis.Suggestions, x => x.SourceClass == "H 50");
            Assert.Contains("stödhand", Cls(analysis, "C", "H 50").MergeBlockReason);
        }

        [Fact]
        public void SupportHandClassAlone_SaysWhyTheOthersDoNotQualify()
        {
            var analysis = Service.Analyze(new[] { C("C", "H 70", 2), C("C", "H 35", 3), C("C", "D 35", 4) });

            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "H 70");
            Assert.Contains("stödhand", Cls(analysis, "C", "H 70").MergeBlockReason);
        }

        [Fact]
        public void ClassWithFiveOrMore_IsNeitherSuggestedNorGivenAReason()
        {
            var analysis = Service.Analyze(new[] { C("C", "H 35", 7), C("C", "H 50", 2) });

            Assert.Equal("", Cls(analysis, "C", "H 35").MergeBlockReason);
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "H 35");
            // ⚠️ Men den får vara MÅL: en klass över fem är precis vad en liten klass ska
            // kunna slås samman med.
            Assert.Contains("C|H 35", analysis.Suggestions.Single().PossibleTargets);
        }

        [Fact]
        public void UnknownClassStrings_AreNeverOffered()
        {
            // Dev-data bär tomma värden och "vuxen-tvahand". Vi kan inte påstå att en klass vi
            // inte känner igen har "samma förutsättningar" som någon annan.
            var analysis = Service.Analyze(new[] { C("C", "vuxen-tvahand", 3), C("C", "", 2), C("C", "H 35", 2) });

            Assert.All(analysis.Suggestions, s => Assert.All(s.PossibleTargets,
                t => Assert.DoesNotContain("vuxen", t)));
            Assert.Contains("känns inte igen", Cls(analysis, "C", "vuxen-tvahand").MergeBlockReason);
        }

        // ── Uppslaget ───────────────────────────────────────────────────────────────────────

        [Fact]
        public void MergeLookup_CollapsesMultipleSourcesIntoOneGroup()
        {
            var lookup = SpringskytteClassMergingService.BuildMergeGroupLookup(new[]
            {
                new SpringskytteClassMergeAction { SourceKey = "C|H 50", TargetKey = "C|H 35" },
                new SpringskytteClassMergeAction { SourceKey = "C|H 60", TargetKey = "C|H 35" }
            });

            // EN grupp, ett namn — inte ett namn per merge med sista skrivningen vinnande.
            Assert.Equal(lookup["C|H 50"], lookup["C|H 35"]);
            Assert.Equal(lookup["C|H 60"], lookup["C|H 35"]);
            Assert.Equal("C|H 35+H 50+H 60", lookup["C|H 35"]);
        }

        [Fact]
        public void MergeLookup_CollapsesChains()
        {
            var lookup = SpringskytteClassMergingService.BuildMergeGroupLookup(new[]
            {
                new SpringskytteClassMergeAction { SourceKey = "C|H 60", TargetKey = "C|H 50" },
                new SpringskytteClassMergeAction { SourceKey = "C|H 50", TargetKey = "C|H 35" }
            });

            Assert.Single(lookup.Values.Distinct());
            Assert.Equal(3, lookup.Count);
        }

        [Fact]
        public void MergeLookup_EmptyOrNull_IsEmpty()
        {
            Assert.Empty(SpringskytteClassMergingService.BuildMergeGroupLookup(null));
            Assert.Empty(SpringskytteClassMergingService.BuildMergeGroupLookup(new List<SpringskytteClassMergeAction>()));
        }

        [Fact]
        public void MergeLookup_IgnoresAForeignConfigShape()
        {
            // Fältskyttet lagrar {sourceClass,targetClass} i SAMMA competition-egenskap. Läses
            // den formen hit blir nycklarna tomma, och resultatet ska vara "ingen
            // sammanslagning" — aldrig en felaktig gruppering.
            var lookup = SpringskytteClassMergingService.BuildMergeGroupLookup(new[]
            {
                new SpringskytteClassMergeAction { SourceKey = "", TargetKey = "" }
            });
            Assert.Empty(lookup);
        }

        [Theory]
        [InlineData("H 65", true)]
        [InlineData("D 70", true)]
        [InlineData("A-H 65", true)]
        [InlineData("H 60", false)]
        [InlineData("D jun", false)]
        [InlineData("", false)]
        public void SupportHandBoundary_IsAt65(string cls, bool expected)
        {
            Assert.Equal(expected, SpringskytteClassMergingService.SupportHandAllowed(cls));
        }
    }
}
