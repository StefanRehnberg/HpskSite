using FluentAssertions;
using HpskSite.Models.Kretsgranskning;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Inläsningen av Förbundets stomprogramsida. Fixturen är sidans tabell som den såg ut 2026-10-02
    /// (rubriken säger 2027 fast adressen säger 2025), med hela radernas egenheter: datum över
    /// månadsgräns, bara en månad, en fotnot, en stavfel-gren och flera grener i samma namn.
    /// </summary>
    public class StomprogramHtmlImportTests
    {
        private const string Page = """
            <html><body><main>
            <h1>Stomprogram 2027</h1>
            <table class="block-table-content__table table"> <thead><tr class="table-odd"><th>#</th><th>Dag(ar)</th><th>Månad</th><th>Tävling</th><th>Plats</th></tr></thead><tbody>
            <tr class="table-odd"><td>1</td><td>1-2</td><td>maj</td><td>Kretsmästerskap i fältskjutning</td><td>Kretsvis</td></tr>
            <tr><td>2</td><td>24-6</td><td>april-juni</td><td>Rikstävlingen hemortens banor, precision och magnumprecision</td><td>Hemmabanan</td></tr>
            <tr class="table-odd"><td>3</td><td>15-16</td><td>maj</td><td>Landsdelsmästerskap fältskjutning</td><td>Landsdelsvis</td></tr>
            <tr><td>4</td><td>4-6</td><td>juni</td><td>SM Magnumprecision</td><td>Syd</td></tr>
            <tr class="table-odd"><td>5</td><td>5-6</td><td>juni</td><td>Landsdelsmästerskap Militär snabbmatch</td><td>Landsdelsvis</td></tr>
            <tr><td>6</td><td>12-13</td><td>juni</td><td>Landsdelsmästerskap PPC</td><td>Landsdelsvis </td></tr>
            <tr class="table-odd"><td>7</td><td>19-20</td><td>juni</td><td>Kretskonferens och F&ouml;rbundsm&ouml;te*</td><td>Stockholm </td></tr>
            <tr><td>8</td><td>6-11</td><td>juli</td><td>SM Fältskjutning, Precision och Militär snabbmatch</td><td>Skövde</td></tr>
            <tr class="table-odd"><td>9</td><td>17-18</td><td>juli</td><td>SM Magnumfältskjutning</td><td></td></tr>
            <tr><td>10</td><td>29-1</td><td>juli</td><td>SM PPC</td><td>Uppsala</td></tr>
            <tr class="table-odd"><td>11</td><td>7-8</td><td>augusti</td><td>Uttagning för landslaget i fältskytte</td><td>?</td></tr>
            <tr><td>12</td><td>21-22</td><td>augusti </td><td>SM Springskytte</td><td>Uppsala</td></tr>
            <tr><td>14</td><td>28-19</td><td>augusti-september </td><td>Rikstävling hemortens banor Militär snabbmatch</td><td>Hemmabanan</td></tr>
            <tr><td>16</td><td></td><td>september</td><td>Nordiskt mästerskap fätskjutning</td><td>Norge</td></tr>
            <tr class="table-odd"><td>17</td><td>8-16</td><td>oktober</td><td>VM PPC</td><td>Australien</td></tr>
            <tr><td>18</td><td></td><td></td><td>*Som orientering.</td><td></td></tr>
            </tbody> </table>
            </main></body></html>
            """;

        private static StomprogramHtmlImport.Result R() => StomprogramHtmlImport.Parse(Page);
        private static List<StomprogramHtmlImport.Row> RowsNamed(string startsWith) =>
            R().Rows.Where(r => r.Name.StartsWith(startsWith, StringComparison.Ordinal)).ToList();

        [Fact]
        public void Aret_LasesUrRubriken_InteUrAdressen()
        {
            R().Year.Should().Be(2027);
            R().Warnings.Should().BeEmpty();
        }

        [Fact]
        public void UtanAr_AntasReservaret_OchDetSags()
        {
            var r = StomprogramHtmlImport.Parse(Page.Replace("Stomprogram 2027", "Stomprogram"), 2030);
            r.Year.Should().Be(2030);
            r.Warnings.Should().ContainSingle().Which.Should().Contain("2030");
        }

        [Fact]
        public void EnkeltIntervall()
        {
            var km = RowsNamed("Kretsmästerskap i fält").Single();
            km.Start.Should().Be(new DateTime(2027, 5, 1));
            km.End.Should().Be(new DateTime(2027, 5, 2));
            km.Discipline.Should().Be("Faltskytte");
            km.Note.Should().Be("Kretsvis");
            km.IsPeriod.Should().BeFalse();
        }

        [Fact]
        public void SlutdagForeStartdag_MedEnManad_BorjarManadenForut()
        {
            var ppc = RowsNamed("SM PPC").Single();
            ppc.Start.Should().Be(new DateTime(2027, 6, 29));
            ppc.End.Should().Be(new DateTime(2027, 7, 1));
        }

        [Fact]
        public void TvaManader_GerStartOchSlutIVarsinManad()
        {
            var riks = RowsNamed("Rikstävlingen hemortens").ToList();
            riks.Should().OnlyContain(r => r.Start == new DateTime(2027, 4, 24) && r.End == new DateTime(2027, 6, 6));
            var mil = RowsNamed("Rikstävling hemortens banor Militär").Single();
            mil.Start.Should().Be(new DateTime(2027, 8, 28));
            mil.End.Should().Be(new DateTime(2027, 9, 19));
        }

        [Fact]
        public void HemortensBanor_ArEnPeriod()
        {
            RowsNamed("Rikstävlingen hemortens").Should().OnlyContain(r => r.IsPeriod);
            RowsNamed("Rikstävling hemortens").Should().OnlyContain(r => r.IsPeriod);
            RowsNamed("SM Fältskjutning").Should().OnlyContain(r => !r.IsPeriod);
        }

        [Fact]
        public void FleraGrenerINamnet_BlirEnRadPerGren()
        {
            RowsNamed("SM Fältskjutning, Precision").Select(r => r.Discipline)
                .Should().BeEquivalentTo(new[] { "Faltskytte", "Precision", "Milsnabb" });
            // ⚠️ "magnumprecision" får inte också ge Precision av en slump — men här står båda.
            RowsNamed("Rikstävlingen hemortens").Select(r => r.Discipline)
                .Should().BeEquivalentTo(new[] { "MagnumPrecision", "Precision" });
        }

        [Fact]
        public void MagnumOrdenTarInteDenVanligaGrenen()
        {
            RowsNamed("SM Magnumprecision").Single().Discipline.Should().Be("MagnumPrecision");
            RowsNamed("SM Magnumfält").Single().Discipline.Should().Be("MagnumFalt");
        }

        [Fact]
        public void GrenViInteHar_ArAnnanGren()
        {
            RowsNamed("SM PPC").Single().Discipline.Should().Be(StomprogramDisciplines.Other);
            RowsNamed("Landsdelsmästerskap PPC").Single().Discipline.Should().Be(StomprogramDisciplines.Other);
        }

        [Fact]
        public void IngenGrenINamnet_ArAllaGrener_OchFotnotenBlirAnteckning()
        {
            var fm = RowsNamed("Kretskonferens").Single();
            fm.Name.Should().Be("Kretskonferens och Förbundsmöte");
            fm.Discipline.Should().BeNull();
            fm.Note.Should().Be("Stockholm — Som orientering.");
        }

        [Fact]
        public void Fotnotsraden_LasesInteSomEnTavling()
        {
            R().Rows.Should().NotContain(r => r.Name.Contains("orientering"));
        }

        [Fact]
        public void BaraManad_GerIngetDatum_OchSagerVarfor()
        {
            var nm = RowsNamed("Nordiskt").Single();
            nm.Start.Should().BeNull();
            nm.Problem.Should().Contain("september");
            nm.Discipline.Should().Be("Faltskytte");   // stavfelet "fätskjutning" på Förbundets sida
        }

        [Fact]
        public void FragetecknetSomPlats_Utelamnas()
        {
            RowsNamed("Uttagning").Single().Note.Should().BeNull();
        }

        [Fact]
        public void SlutdatumLikaMedStart_BlirEndagars()
        {
            StomprogramHtmlImport.TryDates("12", "juni", 2027, out var s, out var e, out _).Should().BeTrue();
            s.Should().Be(new DateTime(2027, 6, 12));
            e.Should().BeNull();
        }

        [Fact]
        public void OmojligtDatum_ArEttProblem_InteEttUndantag()
        {
            StomprogramHtmlImport.TryDates("30-31", "februari", 2027, out var s, out _, out var why).Should().BeFalse();
            s.Should().BeNull();
            why.Should().NotBeNullOrEmpty();
        }

        [Fact]
        public void OverArsskiftet()
        {
            StomprogramHtmlImport.TryDates("28-3", "december-januari", 2027, out var s, out var e, out _).Should().BeTrue();
            s.Should().Be(new DateTime(2027, 12, 28));
            e.Should().Be(new DateTime(2028, 1, 3));
            StomprogramHtmlImport.TryDates("30-2", "januari", 2027, out s, out e, out _).Should().BeTrue();
            s.Should().Be(new DateTime(2026, 12, 30));
            e.Should().Be(new DateTime(2027, 1, 2));
        }

        [Fact]
        public void UtanTabell_SagerDet()
        {
            var r = StomprogramHtmlImport.Parse("<h1>Stomprogram 2027</h1><p>Inget här</p>");
            r.Rows.Should().BeEmpty();
            r.Warnings.Should().ContainSingle().Which.Should().Contain("tabell");
        }

        [Fact]
        public void Grenetiketter()
        {
            StomprogramDisciplines.Label(null).Should().Be("Alla grener");
            StomprogramDisciplines.Label(StomprogramDisciplines.Other).Should().Be("Annan gren");
            StomprogramDisciplines.IsValid("Precision").Should().BeTrue();
            StomprogramDisciplines.IsValid("PPC").Should().BeFalse();
        }

        // ── Krockregeln för en stomprogramperiod ──

        [Fact]
        public void StomprogramPeriod_VarnarInte_MenEnSmVeckaGorDet()
        {
            var comp = new KretsCalendarEntry { Kind = "competition", Id = 1, Name = "Klubbtävling", Date = new DateTime(2027, 5, 10), Discipline = "Precision" };
            var period = new KretsCalendarEntry { Kind = "stomprogram", Id = 2, Name = "Riks hemort", Date = new DateTime(2027, 4, 24), EndDate = new DateTime(2027, 6, 6), Discipline = "Precision", IsPeriod = true };
            KretsCalendarConflicts.Mark(new[] { comp, period });
            comp.Conflicts.Should().BeEmpty();
            period.IsPeriod.Should().BeTrue();

            var comp2 = new KretsCalendarEntry { Kind = "competition", Id = 3, Name = "Klubbtävling", Date = new DateTime(2027, 7, 8), Discipline = "Precision" };
            var sm = new KretsCalendarEntry { Kind = "stomprogram", Id = 4, Name = "SM", Date = new DateTime(2027, 7, 6), EndDate = new DateTime(2027, 7, 11), Discipline = "Precision" };
            KretsCalendarConflicts.Mark(new[] { comp2, sm });
            comp2.Conflicts.Should().ContainSingle();
            sm.IsPeriod.Should().BeFalse();
        }

        [Fact]
        public void AnnanGren_KrockarInte()
        {
            var comp = new KretsCalendarEntry { Kind = "competition", Id = 1, Name = "Klubbtävling", Date = new DateTime(2027, 6, 30), Discipline = "Precision" };
            var ppc = new KretsCalendarEntry { Kind = "stomprogram", Id = 2, Name = "SM PPC", Date = new DateTime(2027, 6, 29), EndDate = new DateTime(2027, 7, 1), Discipline = StomprogramDisciplines.Other };
            KretsCalendarConflicts.Mark(new[] { comp, ppc });
            comp.Conflicts.Should().BeEmpty();
        }
    }
}
