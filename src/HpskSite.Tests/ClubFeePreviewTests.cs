using HpskSite.Models;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Differentialtest: exempelräkningen ska ge exakt samma avgift som listan/skapandet ger för en riktig
    /// medlem med samma uppgifter, mitt bland andra medlemmar.
    /// </summary>
    public class ClubFeePreviewTests
    {
        private static readonly MembershipFeeCategory[] Cats =
        {
            new() { Id = 1, MembershipType = "Senior", Label = "Senior", Amount = 600m },
            new() { Id = 2, MembershipType = "Junior", Label = "Junior", Amount = 200m },
            new() { Id = 4, MembershipType = "Familj", Label = "Familj", Amount = 900m },
        };

        private static readonly MembershipFeePart[] Parts =
        {
            new() { Id = 1, Label = "Annan huvudklubb", Amount = 100m, Condition = MembershipFeePartCondition.NotPrimaryClub, SortOrder = 1 },
            new() { Id = 2, Label = "SPSF", Amount = 150m, Condition = MembershipFeePartCondition.Option, OptionId = 7, SortOrder = 2 },
            new() { Id = 3, Label = "Juniorrabatt", Amount = -50m, Condition = MembershipFeePartCondition.Always, SortOrder = 3 },
        };

        private static readonly ILookup<int, string> Types =
            new[] { (3, "Junior") }.ToLookup(x => x.Item1, x => x.Item2);

        private static MemberFeeInput P(string type, PrimaryClubState home, params int[] options)
            => new() { MembershipType = type, HomeClub = home, OptionIds = options };

        [Theory]
        [InlineData("Senior", PrimaryClubState.Ours, new int[0], 600)]
        [InlineData("Senior", PrimaryClubState.Other, new int[0], 700)]
        [InlineData("Senior", PrimaryClubState.Other, new[] { 7 }, 850)]
        [InlineData("Junior", PrimaryClubState.Ours, new[] { 7 }, 300)]
        public void Individ_ger_samma_summa_som_riktiga_listan(string type, PrimaryClubState home, int[] opts, int expected)
        {
            var preview = ClubFeePreview.Compute(Cats, Parts, Types, new[] { P(type, home, opts) });

            // Samma medlem på riktigt, omgiven av andra och med ett högt id.
            var real = new MemberFeeInput { MemberId = 999, MembershipType = type, HomeClub = home, OptionIds = opts };
            var members = new List<MemberFeeInput>
            {
                new() { MemberId = 1, MembershipType = "Senior", HomeClub = PrimaryClubState.Ours },
                real,
                new() { MemberId = 1000, MembershipType = "", HomeClub = PrimaryClubState.Other },
            };
            var proposal = ClubFeeProposal.Build(Cats, members);
            var calcs = ClubFeeCalculator.ForProposal(proposal, Cats, members, Parts, Types);

            Assert.Equal(expected, (int)preview.Calculation.Total);
            Assert.Equal(calcs[999].Total, preview.Calculation.Total);
            Assert.Equal(calcs[999].Lines.Select(l => (l.Kind, l.Description, l.Amount)),
                         preview.Calculation.Lines.Select(l => (l.Kind, l.Description, l.Amount)));
            Assert.Equal(proposal[999].Kind, preview.Kind);
        }

        [Fact]
        public void Familj_prövas_per_person_som_i_listan()
        {
            var preview = ClubFeePreview.Compute(Cats, Parts, Types, new[]
            {
                P("Familj", PrimaryClubState.Ours), P("Familj", PrimaryClubState.Other), P("Familj", PrimaryClubState.Other),
            });
            Assert.Equal(ClubFeeKind.HouseholdPrimary, preview.Kind);
            Assert.Equal(900m + 100m + 100m, preview.Calculation.Total);
        }

        [Fact]
        public void Typ_utan_avgift_och_tom_lista_ger_noll_utan_kast()
        {
            Assert.Equal(0m, ClubFeePreview.Compute(Cats, Parts, Types, new[] { P("Hedersmedlem", PrimaryClubState.Ours) }).Calculation.Total);
            Assert.Equal(ClubFeeKind.NoCategory,
                ClubFeePreview.Compute(Cats, Parts, Types, new[] { P("Hedersmedlem", PrimaryClubState.Ours) }).Kind);
            Assert.Equal(0m, ClubFeePreview.Compute(Cats, Parts, Types, Array.Empty<MemberFeeInput>()).Calculation.Total);
        }
    }
}
