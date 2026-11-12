using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Bildgalleriet (bildbiblioteket, del 2): bilder ur ägarens bibliotek som miniatyrer. Allt är ren server-HTML, kodat, och en bild serveras bara när ett publicerat block som visar den är synligt för tittaren.</summary>
    public class SiteGalleryBlockTests
    {
        private static SiteContentResult Parse(string data, int vis = 2, bool region = false)
        {
            var b = "{\"id\":\"abcd1\",\"type\":\"gallery\",\"visibility\":" + vis + ",\"data\":" + data + "}";
            return SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[" + b + "]}", SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar,
                region ? SiteTemplates.RegionTemplate : SiteTemplates.ClubTemplate);
        }

        private static string Items(params string[] refs) => "[" + string.Join(",", refs.Select(r => "{\"ref\":\"" + r + "\"}")) + "]";

        [Fact]
        public void Ett_galleri_har_rubrik_och_bilder_och_gäller_både_klubb_och_krets()
        {
            Assert.True(Parse("{\"items\":" + Items("file:1", "file:2") + "}").Ok);
            Assert.True(Parse("{\"title\":\"Årsmötet\",\"items\":[{\"ref\":\"file:3\",\"alt\":\"Skyttar\",\"caption\":\"Vid banan\"}]}", region: true).Ok);
            Assert.True(Parse("{}").Ok);   // ett nytt galleri utan bilder får sparas som utkast
            Assert.False(SiteBlockSchema.Types["gallery"].Personal);
        }

        [Theory]
        [InlineData("{\"items\":[{\"ref\":\"file:0\"}]}")]
        [InlineData("{\"items\":[{\"ref\":\"doc:1\"}]}")]
        [InlineData("{\"items\":[{\"ref\":\"https://elak.se/x.png\"}]}")]
        [InlineData("{\"items\":[{\"ref\":\"file:1\",\"url\":\"https://elak.se/\"}]}")]      // ingen adress: bara bilder ur biblioteket
        [InlineData("{\"items\":[{\"ref\":\"file:1\"},{\"ref\":\"file:1\"}]}")]               // samma bild två gånger
        [InlineData("{\"items\":\"file:1\"}")]
        [InlineData("{\"items\":[5]}")]
        [InlineData("{\"items\":[{\"ref\":\"file:1\",\"caption\":\"<b>\"}]}")]
        [InlineData("{\"html\":\"<p>x</p>\"}")]
        public void Ogiltiga_gallerier_avvisas(string data) => Assert.False(Parse(data).Ok, data);

        [Fact]
        public void Taket_är_24_bilder_och_en_bildtext_är_högst_200_tecken()
        {
            Assert.True(Parse("{\"items\":" + Items(Enumerable.Range(1, 24).Select(i => "file:" + i).ToArray()) + "}").Ok);
            Assert.False(Parse("{\"items\":" + Items(Enumerable.Range(1, 25).Select(i => "file:" + i).ToArray()) + "}").Ok);
            Assert.True(Parse("{\"items\":[{\"ref\":\"file:1\",\"caption\":\"" + new string('x', 200) + "\"}]}").Ok);
            Assert.False(Parse("{\"items\":[{\"ref\":\"file:1\",\"caption\":\"" + new string('x', 201) + "\"}]}").Ok);
        }

        [Fact]
        public void Galleriets_bilder_räknas_som_filreferenser_så_att_de_serveras_och_räknas_som_använda()
        {
            var c = Parse("{\"items\":" + Items("file:4", "file:9") + "}");
            Assert.True(c.Ok);
            Assert.Equal(new[] { 4, 9 }, SiteBlockSchema.FileIdsOf(c.Blocks[0]).OrderBy(x => x).ToArray());
            Assert.Equal(new[] { ("file", 4), ("file", 9) }, SiteBlockSchema.ExtractRefs(c).Where(r => r.Kind == "file").OrderBy(r => r.Id).ToArray());
        }

        [Fact]
        public void En_bild_i_ett_gallerit_serveras_bara_till_den_som_får_se_blocket()
        {
            var c = Parse("{\"items\":" + Items("file:4") + "}");
            Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(4, SiteViewer.Anonymous, new[] { (SiteVisibility.Klubbmedlemmar, c) }));
            Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(4, new SiteViewer(true, false, false, false), new[] { (SiteVisibility.Klubbmedlemmar, c) }));
            Assert.Equal(SiteImageDecision.ServePrivate, SiteImageAccess.Decide(4, new SiteViewer(true, true, false, false), new[] { (SiteVisibility.Klubbmedlemmar, c) }));
            Assert.Equal(SiteImageDecision.NotFound, SiteImageAccess.Decide(5, new SiteViewer(true, true, false, false), new[] { (SiteVisibility.Klubbmedlemmar, c) }));   // en bild som inte står i galleriet
        }

        [Fact]
        public void Personnummerspärren_läser_galleriets_bildtexter()
        {
            var c = Parse("{\"items\":[{\"ref\":\"file:1\",\"alt\":\"Beskrivning\",\"caption\":\"Anna 19900101-1234\"}]}");
            Assert.True(c.Ok);
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(SitePageWriter.TextOf("Sida", c)));
        }

        private sealed class ImgData : ISiteBlockData
        {
            public int[] Owned = { 5, 6 };
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public IReadOnlyList<SiteImageInfo> Images(IReadOnlyList<int> ids) => ids.Where(Owned.Contains).Select(i => new SiteImageInfo(i, 800, 600)).ToList();
        }

        private static readonly SiteViewer Member = new(true, true, false, false);

        private static string Render(JsonObject data, SiteViewer? v = null, SiteVisibility vis = SiteVisibility.Klubbmedlemmar) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", "gallery", vis, data), SiteVisibility.Klubbmedlemmar,
                new SiteRenderContext(v ?? Member, "/halland/ankeborg/", SiteTemplates.ClubTemplate, new ImgData()));

        private static JsonObject G(params (int Id, string? Alt, string? Caption)[] items) => new()
        {
            ["title"] = "Bilder <från> & till",
            ["items"] = new JsonArray(items.Select(i =>
            {
                var o = new JsonObject { ["ref"] = "file:" + i.Id };
                if (i.Alt != null) o["alt"] = i.Alt; if (i.Caption != null) o["caption"] = i.Caption;
                return (JsonNode)o;
            }).ToArray()),
        };

        [Fact]
        public void Galleriet_ritas_som_miniatyrer_med_kodade_texter_och_länkar_till_originalet()
        {
            var html = Render(G((5, "Skyttar <vid> banan", "Årsmötet & lunch"), (6, null, null)));
            Assert.Contains("Bilder &lt;från&gt; &amp; till", html);
            Assert.Contains("Skyttar &lt;vid&gt; banan", html);
            Assert.Contains("Årsmötet &amp; lunch", html);
            Assert.Contains("href=\"/umbraco/surface/SiteFile/Image?id=5\"", html);              // länken går till originalet
            Assert.Contains("src=\"/umbraco/surface/SiteFile/Image?id=5&amp;w=640\"", html);     // bilden är en miniatyr
            Assert.Contains("w=320 320w", html.Replace("&amp;", "&").Replace("&w=", "w="));
            Assert.Contains("loading=\"lazy\"", html);
            Assert.Contains("target=\"_blank\" rel=\"noopener\"", html);
            Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<b>", html);
            Assert.Contains("aria-label=\"Öppna bilden i full storlek\"", html);                  // en bild utan beskrivning och bildtext får ändå ett namn på länken
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(html, "<figure").Count);
        }

        [Fact]
        public void Beskrivningen_faller_tillbaka_på_bildtexten_och_en_bild_ägaren_inte_har_visas_aldrig()
        {
            var html = Render(G((5, null, "Bara en bildtext"), (999, "Någon annans bild", null)));
            Assert.Contains("alt=\"Bara en bildtext\"", html);
            Assert.DoesNotContain("Någon annans bild", html);
            Assert.DoesNotContain("id=999", html);
        }

        [Fact]
        public void Inga_bilder_kvar_ger_tomt_svar_inte_en_ensam_rubrik()
        {
            Assert.Equal("", Render(G()));
            Assert.Equal("", Render(G((999, "x", null))));
            Assert.Equal("", Render(new JsonObject { ["title"] = "Rubrik" }));
        }

        [Fact]
        public void Galleriet_följer_sin_nivå()
        {
            Assert.NotEqual("", Render(G((5, "a", null))));
            Assert.Equal("", Render(G((5, "a", null)), new SiteViewer(true, false, false, false)));
            Assert.Equal("", Render(G((5, "a", null)), SiteViewer.Anonymous, SiteVisibility.Inloggade));
        }

        // ── Miniatyrer ────────────────────────────────────────────────────────────────────
        private static byte[] Png(int w, int h)
        {
            using var img = new Image<Rgba32>(w, h, new Rgba32(10, 120, 200));
            using var ms = new MemoryStream(); img.Save(ms, new PngEncoder()); return ms.ToArray();
        }

        [Fact]
        public async Task En_stor_bild_förminskas_med_bevarade_proportioner_och_samma_typ()
        {
            var thumb = await SiteImages.ThumbnailAsync(Png(1600, 800), "image/png", 640);
            Assert.NotNull(thumb);
            var info = Image.Identify(thumb!);
            Assert.Equal((640, 320), (info.Width, info.Height));
            Assert.Equal("PNG", info.Metadata.DecodedImageFormat!.Name);
            Assert.True(thumb!.Length < Png(1600, 800).Length);
        }

        [Fact]
        public async Task En_bild_som_redan_är_liten_en_gif_och_en_otillåten_bredd_ger_originalet_eller_ingen_miniatyr()
        {
            Assert.Null(await SiteImages.ThumbnailAsync(Png(300, 200), "image/png", 320));     // förstoras aldrig
            Assert.Null(await SiteImages.ThumbnailAsync(Png(1600, 800), "image/png", 100));    // bara tillåtna bredder
            Assert.Null(await SiteImages.ThumbnailAsync(Png(1600, 800), "image/png", 5000));
            using var gif = new Image<Rgba32>(1600, 800); using var ms = new MemoryStream(); gif.Save(ms, new GifEncoder());
            Assert.Null(await SiteImages.ThumbnailAsync(ms.ToArray(), "image/gif", 640));       // en animerad gif behåller sina rutor
        }

        [Theory]
        [InlineData(100L * 1024 * 1024, 30L * 1024 * 1024, 70L * 1024 * 1024)]
        [InlineData(100L * 1024 * 1024, 100L * 1024 * 1024, 0L)]
        [InlineData(100L * 1024 * 1024, 150L * 1024 * 1024, 0L)]   // dokumenten har redan tagit mer än gränsen: aldrig ett negativt utrymme
        [InlineData(0L, 0L, 0L)]
        public void Bilderna_får_det_dokumenten_lämnat_kvar_av_den_gemensamma_gränsen(long limit, long docs, long expected) =>
            Assert.Equal(expected, SiteImages.QuotaFor(limit, docs));

        [Fact]
        public void Miniatyradressen_bär_bara_id_och_en_tillåten_bredd()
        {
            Assert.Equal("/umbraco/surface/SiteFile/Image?id=7&w=320", SiteImages.ThumbUrlFor(7, 320));
            Assert.Equal(new[] { 320, 640, 1280 }, SiteImages.ThumbnailWidths);
        }
    }
}
