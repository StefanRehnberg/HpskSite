using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Läcktestmotorn (klubbsidorna plan v2 §9, G2). Genererar ALLA kombinationer av tittare × sidans synlighet × blocktyp × blockets synlighet × dokumentets nivå
    /// och prövar varje utväg som är ren kod (blockrendering, menyn, åtkomstbeslutet) mot ett FRISTÅENDE orakel. Oraklet är en sanningstabell skriven för hand
    /// (vem som ser vilken nivå), inte samma kod som <c>SiteAccess.CanSee</c>: en tabell kan inte dela ett fel med koden den prövar.
    ///
    /// <para>Varje block bär ett unikt kanariemärke. Regeln är enkel och hård: <b>märket får finnas i svaret om och bara om oraklet säger att tittaren får se
    /// blocket.</b> Ett märke hos fel tittare är ett läckage; ett saknat märke hos rätt tittare är en trasig funktion. HTTP-nivån (riktiga svar, huvuden, filer)
    /// prövas av <c>hpsk-verify/klubbsidor-2a-lasvag-verify.mjs</c>; det här är motorn som aldrig glömmer en kombination.</para>
    /// </summary>
    public class SiteLeakEngineTests
    {
        // ── Tittarna ──────────────────────────────────────────────────────────────
        private sealed record Persona(string Name, SiteViewer Viewer);

        private static readonly Persona[] Viewers =
        {
            new("utloggad", SiteViewer.Anonymous),
            new("inloggad, inte medlem", new SiteViewer(true, false, false, false)),
            new("medlem", new SiteViewer(true, true, false, false)),
            new("styrelse (ej medlem)", new SiteViewer(true, false, true, false)),
            new("styrelse och medlem", new SiteViewer(true, true, true, false)),
            new("admin", new SiteViewer(true, false, false, true)),
            new("admin, styrelse och medlem", new SiteViewer(true, true, true, true)),
        };

        // ── Oraklet: en sanningstabell, inte SiteAccess ────────────────────────────
        /// <summary>Får <paramref name="v"/> se något på nivå <paramref name="level"/> (0 Alla, 1 Inloggade, 2 Klubbmedlemmar, 3 Styrelse, 4 Admin)?</summary>
        private static bool Oracle(int level, SiteViewer v) => level switch
        {
            0 => true,
            1 => v.LoggedIn,
            2 => v.LoggedIn && (v.IsOwnerMember || v.IsOwnerAdmin),
            3 => v.LoggedIn && (v.IsOwnerBoard || v.IsOwnerAdmin),
            4 => v.LoggedIn && v.IsOwnerAdmin,
            _ => false,
        };

        /// <summary>Den striktaste av två nivåer (högre siffra = striktare). Skriven här, inte hämtad från SiteAccess.Strictest.</summary>
        private static int Strict(int a, int b) => a > b ? a : b;

        private static int DocLevelToVisibility(int accessLevel) => accessLevel switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };

        // ── Blocken ───────────────────────────────────────────────────────────────
        private sealed class Data : ISiteBlockData
        {
            public List<SiteDocumentInfo> Docs = new();
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Docs;
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => new[] { new SiteEventInfo("KANARIE-EVENT-X", new DateTime(2027, 5, 1, 18, 0, 0), "Banan", "/x/") };
            public SiteContactInfo? Contact() => new("KANARIE-KONTAKT-X", "kanarie@example.se", "0701234567");
            public IReadOnlyList<SiteNewsInfo> News(int count) => new[] { new SiteNewsInfo("KANARIE-NYHET-X", new DateTime(2027, 4, 1), "/x/") };
            public IReadOnlyList<SiteImageInfo> Images(IReadOnlyList<int> ids) => new[] { new SiteImageInfo(5, 40, 30) };   // en bild som ägaren har; märket ligger i beskrivningen
        }

        /// <summary>Ett block av varje typ med ett unikt märke i det som skrivs ut. Märket för dokument ligger i dokumentens titlar (per nivå).</summary>
        private static SiteBlock Block(string type, int vis, string canary)
        {
            JsonObject data = type switch
            {
                "text" => new JsonObject { ["html"] = $"<p>{canary}</p>" },
                "heading" => new JsonObject { ["text"] = canary, ["level"] = 2 },
                "links" => new JsonObject { ["items"] = new JsonArray { new JsonObject { ["label"] = canary, ["url"] = "https://pistol.nu/x" } } },
                "documents" => new JsonObject { ["refs"] = new JsonArray("doc:10", "doc:11", "doc:12", "doc:13") },
                "contact" => new JsonObject { ["title"] = canary },
                "events" => new JsonObject { ["title"] = canary, ["count"] = 3 },
                "pagelink" => new JsonObject { ["key"] = "records", ["label"] = canary },
                "news" => new JsonObject { ["title"] = canary, ["count"] = 3 },
                "image" => new JsonObject { ["ref"] = "file:5", ["alt"] = canary, ["decorative"] = false },
                _ => new JsonObject(),
            };
            return new SiteBlock("abcd1", type, (SiteVisibility)vis, data);
        }

        private static readonly string[] Types = { "text", "heading", "links", "documents", "contact", "events", "pagelink", "image", "news" };
        private static bool IsPersonal(string type) => type == "contact";

        [Fact]
        public void Varje_kombination_visar_ett_blockmärke_om_och_bara_om_oraklet_säger_att_tittaren_får_se_det()
        {
            var failures = new List<string>();
            var combos = 0;
            foreach (var persona in Viewers)
            foreach (var pageVis in Enumerable.Range(0, 5))
            foreach (var type in Types)
            foreach (var blockVis in Enumerable.Range(0, 5))          // även block LÖSARE än sidan: renderingen ska ändå hålla sidans nivå
            foreach (var docLevel in type == "documents" ? Enumerable.Range(0, 4) : new[] { 0 })
            {
                combos++;
                var canary = $"KANARIE-{type}-{blockVis}-{pageVis}-{docLevel}";
                var data = new Data();
                // Fyra dokument, ett per nivå, med nivåns namn som märke. Bara det på testets nivå ska kunna synas.
                data.Docs = Enumerable.Range(0, 4).Select(l => new SiteDocumentInfo(10 + l, $"{canary}-DOK{l}", null, 1, (SiteVisibility)DocLevelToVisibility(l))).ToList();
                var ctx = new SiteRenderContext(persona.Viewer, "/halland/ankeborg/", SiteTemplates.ClubTemplate, data);
                var html = SiteBlockRenderer.Render(Block(type, blockVis, canary), (SiteVisibility)pageVis, ctx);

                var effective = Strict(blockVis, pageVis);
                if (IsPersonal(type) && effective == 0) effective = 1;                    // personblock aldrig Alla
                var blockVisible = Oracle(effective, persona.Viewer);

                if (type == "documents")
                {
                    for (var l = 0; l < 4; l++)
                    {
                        var shouldSee = blockVisible && Oracle(Strict(effective, DocLevelToVisibility(l)), persona.Viewer);
                        var sees = html.Contains($"{canary}-DOK{l}");
                        if (shouldSee != sees) failures.Add($"{(sees ? "LÄCKA" : "SAKNAS")}: {persona.Name} / sida {pageVis} / dokumentblock {blockVis} / dokument nivå {l}");
                    }
                    continue;
                }
                if (type == "pagelink")
                    blockVisible = blockVisible && Oracle(1, persona.Viewer);              // länken går till Rekord (kräver inloggning) och visas bara för den som får se den
                var marker = type switch { "contact" => "KANARIE-KONTAKT-X", "events" => "KANARIE-EVENT-X", "news" => "KANARIE-NYHET-X", _ => canary };
                var seesMarker = html.Contains(marker);
                if (blockVisible != seesMarker) failures.Add($"{(seesMarker ? "LÄCKA" : "SAKNAS")}: {persona.Name} / sida {pageVis} / {type} block {blockVis}");
                if (!blockVisible && html.Length != 0) failures.Add($"TOM SAKNAS: {persona.Name} / sida {pageVis} / {type} block {blockVis}: ett dolt block ska ge tomt svar, inte en tom ruta");
            }
            Assert.True(combos > 1500, "motorn ska ha genererat tusentals kombinationer: " + combos);
            Assert.True(failures.Count == 0, $"{failures.Count} av {combos} kombinationer fel:\n" + string.Join("\n", failures.Take(25)));
        }

        // ── Åtkomstbeslutet ───────────────────────────────────────────────────────
        [Fact]
        public void Åtkomstbeslutet_följer_oraklet_för_varje_tittare_och_sidnivå_och_avslöjar_aldrig_en_dold_sida()
        {
            var failures = new List<string>();
            foreach (var persona in Viewers)
            foreach (var pageVis in Enumerable.Range(0, 5))
            {
                var page = new SitePageDef("page", "x", "X", 1, new[] { new SiteBlockDef("page", (SiteVisibility)pageVis) });
                var got = SitePageAccessRules.Decide(SiteTemplates.ClubTemplate, page, persona.Viewer);
                SitePageDecision want;
                if (Oracle(pageVis, persona.Viewer)) want = SitePageDecision.Serve;
                else if (persona.Viewer.LoggedIn) want = SitePageDecision.NotFound;
                else want = pageVis is 1 or 2 ? SitePageDecision.LoginRedirect : SitePageDecision.NotFound;   // Styrelse/Dold: 404 åt alla utan rätt, även utloggade
                if (got != want) failures.Add($"{persona.Name} / sida {pageVis}: fick {got}, väntade {want}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ── Menyn ─────────────────────────────────────────────────────────────────
        [Fact]
        public void Menyn_visar_en_egen_sidas_etikett_om_och_bara_om_tittaren_får_se_sidan_och_utloggade_ser_aldrig_en_klubbmeny()
        {
            var failures = new List<string>();
            foreach (var template in new[] { SiteTemplates.Club, SiteTemplates.Region })
            foreach (var persona in Viewers)
            foreach (var pageVis in Enumerable.Range(0, 5))
            {
                var extra = new[] { new SiteMenuPage(7, $"MENYETIKETT-{pageVis}", "etikett", (SiteVisibility)pageVis, 10) };
                var menu = SitePageView.On(template, "/halland/x/", "home", persona.Viewer, extra).Menu();
                var shown = menu.Any(e => e.Page.Label == $"MENYETIKETT-{pageVis}");
                var clubMenuNeedsLogin = template == SiteTemplates.Club;                  // klubbens meny finns bara för inloggade (kretsens publika sidor syns utloggade)
                var should = Oracle(pageVis, persona.Viewer) && !(clubMenuNeedsLogin && !persona.Viewer.LoggedIn);
                if (should != shown) failures.Add($"{(shown ? "LÄCKA" : "SAKNAS")}: {template}, {persona.Name}, sida {pageVis}");
                if (!persona.Viewer.LoggedIn && clubMenuNeedsLogin && menu.Count != 0) failures.Add($"utloggad ser en klubbmeny ({menu.Count} poster): {persona.Name}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ── Systemsidornas mall ───────────────────────────────────────────────────
        [Fact]
        public void Mallens_systemsidor_visas_i_menyn_exakt_för_dem_som_oraklet_släpper_in_och_Admin_bara_för_ägarens_admin()
        {
            var failures = new List<string>();
            foreach (var template in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
            foreach (var persona in Viewers)
            foreach (var page in template.Pages)
            {
                var got = template.PageVisible(page, persona.Viewer);
                // Oraklet: en sida syns om minst ett av dess block syns för tittaren (personblock aldrig Alla); Admin bara för ägarens admin.
                bool want;
                if (page.IsAdmin) want = persona.Viewer.LoggedIn && persona.Viewer.IsOwnerAdmin;
                else want = page.Blocks.Where(b => !b.Retired).Any(b =>
                {
                    var level = (int)b.Visibility;
                    if (level == 5) return !persona.Viewer.LoggedIn;                        // Utloggad: bara systemblock, bara den som inte är inloggad
                    if (b.PersonalData && level == 0) level = 1;
                    return Oracle(level, persona.Viewer);
                });
                if (got != want) failures.Add($"{template.OwnerType}/{page.Key}/{persona.Name}: fick {got}, väntade {want}");
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        // ── Övriga utvägar som ren kod ─────────────────────────────────────────────
        [Fact]
        public void Blockschemat_vägrar_en_synlighet_som_ger_ett_läckage()
        {
            string Page(int blockVis, string type = "text", string data = "{\"html\":\"x\"}") =>
                "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"" + type + "\",\"visibility\":" + blockVis + ",\"data\":" + data + "}]}";
            for (var pageVis = 0; pageVis <= 4; pageVis++)
            for (var blockVis = 0; blockVis <= 6; blockVis++)
            {
                var r = SiteBlockSchema.Parse(Page(blockVis), SiteContentKind.FreePage, (SiteVisibility)pageVis, SiteTemplates.ClubTemplate);
                var allowed = blockVis is >= 0 and <= 4 && blockVis >= pageVis;             // aldrig öppnare än sidan, aldrig 5 (Utloggad) eller utanför
                Assert.True(r.Ok == allowed, $"sida {pageVis} / block {blockVis}: schemat sa {(r.Ok ? "ok" : "nej")}, väntade {(allowed ? "ok" : "nej")}");
            }
            for (var pageVis = 0; pageVis <= 4; pageVis++)                                  // personblock aldrig Alla
            {
                var r = SiteBlockSchema.Parse(Page(0, "contact", "{}"), SiteContentKind.FreePage, (SiteVisibility)pageVis, SiteTemplates.ClubTemplate);
                Assert.False(r.Ok, $"contact med synlighet Alla släpptes igenom på en sida med nivå {pageVis}");
            }
        }

        [Fact]
        public void Motorn_känner_till_alla_blocktyper_så_att_en_ny_typ_inte_kan_glömmas()
        {
            var registered = SiteBlockSchema.Types.Keys.OrderBy(k => k).ToList();
            Assert.Equal(Types.OrderBy(t => t).ToList(), registered);                         // en ny blocktyp måste läggas in i motorn (och få ett orakel) innan bygget går grönt
        }
    }
}
