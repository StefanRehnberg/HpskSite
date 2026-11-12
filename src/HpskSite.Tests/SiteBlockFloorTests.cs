using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Blockbiblioteket, steg 2: golv-tabellen (det öppnaste varje blocktyp får vara per ägartyp) är EN källa som skrivlagret, ritningen och editorn delar,
    /// utgångna typer ritas aldrig, och tre arkitekttester håller blockvyn ren (inget rått innerHTML, inga fasta id:n utan blockprefix, ingen innehållstjänst i läsvägarna).
    /// </summary>
    public class SiteBlockFloorTests
    {
        private static readonly SiteViewer Anonymous = SiteViewer.Anonymous;
        private static readonly SiteViewer Member = new(true, true, false, false);

        private static SiteTemplate TemplateFor(string ownerType) => ownerType == SiteTemplates.Region ? SiteTemplates.RegionTemplate : SiteTemplates.ClubTemplate;

        /// <summary>Minsta giltiga data per typ, så att Parse bedömer NIVÅN och inget annat.</summary>
        private static JsonObject Minimal(string type, SiteTemplate t) => type switch
        {
            "text" => new JsonObject { ["html"] = "<p>x</p>" },
            "heading" => new JsonObject { ["text"] = "x", ["level"] = 2 },
            "links" => new JsonObject { ["items"] = new JsonArray(new JsonObject { ["label"] = "a", ["url"] = "https://example.com/" }) },
            "documents" => new JsonObject { ["title"] = "x", ["refs"] = new JsonArray("doc:1") },
            "pagelink" => new JsonObject { ["key"] = t.Pages.First(p => !p.IsAdmin).Key, ["label"] = "x" },
            "image" => new JsonObject { ["ref"] = "file:5", ["alt"] = "x", ["decorative"] = false },
            "events" => new JsonObject { ["title"] = "x", ["count"] = 3 },
            "news" or "competitions" or "region-series" => new JsonObject { ["title"] = "x", ["count"] = 3 },
            _ => new JsonObject { ["title"] = "x" },
        };

        private static bool ParsesAt(string type, SiteVisibility blockVis, string ownerType)
        {
            var t = TemplateFor(ownerType);
            var json = new JsonObject
            {
                ["schema"] = 1,
                ["blocks"] = new JsonArray(new JsonObject { ["id"] = "abcd1", ["type"] = type, ["visibility"] = (int)blockVis, ["data"] = Minimal(type, t) }),
            }.ToJsonString();
            return SiteBlockSchema.Parse(json, SiteContentKind.FreePage, SiteVisibility.Alla, t).Ok;
        }

        public static IEnumerable<object[]> Owners() { yield return new object[] { SiteTemplates.Club }; yield return new object[] { SiteTemplates.Region }; }

        [Theory, MemberData(nameof(Owners))]
        public void Golvtabellen_och_skrivlagret_är_överens_för_varje_blocktyp(string ownerType)
        {
            var table = SiteBlockSchema.FloorTable(ownerType);
            Assert.NotEmpty(table);
            foreach (var (type, floor) in table)
            {
                Assert.True(ParsesAt(type, floor, ownerType), $"{type}: skrivlagret ska släppa in blocket på golvet ({floor})");
                if (floor > SiteVisibility.Alla)
                    Assert.False(ParsesAt(type, floor - 1, ownerType), $"{type}: skrivlagret ska vägra blocket en nivå öppnare än golvet");
            }
        }

        [Theory, MemberData(nameof(Owners))]
        public void Golvet_för_ett_personblock_är_Inloggade_och_övriga_Alla_och_golvet_hålls_vid_ritning(string ownerType)
        {
            foreach (var (type, floor) in SiteBlockSchema.FloorTable(ownerType))
            {
                var personal = SiteBlockSchema.IsPersonalBlock(type, ownerType, null);
                Assert.Equal(personal ? SiteVisibility.Inloggade : SiteVisibility.Alla, floor);
                if (!personal) continue;
                // Ritningen kapar också ett lagrat Alla: en utloggad ser aldrig ett personblock, vad raden än säger.
                var html = SiteBlockRenderer.Render(new SiteBlock("abcd1", type, SiteVisibility.Alla, Minimal(type, TemplateFor(ownerType))), SiteVisibility.Alla,
                    new SiteRenderContext(Anonymous, "/x/", TemplateFor(ownerType), new NoData()));
                Assert.Equal("", html);
            }
        }

        private sealed class NoData : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => new("Person", "a@b.se", "070");
        }

        [Fact]
        public void Kretsblock_saknas_i_golvtabellen_för_en_klubb_och_paneler_aldrig_finns_med()
        {
            Assert.DoesNotContain("region-clubs", SiteBlockSchema.FloorTable(SiteTemplates.Club).Keys);
            Assert.Contains("region-clubs", SiteBlockSchema.FloorTable(SiteTemplates.Region).Keys);
            Assert.DoesNotContain("panel", SiteBlockSchema.FloorTable(SiteTemplates.Club).Keys);   // paneler har sitt golv i panelregistret
        }

        [Fact]
        public void En_utgången_typ_är_utgången_och_en_vanlig_typ_är_det_inte()
        {
            var retired = new SiteBlockSchema.BlockType("gammal", false, false, new HashSet<string> { "title" }, Retired: true);
            var live = new SiteBlockSchema.BlockType("ny", false, false, new HashSet<string> { "title" });
            Assert.True(SiteBlockSchema.IsRetired(retired));
            Assert.False(SiteBlockSchema.IsRetired(live));
            Assert.False(SiteBlockSchema.IsRetired((SiteBlockSchema.BlockType?)null));
            Assert.False(SiteBlockSchema.IsRetired("finns-inte"));
        }

        // ── Arkitekttesterna: källfilerna läses som text. ────────────────────────────────────────────────
        private static string Source(string relative)
        {
            var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "HpskSite", relative);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }
            throw new FileNotFoundException(relative);
        }

        private const string RendererFile = "Services/SitePages/SiteBlockRenderer.cs";
        private const string ReaderFile = "Services/SitePages/SitePageReader.cs";

        [Fact]
        public void Blockvyn_skriver_aldrig_rå_html_via_innerHTML_och_ritaren_är_ren_kod()
        {
            var src = Source(RendererFile);
            Assert.DoesNotContain("innerHTML", src);
            Assert.DoesNotContain("IContentService", src);
            Assert.DoesNotContain("IScopeProvider", src);   // ren kod: ingen databas (data kommer genom ISiteBlockData)
            Assert.DoesNotContain("using Umbraco", src);
            Assert.Contains("SiteBlockSchema.IsRetired(block.Type)", src);   // kopplingen: ritningen frågar den enda funktionen om utgångna typer
        }

        [Fact]
        public void Inget_fast_id_i_blockvyn_saknar_blockets_prefix()
        {
            // Ett block kan stå flera gånger på en sida: ett fast id="…" vore en dubblett. Tillåtet är bara id byggda ur blockets eget id (id=\"{…}…).
            var src = Source(RendererFile);
            var fixedIds = Regex.Matches(src, "(?<![\\w-])id=\\\\\"(?!\\{)").Count;
            Assert.Equal(0, fixedIds);
        }

        [Fact]
        public void Läsvägarna_använder_ingen_innehållstjänst_och_ingen_trädskanning()
        {
            foreach (var file in new[] { RendererFile, ReaderFile })
            {
                var src = Source(file);
                Assert.DoesNotContain("IContentService", src);
                Assert.DoesNotContain("GetPagedChildren", src);
                Assert.DoesNotContain("GetPagedDescendants", src);
            }
        }
    }
}
