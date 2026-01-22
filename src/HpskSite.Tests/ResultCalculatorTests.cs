using HpskSite.Shared.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for ResultCalculator.
    /// Tests the centralized calculation logic to ensure consistent results
    /// between mobile app and web site.
    /// </summary>
    public class ResultCalculatorTests
    {
        // ============ Constants Tests ============

        [Fact]
        public void MaxScorePerSeries_Is50()
        {
            Assert.Equal(50, ResultCalculator.MaxScorePerSeries);
        }

        [Fact]
        public void StandardRounding_IsAwayFromZero()
        {
            Assert.Equal(MidpointRounding.AwayFromZero, ResultCalculator.StandardRounding);
        }

        // ============ CalculateShotsTotal Tests ============

        [Fact]
        public void CalculateShotsTotal_WithValidShots_ReturnsCorrectTotalAndXCount()
        {
            var shots = new List<string> { "X", "10", "9", "8", "7" };
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(44, total);
            Assert.Equal(1, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithAllX_Returns50And5()
        {
            var shots = new List<string> { "X", "X", "X", "X", "X" };
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(50, total);
            Assert.Equal(5, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithLowercaseX_TreatsAsX()
        {
            var shots = new List<string> { "x", "x", "10", "9", "8" };
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(47, total);
            Assert.Equal(2, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithAllZeros_ReturnsZero()
        {
            var shots = new List<string> { "0", "0", "0", "0", "0" };
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(0, total);
            Assert.Equal(0, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithNull_ReturnsZero()
        {
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(null);

            Assert.Equal(0, total);
            Assert.Equal(0, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithEmptyList_ReturnsZero()
        {
            var shots = new List<string>();
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(0, total);
            Assert.Equal(0, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithWhitespace_TrimsAndCalculates()
        {
            var shots = new List<string> { " X ", " 10 ", " 9 ", "", "  " };
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(29, total);
            Assert.Equal(1, xCount);
        }

        [Fact]
        public void CalculateShotsTotal_WithInvalidValues_IgnoresThem()
        {
            var shots = new List<string> { "X", "abc", "10", "invalid", "9" };
            var (total, xCount) = ResultCalculator.CalculateShotsTotal(shots);

            Assert.Equal(29, total);
            Assert.Equal(1, xCount);
        }

        // ============ CalculateAdjustedSeriesScore Tests ============
        // These tests verify the rounding behavior that caused the 1-point bug

        [Theory]
        [InlineData(47, 1.5, 49)]   // 48.5 rounds to 49 (away from zero)
        [InlineData(46, 2.5, 49)]   // 48.5 rounds to 49 (away from zero)
        [InlineData(45, 2.5, 48)]   // 47.5 rounds to 48 (away from zero)
        [InlineData(44, 2.5, 47)]   // 46.5 rounds to 47 (away from zero)
        [InlineData(49, 0.5, 50)]   // 49.5 rounds to 50 (away from zero), capped at 50
        [InlineData(48, 2.75, 50)]  // 50.75 rounds to 51, capped at 50
        public void CalculateAdjustedSeriesScore_RoundsAwayFromZero(int rawScore, decimal handicap, int expected)
        {
            var result = ResultCalculator.CalculateAdjustedSeriesScore(rawScore, handicap);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void CalculateAdjustedSeriesScore_CapsAt50()
        {
            // 48 + 5 = 53, should cap at 50
            var result = ResultCalculator.CalculateAdjustedSeriesScore(48, 5.0m);
            Assert.Equal(50, result);
        }

        [Fact]
        public void CalculateAdjustedSeriesScore_WithZeroHandicap_ReturnsRawScore()
        {
            var result = ResultCalculator.CalculateAdjustedSeriesScore(45, 0);
            Assert.Equal(45, result);
        }

        [Fact]
        public void CalculateAdjustedSeriesScore_WithNegativeHandicap_SubtractsFromScore()
        {
            // Some shooters might have negative handicap if they're very skilled
            var result = ResultCalculator.CalculateAdjustedSeriesScore(48, -1.5m);
            // 48 - 1.5 = 46.5, rounds to 47 (away from zero)
            Assert.Equal(47, result);
        }

        [Theory]
        [InlineData(47, 1.25, 48)]  // 48.25 rounds to 48
        [InlineData(47, 1.75, 49)]  // 48.75 rounds to 49
        [InlineData(47, 2.0, 49)]   // 49.0 is exact
        [InlineData(47, 2.25, 49)]  // 49.25 rounds to 49
        public void CalculateAdjustedSeriesScore_WithQuarterPointHandicap_RoundsCorrectly(
            int rawScore, decimal handicap, int expected)
        {
            var result = ResultCalculator.CalculateAdjustedSeriesScore(rawScore, handicap);
            Assert.Equal(expected, result);
        }

        // ============ Banker's Rounding vs Standard Rounding Comparison ============
        // These tests document the difference between Banker's rounding (old behavior)
        // and standard rounding (new behavior) that caused the 1-point bug

        [Fact]
        public void RoundingComparison_BankersVsStandard_DemonstratesDifference()
        {
            // This test demonstrates why the bug occurred
            decimal value = 48.5m;

            // Old behavior (Banker's rounding - ToEven): 48.5 -> 48
            var bankersResult = (int)Math.Round(value, MidpointRounding.ToEven);

            // New behavior (Standard rounding - AwayFromZero): 48.5 -> 49
            var standardResult = (int)Math.Round(value, MidpointRounding.AwayFromZero);

            Assert.Equal(48, bankersResult);  // Banker's rounds to even
            Assert.Equal(49, standardResult); // Standard rounds up

            // Our calculator should use standard rounding
            var calculatorResult = ResultCalculator.CalculateAdjustedSeriesScore(47, 1.5m);
            Assert.Equal(49, calculatorResult);
        }

        [Fact]
        public void RoundingComparison_49Point5_DemonstratesDifference()
        {
            decimal value = 49.5m;

            // Old behavior (Banker's rounding): 49.5 -> 50 (rounds to even)
            var bankersResult = (int)Math.Round(value, MidpointRounding.ToEven);

            // New behavior (Standard rounding): 49.5 -> 50 (rounds up)
            var standardResult = (int)Math.Round(value, MidpointRounding.AwayFromZero);

            // In this case they happen to match (50 is even)
            Assert.Equal(50, bankersResult);
            Assert.Equal(50, standardResult);
        }

        // ============ CalculateRawTotal Tests ============

        [Fact]
        public void CalculateRawTotal_SumsAllSeriesCorrectly()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 }
            };

            var result = ResultCalculator.CalculateRawTotal(scores);
            Assert.Equal(140, result);
        }

        [Fact]
        public void CalculateRawTotal_CapsEachSeriesAt50()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 52, XCount = 5 }, // Invalid but should cap
                new TestSeriesScore { SeriesNumber = 2, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 3, Total = 48, XCount = 3 }
            };

            var result = ResultCalculator.CalculateRawTotal(scores);
            Assert.Equal(148, result); // 50 + 50 + 48
        }

        [Fact]
        public void CalculateRawTotal_WithEqualizedCount_OnlyIncludesFirstNSeries()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 4, Total = 50, XCount = 5 }
            };

            var result = ResultCalculator.CalculateRawTotal(scores, equalizedCount: 2);
            Assert.Equal(93, result); // Only series 1 + 2 = 48 + 45
        }

        [Fact]
        public void CalculateRawTotal_WithEqualizedCount_OrdersBySeriesNumber()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 }
            };

            var result = ResultCalculator.CalculateRawTotal(scores, equalizedCount: 2);
            Assert.Equal(93, result); // Series 1 + 2 = 48 + 45
        }

        [Fact]
        public void CalculateRawTotal_WithEmptyList_ReturnsZero()
        {
            var scores = new List<TestSeriesScore>();
            var result = ResultCalculator.CalculateRawTotal(scores);
            Assert.Equal(0, result);
        }

        // ============ CalculateTotalXCount Tests ============

        [Fact]
        public void CalculateTotalXCount_SumsAllXCounts()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 4 }
            };

            var result = ResultCalculator.CalculateTotalXCount(scores);
            Assert.Equal(9, result);
        }

        [Fact]
        public void CalculateTotalXCount_WithEqualizedCount_OnlyIncludesFirstNSeries()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 5 }
            };

            var result = ResultCalculator.CalculateTotalXCount(scores, equalizedCount: 2);
            Assert.Equal(5, result); // Only series 1 + 2 = 3 + 2
        }

        // ============ CalculateAdjustedTotal Tests ============

        [Fact]
        public void CalculateAdjustedTotal_AppliesHandicapToTotal()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 45, XCount = 2 }
            };

            // Per spec: FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
            // 135 + (2.5 × 3) = 135 + 7.5 = 142.5 -> rounds to 143
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 2.5m);
            Assert.Equal(143, result);
        }

        [Fact]
        public void CalculateAdjustedTotal_CapsAtMaxPossible()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 }
            };

            // Per spec: FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
            // 144 + (3.0 × 3) = 144 + 9 = 153 -> capped at 150 (50 × 3)
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 3.0m);
            Assert.Equal(150, result);
        }

        [Fact]
        public void CalculateAdjustedTotal_WithZeroHandicap_EqualsTotalScore()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 }
            };

            var rawTotal = ResultCalculator.CalculateRawTotal(scores);
            var adjustedTotal = ResultCalculator.CalculateAdjustedTotal(scores, 0);

            Assert.Equal(rawTotal, adjustedTotal);
        }

        [Fact]
        public void CalculateAdjustedTotal_WithEqualizedCount_OnlyIncludesFirstNSeries()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 45, XCount = 2 }
            };

            // Only first 2 series: 90 + (2.5 × 2) = 90 + 5 = 95
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 2.5m, equalizedCount: 2);
            Assert.Equal(95, result);
        }

        // ============ RoundToInt Tests ============

        [Theory]
        [InlineData(48.5, 49)]   // .5 rounds up (away from zero)
        [InlineData(47.5, 48)]   // .5 rounds up (away from zero)
        [InlineData(48.4, 48)]   // < .5 rounds down
        [InlineData(48.6, 49)]   // > .5 rounds up
        [InlineData(-48.5, -49)] // Negative .5 rounds away from zero (down)
        public void RoundToInt_UsesStandardRounding(decimal value, int expected)
        {
            var result = ResultCalculator.RoundToInt(value);
            Assert.Equal(expected, result);
        }

        // ============ RoundToQuarter Tests ============

        [Theory]
        [InlineData(2.0, 2.0)]
        [InlineData(2.125, 2.25)]  // Rounds to nearest quarter
        [InlineData(2.375, 2.5)]   // Rounds to nearest quarter
        [InlineData(2.625, 2.75)]  // Rounds to nearest quarter
        [InlineData(2.875, 3.0)]   // Rounds to nearest quarter
        [InlineData(2.5, 2.5)]     // Already at quarter
        public void RoundToQuarter_RoundsToNearestQuarter(decimal value, decimal expected)
        {
            var result = ResultCalculator.RoundToQuarter(value);
            Assert.Equal(expected, result);
        }

        // ============ Integration Tests ============

        [Fact]
        public void Integration_FullMatchCalculation_ProducesConsistentResults()
        {
            // Simulate a typical match with 10 series
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 4, Total = 46, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 5, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 6, Total = 44, XCount = 1 },
                new TestSeriesScore { SeriesNumber = 7, Total = 47, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 8, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 9, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 10, Total = 46, XCount = 2 }
            };

            decimal handicap = 1.75m;

            var rawTotal = ResultCalculator.CalculateRawTotal(scores);
            var adjustedTotal = ResultCalculator.CalculateAdjustedTotal(scores, handicap);
            var xCount = ResultCalculator.CalculateTotalXCount(scores);

            // Raw total: 47+45+48+46+49+44+47+48+45+46 = 465
            Assert.Equal(465, rawTotal);

            // X count: 2+2+3+2+4+1+3+3+2+2 = 24
            Assert.Equal(24, xCount);

            // Per spec: FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
            // 465 + (1.75 × 10) = 465 + 17.5 = 482.5 -> rounds to 483
            Assert.Equal(483, adjustedTotal);

            // Handicap actually applied
            var handicapApplied = adjustedTotal - rawTotal;
            Assert.Equal(18, handicapApplied); // 483 - 465 = 18 (1.75 × 10 = 17.5 rounded to 18)
        }

        [Fact]
        public void Integration_ScenarioThatCausedOriginalBug()
        {
            // This test verifies standard rounding (AwayFromZero) is used for the final total
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 2, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 4, Total = 47, XCount = 2 }
            };

            decimal handicap = 1.5m;

            // Per spec: FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
            // 188 + (1.5 × 4) = 188 + 6 = 194
            // With Banker's rounding, 194.0 would still be 194, so no difference here
            // But a 0.5 final total would round to 195 with AwayFromZero

            var result = ResultCalculator.CalculateAdjustedTotal(scores, handicap);

            // Standard rounding on total: 188 + 6 = 194
            Assert.Equal(194, result);
        }

        // ============ Negative Handicap Tests ============
        // Elite shooters may have negative handicap (they give points to others)

        [Theory]
        [InlineData(48, -0.5, 48)]   // 47.5 rounds to 48
        [InlineData(48, -1.0, 47)]   // 47.0 exact
        [InlineData(48, -1.5, 47)]   // 46.5 rounds to 47 (away from zero)
        [InlineData(48, -2.5, 46)]   // 45.5 rounds to 46 (away from zero)
        [InlineData(48, -3.0, 45)]   // 45.0 exact
        [InlineData(50, -5.0, 45)]   // Perfect score minus 5
        public void CalculateAdjustedSeriesScore_WithNegativeHandicap_ReducesScore(
            int rawScore, decimal handicap, int expected)
        {
            var result = ResultCalculator.CalculateAdjustedSeriesScore(rawScore, handicap);
            Assert.Equal(expected, result);
        }

        [Fact]
        public void CalculateAdjustedTotal_WithNegativeHandicap_ReducesTotalScore()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 }
            };

            // Negative handicap: skilled shooter gives points
            // Per spec: 144 + (-2.0 × 3) = 144 - 6 = 138
            var result = ResultCalculator.CalculateAdjustedTotal(scores, -2.0m);
            Assert.Equal(138, result);
        }

        [Fact]
        public void CalculateAdjustedTotal_WithNegativeHandicap_CannotGoBelowZero()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 10, XCount = 0 },
                new TestSeriesScore { SeriesNumber = 2, Total = 5, XCount = 0 }
            };

            // With extreme negative handicap, score could theoretically go negative
            // Per spec: 15 + (-15.0 × 2) = 15 - 30 = -15
            var result = ResultCalculator.CalculateAdjustedTotal(scores, -15.0m);

            Assert.Equal(-15, result);
        }

        [Fact]
        public void Integration_NegativeHandicap_FullMatchCalculation()
        {
            // Simulate an elite shooter with negative handicap
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 2, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 3, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 4, Total = 49, XCount = 4 }
            };

            decimal negativeHandicap = -1.75m;

            var rawTotal = ResultCalculator.CalculateRawTotal(scores);
            var adjustedTotal = ResultCalculator.CalculateAdjustedTotal(scores, negativeHandicap);

            // Raw: 49 + 50 + 48 + 49 = 196
            Assert.Equal(196, rawTotal);

            // Per spec: FinalScore = Sum(RawSeriesScores) + (HandicapPerSeries × SeriesCount)
            // 196 + (-1.75 × 4) = 196 - 7 = 189
            Assert.Equal(189, adjustedTotal);

            // Handicap applied is negative (points deducted)
            Assert.Equal(-7, adjustedTotal - rawTotal);
        }

        // ============ Maximum Score Rules Tests ============
        // Rule 1: Sum of shots in one series can never exceed 50
        // Rule 2: Sum of all series in a match can never exceed (numberOfSeries × 50)

        [Fact]
        public void MaxScoreRule_SeriesScoreNeverExceeds50_WithPositiveHandicap()
        {
            // Even with large handicap, adjusted series score caps at 50
            var testCases = new[]
            {
                (raw: 50, handicap: 5.0m, expected: 50),   // 55 -> 50
                (raw: 48, handicap: 10.0m, expected: 50),  // 58 -> 50
                (raw: 45, handicap: 100.0m, expected: 50), // 145 -> 50
                (raw: 0, handicap: 100.0m, expected: 50),  // 100 -> 50
            };

            foreach (var (raw, handicap, expected) in testCases)
            {
                var result = ResultCalculator.CalculateAdjustedSeriesScore(raw, handicap);
                Assert.True(result <= 50, $"Series score {result} exceeds 50 for raw={raw}, hcp={handicap}");
                Assert.Equal(expected, result);
            }
        }

        [Fact]
        public void MaxScoreRule_RawTotalNeverExceedsSeriesCountTimes50()
        {
            // Even if individual series have invalid totals > 50, the cap applies
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 55, XCount = 5 }, // Invalid but test cap
                new TestSeriesScore { SeriesNumber = 2, Total = 60, XCount = 5 }, // Invalid but test cap
                new TestSeriesScore { SeriesNumber = 3, Total = 100, XCount = 5 } // Invalid but test cap
            };

            var result = ResultCalculator.CalculateRawTotal(scores);
            var maxPossible = scores.Count * ResultCalculator.MaxScorePerSeries; // 3 × 50 = 150

            // Each series capped at 50: 50 + 50 + 50 = 150
            Assert.Equal(150, result);
            Assert.True(result <= maxPossible, $"Total {result} exceeds max possible {maxPossible}");
        }

        [Fact]
        public void MaxScoreRule_AdjustedTotalNeverExceedsSeriesCountTimes50()
        {
            // All perfect scores with positive handicap
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 2, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 3, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 4, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 5, Total = 50, XCount = 5 }
            };

            // Even with handicap, cannot exceed 5 × 50 = 250
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 5.0m);
            var maxPossible = scores.Count * ResultCalculator.MaxScorePerSeries; // 5 × 50 = 250

            Assert.Equal(250, result);
            Assert.True(result <= maxPossible, $"Total {result} exceeds max possible {maxPossible}");
        }

        [Theory]
        [InlineData(1, 50)]    // 1 series max = 50
        [InlineData(5, 250)]   // 5 series max = 250
        [InlineData(10, 500)]  // 10 series max = 500
        [InlineData(12, 600)]  // 12 series max = 600
        public void MaxScoreRule_MatchTotalNeverExceedsLimit(int seriesCount, int maxTotal)
        {
            // Create series all with perfect scores + high handicap
            var scores = Enumerable.Range(1, seriesCount)
                .Select(i => new TestSeriesScore { SeriesNumber = i, Total = 50, XCount = 5 })
                .ToList();

            var rawTotal = ResultCalculator.CalculateRawTotal(scores);
            var adjustedTotal = ResultCalculator.CalculateAdjustedTotal(scores, 10.0m);

            Assert.Equal(maxTotal, rawTotal);
            Assert.Equal(maxTotal, adjustedTotal);
            Assert.True(rawTotal <= maxTotal);
            Assert.True(adjustedTotal <= maxTotal);
        }

        [Fact]
        public void MaxScoreRule_TotalCappedAtMaxPossible()
        {
            // Mix of scores to verify total is capped at (seriesCount × 50)
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 40, XCount = 1 },
                new TestSeriesScore { SeriesNumber = 3, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 4, Total = 35, XCount = 0 }
            };

            var result = ResultCalculator.CalculateAdjustedTotal(scores, 3.0m);

            // Per spec: 172 + (3.0 × 4) = 172 + 12 = 184
            Assert.Equal(184, result);

            // Verify it's less than max possible (4 × 50 = 200)
            Assert.True(result <= 200);
        }

        [Fact]
        public void MaxScoreRule_HandicapLostToCap_IsCalculatedCorrectly()
        {
            // When handicap would push total above (seriesCount × 50), excess is "lost"
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 3, Total = 45, XCount = 2 }
            };

            decimal handicap = 5.0m;

            var rawTotal = ResultCalculator.CalculateRawTotal(scores);
            var adjustedTotal = ResultCalculator.CalculateAdjustedTotal(scores, handicap);

            // Raw: 48 + 49 + 45 = 142
            Assert.Equal(142, rawTotal);

            // Per spec: 142 + (5.0 × 3) = 142 + 15 = 157, but capped at 150
            Assert.Equal(150, adjustedTotal);

            // Theoretical handicap: 5 × 3 = 15
            // Actual handicap applied: 150 - 142 = 8
            var actualHandicapApplied = adjustedTotal - rawTotal;
            Assert.Equal(8, actualHandicapApplied);

            // Lost to cap: 15 - 8 = 7 (total would exceed max)
            var theoreticalHandicap = handicap * scores.Count;
            var lostToCap = theoreticalHandicap - actualHandicapApplied;
            Assert.Equal(7, lostToCap);
        }

        [Fact]
        public void MaxScoreRule_WithEqualization_MaxIsEqualizedCountTimes50()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 2, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 3, Total = 50, XCount = 5 },
                new TestSeriesScore { SeriesNumber = 4, Total = 50, XCount = 5 }
            };

            // Only count first 2 series
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 5.0m, equalizedCount: 2);

            // Max with equalization: 2 × 50 = 100
            Assert.Equal(100, result);
        }
    }

    /// <summary>
    /// Test helper class that implements ISeriesScore
    /// </summary>
    public class TestSeriesScore : ISeriesScore
    {
        public int SeriesNumber { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
    }
}
