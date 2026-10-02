using FluentAssertions;
using HpskSite.Models;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Fas 4, resten: stomprogrammet, kretsens checklista och grannkretsvalet.</summary>
    public class KretsPlanningTests
    {
        private static string? Disc(string s) => s.Equals("fält", StringComparison.OrdinalIgnoreCase) || s == "Faltskytte" ? "Faltskytte"
            : s.Equals("precision", StringComparison.OrdinalIgnoreCase) ? "Precision" : null;

        // ── Inklistrat stomprogram ───────────────────────────────────────────────────────

        [Fact]
        public void Inklistring_LaserDatumNamnOchGren()
        {
            var r = StomprogramPaste.Parse("2027-06-12;SM fält;fält\n2027-08-20–2027-08-22;SM precision;precision", Disc);
            r.Errors.Should().BeEmpty();
            r.Rows.Should().HaveCount(2);
            r.Rows[0].Should().Be(new StomprogramPaste.Row(new DateTime(2027, 6, 12), null, "SM fält", "Faltskytte"));
            r.Rows[1].End.Should().Be(new DateTime(2027, 8, 22));
            r.Rows[1].Discipline.Should().Be("Precision");
        }

        [Fact]
        public void Inklistring_UtanGren_GallerAllaGrener()
        {
            var r = StomprogramPaste.Parse("2027-09-04;Förbundsmöte\n2027-09-05;Riksstämma;alla", Disc);
            r.Errors.Should().BeEmpty();
            r.Rows.Should().OnlyContain(x => x.Discipline == null);
        }

        [Fact]
        public void Inklistring_TabbSomFranEttKalkylblad()
        {
            var r = StomprogramPaste.Parse("2027-06-12\tSM fält\tfält", Disc);
            r.Rows.Should().ContainSingle().Which.Discipline.Should().Be("Faltskytte");
        }

        [Fact]
        public void Inklistring_KortSlutdag()
        {
            StomprogramPaste.TryRange("2027-06-12--13", out var s, out var e).Should().BeTrue();
            s.Should().Be(new DateTime(2027, 6, 12));
            e.Should().Be(new DateTime(2027, 6, 13));
        }

        [Fact]
        public void Inklistring_SlutFoereStart_Vagras()
        {
            StomprogramPaste.TryRange("2027-06-12–2027-06-10", out _, out _).Should().BeFalse();
        }

        [Fact]
        public void Inklistring_FelSagsPerRad_OchRestenLasesAnda()
        {
            var r = StomprogramPaste.Parse("# kommentar\n12 juni;SM\n2027-06-12;SM fält;okänd gren\n2027-07-01;KM", Disc);
            r.Rows.Should().ContainSingle().Which.Name.Should().Be("KM");
            r.Errors.Should().HaveCount(2);
            r.Errors[0].Should().StartWith("Rad 2");
            r.Errors[1].Should().Contain("okänd gren");
        }

        // ── Krockvarning mot stomprogrammet ─────────────────────────────────────────────

        private static KretsCalendarEntry E(string kind, int id, string disc, DateTime date, bool neighbour = false)
            => new() { Kind = kind, Id = id, Name = $"{kind}{id}", Discipline = disc, DisciplineLabel = disc, Date = date, IsNeighbour = neighbour, RegionName = "X" };

        private static readonly DateTime D = new(2027, 6, 12);

        [Fact]
        public void Stomprogram_SammaGrenSammaDag_Varnas()
        {
            var c = E("competition", 1, "Faltskytte", D);
            var s = E("stomprogram", 9, "Faltskytte", D);
            KretsCalendarConflicts.Mark(new[] { c, s });
            c.Conflicts.Should().ContainSingle().Which.Should().Contain("stomprogram").And.Contain("stomprogram9");
            s.Conflicts.Should().BeEmpty("stomprogrammet är bakgrund, inte något kretsen ska åtgärda");
        }

        [Fact]
        public void Stomprogram_UtanGren_KrockarMedAllaGrener()
        {
            var c = E("application", 1, "Precision", D);
            var s = E("stomprogram", 9, "", D);
            KretsCalendarConflicts.Mark(new[] { c, s });
            c.Conflicts.Should().ContainSingle();
        }

        [Fact]
        public void Stomprogram_AnnanGren_KrockarInte()
        {
            var c = E("competition", 1, "Precision", D);
            var s = E("stomprogram", 9, "Faltskytte", D);
            KretsCalendarConflicts.Mark(new[] { c, s });
            c.Conflicts.Should().BeEmpty();
        }

        [Fact]
        public void Stomprogram_GrannensTavling_VarnasInte()
        {
            var n = E("competition", 1, "Faltskytte", D, neighbour: true);
            var s = E("stomprogram", 9, "Faltskytte", D);
            KretsCalendarConflicts.Mark(new[] { n, s });
            n.Conflicts.Should().BeEmpty();
        }

        [Fact]
        public void Stomprogram_FlerdagarsTackerTavlingen()
        {
            var c = E("competition", 1, "Faltskytte", D.AddDays(1));
            var s = E("stomprogram", 9, "Faltskytte", D);
            s.EndDate = D.AddDays(2);
            KretsCalendarConflicts.Mark(new[] { c, s });
            c.Conflicts.Should().ContainSingle();
        }

        [Fact]
        public void Stomprogram_TvaPoster_KrockarInteMedVarandra()
        {
            var a = E("stomprogram", 1, "Faltskytte", D);
            var b = E("stomprogram", 2, "Faltskytte", D);
            KretsCalendarConflicts.Mark(new[] { a, b });
            a.Conflicts.Should().BeEmpty();
            b.Conflicts.Should().BeEmpty();
        }

        // ── Vilka tävlingar en punkt i checklistan gäller ───────────────────────────────

        [Theory]
        [InlineData(OrganiserChecklistScope.All, false, false, false, true)]
        [InlineData(OrganiserChecklistScope.All, true, true, true, false)]           // klubbinterna aldrig
        [InlineData(OrganiserChecklistScope.KretsOrAbove, false, false, false, false)]
        [InlineData(OrganiserChecklistScope.KretsOrAbove, false, true, false, true)]
        [InlineData(OrganiserChecklistScope.Kretsmasterskap, false, true, false, false)]
        [InlineData(OrganiserChecklistScope.Kretsmasterskap, false, true, true, true)]
        [InlineData("skräp", false, false, false, true)]                             // okänt = alla
        public void Checklista_Galler(string scope, bool clubOnly, bool kretsOrAbove, bool km, bool expected) =>
            OrganiserChecklistScope.Applies(scope, clubOnly, kretsOrAbove, km).Should().Be(expected);

        // ── Grannkretsvalet ──────────────────────────────────────────────────────────────

        [Fact]
        public void Grannar_UtanVal_ArStandarden() =>
            RegionNeighbourSetting.Resolve("Halland", null).Should().BeEquivalentTo(RegionAdjacency.NeighboursOf("Halland"));

        [Fact]
        public void Grannar_TomtVal_ArInga() =>
            RegionNeighbourSetting.Resolve("Halland", "").Should().BeEmpty();

        [Fact]
        public void Grannar_EgetVal_UtanSigSjalv() =>
            RegionNeighbourSetting.Resolve("Halland", "Kronoberg,Halland, Blekinge").Should().BeEquivalentTo(new[] { "Kronoberg", "Blekinge" });

        [Fact]
        public void Grannar_ValSomArStandarden_LagrasSomNull() =>
            RegionNeighbourSetting.ToStored("Halland", RegionAdjacency.NeighboursOf("Halland").Reverse()).Should().BeNull(
                "så att en rättelse av gränsschemat senare slår igenom för kretsar som inte valt själva");

        [Fact]
        public void Grannar_AnnatVal_LagrasSomKoder()
        {
            var stored = RegionNeighbourSetting.ToStored("Halland", new[] { "Blekinge" });
            stored.Should().Be("Blekinge");
            RegionNeighbourSetting.Resolve("Halland", stored).Should().Equal("Blekinge");
        }

        [Fact]
        public void Grannar_IngetValt_LagrasSomTomStrang_InteNull() =>
            RegionNeighbourSetting.ToStored("Halland", Array.Empty<string>()).Should().Be("");
    }
}
