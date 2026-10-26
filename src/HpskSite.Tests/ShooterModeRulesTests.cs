using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Planera dagen: hur en funktionär som också skjuter förhåller sig till startlistan — ett förval och undantag per person.</summary>
    public class ShooterModeRulesTests
    {
        [Theory]
        [InlineData("Reserve", true)]
        [InlineData("Patrol", true)]
        [InlineData("ShootFirst", true)]
        [InlineData("Coordinated", true)]
        [InlineData("reserve", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        [InlineData("Annat", false)]
        public void Bara_de_fyra_lagena_ar_giltiga(string? mode, bool valid) => ShooterModeRules.IsValid(mode).Should().Be(valid);

        [Fact]
        public void Personens_eget_lage_gar_fore_forvalet_som_gar_fore_samordnat()
        {
            ShooterModeRules.Effective(ShooterMode.Patrol, ShooterMode.Reserve).Should().Be(ShooterMode.Reserve);
            ShooterModeRules.Effective(ShooterMode.Patrol, null).Should().Be(ShooterMode.Patrol);
            ShooterModeRules.Effective(null, null).Should().Be(ShooterMode.Coordinated);
        }

        [Fact]
        public void Ett_ogiltigt_lage_ignoreras_i_stallet_for_att_ge_ett_konstigt_resultat()
        {
            ShooterModeRules.Effective("Skräp", "Skräp2").Should().Be(ShooterMode.Coordinated);
            ShooterModeRules.Effective(ShooterMode.Reserve, "Skräp").Should().Be(ShooterMode.Reserve);
        }

        [Fact]
        public void Varje_lage_har_en_rubrik_och_en_mening_och_de_ar_olika()
        {
            var texts = ShooterMode.All.Select(ShooterModeRules.Describe).ToList();
            texts.Should().OnlyContain(t => t.Title.Length > 10 && t.Help.Length > 20);
            texts.Select(t => t.Title).Should().OnlyHaveUniqueItems();
        }

        [Fact]
        public void Bara_reserven_far_ingen_fast_starttid_och_bara_skjuter_forst_avloser_efter_starten()
        {
            ShooterMode.All.Where(ShooterModeRules.GetsFixedStart).Should().BeEquivalentTo(new[] { ShooterMode.Coordinated, ShooterMode.ShootFirst, ShooterMode.Patrol });
            ShooterMode.All.Where(ShooterModeRules.RelievesAfterStart).Should().Equal(ShooterMode.ShootFirst);
        }
    }
}
