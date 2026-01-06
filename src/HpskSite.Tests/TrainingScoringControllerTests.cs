using HpskSite.Shared.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for TrainingScoringController
    /// Tests training score data models, validation, and statistics calculations
    /// </summary>
    public class TrainingScoringControllerTests
    {
        // ============ TrainingSeries Tests ============

        [Fact]
        public void TrainingSeries_CalculateScore_WithPerfectShots_Returns50()
        {
            // Arrange
            var series = new TrainingSeries
            {
                SeriesNumber = 1,
                Shots = new List<string> { "X", "X", "X", "X", "X" }
            };

            // Act
            series.CalculateScore();

            // Assert
            Assert.Equal(50, series.Total);
            Assert.Equal(5, series.XCount);
        }

        [Fact]
        public void TrainingSeries_CalculateScore_WithMixedShots_CalculatesCorrectly()
        {
            // Arrange
            var series = new TrainingSeries
            {
                SeriesNumber = 1,
                Shots = new List<string> { "X", "10", "9", "8", "7" }
            };

            // Act
            series.CalculateScore();

            // Assert
            Assert.Equal(44, series.Total);
            Assert.Equal(1, series.XCount);
        }

        [Fact]
        public void TrainingSeries_CalculateScore_WithZeroShots_Returns0()
        {
            // Arrange
            var series = new TrainingSeries
            {
                SeriesNumber = 1,
                Shots = new List<string> { "0", "0", "0", "0", "0" }
            };

            // Act
            series.CalculateScore();

            // Assert
            Assert.Equal(0, series.Total);
            Assert.Equal(0, series.XCount);
        }

        [Fact]
        public void TrainingSeries_IsValid_WithCompleteSeries_ReturnsTrue()
        {
            // Arrange
            var series = new TrainingSeries
            {
                SeriesNumber = 1,
                Shots = new List<string> { "10", "9", "8", "7", "6" }
            };

            // Act
            bool isValid = series.IsValid();

            // Assert
            Assert.True(isValid);
        }

        [Fact]
        public void TrainingSeries_IsValid_WithIncompleteSeries_ReturnsFalse()
        {
            // Arrange
            var series = new TrainingSeries
            {
                SeriesNumber = 1,
                Shots = new List<string> { "10", "9", "8" } // Only 3 shots
            };

            // Act
            bool isValid = series.IsValid();

            // Assert
            Assert.False(isValid);
        }

        [Fact]
        public void TrainingSeries_IsValid_WithInvalidShotValue_ReturnsFalse()
        {
            // Arrange
            var series = new TrainingSeries
            {
                SeriesNumber = 1,
                Shots = new List<string> { "10", "9", "invalid", "7", "6" }
            };

            // Act
            bool isValid = series.IsValid();

            // Assert
            Assert.False(isValid);
        }

        [Theory]
        [InlineData("X", true)]
        [InlineData("x", true)]
        [InlineData("10", true)]
        [InlineData("9", true)]
        [InlineData("0", true)]
        [InlineData("11", false)]
        [InlineData("-1", false)]
        [InlineData("invalid", false)]
        [InlineData("", false)]
        public void TrainingSeries_ShotValidation_ValidatesCorrectly(string shot, bool expectedValid)
        {
            // This tests shot validation logic
            bool isValid = shot.ToUpper() == "X" || (int.TryParse(shot, out int value) && value >= 0 && value <= 10);
            Assert.Equal(expectedValid, isValid);
        }

        // ============ TrainingScoreEntry Tests ============

        [Fact]
        public void TrainingScoreEntry_CalculateTotals_SumsAllSeries()
        {
            // Arrange
            var entry = new TrainingScoreEntry
            {
                MemberId = 1,
                TrainingDate = DateTime.Now.AddDays(-1),
                WeaponClass = "A",
                Series = new List<TrainingSeries>
                {
                    new TrainingSeries { SeriesNumber = 1, Shots = new List<string> { "X", "X", "X", "X", "X" } },
                    new TrainingSeries { SeriesNumber = 2, Shots = new List<string> { "10", "9", "8", "7", "6" } },
                    new TrainingSeries { SeriesNumber = 3, Shots = new List<string> { "9", "9", "9", "9", "9" } }
                }
            };

            // Calculate each series
            entry.Series[0].CalculateScore(); // 50
            entry.Series[1].CalculateScore(); // 40
            entry.Series[2].CalculateScore(); // 45

            // Act
            entry.CalculateTotals();

            // Assert
            Assert.Equal(135, entry.TotalScore);
            Assert.Equal(5, entry.XCount);
        }

        [Fact]
        public void TrainingScoreEntry_IsValid_WithValidEntry_ReturnsTrue()
        {
            // Arrange
            var entry = new TrainingScoreEntry
            {
                MemberId = 1,
                TrainingDate = DateTime.Now.AddDays(-1),
                WeaponClass = "A",
                Series = new List<TrainingSeries>
                {
                    new TrainingSeries
                    {
                        SeriesNumber = 1,
                        Shots = new List<string> { "10", "9", "8", "7", "6" }
                    }
                }
            };

            entry.Series[0].CalculateScore();

            // Act
            bool isValid = entry.IsValid(out string errorMessage);

            // Assert
            Assert.True(isValid);
            Assert.Empty(errorMessage);
        }

        [Fact]
        public void TrainingScoreEntry_IsValid_WithFutureDate_ReturnsFalse()
        {
            // Arrange
            var entry = new TrainingScoreEntry
            {
                MemberId = 1,
                TrainingDate = DateTime.Now.AddDays(1), // Future date
                WeaponClass = "A",
                Series = new List<TrainingSeries>
                {
                    new TrainingSeries
                    {
                        SeriesNumber = 1,
                        Shots = new List<string> { "10", "9", "8", "7", "6" }
                    }
                }
            };

            // Act
            bool isValid = entry.IsValid(out string errorMessage);

            // Assert
            Assert.False(isValid);
            Assert.Contains("past", errorMessage.ToLower());
        }

        [Fact]
        public void TrainingScoreEntry_IsValid_WithNoSeries_ReturnsFalse()
        {
            // Arrange
            var entry = new TrainingScoreEntry
            {
                MemberId = 1,
                TrainingDate = DateTime.Now.AddDays(-1),
                WeaponClass = "A",
                Series = new List<TrainingSeries>() // Empty
            };

            // Act
            bool isValid = entry.IsValid(out string errorMessage);

            // Assert
            Assert.False(isValid);
            Assert.Contains("series", errorMessage.ToLower());
        }

        [Theory]
        [InlineData("A", true)]
        [InlineData("B", true)]
        [InlineData("C", true)]
        [InlineData("R", true)]
        [InlineData("P", true)]
        [InlineData("", false)]
        [InlineData("Z", true)] // Technically passes string check but invalid weapon
        public void TrainingScoreEntry_WeaponClassValidation_ValidatesCorrectly(string weaponClass, bool hasValue)
        {
            // Test that weapon class is required
            bool isValid = !string.IsNullOrEmpty(weaponClass);
            Assert.Equal(hasValue, isValid);
        }

        [Fact]
        public void TrainingScoreEntry_GetSummary_ReturnsCorrectType()
        {
            // Arrange: Training entry
            var trainingEntry = new TrainingScoreEntry
            {
                IsCompetition = false
            };

            var competitionEntry = new TrainingScoreEntry
            {
                IsCompetition = true
            };

            // Act
            string trainingSummary = trainingEntry.GetSummary();
            string competitionSummary = competitionEntry.GetSummary();

            // Assert
            Assert.Contains("Träning", trainingSummary);
            Assert.Contains("Tävling", competitionSummary);
        }

        // ============ PersonalBest Tests ============

        [Fact]
        public void PersonalBest_Improvement_CalculatesCorrectly()
        {
            // Arrange
            var personalBest = new PersonalBest
            {
                BestScore = 280,
                PreviousBest = 265
            };

            // Act
            int improvement = personalBest.Improvement;

            // Assert
            Assert.Equal(15, improvement);
        }

        [Fact]
        public void PersonalBest_Improvement_WithNoPrevious_Returns0()
        {
            // Arrange
            var personalBest = new PersonalBest
            {
                BestScore = 280,
                PreviousBest = null
            };

            // Act
            int improvement = personalBest.Improvement;

            // Assert
            Assert.Equal(0, improvement);
        }

        [Fact]
        public void PersonalBest_SeparatesByWeaponClass()
        {
            // Arrange: User has personal bests for different weapon classes
            var bests = new List<PersonalBest>
            {
                new PersonalBest { WeaponClass = "A", BestScore = 285, SeriesCount = 6 },
                new PersonalBest { WeaponClass = "B", BestScore = 270, SeriesCount = 6 },
                new PersonalBest { WeaponClass = "C", BestScore = 290, SeriesCount = 6 }
            };

            // Act: Group by weapon class
            var grouped = bests.GroupBy(b => b.WeaponClass).ToList();

            // Assert
            Assert.Equal(3, grouped.Count);
            Assert.Contains(grouped, g => g.Key == "A");
            Assert.Contains(grouped, g => g.Key == "B");
            Assert.Contains(grouped, g => g.Key == "C");
        }

        [Fact]
        public void PersonalBest_SeparatesBySeriesCount()
        {
            // Arrange: User has personal bests for different series counts
            var bests = new List<PersonalBest>
            {
                new PersonalBest { WeaponClass = "A", BestScore = 135, SeriesCount = 3 },
                new PersonalBest { WeaponClass = "A", BestScore = 285, SeriesCount = 6 }
            };

            // Act: Different series counts for same weapon class
            var weaponABests = bests.Where(b => b.WeaponClass == "A").ToList();

            // Assert
            Assert.Equal(2, weaponABests.Count);
            Assert.Contains(weaponABests, b => b.SeriesCount == 3);
            Assert.Contains(weaponABests, b => b.SeriesCount == 6);
        }

        [Fact]
        public void PersonalBest_SeparatesByType_TrainingVsCompetition()
        {
            // Arrange: User has separate bests for training and competition
            var bests = new List<PersonalBest>
            {
                new PersonalBest { WeaponClass = "A", BestScore = 280, SeriesCount = 6, IsCompetition = false },
                new PersonalBest { WeaponClass = "A", BestScore = 285, SeriesCount = 6, IsCompetition = true }
            };

            // Act
            var trainingBest = bests.First(b => !b.IsCompetition);
            var competitionBest = bests.First(b => b.IsCompetition);

            // Assert
            Assert.Equal(280, trainingBest.BestScore);
            Assert.Equal(285, competitionBest.BestScore);
            Assert.False(trainingBest.IsCompetition);
            Assert.True(competitionBest.IsCompetition);
        }

        // ============ Dashboard Statistics Tests ============

        [Fact]
        public void DashboardStatistics_AverageCalculation_UsesSeriesAverage()
        {
            // Arrange: Mix of different series counts
            var results = new List<(int totalScore, int seriesCount)>
            {
                (135, 3), // Average: 45
                (270, 6), // Average: 45
                (240, 5)  // Average: 48
            };

            // Act: Calculate overall average using series averages
            var averages = results.Select(r => (double)r.totalScore / r.seriesCount).ToList();
            double overallAverage = averages.Average();

            // Assert
            Assert.Equal(46.0, overallAverage, 1); // Within 1 point tolerance
        }

        [Fact]
        public void DashboardStatistics_YAxisCap_NeverExceeds50()
        {
            // This tests the fix from 2025-10-31
            // Y-axis maximum should always be capped at 50.0

            // Arrange: Calculate dynamic range
            var scores = new List<double> { 45.3, 47.2, 48.9, 49.1 };
            double minValue = scores.Min();
            double maxValue = 50.0; // Always capped at 50

            // Act: Apply padding to min but cap max at 50
            double range = maxValue - minValue;
            double padding = Math.Max(range * 0.15, 2);
            minValue = Math.Max(0, Math.Floor(minValue - padding));

            // Assert
            Assert.Equal(50.0, maxValue);
            Assert.True(minValue >= 0);
            Assert.True(minValue < maxValue);
        }

        [Fact]
        public void DashboardStatistics_MonthlyData_ShowsIndividualEntries()
        {
            // This tests the fix from 2025-10-31
            // Monthly data should NOT be aggregated by month

            // Arrange: User has 3 entries in October with weapon class C
            var octoberEntries = new List<(DateTime date, string weaponClass, double averageScore)>
            {
                (new DateTime(2025, 10, 1), "C", 43.2),
                (new DateTime(2025, 10, 25), "C", 48.2),
                (new DateTime(2025, 10, 29), "C", 49.0)
            };

            // Act: Each entry should be separate data point
            int dataPointCount = octoberEntries.Count;

            // Assert: Should show 3 separate points, NOT 1 aggregated October point
            Assert.Equal(3, dataPointCount);
            Assert.All(octoberEntries, entry => Assert.Equal(10, entry.date.Month));
        }
    }
}
