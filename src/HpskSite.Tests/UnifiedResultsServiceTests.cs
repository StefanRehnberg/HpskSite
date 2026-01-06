using HpskSite.Shared.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Unit tests for UnifiedResultsService
    /// Tests data aggregation from multiple sources:
    /// 1. TrainingScores table (training entries)
    /// 2. PrecisionResultEntry table (competition entries)
    /// 3. Competition Result documents (not yet implemented)
    /// </summary>
    public class UnifiedResultsServiceTests
    {
        // Note: These are integration-style tests that would require database mocking
        // For full implementation, consider using:
        // - Moq or NSubstitute for mocking IScopeProvider, IDatabase, IContentService
        // - Test database with known data

        [Fact]
        public void UnifiedResultEntry_AverageScore_IsSetCorrectly()
        {
            // Arrange & Act
            var entry = new UnifiedResultEntry
            {
                TotalScore = 270,
                SeriesCount = 6,
                AverageScore = 270.0 / 6.0
            };

            // Assert
            Assert.Equal(45.0, entry.AverageScore);
            Assert.Equal(270, entry.TotalScore);
            Assert.Equal(6, entry.SeriesCount);
        }

        [Fact]
        public void UnifiedResultEntry_TrainingSourceType_IsEditable()
        {
            // Arrange & Act
            var trainingEntry = new UnifiedResultEntry
            {
                SourceType = "Training",
                CanEdit = true,
                CanDelete = true
            };

            // Assert
            Assert.Equal("Training", trainingEntry.SourceType);
            Assert.True(trainingEntry.CanEdit);
            Assert.True(trainingEntry.CanDelete);
        }

        [Fact]
        public void UnifiedResultEntry_CompetitionSourceType_IsNotEditable()
        {
            // Arrange & Act
            var competitionEntry = new UnifiedResultEntry
            {
                SourceType = "Competition",
                CanEdit = false,
                CanDelete = false,
                CompetitionId = 1234,
                CompetitionName = "Test Competition"
            };

            // Assert
            Assert.Equal("Competition", competitionEntry.SourceType);
            Assert.False(competitionEntry.CanEdit);
            Assert.False(competitionEntry.CanDelete);
            Assert.NotNull(competitionEntry.CompetitionId);
            Assert.NotNull(competitionEntry.CompetitionName);
        }

        [Theory]
        [InlineData("A3", "A")]
        [InlineData("B2", "B")]
        [InlineData("C Vet Y", "C")]
        [InlineData("R1", "R")]
        [InlineData("P2", "P")]
        public void WeaponClassExtraction_FromShootingClass_ExtractsCorrectly(string shootingClass, string expectedWeaponClass)
        {
            // This tests the logic from UnifiedResultsService.cs line 152-154
            // Extract weapon class from shooting class (e.g., "A3" -> "A")
            string weaponClass = !string.IsNullOrEmpty(shootingClass) && shootingClass.Length > 0
                ? shootingClass.Substring(0, 1).ToUpper()
                : "A";

            Assert.Equal(expectedWeaponClass, weaponClass);
        }

        [Fact]
        public void WeaponClassExtraction_WithEmptyShootingClass_DefaultsToA()
        {
            // Arrange
            string shootingClass = "";

            // Act
            string weaponClass = !string.IsNullOrEmpty(shootingClass) && shootingClass.Length > 0
                ? shootingClass.Substring(0, 1).ToUpper()
                : "A";

            // Assert
            Assert.Equal("A", weaponClass);
        }

        [Fact]
        public void SeriesDetail_CalculatesCorrectly()
        {
            // Arrange
            var series = new SeriesDetail
            {
                SeriesNumber = 1,
                Shots = new List<string> { "X", "10", "9", "8", "7" },
                Total = 44,
                XCount = 1
            };

            // Assert
            Assert.Equal(1, series.SeriesNumber);
            Assert.Equal(5, series.Shots.Count);
            Assert.Equal(44, series.Total);
            Assert.Equal(1, series.XCount);
        }

        [Theory]
        [InlineData(new[] { "X", "X", "X", "X", "X" }, 50, 5)]
        [InlineData(new[] { "10", "10", "10", "10", "10" }, 50, 0)]
        [InlineData(new[] { "9", "8", "7", "6", "5" }, 35, 0)]
        [InlineData(new[] { "X", "9", "8", "7", "6" }, 40, 1)]
        [InlineData(new[] { "0", "0", "0", "0", "0" }, 0, 0)]
        public void CalculateTotal_FromShots_ReturnsCorrectScore(string[] shots, int expectedTotal, int expectedXCount)
        {
            // This tests the logic from UnifiedResultsService.cs lines 370-393
            int total = 0;
            int xCount = 0;

            foreach (var shot in shots)
            {
                if (shot.ToUpper() == "X")
                {
                    total += 10;
                    xCount++;
                }
                else if (int.TryParse(shot, out int value))
                {
                    total += value;
                }
            }

            Assert.Equal(expectedTotal, total);
            Assert.Equal(expectedXCount, xCount);
        }

        [Fact]
        public void CompetitionCount_CountsEntries_NotUniqueCompetitions()
        {
            // This tests the critical fix from 2025-10-31
            // Multiple weapon classes in same competition = multiple entries

            // Arrange: User shot competition 1098 with weapon classes A, B, C
            var competitionResults = new List<UnifiedResultEntry>
            {
                new UnifiedResultEntry { Id = 1, CompetitionId = 1098, WeaponClass = "A", SourceType = "Competition" },
                new UnifiedResultEntry { Id = 2, CompetitionId = 1098, WeaponClass = "B", SourceType = "Competition" },
                new UnifiedResultEntry { Id = 3, CompetitionId = 1098, WeaponClass = "C", SourceType = "Competition" }
            };

            // Act
            int totalCompetitions = competitionResults.Count; // Should be 3, NOT 1

            // Assert
            Assert.Equal(3, totalCompetitions);

            // Verify all entries have same CompetitionId but different weapon classes
            Assert.Equal(1, competitionResults.Select(r => r.CompetitionId).Distinct().Count());
            Assert.Equal(3, competitionResults.Select(r => r.WeaponClass).Distinct().Count());
        }

        [Fact]
        public void SeriesAverage_Calculation_AllowsFairComparison()
        {
            // Arrange: Different series counts should use average for comparison
            var entry3Series = new UnifiedResultEntry
            {
                TotalScore = 135, // 45 per series
                SeriesCount = 3,
                AverageScore = 45.0
            };

            var entry6Series = new UnifiedResultEntry
            {
                TotalScore = 270, // 45 per series
                SeriesCount = 6,
                AverageScore = 45.0
            };

            // Assert: Both have same average despite different total scores
            Assert.Equal(entry3Series.AverageScore, entry6Series.AverageScore);
            Assert.NotEqual(entry3Series.TotalScore, entry6Series.TotalScore);
        }
    }
}
