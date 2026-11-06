using System.Linq;
using HpskSite.CompetitionTypes.Common;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tolkningen av sajtinställningen <c>OutOfCompetition:Disciplines</c> (rena funktioner; testerna rör aldrig den processgemensamma listan, som
    /// andra tester läser parallellt). Förvalet MÅSTE förbli fält-bara tills en gren släpps in med avsikt.
    /// </summary>
    public class OutOfCompetitionDisciplinesTests
    {
        [Fact]
        public void Forvalet_ar_falt_och_magnumfalt_och_ingenting_annat()
        {
            Assert.Equal(new[] { "Faltskytte", "MagnumFalt" }, OutOfCompetitionDisciplines.Default.OrderBy(x => x).ToArray());
            // Processens gällande lista är förvalet så länge ingen hostad tjänst har läst en inställning (testprocessen kör ingen).
            Assert.True(OutOfCompetitionDisciplines.Allows("Faltskytte"));
            Assert.False(OutOfCompetitionDisciplines.Allows("Precision"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Tomt_eller_saknat_varde_ger_forvalet(string? raw)
        {
            var (set, ignored) = OutOfCompetitionDisciplines.Parse(raw);
            Assert.Equal(new[] { "Faltskytte", "MagnumFalt" }, set.OrderBy(x => x).ToArray());
            Assert.Empty(ignored);
        }

        [Theory]
        [InlineData("off")]
        [InlineData("OFF")]
        [InlineData("none")]
        public void Off_stanger_av_alla_grenar(string raw)
        {
            var (set, _) = OutOfCompetitionDisciplines.Parse(raw);
            Assert.Empty(set);
        }

        [Fact]
        public void En_lista_tolkas_skiftlagesokant_och_med_komma_eller_semikolon()
        {
            var (set, ignored) = OutOfCompetitionDisciplines.Parse("faltskytte, MAGNUMFALT;precision");
            Assert.Equal(new[] { "Faltskytte", "MagnumFalt", "Precision" }, set.OrderBy(x => x).ToArray());
            Assert.Empty(ignored);
        }

        [Fact]
        public void Okanda_namn_ignoreras_och_rapporteras_och_springskytte_ar_inte_en_kand_gren()
        {
            var (set, ignored) = OutOfCompetitionDisciplines.Parse("Precision,Springskytte,Pilbage");
            Assert.Equal(new[] { "Precision" }, set.ToArray());
            Assert.Equal(new[] { "Springskytte", "Pilbage" }, ignored.ToArray());
        }

        [Fact]
        public void Hela_precisionsfamiljen_ar_en_kand_gren()
        {
            foreach (var d in new[] { "Precision", "Milsnabb", "Duell", "NationellHelmatch", "MagnumPrecision", "Standardpistol", "Sportpistol" })
                Assert.Contains(d, OutOfCompetitionDisciplines.Known);
        }

        [Fact]
        public void Reglerna_fraga_listan_inte_en_hardkodad_grenmangd()
        {
            // OutOfCompetitionApplies ska gå genom OutOfCompetitionDisciplines — annars går en gren inte att släppa in eller stänga av med inställningen.
            var src = System.IO.File.ReadAllText(System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(System.IO.Path.GetDirectoryName(Here()))!, "HpskSite", "CompetitionTypes", "Common", "ChampionshipRule.cs"));
            Assert.Contains("OutOfCompetitionDisciplines.Allows(discipline)", src);
            Assert.DoesNotContain("discipline is \"Faltskytte\"", src);
        }

        [Theory]
        [InlineData("Precision")]
        [InlineData("Milsnabb")]
        [InlineData("Duell")]
        [InlineData("NationellHelmatch")]
        [InlineData("MagnumPrecision")]
        [InlineData("Standardpistol")]
        [InlineData("Sportpistol")]
        public void Precisionsfamiljen_erbjuds_inget_vapengruppsval(string discipline)
        {
            // Stefan 2026-11-06: ingen vapengruppsinställning för precision. Finalen och medaljerna följer mästerskapskategorierna; en grupp "utanför"
            // skulle ge en kategorifinal utan mästare.
            Assert.Empty(ChampionshipWeaponGroups.OfferedFor(discipline, new[] { "A", "B", "C" }, new[] { "C" }));
        }

        [Fact]
        public void Falt_erbjuds_fortfarande_sina_vapengrupper()
        {
            var offered = ChampionshipWeaponGroups.OfferedFor("Faltskytte", System.Array.Empty<string>(), System.Array.Empty<string>());
            Assert.Equal(new[] { "A", "A Opt", "B", "C", "R" }.OrderBy(x => x, System.StringComparer.Ordinal).ToArray(), offered.ToArray());
        }

        private static string Here([System.Runtime.CompilerServices.CallerFilePath] string f = "") => f;
    }
}
