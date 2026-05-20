using System.Collections.Generic;
using System.Linq;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Targeted tests for the A_opt refactor: A_opt_2 and A_opt_3 must follow the
    /// same level-2/3 merge rule as A2+A3 once A_opt is its own weapon class.
    /// A_opt_1 must continue to never merge.
    /// </summary>
    public class ClassMergingServiceTests
    {
        private static readonly ClassMergingService Service = new();

        [Fact]
        public void AOpt2AndAOpt3_MergeWhenBelowThreshold()
        {
            // 3 + 2 participants — both classes below the 5-shooter threshold
            var counts = new Dictionary<string, int>
            {
                ["A Opt 2"] = 3,
                ["A Opt 3"] = 2
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            Assert.Single(analysis.Suggestions);
            var suggestion = analysis.Suggestions[0];
            // The dedupe step removes the bidirectional twin, so we expect one suggestion
            // whose source is one of A_opt_2 / A_opt_3 and target is the other.
            Assert.Contains(suggestion.SourceClass, new[] { "A Opt 2", "A Opt 3" });
            Assert.Contains(suggestion.DefaultTarget!, new[] { "A Opt 2", "A Opt 3" });
            Assert.NotEqual(suggestion.SourceClass, suggestion.DefaultTarget);
            Assert.False(suggestion.RequiresAdminChoice);
        }

        [Fact]
        public void AOpt1_NeverMerges()
        {
            var counts = new Dictionary<string, int>
            {
                ["A Opt 1"] = 2,
                ["A Opt 2"] = 6  // above threshold; would otherwise be a merge target
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            // A Opt 1 is a level-1 class — should never appear as a merge source.
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "A Opt 1");
        }

        [Fact]
        public void AOpt_DoesNotCrossInto_PlainA()
        {
            // 2 of A_opt_2 and 2 of A2 — they must NOT merge with each other (different weapon groups)
            var counts = new Dictionary<string, int>
            {
                ["A Opt 2"] = 2,
                ["A2"] = 2,
                ["A3"] = 6
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            // A_opt_2 has no partner in its own group → no suggestion for it.
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "A Opt 2");

            // Plain A2 still suggests merging with A3 (same plain-A group).
            var a2Suggestion = analysis.Suggestions.FirstOrDefault(s => s.SourceClass == "A2");
            Assert.NotNull(a2Suggestion);
            Assert.Equal("A3", a2Suggestion!.DefaultTarget);
        }

        // ── A-family subgroups (AM/AP/AG): merge rules ────────────────────────────────
        // Per SPSF: levels (1, 2, 3) inside a subgroup may merge when <5 participants in
        // an individual level, but the subgroups themselves never merge with each other
        // or with the open A class. Each is its own weapon group.

        [Theory]
        [InlineData("AM2", "AM3")]
        [InlineData("AP2", "AP3")]
        [InlineData("AG2", "AG3")]
        public void AFamilySubgroup_Level2AndLevel3_MergeWhenBelowThreshold(string lower, string higher)
        {
            var counts = new Dictionary<string, int>
            {
                [lower] = 3,
                [higher] = 2
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            Assert.Single(analysis.Suggestions);
            var s = analysis.Suggestions[0];
            Assert.Contains(s.SourceClass, new[] { lower, higher });
            Assert.Contains(s.DefaultTarget!, new[] { lower, higher });
            Assert.NotEqual(s.SourceClass, s.DefaultTarget);
            Assert.False(s.RequiresAdminChoice);
        }

        [Theory]
        [InlineData("AM1", "AM2")]
        [InlineData("AP1", "AP2")]
        [InlineData("AG1", "AG2")]
        public void AFamilySubgroup_Level1_NeverMerges(string level1Class, string level2Partner)
        {
            // Level 1 never merges — same rule as A1.
            var counts = new Dictionary<string, int>
            {
                [level1Class] = 2,
                [level2Partner] = 6
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == level1Class);
        }

        [Fact]
        public void AFamilySubgroups_DoNotMergeWithEachOther()
        {
            // 2 of AM2 and 2 of AP2 and 2 of AG2 — they must NOT merge across subgroups
            // even though all are below the 5-threshold. Per-subgroup rule only.
            var counts = new Dictionary<string, int>
            {
                ["AM2"] = 2,
                ["AP2"] = 2,
                ["AG2"] = 2
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            // Each subgroup has no level-2/3 partner of its own → no suggestions.
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "AM2");
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "AP2");
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "AG2");
        }

        [Fact]
        public void AFamilySubgroup_DoesNotMergeWithOpenA()
        {
            // 2 of AM2 and 2 of A2 — never merge across subgroup ↔ open A boundary,
            // even though pooling happens for medal eligibility separately.
            var counts = new Dictionary<string, int>
            {
                ["AM2"] = 2,
                ["A2"] = 2,
                ["A3"] = 6
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");

            // AM2 has no AM3 partner → no suggestion for it (must not link to A2/A3 either).
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "AM2");

            // Plain A2 still suggests A3 (its own subgroup partner exists).
            var a2 = analysis.Suggestions.FirstOrDefault(s => s.SourceClass == "A2");
            Assert.NotNull(a2);
            Assert.Equal("A3", a2!.DefaultTarget);
        }

        [Theory]
        [InlineData("AM2", "AM3", "AM2+3")]
        [InlineData("AP2", "AP3", "AP2+3")]
        [InlineData("AG2", "AG3", "AG2+3")]
        public void AFamilySubgroup_CombinedClassName_UsesCompactForm(string a, string b, string expected)
        {
            // Display name uses compact form, never the underscore-style enum spelling.
            Assert.Equal(expected, ClassMergingService.GetCombinedClassName(a, b));
        }

        [Theory]
        [InlineData("AM2", "AM3", "AM")]
        [InlineData("AP2", "AP3", "AP")]
        [InlineData("AG2", "AG3", "AG")]
        public void AFamilySubgroup_ReasonText_UsesFriendlyLabel(string lower, string higher, string label)
        {
            var counts = new Dictionary<string, int>
            {
                [lower] = 3,
                [higher] = 2
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var s = analysis.Suggestions.Single();
            // Reason text should say "vapengrupp AM/AP/AG", not the underscore-style "A_M".
            Assert.Contains($"vapengrupp {label}", s.Reason);
            Assert.DoesNotContain("A_M", s.Reason);
            Assert.DoesNotContain("A_P", s.Reason);
            Assert.DoesNotContain("A_G", s.Reason);
        }

        // ── SHB 2026 §D.2.3 (Teknisk specifikation §4): Vapengrupp C ────────────────
        //
        // | Original (N<5) | Priority 1   | Priority 2 |
        // | Dam C 1/2/3    | C N (same level) | —      |
        // | Junior C       | C Klass      | Dam Klass |
        // | Veteran Y      | C Klass      | Dam Klass |
        // | Veteran Ä      | C Klass      | Dam Klass |

        [Fact]
        public void DamC2_MergesIntoC2_NoFallback()
        {
            var counts = new Dictionary<string, int> { ["C2 Dam"] = 3, ["C2"] = 6 };
            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var s = analysis.Suggestions.Single(x => x.SourceClass == "C2 Dam");
            Assert.Equal("C2", s.DefaultTarget);
            Assert.Equal(new[] { "C2" }, s.PossibleTargets);
            Assert.False(s.RequiresAdminChoice);
        }

        [Fact]
        public void DamC3_NoCorrespondingOpenClass_FallsBackToNearestLevel()
        {
            // Per SHB §3 FR-104 (clarified 2026-05-19): only Klass 1 has the absolute spärr.
            // When Dam C 3's priority-1 target (C 3) doesn't exist, the class falls back to
            // the nearest competence level — C 2 (or C 2 Dam) — within the same weapon group.
            var counts = new Dictionary<string, int>
            {
                ["C3 Dam"] = 1,
                ["C2"] = 3,
                ["C2 Dam"] = 2
            };
            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var damC3 = analysis.Suggestions.Single(s => s.SourceClass == "C3 Dam");

            Assert.True(damC3.RequiresAdminChoice);
            Assert.Equal(new[] { "C2", "C2 Dam" }, damC3.PossibleTargets);
            Assert.Equal("C2", damC3.DefaultTarget);
        }

        [Fact]
        public void DamC1_NoCorrespondingOpenClass_NoFallback()
        {
            // Klass 1 has the absolute spärr (FR-102) — Dam C 1 cannot fall back to Klass 2/3.
            var counts = new Dictionary<string, int> { ["C1 Dam"] = 1, ["C2"] = 3 };
            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            Assert.DoesNotContain(analysis.Suggestions, s => s.SourceClass == "C1 Dam");
        }

        [Fact]
        public void OpenC2AndC3_MergeAsLevel23_WhenBothPresent()
        {
            // FR-104 clarification: in Vapengrupp C the binary 2↔3 rule applies as fallback
            // (same logic as A/B/R), once category mergers are exhausted.
            var counts = new Dictionary<string, int> { ["C2"] = 3, ["C3"] = 2 };
            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            Assert.Single(analysis.Suggestions);
            var s = analysis.Suggestions[0];
            Assert.Contains(s.SourceClass, new[] { "C2", "C3" });
            Assert.Contains(s.DefaultTarget!, new[] { "C2", "C3" });
            Assert.NotEqual(s.SourceClass, s.DefaultTarget);
        }

        [Fact]
        public void CombinedClassName_DamCrossingLevel_TaggedWithSourceLevel()
        {
            // Dam C 3 → C 2 should render as "C2+Dam3" so it's clear which Dam-level joined.
            Assert.Equal("C2+Dam3", ClassMergingService.GetCombinedClassName("C3 Dam", "C2"));
            Assert.Equal("C2+Dam3", ClassMergingService.GetCombinedClassName("C2", "C3 Dam"));
        }

        [Fact]
        public void CombinedClassName_TwoDamDifferentLevels_PreservesDamMarker()
        {
            // Dam C 3 + Dam C 2 should render as "C2+3 Dam".
            Assert.Equal("C2+3 Dam", ClassMergingService.GetCombinedClassName("C3 Dam", "C2 Dam"));
            Assert.Equal("C2+3 Dam", ClassMergingService.GetCombinedClassName("C2 Dam", "C3 Dam"));
        }

        [Fact]
        public void CVetY_OffersOpenC_AndDamC_AsPrioritizedTargets()
        {
            // Exactly the competition-2173 case: C Vet Y exists alongside C2 and C2/C3 Dam.
            // Per spec, all three are valid targets (C2 = prio 1, Dam classes = prio 2),
            // admin picks. Default = the priority-1 open class.
            var counts = new Dictionary<string, int>
            {
                ["A3"] = 5,
                ["C Vet Y"] = 1,
                ["C2"] = 2,
                ["C2 Dam"] = 4,
                ["C3 Dam"] = 1
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var vetY = analysis.Suggestions.Single(s => s.SourceClass == "C Vet Y");

            Assert.True(vetY.RequiresAdminChoice);
            Assert.Equal("C2", vetY.DefaultTarget);
            Assert.Equal(new[] { "C2", "C2 Dam", "C3 Dam" }, vetY.PossibleTargets);
            Assert.Contains("Veteran Yngre", vetY.Reason);
        }

        [Fact]
        public void CVetA_OffersOpenC_AndDamC_NotCascadeToVetY()
        {
            // The previous code cascaded Vet Ä → Vet Y. Per SHB 2026 spec there is no such
            // cascade — Vet Ä goes directly to open C (priority 1) or Dam C (priority 2).
            var counts = new Dictionary<string, int>
            {
                ["C Vet Ä"] = 2,
                ["C Vet Y"] = 4, // exists, but should NOT be the target
                ["C2"] = 3,
                ["C1 Dam"] = 2
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var vetA = analysis.Suggestions.Single(s => s.SourceClass == "C Vet Ä");

            Assert.True(vetA.RequiresAdminChoice);
            Assert.DoesNotContain("C Vet Y", vetA.PossibleTargets);
            Assert.Equal(new[] { "C2", "C1 Dam" }, vetA.PossibleTargets);
        }

        [Fact]
        public void CJunior_OffersOpenC_AndDamC()
        {
            var counts = new Dictionary<string, int>
            {
                ["C Jun"] = 2,
                ["C1"] = 4,
                ["C2"] = 6,
                ["C3 Dam"] = 3
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var jun = analysis.Suggestions.Single(s => s.SourceClass == "C Jun");

            Assert.True(jun.RequiresAdminChoice);
            Assert.Equal("C1", jun.DefaultTarget); // first priority-1 open class
            Assert.Equal(new[] { "C1", "C2", "C3 Dam" }, jun.PossibleTargets);
        }

        [Fact]
        public void CategoryClass_FallsBackToDamWhenNoOpenAvailable()
        {
            // No open C-classes exist — Junior must still be mergeable via priority-2 Dam.
            var counts = new Dictionary<string, int>
            {
                ["C Jun"] = 1,
                ["C2 Dam"] = 4
            };

            var analysis = Service.AnalyzeFromCounts(counts, "Precision");
            var jun = analysis.Suggestions.Single(s => s.SourceClass == "C Jun");
            Assert.Equal("C2 Dam", jun.DefaultTarget);
            Assert.Equal(new[] { "C2 Dam" }, jun.PossibleTargets);
        }

        [Fact]
        public void CombinedClassName_VetIntoDam_TargetPlusVet()
        {
            // C Vet Y → C2 Dam should render as "C2 Dam+Vet" (target carrying the suffix).
            Assert.Equal("C2 Dam+Vet", ClassMergingService.GetCombinedClassName("C Vet Y", "C2 Dam"));
            Assert.Equal("C2 Dam+Vet", ClassMergingService.GetCombinedClassName("C Vet Ä", "C2 Dam"));
        }

        [Fact]
        public void CombinedClassName_JunIntoOpen_TargetPlusJun()
        {
            Assert.Equal("C2+Jun", ClassMergingService.GetCombinedClassName("C Jun", "C2"));
            Assert.Equal("C1 Dam+Jun", ClassMergingService.GetCombinedClassName("C Jun", "C1 Dam"));
        }

        // ── Multi-source merge into a single target (BuildMergeGroupLookup) ──────────
        // When the admin ticks several rows in the modal that all target the same class,
        // the result must be ONE combined group, not several.

        [Fact]
        public void BuildMergeGroupLookup_ThreeSourcesIntoOneTarget_ProducesOneCombinedGroup()
        {
            // Competition 2173 scenario: C2 Dam, C3 Dam, and C Vet Y all merge into C2.
            // Expected: a single group "C2+Dam+Vet" with all four classes mapping to it.
            var merges = new List<ClassMergeAction>
            {
                new() { SourceClass = "C2 Dam", TargetClass = "C2" },
                new() { SourceClass = "C3 Dam", TargetClass = "C2" },
                new() { SourceClass = "C Vet Y", TargetClass = "C2" },
            };

            var lookup = ClassMergingService.BuildMergeGroupLookup(merges);

            Assert.Equal("C2+Dam+Vet", lookup["C2"]);
            Assert.Equal("C2+Dam+Vet", lookup["C2 Dam"]);
            Assert.Equal("C2+Dam+Vet", lookup["C3 Dam"]);
            Assert.Equal("C2+Dam+Vet", lookup["C Vet Y"]);
        }

        [Fact]
        public void BuildMergeGroupLookup_SingleMerge_KeepsSingleSourceNaming()
        {
            // One merge → use the single-source GetCombinedClassName, not the
            // multi-source collapsed form.
            var merges = new List<ClassMergeAction>
            {
                new() { SourceClass = "C3 Dam", TargetClass = "C2" },
            };

            var lookup = ClassMergingService.BuildMergeGroupLookup(merges);

            Assert.Equal("C2+Dam3", lookup["C2"]);
            Assert.Equal("C2+Dam3", lookup["C3 Dam"]);
        }

        [Fact]
        public void BuildMergeGroupLookup_ChainedMerges_ResolveToFinalTarget()
        {
            // A → B and B → C must collapse to a single group rooted at C.
            var merges = new List<ClassMergeAction>
            {
                new() { SourceClass = "C2 Dam", TargetClass = "C2" },
                new() { SourceClass = "C2", TargetClass = "C3" },
            };

            var lookup = ClassMergingService.BuildMergeGroupLookup(merges);

            Assert.Equal(lookup["C3"], lookup["C2"]);
            Assert.Equal(lookup["C3"], lookup["C2 Dam"]);
            Assert.StartsWith("C3+", lookup["C3"]);
        }

        [Fact]
        public void BuildMergeGroupLookup_EmptyOrNull_ReturnsEmptyLookup()
        {
            Assert.Empty(ClassMergingService.BuildMergeGroupLookup(null));
            Assert.Empty(ClassMergingService.BuildMergeGroupLookup(new List<ClassMergeAction>()));
        }

        [Fact]
        public void CombinedClassNameMulti_CollapsesMultipleDamLevelsUnderOneSuffix()
        {
            // C2 Dam + C3 Dam both merging into C2 → just one "Dam" suffix (not "Dam+Dam3").
            var name = ClassMergingService.GetCombinedClassNameMulti("C2", new[] { "C2 Dam", "C3 Dam" });
            Assert.Equal("C2+Dam", name);
        }

        [Fact]
        public void CombinedClassNameMulti_MixedCategories_OrdersSuffixesConsistently()
        {
            // Open-level numbers come first, then Dam, Jun, Vet — alphabetically stable.
            var name = ClassMergingService.GetCombinedClassNameMulti("C2",
                new[] { "C3", "C2 Dam", "C Jun", "C Vet Y" });
            Assert.Equal("C2+3+Dam+Jun+Vet", name);
        }
    }
}
