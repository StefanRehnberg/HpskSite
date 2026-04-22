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
    }
}
