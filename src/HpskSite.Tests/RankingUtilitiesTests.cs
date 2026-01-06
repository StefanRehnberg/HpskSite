using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for RankingUtilities
    /// Tests ranking, tie-breaking, and grouping functionality
    /// </summary>
    public class RankingUtilitiesTests
    {
        public class TestParticipant
        {
            public int Id { get; set; }
            public string Name { get; set; }
            public decimal Score { get; set; }
            public string Class { get; set; }
            public int InnerTens { get; set; }
        }

        // ============ RankWithTieBreakers Tests ============

        [Fact]
        public void RankWithTieBreakers_WithDistinctScores_AssignsSequentialRanks()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Id = 1, Name = "Alice", Score = 100 },
                new TestParticipant { Id = 2, Name = "Bob", Score = 90 },
                new TestParticipant { Id = 3, Name = "Charlie", Score = 95 }
            };

            var result = RankingUtilities.RankWithTieBreakers(
                participants,
                p => -p.Score  // Higher score = better (descending)
            );

            Assert.Equal(3, result.Count);
            Assert.Equal(1, result[0].rank);  // Alice (100) = rank 1
            Assert.Equal(2, result[1].rank);  // Charlie (95) = rank 2
            Assert.Equal(3, result[2].rank);  // Bob (90) = rank 3
        }

        [Fact]
        public void RankWithTieBreakers_WithTiedScores_AssignsSameRank()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Id = 1, Name = "Alice", Score = 100, InnerTens = 8 },
                new TestParticipant { Id = 2, Name = "Bob", Score = 100, InnerTens = 6 }
            };

            var result = RankingUtilities.RankWithTieBreakers(
                participants,
                p => -p.Score,        // Primary: higher score
                p => -p.InnerTens     // Tiebreaker: more inner tens
            );

            Assert.Equal(2, result.Count);
            Assert.Equal(1, result[0].rank);  // Alice (100, 8X's) - Both have rank 1 (tied on primary)
            Assert.Equal(1, result[1].rank);  // Bob (100, 6X's) - Both have rank 1 (tied on primary)
        }

        [Fact]
        public void RankWithTieBreakers_WithMultipleTiebreakers_AppliesInOrder()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Id = 1, Name = "Alice", Score = 100, InnerTens = 8 },
                new TestParticipant { Id = 2, Name = "Bob", Score = 100, InnerTens = 8 },
                new TestParticipant { Id = 3, Name = "Charlie", Score = 95, InnerTens = 10 }
            };

            var result = RankingUtilities.RankWithTieBreakers(
                participants,
                p => -p.Score,            // Primary: higher score
                p => -p.InnerTens,        // Tiebreaker 1: more inner tens
                p => p.Name               // Tiebreaker 2: alphabetical name
            );

            // Alice and Bob both have 100/8, so both get rank 1 (tied on primary)
            Assert.Equal(1, result[0].rank);  // Alice (100, 8X's) - Sorted alphabetically first
            Assert.Equal(1, result[1].rank);  // Bob (100, 8X's) - Both tied on primary score
            Assert.Equal(3, result[2].rank);  // Charlie (95) - Gets rank 3 (two tied for 1st, so next is 3rd)
        }

        // ============ GroupByProperty Tests ============

        [Fact]
        public void GroupByProperty_WithValidGroups_ReturnsCorrectGrouping()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Class = "A", Name = "Alice" },
                new TestParticipant { Class = "B", Name = "Bob" },
                new TestParticipant { Class = "A", Name = "Charlie" }
            };

            var result = RankingUtilities.GroupByProperty(participants, p => p.Class);

            Assert.Equal(2, result.Count);
            Assert.Contains("A", result.Keys);
            Assert.Contains("B", result.Keys);
            Assert.Equal(2, result["A"].Count);  // Alice and Charlie
            Assert.Equal(1, result["B"].Count);  // Bob
        }

        [Fact]
        public void GroupByProperty_WithNull_ReturnsEmptyDictionary()
        {
            var result = RankingUtilities.GroupByProperty<TestParticipant>(null, p => p.Class);
            Assert.Empty(result);
        }

        // ============ RankByGroup Tests ============

        [Fact]
        public void RankByGroup_WithMultipleGroups_RanksWithinEachGroup()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Class = "A", Score = 100 },
                new TestParticipant { Class = "A", Score = 90 },
                new TestParticipant { Class = "B", Score = 85 },
                new TestParticipant { Class = "B", Score = 95 }
            };

            var result = RankingUtilities.RankByGroup(
                participants,
                p => p.Class,
                p => -p.Score
            );

            Assert.Equal(2, result.Count);
            Assert.Equal(2, result["A"].Count);
            Assert.Equal(2, result["B"].Count);
            Assert.Equal(1, result["A"][0].rank);  // Class A: 100 = rank 1
            Assert.Equal(2, result["A"][1].rank);  // Class A: 90 = rank 2
            Assert.Equal(1, result["B"][0].rank);  // Class B: 95 = rank 1
            Assert.Equal(2, result["B"][1].rank);  // Class B: 85 = rank 2
        }

        // ============ SortDescending Tests ============

        [Fact]
        public void SortDescending_WithValidScores_SortsHighestFirst()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Score = 100 },
                new TestParticipant { Score = 90 },
                new TestParticipant { Score = 95 }
            };

            var result = RankingUtilities.SortDescending(participants, p => p.Score);

            Assert.Equal(100, result[0].Score);
            Assert.Equal(95, result[1].Score);
            Assert.Equal(90, result[2].Score);
        }

        // ============ SortAscending Tests ============

        [Fact]
        public void SortAscending_WithValidScores_SortsLowestFirst()
        {
            var participants = new List<TestParticipant>
            {
                new TestParticipant { Score = 100 },
                new TestParticipant { Score = 90 },
                new TestParticipant { Score = 95 }
            };

            var result = RankingUtilities.SortAscending(participants, p => p.Score);

            Assert.Equal(90, result[0].Score);
            Assert.Equal(95, result[1].Score);
            Assert.Equal(100, result[2].Score);
        }

        // ============ GetTopN Tests ============

        [Fact]
        public void GetTopN_WithValidCount_ReturnsTopItems()
        {
            var ranked = new List<(TestParticipant item, int rank)>
            {
                (new TestParticipant { Name = "Alice" }, 1),
                (new TestParticipant { Name = "Bob" }, 2),
                (new TestParticipant { Name = "Charlie" }, 3),
                (new TestParticipant { Name = "David" }, 4)
            };

            var result = RankingUtilities.GetTopN(ranked, 2);

            Assert.Equal(2, result.Count);
            Assert.Equal("Alice", result[0].Name);
            Assert.Equal("Bob", result[1].Name);
        }

        [Fact]
        public void GetTopN_WithCountGreaterThanItems_ReturnsAll()
        {
            var ranked = new List<(TestParticipant item, int rank)>
            {
                (new TestParticipant { Name = "Alice" }, 1),
                (new TestParticipant { Name = "Bob" }, 2)
            };

            var result = RankingUtilities.GetTopN(ranked, 10);

            Assert.Equal(2, result.Count);
        }

        // ============ GetRankRange Tests ============

        [Fact]
        public void GetRankRange_WithValidRange_ReturnsItemsInRange()
        {
            var ranked = new List<(TestParticipant item, int rank)>
            {
                (new TestParticipant { Name = "Alice" }, 1),
                (new TestParticipant { Name = "Bob" }, 2),
                (new TestParticipant { Name = "Charlie" }, 3),
                (new TestParticipant { Name = "David" }, 4)
            };

            var result = RankingUtilities.GetRankRange(ranked, 2, 3);

            Assert.Equal(2, result.Count);
            Assert.Equal("Bob", result[0].Name);
            Assert.Equal("Charlie", result[1].Name);
        }

        // ============ GetRank Tests ============

        [Fact]
        public void GetRank_WithValidRank_ReturnsItemsWithThatRank()
        {
            var ranked = new List<(TestParticipant item, int rank)>
            {
                (new TestParticipant { Name = "Alice" }, 1),
                (new TestParticipant { Name = "Bob" }, 2),
                (new TestParticipant { Name = "Charlie" }, 2),
                (new TestParticipant { Name = "David" }, 3)
            };

            var result = RankingUtilities.GetRank(ranked, 2);

            Assert.Equal(2, result.Count);
            Assert.Contains(result, item => item.Name == "Bob");
            Assert.Contains(result, item => item.Name == "Charlie");
        }

        // ============ GetPlacementLabel Tests ============

        [Theory]
        [InlineData(1, "🥇 Gold")]
        [InlineData(2, "🥈 Silver")]
        [InlineData(3, "🥉 Bronze")]
        [InlineData(4, "4th Place")]
        [InlineData(5, "5th Place")]
        public void GetPlacementLabel_WithRank_ReturnsCorrectLabel(int rank, string expected)
        {
            var result = RankingUtilities.GetPlacementLabel(rank);
            Assert.Equal(expected, result);
        }

        // ============ IsTied Tests ============

        [Fact]
        public void IsTied_WithSameRank_ReturnsTrue()
        {
            var item1 = (new TestParticipant { Name = "Alice" }, 1);
            var item2 = (new TestParticipant { Name = "Bob" }, 1);

            var result = RankingUtilities.IsTied(item1, item2);

            Assert.True(result);
        }

        [Fact]
        public void IsTied_WithDifferentRank_ReturnsFalse()
        {
            var item1 = (new TestParticipant { Name = "Alice" }, 1);
            var item2 = (new TestParticipant { Name = "Bob" }, 2);

            var result = RankingUtilities.IsTied(item1, item2);

            Assert.False(result);
        }

        // ============ GroupByRank Tests ============

        [Fact]
        public void GroupByRank_WithMultipleRanks_GroupsCorrectly()
        {
            var ranked = new List<(TestParticipant item, int rank)>
            {
                (new TestParticipant { Name = "Alice" }, 1),
                (new TestParticipant { Name = "Bob" }, 1),
                (new TestParticipant { Name = "Charlie" }, 2),
                (new TestParticipant { Name = "David" }, 3)
            };

            var result = RankingUtilities.GroupByRank(ranked);

            Assert.Equal(3, result.Count);
            Assert.Equal(2, result[1].Count);  // Two items with rank 1
            Assert.Equal(1, result[2].Count);  // One item with rank 2
            Assert.Equal(1, result[3].Count);  // One item with rank 3
        }

        // ============ RenumberSequential Tests ============

        [Fact]
        public void RenumberSequential_WithGappedRanks_ReturnsSequential()
        {
            var ranked = new List<(TestParticipant item, int rank)>
            {
                (new TestParticipant { Name = "Alice" }, 1),
                (new TestParticipant { Name = "Bob" }, 1),
                (new TestParticipant { Name = "Charlie" }, 3),
                (new TestParticipant { Name = "David" }, 4)
            };

            var result = RankingUtilities.RenumberSequential(ranked);

            Assert.Equal(4, result.Count);
            Assert.Equal(1, result[0].rank);
            Assert.Equal(2, result[1].rank);
            Assert.Equal(3, result[2].rank);
            Assert.Equal(4, result[3].rank);
        }
    }
}
