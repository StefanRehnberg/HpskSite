using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Kretsavgiften — vad en klubb ska betala. Alla tre sätten kretsarna använder: fast belopp,
    /// per medlem, och handskrivet. Samma formel för de två första.
    /// </summary>
    public class RegionFeeCalculatorTests
    {
        [Fact]
        public void Fast_belopp_per_klubb_ar_bara_grundavgiften()
        {
            var lines = RegionFeeCalculator.Lines(2026, 300m, 0m, 25, null);

            lines.Should().ContainSingle().Which.Kind.Should().Be(MembershipFeeLineKind.Base);
            RegionFeeCalculator.Total(lines).Should().Be(300m);
        }

        [Fact]
        public void Per_medlem_ar_pris_ganger_antal()
        {
            // Hallands siffror (120, 280, 460) går jämnt upp i 20 kr per medlem.
            var lines = RegionFeeCalculator.Lines(2026, 0m, 20m, 23, null);

            var l = lines.Should().ContainSingle().Which;
            l.Kind.Should().Be(MembershipFeeLineKind.PerMember);
            l.Quantity.Should().Be(23);
            l.UnitPrice.Should().Be(20m);
            RegionFeeCalculator.Total(lines).Should().Be(460m);
        }

        [Fact]
        public void Grund_plus_per_medlem_ger_tva_rader_i_den_ordningen()
        {
            var lines = RegionFeeCalculator.Lines(2026, 200m, 20m, 14, null);

            lines.Select(l => l.Kind).Should().Equal(MembershipFeeLineKind.Base, MembershipFeeLineKind.PerMember);
            lines.Select(l => l.SortOrder).Should().Equal(0, 1);
            RegionFeeCalculator.Total(lines).Should().Be(480m);
        }

        [Fact]
        public void Ett_handskrivet_belopp_ERSATTER_formeln()
        {
            // Att dessutom lägga på grundavgiften vore att ta betalt två gånger.
            var lines = RegionFeeCalculator.Lines(2026, 200m, 20m, 14, 330m);

            lines.Should().ContainSingle().Which.Kind.Should().Be(MembershipFeeLineKind.Manual);
            RegionFeeCalculator.Total(lines).Should().Be(330m);
        }

        [Fact]
        public void Noll_medlemmar_ger_ingen_per_medlem_rad()
            // "0 medlemmar à 20 kr" läser som ett fel i klubbens register.
            => RegionFeeCalculator.Lines(2026, 200m, 20m, 0, null)
                .Should().ContainSingle().Which.Kind.Should().Be(MembershipFeeLineKind.Base);

        [Fact]
        public void Ingen_taxa_ger_inga_rader()
            // Ett krav på noll kronor skapas aldrig — avgift 0 betyder gratis, inte "ofylld".
            => RegionFeeCalculator.Lines(2026, 0m, 0m, 30, null).Should().BeEmpty();

        [Fact]
        public void Ett_handskrivet_nollbelopp_ger_inga_rader()
            => RegionFeeCalculator.Lines(2026, 200m, 20m, 14, 0m).Should().BeEmpty();

        [Fact]
        public void Per_medlem_raden_bar_antalet_i_texten()
            => RegionFeeCalculator.Lines(2026, 0m, 12.5m, 8, null).Single().Description
                .Should().Be("Avgift per medlem 2026, 8 medlemmar à 12,5 kr");

        [Fact]
        public void Att_ratta_antalet_raknar_om_bara_per_medlem_raden()
        {
            var lines = RegionFeeCalculator.Lines(2026, 200m, 20m, 14, null);
            lines.Add(new MembershipFeeChargeLine { Kind = MembershipFeeLineKind.Extra, Description = "Lagavgift", Amount = 50m });

            RegionFeeCalculator.SetMemberCount(lines, 2026, 20m, 20).Should().BeNull();

            lines.Select(l => l.Kind).Should().Equal(
                MembershipFeeLineKind.Base, MembershipFeeLineKind.PerMember, MembershipFeeLineKind.Extra);
            lines.Single(l => l.Kind == MembershipFeeLineKind.PerMember).Amount.Should().Be(400m);
            RegionFeeCalculator.Total(lines).Should().Be(650m);
            lines.Select(l => l.SortOrder).Should().Equal(0, 1, 2);
        }

        [Fact]
        public void Att_ratta_antalet_behaller_det_pris_kravet_skapades_med()
        {
            // Kretsen kan ha höjt taxan sedan kravet skapades. Ett rättat antal på ett gammalt krav
            // ska inte tyst byta pris — då ändras beloppet av något ingen bett om.
            var lines = RegionFeeCalculator.Lines(2026, 0m, 20m, 10, null);

            RegionFeeCalculator.SetMemberCount(lines, 2026, 25m, 12);

            lines.Single().UnitPrice.Should().Be(20m);
            lines.Single().Amount.Should().Be(240m);
        }

        [Fact]
        public void Att_ratta_till_noll_tar_bort_raden()
        {
            var lines = RegionFeeCalculator.Lines(2026, 200m, 20m, 14, null);
            RegionFeeCalculator.SetMemberCount(lines, 2026, 20m, 0);
            lines.Should().ContainSingle().Which.Kind.Should().Be(MembershipFeeLineKind.Base);
        }

        [Fact]
        public void Ett_handskrivet_belopp_gar_inte_att_rakna_per_medlem()
        {
            var lines = RegionFeeCalculator.Lines(2026, 200m, 20m, 14, 330m);
            RegionFeeCalculator.SetMemberCount(lines, 2026, 20m, 20).Should().NotBeNull();
            RegionFeeCalculator.Total(lines).Should().Be(330m);
        }

        [Fact]
        public void Negativt_antal_vagras()
            => RegionFeeCalculator.SetMemberCount(RegionFeeCalculator.Lines(2026, 0m, 20m, 5, null), 2026, 20m, -1)
                .Should().NotBeNull();

        [Theory]
        [InlineData("", 50)]
        [InlineData("   ", 50)]
        [InlineData("Lagavgift", 0)]
        [InlineData("Lagavgift", -10)]
        public void Ett_ofullstandigt_tillagg_vagras(string text, double amount)
            => RegionFeeCalculator.ValidateExtra(text, (decimal)amount).Should().NotBeNull();

        [Fact]
        public void Ett_riktigt_tillagg_godtas()
            => RegionFeeCalculator.ValidateExtra("Lagavgift kretsserien", 50m).Should().BeNull();
    }
}
