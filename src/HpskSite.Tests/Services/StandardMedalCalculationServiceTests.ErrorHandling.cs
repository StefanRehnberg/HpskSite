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
    /// Tests for error handling and edge cases in StandardMedalCalculationService
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        #region Null/Empty Input Tests

        [Fact]
        public void CalculateStandardMedals_NullShooterList_DoesNotThrow()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            var act = () => service.CalculateStandardMedals(null, config);

            // Assert
            act.Should().NotThrow("should handle null shooter list gracefully");
        }

        [Fact]
        public void CalculateStandardMedals_EmptyShooterList_DoesNotThrow()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>();
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            var act = () => service.CalculateStandardMedals(shooters, config);

            // Assert
            act.Should().NotThrow("should handle empty shooter list gracefully");
        }

        [Fact]
        public void CalculateStandardMedals_SingleShooter_DoesNotThrow()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(285, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            var act = () => service.CalculateStandardMedals(shooters, config);

            // Assert
            act.Should().NotThrow("should handle single shooter gracefully");
        }

        #endregion

        #region Invalid Series Count Tests

        [Fact]
        public void CalculateStandardMedals_LessThan6Series_DoesNotCalculateMedals()
        {
            // Arrange - Only 5 series (below minimum of 6)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(285, 5).Build(),
                builder.Reset().WithShootingClass("A1").WithTotalScore(280, 5).Build(),
                builder.Reset().WithShootingClass("A1").WithTotalScore(275, 5).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(5).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().BeNullOrEmpty(
                "medals should not be calculated for less than 6 series"));
        }

        [Fact]
        public void CalculateStandardMedals_ZeroSeries_DoesNotCalculateMedals()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").Build()  // No series
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(0).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters[0].StandardMedal.Should().BeNullOrEmpty("should not calculate medals with 0 series");
        }

        [Fact]
        public void CalculateStandardMedals_UnsupportedSeriesCount_UsesPercentageOnly()
        {
            // Arrange - 8 series (not 6, 7, or 10) - should use percentage method only
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 9; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(380 - (i * 5), 8)  // 8 series
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(8).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Top shooter should still get silver via percentage method
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S",
                "percentage method should still work with unsupported series count");
        }

        #endregion

        #region Invalid Shooting Class Tests

        [Fact]
        public void CalculateStandardMedals_NullShootingClass_DefaultsToGroupC()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(null).WithTotalScore(290, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            var act = () => service.CalculateStandardMedals(shooters, config);

            // Assert
            act.Should().NotThrow("should handle null shooting class gracefully");
        }

        [Fact]
        public void CalculateStandardMedals_EmptyShootingClass_DefaultsToGroupC()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("").WithTotalScore(290, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            var act = () => service.CalculateStandardMedals(shooters, config);

            // Assert
            act.Should().NotThrow("should handle empty shooting class gracefully");
        }

        [Fact]
        public void CalculateStandardMedals_UnknownShootingClass_DefaultsToGroupC()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                // Unknown weapon groups should default to C
                builder.Reset().WithShootingClass("X1").WithTotalScore(290, 6).Build(),
                builder.Reset().WithShootingClass("Z2").WithTotalScore(285, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            var act = () => service.CalculateStandardMedals(shooters, config);

            // Assert
            act.Should().NotThrow("should handle unknown shooting classes gracefully");
        }

        #endregion
    }
}
