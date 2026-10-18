using FluentAssertions;
using HpskSite.Routing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Fas E: besökarnas gamla trädadresser bar Umbracos standardstavning av å/ä/ö (aa/ae/oe), och adresstabellen
    /// skrevs om till vår teckentabells (a/a/o) vid 17-deployen. Värdena är prods riktiga adresser (besöksloggen).
    /// </summary>
    public class CompetitionTreeLegacySpellingTests
    {
        [Theory]
        [InlineData("hallandsserien-omgaang-1-april", "hallandsserien-omgang-1-april")]
        [InlineData("vaexjoetraeffen-2025", "vaxjotraffen-2025")]
        [InlineData("OMGAANG", "omgang")]
        [InlineData("hallandsserien-2025", "hallandsserien-2025")]
        [InlineData("competitions", "competitions")]
        public void Gamla_stavningen_blir_tabellens(string old, string expected) =>
            CompetitionTreeRedirectFinder.LegacyAsciiFold(old).Should().BeEquivalentTo(expected);
    }
}
