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
            public IReadOnlyList<SiteRegionClub> RegionClubs() => new[] { new SiteRegionClub(1, "KANARIE-KLUBB-X", "/halland/x/") };
            public IReadOnlyList<SiteRangeInfo> RegionRanges() => new[] { new SiteRangeInfo(1, "KANARIE-BANA-X", "Ort", "/skjutbana/1") };
            public IReadOnlyList<SiteSeriesInfo> RegionSeries(int count) => new[] { new SiteSeriesInfo("KANARIE-SERIE-X", "/serie/", null, null) };
            public SiteRegionStats? RegionStats() => new(7777, 2, 3, 4);
            public string? AboutHtml() => "<p>KANARIE-OM-X</p>";
            public IReadOnlyList<SiteCompetitionInfo> UpcomingCompetitions(int count) => new[] { new SiteCompetitionInfo("KANARIE-TAVLING-X", new DateTime(2027, 6, 1), "/t/") };
            public IReadOnlyList<SiteRecordInfo> Records() => new[] { new SiteRecordInfo("Precision", "Klass", false, "290 / 300", "KANARIE-REKORD-X", new DateTime(2026, 1, 1)) };
            public IReadOnlyList<SiteChampionInfo> Champions() => new[] { new SiteChampionInfo(2026, "Precision", "Klass", false, "290 / 300", "KANARIE-MASTARE-X") };
            public IReadOnlyList<SiteStatItem> Stats() => new[] { new SiteStatItem("KANARIE-SIFFRA-X", 4242) };
            public IReadOnlyList<SiteRangeInfo> OwnerRanges() => new[] { new SiteRangeInfo(1, "KANARIE-BANA-X", "Ort", "/skjutbana/1") };
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
                "about" => new JsonObject { ["title"] = canary },
                "competitions" => new JsonObject { ["title"] = canary, ["count"] = 3 },
                "records" => new JsonObject { ["title"] = canary },
                "ranges" => new JsonObject { ["title"] = canary },
                "champions" => new JsonObject { ["title"] = canary },
                "gallery" => new JsonObject { ["title"] = canary, ["items"] = new JsonArray(new JsonObject { ["ref"] = "file:5", ["caption"] = "KANARIE-GALLERI-X" }) },
                "stats" => new JsonObject { ["title"] = canary },
                "image" => new JsonObject { ["ref"] = "file:5", ["alt"] = canary, ["decorative"] = false },
                _ => new JsonObject(),
            };
            return new SiteBlock("abcd1", type, (SiteVisibility)vis, data);
        }

        private static readonly string[] Types = { "text", "heading", "links", "documents", "contact", "events", "pagelink", "image", "news", "about", "competitions", "records", "ranges", "champions", "stats", "gallery" };
        private static readonly string[] RegionTypes = { "region-clubs", "region-ranges", "region-series", "region-stats" };
        private static bool IsPersonal(string type) => type == "contact" || type == "champions";

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
                var marker = type switch { "contact" => "KANARIE-KONTAKT-X", "events" => "KANARIE-EVENT-X", "news" => "KANARIE-NYHET-X", "about" => "KANARIE-OM-X", "competitions" => "KANARIE-TAVLING-X", "records" => "KANARIE-REKORD-X", "ranges" => "KANARIE-BANA-X", "champions" => "KANARIE-MASTARE-X", "stats" => "KANARIE-SIFFRA-X", "gallery" => "KANARIE-GALLERI-X", _ => canary };
                var seesMarker = html.Contains(marker);
                if (blockVisible != seesMarker) failures.Add($"{(seesMarker ? "LÄCKA" : "SAKNAS")}: {persona.Name} / sida {pageVis} / {type} block {blockVis}");
                if (!blockVisible && html.Length != 0) failures.Add($"TOM SAKNAS: {persona.Name} / sida {pageVis} / {type} block {blockVis}: ett dolt block ska ge tomt svar, inte en tom ruta");
            }
            Assert.True(combos > 1500, "motorn ska ha genererat tusentals kombinationer: " + combos);
            Assert.True(failures.Count == 0, $"{failures.Count} av {combos} kombinationer fel:\n" + string.Join("\n", failures.Take(25)));
        }

        [Fact]
        public void Kretsens_block_visar_sitt_märke_om_och_bara_om_oraklet_säger_ja_och_aldrig_på_en_klubbs_sida()
        {
            var failures = new List<string>();
            var combos = 0;
            foreach (var persona in Viewers)
            foreach (var pageVis in Enumerable.Range(0, 5))
            foreach (var type in RegionTypes)
            foreach (var blockVis in Enumerable.Range(0, 5))
            {
                combos++;
                var marker = type switch { "region-clubs" => "KANARIE-KLUBB-X", "region-ranges" => "KANARIE-BANA-X", "region-series" => "KANARIE-SERIE-X", _ => "7777" };
                var block = new SiteBlock("abcd1", type, (SiteVisibility)blockVis, new JsonObject { ["title"] = "T", ["count"] = 5 });
                var visible = Oracle(Strict(blockVis, pageVis), persona.Viewer);
                var onRegion = SiteBlockRenderer.Render(block, (SiteVisibility)pageVis, new SiteRenderContext(persona.Viewer, "/halland/", SiteTemplates.RegionTemplate, new Data()));
                if (visible != onRegion.Contains(marker)) failures.Add($"{(onRegion.Contains(marker) ? "LÄCKA" : "SAKNAS")}: {persona.Name} / sida {pageVis} / {type} block {blockVis}");
                if (!visible && onRegion.Length != 0) failures.Add($"TOM SAKNAS: {persona.Name} / {type}");
                var onClub = SiteBlockRenderer.Render(block, (SiteVisibility)pageVis, new SiteRenderContext(persona.Viewer, "/halland/x/", SiteTemplates.ClubTemplate, new Data()));
                if (onClub.Length != 0) failures.Add($"KRETSBLOCK PÅ KLUBBSIDA: {persona.Name} / {type}");
            }
            // schemat: kretsens block finns bara på en krets
            foreach (var type in RegionTypes)
            {
                var json = "{\"schema\":1,\"blocks\":[{\"id\":\"abcd1\",\"type\":\"" + type + "\",\"visibility\":1,\"data\":{}}]}";
                if (SiteBlockSchema.Parse(json, SiteContentKind.FreePage, SiteVisibility.Alla, SiteTemplates.ClubTemplate).Ok) failures.Add("schemat släppte " + type + " på en klubb");
                if (!SiteBlockSchema.Parse(json, SiteContentKind.FreePage, SiteVisibility.Alla, SiteTemplates.RegionTemplate).Ok) failures.Add("schemat vägrade " + type + " på en krets");
            }
            Assert.True(combos > 500);
            Assert.True(failures.Count == 0, $"{failures.Count} av {combos} fel:\n" + string.Join("\n", failures.Distinct().Take(25)));
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
                else want = pageVis is 1 or 2 ? SitePageDecision.Locked : SitePageDecision.NotFound;   // Inloggade/Klubbmedlemmar: en låst sida (hänglås + ruta); Styrelse/Dold: 404 åt alla utan rätt
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
                var entry = menu.FirstOrDefault(e => e.Page.Label == $"MENYETIKETT-{pageVis}");
                var open = entry != null && !entry.Locked;
                var locked = entry is { Locked: true };
                var clubMenuNeedsLogin = template == SiteTemplates.Club;                  // klubbens egna sidor visas aldrig för utloggade (kretsens publika sidor syns utloggade)
                var shouldOpen = Oracle(pageVis, persona.Viewer) && !(clubMenuNeedsLogin && !persona.Viewer.LoggedIn);
                // En låst egen sida (hänglås) visas bara för en INLOGGAD som inte får se en medlemssida: aldrig för utloggade, aldrig en Styrelse-/Dold-sida.
                var shouldLock = !Oracle(pageVis, persona.Viewer) && persona.Viewer.LoggedIn && pageVis == 2;
                if (shouldOpen != open) failures.Add($"{(open ? "LÄCKA" : "SAKNAS")}: {template}, {persona.Name}, sida {pageVis}");
                if (shouldLock != locked) failures.Add($"{(locked ? "LÄCKA (hänglås)" : "SAKNAS (hänglås)")}: {template}, {persona.Name}, sida {pageVis}");
                if (!persona.Viewer.LoggedIn && clubMenuNeedsLogin && menu.Any(e => e.Page.Key.StartsWith("page:"))) failures.Add($"utloggad ser en egen etikett i klubbmenyn: {persona.Name}");
                // Systemsidorna: en post är öppen eller låst, och en sida som ger 404 för tittaren finns aldrig i menyn.
                var tpl = SiteTemplates.For(template);
                foreach (var p in tpl.Pages)
                {
                    var e = menu.FirstOrDefault(x => x.Page.Key == p.Key);
                    var decision = p.IsAdmin ? (tpl.PageVisible(p, persona.Viewer) ? SitePageDecision.Serve : SitePageDecision.NotFound) : SitePageAccessRules.Decide(tpl, p, persona.Viewer);
                    if (decision == SitePageDecision.NotFound && e != null) failures.Add($"DOLD SIDA I MENYN: {template}/{p.Key}, {persona.Name}");
                    if (decision == SitePageDecision.Serve && (e == null || e.Locked)) failures.Add($"ÖPPEN SIDA SAKNAS/LÅST: {template}/{p.Key}, {persona.Name}");
                    if (decision == SitePageDecision.Locked && (e == null || !e.Locked)) failures.Add($"LÅST SIDA SAKNAS: {template}/{p.Key}, {persona.Name}");
                }
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
            Assert.Equal(Types.Concat(RegionTypes).Append("panel").OrderBy(t => t).ToList(), registered);         // en ny blocktyp måste läggas in i motorn (och få ett orakel) innan bygget går grönt
        }

        // ── Panelblock (schema 2, konvertera-planen K1) ───────────────────────────
        /// <summary>Paneler som visar personuppgifter, handskrivet (inte ur registret): medlemskatalog, instruktörer, Guldserien, kretsens styrelse.</summary>
        private static readonly string[] PersonalPanelsClub = { "instructors", "guldserie", "member-directory" };
        private static readonly string[] PersonalPanelsRegion = { "region-instructors", "board" };

        [Fact]
        public void Registrets_personpaneler_är_exakt_de_handskrivna_så_att_en_ny_personpanel_måste_få_ett_orakel()
        {
            Assert.Equal(PersonalPanelsClub.OrderBy(x => x), PanelCatalog.Club.Where(d => d.PersonalData).Select(d => d.Key).OrderBy(x => x));
            Assert.Equal(PersonalPanelsRegion.OrderBy(x => x), PanelCatalog.Region.Where(d => d.PersonalData).Select(d => d.Key).OrderBy(x => x));
        }

        [Fact]
        public void Varje_panel_syns_om_och_bara_om_oraklet_säger_att_tittaren_får_se_den_och_personpaneler_aldrig_för_Alla()
        {
            var failures = new List<string>();
            var combos = 0;
            foreach (var template in new[] { SiteTemplates.ClubTemplate, SiteTemplates.RegionTemplate })
            foreach (var def in PanelCatalog.For(template.OwnerType))
            foreach (var pageKey in template.Pages.Where(p => !p.IsAdmin).Select(p => p.Key).Where(k => PanelCatalog.AllowedOn(template.OwnerType, k, def.Key)))
            foreach (var persona in Viewers)
            foreach (var pageVis in Enumerable.Range(0, 5))
            foreach (var blockVis in template.IsLoggedOutOnly(pageKey, def.Key) ? new[] { 5 } : Enumerable.Range(0, 5).ToArray())   // mallen avgör vad som bara finns för utloggade
            {
                combos++;
                var personal = (template.OwnerType == SiteTemplates.Club ? PersonalPanelsClub : PersonalPanelsRegion).Contains(def.Key);
                bool want;
                if (blockVis == 5) want = !persona.Viewer.LoggedIn;                           // systemrutan: bara den som inte är inloggad
                else
                {
                    var level = Strict(blockVis, pageVis);
                    if (personal && level == 0) level = 1;
                    want = Oracle(level, persona.Viewer);
                }
                var block = new SiteBlock("abcd1", "panel", (SiteVisibility)blockVis, new JsonObject { ["key"] = def.Key, ["zone"] = "full" });
                var ctx = new SiteRenderContext(persona.Viewer, "/x/", template, new Data());
                if (SiteBlockRenderer.PanelVisible(block, (SiteVisibility)pageVis, ctx) != want)
                    failures.Add($"{template.OwnerType}/{def.Key}: {persona.Name} / sida {pageVis} / block {blockVis}: väntade {want}");
                if (SiteBlockRenderer.Render(block, (SiteVisibility)pageVis, ctx).Length != 0)
                    failures.Add($"den rena renderaren skrev ut panelen {def.Key}");
            }
            Assert.True(combos > 800, "motorn ska ha genererat hundratals panelkombinationer: " + combos);
            Assert.True(failures.Count == 0, $"{failures.Count} av {combos} fel:\n" + string.Join("\n", failures.Distinct().Take(25)));
        }
    }
}
