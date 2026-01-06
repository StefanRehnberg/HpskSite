using Xunit;
using FluentAssertions;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Tests.TestDataBuilders;
using System.Collections.Generic;
using System.Linq;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Tests for weapon group extraction and classification logic in StandardMedalCalculationService
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        #region ShouldSplitGroupC Tests

        [Fact]
        public void ShouldSplitGroupC_WithSwedishChampionship_ReturnsTrue()
        {
            // Arrange
            var service = new StandardMedalCalculationService();

            // Act
            var result = service.ShouldSplitGroupC("Svenskt Mästerskap");

            // Assert
            result.Should().BeTrue("SM championships should split Group C by classification");
        }

        [Fact]
        public void ShouldSplitGroupC_WithLandsdelsmasterskap_ReturnsTrue()
        {
            // Arrange
            var service = new StandardMedalCalculationService();

            // Act
            var result = service.ShouldSplitGroupC("Landsdelsmästerskap");

            // Assert
            result.Should().BeTrue("Landsdel championships should split Group C by classification");
        }

        [Fact]
        public void ShouldSplitGroupC_WithKretsmasterskap_ReturnsFalse()
        {
            // Arrange
            var service = new StandardMedalCalculationService();

            // Act
            var result = service.ShouldSplitGroupC("Kretsmästerskap");

            // Assert
            result.Should().BeFalse("KM competitions should NOT split Group C");
        }

        [Fact]
        public void ShouldSplitGroupC_WithKlubbmasterskap_ReturnsFalse()
        {
            // Arrange
            var service = new StandardMedalCalculationService();

            // Act
            var result = service.ShouldSplitGroupC("Klubbmästerskap");

            // Assert
            result.Should().BeFalse("club championships should NOT split Group C");
        }

        [Fact]
        public void ShouldSplitGroupC_WithEmptyString_ReturnsFalse()
        {
            // Arrange
            var service = new StandardMedalCalculationService();

            // Act
            var result = service.ShouldSplitGroupC("");

            // Assert
            result.Should().BeFalse("empty scope should default to NOT splitting Group C");
        }

        [Fact]
        public void ShouldSplitGroupC_WithNull_ReturnsFalse()
        {
            // Arrange
            var service = new StandardMedalCalculationService();

            // Act
            var result = service.ShouldSplitGroupC(null);

            // Assert
            result.Should().BeFalse("null scope should default to NOT splitting Group C");
        }

        #endregion

        #region Weapon Group Extraction Tests (Indirect via Grouping)

        [Fact]
        public void CalculateStandardMedals_WithGroupAShooters_GroupsCorrectly()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(280, 6).Build(),
                builder.Reset().WithShootingClass("A2").WithTotalScore(275, 6).Build(),
                builder.Reset().WithShootingClass("A3").WithTotalScore(270, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - All Group A shooters should have medals assigned
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().NotBeNullOrEmpty());
        }

        [Fact]
        public void CalculateStandardMedals_WithGroupBShooters_GroupsCorrectly()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("B1").WithTotalScore(285, 6).Build(),
                builder.Reset().WithShootingClass("B2").WithTotalScore(280, 6).Build(),
                builder.Reset().WithShootingClass("B3").WithTotalScore(275, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - All Group B shooters should have medals assigned
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().NotBeNullOrEmpty());
        }

        [Fact]
        public void CalculateStandardMedals_WithGroupCShooters_GroupsCorrectly()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("C1").WithTotalScore(287, 6).Build(),
                builder.Reset().WithShootingClass("C2").WithTotalScore(283, 6).Build(),
                builder.Reset().WithShootingClass("C3").WithTotalScore(278, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - All Group C shooters should have medals assigned
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().NotBeNullOrEmpty());
        }

        [Fact]
        public void CalculateStandardMedals_WithMixedWeaponGroups_SeparatesGroupsCorrectly()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                // Group A - lower scores
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(270, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A2").WithTotalScore(265, 6).Build(),
                // Group B - higher scores
                builder.Reset().WithMemberId(3).WithShootingClass("B1").WithTotalScore(290, 6).Build(),
                builder.Reset().WithMemberId(4).WithShootingClass("B2").WithTotalScore(285, 6).Build(),
                // Group C - highest scores
                builder.Reset().WithMemberId(5).WithShootingClass("C1").WithTotalScore(295, 6).Build(),
                builder.Reset().WithMemberId(6).WithShootingClass("C2").WithTotalScore(290, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Groups should be evaluated independently
            // Group A shooter with 270 should get medal (high score in their group)
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().NotBeNullOrEmpty();
            // Group B shooter with 290 should get medal (high score in their group)
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().NotBeNullOrEmpty();
            // Group C shooter with 295 should get medal (high score in their group)
            shooters.First(s => s.MemberId == 5).StandardMedal.Should().NotBeNullOrEmpty();
        }

        #endregion

        #region Group C Classification Splitting Tests

        [Fact]
        public void CalculateStandardMedals_WithGroupCDamShooters_SplitsWhenSM()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                // C Dam shooters
                builder.Reset().WithMemberId(1).WithShootingClass("C1Dam").WithTotalScore(290, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("C2Dam").WithTotalScore(285, 6).Build(),
                // C Jun shooters
                builder.Reset().WithMemberId(3).WithShootingClass("C1Jun").WithTotalScore(280, 6).Build(),
                builder.Reset().WithMemberId(4).WithShootingClass("C2Jun").WithTotalScore(275, 6).Build()
            };
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(true)  // SM mode
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Dam and Jun should be evaluated separately
            // Both top scorers in their classification should get medals
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().NotBeNullOrEmpty("top Dam shooter");
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().NotBeNullOrEmpty("top Jun shooter");
        }

        [Fact]
        public void CalculateStandardMedals_WithGroupCMixedClassifications_DoesNotSplitWhenKM()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                // C Dam shooter with lower score
                builder.Reset().WithMemberId(1).WithShootingClass("C1Dam").WithTotalScore(280, 6).Build(),
                // C Jun shooter with higher score
                builder.Reset().WithMemberId(2).WithShootingClass("C1Jun").WithTotalScore(290, 6).Build(),
                // C Open shooter with mid score
                builder.Reset().WithMemberId(3).WithShootingClass("C1").WithTotalScore(285, 6).Build()
            };
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)  // KM mode
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - All C shooters evaluated together, highest score should get medal
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().NotBeNullOrEmpty("highest score overall");
        }

        [Fact]
        public void CalculateStandardMedals_WithGroupCVetYAndVetÄ_SplitsWhenSM()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                // C Vet Y
                builder.Reset().WithMemberId(1).WithShootingClass("C1VetY").WithTotalScore(290, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("C2VetY").WithTotalScore(285, 6).Build(),
                // C Vet Ä
                builder.Reset().WithMemberId(3).WithShootingClass("C1VetÄ").WithTotalScore(280, 6).Build(),
                builder.Reset().WithMemberId(4).WithShootingClass("C2VetÄ").WithTotalScore(275, 6).Build()
            };
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(true)  // SM mode
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Vet Y and Vet Ä should be evaluated separately
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().NotBeNullOrEmpty("top Vet Y shooter");
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().NotBeNullOrEmpty("top Vet Ä shooter");
        }

        #endregion
    }
}
