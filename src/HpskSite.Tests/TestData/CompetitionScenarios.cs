using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Tests.TestDataBuilders;
using System.Collections.Generic;

namespace HpskSite.Tests.TestData
{
    /// <summary>
    /// Predefined realistic test datasets for competition scenarios
    /// </summary>
    public static class CompetitionScenarios
    {
        /// <summary>
        /// Small club competition with mixed weapon groups
        /// 12 shooters, 6 series, no Group C splitting
        /// </summary>
        public static List<PrecisionShooterResult> SmallClubCompetition()
        {
            var builder = new ShooterResultBuilder();
            return new List<PrecisionShooterResult>
            {
                // Group A shooters (4 shooters)
                builder.Reset().WithMemberId(1).WithName("Alice Andersson").WithShootingClass("A1").WithTotalScore(285, 6).Build(),
                builder.Reset().WithMemberId(2).WithName("Anders Bengtsson").WithShootingClass("A2").WithTotalScore(278, 6).Build(),
                builder.Reset().WithMemberId(3).WithName("Anna Carlsson").WithShootingClass("A3").WithTotalScore(272, 6).Build(),
                builder.Reset().WithMemberId(4).WithName("Adam Davidsson").WithShootingClass("A1").WithTotalScore(265, 6).Build(),

                // Group B shooters (4 shooters)
                builder.Reset().WithMemberId(5).WithName("Bengt Eriksson").WithShootingClass("B1").WithTotalScore(286, 6).Build(),
                builder.Reset().WithMemberId(6).WithName("Birgit Forsberg").WithShootingClass("B2").WithTotalScore(280, 6).Build(),
                builder.Reset().WithMemberId(7).WithName("Björn Gustafsson").WithShootingClass("B3").WithTotalScore(275, 6).Build(),
                builder.Reset().WithMemberId(8).WithName("Britt Holmberg").WithShootingClass("B1").WithTotalScore(270, 6).Build(),

                // Group C shooters (4 shooters)
                builder.Reset().WithMemberId(9).WithName("Carl Isaksson").WithShootingClass("C1").WithTotalScore(287, 6).Build(),
                builder.Reset().WithMemberId(10).WithName("Cecilia Johansson").WithShootingClass("C2").WithTotalScore(281, 6).Build(),
                builder.Reset().WithMemberId(11).WithName("Christoffer Karlsson").WithShootingClass("C3").WithTotalScore(278, 6).Build(),
                builder.Reset().WithMemberId(12).WithName("Christina Larsson").WithShootingClass("C1").WithTotalScore(274, 6).Build()
            };
        }

        /// <summary>
        /// Regional championship (Kretsmästerskap) with larger field
        /// 30 shooters, 6 series, no Group C splitting
        /// </summary>
        public static List<PrecisionShooterResult> RegionalChampionship()
        {
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // Group A (10 shooters) - scores from 290 down to 245
            for (int i = 0; i < 10; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(100 + i)
                    .WithName($"A-Shooter {i + 1}")
                    .WithShootingClass("A1")
                    .WithTotalScore(290 - (i * 5), 6)
                    .Build());
            }

            // Group B (10 shooters) - scores from 292 down to 247
            for (int i = 0; i < 10; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(200 + i)
                    .WithName($"B-Shooter {i + 1}")
                    .WithShootingClass("B1")
                    .WithTotalScore(292 - (i * 5), 6)
                    .Build());
            }

            // Group C (10 shooters) - scores from 293 down to 248
            for (int i = 0; i < 10; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(300 + i)
                    .WithName($"C-Shooter {i + 1}")
                    .WithShootingClass("C1")
                    .WithTotalScore(293 - (i * 5), 6)
                    .Build());
            }

            return shooters;
        }

        /// <summary>
        /// Swedish Championship (SM) with Group C splitting by classification
        /// 50 shooters, 6 series, Group C SPLIT
        /// </summary>
        public static List<PrecisionShooterResult> SwedishChampionship()
        {
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // Group A (15 shooters)
            for (int i = 0; i < 15; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(1000 + i)
                    .WithName($"SM-A-Shooter {i + 1}")
                    .WithShootingClass("A1")
                    .WithTotalScore(293 - (i * 3), 6)
                    .Build());
            }

            // Group B (15 shooters)
            for (int i = 0; i < 15; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(2000 + i)
                    .WithName($"SM-B-Shooter {i + 1}")
                    .WithShootingClass("B1")
                    .WithTotalScore(295 - (i * 3), 6)
                    .Build());
            }

            // Group C (20 shooters) - mixed classifications for splitting
            var cClasses = new[] { "C1Dam", "C1Jun", "C1VetY", "C1VetÄ", "C1Open" };
            for (int i = 0; i < 20; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(3000 + i)
                    .WithName($"SM-C-Shooter {i + 1}")
                    .WithShootingClass(cClasses[i % 5])
                    .WithTotalScore(296 - (i * 2), 6)
                    .Build());
            }

            return shooters;
        }

        /// <summary>
        /// Finals competition with 10 series (6 qualification + 4 finals)
        /// 20 shooters, mixed weapon groups
        /// </summary>
        public static List<PrecisionShooterResult> FinalsCompetition()
        {
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            // Create shooters with 10 series each
            var classes = new[] { "A1", "A2", "B1", "B2", "C1", "C2" };
            for (int i = 0; i < 20; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(4000 + i)
                    .WithName($"Finals-Shooter {i + 1}")
                    .WithShootingClass(classes[i % 6])
                    .WithTotalScore(475 - (i * 5), 10)  // 10 series
                    .Build());
            }

            return shooters;
        }

        /// <summary>
        /// Large dataset for performance testing
        /// 100 shooters, 6 series, mixed weapon groups
        /// </summary>
        public static List<PrecisionShooterResult> LargeDataset()
        {
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();
            var weaponGroups = new[] { "A", "B", "C", "R", "P" };

            for (int i = 0; i < 100; i++)
            {
                var weaponGroup = weaponGroups[i % 5];
                shooters.Add(builder.Reset()
                    .WithMemberId(5000 + i)
                    .WithName($"Large-Shooter {i + 1}")
                    .WithShootingClass($"{weaponGroup}{(i % 3) + 1}")
                    .WithTotalScore(295 - i, 6)
                    .Build());
            }

            return shooters;
        }

        /// <summary>
        /// Edge case: Single shooter per weapon group
        /// 3 shooters total, 6 series
        /// </summary>
        public static List<PrecisionShooterResult> SingleShooterPerGroup()
        {
            var builder = new ShooterResultBuilder();
            return new List<PrecisionShooterResult>
            {
                builder.Reset().WithMemberId(6001).WithName("Solo A").WithShootingClass("A1").WithTotalScore(285, 6).Build(),
                builder.Reset().WithMemberId(6002).WithName("Solo B").WithShootingClass("B1").WithTotalScore(286, 6).Build(),
                builder.Reset().WithMemberId(6003).WithName("Solo C").WithShootingClass("C1").WithTotalScore(287, 6).Build()
            };
        }

        /// <summary>
        /// Edge case: All shooters in one weapon group
        /// 15 shooters, all in Group A, 6 series
        /// </summary>
        public static List<PrecisionShooterResult> SingleWeaponGroup()
        {
            var builder = new ShooterResultBuilder();
            var shooters = new List<PrecisionShooterResult>();

            for (int i = 0; i < 15; i++)
            {
                shooters.Add(builder.Reset()
                    .WithMemberId(7000 + i)
                    .WithName($"GroupA-Only {i + 1}")
                    .WithShootingClass("A1")
                    .WithTotalScore(290 - (i * 3), 6)
                    .Build());
            }

            return shooters;
        }
    }
}
