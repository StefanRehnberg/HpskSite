using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Expertgruppen 2026-11-09 (K6): blocktypernas, panelernas och fältens NAMN är ett kontrakt med allt som redan är lagrat. <c>SiteBlockSchema.Parse</c> ger fel för en okänd
    /// blocktyp, ett okänt fält eller en okänd panel, och då faller HELA sidan, inte bara blocket. En omdöpt eller borttagen nyckel fäller alltså varje lagrad sida som använder den.
    /// Det här provet är ögonblicksbilden: ändrar du en rad måste du göra det medvetet, och en nyckel tas aldrig bort eller döps om utan en väg för det lagrade
    /// (en alias- eller migreringsväg). En NY nyckel kräver en ny rad här, så att regeln för den (personuppgift, publik, obligatorisk) är ett beslut och inte en bieffekt.
    /// </summary>
    public class SiteContractSnapshotTests
    {
        private static string B(bool v) => v ? "Y" : "n";

        [Fact]
        public void Blocktyperna_och_deras_fält_är_den_beslutade_mängden()
        {
            // namn | personuppgift | tillåten före/efter systemsida | bara krets | bara omvandlad sida | fält
            var expected = new[]
            {
                "text|n|Y|n|n|html",
                "heading|n|n|n|n|level,text",
                "links|n|Y|n|n|items",
                "documents|n|n|n|n|refs,title",
                "contact|Y|n|n|n|title",
                "events|n|n|n|n|count,title,type",
                "pagelink|n|n|n|n|key,label",
                "image|n|n|n|n|alt,caption,decorative,ref",
                "region-clubs|n|n|Y|n|title",
                "region-ranges|n|n|Y|n|title",
                "region-series|n|n|Y|n|count,title",
                "region-stats|n|n|Y|n|title",
                "news|n|Y|n|n|count,title",
                "about|n|n|n|n|title",
                "panel|n|n|n|Y|key,zone",
            };
            var actual = SiteBlockSchema.Types.Values
                .Select(t => $"{t.Name}|{B(t.Personal)}|{B(t.AllowedInSlot)}|{B(t.RegionOnly)}|{B(t.ConvertedOnly)}|{string.Join(",", t.Fields.OrderBy(f => f, StringComparer.Ordinal))}")
                .ToArray();
            Assert.Equal(expected.OrderBy(x => x, StringComparer.Ordinal), actual.OrderBy(x => x, StringComparer.Ordinal));
        }

        [Fact]
        public void Klubbens_paneler_är_den_beslutade_mängden()
        {
            // nyckel | zon | personuppgift | obligatorisk | värd | kan visas publikt
            var expected = new[]
            {
                "about-text|Main|n|n||n", "login-prompt|Side|n|Y||n", "contact-card|Side|n|n||n", "quicklink-docs|Side|n|Y||n",
                "whats-happening|Full|n|n||n", "upcoming-events|Main|n|n||n", "motions|Side|n|Y||n", "quicklinks|Side|n|n||n",
                "ranges|Full|n|n||Y", "calendar|Full|n|Y||Y", "competitions|Full|n|Y||Y", "instructors|Full|Y|n||n",
                "guldserie|Full|Y|n||n", "member-directory|Full|Y|Y||n", "records|Full|n|n||Y", "document-archive|Full|n|Y||n",
            };
            Assert.Equal(expected, PanelCatalog.Club.Select(Line).ToArray());
        }

        [Fact]
        public void Kretsens_paneler_är_den_beslutade_mängden()
        {
            var expected = new[]
            {
                "welcome|Full|n|n||n", "whats-happening|Full|n|n||n", "stats-card|Main|n|n||n", "motions|Main|n|Y||n",
                "upcoming-events|Main|n|n||n", "contact-card|Side|n|n||n", "quicklinks|Side|n|n||n", "quicklink-docs|Side|n|Y|quicklinks|n",
                "quicklink-records|Side|n|n|quicklinks|n", "champions|Side|n|n||n", "about-region|Full|n|n||n",
                "region-instructors|Full|Y|n||n", "competitions|Full|n|Y||n", "calendar|Full|n|Y||n", "records|Full|n|n||Y",
                "document-archive|Full|n|Y||n", "board|Full|Y|Y||n",
            };
            Assert.Equal(expected, PanelCatalog.Region.Select(Line).ToArray());
        }

        private static string Line(PanelDef d) => $"{d.Key}|{d.Zone}|{B(d.PersonalData)}|{B(d.Required)}|{d.HostedBy}|{B(d.PublicAllowed)}";
    }
}
