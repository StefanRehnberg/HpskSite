using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna steg 1 (plan v4, 2.6): vad vyn får veta. Av = dagens vy oförändrad. På = rotadressen visar Hem (och Admin
    /// för ägarens admin) med dagens flik-id:n, varje annan sida är en egen adress med bara sin egen panel.
    /// </summary>
    public class SitePageViewTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer LoggedIn = new(true, false, false, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);
        private const string Url = "/halland/haaplinge-goass/";

        private static string[] Shown(SitePageView v, params string[] panes) => panes.Where(v.ShowsPane).ToArray();

        private static readonly string[] ClubPanes =
            { "clubHome", "clubAbout", "clubRanges", "clubCalendar", "clubCompetitions", "clubMembers", "clubRecords", "clubDocuments", "clubAdmin" };

        // ── Av ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Av_visar_alla_paneler_och_dagens_klasser()
        {
            var v = SitePageView.Off(Url);
            Assert.False(v.Enabled);
            Assert.Equal(ClubPanes, Shown(v, ClubPanes));
            Assert.Equal("show active", v.PaneClass("clubHome"));
            Assert.Equal("show active", v.PaneClass("regionHome"));
            Assert.Equal("", v.PaneClass("clubRanges"));
            Assert.Equal("", v.PaneClass("clubAdmin"));
            Assert.Empty(v.Menu());
        }

        [Fact]
        public void Av_bär_den_verkliga_tittaren_så_att_en_inloggad_inte_får_klubbens_utloggade_Hem()
        {
            // Felet (2026-11-12, Joakim Öman): For() gav Off(ownerUrl) = anonym tittare, och vyerna ritar panelerna med sp.Viewer —
            // en inloggad klubbadmin fick Hems utloggade lista med inloggningsruta så länge strukturen var AV.
            var v = SitePageView.Off(Url, Admin);
            Assert.Equal(Admin, v.Viewer);
            var ctx = new SitePanelContext(SiteTemplates.Club, 1, "x", null!, v.Viewer);
            Assert.True(ctx.LoggedIn);
            Assert.DoesNotContain(PanelCatalog.Standard(SiteTemplates.Club, "home", ctx.LoggedIn), p => p.Key == "login-prompt");
            Assert.Equal(Anon, SitePageView.Off(Url).Viewer);   // utan tittare = anonym som förut
        }

        [Fact]
        public void For_ger_Off_den_verkliga_tittaren()
        {
            var src = File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(SourceFile())!, "..", "HpskSite", "Services", "SitePages", "SitePageView.cs")));
            Assert.Contains("return SitePageView.Off(ownerUrl, viewer);", src);
            Assert.DoesNotContain("SitePageView.Off(ownerUrl);", src);
        }

        private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string here = "") => here;

        // ── På: klubben ────────────────────────────────────────────────────────

        [Fact]
        public void På_rotadressen_visar_Hem_men_inte_Admin_för_en_vanlig_inloggad()
        {
            var v = SitePageView.On(SiteTemplates.Club, Url, null, LoggedIn);
            Assert.True(v.IsRoot);
            Assert.Equal(new[] { "clubHome" }, Shown(v, ClubPanes));
            Assert.Equal("show active", v.PaneClass("clubHome"));
        }

        [Fact]
        public void På_rotadressen_visar_Hem_och_Admin_för_ägarens_admin_och_Admin_startar_inaktiv()
        {
            var v = SitePageView.On(SiteTemplates.Club, Url, "home", Admin);
            Assert.Equal(new[] { "clubHome", "clubAdmin" }, Shown(v, ClubPanes));
            Assert.Equal("show active", v.PaneClass("clubHome"));
            Assert.Equal("", v.PaneClass("clubAdmin"));
        }

        [Fact]
        public void På_en_undersida_visas_bara_den_sidans_panel_och_aldrig_Admin()
        {
            var v = SitePageView.On(SiteTemplates.Club, Url, "ranges", Admin);
            Assert.False(v.IsRoot);
            Assert.Equal(new[] { "clubRanges" }, Shown(v, ClubPanes));
            Assert.Equal("show active", v.PaneClass("clubRanges"));
            Assert.Equal("", v.PaneClass("clubHome"));
        }

        [Fact]
        public void En_utloggad_ser_ingen_panel_på_en_inloggad_undersida()
        {
            var v = SitePageView.On(SiteTemplates.Club, Url, "calendar", Anon);
            Assert.Empty(Shown(v, ClubPanes));
        }

        [Fact]
        public void En_utloggad_ser_Hem_på_rotadressen_som_idag()
        {
            var v = SitePageView.On(SiteTemplates.Club, Url, "home", Anon);
            Assert.Equal(new[] { "clubHome" }, Shown(v, ClubPanes));
            Assert.Equal("home", v.Menu().Single(e => !e.Locked).Page.Key); // Besökarens vy: Hem öppen, övriga sidor med hänglås (ingen sidas innehåll visas)
        }

        [Theory]
        [InlineData("admin")]
        [InlineData("okand")]
        [InlineData(null)]
        public void Admin_och_okända_nycklar_blir_Hem_aldrig_en_sida_som_inte_finns(string? key)
        {
            var v = SitePageView.On(SiteTemplates.Club, Url, key, Admin);
            Assert.True(v.IsRoot);
            Assert.Equal("home", v.PageKey);
        }

        [Fact]
        public void Menyn_på_roten_har_Hem_och_Admin_som_flikar_och_resten_som_länkar()
        {
            var menu = SitePageView.On(SiteTemplates.Club, Url, "home", Admin).Menu();
            Assert.Equal(9, menu.Count);
            Assert.True(menu.Single(m => m.Page.Key == "home").IsTabButton);
            Assert.True(menu.Single(m => m.Page.Key == "admin").IsTabButton);
            Assert.All(menu.Where(m => m.Page.Key is not ("home" or "admin")), m => Assert.False(m.IsTabButton));
            Assert.Equal(Url + "banor/", menu.Single(m => m.Page.Key == "ranges").Href);
            Assert.Equal(Url + "vara-tavlingar/", menu.Single(m => m.Page.Key == "competitions").Href);
            Assert.Equal(Url + "#clubAdmin", menu.Single(m => m.Page.Key == "admin").Href);
            Assert.Equal(Url, menu.Single(m => m.Page.Key == "home").Href);
            Assert.Equal("home", menu.Single(m => m.Current).Page.Key);
        }

        [Fact]
        public void Menyn_på_en_undersida_är_bara_länkar_och_Admin_går_till_rotens_hash()
        {
            var menu = SitePageView.On(SiteTemplates.Club, Url, "ranges", Admin).Menu();
            Assert.All(menu, m => Assert.False(m.IsTabButton));
            Assert.Equal("ranges", menu.Single(m => m.Current).Page.Key);
            Assert.Equal(Url + "#clubAdmin", menu.Single(m => m.Page.Key == "admin").Href);
            Assert.Equal(Url, menu.Single(m => m.Page.Key == "home").Href);
        }

        [Fact]
        public void Admin_syns_inte_i_menyn_för_en_vanlig_inloggad()
        {
            var menu = SitePageView.On(SiteTemplates.Club, Url, "home", LoggedIn).Menu();
            Assert.Equal(8, menu.Count);
            Assert.DoesNotContain(menu, m => m.Page.IsAdmin);
        }

        [Theory]
        [InlineData("/halland/x", "/halland/x/")]
        [InlineData("/halland/x/", "/halland/x/")]
        [InlineData("", "/")]
        [InlineData("#", "/")]
        public void Ägarens_adress_normaliseras_med_snedstreck(string input, string expected) =>
            Assert.Equal(expected, SitePageView.Off(input).OwnerUrl);

        // ── På: kretsen ────────────────────────────────────────────────────────

        private static readonly string[] RegionPanes =
            { "regionHome", "regionAbout", "regionCompetitions", "regionCalendar", "regionRecords", "regionDocuments", "regionBoard", "regionAdmin" };

        [Fact]
        public void Kretsen_utloggad_har_fyra_publika_sidor_och_ser_Rekord_Dokument_och_Styrelsen_bara_låsta()
        {
            var home = SitePageView.On(SiteTemplates.Region, "/halland/", "home", Anon);
            Assert.Equal(4, home.Menu().Count(e => !e.Locked));
            Assert.Equal(new[] { "records", "documents", "board" }, home.Menu().Where(e => e.Locked).Select(e => e.Page.Key).OrderBy(k => new[] { "records", "documents", "board" }.ToList().IndexOf(k)));
            Assert.Equal(new[] { "regionHome" }, Shown(home, RegionPanes));
            Assert.Equal(new[] { "regionAbout" }, Shown(SitePageView.On(SiteTemplates.Region, "/halland/", "about", Anon), RegionPanes));
            Assert.Empty(Shown(SitePageView.On(SiteTemplates.Region, "/halland/", "records", Anon), RegionPanes));
            Assert.Empty(Shown(SitePageView.On(SiteTemplates.Region, "/halland/", "board", Anon), RegionPanes));
        }

        [Fact]
        public void Kretsens_Styrelse_är_regionBoard_och_Admin_regionAdmin()
        {
            Assert.Equal("regionBoard", SiteTemplates.LegacyPaneId(SiteTemplates.Region, "board"));
            Assert.Equal("regionAdmin", SiteTemplates.LegacyPaneId(SiteTemplates.Region, "admin"));
            Assert.Equal("clubAdmin", SiteTemplates.LegacyPaneId(SiteTemplates.Club, "admin"));
            var admin = SitePageView.On(SiteTemplates.Region, "/halland/", "home", Admin);
            Assert.Contains("regionAdmin", Shown(admin, RegionPanes));
        }

        // ── Kontraktet mot dagens skal ─────────────────────────────────────────

        [Fact]
        public void Varje_mallsida_har_ett_panel_id_i_dagens_skal()
        {
            foreach (var t in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
                foreach (var p in t.Pages)
                {
                    var id = SiteTemplates.LegacyPaneId(t.OwnerType, p.Key);
                    Assert.Matches("^(club|region)[A-Z][A-Za-z]+$", id);
                }
            Assert.Throws<ArgumentOutOfRangeException>(() => SiteTemplates.LegacyPaneId(SiteTemplates.Club, "nonexistent"));
        }

        // ── Fabriken ───────────────────────────────────────────────────────────

        private sealed class Owners : SiteOwnerService
        {
            private readonly HashSet<string> _on;
            public Owners(params string[] on) : base(NullLogger<SiteOwnerService>.Instance, null) { _on = new HashSet<string>(on); }
            protected override HashSet<string> LoadEnabled() => _on;
        }

        private sealed class Accessor : IHttpContextAccessor
        {
            public HttpContext? HttpContext { get; set; } = new DefaultHttpContext();
        }

        [Fact]
        public void Fabriken_ger_Av_för_en_ägare_som_inte_är_påslagen_även_om_findern_pekat_ut_en_sida()
        {
            var acc = new Accessor();
            acc.HttpContext!.Items[SitePageRequest.ItemKey] = new SitePageRequest(SiteTemplates.Club, 5, "ranges");
            var f = new SitePageViewFactory(new Owners(), acc);
            Assert.False(f.For(SiteTemplates.Club, 5, Url, LoggedIn).Enabled);
        }

        [Fact]
        public void Fabriken_läser_sidan_ur_findern_men_bara_för_samma_ägare()
        {
            var acc = new Accessor();
            acc.HttpContext!.Items[SitePageRequest.ItemKey] = new SitePageRequest(SiteTemplates.Club, 5, "ranges");
            var f = new SitePageViewFactory(new Owners(SiteOwnerService.Key(SiteTemplates.Club, 5), SiteOwnerService.Key(SiteTemplates.Club, 6)), acc);
            Assert.Equal("ranges", f.For(SiteTemplates.Club, 5, Url, LoggedIn).PageKey);
            Assert.Equal("home", f.For(SiteTemplates.Club, 6, Url, LoggedIn).PageKey);   // ett annat id: inte den här begäran
            Assert.Equal("home", f.For(SiteTemplates.Region, 5, Url, LoggedIn).PageKey); // sorten följer id:t
        }

        [Fact]
        public void Fabriken_utan_Items_är_rotadressen()
        {
            var f = new SitePageViewFactory(new Owners(SiteOwnerService.Key(SiteTemplates.Club, 5)), new Accessor());
            var v = f.For(SiteTemplates.Club, 5, Url, LoggedIn);
            Assert.True(v.Enabled);
            Assert.True(v.IsRoot);
        }

        [Fact]
        public void Fabriken_utan_HttpContext_kastar_inte()
        {
            var f = new SitePageViewFactory(new Owners(SiteOwnerService.Key(SiteTemplates.Club, 5)), new Accessor { HttpContext = null });
            Assert.True(f.For(SiteTemplates.Club, 5, Url, LoggedIn).IsRoot);
        }
    }
}
