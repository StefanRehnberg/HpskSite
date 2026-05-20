using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Unit tests for the pure (non-DB) parts of <see cref="FaltskytteShootOffService"/>
    /// — tied-medal detection, the three per-variation round comparers, and the
    /// progressive-resolution post-process. DB IO is covered separately.
    /// </summary>
    public class FaltskytteShootOffServiceTests
    {
        // ── Comparer factory ─────────────────────────────────────────────

        [Theory]
        [InlineData("Faltskytte", "Normal", typeof(NormalRoundComparer))]
        [InlineData("Faltskytte", "Poang", typeof(PoangRoundComparer))]
        [InlineData("MagnumFalt", "Normal", typeof(MagnumRoundComparer))]
        [InlineData("MagnumFalt", "Poang", typeof(MagnumRoundComparer))]
        [InlineData("Faltskytte", null, typeof(NormalRoundComparer))]
        public void ComparerFor_SelectsCorrectStrategy(string competitionType, string? scoringMode, System.Type expected)
        {
            var comparer = FaltskytteShootOffService.ComparerFor(competitionType, scoringMode);
            comparer.Should().BeOfType(expected);
        }

        // ── Normal comparer ──────────────────────────────────────────────

        [Fact]
        public void NormalRoundComparer_HigherHitsWins()
        {
            var c = new NormalRoundComparer();
            var a = new FaltskytteShootOffEntry { Hits = 5, Figures = 4, TiebreakerScore = 18 };
            var b = new FaltskytteShootOffEntry { Hits = 4, Figures = 4, TiebreakerScore = 18 };
            c.Compare(a, b).Should().BeLessThan(0); // a beats b (lower = earlier in sort)
        }

        [Fact]
        public void NormalRoundComparer_TiedHits_FiguresDecide()
        {
            var c = new NormalRoundComparer();
            var a = new FaltskytteShootOffEntry { Hits = 5, Figures = 4, TiebreakerScore = 10 };
            var b = new FaltskytteShootOffEntry { Hits = 5, Figures = 3, TiebreakerScore = 18 };
            c.Compare(a, b).Should().BeLessThan(0);
        }

        [Fact]
        public void NormalRoundComparer_TiedHitsAndFigures_PoangmalDecides()
        {
            var c = new NormalRoundComparer();
            var a = new FaltskytteShootOffEntry { Hits = 5, Figures = 4, TiebreakerScore = 12 };
            var b = new FaltskytteShootOffEntry { Hits = 5, Figures = 4, TiebreakerScore = 18 };
            c.Compare(a, b).Should().BeGreaterThan(0); // b wins
        }

        [Fact]
        public void NormalRoundComparer_FormatRound_HitsSlashFigures()
        {
            new NormalRoundComparer().FormatRound(new FaltskytteShootOffEntry { Hits = 5, Figures = 4 })
                .Should().Be("5/4");
        }

        // ── Poäng comparer ───────────────────────────────────────────────

        [Fact]
        public void PoangRoundComparer_HigherPointsWins()
        {
            var c = new PoangRoundComparer();
            var a = new FaltskytteShootOffEntry { Hits = 6, Figures = 4, TiebreakerScore = 0 };  // 10p
            var b = new FaltskytteShootOffEntry { Hits = 5, Figures = 4, TiebreakerScore = 18 }; // 9p
            c.Compare(a, b).Should().BeLessThan(0);
        }

        [Fact]
        public void PoangRoundComparer_TiedPoints_PoangmalDecides()
        {
            var c = new PoangRoundComparer();
            var a = new FaltskytteShootOffEntry { Hits = 5, Figures = 4, TiebreakerScore = 10 };
            var b = new FaltskytteShootOffEntry { Hits = 6, Figures = 3, TiebreakerScore = 18 }; // both 9p
            c.Compare(a, b).Should().BeGreaterThan(0);
        }

        [Fact]
        public void PoangRoundComparer_FormatRound_PointsWithP()
        {
            new PoangRoundComparer().FormatRound(new FaltskytteShootOffEntry { Hits = 6, Figures = 4 })
                .Should().Be("10p");
        }

        // ── Magnum comparer ──────────────────────────────────────────────

        [Fact]
        public void MagnumRoundComparer_HigherTiebreakerWins()
        {
            var c = new MagnumRoundComparer();
            var a = new FaltskytteShootOffEntry { TiebreakerScore = 23 };
            var b = new FaltskytteShootOffEntry { TiebreakerScore = 19 };
            c.Compare(a, b).Should().BeLessThan(0);
        }

        [Fact]
        public void MagnumRoundComparer_FormatRound_PointsWithP()
        {
            new MagnumRoundComparer().FormatRound(new FaltskytteShootOffEntry { TiebreakerScore = 23 })
                .Should().Be("23p");
        }

        [Fact]
        public void MagnumRoundComparer_IgnoresHitsAndFigures()
        {
            var c = new MagnumRoundComparer();
            var a = new FaltskytteShootOffEntry { Hits = 10, Figures = 10, TiebreakerScore = 15 };
            var b = new FaltskytteShootOffEntry { Hits = 0, Figures = 0, TiebreakerScore = 20 };
            c.Compare(a, b).Should().BeGreaterThan(0); // b wins on points alone
        }

        // ── Tied-medal detection ─────────────────────────────────────────

        [Fact]
        public void DetectTiedMedalGroups_NoTies_ReturnsEmpty()
        {
            var shooters = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, TotalHits = 49, TotalFigures = 40, TotalPoints = 89 },
                new() { MemberId = 3, TotalHits = 48, TotalFigures = 40, TotalPoints = 88 },
            };
            FaltskytteShootOffService.DetectTiedMedalGroups(shooters, "Normal", "Faltskytte")
                .Should().BeEmpty();
        }

        [Fact]
        public void DetectTiedMedalGroups_TwoTiedForGold_Normal()
        {
            var shooters = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, Name = "A", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, Name = "B", TotalHits = 50, TotalFigures = 38, TotalPoints = 88 },
                new() { MemberId = 3, Name = "C", TotalHits = 48, TotalFigures = 38, TotalPoints = 86 },
            };
            var result = FaltskytteShootOffService.DetectTiedMedalGroups(shooters, "Normal", "Faltskytte");

            result.Should().HaveCount(1);
            result[0].FirstRank.Should().Be(1);
            result[0].LastRank.Should().Be(2);
            result[0].MedalTier.Should().Be("Guld + Silver");
            result[0].TiedScore.Should().Be(50);
        }

        [Fact]
        public void DetectTiedMedalGroups_PoangMode_TriggersOnPoints()
        {
            // Same total hits but different points → in Poäng mode these are NOT tied
            // (10p vs 9p). And vice-versa.
            var shooters = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, TotalHits = 49, TotalFigures = 41, TotalPoints = 90 },
                new() { MemberId = 3, TotalHits = 48, TotalFigures = 40, TotalPoints = 88 },
            };
            var result = FaltskytteShootOffService.DetectTiedMedalGroups(shooters, "Poang", "Faltskytte");
            result.Should().HaveCount(1, "shooters 1 and 2 tie on points = 90 in Poäng mode");
            result[0].FirstRank.Should().Be(1);
            result[0].LastRank.Should().Be(2);
            result[0].TiedScore.Should().Be(90);
        }

        [Fact]
        public void DetectTiedMedalGroups_MagnumUsesPoints()
        {
            // Magnum looks at TotalPoints just like Poäng mode.
            var shooters = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, TotalPoints = 23 },
                new() { MemberId = 2, TotalPoints = 23 },
                new() { MemberId = 3, TotalPoints = 19 },
            };
            var result = FaltskytteShootOffService.DetectTiedMedalGroups(shooters, "", "MagnumFalt");
            result.Should().HaveCount(1);
        }

        [Fact]
        public void DetectTiedMedalGroups_TiedAtRankFour_IsIgnored()
        {
            var shooters = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, TotalHits = 49, TotalFigures = 40, TotalPoints = 89 },
                new() { MemberId = 3, TotalHits = 48, TotalFigures = 40, TotalPoints = 88 },
                new() { MemberId = 4, TotalHits = 45, TotalFigures = 38, TotalPoints = 83 },
                new() { MemberId = 5, TotalHits = 45, TotalFigures = 38, TotalPoints = 83 },
            };
            FaltskytteShootOffService.DetectTiedMedalGroups(shooters, "Normal", "Faltskytte")
                .Should().BeEmpty("rank-4 ties don't trigger shoot-off");
        }

        // ── Progressive resolution ──────────────────────────────────────

        [Fact]
        public void ApplyShootOffOverride_FourWayTie_Round1SeparatesBottomTwo()
        {
            // Four tied at hits=50/figures=40. Round 1: A=5/4, B=5/4, C=4/3, D=3/2.
            // Expected: C and D resolved (ranks 3 & 4); A and B still tied → need round 2.
            var sorted = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, Name = "A", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, Name = "B", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 3, Name = "C", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 4, Name = "D", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
            };

            var entries = new List<FaltskytteShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "C2", Round = 1, Hits = 5, Figures = 4 },
                new() { MemberId = 2, ShootingClass = "C2", Round = 1, Hits = 5, Figures = 4 },
                new() { MemberId = 3, ShootingClass = "C2", Round = 1, Hits = 4, Figures = 3 },
                new() { MemberId = 4, ShootingClass = "C2", Round = 1, Hits = 3, Figures = 2 },
            };

            var tied = FaltskytteShootOffService.DetectTiedMedalGroups(sorted, "Normal", "Faltskytte");
            FaltskytteShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId), new NormalRoundComparer());

            FaltskytteShooterResult ById(int id) => sorted.First(s => s.MemberId == id);
            ById(3).ShootOffIsResolved.Should().BeTrue();
            ById(4).ShootOffIsResolved.Should().BeTrue();
            ById(1).ShootOffIsResolved.Should().BeFalse();
            ById(2).ShootOffIsResolved.Should().BeFalse();
            ById(1).ShootOffNextRound.Should().Be(2);
            ById(2).ShootOffNextRound.Should().Be(2);
            tied[0].Resolved.Should().BeFalse();
        }

        [Fact]
        public void ApplyShootOffOverride_AfterRound2_FullyResolved()
        {
            var sorted = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, Name = "A", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, Name = "B", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
            };
            var entries = new List<FaltskytteShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "C2", Round = 1, Hits = 5, Figures = 4 },
                new() { MemberId = 2, ShootingClass = "C2", Round = 1, Hits = 5, Figures = 4 },
                new() { MemberId = 1, ShootingClass = "C2", Round = 2, Hits = 6, Figures = 4 },
                new() { MemberId = 2, ShootingClass = "C2", Round = 2, Hits = 4, Figures = 3 },
            };

            var tied = FaltskytteShootOffService.DetectTiedMedalGroups(sorted, "Normal", "Faltskytte");
            FaltskytteShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId), new NormalRoundComparer());

            sorted[0].MemberId.Should().Be(1, "A wins round 2 6/4 vs B's 4/3");
            sorted[1].MemberId.Should().Be(2);
            tied[0].Resolved.Should().BeTrue();
        }

        [Fact]
        public void ApplyShootOffOverride_NoEntries_StillTied()
        {
            var sorted = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
                new() { MemberId = 2, ShootingClass = "C2", TotalHits = 50, TotalFigures = 40, TotalPoints = 90 },
            };
            var tied = FaltskytteShootOffService.DetectTiedMedalGroups(sorted, "Normal", "Faltskytte");
            FaltskytteShootOffService.ApplyShootOffOverride(sorted, tied, Enumerable.Empty<FaltskytteShootOffEntry>().ToLookup(e => e.MemberId), new NormalRoundComparer());
            tied[0].Resolved.Should().BeFalse();
            sorted[0].ShootOffNextRound.Should().Be(1);
            sorted[1].ShootOffNextRound.Should().Be(1);
        }

        [Fact]
        public void ApplyShootOffOverride_LargeGroup_FiveShootersTiedAtGold()
        {
            // 5 shooters tied at hits=50/40 — overlaps Guld/Silver/Brons (ranks 1-3).
            // Round 1: A=6/4, B=5/4, C=4/3, D=4/3, E=2/2.
            // Expected: A,B,E resolved; C and D still tied; A=gold, B=silver, then C/D contest bronze.
            var sorted = new List<FaltskytteShooterResult>
            {
                new() { MemberId = 1, Name = "A", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40 },
                new() { MemberId = 2, Name = "B", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40 },
                new() { MemberId = 3, Name = "C", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40 },
                new() { MemberId = 4, Name = "D", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40 },
                new() { MemberId = 5, Name = "E", ShootingClass = "C2", TotalHits = 50, TotalFigures = 40 },
            };
            var entries = new List<FaltskytteShootOffEntry>
            {
                new() { MemberId = 1, ShootingClass = "C2", Round = 1, Hits = 6, Figures = 4 },
                new() { MemberId = 2, ShootingClass = "C2", Round = 1, Hits = 5, Figures = 4 },
                new() { MemberId = 3, ShootingClass = "C2", Round = 1, Hits = 4, Figures = 3 },
                new() { MemberId = 4, ShootingClass = "C2", Round = 1, Hits = 4, Figures = 3 },
                new() { MemberId = 5, ShootingClass = "C2", Round = 1, Hits = 2, Figures = 2 },
            };

            var tied = FaltskytteShootOffService.DetectTiedMedalGroups(sorted, "Normal", "Faltskytte");
            FaltskytteShootOffService.ApplyShootOffOverride(sorted, tied, entries.ToLookup(e => e.MemberId), new NormalRoundComparer());

            FaltskytteShooterResult ById(int id) => sorted.First(s => s.MemberId == id);
            ById(1).ShootOffIsResolved.Should().BeTrue();
            ById(2).ShootOffIsResolved.Should().BeTrue();
            ById(5).ShootOffIsResolved.Should().BeTrue();
            ById(3).ShootOffIsResolved.Should().BeFalse();
            ById(4).ShootOffIsResolved.Should().BeFalse();
            ById(3).ShootOffNextRound.Should().Be(2);
            ById(4).ShootOffNextRound.Should().Be(2);
        }
    }
}
