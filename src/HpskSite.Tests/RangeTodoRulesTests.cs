using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Rutan "Att göra" på banans översikt (2026-10-11). En ruta som alltid lyser slutar läsas, och en
    /// som tiger när tillståndet gått ut är värre än ingen — därför prövas båda riktningarna.
    /// </summary>
    public class RangeTodoRulesTests
    {
        private static readonly DateTime Today = new(2026, 10, 11);

        /// <summary>En bana där allt är i ordning — utgångsläget som varje test ändrar EN sak i.</summary>
        private static RangeTodoInput Ok() => new()
        {
            Today = Today,
            Permits = { new RangeTodoDated("Polistillstånd", Today.AddYears(2), 50000) },
            Documents = { new RangeTodoDatedDoc("Besiktningsprotokoll", Today.AddYears(1)) },
            Clubs = { new RangeTodoClub("Haaplinge GoAss", 2) },
            ShotsThisYear = 1000,
            HasSkjutbanechef = true,
            HasPosition = true,
            SectionCount = 2
        };

        [Fact]
        public void En_bana_i_ordning_har_ingenting_att_gora()
            => RangeTodoRules.Build(Ok()).Should().BeEmpty();

        [Fact]
        public void Ett_tillstand_som_gatt_ut_ar_brattom_och_kommer_forst()
        {
            var i = Ok();
            i.HasSkjutbanechef = false;
            i.Permits[0] = new RangeTodoDated("Polistillstånd", Today.AddDays(-3), 50000);
            var list = RangeTodoRules.Build(i);
            list.First().Severity.Should().Be(RangeTodoItem.Danger);
            list.First().Text.Should().Contain("gick ut 2026-10-08");
            list.First().GoTo.Should().Be("tillstand");
        }

        [Theory]
        [InlineData(0, true, "går ut i dag")]
        [InlineData(1, true, "om 1 dag)")]
        [InlineData(90, true, "om 90 dagar")]
        [InlineData(91, false, "")]
        public void Ett_tillstand_som_snart_gar_ut_varnar_inom_90_dagar(int days, bool warns, string text)
        {
            var i = Ok();
            i.Permits[0] = new RangeTodoDated("Polistillstånd", Today.AddDays(days), 50000);
            var list = RangeTodoRules.Build(i);
            if (warns)
            {
                list.Should().ContainSingle();
                list[0].Severity.Should().Be(RangeTodoItem.Warn);
                list[0].Text.Should().Contain(text);
            }
            else list.Should().BeEmpty();
        }

        [Fact]
        public void Ett_dokument_som_gar_ut_namnges()
        {
            var i = Ok();
            i.Documents[0] = new RangeTodoDatedDoc("Besiktningsprotokoll", Today.AddDays(10));
            RangeTodoRules.Build(i).Should().ContainSingle().Which.Text.Should().StartWith("Besiktningsprotokoll går ut");
        }

        [Fact]
        public void Utan_tillstand_sags_det_men_inte_att_skottaket_saknas()
        {
            var i = Ok();
            i.Permits.Clear();
            var list = RangeTodoRules.Build(i);
            list.Should().ContainSingle().Which.Text.Should().Contain("Inga tillstånd");
        }

        [Fact]
        public void Tillstand_utan_skottak_sags()
        {
            var i = Ok();
            i.Permits[0] = new RangeTodoDated("Polistillstånd", Today.AddYears(2), null);
            RangeTodoRules.Build(i).Should().ContainSingle().Which.Text.Should().Contain("maxgräns");
        }

        [Theory]
        [InlineData(39999, null)]
        [InlineData(40000, RangeTodoItem.Warn)]
        [InlineData(50000, RangeTodoItem.Danger)]
        public void Skottaket_varnar_vid_80_procent_och_larmar_vid_taket(int shots, string? severity)
        {
            var i = Ok();
            i.ShotsThisYear = shots;
            var list = RangeTodoRules.Build(i);
            if (severity == null) list.Should().BeEmpty();
            else list.Should().ContainSingle().Which.Severity.Should().Be(severity);
        }

        [Fact]
        public void Lagsta_taket_galler_nar_tva_tillstand_anger_var_sitt()
        {
            var i = Ok();
            i.Permits.Add(new RangeTodoDated("Miljö", Today.AddYears(2), 10000));
            i.ShotsThisYear = 9000;
            RangeTodoRules.Build(i).Should().ContainSingle().Which.Text.Should().Contain("av 10");
        }

        [Fact]
        public void Klubb_utan_tider_namnges()
        {
            var i = Ok();
            i.Clubs.Add(new RangeTodoClub("Varbergs PK", 0));
            RangeTodoRules.Build(i).Should().ContainSingle().Which.Text.Should().Be("Varbergs PK har inga fasta tider på banan.");
        }

        [Fact]
        public void Tider_utanfor_fonstret_varnar()
        {
            var i = Ok();
            i.AllocationsOutsideWindow = 2;
            RangeTodoRules.Build(i).Should().ContainSingle().Which.Text.Should().StartWith("2 fasta tider");
        }

        [Fact]
        public void Saknad_skjutbanechef_position_och_vallar_sags()
        {
            var i = Ok();
            i.HasSkjutbanechef = false;
            i.HasPosition = false;
            i.SectionCount = 0;
            var list = RangeTodoRules.Build(i);
            list.Select(t => t.GoTo).Should().Equal("redigera", "redigera", "banor");
        }

        // ---- Fönstret -----------------------------------------------------------------------

        private static readonly RangeTodoWindow[] MonEvening = { new(1, new TimeSpan(17, 0, 0), new TimeSpan(21, 0, 0)) };

        [Theory]
        [InlineData(1, 18, 20, false)]   // helt inom
        [InlineData(1, 17, 21, false)]   // precis fönstret
        [InlineData(1, 16, 20, true)]    // börjar före
        [InlineData(1, 18, 22, true)]    // slutar efter
        [InlineData(2, 18, 20, true)]    // annan veckodag
        public void En_fast_tid_jamfors_mot_tillstandets_fonster(int day, int from, int to, bool outside)
            => RangeTodoRules.IsOutsideWindows((byte)day, new TimeSpan(from, 0, 0), new TimeSpan(to, 0, 0), MonEvening)
                .Should().Be(outside);

        [Fact]
        public void Utan_fonster_ligger_ingen_tid_utanfor()
            => RangeTodoRules.IsOutsideWindows(3, new TimeSpan(2, 0, 0), new TimeSpan(4, 0, 0), Array.Empty<RangeTodoWindow>())
                .Should().BeFalse();
    }
}
