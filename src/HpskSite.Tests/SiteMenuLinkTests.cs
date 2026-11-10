using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Externa länkar i menyn: reglerna (ren kod), vem som ser dem (nivå per länk, en meny som kräver inloggning) och lagringen mot SQL (en ägare, id-lagen, taket, ordningen, loggen).</summary>
    public class SiteMenuLinkRulesTests
    {
        [Theory]
        [InlineData("Skjuthandboken", "https://skjuthandboken.se/")]
        [InlineData("Facebook", "https://www.facebook.com/min.klubb?ref=x#y")]
        [InlineData("  Webbshop  ", "  https://shop.example.se/a/b  ")]
        public void En_vanlig_https_adress_godtas_och_trimmas(string label, string url)
        {
            var r = SiteMenuLinkRules.Check(label, url, 0);
            Assert.True(r.Ok, r.Error);
            Assert.Equal(label.Trim(), r.Label);
            Assert.Equal(url.Trim(), r.Url);
        }

        [Theory]
        [InlineData("http://exempel.se/")]                  // inte krypterad
        [InlineData("javascript:alert(1)")]
        [InlineData("data:text/html,<script>1</script>")]
        [InlineData("//exempel.se/")]                       // schemalös: samma värd, annat schema
        [InlineData("/halland/x/")]                         // intern adress hör till sidor, inte till en extern länk
        [InlineData("ftp://exempel.se/")]
        [InlineData("https://user:pass@exempel.se/")]       // inloggningsuppgifter i adressen
        [InlineData("https://exempel.se/a b")]
        [InlineData("https:\\\\exempel.se")]
        [InlineData("https://localhost/")]
        [InlineData("https://intranet/")]                   // ingen punkt i värdnamnet
        [InlineData("https://192.168.0.1/")]
        [InlineData("https://[::1]/")]
        [InlineData("https://mitt.local/")]
        [InlineData("https://")]
        [InlineData("exempel.se")]
        [InlineData("")]
        public void Otillåtna_adresser_vägras(string url) => Assert.False(SiteMenuLinkRules.Check("Namn", url, 0).Ok);

        [Fact]
        public void Namn_och_adress_har_gränser_och_synligheten_måste_vara_vald()
        {
            Assert.False(SiteMenuLinkRules.Check("", "https://exempel.se/", 0).Ok);
            Assert.False(SiteMenuLinkRules.Check("   ", "https://exempel.se/", 0).Ok);
            Assert.False(SiteMenuLinkRules.Check(new string('x', 81), "https://exempel.se/", 0).Ok);
            Assert.True(SiteMenuLinkRules.Check(new string('x', 80), "https://exempel.se/", 0).Ok);
            Assert.False(SiteMenuLinkRules.Check("a\tb", "https://exempel.se/", 0).Ok);
            Assert.False(SiteMenuLinkRules.Check("Namn", "https://exempel.se/" + new string('a', 500), 0).Ok);
            Assert.False(SiteMenuLinkRules.Check("Namn", "https://exempel.se/", -1).Ok);
            Assert.False(SiteMenuLinkRules.Check("Namn", "https://exempel.se/", 5).Ok);
            for (var v = 0; v <= 4; v++) Assert.True(SiteMenuLinkRules.Check("Namn", "https://exempel.se/", v).Ok);
        }
    }

    public class SiteMenuLinkViewTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Outsider = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Board = new(true, true, true, false);
        private static readonly SiteViewer Admin = new(true, true, true, true);

        private static readonly SiteExternalLink[] Links =
        {
            new(1, "KANARIE-ALLA", "https://a.example.se/", SiteVisibility.Alla),
            new(2, "KANARIE-INLOGGADE", "https://b.example.se/", SiteVisibility.Inloggade),
            new(3, "KANARIE-MEDLEM", "https://c.example.se/", SiteVisibility.Klubbmedlemmar),
            new(4, "KANARIE-STYRELSE", "https://d.example.se/", SiteVisibility.Styrelse),
            new(5, "KANARIE-ADMIN", "https://e.example.se/", SiteVisibility.Dold),
        };

        private static string[] Seen(string ownerType, SiteViewer v) =>
            SitePageView.On(ownerType, "/halland/x/", "home", v, links: Links).Links().Select(l => l.Label).ToArray();

        [Fact]
        public void Kretsens_meny_visar_varje_länk_för_den_som_nivån_släpper_in_och_ingen_annan()
        {
            Assert.Equal(new[] { "KANARIE-ALLA" }, Seen(SiteTemplates.Region, Anon));
            Assert.Equal(new[] { "KANARIE-ALLA", "KANARIE-INLOGGADE" }, Seen(SiteTemplates.Region, Outsider));
            Assert.Equal(new[] { "KANARIE-ALLA", "KANARIE-INLOGGADE", "KANARIE-MEDLEM" }, Seen(SiteTemplates.Region, Member));
            Assert.Equal(new[] { "KANARIE-ALLA", "KANARIE-INLOGGADE", "KANARIE-MEDLEM", "KANARIE-STYRELSE" }, Seen(SiteTemplates.Region, Board));
            Assert.Equal(5, Seen(SiteTemplates.Region, Admin).Length);
        }

        [Fact]
        public void En_klubbmeny_som_kräver_inloggning_visar_ingen_länk_för_en_utloggad_men_samma_nivåer_för_inloggade()
        {
            Assert.Empty(Seen(SiteTemplates.Club, Anon));   // ingen fri text till anonyma i en meny som kräver inloggning
            Assert.Equal(new[] { "KANARIE-ALLA", "KANARIE-INLOGGADE" }, Seen(SiteTemplates.Club, Outsider));
            Assert.Equal(new[] { "KANARIE-ALLA", "KANARIE-INLOGGADE", "KANARIE-MEDLEM" }, Seen(SiteTemplates.Club, Member));
        }

        [Fact]
        public void Ordningen_är_den_lagrade_och_utan_länkar_ger_menyn_inga()
        {
            var shuffled = new[] { Links[2], Links[0], Links[1] };
            var seen = SitePageView.On(SiteTemplates.Region, "/halland/x/", "home", Member, links: shuffled).Links().Select(l => l.Id).ToArray();
            Assert.Equal(new[] { 3, 1, 2 }, seen);   // vyn ändrar aldrig ordningen: den lagrade listan är sorterad av lagret
            Assert.Empty(SitePageView.On(SiteTemplates.Region, "/halland/x/", "home", Member).Links());
            Assert.Empty(SitePageView.Off("/halland/x/").Links());
        }
    }

    [Collection(SiteSqlCollection.Name)]
    public class SiteMenuLinkStoreTests
    {
        private const string Club = "Club";
        private const int Owner = 990011;
        private const int OtherOwner = 990012;
        private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);
        private static readonly SiteActor Actor = new(7, AsSupport: false);

        private static void SeedOwners(IDatabase db)
        {
            foreach (var id in new[] { Owner, OtherOwner })
                db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id);
        }

        private static int Add(IDatabase db, string label, string url = "https://exempel.se/", int vis = 0, int owner = Owner)
        {
            var r = SiteMenuLinkWriter.Save(db, Club, owner, null, label, url, vis, Actor, Now);
            Assert.True(r.Ok, r.Message);
            return r.Id!.Value;
        }

        [SqlFact]
        public void Länkar_läggs_sist_i_ordning_och_listan_är_sorterad()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var a = Add(f.Db, "A"); var b = Add(f.Db, "B"); var c = Add(f.Db, "C");
            Assert.Equal(new[] { a, b, c }, SiteMenuLinkStore.List(f.Db, Club, Owner).Select(x => x.Id).ToArray());
        }

        [SqlFact]
        public void Flytta_byter_plats_med_grannen_och_ytterlägen_ändrar_inget()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var a = Add(f.Db, "A"); var b = Add(f.Db, "B"); var c = Add(f.Db, "C");
            Assert.True(SiteMenuLinkWriter.Move(f.Db, Club, Owner, c, -1, Actor, Now).Ok);
            Assert.Equal(new[] { a, c, b }, SiteMenuLinkStore.List(f.Db, Club, Owner).Select(x => x.Id).ToArray());
            Assert.True(SiteMenuLinkWriter.Move(f.Db, Club, Owner, a, -1, Actor, Now).Ok);   // redan överst: inget fel, ingen ändring
            Assert.Equal(new[] { a, c, b }, SiteMenuLinkStore.List(f.Db, Club, Owner).Select(x => x.Id).ToArray());
            Assert.Equal(3, f.Db.ExecuteScalar<int>("SELECT COUNT(DISTINCT SortOrder) FROM dbo.SiteMenuItem WHERE OwnerId = @0 AND Kind = 2", Owner));   // aldrig två länkar med samma ordning
            Assert.False(SiteMenuLinkWriter.Move(f.Db, Club, Owner, a, 2, Actor, Now).Ok);
        }

        [SqlFact]
        public void Taket_på_länkar_gäller_per_ägare()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            for (var i = 0; i < SiteMenuLinkRules.MaxLinks; i++) Add(f.Db, "L" + i);
            var over = SiteMenuLinkWriter.Save(f.Db, Club, Owner, null, "För många", "https://exempel.se/", 0, Actor, Now);
            Assert.False(over.Ok);
            Assert.Contains("taket", over.Message);
            Assert.True(Add(f.Db, "Annan ägare", owner: OtherOwner) > 0);   // en annan ägares tak är orört
        }

        [SqlFact]
        public void En_länk_kan_aldrig_ändras_flyttas_eller_tas_bort_av_en_annan_ägare()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var mine = Add(f.Db, "Min");
            var theirs = Add(f.Db, "Deras", owner: OtherOwner);
            Assert.False(SiteMenuLinkWriter.Save(f.Db, Club, Owner, theirs, "Kapad", "https://elak.example.se/", 0, Actor, Now).Ok);
            Assert.False(SiteMenuLinkWriter.Remove(f.Db, Club, Owner, theirs, Actor, Now).Ok);
            Assert.False(SiteMenuLinkWriter.Move(f.Db, Club, Owner, theirs, 1, Actor, Now).Ok);
            Assert.Null(SiteMenuLinkStore.Get(f.Db, theirs, Club, Owner));
            var still = SiteMenuLinkStore.Get(f.Db, theirs, Club, OtherOwner)!;
            Assert.Equal("Deras", still.Label);
            Assert.Equal("https://exempel.se/", still.Url);
            Assert.NotNull(SiteMenuLinkStore.Get(f.Db, mine, Club, Owner));
        }

        [SqlFact]
        public void Ändra_och_ta_bort_fungerar_och_en_ogiltig_adress_når_aldrig_databasen()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var id = Add(f.Db, "Gammal");
            Assert.True(SiteMenuLinkWriter.Save(f.Db, Club, Owner, id, "Ny", "https://ny.example.se/", 1, Actor, Now).Ok);
            var row = SiteMenuLinkStore.Get(f.Db, id, Club, Owner)!;
            Assert.Equal(("Ny", "https://ny.example.se/", (byte)1), (row.Label, row.Url, row.Visibility));
            Assert.False(SiteMenuLinkWriter.Save(f.Db, Club, Owner, id, "Ny", "javascript:alert(1)", 1, Actor, Now).Ok);
            Assert.Equal("https://ny.example.se/", SiteMenuLinkStore.Get(f.Db, id, Club, Owner)!.Url);
            Assert.True(SiteMenuLinkWriter.Remove(f.Db, Club, Owner, id, Actor, Now).Ok);
            Assert.Null(SiteMenuLinkStore.Get(f.Db, id, Club, Owner));
            Assert.False(SiteMenuLinkWriter.Remove(f.Db, Club, Owner, id, Actor, Now).Ok);
        }

        [SqlFact]
        public void Varje_skrivning_loggas_och_en_länk_blandas_aldrig_med_en_sidas_menyrad()
        {
            using var f = new SiteSqlFixture(); SeedOwners(f.Db);
            var id = Add(f.Db, "Logg");
            SiteMenuLinkWriter.Save(f.Db, Club, Owner, id, "Logg2", "https://exempel.se/", 0, Actor, Now);
            SiteMenuLinkWriter.Remove(f.Db, Club, Owner, id, Actor, Now);
            var actions = f.Db.Fetch<string>("SELECT Action FROM dbo.SiteAudit WHERE OwnerType = 'Club' AND OwnerId = @0 AND ObjectKind = 'menu' ORDER BY Id", Owner);
            Assert.Equal(new[] { "link-add", "link-edit", "link-remove" }, actions.ToArray());
            // En egen sida i menyn (Kind = 1) syns inte som länk och tvärtom.
            var page = SitePageStore.Create(f.Db, Club, Owner, "Sida", SiteVisibility.Alla, null, null, 7, false, Now);
            Assert.True(SitePageStore.SetInMenu(f.Db, page.PageId, Club, Owner, true, Now));
            Assert.Empty(SiteMenuLinkStore.List(f.Db, Club, Owner));
        }
    }
}
