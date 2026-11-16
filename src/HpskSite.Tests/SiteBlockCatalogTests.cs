using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Blockregistret (väljarens grupper, etiketter, ikoner, standardinnehåll och uppgiftskort) är bundet till schemat: ett nytt block måste finnas på båda ställena, och ett block
    /// heter samma sak i väljaren, redigeraren och "Vem ser sidan?". Registret bestämmer presentationen, schemat bestämmer vad som får finnas.
    /// </summary>
    public class SiteBlockCatalogTests
    {
        public static IEnumerable<object[]> Owners() { yield return new object[] { SiteTemplates.Club }; yield return new object[] { SiteTemplates.Region }; }

        private static SiteTemplate TemplateFor(string ownerType) => ownerType == SiteTemplates.Region ? SiteTemplates.RegionTemplate : SiteTemplates.ClubTemplate;

        [Theory, MemberData(nameof(Owners))]
        public void Registret_har_en_rad_per_blocktyp_som_ägartypen_får_ha_och_inga_andra(string ownerType)
        {
            var expected = SiteBlockSchema.Types.Values.Where(t => !t.ConvertedOnly && (!t.RegionOnly || ownerType == SiteTemplates.Region)).Select(t => t.Name).OrderBy(x => x, StringComparer.Ordinal);
            var actual = SiteBlockCatalog.For(ownerType).Select(m => m.Type).OrderBy(x => x, StringComparer.Ordinal);
            Assert.Equal(expected, actual);   // en ny typ utan registerrad, eller en registerrad utan typ, fäller bygget
        }

        [Theory, MemberData(nameof(Owners))]
        public void Registrets_flaggor_stämmer_med_schemat_och_grupperna_finns(string ownerType)
        {
            var groups = SiteBlockCatalog.Groups(ownerType);
            foreach (var m in SiteBlockCatalog.For(ownerType))
            {
                var t = SiteBlockSchema.Types[m.Type];
                Assert.True(m.Slot == t.AllowedInSlot, $"{m.Type}: platsen före/efter en systemsida ska följa schemat");
                Assert.True(m.RegionOnly == t.RegionOnly, $"{m.Type}: kretsblock ska märkas som det");
                Assert.InRange(m.Group, -1, groups.Count - 1);
                Assert.False(string.IsNullOrWhiteSpace(m.Label), m.Type);
                Assert.StartsWith("bi-", m.Icon);
                if (m.Group >= 0) Assert.False(string.IsNullOrWhiteSpace(m.Description), $"{m.Type}: väljaren säger vad blocket visar");
                if (m.Defaults == null) Assert.True(m.NeedsImage || m.Type == "pagelink", $"{m.Type}: bara bild, galleri och sidlänk bygger sitt innehåll i editorn");
            }
        }

        /// <summary>Typer vars standardinnehåll är ofärdigt med flit: servern kräver en komplett länk respektive ett valt dokument, så redigeraren sparar blocket först när det fyllts i (savableBlock i hemsidan.js).</summary>
        private static readonly string[] IncompleteByDesign = { "links", "documents" };

        [Theory, MemberData(nameof(Owners))]
        public void Standardinnehållet_är_ett_giltigt_block_på_en_sida_som_är_öppen_för_medlemmar_utom_de_två_som_fylls_i_först(string ownerType)
        {
            foreach (var m in SiteBlockCatalog.For(ownerType).Where(m => m.Defaults != null))
            {
                if (IncompleteByDesign.Contains(m.Type))
                {
                    // Låser att just de här två är ofärdiga: blir de giltiga (eller en annan typ ofärdig) ska listan och editorn ses över.
                    var bad = SiteBlockSchema.Parse(new JsonObject { ["schema"] = 1, ["blocks"] = new JsonArray(new JsonObject { ["id"] = "abcd1", ["type"] = m.Type, ["visibility"] = 2, ["data"] = JsonNode.Parse(m.Defaults!.ToJsonString()) }) }.ToJsonString(),
                        SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, TemplateFor(ownerType));
                    Assert.False(bad.Ok, $"{m.Type}: standardinnehållet är ofärdigt med flit. Är det giltigt nu? Ta bort typen ur IncompleteByDesign och ur savableBlock.");
                    continue;
                }
                var vis = (int)SiteVisibility.Klubbmedlemmar;
                var json = new JsonObject
                {
                    ["schema"] = 1,
                    ["blocks"] = new JsonArray(new JsonObject { ["id"] = "abcd1", ["type"] = m.Type, ["visibility"] = vis, ["data"] = JsonNode.Parse(m.Defaults!.ToJsonString()) }),
                }.ToJsonString();
                var r = SiteBlockSchema.Parse(json, SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, TemplateFor(ownerType));
                Assert.True(r.Ok, $"{m.Type}: standardinnehållet ska sparas som utkast. Fel: " + string.Join("; ", r.Errors.Select(e => e.Path + " " + e.Message)));
            }
        }

        [Theory, MemberData(nameof(Owners))]
        public void Uppgiftskorten_lägger_bara_block_som_finns_och_får_läggas_på_en_egen_sida(string ownerType)
        {
            var metas = SiteBlockCatalog.For(ownerType).ToDictionary(m => m.Type);
            var tasks = SiteBlockCatalog.Tasks(ownerType);
            Assert.Equal(tasks.Count, tasks.Select(t => t.Key).Distinct().Count());
            Assert.Equal(tasks.Count, tasks.Select(t => t.Title).Distinct().Count());
            foreach (var t in tasks)
            {
                Assert.NotEmpty(t.Blocks);
                foreach (var b in t.Blocks)
                {
                    Assert.True(metas.TryGetValue(b, out var meta), $"{t.Key}: blocket {b} finns inte i registret för {ownerType}");
                    Assert.True(meta!.Group >= 0, $"{t.Key}: {b} erbjuds inte längre i väljaren");
                }
                Assert.StartsWith("bi-", t.Icon);
            }
        }

        [Fact]
        public void Ett_block_heter_samma_sak_i_Vem_ser_sidan_som_i_väljaren()
        {
            var json = "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"records\",\"visibility\":2,\"data\":{\"title\":\"x\"}},{\"id\":\"abcd2\",\"type\":\"stats\",\"visibility\":2,\"data\":{\"title\":\"\"}}]}";
            var content = SiteBlockSchema.Parse(json, SiteContentKind.FreePage, SiteVisibility.Klubbmedlemmar, SiteTemplates.ClubTemplate);
            Assert.True(content.Ok);
            var m = SitePagePreview.Matrix(SiteTemplates.Club, "Sida", null, SiteVisibility.Klubbmedlemmar, content);
            var labels = m.Rows.Where(r => r.Kind == "block").Select(r => r.Label).ToArray();
            Assert.Equal(new[] { "Klubbrekord", "Klubben i siffror" }, labels);   // samma ord som i väljaren (förut "Rekord" och "Siffror")
            Assert.Equal("Klubbrekord", SiteBlockCatalog.LabelOf("records", SiteTemplates.Club));
            Assert.Equal("Kretsrekord", SiteBlockCatalog.LabelOf("records", SiteTemplates.Region));
        }
    }
}
