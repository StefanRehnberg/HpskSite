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

        // ── Starter och tävlingar (Hallands faktura 2512, 2025-06-11) ──────────────────────────

        private static readonly RegionFeeRate Halland =
            new(0m, 10m, PerStart: 20m, PerCompetition: 20m, StartLabel: "Startavgifter", CompetitionLabel: "Lagavgift kretsen");

        [Fact]
        public void Hallands_faktura_blir_samma_tre_rader_och_summa()
        {
            var lines = RegionFeeCalculator.Lines(2024, Halland, 130, 140, 11, null);

            lines.Select(l => l.Kind).Should().Equal(
                MembershipFeeLineKind.PerMember, MembershipFeeLineKind.PerCompetition, MembershipFeeLineKind.PerStart);
            lines.Select(l => l.Amount).Should().Equal(1300m, 220m, 2800m);
            RegionFeeCalculator.Total(lines).Should().Be(4320m);
        }

        [Fact]
        public void Radernas_text_bar_kretsens_egna_namn_antal_och_pris()
        {
            var lines = RegionFeeCalculator.Lines(2024, Halland, 130, 140, 11, null);

            lines[1].Description.Should().Be("Lagavgift kretsen 2024, 11 tävlingar à 20 kr");
            lines[2].Description.Should().Be("Startavgifter 2024, 140 starter à 20 kr");
            lines[2].Quantity.Should().Be(140);
            lines[2].UnitPrice.Should().Be(20m);
        }

        [Fact]
        public void Noll_starter_ger_ingen_rad()
            => RegionFeeCalculator.Lines(2024, Halland, 130, 0, 0, null)
                .Should().ContainSingle().Which.Kind.Should().Be(MembershipFeeLineKind.PerMember);

        [Fact]
        public void Ett_handskrivet_belopp_ersatter_aven_starterna()
            => RegionFeeCalculator.Lines(2024, Halland, 130, 140, 11, 4000m)
                .Should().ContainSingle().Which.Amount.Should().Be(4000m);

        [Fact]
        public void En_start_och_en_tavling_i_singular()
        {
            var lines = RegionFeeCalculator.Lines(2024, Halland, 0, 1, 1, null);
            lines.Select(l => l.Description).Should().Equal(
                "Lagavgift kretsen 2024, 1 tävling à 20 kr", "Startavgifter 2024, 1 start à 20 kr");
        }

        [Fact]
        public void Rattat_antal_pa_skickad_avgift_behaller_pris_namn_och_tillagg()
        {
            var lines = RegionFeeCalculator.Lines(2024, Halland, 130, 140, 11, null);
            lines.Add(new MembershipFeeChargeLine { Kind = MembershipFeeLineKind.Extra, Description = "Porto", Amount = 50m });

            // Taxan har höjts efter utskicket — en skickad räkning behåller sitt pris.
            var raised = Halland with { PerStart = 30m, StartLabel = "Nytt namn" };
            RegionFeeCalculator.SetCompetitionCounts(lines, 2024, raised, 150, 11).Should().BeNull();

            var start = lines.Single(l => l.Kind == MembershipFeeLineKind.PerStart);
            start.Amount.Should().Be(3000m);
            start.Description.Should().Be("Startavgifter 2024, 150 starter à 20 kr");
            lines.Last().Description.Should().Be("Porto", "tilläggen står sist");
            RegionFeeCalculator.Total(lines).Should().Be(1300m + 220m + 3000m + 50m);
        }

        [Fact]
        public void Rattat_antal_vagras_nar_beloppet_ar_handskrivet()
        {
            var lines = RegionFeeCalculator.Lines(2024, Halland, 130, 140, 11, 4000m);
            RegionFeeCalculator.SetCompetitionCounts(lines, 2024, Halland, 150, 11).Should().NotBeNull();
        }

        [Fact]
        public void Gamla_signaturen_ger_inga_start_eller_tavlingsrader()
            => RegionFeeCalculator.Lines(2024, 0m, 10m, 130, null)
                .Should().ContainSingle().Which.Kind.Should().Be(MembershipFeeLineKind.PerMember);
    }
}
