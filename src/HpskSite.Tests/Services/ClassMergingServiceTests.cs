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
    }
}
