using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for ScoringUtilities
    /// Tests shot scoring, validation, and calculations
    /// </summary>
    public class ScoringUtilitiesTests
    {
        // ============ ShotToPoints Tests ============

        [Fact]
        public void ShotToPoints_WithX_Returns10()
        {
            var result = ScoringUtilities.ShotToPoints("X");
            Assert.Equal(10, result);
        }

        [Fact]
        public void ShotToPoints_WithLowercaseX_Returns10()
        {
            var result = ScoringUtilities.ShotToPoints("x");
            Assert.Equal(10, result);
        }

        [Fact]
        public void ShotToPoints_With10_Returns10()
        {
            var result = ScoringUtilities.ShotToPoints("10");
            Assert.Equal(10, result);
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("1", 1)]
        [InlineData("5", 5)]
        [InlineData("9", 9)]
        public void ShotToPoints_WithValidNumeric_ReturnsCorrectValue(string shot, int expected)
        {
            var result = ScoringUtilities.ShotToPoints(shot);
            Assert.Equal(expected, result);
        }

        [Theory]
        [InlineData("11")]
        [InlineData("-1")]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData(null)]
        public void ShotToPoints_WithInvalidValue_ReturnsZero(string shot)
        {
            var result = ScoringUtilities.ShotToPoints(shot);
            Assert.Equal(0, result);
        }

        [Theory]
        [InlineData(" X ", 10)]
        [InlineData(" 10 ", 10)]
        [InlineData(" 5 ", 5)]
        public void ShotToPoints_WithWhitespace_TrimsAndCalculates(string shot, decimal expected)
        {
            var result = ScoringUtilities.ShotToPoints(shot);
            Assert.Equal(expected, result);
        }

        // ============ IsValidShotValue Tests ============

        [Theory]
        [InlineData("X")]
        [InlineData("x")]
        [InlineData("0")]
        [InlineData("5")]
        [InlineData("10")]
        public void IsValidShotValue_WithValidShot_ReturnsTrue(string shot)
        {
            var result = ScoringUtilities.IsValidShotValue(shot);
            Assert.True(result);
        }

        [Theory]
        [InlineData("11")]
        [InlineData("-1")]
        [InlineData("abc")]
        [InlineData("")]
        [InlineData(null)]
        public void IsValidShotValue_WithInvalidShot_ReturnsFalse(string shot)
        {
            var result = ScoringUtilities.IsValidShotValue(shot);
            Assert.False(result);
        }

        // ============ CalculateTotal Tests ============

        [Fact]
        public void CalculateTotal_WithValidShots_ReturnsCorrectSum()
        {
            var shots = new List<string> { "X", "10", "9", "8", "7" };
            var result = ScoringUtilities.CalculateTotal(shots);
            Assert.Equal(44, result);
        }

        [Fact]
        public void CalculateTotal_WithAllX_Returns50()
        {
            var shots = new List<string> { "X", "X", "X", "X", "X" };
            var result = ScoringUtilities.CalculateTotal(shots);
            Assert.Equal(50, result);
        }

        [Fact]
        public void CalculateTotal_WithAllZeros_ReturnsZero()
        {
            var shots = new List<string> { "0", "0", "0", "0", "0" };
            var result = ScoringUtilities.CalculateTotal(shots);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CalculateTotal_WithMixedValid_ReturnsCorrectSum()
        {
            var shots = new List<string> { "X", "X", "9", "8", "7" };
            var result = ScoringUtilities.CalculateTotal(shots);
            Assert.Equal(44, result);
        }

        [Fact]
        public void CalculateTotal_WithEmptyList_ReturnsZero()
        {
            var shots = new List<string>();
            var result = ScoringUtilities.CalculateTotal(shots);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CalculateTotal_WithNull_ReturnsZero()
        {
            var result = ScoringUtilities.CalculateTotal(null);
            Assert.Equal(0, result);
        }

        // ============ CountInnerTens Tests ============

        [Fact]
        public void CountInnerTens_WithAllX_Returns5()
        {
            var shots = new List<string> { "X", "X", "X", "X", "X" };
            var result = ScoringUtilities.CountInnerTens(shots);
            Assert.Equal(5, result);
        }

        [Fact]
        public void CountInnerTens_WithNoX_ReturnsZero()
        {
            var shots = new List<string> { "10", "9", "8", "7", "6" };
            var result = ScoringUtilities.CountInnerTens(shots);
            Assert.Equal(0, result);
        }

        [Fact]
        public void CountInnerTens_WithMixed_ReturnsCorrectCount()
        {
            var shots = new List<string> { "X", "10", "X", "9", "X" };
            var result = ScoringUtilities.CountInnerTens(shots);
            Assert.Equal(3, result);
        }

        [Fact]
        public void CountInnerTens_WithNull_ReturnsZero()
        {
            var result = ScoringUtilities.CountInnerTens(null);
            Assert.Equal(0, result);
        }

        // ============ CountAllTens Tests ============

        [Fact]
        public void CountAllTens_WithAllXAnd10_ReturnsTotal()
        {
            var shots = new List<string> { "X", "10", "X", "10", "X" };
            var result = ScoringUtilities.CountAllTens(shots);
            Assert.Equal(5, result);
        }

        [Fact]
        public void CountAllTens_WithOnlyX_ReturnsCorrectCount()
        {
            var shots = new List<string> { "X", "X", "9", "8", "7" };
            var result = ScoringUtilities.CountAllTens(shots);
            Assert.Equal(2, result);
        }

        [Fact]
        public void CountAllTens_WithOnly10_ReturnsCorrectCount()
        {
            var shots = new List<string> { "10", "9", "8", "7", "6" };
            var result = ScoringUtilities.CountAllTens(shots);
            Assert.Equal(1, result);
        }

        [Fact]
        public void CountAllTens_WithNoTens_ReturnsZero()
        {
            var shots = new List<string> { "9", "9", "8", "7", "6" };
            var result = ScoringUtilities.CountAllTens(shots);
            Assert.Equal(0, result);
        }

        // ============ CountNines Tests ============

        [Fact]
        public void CountNines_WithMultiple9s_ReturnsCorrectCount()
        {
            var shots = new List<string> { "9", "9", "X", "10", "9" };
            var result = ScoringUtilities.CountNines(shots);
            Assert.Equal(3, result);
        }

        [Fact]
        public void CountNines_WithNo9s_ReturnsZero()
        {
            var shots = new List<string> { "X", "10", "8", "7", "6" };
            var result = ScoringUtilities.CountNines(shots);
            Assert.Equal(0, result);
        }

        // ============ GetValidShots Tests ============

        [Fact]
        public void GetValidShots_WithMixedValidInvalid_ReturnsOnlyValid()
        {
            var shots = new List<string> { "X", "invalid", "10", "abc", "5" };
            var result = ScoringUtilities.GetValidShots(shots);
            Assert.Equal(3, result.Count);
            Assert.Contains("X", result);
            Assert.Contains("10", result);
            Assert.Contains("5", result);
        }

        [Fact]
        public void GetValidShots_WithAllValid_ReturnsAll()
        {
            var shots = new List<string> { "X", "10", "9", "8" };
            var result = ScoringUtilities.GetValidShots(shots);
            Assert.Equal(4, result.Count);
        }

        // ============ GetInvalidShots Tests ============

        [Fact]
        public void GetInvalidShots_WithMixedValidInvalid_ReturnsOnlyInvalid()
        {
            var shots = new List<string> { "X", "invalid", "10", "abc", "5" };
            var result = ScoringUtilities.GetInvalidShots(shots);
            Assert.Equal(2, result.Count);
            Assert.Contains("invalid", result);
            Assert.Contains("abc", result);
        }

        // ============ CalculateAverage Tests ============

        [Fact]
        public void CalculateAverage_WithValidShots_ReturnsCorrectAverage()
        {
            var shots = new List<string> { "X", "10", "8", "6", "4" };
            var result = ScoringUtilities.CalculateAverage(shots);
            Assert.Equal(7.6m, result);
        }

        [Fact]
        public void CalculateAverage_WithEmptyList_ReturnsZero()
        {
            var shots = new List<string>();
            var result = ScoringUtilities.CalculateAverage(shots);
            Assert.Equal(0, result);
        }

        // ============ GetLowestShot Tests ============

        [Fact]
        public void GetLowestShot_WithMixedValues_ReturnsLowest()
        {
            var shots = new List<string> { "X", "10", "3", "8", "9" };
            var result = ScoringUtilities.GetLowestShot(shots);
            Assert.Equal(3, result);
        }

        [Fact]
        public void GetLowestShot_WithAllX_Returns10()
        {
            var shots = new List<string> { "X", "X", "X" };
            var result = ScoringUtilities.GetLowestShot(shots);
            Assert.Equal(10, result);
        }

        // ============ GetHighestShot Tests ============

        [Fact]
        public void GetHighestShot_WithMixedValues_ReturnsHighest()
        {
            var shots = new List<string> { "5", "10", "3", "8", "9" };
            var result = ScoringUtilities.GetHighestShot(shots);
            Assert.Equal(10, result);
        }

        [Fact]
        public void GetHighestShot_WithAllX_Returns10()
        {
            var shots = new List<string> { "X", "X", "X" };
            var result = ScoringUtilities.GetHighestShot(shots);
            Assert.Equal(10, result);
        }
    }
}
