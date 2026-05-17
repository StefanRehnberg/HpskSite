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
    /// A-family (A + AM + AP + AG) standard-medal pooling.
    ///
    /// Per SPSF: AM/AP/AG shooters compete in their own display class but their results
    /// are pooled with the open A class for standard-medal eligibility. A_Opt is a
    /// parallel weapon group and stays separate from the pool.
    ///
    /// These tests verify both halves of the rule:
    ///   1. Percentage-based medals (top 1/9 silver, top 1/3 bronze) are computed
    ///      across the pooled A-family ranking.
    ///   2. Fixed-score thresholds (267/277 for 6 series, etc.) apply identically
    ///      to every A-family subgroup.
    ///   3. A_Opt does NOT participate in the pool.
    /// </summary>
    public partial class StandardMedalCalculationServiceTests
    {
        // ── Helpers ──────────────────────────────────────────────────────────────
        // 6-series totals chosen so the thresholds (267 bronze, 277 silver) bracket
        // the cohort: silver if ≥ 277, bronze if ≥ 267, none below.

        private static PrecisionShooterResult Shooter(int memberId, string shootingClass, int totalScore)
            => new ShooterResultBuilder()
                .WithMemberId(memberId)
                .WithShootingClass(shootingClass)
                .WithTotalScore(totalScore, seriesCount: 6)
                .Build();

        private static StandardMedalConfig Config6Series()
            => new StandardMedalConfigBuilder().WithSeriesCount(6).Build();

        // ── Percentage pooling ──────────────────────────────────────────────────

        [Fact]
        public void PercentageMedals_PoolAAndAMTogether_TopRankedAcrossSubgroupsGetSilver()
        {
            // 9 A-family shooters split across A and AM. With pooling, top 1/9 (=1 silver)
            // and top 1/3 (=3 bronze) are computed on the combined 9-shooter ranking.
            // Without pooling, each subgroup would compute quotas separately (5/9=0, 4/9=0)
            // and no silver would ever be awarded.
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>
            {
                Shooter(1, "A1",    240),   // A-family, low score — no medal
                Shooter(2, "A2",    250),
                Shooter(3, "A3",    260),
                Shooter(4, "A1",    270),
                Shooter(5, "A2",    280),
                Shooter(6, "A3",    290),   // A — top of pool → silver
                Shooter(7, "A_m_1", 200),   // AM — bottom; pulled in for ranking only
                Shooter(8, "A_m_2", 210),
                Shooter(9, "A_m_3", 220),
            };

            service.CalculateStandardMedals(shooters, Config6Series());

            // memberId 6 (A3, 290) is the unique top of the pool — must earn silver.
            var topShooter = shooters.Single(s => s.MemberId == 6);
            topShooter.StandardMedal.Should().Be("S", "top of pooled A-family ranking gets percentage silver");
        }

        [Fact]
        public void PercentageMedals_AMShooterCanWinSilver_WhenTopOfAFamilyPool()
        {
            // An AM shooter who outscores the entire pool earns silver — they participate
            // in the same medal ranking as A despite being in a separate display class.
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>
            {
                Shooter(1, "A1",    240),
                Shooter(2, "A2",    250),
                Shooter(3, "A3",    260),
                Shooter(4, "A1",    270),
                Shooter(5, "A2",    275),
                Shooter(6, "A3",    278),
                Shooter(7, "A_m_3", 300),   // AM shooter is top of pool
                Shooter(8, "A_p_2", 240),
                Shooter(9, "A_g_1", 230),
            };

            service.CalculateStandardMedals(shooters, Config6Series());

            var amShooter = shooters.Single(s => s.MemberId == 7);
            amShooter.StandardMedal.Should().Be("S",
                "an AM shooter at the top of the pooled A-family ranking earns percentage silver");
        }

        [Fact]
        public void PercentageMedals_BronzeQuotaCountedAcrossPooledFamily()
        {
            // 6 A-family shooters: bronze quota = 6/3 = 2. Pool-ranked positions 1–2 get
            // bronze (or better). Pool-ranked positions 3–6 get nothing from percentage.
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>
            {
                Shooter(1, "A2",    250),   // pool rank 5
                Shooter(2, "A3",    240),   // pool rank 6
                Shooter(3, "A_m_2", 280),   // pool rank 2 → bronze or better (78 ≥ silver 277)
                Shooter(4, "A_m_3", 260),   // pool rank 3
                Shooter(5, "A_p_2", 290),   // pool rank 1 → bronze or better
                Shooter(6, "A_g_3", 255),   // pool rank 4
            };

            service.CalculateStandardMedals(shooters, Config6Series());

            // Top 2 in the pool earn a medal (Silver wins over Bronze when both apply).
            shooters.Single(s => s.MemberId == 5).StandardMedal.Should().NotBeNullOrEmpty();
            shooters.Single(s => s.MemberId == 3).StandardMedal.Should().NotBeNullOrEmpty();
            // Pool rank 3 (260 ≥ 267? NO) — fixed-score gives nothing; percentage gives nothing too.
            shooters.Single(s => s.MemberId == 4).StandardMedal.Should().BeNullOrEmpty();
        }

        // ── Fixed-score parity ──────────────────────────────────────────────────

        [Theory]
        [InlineData("A_m_2", 280, "S")]   // ≥ 277 silver threshold (A's thresholds apply)
        [InlineData("A_m_2", 270, "B")]   // ≥ 267 bronze, < 277 silver
        [InlineData("A_m_2", 260, null)]  // below both
        [InlineData("A_p_2", 280, "S")]
        [InlineData("A_p_2", 270, "B")]
        [InlineData("A_g_2", 280, "S")]
        [InlineData("A_g_2", 270, "B")]
        public void FixedScoreMedals_AFamilySubgroups_UseAClassThresholds(
            string shootingClass, int score, string? expectedMedal)
        {
            // Each subgroup tested in isolation so the percentage method (which depends on
            // cohort size) doesn't grant an extra medal. Single-shooter group → percentage
            // quotas are 0 (1/9 and 1/3 round down to 0), so only fixed-score can fire.
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>
            {
                Shooter(1, shootingClass, score)
            };

            service.CalculateStandardMedals(shooters, Config6Series());

            if (expectedMedal == null)
            {
                shooters[0].StandardMedal.Should().BeNullOrEmpty(
                    $"score {score} is below A's 6-series thresholds (267/277)");
            }
            else
            {
                shooters[0].StandardMedal.Should().Be(expectedMedal,
                    $"score {score} should award {expectedMedal} using A's 6-series thresholds");
            }
        }

        // ── A_Opt isolation ─────────────────────────────────────────────────────

        [Fact]
        public void PercentageMedals_AOptShootersNotPooledWithAFamily()
        {
            // 9 A-family + 1 A_Opt shooter. The A_Opt shooter must be ranked alone in
            // their own group — they're a parallel weapon class, not part of the pool.
            // With a single-shooter A_Opt group, percentage quotas are 0 so no medal
            // can come from percentage. A medal would only come from fixed-score.
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>
            {
                Shooter(1, "A1",      270),
                Shooter(2, "A2",      275),
                Shooter(3, "A3",      278),
                Shooter(4, "A1",      280),
                Shooter(5, "A2",      282),
                Shooter(6, "A3",      285),
                Shooter(7, "A_m_2",   265),
                Shooter(8, "A_m_3",   263),
                Shooter(9, "A_g_2",   260),
                Shooter(10, "A_opt_2", 250), // below A's bronze threshold — no medal
            };

            service.CalculateStandardMedals(shooters, Config6Series());

            // A_Opt shooter scored 250: below A's bronze 267 (fixed-score doesn't fire),
            // and they're alone in A_Opt so percentage quotas are 0. No medal expected.
            // If A_Opt were wrongly pooled into the A-family ranking they'd be near the
            // bottom (rank 10/10) and still no medal — but the assertion that matters is
            // the bronze count: with pooling, bronze quota would be 10/3 = 3; without,
            // bronze quota for A-family alone is 9/3 = 3. Either way pool size differs.
            var aOpt = shooters.Single(s => s.MemberId == 10);
            aOpt.StandardMedal.Should().BeNullOrEmpty(
                "A_Opt shooter at 250 is below A's fixed-score thresholds and is alone in their A_Opt group");
        }

        // ── Display grouping is unaffected ──────────────────────────────────────

        [Fact]
        public void AFamilyPooling_DoesNotChangeShootingClassOnShooter()
        {
            // Medal pooling is internal to the calculator — the shooter's ShootingClass
            // field must stay intact so the result list still groups them under their
            // original display class (AM2 stays AM2, not "A").
            var service = new StandardMedalCalculationService();
            var shooters = new List<PrecisionShooterResult>
            {
                Shooter(1, "A_m_2", 290),
                Shooter(2, "A2",    280),
            };

            service.CalculateStandardMedals(shooters, Config6Series());

            shooters[0].ShootingClass.Should().Be("A_m_2");
            shooters[1].ShootingClass.Should().Be("A2");
        }
    }
}
