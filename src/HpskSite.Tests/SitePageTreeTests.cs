using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Sidor under sidor: reglerna som ren kod (SitePageTree), trädordningen och hur menyn byggs. Inga databasanrop.</summary>
    public class SitePageTreeTests
    {
        private static SitePageRow Page(int id, int? parent = null, int sort = 10, string title = "Sida", string template = null!, SiteVisibility vis = SiteVisibility.Klubbmedlemmar, bool deleted = false, string owner = "Club", int ownerId = 1) =>
            new() { Id = id, ParentPageId = parent, SortOrder = sort, Title = title, TemplateKey = template, Visibility = (byte)vis, DeletedDate = deleted ? DateTime.Now : null, OwnerType = owner, OwnerId = ownerId };

        // ── Reglerna ───────────────────────────────────────────────────────────

        [Fact]
        public void En_egen_sida_kan_läggas_under_en_annan_egen_sida_på_egen_nivå()
        {
            Assert.Null(SitePageTree.ParentError(Page(2), Page(1), 0, false));
            Assert.Null(SitePageTree.ParentError(Page(2, parent: 1), Page(1), SitePageTree.MaxChildrenPerParent, false));   // redan under den: ingen ny plats behövs
        }

        [Fact]
        public void Bara_två_nivåer_en_sida_kan_inte_bli_sin_egen_förälder_eller_ligga_under_en_undersida()
        {
            Assert.NotNull(SitePageTree.ParentError(Page(1), Page(1), 0, false));                   // sig själv
            Assert.NotNull(SitePageTree.ParentError(Page(3), Page(2, parent: 1), 0, false));        // föräldern är själv en undersida: tre nivåer
            Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1), 0, pageHasChildren: true));    // en sida med undersidor kan inte själv bli en undersida
        }

        [Fact]
        public void Bara_egna_sidor_hos_samma_förening_och_inte_raderade_kan_vara_föräldrar()
        {
            Assert.NotNull(SitePageTree.ParentError(Page(2, template: "calendar"), Page(1), 0, false));                       // en systemsidas kopia kan inte flyttas
            Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1, template: "calendar"), 0, false));                       // en systemsida kan inte vara förälder (inte än)
            Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1, deleted: true), 0, false));
            Assert.NotNull(SitePageTree.ParentError(Page(2), null, 0, false));
            Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1, ownerId: 99), 0, false));                                // en annan förening
            Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1, owner: "Region"), 0, false));
        }

        [Fact]
        public void En_omgjord_Om_sida_kan_vara_förälder_men_ingen_annan_systemsida()
        {
            var about = Page(1, template: "about");
            Assert.Null(SitePageTree.ParentError(Page(2), about, 0, false, parentConverted: true));
            Assert.Null(SitePageTree.NewChildError("Club", 1, about, 0, parentConverted: true));
            Assert.NotNull(SitePageTree.ParentError(Page(2), about, 0, false, parentConverted: false));              // Om som inte är omgjord har ingen rad att peka på
            Assert.NotNull(SitePageTree.NewChildError("Club", 1, about, 0));
            foreach (var key in new[] { "home", "admin", "calendar", "documents", "members" })                         // bara Om först (Stefans väg A)
            {
                Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1, template: key), 0, false, parentConverted: true));
                Assert.NotNull(SitePageTree.NewChildError("Club", 1, Page(1, template: key), 0, parentConverted: true));
            }
            Assert.NotNull(SitePageTree.ParentError(Page(3), Page(2, parent: 1, template: "about"), 0, false, parentConverted: true));   // två nivåer gäller även här
            Assert.NotNull(SitePageTree.ParentError(Page(2), about, SitePageTree.MaxChildrenPerParent, false, parentConverted: true));
        }

        [Fact]
        public void En_sida_har_högst_så_många_undersidor_som_taket()
        {
            Assert.Null(SitePageTree.ParentError(Page(2), Page(1), SitePageTree.MaxChildrenPerParent - 1, false));
            Assert.NotNull(SitePageTree.ParentError(Page(2), Page(1), SitePageTree.MaxChildrenPerParent, false));
        }

        [Fact]
        public void En_ny_undersida_prövas_mot_föräldern_med_samma_regler()
        {
            Assert.Null(SitePageTree.NewChildError("Club", 1, Page(1), 3));
            Assert.NotNull(SitePageTree.NewChildError("Club", 1, null, 0));
            Assert.NotNull(SitePageTree.NewChildError("Club", 1, Page(1, deleted: true), 0));
            Assert.NotNull(SitePageTree.NewChildError("Club", 2, Page(1), 0));                  // föräldern hör till en annan förening
            Assert.NotNull(SitePageTree.NewChildError("Club", 1, Page(1, parent: 9), 0));        // föräldern är en undersida
            Assert.NotNull(SitePageTree.NewChildError("Club", 1, Page(1, template: "home"), 0));
            Assert.NotNull(SitePageTree.NewChildError("Club", 1, Page(1), SitePageTree.MaxChildrenPerParent));
        }

        [Theory]
        [InlineData(SiteVisibility.Klubbmedlemmar, SiteVisibility.Klubbmedlemmar, true)]
        [InlineData(SiteVisibility.Styrelse, SiteVisibility.Klubbmedlemmar, true)]
        [InlineData(SiteVisibility.Dold, SiteVisibility.Alla, true)]
        [InlineData(SiteVisibility.Alla, SiteVisibility.Klubbmedlemmar, false)]       // öppnare än föräldern
        [InlineData(SiteVisibility.Inloggade, SiteVisibility.Styrelse, false)]
        [InlineData(SiteVisibility.Klubbmedlemmar, SiteVisibility.Dold, false)]
        public void En_undersida_är_aldrig_öppnare_än_sin_förälder(SiteVisibility child, SiteVisibility parent, bool ok) =>
            Assert.Equal(ok, SitePageTree.ChildLevelOk(child, parent));

        // ── Trädordningen ──────────────────────────────────────────────────────

        private static List<(int Id, int Depth)> Flat(params SitePageRow[] rows) =>
            SitePageTree.Flatten(rows, r => r.Id, r => r.ParentPageId, r => r.SortOrder, r => r.Title).Select(t => (t.Item.Id, t.Depth)).ToList();

        [Fact]
        public void Sidor_på_egen_nivå_följs_av_sina_undersidor_i_ordning()
        {
            var flat = Flat(Page(1, sort: 20, title: "B"), Page(2, sort: 10, title: "A"), Page(3, parent: 1, sort: 20, title: "B2"), Page(4, parent: 1, sort: 10, title: "B1"), Page(5, parent: 2, sort: 10, title: "A1"));
            Assert.Equal(new[] { (2, 0), (5, 1), (1, 0), (4, 1), (3, 1) }, flat);
        }

        [Fact]
        public void Lika_ordning_avgörs_av_titeln_och_sedan_id()
        {
            Assert.Equal(new[] { 2, 3, 1 }, Flat(Page(1, sort: 10, title: "Ö"), Page(2, sort: 10, title: "A"), Page(3, sort: 10, title: "B")).Select(t => t.Id));
            Assert.Equal(new[] { 1, 2 }, Flat(Page(2, sort: 10, title: "Lika"), Page(1, sort: 10, title: "Lika")).Select(t => t.Id));
        }

        [Fact]
        public void Ingen_sida_försvinner_ur_listan_hur_trasig_strukturen_än_är()
        {
            // en undersida vars förälder saknas, en tredje nivå och en sida som pekar på sig själv: alla visas
            var flat = Flat(Page(1), Page(2, parent: 99), Page(3, parent: 1), Page(4, parent: 3), Page(5, parent: 5));
            Assert.Equal(new[] { 1, 2, 3, 4, 5 }.OrderBy(x => x), flat.Select(t => t.Id).OrderBy(x => x));
            Assert.Equal(5, flat.Count);
            Assert.Contains((1, 0), flat); Assert.Contains((3, 1), flat); Assert.Contains((2, 0), flat);
        }

        // ── Menyn ──────────────────────────────────────────────────────────────

        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);

        private static IReadOnlyList<SiteMenuPage> Tree(params SiteMenuPage[] p) => p;
        private static List<string> Keys(SitePageView v) => v.Menu().Where(e => e.Page.Key.StartsWith("page:")).Select(e => e.Page.Key).ToList();
        private static SitePageView Club(SiteViewer v, IReadOnlyList<SiteMenuPage> pages, int? free = null) => SitePageView.On(SiteTemplates.Club, "/halland/ankeborg/", "home", v, pages, free);

        [Fact]
        public void Undersidor_ligger_direkt_efter_sin_förälder_i_menyn_och_bär_förälderns_id()
        {
            var pages = Tree(new(1, "Om oss", "om-oss", SiteVisibility.Alla, 10), new(3, "Bli medlem", "bli-medlem", SiteVisibility.Alla, 20),
                             new(2, "Vår historia", "var-historia", SiteVisibility.Alla, 10, ParentPageId: 1), new(4, "Stadgar", "stadgar", SiteVisibility.Alla, 20, ParentPageId: 1));
            var entries = Club(Member, pages).Menu().Where(e => e.Page.Key.StartsWith("page:")).ToList();
            Assert.Equal(new[] { "page:1", "page:2", "page:4", "page:3" }, entries.Select(e => e.Page.Key));
            Assert.Equal(new int?[] { null, 1, 1, null }, entries.Select(e => e.ParentPageId));
            Assert.Equal(new int?[] { 1, 2, 4, 3 }, entries.Select(e => e.FreePageId));
        }

        [Fact]
        public void En_låst_förälder_visar_inga_undersidor_så_ingen_etikett_läcker()
        {
            var pages = Tree(new(1, "Medlemmar", "medlemmar", SiteVisibility.Klubbmedlemmar, 10), new(2, "Hemlig undersida", "hemlig", SiteVisibility.Klubbmedlemmar, 10, ParentPageId: 1));
            Assert.Equal(new[] { "page:1" }, Keys(Club(Logged, pages)));                                    // en inloggad icke-medlem ser föräldern med hänglås, inget under den
            Assert.True(Club(Logged, pages).Menu().Single(e => e.Page.Key == "page:1").Locked);
            Assert.Equal(new[] { "page:1", "page:2" }, Keys(Club(Member, pages)));
        }

        [Fact]
        public void En_undersida_visas_aldrig_för_den_som_inte_får_se_den_och_aldrig_utan_sin_förälder()
        {
            var pages = Tree(new(1, "Öppen", "oppen", SiteVisibility.Alla, 10), new(2, "Bara styrelsen", "styrelse-sida", SiteVisibility.Styrelse, 10, ParentPageId: 1),
                             new(3, "Föräldralös", "foraldralos", SiteVisibility.Alla, 10, ParentPageId: 99));
            Assert.Equal(new[] { "page:1" }, Keys(Club(Member, pages)));
            Assert.Equal(new[] { "page:1" }, Keys(Club(Anon, pages)));    // en utloggad ser bara det ägaren gett nivån Alla (inte styrelsens undersida, inte den föräldralösa)
            Assert.DoesNotContain("page:3", Keys(Club(Member, pages)));   // förälder som inte finns i menyn: undersidan hänger inte i luften
        }

        [Fact]
        public void Undersidor_till_Om_ligger_direkt_efter_systemposten_Om_och_bär_dess_nyckel()
        {
            var pages = Tree(new(2, "Historia", "historia", SiteVisibility.Klubbmedlemmar, 20, ParentPageId: 900, ParentTemplateKey: "about"),
                             new(1, "Bli medlem", "bli-medlem", SiteVisibility.Klubbmedlemmar, 10, ParentPageId: 900, ParentTemplateKey: "about"),
                             new(3, "Egen sida", "egen", SiteVisibility.Klubbmedlemmar, 10));
            var all = Club(Member, pages).Menu().ToList();
            var i = all.FindIndex(e => e.Page.Key == "about");
            Assert.True(i >= 0, "Om-posten finns");
            Assert.Equal(new[] { "page:1", "page:2" }, all.Skip(i + 1).Take(2).Select(e => e.Page.Key));                     // i sin ordning, direkt efter Om
            Assert.All(all.Skip(i + 1).Take(2), e => Assert.Equal("about", e.ParentKey));
            Assert.Null(all.Single(e => e.Page.Key == "page:3").ParentKey);                                                    // en vanlig egen sida är inte under Om
        }

        [Fact]
        public void Undersidor_till_Om_visas_inte_under_en_låst_Om_eller_för_den_som_inte_får_se_dem()
        {
            var pages = Tree(new(1, "Stadgar", "stadgar", SiteVisibility.Styrelse, 10, ParentPageId: 900, ParentTemplateKey: "about"),
                             new(2, "Historia", "historia", SiteVisibility.Klubbmedlemmar, 20, ParentPageId: 900, ParentTemplateKey: "about"));
            Assert.Equal(new[] { "page:2" }, Keys(Club(Member, pages)));                                                       // styrelsesidan syns inte för en medlem
            Assert.Empty(Keys(Club(SiteViewer.Anonymous, pages)));                                                              // utloggad ser ingen etikett
        }

        [Fact]
        public void Den_aktuella_undersidan_är_aktuell_och_föräldern_är_det_inte()
        {
            var pages = Tree(new(1, "Om oss", "om-oss", SiteVisibility.Alla, 10), new(2, "Historia", "historia", SiteVisibility.Alla, 10, ParentPageId: 1));
            var entries = Club(Member, pages, free: 2).Menu().Where(e => e.Page.Key.StartsWith("page:")).ToList();
            Assert.False(entries.Single(e => e.FreePageId == 1).Current);
            Assert.True(entries.Single(e => e.FreePageId == 2).Current);
        }
    }
}
