using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Klubbsidorna 2b: "Se som". Fasta tittare, samma renderare och åtkomstregler som besökarna, alltid mot ett fristående orakel.</summary>
    public class SitePagePreviewTests
    {
        private sealed class NoData : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
        }

        private static bool Oracle(int level, SiteViewer v) => level switch
        {
            0 => true, 1 => v.LoggedIn, 2 => v.LoggedIn && (v.IsOwnerMember || v.IsOwnerAdmin), 3 => v.LoggedIn && (v.IsOwnerBoard || v.IsOwnerAdmin), 4 => v.LoggedIn && v.IsOwnerAdmin, _ => false,
        };

        private static SiteContentResult Content(int blockVis, string canary) =>
            SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":" + blockVis + ",\"data\":{\"html\":\"<p>" + canary + "</p>\"}}]}",
                SiteContentKind.FreePage, SiteVisibility.Alla, SiteTemplates.ClubTemplate);

        [Fact]
        public void Klubben_har_fyra_fasta_tittare_kretsen_tre_och_ingen_är_admin()
        {
            Assert.Equal(new[] { "anon", "logged", "member", "board" }, SitePagePreview.Personas(SiteTemplates.Club).Select(p => p.Key));
            Assert.Equal(new[] { "anon", "logged", "member", "board" }, SitePagePreview.Personas(SiteTemplates.Region).Select(p => p.Key));   // kretsmedlem (2d)
            foreach (var t in new[] { SiteTemplates.Club, SiteTemplates.Region })
                Assert.All(SitePagePreview.Personas(t), p => Assert.False(p.Viewer.IsOwnerAdmin, "adminens egen roll får aldrig färga bilden: " + p.Key));
        }

        [Fact]
        public void En_okänd_tittare_ger_null()
        {
            Assert.Null(SitePagePreview.Persona(SiteTemplates.Club, "admin"));
            Assert.Null(SitePagePreview.Persona(SiteTemplates.Region, "admin"));
            Assert.Null(SitePagePreview.Persona(SiteTemplates.Club, null));
        }

        [Fact]
        public void Varje_tittare_och_sidnivå_ger_beslutet_oraklet_väntar_och_blocket_syns_bara_för_den_som_får_se_det()
        {
            var failures = new List<string>();
            foreach (var type in new[] { SiteTemplates.Club, SiteTemplates.Region })
            foreach (var persona in SitePagePreview.Personas(type))
            foreach (var pageVis in Enumerable.Range(0, 5))
            foreach (var blockVis in Enumerable.Range(pageVis, 5 - pageVis))
            {
                var canary = $"KANARIE-{type}-{pageVis}-{blockVis}";
                var content = SiteBlockSchema.Parse("{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"text\",\"visibility\":" + blockVis + ",\"data\":{\"html\":\"<p>" + canary + "</p>\"}}]}",
                    SiteContentKind.FreePage, (SiteVisibility)pageVis, SiteTemplates.For(type));
                Assert.True(content.Ok);
                var r = SitePagePreview.Build(type, "Titel", (SiteVisibility)pageVis, content, persona, new NoData(), "/halland/x/");
                var pageOk = Oracle(pageVis, persona.Viewer);
                var blockOk = pageOk && Oracle(blockVis, persona.Viewer);
                SitePageDecision want = pageOk ? SitePageDecision.Serve
                    : persona.Viewer.LoggedIn ? SitePageDecision.NotFound
                    : pageVis is 1 or 2 ? SitePageDecision.LoginRedirect : SitePageDecision.NotFound;
                if (r.Decision != want) failures.Add($"beslut {type}/{persona.Key}/sida {pageVis}: {r.Decision} (väntade {want})");
                var shows = r.Document.Contains(canary);
                if (shows != blockOk) failures.Add($"{(shows ? "LÄCKA" : "SAKNAS")}: {type}/{persona.Key}/sida {pageVis}/block {blockVis}");
                if (r.Decision != SitePageDecision.Serve && r.Document.Length != 0) failures.Add($"dokument trots {r.Decision}: {type}/{persona.Key}/sida {pageVis}");
                if (string.IsNullOrWhiteSpace(r.Notice)) failures.Add($"inget besked: {type}/{persona.Key}/sida {pageVis}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
        }

        [Fact]
        public void När_inget_block_syns_för_tittaren_säger_beskedet_det_och_dokumentet_har_bara_rubriken()
        {
            var board = SitePagePreview.Persona(SiteTemplates.Club, "anon")!;
            var r = SitePagePreview.Build(SiteTemplates.Club, "Sida", SiteVisibility.Alla, Content(3, "STYRELSEKANARIE"), board, new NoData(), "/x/");
            Assert.Equal(SitePageDecision.Serve, r.Decision);
            Assert.Contains("ser inget av innehållet", r.Notice);
            Assert.DoesNotContain("STYRELSEKANARIE", r.Document);
            Assert.Contains("<h1", r.Document);
        }

        [Fact]
        public void Dokumentet_kodar_titeln_innehåller_inga_skript_och_laddar_bara_bootstrap_utifrån()
        {
            var doc = SitePagePreview.Document("<script>alert(1)</script>Åtta & \"citat\"", "<p>x</p>");
            Assert.DoesNotContain("<script", doc, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("&lt;script&gt;", doc);
            Assert.Contains("Åtta", doc);                                        // å/ä/ö står kvar som tecken
            var externals = System.Text.RegularExpressions.Regex.Matches(doc, "(?:href|src)=\"(https?:[^\"]+)\"").Select(m => m.Groups[1].Value).ToList();
            Assert.Equal(new[] { "https://cdn.jsdelivr.net/npm/bootstrap@5.3.8/dist/css/bootstrap.min.css" }, externals);
        }
    }
}
