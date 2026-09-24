using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Ledger;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tävlingar och evenemang blir projekt — de rena reglerna.
    ///
    /// <para><b>⚠️ ETT FEL HÄR ÄR PERMANENT.</b> Projektet stämplas på en konteringsrad som aldrig
    /// kan ändras. Hamnar Nybörjarkvällen den 10 september på Nybörjarkvällen den 3 septembers
    /// projekt, eller får varje skytt ett eget projekt, går det inte att rätta i efterhand. Därför
    /// prövas både VILKEN källa som ger ett projekt och HUR namnet väljs när två källor heter lika.</para>
    /// </summary>
    public class LedgerProjectSourceTests
    {
        // ── Vilken källa ger vilket projekt ────────────────────────────────────────────────────

        [Fact]
        public void Anmalningsavgift_och_lagavgift_pekar_pa_SAMMA_tavlingsprojekt()
        {
            // Delades de skulle tävlingens resultat ligga i två projekt — exakt uppdelningen
            // dimensionen finns för att undvika.
            var reg = LedgerProjectSource.For(LedgerSourceType.CompetitionRegistration, 2203);
            var team = LedgerProjectSource.For(LedgerSourceType.TeamFee, 2203);

            reg.Should().Be((LedgerProjectSource.Competition, 2203));
            team.Should().Be(reg);
        }

        [Fact]
        public void Evenemang_blir_ett_evenemangsprojekt()
            => LedgerProjectSource.For(LedgerSourceType.Event, 9054)
                .Should().Be((LedgerProjectSource.Event, 9054));

        [Theory]
        [InlineData(LedgerSourceType.Manual)]
        [InlineData(LedgerSourceType.MembershipFee)]
        [InlineData(LedgerSourceType.RegionFee)]
        [InlineData(LedgerSourceType.Expense)]
        [InlineData(LedgerSourceType.AssetDepreciation)]
        [InlineData(LedgerSourceType.AssetDisposal)]
        [InlineData(LedgerSourceType.BankImport)]
        [InlineData(LedgerSourceType.OpeningBalance)]
        [InlineData("okand-kalla")]
        public void Allt_annat_far_INGET_automatiskt_projekt(string sourceType)
            // En medlemsavgift hör inte till en tävling; att hitta på ett projekt för den vore att
            // märka en rad med något som inte är sant.
            => LedgerProjectSource.For(sourceType, 1234).Should().BeNull();

        [Theory]
        [InlineData(null)]
        [InlineData(0)]
        [InlineData(-5)]
        public void Utan_en_riktig_nod_blir_det_inget_projekt(int? sourceId)
            // Nod-id är positiva. Ett projekt nycklat på 0 hade samlat varje källlös rad på ett ställe.
            => LedgerProjectSource.For(LedgerSourceType.Event, sourceId).Should().BeNull();

        // ── Namnet ─────────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Forsta_namnet_ar_namnet_med_artal()
            => LedgerProjectSource.NameCandidates("Klubbmästerskap precision", new DateTime(2026, 5, 3),
                                                  LedgerProjectSource.Competition, 2203)
                .First().Should().Be("Klubbmästerskap precision 2026");

        [Fact]
        public void Artalet_laggs_inte_till_tva_ganger()
            => LedgerProjectSource.NameCandidates("SSM 2026", new DateTime(2026, 8, 29),
                                                  LedgerProjectSource.Competition, 8695)
                .First().Should().Be("SSM 2026");

        [Fact]
        public void Ett_annat_artal_i_namnet_ar_arrangorens_och_star_kvar()
            // "Årsmöte 2027" hålls hösten 2026 — det är ÅRSMÖTET för 2027, och "Årsmöte 2027 2026"
            // vore obegripligt. Hittat i dev-data (händelse 9055).
            => LedgerProjectSource.NameCandidates("Årsmöte 2027", new DateTime(2026, 10, 20),
                                                  LedgerProjectSource.Event, 9055)
                .Should().Equal("Årsmöte 2027", "Årsmöte 2027 2026-10-20", "Årsmöte 2027 (#9055)");

        [Fact]
        public void Ett_tal_som_inte_ar_ett_artal_raknas_inte_som_artal()
            // "Serie 12500" eller "Omgång 3" är inte årtal; årtalet ska läggas till.
            => LedgerProjectSource.NameCandidates("Poäng 12500", new DateTime(2026, 5, 1),
                                                  LedgerProjectSource.Competition, 1)
                .First().Should().Be("Poäng 12500 2026");

        [Fact]
        public void Nar_namnet_ar_upptaget_blir_nasta_kandidat_DATUMET_sedan_ID()
        {
            // Nybörjarkvällen kopieras varje vecka: samma namn, samma år. Utan datumet hade den
            // andra veckans avgifter hamnat på den första veckans projekt.
            var names = LedgerProjectSource.NameCandidates("Nybörjarkväll", new DateTime(2026, 9, 10),
                                                           LedgerProjectSource.Event, 9055);

            names.Should().Equal("Nybörjarkväll 2026", "Nybörjarkväll 2026-09-10", "Nybörjarkväll (#9055)");
        }

        [Fact]
        public void Sista_kandidaten_ar_alltid_unik_per_kalla()
        {
            var a = LedgerProjectSource.NameCandidates("Träning", new DateTime(2026, 9, 10), LedgerProjectSource.Event, 1);
            var b = LedgerProjectSource.NameCandidates("Träning", new DateTime(2026, 9, 10), LedgerProjectSource.Event, 2);

            // Allt utom id-kandidaten är lika — och id-kandidaten måste därför skilja.
            a.Last().Should().NotBe(b.Last());
        }

        [Fact]
        public void Utan_datum_provas_namnet_och_sedan_id()
            => LedgerProjectSource.NameCandidates("Hallandsserien", null, LedgerProjectSource.Series, 2202)
                .Should().Equal("Hallandsserien", "Hallandsserien (#2202)");

        [Fact]
        public void Ett_orimligt_datum_raknas_som_inget_datum()
            // DateTime.MinValue är vad en osatt datumegenskap ger. "Klubbkväll 1" vore ett namn
            // ingen förstår.
            => LedgerProjectSource.NameCandidates("Klubbkväll", DateTime.MinValue, LedgerProjectSource.Event, 7)
                .First().Should().Be("Klubbkväll");

        [Fact]
        public void Tomt_namn_far_ett_begripligt_ersattningsnamn()
        {
            LedgerProjectSource.NameCandidates("   ", null, LedgerProjectSource.Event, 7)
                .First().Should().Be("Evenemang");
            LedgerProjectSource.NameCandidates(null, null, LedgerProjectSource.Competition, 7)
                .First().Should().Be("Tävling");
        }

        [Fact]
        public void Ett_langt_namn_kapas_men_behaller_det_sarskiljande_slutet()
        {
            var longName = new string('x', 200);
            var names = LedgerProjectSource.NameCandidates(longName, new DateTime(2026, 9, 10),
                                                           LedgerProjectSource.Event, 9055);

            names.Should().OnlyContain(n => n.Length <= LedgerProjectSource.MaxNameLength);

            // Kapades SLUTET skulle alla tre kandidaterna bli samma sträng, och kedjan hade inte
            // längre kunnat hitta ett ledigt namn.
            names.Should().OnlyHaveUniqueItems();
            names[0].Should().EndWith(" 2026");
            names[1].Should().EndWith(" 2026-09-10");
            names[2].Should().EndWith(" (#9055)");
        }

        [Fact]
        public void Mellanslag_i_namnet_stadas()
            => LedgerProjectSource.NameCandidates("  Gåsa   skjutning ", new DateTime(2026, 11, 7),
                                                  LedgerProjectSource.Event, 1)
                .First().Should().Be("Gåsa skjutning 2026");
    }

    /// <summary>
    /// Gruppernas utfall. <b>Den enda fällan är dubbelräkning</b>, och den prövas här.
    /// </summary>
    public class LedgerProjectGroupReportTests
    {
        private static List<ProjectFigures> Projects() => new()
        {
            new() { ProjectId = 1, Name = "Klubbmästerskap precision 2026", Income = 3000m, Costs = 1200m },
            new() { ProjectId = 2, Name = "Klubbmästerskap fält 2026", Income = 2500m, Costs = 1800m },
            new() { ProjectId = 3, Name = "Kretsfältskytte 2026", Income = 9000m, Costs = 9400m },
            new() { ProjectId = 4, Name = "Klubbstugan", Income = 0m, Costs = 5000m }
        };

        private static LedgerProjectGroup G(int id, string name, string? source = null)
            => new() { Id = id, Name = name, SourceType = source };

        private static LedgerProjectGroupMember M(int group, int project)
            => new() { GroupId = group, ProjectId = project };

        [Fact]
        public void Gruppens_utfall_ar_summan_av_dess_projekt()
        {
            var r = LedgerProjectGroupReport.Build(
                new[] { G(10, "Klubbtävlingar") },
                new[] { M(10, 1), M(10, 2) },
                Projects()).Single();

            r.Income.Should().Be(5500m);
            r.Costs.Should().Be(3000m);
            r.Net.Should().Be(2500m);
            r.ProjectIds.Should().BeEquivalentTo(new[] { 1, 2 });
        }

        [Fact]
        public void Overlappande_grupper_sager_vilken_de_delar_projekt_med()
        {
            // "Klubbmästerskap fält" ingår i båda. Den som lägger ihop de två gruppernas summor får
            // ett tal som inte finns — därför måste båda säga det.
            var r = LedgerProjectGroupReport.Build(
                new[] { G(10, "Klubbtävlingar"), G(11, "Fältskytte") },
                new[] { M(10, 1), M(10, 2), M(11, 2), M(11, 3) },
                Projects());

            var klubb = r.Single(g => g.GroupId == 10);
            var falt = r.Single(g => g.GroupId == 11);

            klubb.Overlaps.Should().ContainSingle().Which.GroupName.Should().Be("Fältskytte");
            klubb.Overlaps.Single().SharedProjects.Should().Be(1);
            falt.Overlaps.Should().ContainSingle().Which.GroupName.Should().Be("Klubbtävlingar");
        }

        [Fact]
        public void Grupper_utan_gemensamma_projekt_har_inga_overlapp()
        {
            // Kontrollprov: utan det kunde "överlapp" vara en rad som alltid lyser.
            var r = LedgerProjectGroupReport.Build(
                new[] { G(10, "Klubbtävlingar"), G(12, "Anläggningen") },
                new[] { M(10, 1), M(10, 2), M(12, 4) },
                Projects());

            r.Should().OnlyContain(g => g.Overlaps.Count == 0);
        }

        [Fact]
        public void Samma_projekt_raknas_EN_gang_inom_en_grupp()
        {
            // En dubblerad medlemsrad (som primärnyckeln förhindrar, men ändå) får inte ge
            // projektet dubbel vikt i gruppen.
            var r = LedgerProjectGroupReport.Build(
                new[] { G(10, "Klubbtävlingar") },
                new[] { M(10, 1), M(10, 1) },
                Projects()).Single();

            r.Income.Should().Be(3000m);
        }

        [Fact]
        public void Ett_medlemskap_pa_ett_okant_projekt_blir_inget_tal()
        {
            // Ett projekt som inte hör till föreningen får aldrig synas i dess grupp.
            var r = LedgerProjectGroupReport.Build(
                new[] { G(10, "Klubbtävlingar") },
                new[] { M(10, 1), M(10, 999) },
                Projects()).Single();

            r.ProjectIds.Should().Equal(1);
            r.Income.Should().Be(3000m);
        }

        [Fact]
        public void En_tom_grupp_ar_noll_inte_ett_fel()
        {
            var r = LedgerProjectGroupReport.Build(new[] { G(10, "Ny grupp") },
                Array.Empty<LedgerProjectGroupMember>(), Projects()).Single();

            r.ProjectIds.Should().BeEmpty();
            r.Net.Should().Be(0m);
        }

        [Fact]
        public void Seriens_grupp_ar_markt_som_serie()
            => LedgerProjectGroupReport.Build(
                    new[] { G(10, "Hallandsserien", LedgerProjectSource.Series), G(11, "Egen") },
                    Array.Empty<LedgerProjectGroupMember>(), Projects())
                .Select(g => (g.Name, g.IsFromSeries))
                .Should().BeEquivalentTo(new[] { ("Egen", false), ("Hallandsserien", true) });
    }
}
