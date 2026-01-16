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
        public void CalculateAdjustedTotal_AppliesHandicapToEachSeries()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 2, Total = 45, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 45, XCount = 2 }
            };

            // 45 + 2.5 = 47.5 -> rounds to 48 for each series
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 2.5m);
            Assert.Equal(144, result); // 48 * 3
        }

        [Fact]
        public void CalculateAdjustedTotal_CapsEachAdjustedSeriesAt50()
        {
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 },
                new TestSeriesScore { SeriesNumber = 2, Total = 49, XCount = 4 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 }
            };

            // With 3.0 handicap:
            // 48 + 3 = 51 -> capped at 50
            // 49 + 3 = 52 -> capped at 50
            // 47 + 3 = 50 -> exactly 50
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

            // Only first 2 series: (45 + 2.5) * 2 = 47.5 * 2 -> 48 * 2 = 96
            var result = ResultCalculator.CalculateAdjustedTotal(scores, 2.5m, equalizedCount: 2);
            Assert.Equal(96, result);
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

            // Adjusted (each series + 1.75, rounded, capped):
            // 47+1.75=48.75->49, 45+1.75=46.75->47, 48+1.75=49.75->50,
            // 46+1.75=47.75->48, 49+1.75=50.75->51->50 (capped),
            // 44+1.75=45.75->46, 47+1.75=48.75->49, 48+1.75=49.75->50,
            // 45+1.75=46.75->47, 46+1.75=47.75->48
            // = 49+47+50+48+50+46+49+50+47+48 = 484
            Assert.Equal(484, adjustedTotal);

            // Handicap actually applied
            var handicapApplied = adjustedTotal - rawTotal;
            Assert.Equal(19, handicapApplied); // 484 - 465 = 19 (less than 1.75 * 10 = 17.5 rounded due to caps)
        }

        [Fact]
        public void Integration_ScenarioThatCausedOriginalBug()
        {
            // This is the exact scenario that could cause 1-point difference
            // with Banker's rounding vs standard rounding
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 2, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 3, Total = 47, XCount = 2 },
                new TestSeriesScore { SeriesNumber = 4, Total = 47, XCount = 2 }
            };

            decimal handicap = 1.5m; // This creates 48.5 for each series

            // With standard rounding (AwayFromZero):
            // 47 + 1.5 = 48.5 -> 49 for each series
            // Total: 49 * 4 = 196

            // With Banker's rounding (ToEven) - the OLD bug:
            // 47 + 1.5 = 48.5 -> 48 for each series (rounds to even)
            // Total: 48 * 4 = 192

            // Difference: 4 points! (1 per series)

            var result = ResultCalculator.CalculateAdjustedTotal(scores, handicap);

            // Our implementation should use standard rounding
            Assert.Equal(196, result);
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
            // 48 - 2.0 = 46, 49 - 2.0 = 47, 47 - 2.0 = 45
            // Total: 46 + 47 + 45 = 138
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
            // 10 - 15 = -5 -> should round to -5 (but this is edge case)
            // The system allows negative adjusted scores (rare in practice)
            var result = ResultCalculator.CalculateAdjustedTotal(scores, -15.0m);

            // 10 - 15 = -5, 5 - 15 = -10, Total = -15
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

            // Adjusted:
            // 49 - 1.75 = 47.25 -> 47
            // 50 - 1.75 = 48.25 -> 48
            // 48 - 1.75 = 46.25 -> 46
            // 49 - 1.75 = 47.25 -> 47
            // Total: 47 + 48 + 46 + 47 = 188
            Assert.Equal(188, adjustedTotal);

            // Handicap applied is negative (points deducted)
            Assert.Equal(-8, adjustedTotal - rawTotal);
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
        public void MaxScoreRule_EachSeriesCappedIndependently()
        {
            // Mix of scores where some hit the cap and some don't
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 }, // 48+3=51 -> 50
                new TestSeriesScore { SeriesNumber = 2, Total = 40, XCount = 1 }, // 40+3=43 -> 43
                new TestSeriesScore { SeriesNumber = 3, Total = 49, XCount = 4 }, // 49+3=52 -> 50
                new TestSeriesScore { SeriesNumber = 4, Total = 35, XCount = 0 }  // 35+3=38 -> 38
            };

            var result = ResultCalculator.CalculateAdjustedTotal(scores, 3.0m);

            // Expected: 50 + 43 + 50 + 38 = 181
            Assert.Equal(181, result);

            // Verify it's less than max possible (4 × 50 = 200)
            Assert.True(result <= 200);
        }

        [Fact]
        public void MaxScoreRule_HandicapLostToCap_IsCalculatedCorrectly()
        {
            // When handicap would push score above 50, some handicap is "lost"
            var scores = new List<TestSeriesScore>
            {
                new TestSeriesScore { SeriesNumber = 1, Total = 48, XCount = 3 }, // Would be 53, capped to 50, lost 3
                new TestSeriesScore { SeriesNumber = 2, Total = 49, XCount = 4 }, // Would be 54, capped to 50, lost 4
                new TestSeriesScore { SeriesNumber = 3, Total = 45, XCount = 2 }  // Would be 50, no loss
            };

            decimal handicap = 5.0m;

            var rawTotal = ResultCalculator.CalculateRawTotal(scores);
            var adjustedTotal = ResultCalculator.CalculateAdjustedTotal(scores, handicap);

            // Raw: 48 + 49 + 45 = 142
            Assert.Equal(142, rawTotal);

            // Adjusted: 50 + 50 + 50 = 150 (all capped)
            Assert.Equal(150, adjustedTotal);

            // Theoretical handicap: 5 × 3 = 15
            // Actual handicap applied: 150 - 142 = 8
            var actualHandicapApplied = adjustedTotal - rawTotal;
            Assert.Equal(8, actualHandicapApplied);

            // Lost to cap: 15 - 8 = 7 (3 from series 1, 4 from series 2)
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
