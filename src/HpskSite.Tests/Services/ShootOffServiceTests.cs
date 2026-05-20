using FluentAssertions;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Tests.TestDataBuilders;
using Newtonsoft.Json;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Unit tests for the pure (non-DB) parts of ShootOffService — tied medal-group
    /// detection and shoot-off override application. The DB read/write methods are
    /// thin NPoco wrappers and are covered by integration tests separately.
    /// </summary>
    public class ShootOffServiceTests
    {
        // ── CompetitionScopeHelper ────────────────────────────────────────────

        [Theory]
        [InlineData("Svenskt Mästerskap", true)]
        [InlineData("Landsdelsmästerskap", true)]
        [InlineData("Kretsmästerskap", true)]
        [InlineData("Klubbmästerskap", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("Något annat", false)]
        public void IsChampionshipScope_RecognizesAllFourMasterskap(string? scope, bool expected)
        {
            CompetitionScopeHelper.IsChampionshipScope(scope).Should().Be(expected);
        }

        // ── Tied medal group detection ───────────────────────────────────────

        [Fact]
        public void DetectTiedMedalGroups_NoTies_ReturnsEmpty()
        {
            var shooters = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithSeries(50, 50, 49).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithSeries(50, 49, 49).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithSeries(49, 49, 49).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();

            var result = ShootOffService.DetectTiedMedalGroups(shooters, "A1");

            result.Should().BeEmpty("each shooter has a unique total");
        }

        [Fact]
        public void DetectTiedMedalGroups_TwoTiedForGold_ReturnsOneGroup()
        {
            // Two shooters tied at 295, one at 290. Gold/Silver tied → group at firstRank 1.
            var shooters = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithName("C").WithSeries(50, 50, 50, 50, 45, 45).Build(),
            };
            var sorted = shooters.OrderByDescending(s => s.TotalScore).ToList();
            var result = ShootOffService.DetectTiedMedalGroups(sorted, "A1");

            result.Should().HaveCount(1);
            result[0].MedalTier.Should().Be("Guld + Silver", "a 2-way tie at rank 1 blocks both gold and silver");
            result[0].FirstRank.Should().Be(1);
            result[0].LastRank.Should().Be(2);
            result[0].Shooters.Should().HaveCount(2);
        }

        [Fact]
        public void DetectTiedMedalGroups_TiedAtRankFourOnly_IsIgnored()
        {
            // 295, 290, 289, 285, 285 — tie at rank 4. Not a medal-tier tie.
            var shooters = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithSeries(50, 50, 50, 50, 50, 45).Build(),  // 295
                new ShooterResultBuilder().WithMemberId(2).WithSeries(50, 50, 50, 50, 50, 40).Build(),  // 290
                new ShooterResultBuilder().WithMemberId(3).WithSeries(50, 50, 50, 50, 49, 40).Build(),  // 289
                new ShooterResultBuilder().WithMemberId(4).WithSeries(50, 50, 50, 50, 45, 40).Build(),  // 285
                new ShooterResultBuilder().WithMemberId(5).WithSeries(50, 50, 50, 50, 45, 40).Build(),  // 285
            };
            var sorted = shooters.OrderByDescending(s => s.TotalScore).ToList();

            var result = ShootOffService.DetectTiedMedalGroups(sorted, "A1");

            result.Should().BeEmpty("rank-4 ties don't trigger shoot-off");
        }

        [Fact]
        public void DetectTiedMedalGroups_XCountDifferent_StillTied()
        {
            // Both shooters total 295 but with different X-counts. The detection must
            // ignore X — the championship rule is total-only.
            var a = new ShooterResultBuilder().WithMemberId(1)
                .WithSeriesAndXCounts(new List<(int score, int xCount)>
                {
                    (50, 5), (50, 3), (50, 2), (50, 1), (50, 0), (45, 0)
                }).Build();
            var b = new ShooterResultBuilder().WithMemberId(2)
                .WithSeriesAndXCounts(new List<(int score, int xCount)>
                {
                    (50, 0), (50, 0), (50, 0), (50, 0), (50, 0), (45, 0)
                }).Build();

            a.TotalScore.Should().Be(b.TotalScore);
            a.TotalXCount.Should().NotBe(b.TotalXCount);

            var sorted = new List<PrecisionShooterResult> { a, b }.OrderByDescending(s => s.TotalScore).ToList();
            var result = ShootOffService.DetectTiedMedalGroups(sorted, "A1");

            result.Should().HaveCount(1);
            result[0].Shooters.Should().HaveCount(2);
        }

        // ── Shoot-off override application ───────────────────────────────────

        [Fact]
        public void ApplyShootOffOverride_Round1Decides_ReordersAndAnnotates()
        {
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();

            var entries = new List<CompetitionShootOffEntry>
            {
                new() { CompetitionId = 1, MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","9","8","7"}) },  // 44
                new() { CompetitionId = 1, MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","X","X","10","10"}) }, // 50
            };
            // Both shooters need ShootingClass set on the result so the lookup matches.
            sorted[0].ShootingClass = "A1";
            sorted[1].ShootingClass = "A1";

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            // B (memberId 2) had 50, wins gold.
            sorted[0].MemberId.Should().Be(2);
            sorted[1].MemberId.Should().Be(1);
            sorted[0].ShootOffScore.Should().Be(50);
            sorted[1].ShootOffScore.Should().Be(44);
            tied[0].Resolved.Should().BeTrue();
        }

        [Fact]
        public void ApplyShootOffOverride_Round1Tied_NeedsRound2()
        {
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            sorted[0].ShootingClass = "A1";
            sorted[1].ShootingClass = "A1";

            // Both score 50 in round 1.
            var entries = new List<CompetitionShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","X","X","10","10"}) },
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","X","X","10","10"}) },
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            tied[0].Resolved.Should().BeFalse("round 1 left both shooters tied");
            tied[0].RoundsCompleted.Should().Be(1);
        }

        [Fact]
        public void ApplyShootOffOverride_NoEntries_LeavesOrderUntouched()
        {
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            sorted[0].ShootingClass = "A1";
            sorted[1].ShootingClass = "A1";

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, Enumerable.Empty<CompetitionShootOffEntry>().ToLookup(e => e.MemberId));

            tied[0].Resolved.Should().BeFalse();
            tied[0].RoundsCompleted.Should().Be(0);
            sorted[0].ShootOffScore.Should().BeNull();
            sorted[1].ShootOffScore.Should().BeNull();
            // Both shooters need to shoot round 1.
            sorted[0].ShootOffNextRound.Should().Be(1);
            sorted[1].ShootOffNextRound.Should().Be(1);
            sorted[0].ShootOffIsResolved.Should().BeFalse();
            sorted[1].ShootOffIsResolved.Should().BeFalse();
        }

        // ── Progressive resolution (per-shooter status) ────────────────────────
        //
        // Real-world flow: all tied shooters shoot together. Whoever is uniquely
        // separated by their round-score keeps their placement and is done. Tied
        // sub-groups continue to subsequent rounds; different medal slots can be
        // decided at different rounds.

        [Fact]
        public void ProgressiveResolution_FourWayTie_Round1SeparatesBottomTwo_TopTwoNeedRound2()
        {
            // All four tied at 295. Round 1: A=49, B=49, C=45, D=42.
            // Expected: C (bronze) and D (4th) are resolved; A and B still tied for gold/silver.
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithName("C").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(4).WithName("D").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            foreach (var s in sorted) s.ShootingClass = "A1";

            var entries = new List<CompetitionShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) },  // 49
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) },  // 49
                new() { MemberId = 3, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","9","8","8"}) },  // 45
                new() { MemberId = 4, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","9","9","8","6"}) },   // 42
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            // Look up status by member id since the list is now lex-sorted.
            PrecisionShooterResult ById(int id) => sorted.First(s => s.MemberId == id);

            ById(3).ShootOffIsResolved.Should().BeTrue("C is uniquely placed at rank 3 after round 1");
            ById(4).ShootOffIsResolved.Should().BeTrue("D is uniquely placed at rank 4 after round 1");
            ById(1).ShootOffIsResolved.Should().BeFalse("A is still tied with B for rank 1/2");
            ById(2).ShootOffIsResolved.Should().BeFalse("B is still tied with A for rank 1/2");

            ById(1).ShootOffNextRound.Should().Be(2, "A must shoot round 2 to break the tie with B");
            ById(2).ShootOffNextRound.Should().Be(2, "B must shoot round 2 to break the tie with A");
            ById(3).ShootOffNextRound.Should().BeNull("C is resolved; no further shooting");
            ById(4).ShootOffNextRound.Should().BeNull("D is resolved; no further shooting");

            tied[0].Resolved.Should().BeFalse("the whole group is not fully resolved while A/B still tied");
        }

        [Fact]
        public void ProgressiveResolution_FourWayTie_Round2OnlyAandB_FullyResolved()
        {
            // Continuation: after round 1, only A and B shoot round 2. A wins 49–46.
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithName("C").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(4).WithName("D").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            foreach (var s in sorted) s.ShootingClass = "A1";

            var entries = new List<CompetitionShootOffEntry>
            {
                // Round 1
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) },
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) },
                new() { MemberId = 3, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","9","8","8"}) },
                new() { MemberId = 4, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","9","9","8","6"}) },
                // Round 2 — ONLY A and B (C and D are already resolved and don't shoot again)
                new() { MemberId = 1, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) }, // 49
                new() { MemberId = 2, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","9","9","8"}) },  // 46
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            PrecisionShooterResult ById(int id) => sorted.First(s => s.MemberId == id);

            ById(1).ShootOffIsResolved.Should().BeTrue();
            ById(2).ShootOffIsResolved.Should().BeTrue();
            ById(3).ShootOffIsResolved.Should().BeTrue();
            ById(4).ShootOffIsResolved.Should().BeTrue();
            tied[0].Resolved.Should().BeTrue();
            sorted.Select(s => s.MemberId).Should().Equal(new[] { 1, 2, 3, 4 }, "A→B→C→D after all rounds");
        }

        [Fact]
        public void ProgressiveResolution_TiedShooterWhoHasShotCurrentRound_IsWaitingNotPrompted()
        {
            // 4 tied. After round 1, A=49 and B=49 are mid-resolution: A has shot round 2
            // (48), but B hasn't yet. A must NOT be prompted for round 2 again — A is
            // waiting on B.
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithName("C").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(4).WithName("D").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            foreach (var s in sorted) s.ShootingClass = "A1";

            var entries = new List<CompetitionShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) }, // 49
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) }, // 49
                new() { MemberId = 3, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","9","8","8"}) }, // 45
                new() { MemberId = 4, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","9","9","8","6"}) },  // 42
                // A has shot round 2 but B has not yet.
                new() { MemberId = 1, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","10","9","9"}) }, // 48
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            PrecisionShooterResult ById(int id) => sorted.First(s => s.MemberId == id);

            ById(1).ShootOffIsResolved.Should().BeFalse("A is still tied with B until B shoots round 2");
            ById(1).ShootOffNextRound.Should().BeNull("A has already shot round 2 — A is waiting, not prompted again");
            ById(2).ShootOffIsResolved.Should().BeFalse();
            ById(2).ShootOffNextRound.Should().Be(2, "B still has round 2 to shoot");
        }

        [Fact]
        public void ProgressiveResolution_FiveWayTieAtRank1_OnlyTopThreeAreMedalSlots()
        {
            // 5 shooters all tied at 295 — overlaps Guld/Silver/Brons. Rank 4 and 5 also
            // contested within the same group but they're not medal slots.
            // After round 1: A=49, B=49, C=49, D=42, E=40.
            // Expected: D and E resolved (4th and 5th). A, B, C still tied for 1/2/3.
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(4).WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(5).WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            foreach (var s in sorted) s.ShootingClass = "A1";

            var entries = new List<CompetitionShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) }, // 49
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) }, // 49
                new() { MemberId = 3, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) }, // 49
                new() { MemberId = 4, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","9","9","8","6"}) },   // 42
                new() { MemberId = 5, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"9","9","8","8","6"}) },    // 40
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            PrecisionShooterResult ById(int id) => sorted.First(s => s.MemberId == id);

            ById(4).ShootOffIsResolved.Should().BeTrue();
            ById(5).ShootOffIsResolved.Should().BeTrue();
            ById(1).ShootOffNextRound.Should().Be(2);
            ById(2).ShootOffNextRound.Should().Be(2);
            ById(3).ShootOffNextRound.Should().Be(2);
        }

        [Fact]
        public void ApplyShootOffOverride_Round2BreaksTie()
        {
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            sorted[0].ShootingClass = "A1";
            sorted[1].ShootingClass = "A1";

            var entries = new List<CompetitionShootOffEntry>
            {
                // Round 1 — both 50, still tied
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","X","X","10","10"}) },
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","X","X","10","10"}) },
                // Round 2 — A wins 48 to 45
                new() { MemberId = 1, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","10","10","8"}) },
                new() { MemberId = 2, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","9","9","9","8"}) },
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            sorted[0].MemberId.Should().Be(1, "A's round-2 total of 48 beat B's 45");
            sorted[1].MemberId.Should().Be(2);
            tied[0].Resolved.Should().BeTrue();
            tied[0].RoundsCompleted.Should().Be(2);
            sorted[0].ShootOffRound.Should().Be(2);
        }

        [Fact]
        public void DetectTiedMedalGroups_FourWayTieAtGold_LabelCoversAllThreeMedals()
        {
            // 295, 295, 295, 295, 290 — four shooters tied for gold.
            // All three medal positions are pending; the label must reflect that.
            var shooters = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithSeries(50, 50, 50, 50, 50, 45).Build(), // 295
                new ShooterResultBuilder().WithMemberId(2).WithSeries(50, 50, 50, 50, 50, 45).Build(), // 295
                new ShooterResultBuilder().WithMemberId(3).WithSeries(50, 50, 50, 50, 50, 45).Build(), // 295
                new ShooterResultBuilder().WithMemberId(4).WithSeries(50, 50, 50, 50, 50, 45).Build(), // 295
                new ShooterResultBuilder().WithMemberId(5).WithSeries(50, 50, 50, 50, 50, 40).Build(), // 290
            };
            var sorted = shooters.OrderByDescending(s => s.TotalScore).ToList();

            var result = ShootOffService.DetectTiedMedalGroups(sorted, "A1");

            result.Should().HaveCount(1);
            result[0].FirstRank.Should().Be(1);
            result[0].LastRank.Should().Be(4);
            result[0].MedalTier.Should().Be("Guld + Silver + Brons");
            // Medal positions covered = min(4,3) - 1 + 1 = 3 — three medals pending.
            (Math.Min(result[0].LastRank, 3) - result[0].FirstRank + 1).Should().Be(3);
        }

        [Fact]
        public void MedalNounsForRange_FormatsSwedishDefiniteList()
        {
            ShootOffService.MedalNounsForRange(1, 1).Should().Be("guldet");
            ShootOffService.MedalNounsForRange(1, 2).Should().Be("guldet och silvret");
            ShootOffService.MedalNounsForRange(1, 3).Should().Be("guldet, silvret och bronset");
            ShootOffService.MedalNounsForRange(1, 4).Should().Be("guldet, silvret och bronset", "rank 4 is not a medal slot");
            ShootOffService.MedalNounsForRange(2, 3).Should().Be("silvret och bronset");
            ShootOffService.MedalNounsForRange(3, 3).Should().Be("bronset");
        }

        [Fact]
        public void ApplyShootOffOverride_PartialResolution_StillRespectsRound1Separation()
        {
            // Four shooters tied at 295. Round 1: A=49, B=47, C=45, D=45 → C and D need round 2.
            // Admin enters round 2 only for C and D. Resolved logic must use lex comparison —
            // A and B were separated in round 1, so their tied round-2 (both 0) doesn't unresolve them.
            var sorted = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithName("A").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(2).WithName("B").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(3).WithName("C").WithSeries(50, 50, 50, 50, 50, 45).Build(),
                new ShooterResultBuilder().WithMemberId(4).WithName("D").WithSeries(50, 50, 50, 50, 50, 45).Build(),
            }.OrderByDescending(s => s.TotalScore).ToList();
            foreach (var s in sorted) s.ShootingClass = "A1";

            var entries = new List<CompetitionShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","10","9"}) },  // 49
                new() { MemberId = 2, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","9","8"}) },  // 47
                new() { MemberId = 3, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","9","8","8"}) },  // 45
                new() { MemberId = 4, ShootingClass = "A1", Round = 1, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","10","9","8","8"}) },  // 45
                // Round 2 — only C and D shoot (they're the remaining tied pair)
                new() { MemberId = 3, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"X","10","10","9","9"}) }, // 48
                new() { MemberId = 4, ShootingClass = "A1", Round = 2, SeriesNumber = 1, Shots = JsonConvert.SerializeObject(new[] {"10","9","9","8","8"}) },  // 44
            };

            var tied = ShootOffService.DetectTiedMedalGroups(sorted, "A1");
            ShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId));

            sorted.Select(s => s.MemberId).Should().Equal(1, 2, 3, 4);
            tied[0].Resolved.Should().BeTrue("A>B in round 1, B>C in round 1, C>D in round 2 — full lex order");
        }

        [Fact]
        public void DetectTiedMedalGroups_TripleTieAtSilverAndBronze_OneGroup()
        {
            // 295, 290, 290, 290 — silver/bronze/4th tied at 290.
            // FirstRank 2, LastRank 4: medal slots 2..3 are blocked (silver+bronze).
            // Rank 4 isn't a medal so the label trims to "Silver + Brons".
            var shooters = new List<PrecisionShooterResult>
            {
                new ShooterResultBuilder().WithMemberId(1).WithSeries(50, 50, 50, 50, 50, 45).Build(),  // 295
                new ShooterResultBuilder().WithMemberId(2).WithSeries(50, 50, 50, 50, 50, 40).Build(),  // 290
                new ShooterResultBuilder().WithMemberId(3).WithSeries(50, 50, 50, 50, 50, 40).Build(),  // 290
                new ShooterResultBuilder().WithMemberId(4).WithSeries(50, 50, 50, 50, 50, 40).Build(),  // 290
            };
            var sorted = shooters.OrderByDescending(s => s.TotalScore).ToList();

            var result = ShootOffService.DetectTiedMedalGroups(sorted, "A1");

            result.Should().HaveCount(1);
            result[0].MedalTier.Should().Be("Silver + Brons");
            result[0].FirstRank.Should().Be(2);
            result[0].LastRank.Should().Be(4);
            result[0].Shooters.Should().HaveCount(3);
        }
    }
}
