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
    /// Tests for percentage-based medal calculation (Method A: top 1/9 Silver, top 1/3 Bronze)
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        #region Quota Calculation Tests (Rounding Down)

        [Theory]
        [InlineData(9, 1, 2)]   // 9 shooters: 1 silver, 3-1=2 bronze-only
        [InlineData(10, 1, 2)]  // 10 shooters: 1 silver, 3-1=2 bronze-only (rounds down from 1.11 and 3.33)
        [InlineData(18, 2, 4)]  // 18 shooters: 2 silver, 6-2=4 bronze-only
        [InlineData(27, 3, 6)]  // 27 shooters: 3 silver, 9-3=6 bronze-only
        [InlineData(30, 3, 7)] // 30 shooters: 3 silver, 10-3=7 bronze-only
        public void CalculateStandardMedals_PercentageMethod_CalculatesQuotasCorrectly(
            int shooterCount, int expectedSilver, int expectedBronze)
        {
            // Arrange - Use scores below fixed score thresholds (< 267) to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < shooterCount; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(266 - i, 6)  // Start at 266 (below bronze threshold) to avoid fixed score medals
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var silverCount = shooters.Count(s => s.StandardMedal == "S");
            var bronzeCount = shooters.Count(s => s.StandardMedal == "B");

            silverCount.Should().Be(expectedSilver, $"with {shooterCount} shooters, should award {expectedSilver} silver medals");
            bronzeCount.Should().Be(expectedBronze, $"with {shooterCount} shooters, should award {expectedBronze} bronze medals");
        }

        [Fact]
        public void CalculateStandardMedals_With8Shooters_Awards0Silver()
        {
            // Arrange - 8 shooters: 8/9 = 0.88, rounds down to 0 silver via percentage
            // Use scores below silver threshold (< 277) to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 8; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(266 - i, 6)  // Scores 266-259, below bronze threshold
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - 8/9 = 0 silver, 8/3 = 2 bronze (via percentage only)
            shooters.Should().NotContain(s => s.StandardMedal == "S", "8/9 rounds down to 0 silver medals");
            shooters.Count(s => s.StandardMedal == "B").Should().Be(2, "8/3 = 2 bronze medals");
        }

        [Fact]
        public void CalculateStandardMedals_With2Shooters_GetsFixedScoreMedals()
        {
            // Arrange - 2 shooters: percentage gives 0 medals (2/9 = 0, 2/3 = 0)
            // But fixed score method awards silver (285, 280 both >= 277)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(285, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(280, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Both get silver via fixed score method despite no percentage medals
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().Be("S", "both qualify via fixed score method (>= 277)"));
        }

        #endregion

        #region Medal Award Priority Tests

        [Fact]
        public void CalculateStandardMedals_TopShooter_GetsSilver()
        {
            // Arrange - 9 shooters, top 1 should get Silver via percentage (9/9 = 1)
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 9; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(266 - i, 6)  // Start at 266 to avoid fixed score medals
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S", "top shooter should get Silver");
        }

        [Fact]
        public void CalculateStandardMedals_SilverRangeShooters_DoNotGetDowngradedToBronze()
        {
            // Arrange - 18 shooters: 2 silver (18/9), 6 bronze (18/3)
            // Ensure shooter ranked 2nd (last silver) keeps Silver and doesn't get "downgraded" to Bronze
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 18; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(266 - i, 6)  // Start at 266 to avoid fixed score medals
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S", "1st place");
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().Be("S", "2nd place - last silver spot");
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("B", "3rd place - first bronze-only spot");
        }

        #endregion

        #region Tie Handling Tests

        [Fact]
        public void CalculateStandardMedals_TieAtBronzeCutoff_AwardsMedalToAllTied()
        {
            // Arrange - 9 shooters, top 3 get bronze (9/3), 3rd and 4th are tied
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(266, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(265, 6).Build(),
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(264, 6).Build(), // Last bronze spot
                builder.Reset().WithMemberId(4).WithShootingClass("A1").WithTotalScore(264, 6).Build(), // TIED with #3
                builder.Reset().WithMemberId(5).WithShootingClass("A1").WithTotalScore(263, 6).Build(),
                builder.Reset().WithMemberId(6).WithShootingClass("A1").WithTotalScore(262, 6).Build(),
                builder.Reset().WithMemberId(7).WithShootingClass("A1").WithTotalScore(261, 6).Build(),
                builder.Reset().WithMemberId(8).WithShootingClass("A1").WithTotalScore(260, 6).Build(),
                builder.Reset().WithMemberId(9).WithShootingClass("A1").WithTotalScore(259, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("B", "last bronze spot");
            shooters.First(s => s.MemberId == 4).StandardMedal.Should().Be("B", "tied with last bronze spot - should also get bronze");
            shooters.First(s => s.MemberId == 5).StandardMedal.Should().BeNullOrEmpty("not tied, no medal");
        }

        [Fact]
        public void CalculateStandardMedals_TieAtSilverCutoff_AwardsSilverToAllTied()
        {
            // Arrange - 18 shooters, top 2 get silver (18/9), 2nd and 3rd are tied
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // First shooter - clear winner
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(266, 6).Build());

            // 2nd and 3rd tied at 265
            shooters.Add(builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(265, 6).Build()); // Last silver spot
            shooters.Add(builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(265, 6).Build()); // TIED

            // Fill rest with descending scores
            for (int i = 4; i <= 18; i++)
            {
                shooters.Add(builder.Reset().WithMemberId(i).WithShootingClass("A1").WithTotalScore(264 - (i - 4), 6).Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S", "1st place");
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().Be("S", "2nd place - last silver spot");
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("S", "tied with last silver - should get silver too");
        }

        [Fact]
        public void CalculateStandardMedals_TieWithXCount_BreaksTieCorrectly()
        {
            // Arrange - Same total score, but different X counts. 9 shooters: 1 silver (9/9)
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1")
                    .WithSeriesAndXCounts(new List<(int, int)> { (44, 5), (44, 4), (44, 5), (44, 3), (44, 5), (44, 4) })  // 264, 26X
                    .Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A1")
                    .WithSeriesAndXCounts(new List<(int, int)> { (44, 3), (44, 3), (44, 4), (44, 3), (44, 4), (44, 3) })  // 264, 20X
                    .Build(),
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(263, 6).Build(),
                builder.Reset().WithMemberId(4).WithShootingClass("A1").WithTotalScore(262, 6).Build(),
                builder.Reset().WithMemberId(5).WithShootingClass("A1").WithTotalScore(261, 6).Build(),
                builder.Reset().WithMemberId(6).WithShootingClass("A1").WithTotalScore(260, 6).Build(),
                builder.Reset().WithMemberId(7).WithShootingClass("A1").WithTotalScore(259, 6).Build(),
                builder.Reset().WithMemberId(8).WithShootingClass("A1").WithTotalScore(258, 6).Build(),
                builder.Reset().WithMemberId(9).WithShootingClass("A1").WithTotalScore(257, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S", "same score but higher X count - ranks 1st, gets silver (9/9 = 1)");
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().NotBe("S", "same score but lower X count - ranks 2nd, no silver (only 1 awarded)");
        }

        [Fact]
        public void CalculateStandardMedals_MultipleTiedAtCutoff_AwardsToAll()
        {
            // Arrange - 9 shooters, 3 tied at bronze cutoff (9/3 = 3 bronze)
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(266, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(265, 6).Build(),
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(264, 6).Build(), // Last bronze
                builder.Reset().WithMemberId(4).WithShootingClass("A1").WithTotalScore(264, 6).Build(), // TIED
                builder.Reset().WithMemberId(5).WithShootingClass("A1").WithTotalScore(264, 6).Build(), // TIED
                builder.Reset().WithMemberId(6).WithShootingClass("A1").WithTotalScore(263, 6).Build(),
                builder.Reset().WithMemberId(7).WithShootingClass("A1").WithTotalScore(262, 6).Build(),
                builder.Reset().WithMemberId(8).WithShootingClass("A1").WithTotalScore(261, 6).Build(),
                builder.Reset().WithMemberId(9).WithShootingClass("A1").WithTotalScore(260, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 4).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 5).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 6).StandardMedal.Should().BeNullOrEmpty("not tied");
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void CalculateStandardMedals_With3Shooters_AllGetSilverViaFixedScore()
        {
            // Arrange - 3 shooters: percentage gives 0 silver (3/9=0), 1 bronze (3/3=1)
            // But all 3 qualify for silver via fixed score (290, 285, 280 all >= 277)
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(290, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(285, 6).Build(),
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(280, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - All get silver via fixed score, overriding percentage result
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().Be("S", "all qualify via fixed score (>= 277)"));
        }

        [Fact]
        public void CalculateStandardMedals_AllShootersTied_AwardsToAll()
        {
            // Arrange - All 9 shooters have exact same score (all tied at bronze cutoff)
            // Use score below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 9; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(265, 6)  // All tied, below bronze threshold
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            // All should get bronze because they're all tied at the cutoff (9/3 = 3, but all are tied)
            var medalCount = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalCount.Should().Be(9, "all tied shooters should get bronze when tied at cutoff");
        }

        [Fact]
        public void CalculateStandardMedals_LargeGroup_AwardsCorrectly()
        {
            // Arrange - 100 shooters: 100/9 = 11 silver, 100/3 = 33 bronze (via percentage)
            // Use scores below fixed score thresholds to test pure percentage logic
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 100; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i + 1)
                    .WithShootingClass("A1")
                    .WithTotalScore(266 - i, 6)  // Start at 266 to avoid fixed score medals
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var silverCount = shooters.Count(s => s.StandardMedal == "S");
            var bronzeCount = shooters.Count(s => s.StandardMedal == "B");

            silverCount.Should().Be(11, "100/9 = 11 silver medals");
            bronzeCount.Should().Be(33 - 11, "100/3 = 33 bronze total, but 11 already have silver, so 22 bronze-only");
        }

        #endregion
    }
}
