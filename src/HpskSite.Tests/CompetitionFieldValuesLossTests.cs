using FluentAssertions;
using HpskSite.Models.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>C3-planen §5b: torrkörningen namnger värden som inte överlever läsregeln före flytten.</summary>
    public class CompetitionFieldValuesLossTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Ett_tomt_varde_ar_aldrig_en_forlust(string? raw)
        {
            foreach (var shape in Enum.GetValues<CompetitionFieldShape>())
                CompetitionFieldValues.LossReason(shape, raw).Should().BeNull();
        }

        [Fact]
        public void Ett_heltal_som_inte_ar_ett_tal_namnges()
        {
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Int, "tio").Should().Contain("heltal");
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Int, "10").Should().BeNull();
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Int, 10).Should().BeNull();
        }

        [Fact]
        public void Ett_jaNej_varde_som_inte_ar_ett_namnges()
        {
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Bool, "ja").Should().Contain("ja/nej");
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Bool, "1").Should().BeNull();
            CompetitionFieldValues.LossReason(CompetitionFieldShape.BoolDefaultTrue, "True").Should().BeNull();
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Bool, 0).Should().BeNull();
        }

        [Fact]
        public void Ett_datum_som_inte_gar_att_lasa_namnges_men_inte_umbracos_tomma_datum()
        {
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Date, "nästa lördag").Should().Contain("datum");
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Date, DateTime.MinValue).Should().BeNull();
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Date, new DateTime(2026, 5, 15)).Should().BeNull();
        }

        [Fact]
        public void Ett_val_med_flera_varden_namnges_eftersom_bara_det_forsta_lases()
        {
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Choice, "[\"Poang\",\"Normal\"]").Should().Contain("flera val");
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Choice, "[\"Poang\"]").Should().BeNull();
            CompetitionFieldValues.LossReason(CompetitionFieldShape.Text, "{\"a\":1,\"b\":2}").Should().BeNull();
        }
    }
}
