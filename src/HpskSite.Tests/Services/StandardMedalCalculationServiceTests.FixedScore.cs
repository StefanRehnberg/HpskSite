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
    /// Tests for fixed score table medal calculation (Method B)
    /// Validates score requirements from BR-PS.2.4.2
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        #region 6 Series Tests

        [Theory]
        [InlineData("A1", 277, "S")]   // Group A: 277+ = Silver
        [InlineData("A2", 267, "B")]   // Group A: 267-276 = Bronze
        [InlineData("A3", 266, "")]    // Group A: <267 = No medal
        public void CalculateStandardMedals_GroupA_6Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group A with score {score} in 6 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group A with score {score} in 6 series should get {expectedMedal}");
            }
        }

        [Theory]
        [InlineData("B1", 282, "S")]   // Group B: 282+ = Silver
        [InlineData("B2", 273, "B")]   // Group B: 273-281 = Bronze
        [InlineData("B3", 272, "")]    // Group B: <273 = No medal
        public void CalculateStandardMedals_GroupB_6Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group B with score {score} in 6 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group B with score {score} in 6 series should get {expectedMedal}");
            }
        }

        [Theory]
        [InlineData("C1", 283, "S")]   // Group C: 283+ = Silver
        [InlineData("C2", 276, "B")]   // Group C: 276-282 = Bronze
        [InlineData("C3", 275, "")]    // Group C: <276 = No medal
        public void CalculateStandardMedals_GroupC_6Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group C with score {score} in 6 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group C with score {score} in 6 series should get {expectedMedal}");
            }
        }

        #endregion

        #region 7 Series Tests

        [Theory]
        [InlineData("A1", 323, "S")]   // Group A: 323+ = Silver
        [InlineData("A2", 312, "B")]   // Group A: 312-322 = Bronze
        [InlineData("A3", 311, "")]    // Group A: <312 = No medal
        public void CalculateStandardMedals_GroupA_7Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 7).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(7).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group A with score {score} in 7 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group A with score {score} in 7 series should get {expectedMedal}");
            }
        }

        [Theory]
        [InlineData("B1", 329, "S")]   // Group B: 329+ = Silver
        [InlineData("B2", 319, "B")]   // Group B: 319-328 = Bronze
        [InlineData("B3", 318, "")]    // Group B: <319 = No medal
        public void CalculateStandardMedals_GroupB_7Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 7).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(7).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group B with score {score} in 7 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group B with score {score} in 7 series should get {expectedMedal}");
            }
        }

        [Theory]
        [InlineData("C1", 330, "S")]   // Group C: 330+ = Silver
        [InlineData("C2", 322, "B")]   // Group C: 322-329 = Bronze
        [InlineData("C3", 321, "")]    // Group C: <322 = No medal
        public void CalculateStandardMedals_GroupC_7Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 7).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(7).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group C with score {score} in 7 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group C with score {score} in 7 series should get {expectedMedal}");
            }
        }

        #endregion

        #region 10 Series Tests

        [Theory]
        [InlineData("A1", 461, "S")]   // Group A: 461+ = Silver
        [InlineData("A2", 445, "B")]   // Group A: 445-460 = Bronze
        [InlineData("A3", 444, "")]    // Group A: <445 = No medal
        public void CalculateStandardMedals_GroupA_10Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 10).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(10).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group A with score {score} in 10 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group A with score {score} in 10 series should get {expectedMedal}");
            }
        }

        [Theory]
        [InlineData("B1", 470, "S")]   // Group B: 470+ = Silver
        [InlineData("B2", 455, "B")]   // Group B: 455-469 = Bronze
        [InlineData("B3", 454, "")]    // Group B: <455 = No medal
        public void CalculateStandardMedals_GroupB_10Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 10).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(10).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group B with score {score} in 10 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group B with score {score} in 10 series should get {expectedMedal}");
            }
        }

        [Theory]
        [InlineData("C1", 471, "S")]   // Group C: 471+ = Silver
        [InlineData("C2", 460, "B")]   // Group C: 460-470 = Bronze
        [InlineData("C3", 459, "")]    // Group C: <460 = No medal
        public void CalculateStandardMedals_GroupC_10Series_AwardsCorrectMedals(
            string shootingClass, int score, string expectedMedal)
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass(shootingClass).WithTotalScore(score, 10).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(10).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            if (string.IsNullOrEmpty(expectedMedal))
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"Group C with score {score} in 10 series should get no medal");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"Group C with score {score} in 10 series should get {expectedMedal}");
            }
        }

        #endregion

        #region Boundary Tests

        [Fact]
        public void CalculateStandardMedals_ExactBronzeScore_AwardsBronze()
        {
            // Arrange - Group A with exactly 267 (bronze threshold)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(267, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters[0].StandardMedal.Should().Be("B", "exact bronze threshold should award bronze");
        }

        [Fact]
        public void CalculateStandardMedals_OneBelowBronze_AwardsNoMedal()
        {
            // Arrange - Group A with 266 (one below bronze threshold)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(266, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters[0].StandardMedal.Should().BeNullOrEmpty("one point below bronze should get no medal");
        }

        [Fact]
        public void CalculateStandardMedals_ExactSilverScore_AwardsSilver()
        {
            // Arrange - Group A with exactly 277 (silver threshold)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(277, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters[0].StandardMedal.Should().Be("S", "exact silver threshold should award silver");
        }

        [Fact]
        public void CalculateStandardMedals_OneBelowSilver_AwardsBronze()
        {
            // Arrange - Group A with 276 (one below silver, but above bronze)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithShootingClass("A1").WithTotalScore(276, 6).Build()
            };
            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters[0].StandardMedal.Should().Be("B", "one point below silver but above bronze should award bronze");
        }

        #endregion
    }
}
