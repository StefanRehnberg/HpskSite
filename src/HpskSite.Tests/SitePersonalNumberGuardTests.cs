using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2a: personnummerspärren (v3 D1), ren funktion.</summary>
    public class SitePersonalNumberGuardTests
    {
        [Theory]
        [InlineData("19480312-1234")]
        [InlineData("480312-1234")]
        [InlineData("480312+1234")]
        [InlineData("200101011234")]
        [InlineData("Ring Anna, 19480312-1234, om du undrar")]
        [InlineData("personnummer:19850630-0017.")]
        public void Personnummer_hittas(string text)
            => Assert.NotNull(SitePersonalNumberGuard.Find(text));

        [Theory]
        [InlineData("0701234567")]            // mobilnummer, tio siffror
        [InlineData("070-123 45 67")]
        [InlineData("+46701234567")]
        [InlineData("5814-1234")]             // plusgiro
        [InlineData("Bankgiro 123-4567")]
        [InlineData("123456-7")]
        [InlineData("19481312-1234")]         // månad 13
        [InlineData("19480332-1234")]         // dag 32
        [InlineData("19480300-1234")]         // dag 00
        [InlineData("1234567890123456")]      // sexton siffror, kontonummer
        [InlineData("3101-2026-11-07")]
        [InlineData("Tävling 2026-11-07")]
        [InlineData("")]
        [InlineData(null)]
        public void Telefon_bank_och_orimliga_datum_hittas_inte(string? text)
            => Assert.Null(SitePersonalNumberGuard.Find(text));

        [Fact]
        public void Ett_nummer_gömt_i_markup_hittas()
        {
            // Det som SYNS prövas: en inline-tagg mitt i siffrorna delar inte numret, en entitet avkodas.
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(new[] { "<p>Anna <strong>19480312</strong>-1234</p>" }));
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(new[] { "<p>Anna 1948<em>03</em>12-1234</p>" }));
            Assert.Null(SitePersonalNumberGuard.FindInAny(new[] { "<p>19480312</p><p>-1234</p>" }));   // två stycken är två texter
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(new[] { "<p>Anna &#49;9480312-1234</p>" }));
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(new[] { "<p title=\"x\">19480312-1234</p>" }));
            Assert.Null(SitePersonalNumberGuard.FindInAny(new[] { "<a href=\"/x/19480312-1234\">länk</a>" }));   // adressen syns inte som text
        }

        [Fact]
        public void FindInAny_prövar_varje_text_och_hoppar_över_null()
        {
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(new string?[] { null, "inget", "<p>480312-1234</p>" }));
            Assert.Null(SitePersonalNumberGuard.FindInAny(new string?[] { null, "inget", "<p>0701234567</p>" }));
        }

        [Fact]
        public void Träffen_returneras_så_att_meddelandet_kan_peka_ut_den()
            => Assert.Equal("19480312-1234", SitePersonalNumberGuard.Find("Kontakt: 19480312-1234 (tel)"));
    }
}
