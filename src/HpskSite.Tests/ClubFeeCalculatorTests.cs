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
        public void Tillagg_for_ett_tillval_far_tillvalets_namn_pa_raden()
        {
            var a = Part(1, "Förbundsavgift", 100m, MembershipFeePartCondition.Option, option: 7); a.OptionName = "SPSF";
            var b = Part(2, "Förbundsavgift", 80m, MembershipFeePartCondition.Option, option: 8); b.OptionName = "Sportskytte";
            var c = Calc(600m, new[] { P(1, options: new[] { 7, 8 }) }, new[] { a, b });
            Assert.Equal(new[] { "Senior", "Förbundsavgift – SPSF", "Förbundsavgift – Sportskytte" }, c.Lines.Select(l => l.Description).ToArray());
        }

        [Fact]
        public void Heter_etiketten_redan_som_tillvalet_upprepas_namnet_inte()
        {
            var a = Part(1, "Avgift till SPSF", 100m, MembershipFeePartCondition.Option, option: 7); a.OptionName = "SPSF";
            var c = Calc(600m, new[] { P(1, options: new[] { 7 }) }, new[] { a });
            Assert.Equal("Avgift till SPSF", c.Lines[1].Description);
        }

        [Fact]
        public void Kort_etikett_som_ingar_i_tillvalets_langa_namn_upprepas_inte()
        {
            var a = Part(1, "Svartkrut", 200m, MembershipFeePartCondition.Option, option: 7); a.OptionName = "Svenska Svartkruts SkytteFederationen";
            var b = Part(2, "Skyttesportförbundet", 300m, MembershipFeePartCondition.Option, option: 8); b.OptionName = "Svenska Skyttesportförbundet";
            var c = Calc(600m, new[] { P(1, options: new[] { 7, 8 }) }, new[] { a, b });
            Assert.Equal(new[] { "Senior", "Svartkrut", "Skyttesportförbundet" }, c.Lines.Select(l => l.Description).ToArray());
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
        public void ForProposal_ger_hushallets_huvudmedlem_summan_och_ovriga_noll()
        {
            var cats = new[]
            {
                new MembershipFeeCategory { Id = 1, MembershipType = "Senior", Label = "Senior", Amount = 600m },
                new MembershipFeeCategory { Id = 4, MembershipType = "Familj", Label = "Familj", Amount = 900m },
            };
            var members = new List<MemberFeeInput>
            {
                P(10, "Senior", PrimaryClubState.Other),
                P(20, "Familj", PrimaryClubState.Ours),
                P(21, "Familj", PrimaryClubState.Other),
                P(40, ""),
            };
            members[1].HouseholdId = "H1"; members[1].HouseholdPrimary = true; members[2].HouseholdId = "H1";
            var parts = new[] { Part(1, "Annan huvudklubb", 100m, MembershipFeePartCondition.NotPrimaryClub) };

            var proposal = ClubFeeProposal.Build(cats, members);
            var calc = ClubFeeCalculator.ForProposal(proposal, cats, members, parts, NoTypes);

            Assert.Equal(700m, calc[10].Total);                 // individ, annan huvudklubb
            Assert.Equal(1000m, calc[20].Total);                // hushåll: 900 + 21:s tillägg
            Assert.Equal(21, calc[20].Lines.Single(l => l.Kind == ClubFeeCalculator.PartKind).SubjectMemberId);
            Assert.Equal(FeeCalculation.None, calc[21]);        // ingår
            Assert.Equal(FeeCalculation.None, calc[40]);        // saknar typ
        }

        [Fact]
        public void Individ_far_ingen_SubjectMemberId()
        {
            var c = Calc(600m, new[] { P(1) }, new[] { Part(1, "X", 10m) });
            Assert.All(c.Lines, l => Assert.Null(l.SubjectMemberId));
        }
    }
}
