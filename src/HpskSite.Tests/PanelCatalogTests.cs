using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Konvertera-planen K0b: panelregistret täcker exakt mallens blocknycklar, och standardlistorna ritar dem.</summary>
    public class PanelCatalogTests
    {
        public static IEnumerable<object[]> Owners() => new[] { new object[] { SiteTemplates.Club }, new object[] { SiteTemplates.Region } };

        private static IEnumerable<string> TemplateKeys(string owner) =>
            SiteTemplates.For(owner).Pages.Where(p => !p.IsAdmin).SelectMany(p => p.Blocks).Select(b => b.Key).Distinct();

        [Theory, MemberData(nameof(Owners))]
        public void Registret_täcker_varje_blocknyckel_i_mallen(string owner)
        {
            var registry = PanelCatalog.For(owner).Select(p => p.Key).ToHashSet();
            foreach (var k in TemplateKeys(owner)) Assert.True(registry.Contains(k), $"{owner}: mallens block '{k}' saknas i registret");
        }

        [Theory, MemberData(nameof(Owners))]
        public void Registret_har_ingen_panel_som_mallen_saknar(string owner)
        {
            var template = TemplateKeys(owner).ToHashSet();
            foreach (var p in PanelCatalog.For(owner)) Assert.True(template.Contains(p.Key), $"{owner}: registrets panel '{p.Key}' finns inte i mallen");
        }

        [Theory, MemberData(nameof(Owners))]
        public void Varje_panel_ritas_av_en_standardlista_eller_av_sin_värd(string owner)
        {
            var listed = PanelCatalog.ListedKeys(owner);
            foreach (var p in PanelCatalog.For(owner))
            {
                if (p.HostedBy != null)
                {
                    Assert.True(listed.Contains(p.HostedBy), $"{owner}: '{p.Key}' har värden '{p.HostedBy}' som ingen lista ritar");
                    continue;
                }
                // Ritas av sidhuvudet (ingen delvy) eller av en lista.
                Assert.True(p.Partial == null || listed.Contains(p.Key), $"{owner}: '{p.Key}' ritas av ingen standardlista");
            }
        }

        [Theory, MemberData(nameof(Owners))]
        public void Varje_standardpanel_har_en_delvy_som_finns_på_disk(string owner)
        {
            var views = FindViewsDir();
            foreach (var p in PanelCatalog.For(owner).Where(x => x.Partial != null))
            {
                var path = Path.Combine(views, p.Partial!.Replace("~/Views/", "").Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{owner}: delvyn för '{p.Key}' saknas: {path}");
            }
        }

        [Theory, MemberData(nameof(Owners))]
        public void Standardlistorna_ger_bara_nycklar_som_mallens_sida_äger(string owner)
        {
            var template = SiteTemplates.For(owner);
            foreach (var page in template.Pages.Where(p => !p.IsAdmin))
                foreach (var loggedIn in new[] { true, false })
                    foreach (var placement in PanelCatalog.Standard(owner, page.Key, loggedIn))
                        Assert.Contains(page.Blocks, b => b.Key == placement.Key);
        }

        [Fact]
        public void Obligatoriska_paneler_enligt_beslut_3()
        {
            foreach (var k in new[] { "motions", "calendar", "competitions", "document-archive", "member-directory", "quicklink-docs", "login-prompt" })
                Assert.True(PanelCatalog.Find(SiteTemplates.Club, k)!.Required, $"klubb: {k}");
            Assert.True(PanelCatalog.Find(SiteTemplates.Region, "board")!.Required);
        }

        [Fact]
        public void Personuppgiftspaneler_följer_mallens_flagga()
        {
            foreach (var owner in new[] { SiteTemplates.Club, SiteTemplates.Region })
                foreach (var b in SiteTemplates.For(owner).Pages.SelectMany(p => p.Blocks).Where(b => b.PersonalData))
                    Assert.True(PanelCatalog.Find(owner, b.Key)!.PersonalData, $"{owner}: {b.Key}");
        }

        [Fact]
        public void Hem_ritar_två_kolumner_men_andra_sidor_ritas_i_hela_bredden()
        {
            Assert.Contains(PanelCatalog.Standard(SiteTemplates.Club, "home", true), p => p.Zone == PanelZone.Main);
            Assert.Contains(PanelCatalog.Standard(SiteTemplates.Club, "home", true), p => p.Zone == PanelZone.Side);
            Assert.All(PanelCatalog.Standard(SiteTemplates.Club, "members", true), p => Assert.Equal(PanelZone.Full, p.Zone));
        }

        private static string FindViewsDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "HpskSite", "Views");
                if (Directory.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir.FullName, "src", "HpskSite", "Views");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Hittar inte Views");
        }
    }
}
