using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2b: ägarens egna sidor i menyn (ren sammanslagning i SitePageView.Menu).</summary>
    public class SiteMenuPagesTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);

        private static readonly SiteMenuPage[] Extra =
        {
            new(11, "Banregler", "banregler", SiteVisibility.Klubbmedlemmar, 20),
            new(10, "Hitta hit", "hitta-hit", SiteVisibility.Alla, 10),
            new(12, "Styrelsens pärm", "parm", SiteVisibility.Styrelse, 30),
        };

        private static SitePageView Club(SiteViewer v, int? free = null, string key = "home") =>
            SitePageView.On(SiteTemplates.Club, "/halland/ankeborg/", key, v, Extra, free);

        [Fact]
        public void Egna_sidor_kommer_efter_mallens_poster_och_före_Admin_i_menyns_ordning()
        {
            var keys = Club(Admin).Menu().Select(e => e.Page.Key).ToList();
            Assert.Equal("admin", keys[^1]);
            var firstExtra = keys.FindIndex(k => k.StartsWith("page:"));
            Assert.True(firstExtra > 0 && firstExtra < keys.Count - 1);
            Assert.Equal(new[] { "page:10", "page:11", "page:12" }, keys.Where(k => k.StartsWith("page:")));   // sorterade på menyns ordning (10, 20, 30); ägarens admin ser även styrelsens sida
        }

        [Fact]
        public void En_egen_sida_syns_bara_för_den_som_får_se_den()
        {
            Assert.DoesNotContain(Club(Logged).Menu(), e => e.Page.Key == "page:11");   // medlemssida: inloggad icke-medlem ser den inte i menyn
            Assert.Contains(Club(Member).Menu(), e => e.Page.Key == "page:11");
            Assert.Contains(Club(Logged).Menu(), e => e.Page.Key == "page:10");         // Alla-sidan syns för alla inloggade
            Assert.Contains(Club(new SiteViewer(true, true, true, false)).Menu(), e => e.Page.Key == "page:12");   // styrelsen ser sin sida
        }

        [Fact]
        public void Klubbens_utloggade_har_ingen_meny_alls_så_ingen_etikett_läcker()
            => Assert.Empty(Club(Anon).Menu());

        [Fact]
        public void Kretsens_utloggade_ser_bara_Alla_sidor()
        {
            var region = SitePageView.On(SiteTemplates.Region, "/halland/", "home", Anon, Extra);
            var extras = region.Menu().Where(e => e.Page.Key.StartsWith("page:")).Select(e => e.Page.Key).ToList();
            Assert.Equal(new[] { "page:10" }, extras);
        }

        [Fact]
        public void På_en_egen_sida_är_den_aktuell_och_ingen_systempost_är_det_och_Hem_är_en_länk()
        {
            var menu = Club(Member, free: 11).Menu();
            Assert.Equal(new[] { "page:11" }, menu.Where(e => e.Current).Select(e => e.Page.Key));
            var home = menu.Single(e => e.Page.Key == "home");
            Assert.False(home.IsTabButton);                                   // Hem är en länk när man står på en annan adress
            Assert.Equal("/halland/ankeborg/", home.Href);
            Assert.Equal("/halland/ankeborg/banregler/", menu.Single(e => e.Page.Key == "page:11").Href);
        }

        [Fact]
        public void På_en_systemsida_är_ingen_egen_sida_aktuell()
        {
            var menu = Club(Member, key: "calendar").Menu();
            Assert.Equal(new[] { "calendar" }, menu.Where(e => e.Current).Select(e => e.Page.Key));
        }

        [Fact]
        public void Utan_egna_sidor_är_menyn_som_förut()
        {
            var plain = SitePageView.On(SiteTemplates.Club, "/halland/ankeborg/", "home", Member).Menu().Select(e => e.Page.Key).ToList();
            Assert.Equal(SiteTemplates.ClubTemplate.MenuFor(Member).Select(p => p.Key).ToList(), plain.OrderBy(k => SiteTemplates.ClubTemplate.PageByKey(k)!.Order).ToList());
            Assert.DoesNotContain(plain, k => k.StartsWith("page:"));
        }

        [Fact]
        public void Hem_är_flik_på_rotadressen_även_med_egna_sidor()
        {
            var home = Club(Member).Menu().Single(e => e.Page.Key == "home");
            Assert.True(home.IsTabButton);
        }
    }
}
