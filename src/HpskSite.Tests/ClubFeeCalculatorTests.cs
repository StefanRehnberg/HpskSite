using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Grundavgift + tillägg/avdrag, golv 0 med justeringsrad, filter, hushåll per person.</summary>
    public class ClubFeeCalculatorTests
    {
        private static MembershipFeePart Part(int id, string label, decimal amount, string cond = MembershipFeePartCondition.Always, int? option = null, int sort = 0)
            => new() { Id = id, Label = label, Amount = amount, Condition = cond, OptionId = option, SortOrder = sort };

        private static MemberFeeInput P(int id, string type = "Senior", PrimaryClubState home = PrimaryClubState.Unknown, params int[] options)
            => new() { MemberId = id, MembershipType = type, HomeClub = home, OptionIds = options };

        private static ILookup<int, string> NoTypes => Array.Empty<(int, string)>().ToLookup(x => x.Item1, x => x.Item2);

        private static FeeCalculation Calc(decimal? baseAmount, IEnumerable<MemberFeeInput> who, IEnumerable<MembershipFeePart> parts,
            ILookup<int, string>? types = null, bool household = false)
            => ClubFeeCalculator.Calculate(baseAmount, "Senior", who, household, parts, types ?? NoTypes);

        [Fact]
        public void Utan_grundavgift_inga_rader_och_ingen_summa()
        {
            var c = Calc(null, new[] { P(1) }, new[] { Part(1, "Tillägg", 100m) });
            Assert.Equal(0m, c.Total);
            Assert.Empty(c.Lines);
        }

        [Fact]
        public void Utan_delar_ar_summan_grundavgiften_med_en_rad()
        {
            var c = Calc(600m, new[] { P(1) }, Array.Empty<MembershipFeePart>());
            Assert.Equal(600m, c.Total);
            var l = Assert.Single(c.Lines);
            Assert.Equal((ClubFeeCalculator.BaseKind, 600m, (int?)null), (l.Kind, l.Amount, l.PartId));
        }

        [Fact]
        public void Summan_ar_alltid_radernas_summa()
        {
            var c = Calc(600m, new[] { P(1, home: PrimaryClubState.Other) },
                new[] { Part(1, "Annan huvudklubb", 150m, MembershipFeePartCondition.NotPrimaryClub), Part(2, "Rabatt", -50m) });
            Assert.Equal(700m, c.Total);
            Assert.Equal(c.Total, c.Lines.Sum(l => l.Amount));
        }

        [Fact]
        public void Saknad_huvudklubb_traffar_varken_vi_eller_annan()
        {
            var parts = new[]
            {
                Part(1, "Vi", 100m, MembershipFeePartCondition.PrimaryClub),
                Part(2, "Annan", 200m, MembershipFeePartCondition.NotPrimaryClub),
            };
            Assert.Equal(600m, Calc(600m, new[] { P(1, home: PrimaryClubState.Unknown) }, parts).Total);
            Assert.Equal(700m, Calc(600m, new[] { P(1, home: PrimaryClubState.Ours) }, parts).Total);
            Assert.Equal(800m, Calc(600m, new[] { P(1, home: PrimaryClubState.Other) }, parts).Total);
        }

        [Fact]
        public void Tillval_ger_tillagget_bara_med_rätt_tillval()
        {
            var parts = new[] { Part(1, "SPSF", 120m, MembershipFeePartCondition.Option, option: 7) };
            Assert.Equal(720m, Calc(600m, new[] { P(1, options: 7) }, parts).Total);
            Assert.Equal(600m, Calc(600m, new[] { P(1, options: 8) }, parts).Total);
            Assert.Equal(600m, Calc(600m, new[] { P(1) }, parts).Total);
        }

        [Fact]
        public void Typfilter_begransar_och_inga_typer_betyder_alla()
        {
            var parts = new[] { Part(1, "Bara junior", -100m) };
            var juniorOnly = new[] { (1, "Junior") }.ToLookup(x => x.Item1, x => x.Item2);
            Assert.Equal(500m, Calc(600m, new[] { P(1, "junior") }, parts, juniorOnly).Total);
            Assert.Equal(600m, Calc(600m, new[] { P(1, "Senior") }, parts, juniorOnly).Total);
            Assert.Equal(500m, Calc(600m, new[] { P(1, "Senior") }, parts).Total);
        }

        [Fact]
        public void Golv_noll_med_synlig_justeringsrad()
        {
            var c = Calc(100m, new[] { P(1) }, new[] { Part(1, "Stor rabatt", -250m) });
            Assert.Equal(0m, c.Total);
            var adj = Assert.Single(c.Lines, l => l.Kind == ClubFeeCalculator.AdjustKind);
            Assert.Equal(150m, adj.Amount);
            Assert.Equal(c.Total, c.Lines.Sum(l => l.Amount));
        }

        [Fact]
        public void Hushall_prövas_per_person_och_raden_bar_personen()
        {
            var parts = new[] { Part(1, "Annan huvudklubb", 100m, MembershipFeePartCondition.NotPrimaryClub) };
            var who = new[] { P(20, "Familj", PrimaryClubState.Ours), P(21, "Familj", PrimaryClubState.Other), P(22, "Familj", PrimaryClubState.Other) };
            var c = Calc(900m, who, parts, household: true);
            Assert.Equal(1100m, c.Total);
            var partLines = c.Lines.Where(l => l.Kind == ClubFeeCalculator.PartKind).ToList();
            Assert.Equal(new int?[] { 21, 22 }, partLines.Select(l => l.SubjectMemberId).ToArray());
            Assert.Null(c.Lines.First(l => l.Kind == ClubFeeCalculator.BaseKind).SubjectMemberId);
        }

        [Fact]
        public void Individ_far_ingen_SubjectMemberId()
        {
            var c = Calc(600m, new[] { P(1) }, new[] { Part(1, "X", 10m) });
            Assert.All(c.Lines, l => Assert.Null(l.SubjectMemberId));
        }
    }
}
