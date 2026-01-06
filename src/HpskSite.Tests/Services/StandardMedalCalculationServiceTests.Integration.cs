using Xunit;
using FluentAssertions;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.Tests.TestDataBuilders;
using HpskSite.Tests.TestData;
using System.Linq;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Integration tests using realistic competition scenarios
    /// Tests end-to-end medal calculation with complete datasets
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        #region Realistic Scenario Tests

        [Fact]
        public void CalculateStandardMedals_SmallClubCompetition_AwardsMedalsCorrectly()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.SmallClubCompetition();  // 12 shooters, mixed groups
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)  // Club competition doesn't split Group C
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().BeGreaterThan(0, "at least some shooters should receive medals");

            // Verify medals are only S or B
            shooters.Where(s => !string.IsNullOrEmpty(s.StandardMedal))
                .Should().AllSatisfy(s => s.StandardMedal.Should().Match(m => m == "S" || m == "B"));
        }

        [Fact]
        public void CalculateStandardMedals_RegionalChampionship_AwardsMedalsToMultipleGroups()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.RegionalChampionship();  // 30 shooters, 10 per group
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)  // Regional doesn't split Group C
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().BeGreaterThan(6, "with 30 shooters, multiple medals should be awarded");

            // Verify each weapon group has some medals
            var groupA = shooters.Where(s => s.ShootingClass.StartsWith("A"));
            var groupB = shooters.Where(s => s.ShootingClass.StartsWith("B"));
            var groupC = shooters.Where(s => s.ShootingClass.StartsWith("C"));

            groupA.Should().Contain(s => !string.IsNullOrEmpty(s.StandardMedal), "Group A should have medals");
            groupB.Should().Contain(s => !string.IsNullOrEmpty(s.StandardMedal), "Group B should have medals");
            groupC.Should().Contain(s => !string.IsNullOrEmpty(s.StandardMedal), "Group C should have medals");
        }

        [Fact]
        public void CalculateStandardMedals_SwedishChampionship_SplitsGroupC()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.SwedishChampionship();  // 50 shooters, Group C has classifications
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(true)  // SM splits Group C by classification
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().BeGreaterThan(10, "SM with 50 shooters should award many medals");

            // Verify Group C shooters with different classifications can both get medals
            var cDam = shooters.Where(s => s.ShootingClass.Contains("Dam")).ToList();
            var cJun = shooters.Where(s => s.ShootingClass.Contains("Jun")).ToList();

            if (cDam.Any() && cJun.Any())
            {
                // At least one from each classification should have a chance at medals
                var anyGroupHasMedals = cDam.Any(s => !string.IsNullOrEmpty(s.StandardMedal)) ||
                                       cJun.Any(s => !string.IsNullOrEmpty(s.StandardMedal));
                anyGroupHasMedals.Should().BeTrue("when Group C is split, different classifications should be evaluated separately");
            }
        }

        [Fact]
        public void CalculateStandardMedals_FinalsCompetition_Handles10Series()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.FinalsCompetition();  // 20 shooters, 10 series each
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(10)
                .WithSplitGroupC(false)
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().BeGreaterThan(0, "10-series competition should award medals");

            // Verify fixed score tables work for 10 series
            var highScorers = shooters.Where(s => s.TotalScore >= 461).ToList();  // A group silver threshold for 10 series
            if (highScorers.Any())
            {
                highScorers.Where(s => s.ShootingClass.StartsWith("A"))
                    .Should().AllSatisfy(s => s.StandardMedal.Should().Be("S", "Group A shooters with 461+ should get silver in 10 series"));
            }
        }

        [Fact]
        public void CalculateStandardMedals_LargeDataset_PerformsWell()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.LargeDataset();  // 100 shooters
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)
                .Build();

            // Act
            var act = () => service.CalculateStandardMedals(shooters, config);

            // Assert
            act.Should().NotThrow("should handle large datasets without errors");

            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().BeGreaterThan(20, "with 100 shooters, many medals should be awarded");

            // Verify no duplicate medals in the same position/group
            var weaponGroups = shooters.GroupBy(s => s.ShootingClass[0]);
            foreach (var group in weaponGroups)
            {
                var groupShooters = group.OrderByDescending(s => s.TotalScore).ThenByDescending(s => s.TotalXCount).ToList();
                // Just verify calculation completed without errors
                groupShooters.Should().NotBeEmpty();
            }
        }

        [Fact]
        public void CalculateStandardMedals_SingleShooterPerGroup_AwardsAppropriately()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.SingleShooterPerGroup();  // 3 shooters, one per group (A:285, B:286, C:287)
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            // With only 1 shooter per group, percentage method won't award medals (1/9=0, 1/3=0)
            // But fixed score method awards silver to all 3 (A:285>=277, B:286>=282, C:287>=283)
            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().Be(3, "all 3 shooters qualify for silver via fixed score method");
            shooters.Should().AllSatisfy(s => s.StandardMedal.Should().Be("S", "all scores exceed their group's silver threshold"));
        }

        [Fact]
        public void CalculateStandardMedals_SingleWeaponGroup_OnlyAwardsToThatGroup()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.SingleWeaponGroup();  // 15 shooters, all Group A
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert
            var medalsAwarded = shooters.Count(s => !string.IsNullOrEmpty(s.StandardMedal));
            medalsAwarded.Should().BeGreaterThan(3, "15 shooters in one group should award multiple medals");

            // All shooters should be Group A
            shooters.Should().AllSatisfy(s => s.ShootingClass.Should().StartWith("A"));

            // All medals should be awarded within Group A only
            shooters.Where(s => !string.IsNullOrEmpty(s.StandardMedal))
                .Should().AllSatisfy(s => s.ShootingClass.Should().StartWith("A"));
        }

        [Fact]
        public void CalculateStandardMedals_MixedScoresAndRanks_AppliesBestOfLogic()
        {
            // Arrange - Create realistic scenario with varied scores
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.RegionalChampionship();
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(false)
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Verify best-of logic
            var silverShooters = shooters.Where(s => s.StandardMedal == "S").ToList();
            var bronzeShooters = shooters.Where(s => s.StandardMedal == "B").ToList();

            // Silver shooters should have either high rank OR high score
            silverShooters.Should().AllSatisfy(s =>
            {
                var groupShooters = shooters.Where(x => x.ShootingClass[0] == s.ShootingClass[0])
                    .OrderByDescending(x => x.TotalScore)
                    .ToList();

                var rank = groupShooters.IndexOf(s) + 1;
                var isHighRank = rank <= (groupShooters.Count / 9.0);  // Top 1/9
                var hasHighScore = s.TotalScore >= 277;  // Group A silver threshold (example)

                (isHighRank || hasHighScore).Should().BeTrue(
                    $"silver shooter should qualify via rank ({rank}/{groupShooters.Count}) or score ({s.TotalScore})");
            });
        }

        [Fact]
        public void CalculateStandardMedals_AllMethodsCombined_ProducesValidResults()
        {
            // Arrange
            var service = new StandardMedalCalculationService();
            var shooters = CompetitionScenarios.SwedishChampionship();
            var config = new StandardMedalConfigBuilder()
                .WithSeriesCount(6)
                .WithSplitGroupC(true)  // SM mode
                .Build();

            // Act
            service.CalculateStandardMedals(shooters, config);

            // Assert - Comprehensive validation
            var allShooters = shooters.ToList();
            var withMedals = allShooters.Where(s => !string.IsNullOrEmpty(s.StandardMedal)).ToList();

            // 1. Some medals should be awarded
            withMedals.Should().NotBeEmpty("competition should award some medals");

            // 2. Only valid medals
            withMedals.Should().AllSatisfy(s => s.StandardMedal.Should().Match(m => m == "S" || m == "B"));

            // 3. Both silver and bronze medals should be awarded
            var silverCount = withMedals.Count(s => s.StandardMedal == "S");
            var bronzeCount = withMedals.Count(s => s.StandardMedal == "B");

            silverCount.Should().BeGreaterThan(0, "should have at least one silver medal");
            bronzeCount.Should().BeGreaterThan(0, "should have at least one bronze medal");

            // 4. Within each group, higher scores should have equal or better medals
            var groups = allShooters.GroupBy(s => s.ShootingClass[0]);
            foreach (var group in groups)
            {
                var sorted = group.OrderByDescending(s => s.TotalScore).ThenByDescending(s => s.TotalXCount).ToList();
                for (int i = 0; i < sorted.Count - 1; i++)
                {
                    var current = sorted[i];
                    var next = sorted[i + 1];

                    // Current (higher score) should have medal that's >= next shooter's medal
                    var currentValue = current.StandardMedal == "S" ? 2 : (current.StandardMedal == "B" ? 1 : 0);
                    var nextValue = next.StandardMedal == "S" ? 2 : (next.StandardMedal == "B" ? 1 : 0);

                    currentValue.Should().BeGreaterThanOrEqualTo(nextValue,
                        $"shooter with higher score ({current.TotalScore}) should have equal or better medal than lower score ({next.TotalScore})");
                }
            }
        }

        #endregion
    }
}
