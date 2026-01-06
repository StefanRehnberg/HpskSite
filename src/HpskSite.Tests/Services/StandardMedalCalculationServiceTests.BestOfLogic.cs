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
    /// Tests for "best of" logic - ensuring shooters get the better medal from either method
    /// and medals are never downgraded
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        #region Best Of Logic Tests

        [Fact]
        public void CalculateStandardMedals_SilverFromPercentage_BronzeFromFixed_KeepsSilver()
        {
            // Arrange - Shooter qualifies for Silver via percentage but only Bronze via fixed score
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // Create 18 shooters so top 2 get silver (18/9=2)
            // First shooter has high rank but score only qualifies for bronze (267)
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(267, 6).Build());  // Rank 1: Silver (%) + Bronze (fixed) = Silver

            // Fill rest with lower scores
            for (int i = 2; i <= 18; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i)
                    .WithShootingClass("A1")
                    .WithTotalScore(266 - i, 6)
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S",
                "should keep silver from percentage method even though fixed score only gives bronze");
        }

        [Fact]
        public void CalculateStandardMedals_BronzeFromPercentage_SilverFromFixed_UpgradesToSilver()
        {
            // Arrange - Shooter qualifies for Bronze via percentage but Silver via fixed score
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // Create 18 shooters, shooter ranked 3rd gets bronze from percentage (18/3=6, top 6 get bronze)
            // But if their score is 277+, they should get silver from fixed score method
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(290, 6).Build());  // Rank 1
            shooters.Add(builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(285, 6).Build());  // Rank 2
            shooters.Add(builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(280, 6).Build());  // Rank 3: Bronze (%) + Silver (fixed) = Silver

            // Fill rest
            for (int i = 4; i <= 18; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i)
                    .WithShootingClass("A1")
                    .WithTotalScore(250 - i, 6)
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("S",
                "should upgrade to silver from fixed score method even though percentage only gives bronze");
        }

        [Fact]
        public void CalculateStandardMedals_NoMedalFromPercentage_BronzeFromFixed_GetsBronze()
        {
            // Arrange - Shooter doesn't qualify via percentage but does via fixed score
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // 5 shooters: 5/9=0 silver, 5/3=1 bronze
            // Shooter ranked 2nd gets no medal from percentage, but score 267 qualifies for bronze
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(270, 6).Build());  // Rank 1: Bronze (%)
            shooters.Add(builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(267, 6).Build());  // Rank 2: No medal (%) + Bronze (fixed) = Bronze
            shooters.Add(builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(265, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(4).WithShootingClass("A1").WithTotalScore(260, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(5).WithShootingClass("A1").WithTotalScore(255, 6).Build());

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().Be("B",
                "should get bronze from fixed score method even though percentage doesn't award medal");
        }

        [Fact]
        public void CalculateStandardMedals_BothMethodsGiveSilver_GetsSilver()
        {
            // Arrange - Shooter qualifies for Silver via both methods
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // 9 shooters: top 1 gets silver from percentage
            // Top shooter also has 277+ score for silver from fixed score
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(285, 6).Build());  // Silver from both

            for (int i = 2; i <= 9; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i)
                    .WithShootingClass("A1")
                    .WithTotalScore(265, 6)
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S",
                "should get silver when both methods award silver");
        }

        [Fact]
        public void CalculateStandardMedals_BothMethodsGiveBronze_GetsBronze()
        {
            // Arrange - Shooter qualifies for Bronze via both methods
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // 9 shooters: top 3 get bronze from percentage
            // Shooter ranked 3rd also has 267-276 score for bronze from fixed score
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(275, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(272, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(270, 6).Build());  // Bronze from both

            for (int i = 4; i <= 9; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(i)
                    .WithShootingClass("A1")
                    .WithTotalScore(260 - i, 6)
                    .Build());
            }

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("B",
                "should get bronze when both methods award bronze");
        }

        [Fact]
        public void CalculateStandardMedals_NeitherMethodGivesMedal_GetsNoMedal()
        {
            // Arrange - Shooter doesn't qualify via either method
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // 5 shooters: 5/9=0 silver, 5/3=1 bronze
            // Last shooter (rank 5) gets no medal from percentage, score 250 doesn't qualify for fixed score
            shooters.Add(builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(270, 6).Build());  // Bronze (%)
            shooters.Add(builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(265, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(260, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(4).WithShootingClass("A1").WithTotalScore(255, 6).Build());
            shooters.Add(builder.Reset().WithMemberId(5).WithShootingClass("A1").WithTotalScore(250, 6).Build());  // No medal from either

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 5).StandardMedal.Should().BeNullOrEmpty(
                "should get no medal when neither method awards one");
        }

        [Fact]
        public void CalculateStandardMedals_HighScoreLowRank_GetsSilverFromFixedScore()
        {
            // Arrange - Shooter has very high score but low rank (small group)
            // Should still get silver from fixed score method
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(290, 6).Build(),
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(285, 6).Build(),  // Rank 2, but 285 > 277 = Silver
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(280, 6).Build()   // Rank 3, but 280 > 277 = Silver
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().Be("S", "high score should get silver via fixed score");
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("S", "high score should get silver via fixed score");
        }

        [Fact]
        public void CalculateStandardMedals_LowScoreHighRank_KeepsBronzeFromPercentage()
        {
            // Arrange - Shooter has low score but high rank in small group
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(265, 6).Build(),  // Rank 1: Bronze (%), No medal (fixed)
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(260, 6).Build(),
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(255, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("B",
                "should keep bronze from percentage even though fixed score doesn't give medal");
        }

        [Fact]
        public void CalculateStandardMedals_MultipleShooters_EachGetsBestMedal()
        {
            // Arrange - Complex scenario with multiple shooters getting medals from different methods
            var service = new StandardMedalCalculationService();
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>
            {
                // 18 shooters: 18/9=2 silver (%), 18/3=6 bronze (%)
                // Group A thresholds: Bronze >= 267, Silver >= 277
                builder.Reset().WithMemberId(1).WithShootingClass("A1").WithTotalScore(290, 6).Build(),  // Rank 1: S(%) + S(fixed) = S
                builder.Reset().WithMemberId(2).WithShootingClass("A1").WithTotalScore(285, 6).Build(),  // Rank 2: S(%) + S(fixed) = S
                builder.Reset().WithMemberId(3).WithShootingClass("A1").WithTotalScore(280, 6).Build(),  // Rank 3: B(%) + S(fixed) = S
                builder.Reset().WithMemberId(4).WithShootingClass("A1").WithTotalScore(270, 6).Build(),  // Rank 4: B(%) + B(fixed) = B
                builder.Reset().WithMemberId(5).WithShootingClass("A1").WithTotalScore(269, 6).Build(),  // Rank 5: B(%) + B(fixed) = B
                builder.Reset().WithMemberId(6).WithShootingClass("A1").WithTotalScore(268, 6).Build(),  // Rank 6: B(%) + B(fixed) = B
                builder.Reset().WithMemberId(7).WithShootingClass("A1").WithTotalScore(267, 6).Build(),  // Rank 7: none(%) + B(fixed) = B
                builder.Reset().WithMemberId(8).WithShootingClass("A1").WithTotalScore(260, 6).Build(),  // Rank 8: none
                builder.Reset().WithMemberId(9).WithShootingClass("A1").WithTotalScore(255, 6).Build(),
                builder.Reset().WithMemberId(10).WithShootingClass("A1").WithTotalScore(250, 6).Build(),
                builder.Reset().WithMemberId(11).WithShootingClass("A1").WithTotalScore(245, 6).Build(),
                builder.Reset().WithMemberId(12).WithShootingClass("A1").WithTotalScore(240, 6).Build(),
                builder.Reset().WithMemberId(13).WithShootingClass("A1").WithTotalScore(235, 6).Build(),
                builder.Reset().WithMemberId(14).WithShootingClass("A1").WithTotalScore(230, 6).Build(),
                builder.Reset().WithMemberId(15).WithShootingClass("A1").WithTotalScore(225, 6).Build(),
                builder.Reset().WithMemberId(16).WithShootingClass("A1").WithTotalScore(220, 6).Build(),
                builder.Reset().WithMemberId(17).WithShootingClass("A1").WithTotalScore(215, 6).Build(),
                builder.Reset().WithMemberId(18).WithShootingClass("A1").WithTotalScore(210, 6).Build()
            };

            var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            shooters.First(s => s.MemberId == 1).StandardMedal.Should().Be("S");
            shooters.First(s => s.MemberId == 2).StandardMedal.Should().Be("S");
            shooters.First(s => s.MemberId == 3).StandardMedal.Should().Be("S");
            shooters.First(s => s.MemberId == 4).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 5).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 6).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 7).StandardMedal.Should().Be("B");
            shooters.First(s => s.MemberId == 8).StandardMedal.Should().BeNullOrEmpty();
        }

        #endregion
    }
}
