using HpskSite.Services;
using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Nyheterna (steg 1): reglerna som rena funktioner — adress, utdrag, validering, läsnivå och bildåtkomst.</summary>
    public class SitePostRulesTests
    {
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Board = new(true, true, true, false);
        private static readonly SiteViewer Admin = new(true, true, true, true);

        [Theory]
        [InlineData("Årsmötet 2027", "arsmotet-2027")]
        [InlineData("Ölfestival på Ängen!", "olfestival-pa-angen")]
        [InlineData("  --Hej   världen--  ", "hej-varlden")]
        [InlineData("???", "nyhet")]
        [InlineData("", "nyhet")]
        public void Adressen_ur_rubriken_blir_gemener_utan_å_ä_ö(string title, string expected) => Assert.Equal(expected, SitePostRules.Slugify(title));

        [Fact]
        public void Adressen_kapas_vid_100_tecken_och_löpnumret_ryms_alltid()
        {
            var slug = SitePostRules.Slugify(new string('a', 300));
            Assert.Equal(SitePostRules.MaxSlug, slug.Length);
            Assert.Equal(slug, SitePostRules.WithSuffix(slug, 1));
            var two = SitePostRules.WithSuffix(slug, 2);
            Assert.EndsWith("-2", two);
            Assert.True(two.Length <= SitePostRules.MaxSlug);
        }

        [Fact]
        public void Utdraget_tar_bort_taggar_klipper_vid_ordgräns_och_avkodar()
        {
            Assert.Equal("Hej alla & välkomna", SitePostRules.AutoExcerpt("<p>Hej alla &amp; välkomna</p>"));
            Assert.Equal("Första Andra", SitePostRules.AutoExcerpt("<p>Första</p><p>Andra</p>"));
            var long1 = "<p>" + string.Join(" ", Enumerable.Repeat("ordet", 100)) + "</p>";
            var ex = SitePostRules.AutoExcerpt(long1);
            Assert.EndsWith("…", ex);
            Assert.True(ex.Length <= SitePostRules.AutoExcerptChars + 1);
            Assert.DoesNotContain("ordet…ordet", ex);
            Assert.Equal("", SitePostRules.AutoExcerpt(null));
        }

        [Fact]
        public void Validering_kräver_rubrik_och_en_valbar_nivå()
        {
            SitePostInput I(string? title = "Hej", SiteVisibility v = SiteVisibility.Klubbmedlemmar, string? ex = null) => new(title, "<p>x</p>", ex, null, null, v);
            Assert.Null(SitePostRules.Validate(I()));
            Assert.NotNull(SitePostRules.Validate(I(title: " ")));
            Assert.NotNull(SitePostRules.Validate(I(title: new string('x', 201))));
            Assert.NotNull(SitePostRules.Validate(I(title: "a<b")));
            Assert.NotNull(SitePostRules.Validate(I(v: SiteVisibility.Utloggad)));      // systemvärdet kan aldrig väljas
            Assert.NotNull(SitePostRules.Validate(I(ex: new string('x', 401))));
        }

        [Fact]
        public void Läsnivåerna_kommer_ur_samma_regel_som_sidor_och_block()
        {
            Assert.Equal(new[] { 0 }, SitePostRules.ReadableLevels(Anon));
            Assert.Equal(new[] { 0, 1 }, SitePostRules.ReadableLevels(Logged));
            Assert.Equal(new[] { 0, 1, 2 }, SitePostRules.ReadableLevels(Member));
            Assert.Equal(new[] { 0, 1, 2, 3 }, SitePostRules.ReadableLevels(Board));
            Assert.Equal(new[] { 0, 1, 2, 3, 4 }, SitePostRules.ReadableLevels(Admin));
        }

        private static SitePostRow Row(bool published, SiteVisibility v, bool deleted = false) => new()
        {
            Status = (byte)(published ? SitePostStatus.Published : SitePostStatus.Draft),
            ReadVisibility = (byte)v,
            DeletedDate = deleted ? DateTime.Now : null,
        };

        [Theory]
        [InlineData(SiteVisibility.Alla, "anon", true)]
        [InlineData(SiteVisibility.Inloggade, "anon", false)]
        [InlineData(SiteVisibility.Inloggade, "logged", true)]
        [InlineData(SiteVisibility.Klubbmedlemmar, "logged", false)]
        [InlineData(SiteVisibility.Klubbmedlemmar, "member", true)]
        [InlineData(SiteVisibility.Styrelse, "member", false)]
        [InlineData(SiteVisibility.Styrelse, "board", true)]
        [InlineData(SiteVisibility.Dold, "board", false)]
        [InlineData(SiteVisibility.Dold, "admin", true)]
        public void Publicerad_nyhet_läses_bara_på_sin_nivå(SiteVisibility level, string who, bool expected)
            => Assert.Equal(expected, SitePosts.CanRead(Row(true, level), V(who), includeDrafts: false));

        [Theory]
        [InlineData("anon")]
        [InlineData("logged")]
        [InlineData("member")]
        [InlineData("board")]
        [InlineData("admin")]
        public void Ett_utkast_läses_aldrig_utan_skribentläge_och_en_raderad_aldrig(string who)
        {
            Assert.False(SitePosts.CanRead(Row(false, SiteVisibility.Alla), V(who), includeDrafts: false));
            Assert.True(SitePosts.CanRead(Row(false, SiteVisibility.Dold), V(who), includeDrafts: true));
            Assert.False(SitePosts.CanRead(Row(true, SiteVisibility.Alla, deleted: true), V(who), includeDrafts: true));
        }

        private static SiteViewer V(string who) => who switch { "anon" => Anon, "logged" => Logged, "member" => Member, "board" => Board, _ => Admin };

        // ── Bildåtkomst: en nyhets bilder följer nyhetens nivå, aldrig mer öppet ──────────────────────────────
        private static SitePostFileRef Ref(bool published, SiteVisibility v) => new() { Status = (byte)(published ? 1 : 0), ReadVisibility = (byte)v };
        private static SiteImageDecision Decide(SiteViewer who, params SitePostFileRef[] refs) =>
            SiteImageAccess.Decide(7, who, Array.Empty<(SiteVisibility, SiteContentResult)>(), refs);

        [Fact]
        public void Bilden_i_en_publicerad_öppen_nyhet_serveras_publikt()
            => Assert.Equal(SiteImageDecision.ServePublic, Decide(Anon, Ref(true, SiteVisibility.Alla)));

        [Fact]
        public void Bilden_i_en_medlemsnyhet_är_404_för_utloggade_och_privat_cachad_för_medlemmar()
        {
            Assert.Equal(SiteImageDecision.NotFound, Decide(Anon, Ref(true, SiteVisibility.Klubbmedlemmar)));
            Assert.Equal(SiteImageDecision.NotFound, Decide(Logged, Ref(true, SiteVisibility.Klubbmedlemmar)));
            Assert.Equal(SiteImageDecision.ServePrivate, Decide(Member, Ref(true, SiteVisibility.Klubbmedlemmar)));
        }

        [Fact]
        public void Bilden_i_ett_utkast_syns_för_skribenterna_men_aldrig_för_besökare()
        {
            Assert.Equal(SiteImageDecision.NotFound, Decide(Anon, Ref(false, SiteVisibility.Alla)));
            Assert.Equal(SiteImageDecision.NotFound, Decide(Member, Ref(false, SiteVisibility.Alla)));
            Assert.Equal(SiteImageDecision.ServePrivate, Decide(Board, Ref(false, SiteVisibility.Alla)));
            Assert.Equal(SiteImageDecision.ServePrivate, Decide(Admin, Ref(false, SiteVisibility.Dold)));
        }

        [Fact]
        public void Ett_utkast_gör_inte_en_publicerad_bild_mindre_öppen_för_besökare_och_blandad_bild_cachas_inte_publikt()
        {
            Assert.Equal(SiteImageDecision.ServePublic, Decide(Anon, Ref(true, SiteVisibility.Alla), Ref(false, SiteVisibility.Dold)));
            Assert.Equal(SiteImageDecision.ServePrivate, Decide(Anon, Ref(true, SiteVisibility.Alla), Ref(true, SiteVisibility.Klubbmedlemmar)));
        }

        [Fact]
        public void Gamla_Decide_utan_nyheter_är_oförändrad()
            => Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(7, Admin, Array.Empty<(SiteVisibility, SiteContentResult)>()));

        // ── Vem får skriva och ta bort (Stefan 2026-11-13): admin, sajtadmin och styrelsen skriver; bara admin och sajtadmin tar bort ──
        [Theory]
        [InlineData(true, true, false, false, true, true)]    // ägarens egen admin
        [InlineData(true, false, true, false, true, true)]    // sajtadmin
        [InlineData(true, false, false, true, true, false)]   // styrelsen: skriver, tar inte bort
        [InlineData(true, false, false, false, false, false)] // vanlig medlem
        [InlineData(false, true, true, true, false, false)]   // utloggad: aldrig, vad flaggorna än säger
        public void Skriv_och_raderingsrätten_följer_beslutet(bool loggedIn, bool ownAdmin, bool siteAdmin, bool board, bool write, bool delete)
        {
            Assert.Equal(write, SiteNewsRules.CanWrite(loggedIn, ownAdmin, siteAdmin, board));
            Assert.Equal(delete, SiteNewsRules.CanDelete(loggedIn, ownAdmin, siteAdmin));
        }

        [Fact]
        public void Skrivytans_adressdel_är_reserverad_och_nyheter_kan_aldrig_bli_en_egen_sida_eller_en_klubb_under_en_krets()
        {
            Assert.True(SitePostRules.IsReserved("skriv"));
            Assert.False(SitePostRules.IsReserved("hej"));
            Assert.Contains("skriv", SitePostRules.ReservedSlugs);
            Assert.True(HpskSite.Services.Addresses.PageAddressRules.IsRegionTemplateSlug("nyheter"));   // en klubb som heter nyheter skulle skugga kretsens arkiv
            Assert.False(SitePageSlugRules.Check("nyheter", SiteTemplates.Club, Array.Empty<string>(), Array.Empty<string>()).Ok);   // och en egen sida får inte heta det
        }

        // ── Mejla till medlemmarna: bara publicerat, bara nivåer alla medlemmar får läsa, högst en gång, högst 250 ──
        [Theory]
        [InlineData(true, SiteVisibility.Alla, false, 10, null)]
        [InlineData(true, SiteVisibility.Inloggade, false, 10, null)]
        [InlineData(true, SiteVisibility.Klubbmedlemmar, false, 250, null)]
        [InlineData(true, SiteVisibility.Klubbmedlemmar, false, 251, "högst 250")]
        [InlineData(true, SiteVisibility.Klubbmedlemmar, false, 0, "inga")]
        [InlineData(true, SiteVisibility.Styrelse, false, 10, "inte")]
        [InlineData(true, SiteVisibility.Dold, false, 10, "inte")]
        [InlineData(true, SiteVisibility.Klubbmedlemmar, true, 10, "redan")]
        [InlineData(false, SiteVisibility.Alla, false, 10, "Publicera")]
        public void Mejlet_prövas_mot_publicering_nivå_tidigare_utskick_och_taket(bool published, SiteVisibility level, bool mailed, int recipients, string? refusalContains)
        {
            var row = new SitePostRow { Status = (byte)(published ? 1 : 0), ReadVisibility = (byte)level, MailedDate = mailed ? DateTime.Now : null };
            var refusal = SiteNewsMailer.Refusal(row, recipients);
            if (refusalContains == null) Assert.Null(refusal);
            else Assert.Contains(refusalContains, refusal ?? "", StringComparison.OrdinalIgnoreCase);
        }

        // ── Spärrarna runt: radering, GDPR och startkontrollen känner till tabellerna ─────────────────────
        [Fact]
        public void Startkontrollen_kräver_nyhetstabellerna()
        {
            Assert.Contains("dbo.SitePost", SiteOwnerSchemaGuardHostedService.RequiredTables);
            Assert.Contains("dbo.SitePostRef", SiteOwnerSchemaGuardHostedService.RequiredTables);
        }

        [Fact]
        public void Gallringen_släpper_författaren_och_byter_namnet()
        {
            var cols = MemberDataPurgeService.AssignmentColumns;
            var author = Assert.Single(cols, c => c.Table == "SitePost" && c.MemberColumn == "AuthorMemberId");
            Assert.Contains("AuthorName", author.ClearSql);
            Assert.Contains(cols, c => c.Table == "SitePost" && c.MemberColumn == "UpdatedByMemberId");
        }
    }

    /// <summary>Nyheterna (steg 1) mot SQL Server (HPSK_TEST_SQL): skrivning med revision, adress, läsnivå per tittare, ägarisolering och radering.</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SitePostStoreTests
    {
        private const int A = 990101;
        private const int B = 990102;
        private static readonly DateTime Now = new(2026, 11, 13, 10, 0, 0);
        private static readonly SiteActor Actor = new(7);
        private static readonly SitePostAuthor Author = new(7, "Anna Andersson");
        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Board = new(true, true, true, false);
        private static readonly SiteViewer Admin = new(true, true, true, true);

        private static void Owner(IDatabase db, int id) =>
            db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled, EditingEnabled) VALUES ('Club', @0, 1, 1)", id);

        private static int File(IDatabase db, int owner) =>
            db.ExecuteScalar<int>(
                @"INSERT INTO dbo.SiteFile (OwnerType, OwnerId, FileName, StoredName, ContentType, Size, Width, Height, ContentHash, UploadedDate)
                  OUTPUT INSERTED.Id VALUES ('Club', @0, N'x.jpg', @1, N'image/jpeg', 10, 1, 1, CONVERT(BINARY(32), 0x00), SYSDATETIME())", owner, Guid.NewGuid().ToString("N"));   // StoredName är unikt

        private static SitePostInput In(string title = "Hej", SiteVisibility v = SiteVisibility.Klubbmedlemmar, string body = "<p>Text</p>", int? cover = null) =>
            new(title, body, null, cover, null, v);

        private static int Create(IDatabase db, int owner, string title = "Hej", SiteVisibility v = SiteVisibility.Klubbmedlemmar, bool publish = true, int? cover = null, string body = "<p>Text</p>")
        {
            var r = SitePosts.Create(db, "Club", owner, In(title, v, body, cover), Author, Now, Actor);
            Assert.Equal(SitePostResult.Ok, r.Result);
            if (publish) Assert.Equal(SitePostResult.Ok, SitePosts.SetPublished(db, r.Id, "Club", owner, r.Revision, true, null, Actor, Now).Result);
            return r.Id;
        }

        [SqlFact]
        public void Ny_nyhet_blir_utkast_med_författare_adress_och_utdrag()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Årsmötet 2027", publish: false);
            var row = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.Equal("arsmotet-2027", row.Slug);
            Assert.Equal((byte)SitePostStatus.Draft, row.Status);
            Assert.Equal("Anna Andersson", row.AuthorName);
            Assert.Equal("Text", row.Excerpt);
            Assert.Equal(1, row.Revision);
            Assert.Null(row.PublishedDate);
        }

        [SqlFact]
        public void Samma_rubrik_ger_löpnummer_och_en_raderad_nyhets_adress_frigörs()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var a = Create(f.Db, A, "Hej"); var b = Create(f.Db, A, "Hej");
            Assert.Equal("hej", SitePosts.Get(f.Db, a, "Club", A)!.Slug);
            Assert.Equal("hej-2", SitePosts.Get(f.Db, b, "Club", A)!.Slug);
            Assert.Equal(SitePostResult.Ok, SitePosts.Delete(f.Db, a, "Club", A, Actor, Now));
            Assert.Equal("hej", SitePosts.Get(f.Db, Create(f.Db, A, "Hej"), "Club", A)!.Slug);
        }

        [SqlFact]
        public void Adressen_är_fast_efter_första_publiceringen_men_följer_rubriken_före()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Förslag", publish: false);
            var r1 = SitePosts.Update(f.Db, id, "Club", A, 1, In("Bättre rubrik"), Actor, Now);
            Assert.Equal("battre-rubrik", SitePosts.Get(f.Db, id, "Club", A)!.Slug);
            Assert.Equal(SitePostResult.Ok, SitePosts.SetPublished(f.Db, id, "Club", A, r1.Revision, true, null, Actor, Now).Result);
            var cur = SitePosts.Get(f.Db, id, "Club", A)!;
            SitePosts.Update(f.Db, id, "Club", A, cur.Revision, In("Helt ny rubrik"), Actor, Now);
            Assert.Equal("battre-rubrik", SitePosts.Get(f.Db, id, "Club", A)!.Slug);   // publicerad adress är permanent
        }

        [SqlFact]
        public void Revisionen_fäller_en_gammal_sparning_och_ändrar_ingenting()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Hej", publish: false);
            Assert.Equal(SitePostResult.Ok, SitePosts.Update(f.Db, id, "Club", A, 1, In("Första"), Actor, Now).Result);
            var stale = SitePosts.Update(f.Db, id, "Club", A, 1, In("Andra"), Actor, Now);   // fliken som läste revision 1
            Assert.Equal(SitePostResult.Conflict, stale.Result);
            Assert.Equal("Första", SitePosts.Get(f.Db, id, "Club", A)!.Title);
            Assert.Equal(SitePostResult.Conflict, SitePosts.SetPublished(f.Db, id, "Club", A, 1, true, null, Actor, Now).Result);
        }

        [SqlFact]
        public void Första_publiceringen_sparas_för_sig_och_att_dra_tillbaka_gör_nyheten_till_utkast()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Hej");
            var row = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.Equal(Now, row.FirstPublishedDate);
            Assert.Equal(Now, row.PublishedDate);
            var later = Now.AddDays(3);
            Assert.Equal(SitePostResult.Ok, SitePosts.SetPublished(f.Db, id, "Club", A, row.Revision, false, null, Actor, later).Result);
            row = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.False(row.IsPublished);
            Assert.Equal(Now, row.FirstPublishedDate);   // aldrig omskriven
        }

        [SqlFact]
        public void Listan_filtrerar_på_läsnivå_per_tittare_och_utkast_syns_bara_för_skribenter()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            Create(f.Db, A, "Alla", SiteVisibility.Alla);
            Create(f.Db, A, "Inloggade", SiteVisibility.Inloggade);
            Create(f.Db, A, "Medlemmar", SiteVisibility.Klubbmedlemmar);
            Create(f.Db, A, "Styrelse", SiteVisibility.Styrelse);
            Create(f.Db, A, "Dold", SiteVisibility.Dold);
            Create(f.Db, A, "Utkast", SiteVisibility.Alla, publish: false);

            string[] Titles(SiteViewer v, bool drafts = false) => SitePosts.List(f.Db, "Club", A, v, drafts, 0, 50).Rows.Select(r => r.Title).OrderBy(x => x).ToArray();
            Assert.Equal(new[] { "Alla" }, Titles(Anon));
            Assert.Equal(new[] { "Alla", "Inloggade" }, Titles(Logged));
            Assert.Equal(new[] { "Alla", "Inloggade", "Medlemmar" }, Titles(Member));
            Assert.Equal(new[] { "Alla", "Inloggade", "Medlemmar", "Styrelse" }, Titles(Board));
            Assert.Equal(new[] { "Alla", "Dold", "Inloggade", "Medlemmar", "Styrelse" }, Titles(Admin));
            Assert.Equal(new[] { "Alla", "Inloggade", "Medlemmar", "Styrelse", "Utkast" }, Titles(Board, drafts: true));
            Assert.DoesNotContain("Utkast", Titles(Admin));   // ett utkast kräver skribentläget uttryckligen, även för admin
        }

        [SqlFact]
        public void Listan_bläddrar_nyast_först_med_total_och_bär_inte_texten()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            for (var i = 1; i <= 5; i++) Create(f.Db, A, "N" + i, SiteVisibility.Alla);
            var (page1, total) = SitePosts.List(f.Db, "Club", A, Anon, false, 0, 2);
            var (page3, _) = SitePosts.List(f.Db, "Club", A, Anon, false, 4, 2);
            Assert.Equal(5, total);
            Assert.Equal(new[] { "N5", "N4" }, page1.Select(r => r.Title));   // samma datum: Id DESC som tiebreak
            Assert.Equal(new[] { "N1" }, page3.Select(r => r.Title));
            Assert.All(page1, r => Assert.Equal("", r.BodyHtml));
        }

        [SqlFact]
        public void Direktlänken_till_en_nyhet_man_inte_får_läsa_ger_null_precis_som_en_som_saknas()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Hemlig", SiteVisibility.Styrelse);
            Assert.Null(SitePosts.GetReadableBySlug(f.Db, "Club", A, "hemlig", Anon, false));
            Assert.Null(SitePosts.GetReadableBySlug(f.Db, "Club", A, "hemlig", Member, false));
            Assert.Null(SitePosts.GetReadableBySlug(f.Db, "Club", A, "finns-inte", Admin, false));
            Assert.NotNull(SitePosts.GetReadableBySlug(f.Db, "Club", A, "hemlig", Board, false));
            Assert.Null(SitePosts.GetReadable(f.Db, id, "Club", A, Member, false));
        }

        [SqlFact]
        public void En_annan_ägares_nyhet_går_inte_att_läsa_ändra_eller_ta_bort_med_ägarens_id()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var id = Create(f.Db, A, "Hej", SiteVisibility.Alla);
            Assert.Null(SitePosts.Get(f.Db, id, "Club", B));
            Assert.Null(SitePosts.GetReadableBySlug(f.Db, "Club", B, "hej", Admin, true));
            Assert.Equal(SitePostResult.NotFound, SitePosts.Update(f.Db, id, "Club", B, 2, In("Kapad"), Actor, Now).Result);
            Assert.Equal(SitePostResult.NotFound, SitePosts.Delete(f.Db, id, "Club", B, Actor, Now));
            Assert.Equal("Hej", SitePosts.Get(f.Db, id, "Club", A)!.Title);
            Assert.Empty(SitePosts.List(f.Db, "Club", B, Admin, true, 0, 50).Rows);
        }

        [SqlFact]
        public void En_annan_ägares_bild_går_inte_att_använda_som_omslag_eller_i_texten()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var theirs = File(f.Db, B);
            var r1 = SitePosts.Create(f.Db, "Club", A, In(cover: theirs), Author, Now, Actor);
            var r2 = SitePosts.Create(f.Db, "Club", A, In(body: $"<p><img src=\"/umbraco/surface/SiteFile/Image?id={theirs}\"></p>"), Author, Now, Actor);
            Assert.Equal(SitePostResult.Invalid, r1.Result);
            Assert.Equal(SitePostResult.Invalid, r2.Result);
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePost WHERE OwnerId = @0", A));
        }

        [SqlFact]
        public void Omslag_och_bilder_i_texten_blir_referenser_och_bildmotorn_ser_dem()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var cover = File(f.Db, A); var inline = File(f.Db, A);
            var id = Create(f.Db, A, "Bild", SiteVisibility.Klubbmedlemmar, cover: cover,
                body: $"<p>Se <img src=\"/umbraco/surface/SiteFile/Image?id={inline}\"></p>");
            Assert.Equal(2, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostRef WHERE PostId = @0", id));
            var refs = SitePosts.RefsForFile(f.Db, cover, "Club", A);
            var one = Assert.Single(refs);
            Assert.Equal((byte)SitePostStatus.Published, one.Status);
            Assert.Equal((byte)SiteVisibility.Klubbmedlemmar, one.ReadVisibility);
            Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(cover, Anon, Array.Empty<(SiteVisibility, SiteContentResult)>(), refs));
            Assert.Equal(SiteImageDecision.ServePrivate, SiteImageAccess.Decide(cover, Member, Array.Empty<(SiteVisibility, SiteContentResult)>(), refs));
        }

        [SqlFact]
        public void En_bild_som_används_av_en_nyhet_går_inte_att_radera_förrän_nyheten_tagits_bort()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var file = File(f.Db, A);
            var id = Create(f.Db, A, "Bild", cover: file);
            Assert.True(SitePosts.FileInUse(f.Db, file, "Club", A));
            Assert.Equal(SiteFileStatus.InUse, SiteFiles.Delete(f.Db, file, "Club", A, Actor, Now, out _));
            Assert.Equal(1, SiteFiles.List(f.Db, "Club", A).Single(x => x.Id == file).UsedBy);   // biblioteket säger varför
            SitePosts.Delete(f.Db, id, "Club", A, Actor, Now);
            Assert.False(SitePosts.FileInUse(f.Db, file, "Club", A));
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Delete(f.Db, file, "Club", A, Actor, Now, out _));
        }

        [SqlFact]
        public void Raderingen_loggas_i_auditen_utan_text_och_nyheten_syns_inte_längre()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Hej", SiteVisibility.Alla);
            Assert.Equal(SitePostResult.Ok, SitePosts.Delete(f.Db, id, "Club", A, Actor, Now));
            Assert.Null(SitePosts.Get(f.Db, id, "Club", A));
            Assert.Equal(SitePostResult.NotFound, SitePosts.Delete(f.Db, id, "Club", A, Actor, Now));   // inte två gånger
            var actions = f.Db.Fetch<string>("SELECT Action FROM dbo.SiteAudit WHERE OwnerId = @0 AND ObjectKind = N'post' AND ObjectId = @1 ORDER BY Id", A, id);
            Assert.Equal(new[] { "post-create", "post-publish", "post-delete" }, actions);
        }

        [SqlFact]
        public void Radering_av_ägaren_tar_med_nyheterna_även_med_omslag_och_rör_inte_andra()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            Create(f.Db, A, "Med omslag", cover: File(f.Db, A));
            Create(f.Db, B, "Stannar", cover: File(f.Db, B));
            Assert.Equal(1, SiteOwnerData.DeleteAll(f.Db, "Club", A));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePost WHERE OwnerId = @0", A));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostRef WHERE OwnerId = @0", A));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePost WHERE OwnerId = @0", B));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePostRef WHERE OwnerId = @0", B));
        }

        [SqlFact]
        public void Utskicket_tas_högst_en_gång_och_bara_för_en_publicerad_nyhet()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A); Owner(f.Db, B);
            var draft = Create(f.Db, A, "Utkast", publish: false);
            Assert.False(SitePosts.ClaimMail(f.Db, draft, "Club", A, Now));                      // ett utkast mejlas aldrig
            var id = Create(f.Db, A, "Hej", SiteVisibility.Alla);
            Assert.False(SitePosts.ClaimMail(f.Db, id, "Club", B, Now));                         // inte med en annan ägares id
            Assert.True(SitePosts.ClaimMail(f.Db, id, "Club", A, Now));
            Assert.False(SitePosts.ClaimMail(f.Db, id, "Club", A, Now.AddMinutes(1)));           // andra försöket (dubbelklick, omladdning) får inget
            SitePosts.SetMailedCount(f.Db, id, "Club", A, 42);
            var row = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.Equal(Now, row.MailedDate);
            Assert.Equal(42, row.MailedCount);
            var deleted = Create(f.Db, A, "Borta", SiteVisibility.Alla);
            SitePosts.Delete(f.Db, deleted, "Club", A, Actor, Now);
            Assert.False(SitePosts.ClaimMail(f.Db, deleted, "Club", A, Now));                    // en raderad nyhet mejlas inte
        }

        [SqlFact]
        public void Texten_saneras_vid_skrivning()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "XSS", body: "<p>Hej<script>alert(1)</script></p><img src=\"https://evil.example/x.png\">");
            var row = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.DoesNotContain("script", row.BodyHtml);
            Assert.DoesNotContain("evil.example", row.BodyHtml);
        }

        [SqlFact]
        public void Gallringen_släpper_författaren_men_nyheten_står_kvar()
        {
            using var f = new SiteSqlFixture(); Owner(f.Db, A);
            var id = Create(f.Db, A, "Hej", SiteVisibility.Alla);
            foreach (var (table, col, clear) in MemberDataPurgeService.AssignmentColumns.Where(c => c.Table == "SitePost"))
                f.Db.Execute($"UPDATE [{table}] SET [{col}] = NULL, {clear} WHERE [{col}] = @0", 7);
            var row = SitePosts.Get(f.Db, id, "Club", A)!;
            Assert.Null(row.AuthorMemberId);
            Assert.Null(row.UpdatedByMemberId);
            Assert.Equal("Tidigare medlem", row.AuthorName);
        }
    }
}
