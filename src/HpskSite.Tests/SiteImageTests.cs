using System.Text;
using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using NPoco;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2c: bilder. Omkodning (ren kod), schema, åtkomstregel, rendering — och SQL-proven för kvot, ägare och radering.</summary>
    public class SiteImageTests
    {
        // ── Omkodning ─────────────────────────────────────────────────────────

        private static byte[] Jpeg(int w, int h, bool withGps)
        {
            using var img = new Image<Rgb24>(w, h, new Rgb24(10, 120, 200));
            if (withGps)
            {
                img.Metadata.ExifProfile = new ExifProfile();
                img.Metadata.ExifProfile.SetValue(ExifTag.Make, "SecretCameraMaker");
                img.Metadata.ExifProfile.SetValue(ExifTag.ImageDescription, "HemligPlats");
            }
            using var ms = new MemoryStream(); img.SaveAsJpeg(ms); return ms.ToArray();
        }

        private static Task<(SiteImageResult?, SiteImageFailure)> Process(byte[] bytes, string name = "bild.jpg") =>
            SiteImages.ProcessAsync(new MemoryStream(bytes), name, bytes.Length);

        [Fact]
        public async Task Omkodningen_tar_bort_exif_och_typen_kommer_ur_innehållet()
        {
            var src = Jpeg(40, 30, withGps: true);
            Assert.Contains("SecretCameraMaker", Encoding.Latin1.GetString(src));   // kontroll: originalet bär metadata
            var (img, fail) = await Process(src);
            Assert.Equal(SiteImageFailure.None, fail);
            Assert.Equal("image/jpeg", img!.ContentType);
            Assert.DoesNotContain("SecretCameraMaker", Encoding.Latin1.GetString(img.Bytes));
            Assert.DoesNotContain("HemligPlats", Encoding.Latin1.GetString(img.Bytes));
            Assert.Equal((40, 30), (img.Width, img.Height));
        }

        [Fact]
        public async Task En_png_med_jpg_ändelse_lagras_som_png()
        {
            using var png = new Image<Rgb24>(8, 8); using var ms = new MemoryStream(); png.SaveAsPng(ms);
            var (img, _) = await Process(ms.ToArray(), "foto.jpg");
            Assert.Equal("image/png", img!.ContentType);   // formatet ur det avkodade, aldrig ur ändelsen
        }

        [Theory]
        [InlineData("skript.html")] [InlineData("bild.svg")] [InlineData("bild")] [InlineData("bild.exe")] [InlineData("bild.tiff")]
        public async Task Annan_ändelse_än_bild_vägras(string name)
        {
            var (img, fail) = await Process(Jpeg(4, 4, false), name);
            Assert.Null(img); Assert.Equal(SiteImageFailure.BadExtension, fail);
        }

        [Fact]
        public async Task Html_med_bildändelse_är_ingen_bild()
        {
            var (img, fail) = await Process(Encoding.UTF8.GetBytes("<html><script>alert(1)</script></html>"), "x.png");
            Assert.Null(img); Assert.Equal(SiteImageFailure.NotAnImage, fail);
        }

        [Fact]
        public async Task Gif_med_html_efter_sig_kodas_om_och_html_försvinner()
        {
            using var gif = new Image<Rgb24>(6, 6); using var ms = new MemoryStream(); gif.SaveAsGif(ms);
            var poly = ms.ToArray().Concat(Encoding.UTF8.GetBytes("<script>alert('x')</script>")).ToArray();
            Assert.Contains("<script>", Encoding.Latin1.GetString(poly));
            var (img, fail) = await Process(poly, "x.gif");
            Assert.Equal(SiteImageFailure.None, fail);
            Assert.DoesNotContain("<script", Encoding.Latin1.GetString(img!.Bytes));
        }

        [Fact]
        public async Task En_stor_bild_förminskas_till_taket()
        {
            var (img, _) = await Process(Jpeg(3000, 1000, false));
            Assert.Equal(SiteImages.MaxSide, img!.Width);
            Assert.True(img.Height is >= 799 and <= 801);
        }

        [Fact]
        public async Task För_många_pixlar_vägras_före_avkodning()
        {
            using var big = new Image<L8>(6500, 6200);   // 40,3 Mpx
            using var ms = new MemoryStream(); big.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.Grayscale, BitDepth = PngBitDepth.Bit8 });
            var (img, fail) = await Process(ms.ToArray(), "x.png");
            Assert.Null(img); Assert.Equal(SiteImageFailure.TooManyPixels, fail);
        }

        [Fact]
        public async Task För_stor_fil_vägras()
        {
            var (img, fail) = await SiteImages.ProcessAsync(new MemoryStream(Jpeg(4, 4, false)), "x.jpg", SiteImages.MaxUploadBytes + 1);
            Assert.Null(img); Assert.Equal(SiteImageFailure.TooLarge, fail);
        }

        [Fact]
        public void Visningsnamnet_saknar_sökväg_kontrolltecken_och_bidi()
        {
            Assert.Equal("semester.jpg", SiteImages.DisplayName("C:\\foto\\semester.jpg"));
            Assert.Equal("ab.jpg", SiteImages.DisplayName("a‮b\u0001.jpg"));
            Assert.Equal("bild", SiteImages.DisplayName("  "));
            Assert.Equal("xy.png", SiteImages.DisplayName("<x>\"y.png"));
        }

        // ── Schema ────────────────────────────────────────────────────────────

        private static string ImageBlock(string data, int vis = 2, string id = "abcd1") =>
            "{\"schema\":1,\"blocks\":[{\"id\":\"" + id + "\",\"type\":\"image\",\"visibility\":" + vis + ",\"data\":" + data + "}]}";

        private static SiteContentResult Parse(string json, SiteVisibility page = SiteVisibility.Klubbmedlemmar, SiteContentKind kind = SiteContentKind.FreePage) =>
            SiteBlockSchema.Parse(json, kind, page, SiteTemplates.ClubTemplate);

        [Fact]
        public void Ett_bildblock_kan_sparas_utan_beskrivning_i_ett_utkast_och_ger_en_filreferens()
        {
            var c = Parse(ImageBlock("{\"ref\":\"file:12\",\"decorative\":false}"));
            Assert.True(c.Ok, string.Join("; ", c.Errors.Select(e => e.Message)));
            Assert.Equal(new[] { ("file", 12) }, SiteBlockSchema.ExtractRefs(c).ToArray());
            Assert.Equal(12, SiteBlockSchema.FileIdOf(c.Blocks[0]));
        }

        [Theory]
        [InlineData("{\"ref\":\"12\"}")] [InlineData("{\"ref\":\"file:0\"}")] [InlineData("{\"ref\":\"file:-3\"}")] [InlineData("{\"ref\":\"doc:5\"}")]
        [InlineData("{\"ref\":\"file:1\",\"url\":\"/x\"}")] [InlineData("{\"ref\":\"file:1\",\"decorative\":\"ja\"}")] [InlineData("{}")]
        [InlineData("{\"ref\":\"file:1\",\"alt\":\"a<b\"}")]
        public void Ett_bildblock_med_fel_form_avvisas(string data) => Assert.False(Parse(ImageBlock(data)).Ok);

        [Fact]
        public void Ett_bildblock_får_inte_ligga_före_eller_efter_en_systemsida()
        {
            var json = "{\"schema\":1,\"slots\":{\"before\":[{\"id\":\"abcd1\",\"type\":\"image\",\"visibility\":2,\"data\":{\"ref\":\"file:1\"}}],\"after\":[]}}";
            Assert.False(Parse(json, kind: SiteContentKind.TemplateCopy).Ok);
        }

        [Fact]
        public void Bildblock_kan_inte_vara_öppnare_än_sidan()
            => Assert.False(Parse(ImageBlock("{\"ref\":\"file:1\"}", vis: 0), SiteVisibility.Klubbmedlemmar).Ok);

        [Fact]
        public void Dekoration_sparar_ingen_beskrivning()
        {
            var c = Parse(ImageBlock("{\"ref\":\"file:1\",\"decorative\":true,\"alt\":\"ska bort\"}"));
            Assert.True(c.Ok);
            Assert.Null(c.Blocks[0].Data["alt"]);
        }

        // ── Åtkomstregeln ─────────────────────────────────────────────────────

        private static (SiteVisibility, SiteContentResult) Page(SiteVisibility page, int blockVis, int file = 7)
        {
            var c = Parse(ImageBlock("{\"ref\":\"file:" + file + "\"}", blockVis), page);
            Assert.True(c.Ok, string.Join("; ", c.Errors.Select(e => e.Message)));
            return (page, c);
        }

        private static readonly SiteViewer Anon = SiteViewer.Anonymous;
        private static readonly SiteViewer Logged = new(true, false, false, false);
        private static readonly SiteViewer Member = new(true, true, false, false);
        private static readonly SiteViewer Board = new(true, true, true, false);
        private static readonly SiteViewer Admin = new(true, false, false, true);

        // Handskriven sanningstabell: (sida, block, tittare) → beslut. Bilden finns bara på en sida.
        public static IEnumerable<object[]> Table() => new[]
        {
            new object[] { SiteVisibility.Alla, 0, "anon", SiteImageDecision.ServePublic },
            new object[] { SiteVisibility.Alla, 0, "member", SiteImageDecision.ServePublic },
            new object[] { SiteVisibility.Inloggade, 1, "anon", SiteImageDecision.NotFound },
            new object[] { SiteVisibility.Inloggade, 1, "logged", SiteImageDecision.ServePrivate },
            new object[] { SiteVisibility.Klubbmedlemmar, 2, "logged", SiteImageDecision.NotFound },
            new object[] { SiteVisibility.Klubbmedlemmar, 2, "member", SiteImageDecision.ServePrivate },
            new object[] { SiteVisibility.Styrelse, 3, "member", SiteImageDecision.NotFound },
            new object[] { SiteVisibility.Styrelse, 3, "board", SiteImageDecision.ServePrivate },
            new object[] { SiteVisibility.Dold, 4, "board", SiteImageDecision.NotFound },
            new object[] { SiteVisibility.Dold, 4, "admin", SiteImageDecision.ServePrivate },
            new object[] { SiteVisibility.Klubbmedlemmar, 3, "member", SiteImageDecision.NotFound },   // sidan öppen för medlem men blocket strängare: striktaste gäller
            new object[] { SiteVisibility.Dold, 4, "anon", SiteImageDecision.NotFound },
        };

        private static SiteViewer Who(string k) => k switch { "anon" => Anon, "logged" => Logged, "member" => Member, "board" => Board, _ => Admin };

        [Theory]
        [MemberData(nameof(Table))]
        public void Åtkomstregeln_följer_sanningstabellen(SiteVisibility page, int blockVis, string who, SiteImageDecision expected)
            => Assert.Equal(expected, SiteImageAccess.Decide(7, Who(who), new[] { Page(page, blockVis) }));

        [Fact]
        public void Bilden_på_en_annan_sida_eller_en_annan_bild_ger_inte_åtkomst()
            => Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(7, Anon, new[] { Page(SiteVisibility.Alla, 0, file: 8) }));

        [Fact]
        public void Ingen_publicerad_referens_ger_404_för_alla()
            => Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(7, Admin, Array.Empty<(SiteVisibility, SiteContentResult)>()));

        [Fact]
        public void En_blandad_bild_är_aldrig_publikt_cachad()
        {
            var pages = new[] { Page(SiteVisibility.Alla, 0), Page(SiteVisibility.Klubbmedlemmar, 2) };
            Assert.Equal(SiteImageDecision.ServePrivate, SiteImageAccess.Decide(7, Anon, pages));    // sedd via den öppna sidan, men cachas inte publikt
            Assert.Equal(SiteImageDecision.ServePrivate, SiteImageAccess.Decide(7, Member, pages));
        }

        // ── Rendering ─────────────────────────────────────────────────────────

        private sealed class ImgData : ISiteBlockData
        {
            public List<SiteImageInfo> Imgs = new();
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public IReadOnlyList<SiteImageInfo> Images(IReadOnlyList<int> ids) => Imgs;   // en dålig implementation: lämnar ut ALLT
        }

        private static string Render(JsonObject data, SiteViewer v, ImgData d, SiteVisibility vis = SiteVisibility.Klubbmedlemmar) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", "image", vis, data), SiteVisibility.Klubbmedlemmar, new SiteRenderContext(v, "/halland/ankeborg/", SiteTemplates.ClubTemplate, d));

        [Fact]
        public void Bilden_visas_med_kodad_beskrivning_mått_och_serveringsadress()
        {
            var html = Render(new JsonObject { ["ref"] = "file:7", ["alt"] = "Skytte \"vid\" <banan>", ["caption"] = "Årsmötet", ["decorative"] = false }, Member,
                new ImgData { Imgs = { new SiteImageInfo(7, 800, 600) } });
            Assert.Contains("src=\"/umbraco/surface/SiteFile/Image?id=7\"", html);
            Assert.Contains("width=\"800\" height=\"600\"", html);
            Assert.DoesNotContain("<banan>", html);
            Assert.Contains("&lt;banan&gt;", html);
            Assert.Contains("Årsmötet", html);
            Assert.Contains("loading=\"lazy\"", html);
        }

        [Fact]
        public void Dekoration_ger_tom_alt_och_ingen_text()
        {
            var html = Render(new JsonObject { ["ref"] = "file:7", ["alt"] = "stannar inte", ["decorative"] = true }, Member, new ImgData { Imgs = { new SiteImageInfo(7, 10, 10) } });
            Assert.Contains("alt=\"\"", html); Assert.Contains("role=\"presentation\"", html); Assert.DoesNotContain("stannar inte", html);
        }

        [Fact]
        public void En_bild_ägaren_inte_har_renderas_aldrig()
            => Assert.Equal("", Render(new JsonObject { ["ref"] = "file:7" }, Member, new ImgData()));

        [Fact]
        public void En_lagrad_rad_med_trasig_referens_renderas_inte()
            => Assert.Equal("", Render(new JsonObject { ["ref"] = "javascript:alert(1)" }, Member, new ImgData { Imgs = { new SiteImageInfo(7, 1, 1) } }));

        [Fact]
        public void Tittaren_som_inte_får_se_blocket_får_ingen_bildtagg()
            => Assert.Equal("", Render(new JsonObject { ["ref"] = "file:7" }, Anon, new ImgData { Imgs = { new SiteImageInfo(7, 1, 1) } }));

        [Fact]
        public void Bildens_text_går_genom_personnummerspärren()
        {
            var c = Parse(ImageBlock("{\"ref\":\"file:1\",\"alt\":\"Kalle 19121212-1212\"}"));
            Assert.True(c.Ok);
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(SitePageWriter.TextOf("Titel", c)));   // samma textuppräkning som skrivlagret använder
        }
    }

    /// <summary>SQL-proven: kvot och antal under lås, ägarprövning, radering, och att skrivlagret avvisar en annan ägares bild.</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteFileSqlTests
    {
        private const string Club = "Club";
        private const int Owner = 990101;
        private const int Other = 990102;
        private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0);
        private static readonly SiteActor Admin = new(7);

        private sealed class Facts : ISiteOwnerFacts
        {
            public bool DocumentBelongsTo(int docId, string ownerType, int ownerId) => false;
            public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) => Array.Empty<string>();
            public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId) => Array.Empty<string>();
        }

        private static void Seed(IDatabase db)
        {
            foreach (var id in new[] { Owner, Other }) db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id);
        }

        private static SiteImageResult Img(int bytes = 100) => new(new byte[bytes], "image/jpeg", 10, 10, new byte[32]);
        private static string Stored() => Guid.NewGuid().ToString("N");

        private static int Add(IDatabase db, int owner = Owner, int bytes = 100)
        {
            var s = SiteFiles.Insert(db, Club, owner, "a.jpg", Stored(), Img(bytes), 7, Now, out var id);
            Assert.Equal(SiteFileStatus.Ok, s);
            return id;
        }

        private static string ImageJson(int fileId, int vis = 2) =>
            "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"image\",\"visibility\":" + vis + ",\"data\":{\"ref\":\"file:" + fileId + "\"}}]}";

        [SqlFact]
        public void Kvoten_prövas_och_en_rad_över_taket_läggs_inte_in()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Insert(f.Db, Club, Owner, "a.jpg", Stored(), Img(600), 7, Now, out _, quotaBytes: 1000));
            Assert.Equal(SiteFileStatus.QuotaFull, SiteFiles.Insert(f.Db, Club, Owner, "b.jpg", Stored(), Img(600), 7, Now, out var id, quotaBytes: 1000));
            Assert.Equal(0, id);
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Insert(f.Db, Club, Other, "c.jpg", Stored(), Img(600), 7, Now, out _, quotaBytes: 1000));   // kvoten är per ägare
            Assert.Equal((1, 600L), SiteFiles.Usage(f.Db, Club, Owner));
        }

        [SqlFact]
        public void Antalet_prövas()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Insert(f.Db, Club, Owner, "a.jpg", Stored(), Img(), 7, Now, out _, maxFiles: 1));
            Assert.Equal(SiteFileStatus.TooMany, SiteFiles.Insert(f.Db, Club, Owner, "b.jpg", Stored(), Img(), 7, Now, out _, maxFiles: 1));
        }

        [SqlFact]
        public void En_ägare_utan_rad_får_ingen_bild()
        {
            using var f = new SiteSqlFixture();
            Assert.Equal(SiteFileStatus.NotFound, SiteFiles.Insert(f.Db, Club, 990199, "a.jpg", Stored(), Img(), 7, Now, out _));
        }

        [SqlFact]
        public void Ett_fil_id_prövas_mot_ägaren()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var id = Add(f.Db);
            Assert.True(SiteFiles.BelongsTo(f.Db, id, Club, Owner));
            Assert.False(SiteFiles.BelongsTo(f.Db, id, Club, Other));
            Assert.False(SiteFiles.BelongsTo(f.Db, id, "Region", Owner));
            Assert.Null(SiteFiles.Get(f.Db, id, Club, Other));
            Assert.Empty(SiteFiles.Info(f.Db, new[] { id }, Club, Other));
            Assert.Single(SiteFiles.Info(f.Db, new[] { id }, Club, Owner));
        }

        [SqlFact]
        public void Skrivlagret_avvisar_en_annan_ägares_bild_och_en_raderad_bild()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var foreign = Add(f.Db, Other);
            var own = Add(f.Db, Owner);
            var page = SitePageWriter.CreatePage(f.Db, facts, Club, Owner, "Bilder", null, SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.True(page.Ok);
            var bad = SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, page.Revision, "Bilder", ImageJson(foreign), SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, bad.Status);
            var good = SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, page.Revision, "Bilder", ImageJson(own), SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.True(good.Ok, good.Status + good.Message);
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SitePageRef WHERE PageId=@0 AND RefKind='file' AND RefId=@1", page.PageId, own));

            // En bild som raderats får inte refereras av en ny sparning.
            var spare = Add(f.Db, Owner);
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Delete(f.Db, spare, Club, Owner, Admin, Now, out _));
            var gone = SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, good.Revision, "Bilder", ImageJson(spare), SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.Equal(SiteWriteStatus.Invalid, gone.Status);
        }

        [SqlFact]
        public void En_bild_i_bruk_kan_inte_tas_bort_men_en_ledig_kan()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var used = Add(f.Db); var free = Add(f.Db);
            var page = SitePageWriter.CreatePage(f.Db, facts, Club, Owner, "Bilder", null, SiteVisibility.Klubbmedlemmar, Admin, Now);
            Assert.True(SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, page.Revision, "Bilder", ImageJson(used), SiteVisibility.Klubbmedlemmar, Admin, Now).Ok);

            Assert.Equal(SiteFileStatus.InUse, SiteFiles.Delete(f.Db, used, Club, Owner, Admin, Now, out _));
            Assert.NotNull(SiteFiles.Get(f.Db, used, Club, Owner));
            Assert.Equal(SiteFileStatus.Ok, SiteFiles.Delete(f.Db, free, Club, Owner, Admin, Now, out var row));
            Assert.NotNull(row);
            Assert.Null(SiteFiles.Get(f.Db, free, Club, Owner));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteAudit WHERE ObjectKind='file' AND ObjectId=@0 AND Action='file-delete' AND ActorMemberId=7", free));
            Assert.Equal(SiteFileStatus.NotFound, SiteFiles.Delete(f.Db, free, Club, Owner, Admin, Now, out _));       // redan borta
            Assert.Equal(SiteFileStatus.NotFound, SiteFiles.Delete(f.Db, used, Club, Other, Admin, Now, out _));       // fel ägare
        }

        [SqlFact]
        public void Listan_visar_användning_och_hoppar_över_raderade()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var facts = new Facts();
            var used = Add(f.Db); var free = Add(f.Db); var gone = Add(f.Db);
            SiteFiles.Delete(f.Db, gone, Club, Owner, Admin, Now, out _);
            var page = SitePageWriter.CreatePage(f.Db, facts, Club, Owner, "Bilder", null, SiteVisibility.Klubbmedlemmar, Admin, Now);
            SitePageWriter.SaveDraft(f.Db, facts, page.PageId, Club, Owner, page.Revision, "Bilder", ImageJson(used), SiteVisibility.Klubbmedlemmar, Admin, Now);
            var list = SiteFiles.List(f.Db, Club, Owner);
            Assert.Equal(2, list.Count);
            Assert.Equal(1, list.Single(x => x.Id == used).UsedBy);
            Assert.Equal(0, list.Single(x => x.Id == free).UsedBy);
        }

        [SqlFact]
        public void Ägarens_radering_tar_bildraderna()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            Add(f.Db); Add(f.Db, Other);
            Assert.Equal(1, SiteOwnerData.DeleteAll(f.Db, Club, Owner));
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFile WHERE OwnerId=@0", Owner));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFile WHERE OwnerId=@0", Other));
        }
    }
}
