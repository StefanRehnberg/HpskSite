using System.Text.Json.Nodes;
using HpskSite.Services.SitePages;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Reglerna för en anmälan (ren kod): vad som krävs, vad som godtas och normaliseras, och att ett fält formuläret inte bad om aldrig lagras.</summary>
    public class SiteSignupRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 10);
        private static SiteSignupConfig Cfg(bool phone = false, bool address = false, bool gender = false, bool pnr = false, bool message = false, string[]? choices = null, DateTime? opens = null, DateTime? closes = null) =>
            new("Anmälan", null, choices ?? Array.Empty<string>(), phone, address, gender, pnr, message, opens, closes, null);
        private static SiteSignupInput In(string? name = "Anna Ek", string? email = "anna@example.se", string? phone = null, string? gender = null, string? pnr = null, string? address = null,
            string? postal = null, string? city = null, string? message = null, string? choice = null, bool consent = true) =>
            new(name, email, phone, gender, pnr, address, postal, city, message, choice, consent);

        [Fact]
        public void Namn_och_e_post_räcker_och_läggs_trimmade()
        {
            var r = SiteSignupRules.Validate(Cfg(), In("  Anna Ek ", " anna@example.se "), Today);
            Assert.True(r.Ok);
            Assert.Equal(("Anna Ek", "anna@example.se"), (r.Data!.Name, r.Data.Email));
        }

        [Theory]
        [InlineData(null)] [InlineData("")] [InlineData("A")] [InlineData("<b>Anna</b>")] [InlineData("Anna\tEk")]
        public void Ett_ogiltigt_namn_vägras(string? name) => Assert.False(SiteSignupRules.Validate(Cfg(), In(name), Today).Ok);

        [Theory]
        [InlineData("anna")] [InlineData("anna@")] [InlineData("anna@exempel")] [InlineData("a b@exempel.se")] [InlineData("a@exempel.se, b@exempel.se")] [InlineData("a@exempel.se;b@exempel.se")]
        [InlineData("Anna <anna@exempel.se>")] [InlineData("")] [InlineData(null)]
        public void En_ogiltig_e_postadress_vägras_också_flera_adresser(string? email) => Assert.False(SiteSignupRules.Validate(Cfg(), In(email: email), Today).Ok);

        [Fact]
        public void Samtycke_krävs()
        {
            var r = SiteSignupRules.Validate(Cfg(), In(consent: false), Today);
            Assert.False(r.Ok);
            Assert.Contains(r.Errors, e => e.Field == "consent");
        }

        [Fact]
        public void Fält_formuläret_inte_bad_om_lagras_aldrig_även_om_de_skickas()
        {
            var r = SiteSignupRules.Validate(Cfg(), In(phone: "0701234567", gender: "Man", pnr: "19811218-9876", address: "Gatan 1", postal: "12345", city: "Staden", message: "Hej"), Today);
            Assert.True(r.Ok);
            Assert.Null(r.Data!.Phone); Assert.Null(r.Data.Gender); Assert.Null(r.Data.PersonalNumber); Assert.Null(r.Data.Address); Assert.Null(r.Data.PostalCode); Assert.Null(r.Data.City); Assert.Null(r.Data.Message);
        }

        [Fact]
        public void Begärda_fält_krävs_utom_kön_och_meddelande_som_är_frivilliga()
        {
            var cfg = Cfg(phone: true, address: true, gender: true, pnr: true, message: true);
            var empty = SiteSignupRules.Validate(cfg, In(), Today);
            Assert.False(empty.Ok);
            foreach (var f in new[] { "phone", "address", "postalCode", "city", "personalNumber" }) Assert.Contains(empty.Errors, e => e.Field == f);
            Assert.DoesNotContain(empty.Errors, e => e.Field is "gender" or "message");
            var ok = SiteSignupRules.Validate(cfg, In(phone: "070-123 45 67", address: "Storgatan 1", postal: "41101", city: "Göteborg", pnr: "198112189876", gender: "Vill inte ange", message: "Hej"), Today);
            Assert.True(ok.Ok, string.Join(";", ok.Errors));
            Assert.Equal(("070-123 45 67", "Storgatan 1", "411 01", "Göteborg", "19811218-9876", "Vill inte ange", "Hej"),
                (ok.Data!.Phone, ok.Data.Address, ok.Data.PostalCode, ok.Data.City, ok.Data.PersonalNumber, ok.Data.Gender, ok.Data.Message));
        }

        [Theory]
        [InlineData("abc")] [InlineData("12")] [InlineData("<script>")] [InlineData("0701234567890123456789")]
        public void Ett_ogiltigt_telefonnummer_vägras(string phone) => Assert.False(SiteSignupRules.Validate(Cfg(phone: true), In(phone: phone), Today).Ok);

        [Theory]
        [InlineData("1234")] [InlineData("123456")] [InlineData("12 3456")] [InlineData("abcde")]
        public void Ett_ogiltigt_postnummer_vägras(string postal) => Assert.False(SiteSignupRules.Validate(Cfg(address: true), In(address: "Gatan 1", postal: postal, city: "Staden"), Today).Ok);

        [Fact]
        public void Kön_måste_vara_ett_av_alternativen()
        {
            Assert.True(SiteSignupRules.Validate(Cfg(gender: true), In(gender: "Kvinna"), Today).Ok);
            Assert.True(SiteSignupRules.Validate(Cfg(gender: true), In(gender: null), Today).Ok);
            Assert.False(SiteSignupRules.Validate(Cfg(gender: true), In(gender: "Valfri text"), Today).Ok);
        }

        [Theory]
        [InlineData("19811218-9876", "19811218-9876")]
        [InlineData("198112189876", "19811218-9876")]
        [InlineData("19811278-9873", "19811278-9873")]   // samordningsnummer (dag + 60)
        public void Giltiga_personnummer_normaliseras(string raw, string expected) => Assert.Equal(expected, SiteSignupRules.NormalizePersonalNumber(raw, Today));

        [Theory]
        [InlineData("19811218-9875")]   // fel kontrollsiffra
        [InlineData("811218-9876")]     // tio siffror: århundradet måste anges
        [InlineData("19810230-1234")]   // 30 februari
        [InlineData("20300101-1234")]   // framtiden
        [InlineData("18991231-1234")]
        [InlineData("1981121898767")]
        [InlineData("abcdefgh-ijkl")]
        [InlineData("")]
        public void Ogiltiga_personnummer_vägras(string raw) => Assert.Null(SiteSignupRules.NormalizePersonalNumber(raw, Today));

        [Fact]
        public void Valen_måste_matcha_exakt_och_krävs_när_blocket_har_val()
        {
            var cfg = Cfg(choices: new[] { "Grundkurs pistol", "Skytt sedan tidigare" });
            Assert.True(SiteSignupRules.Validate(cfg, In(choice: "Grundkurs pistol"), Today).Ok);
            Assert.False(SiteSignupRules.Validate(cfg, In(choice: "grundkurs pistol"), Today).Ok);          // inte "ungefär"
            Assert.False(SiteSignupRules.Validate(cfg, In(choice: "Något helt annat"), Today).Ok);
            Assert.False(SiteSignupRules.Validate(cfg, In(choice: null), Today).Ok);
            Assert.Null(SiteSignupRules.Validate(Cfg(), In(choice: "Grundkurs pistol"), Today).Data!.Choice);   // inga val i blocket: inget val lagras
        }

        [Fact]
        public void Öppnings_och_stängningsdagen_är_öppna_och_utanför_är_stängt()
        {
            var cfg = Cfg(opens: new DateTime(2026, 11, 10), closes: new DateTime(2026, 11, 20));
            Assert.Equal(SiteSignupOpenState.NotYet, cfg.StateOn(new DateTime(2026, 11, 9)));
            Assert.Equal(SiteSignupOpenState.Open, cfg.StateOn(new DateTime(2026, 11, 10)));
            Assert.Equal(SiteSignupOpenState.Open, cfg.StateOn(new DateTime(2026, 11, 20, 23, 59, 0)));
            Assert.Equal(SiteSignupOpenState.Closed, cfg.StateOn(new DateTime(2026, 11, 21)));
            Assert.False(SiteSignupRules.Validate(cfg, In(), new DateTime(2026, 11, 21)).Ok);               // servern prövar datumet, inte bara sidan
            Assert.Contains(SiteSignupRules.Validate(cfg, In(), new DateTime(2026, 11, 21)).Errors, e => e.Field == "");
        }

        [Fact]
        public void Felkoder_ger_bara_kända_texter_och_en_okänd_kod_ekas_aldrig()
        {
            Assert.Contains("inte öppen", SiteSignupRules.ErrorText("closed"));
            Assert.Null(SiteSignupRules.ErrorText(null));
            var t = SiteSignupRules.ErrorText("<script>alert(1)</script>")!;
            Assert.DoesNotContain("script", t);
        }
    }

    /// <summary>Blockets inställningar (schemat) och ritningen: ett vanligt HTML-formulär utan JS, kodat, med skyddstoken och honungsfält.</summary>
    public class SiteSignupBlockTests
    {
        private static SiteContentResult Parse(string data, SiteContentKind kind = SiteContentKind.FreePage, string? pageKey = null, bool region = false)
        {
            var b = "{\"id\":\"abcd1\",\"type\":\"signup\",\"visibility\":2,\"data\":" + data + "}";
            var schema = kind == SiteContentKind.ConvertedPage ? 2 : 1;
            return SiteBlockSchema.Parse("{\"schema\":" + schema + ",\"blocks\":[" + b + "]}", kind, SiteVisibility.Klubbmedlemmar,
                region ? SiteTemplates.RegionTemplate : SiteTemplates.ClubTemplate, pageKey);
        }

        [Fact]
        public void Ett_tomt_formulär_och_ett_fullt_formulär_godtas_och_okända_fält_vägras()
        {
            Assert.True(Parse("{}").Ok);
            Assert.True(Parse("{\"title\":\"Grundkurs\",\"intro\":\"Hej\",\"choices\":[\"A\",\"B\"],\"phone\":true,\"address\":true,\"gender\":true,\"pnr\":true,\"message\":true,\"opens\":\"2026-11-01\",\"closes\":\"2026-12-01\",\"thanks\":\"Tack\"}", region: true).Ok);
            Assert.False(Parse("{\"toEmail\":\"x@y.se\"}").Ok);      // ingen mottagare ur blocket: inget att missbruka
            Assert.False(Parse("{\"action\":\"https://elak.se/\"}").Ok);
            Assert.False(SiteBlockSchema.Types["signup"].Personal);
        }

        [Theory]
        [InlineData("{\"phone\":\"ja\"}")] [InlineData("{\"pnr\":1}")]
        [InlineData("{\"choices\":\"A\"}")] [InlineData("{\"choices\":[\"A\",\"a\"]}")] [InlineData("{\"choices\":[5]}")] [InlineData("{\"choices\":[null]}")] [InlineData("{\"choices\":[\"<b>\"]}")]
        [InlineData("{\"opens\":\"imorgon\"}")] [InlineData("{\"closes\":\"2026-13-40\"}")] [InlineData("{\"opens\":\"2026-12-01\",\"closes\":\"2026-11-01\"}")]
        [InlineData("{\"title\":\"<script>\"}")] [InlineData("{\"intro\":\"x\\ny\"}")]
        public void Ogiltiga_inställningar_avvisas(string data) => Assert.False(Parse(data).Ok, data);

        [Fact]
        public void Taken_gäller_val_och_texter()
        {
            Assert.True(Parse("{\"choices\":[" + string.Join(",", Enumerable.Range(1, 10).Select(i => "\"Val " + i + "\"")) + "]}").Ok);
            Assert.False(Parse("{\"choices\":[" + string.Join(",", Enumerable.Range(1, 11).Select(i => "\"Val " + i + "\"")) + "]}").Ok);
            Assert.False(Parse("{\"intro\":\"" + new string('x', 601) + "\"}").Ok);
            Assert.False(Parse("{\"thanks\":\"" + new string('x', 301) + "\"}").Ok);
        }

        [Fact]
        public void Blocket_finns_på_en_egen_sida_och_en_omgjord_systemsida_men_inte_i_den_gamla_formen()
        {
            Assert.True(Parse("{}").Ok);
            Assert.True(Parse("{}", SiteContentKind.ConvertedPage, "about").Ok);   // en omgjord sida har ett eget sid-id att posta med (se SiteSignupOnConvertedPageTests)
            Assert.False(Parse("{}", SiteContentKind.TemplateCopy, "about").Ok);   // platser före/efter har inget eget innehåll att läsa om vid inskickningen
        }

        [Fact]
        public void Ett_tomt_val_släpps_tyst_så_att_autosparningen_inte_felar_medan_någon_skriver()
        {
            var c = Parse("{\"choices\":[\"A\",\"\",\"  \"]}");
            Assert.True(c.Ok);
            Assert.Equal(new[] { "A" }, SiteSignupConfig.FromBlock(c.Blocks[0]).Choices.ToArray());
            Assert.True(Parse("{\"choices\":[\"\"]}").Ok);
            Assert.False(Parse("{\"choices\":[\"\"]}").Blocks[0].Data.ContainsKey("choices"));
        }

        [Fact]
        public void Falska_flaggor_sparas_inte_och_valen_normaliseras()
        {
            var c = Parse("{\"phone\":false,\"gender\":false,\"choices\":[\"  A  \",\"B\"]}");
            Assert.True(c.Ok);
            var d = c.Blocks[0].Data;
            Assert.False(d.ContainsKey("phone")); Assert.False(d.ContainsKey("gender"));
            Assert.Equal(new[] { "A", "B" }, SiteSignupConfig.FromBlock(c.Blocks[0]).Choices.ToArray());
        }

        private sealed class FormData : ISiteBlockData
        {
            public SiteFormContext? Ctx = new(7, "TOKEN-123", null);
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public SiteFormContext? Form() => Ctx;
        }

        private static readonly SiteViewer Member = new(true, true, false, false);

        private static string Render(JsonObject data, FormData? d = null, SiteViewer? v = null, SiteVisibility vis = SiteVisibility.Alla) =>
            SiteBlockRenderer.Render(new SiteBlock("abcd1", "signup", vis, data), SiteVisibility.Alla,
                new SiteRenderContext(v ?? SiteViewer.Anonymous, "/halland/ankeborg/", SiteTemplates.RegionTemplate, d ?? new FormData()));

        private static JsonObject Full() => new()
        {
            ["title"] = "Grundkurs <pistol> & mer", ["intro"] = "Anmäl dig här",
            ["choices"] = new JsonArray("Grundkurs <A>", "Skytt sedan tidigare"),
            ["phone"] = true, ["address"] = true, ["gender"] = true, ["pnr"] = true, ["message"] = true,
        };

        [Fact]
        public void Formuläret_ritas_med_token_sidans_och_blockets_id_honungsfält_och_kodade_texter()
        {
            var html = Render(Full());
            Assert.Contains("method=\"post\" action=\"/umbraco/surface/SiteForm/Submit\"", html);
            Assert.Contains("name=\"__RequestVerificationToken\" value=\"TOKEN-123\"", html);
            Assert.Contains("name=\"PageId\" value=\"7\"", html);
            Assert.Contains("name=\"BlockId\" value=\"abcd1\"", html);
            Assert.Contains("name=\"Website\"", html);                                          // honungsfältet
            Assert.Contains("Grundkurs &lt;pistol&gt; &amp; mer", html);
            Assert.Contains("Grundkurs &lt;A&gt;", html);
            Assert.DoesNotContain("<pistol>", html);
            foreach (var n in new[] { "Name", "Email", "Phone", "Address", "PostalCode", "City", "Gender", "PersonalNumber", "Message", "Choice", "Consent" })
                Assert.Contains($"name=\"{n}\"", html);
            Assert.Contains("href=\"/integritetspolicy\"", html);
            // Formuläret har ett enda eget skript (fälten sparas i webbläsaren efter ett fel) och inget av det som redaktören skrivit hamnar i det
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(html, "<script", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count);
            var script = html.Substring(html.IndexOf("<script", StringComparison.Ordinal), html.IndexOf("</script>", StringComparison.Ordinal) - html.IndexOf("<script", StringComparison.Ordinal));
            Assert.DoesNotContain("Grundkurs", script);
            Assert.DoesNotContain("onsubmit", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Bara_de_begärda_fälten_ritas()
        {
            var html = Render(new JsonObject());
            foreach (var n in new[] { "Name", "Email", "Consent" }) Assert.Contains($"name=\"{n}\"", html);
            foreach (var n in new[] { "Phone", "Address", "PostalCode", "City", "Gender", "PersonalNumber", "Message", "Choice" }) Assert.DoesNotContain($"name=\"{n}\"", html);
            Assert.Contains("<h3>Anmälan</h3>", html);   // standardrubrik
        }

        [Fact]
        public void Varje_fält_har_ett_id_byggt_av_blockets_id_så_att_två_formulär_aldrig_krockar()
        {
            var html = Render(new JsonObject());
            Assert.Contains("id=\"abcd1-name\"", html); Assert.Contains("for=\"abcd1-name\"", html); Assert.Contains("id=\"abcd1-anmalan\"", html);
        }

        [Fact]
        public void Efter_en_lyckad_inskickning_visas_tacket_för_det_blocket_och_inget_formulär()
        {
            var d = new FormData { Ctx = new SiteFormContext(7, "T", new SiteFormFlash("abcd1", true, null)) };
            var html = Render(new JsonObject { ["thanks"] = "Tack <3" }, d);
            Assert.Contains("Tack &lt;3", html);
            Assert.DoesNotContain("<form", html);
            Assert.Contains("<form", Render(new JsonObject(), new FormData { Ctx = new SiteFormContext(7, "T", new SiteFormFlash("annat1", true, null)) }));   // ett tack för ett annat block rör inte det här
            Assert.Contains("Tack! Vi har tagit emot", Render(new JsonObject(), d));                                                                      // standardtack
        }

        [Fact]
        public void Ett_fel_visar_bara_en_känd_text_och_formuläret_finns_kvar()
        {
            var d = new FormData { Ctx = new SiteFormContext(7, "T", new SiteFormFlash("abcd1", false, "rate")) };
            var html = Render(new JsonObject(), d);
            Assert.Contains("alert-danger", html); Assert.Contains("Vänta en stund", html); Assert.Contains("<form", html);
            var unknown = Render(new JsonObject(), new FormData { Ctx = new SiteFormContext(7, "T", new SiteFormFlash("abcd1", false, "<script>x</script>")) });
            Assert.DoesNotContain("<script>x", unknown);
        }

        [Fact]
        public void Före_öppnandet_och_efter_stängningen_visas_ett_besked_och_inget_formulär()
        {
            var tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd"); var yesterday = DateTime.Today.AddDays(-1).ToString("yyyy-MM-dd");
            var notYet = Render(new JsonObject { ["opens"] = tomorrow });
            var closed = Render(new JsonObject { ["closes"] = yesterday });
            Assert.Contains("Anmälan öppnar", notYet); Assert.DoesNotContain("<form", notYet);
            Assert.Contains("Anmälan har stängt", closed); Assert.DoesNotContain("<form", closed);
            Assert.Contains("<form", Render(new JsonObject { ["opens"] = DateTime.Today.ToString("yyyy-MM-dd"), ["closes"] = DateTime.Today.ToString("yyyy-MM-dd") }));
        }

        [Fact]
        public void En_förhandsvisning_ritar_formuläret_avstängt_utan_mål_och_token()
        {
            var html = Render(Full(), new FormData { Ctx = null });
            Assert.Contains("<fieldset disabled>", html);
            Assert.DoesNotContain("action=", html); Assert.DoesNotContain("__RequestVerificationToken", html); Assert.DoesNotContain("TOKEN-123", html);
            Assert.Contains("Förhandsvisning", html);
            Assert.DoesNotContain("onsubmit", html, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Ett_formulär_utan_token_ritas_också_avstängt_aldrig_med_ett_tomt_skyddsfält()
        {
            var html = Render(new JsonObject(), new FormData { Ctx = new SiteFormContext(7, null, null) });
            Assert.Contains("<fieldset disabled>", html); Assert.DoesNotContain("action=", html);
        }

        [Fact]
        public void Blocket_följer_sin_nivå_en_icke_medlem_ser_inget_när_blocket_är_stängt_för_medlemmar()
        {
            Assert.NotEqual("", Render(new JsonObject(), v: new SiteViewer(true, false, false, false)));   // Alla: öppet
            Assert.Equal("", SiteBlockRenderer.Render(new SiteBlock("abcd1", "signup", SiteVisibility.Klubbmedlemmar, new JsonObject()), SiteVisibility.Klubbmedlemmar,
                new SiteRenderContext(new SiteViewer(true, false, false, false), "/x/", SiteTemplates.ClubTemplate, new FormData())));
            Assert.Equal("", SiteBlockRenderer.Render(new SiteBlock("abcd1", "signup", SiteVisibility.Inloggade, new JsonObject()), SiteVisibility.Inloggade,
                new SiteRenderContext(SiteViewer.Anonymous, "/x/", SiteTemplates.ClubTemplate, new FormData())));
        }

        [Fact]
        public void Personnummerspärren_läser_formulärets_texter()
        {
            var c = Parse("{\"intro\":\"Ring 19811218-9876\"}");
            Assert.True(c.Ok);
            Assert.NotNull(SitePersonalNumberGuard.FindInAny(SitePageWriter.TextOf("Sida", c)));
        }
    }

    /// <summary>Lagringen mot en riktig SQL Server: dubbletter, tak, ägarisolering, status, arkiv, CSV och gallring. Allt rullas tillbaka.</summary>
    [Collection(SiteSqlCollection.Name)]
    public class SiteSignupStoreTests
    {
        private const string Club = "Club";
        private const int Owner = 990021;
        private const int OtherOwner = 990022;
        private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0);
        private static readonly SiteActor Actor = new(7);

        private static void Seed(IDatabase db) { foreach (var id in new[] { Owner, OtherOwner }) db.Execute("INSERT INTO dbo.SiteOwner (OwnerType, OwnerId, IsEnabled) VALUES ('Club', @0, 1)", id); }

        private static SiteSignupData D(string name = "Anna Ek", string email = "anna@example.se", string? pnr = null) =>
            new(name, email, "0701234567", null, pnr, null, null, null, "Hej", "Grundkurs");

        private static int Add(IDatabase db, SiteSignupData d, int owner = Owner, int pageId = 11, string blockId = "abcd1", DateTime? at = null)
        {
            Assert.Equal(SiteSignupInsert.Ok, SiteSignupStore.Insert(db, Club, owner, pageId, blockId, "Grundkurs", d, at ?? Now, out var id));
            return id;
        }

        [SqlFact]
        public void En_anmälan_läggs_in_med_status_ny_och_alla_fält()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var id = Add(f.Db, D(pnr: "19811218-9876"));
            var r = SiteSignupStore.Get(f.Db, id, Club, Owner)!;
            Assert.Equal((byte)0, r.Status);
            Assert.Equal(("Anna Ek", "anna@example.se", "0701234567", "19811218-9876", "Grundkurs", "Grundkurs", 11, "abcd1"), (r.Name, r.Email, r.Phone, r.PersonalNumber, r.Choice, r.FormTitle, r.PageId, r.BlockId));
            Assert.Null(r.HandledDate);
        }

        [SqlFact]
        public void Samma_e_post_i_samma_formulär_ger_ingen_ny_rad_så_länge_den_väntar_men_väl_efter_ett_avslag_eller_i_ett_annat_formulär()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var first = Add(f.Db, D());
            Assert.Equal(SiteSignupInsert.Duplicate, SiteSignupStore.Insert(f.Db, Club, Owner, 11, "abcd1", "Grundkurs", D("Annan Person", "ANNA@example.se"), Now, out _));   // versaler räknas som samma adress
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerId = @0", Owner));
            Add(f.Db, D(), pageId: 12);                                                                       // annat formulär: ny rad
            SiteSignupStore.SetStatus(f.Db, first, Club, Owner, SiteSignupStatus.Denied, 7, Now);
            Add(f.Db, D());                                                                                   // efter ett avslag får samma adress anmäla sig igen
            Assert.Equal(3, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerId = @0", Owner));
        }

        [SqlFact]
        public void Taket_per_formulär_stoppar_nästa_anmälan_men_inte_ett_annat_formulär()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            f.Db.Execute(@"INSERT INTO dbo.SiteFormEntry (OwnerType, OwnerId, PageId, BlockId, FormTitle, Name, Email, CreatedDate)
                           SELECT TOP (@0) N'Club', @1, 11, N'abcd1', N'T', N'N', CONCAT(N'u', ROW_NUMBER() OVER (ORDER BY (SELECT 1)), N'@@example.se'), @2 FROM sys.all_columns a CROSS JOIN sys.all_columns b",
                SiteSignupStore.MaxPerForm, Owner, Now);
            Assert.Equal(SiteSignupInsert.Full, SiteSignupStore.Insert(f.Db, Club, Owner, 11, "abcd1", "T", D(email: "ny@example.se"), Now, out _));
            Assert.Equal(SiteSignupInsert.Ok, SiteSignupStore.Insert(f.Db, Club, Owner, 12, "abcd1", "T", D(email: "ny@example.se"), Now, out _));
        }

        [SqlFact]
        public void En_ägare_utan_rad_i_SiteOwner_tar_inte_emot_något()
        {
            using var f = new SiteSqlFixture();
            Assert.Equal(SiteSignupInsert.NoOwner, SiteSignupStore.Insert(f.Db, Club, Owner, 11, "abcd1", "T", D(), Now, out _));
        }

        [SqlFact]
        public void En_anmälan_kan_aldrig_läsas_ändras_arkiveras_eller_raderas_av_en_annan_ägare()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var mine = Add(f.Db, D(), Owner); var theirs = Add(f.Db, D(email: "b@example.se"), OtherOwner);
            Assert.Null(SiteSignupStore.Get(f.Db, theirs, Club, Owner));
            Assert.False(SiteSignupWriter.Status(f.Db, Club, Owner, theirs, SiteSignupStatus.Approved, Actor, Now));
            Assert.False(SiteSignupWriter.Note(f.Db, Club, Owner, theirs, "kapad", Actor, Now));
            Assert.False(SiteSignupWriter.Archive(f.Db, Club, Owner, theirs, true, Actor, Now));
            Assert.False(SiteSignupWriter.Delete(f.Db, Club, Owner, theirs, Actor, Now));
            var still = SiteSignupStore.Get(f.Db, theirs, Club, OtherOwner)!;
            Assert.Equal((byte)0, still.Status); Assert.Null(still.AdminNote); Assert.Null(still.ArchivedDate);
            Assert.DoesNotContain(SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter()), r => r.Id == theirs);
            Assert.Single(SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter()));
            Assert.NotNull(SiteSignupStore.Get(f.Db, mine, Club, Owner));
        }

        [SqlFact]
        public void Status_avgjord_får_datum_och_vem_och_ny_eller_köad_rensar_avgörandet()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var id = Add(f.Db, D());
            Assert.True(SiteSignupWriter.Status(f.Db, Club, Owner, id, SiteSignupStatus.Approved, Actor, Now.AddDays(1)));
            var a = SiteSignupStore.Get(f.Db, id, Club, Owner)!;
            Assert.Equal(((byte)3, (DateTime?)Now.AddDays(1), (int?)7), (a.Status, a.HandledDate, a.HandledByMemberId));
            Assert.True(SiteSignupWriter.Status(f.Db, Club, Owner, id, SiteSignupStatus.Queued, Actor, Now.AddDays(2)));
            var q = SiteSignupStore.Get(f.Db, id, Club, Owner)!;
            Assert.Equal(((byte)1, (DateTime?)null, (int?)null), (q.Status, q.HandledDate, q.HandledByMemberId));   // en köad anmälan gallras inte som avgjord
            Assert.False(SiteSignupWriter.Status(f.Db, Club, Owner, 999999, SiteSignupStatus.Denied, Actor, Now));
            Assert.Equal(new[] { "entry-status-3", "entry-status-1" }, f.Db.Fetch<string>("SELECT Action FROM dbo.SiteAudit WHERE ObjectKind = 'form' AND ObjectId = @0 ORDER BY Id", id).ToArray());
        }

        [SqlFact]
        public void Listan_filtreras_på_status_formulär_sökning_och_arkiv_och_räknas_per_status()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var a = Add(f.Db, D("Anna Ek", "anna@example.se"), at: Now);
            var b = Add(f.Db, D("Bertil 100%", "bertil@example.se"), at: Now.AddMinutes(1));
            var c = Add(f.Db, D("Cecilia", "cecilia@example.se"), pageId: 12, blockId: "efgh2", at: Now.AddMinutes(2));
            SiteSignupStore.SetStatus(f.Db, b, Club, Owner, SiteSignupStatus.Queued, 7, Now);
            SiteSignupStore.SetArchived(f.Db, c, Club, Owner, true, Now);
            Assert.Equal(new[] { b, a }, SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter()).Select(r => r.Id).ToArray());                 // nyast först, arkiv utan
            Assert.Equal(new[] { b }, SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(Status: 1)).Select(r => r.Id).ToArray());
            Assert.Equal(new[] { c }, SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(Archived: true)).Select(r => r.Id).ToArray());
            Assert.Equal(new[] { a }, SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(Search: "ANNA")).Select(r => r.Id).ToArray());
            Assert.Equal(new[] { b }, SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(Search: "100%")).Select(r => r.Id).ToArray());          // % är en bokstav, inte ett jokertecken
            Assert.Empty(SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(Search: "_")));      // _ är en bokstav, inte "vilket tecken som helst" (annars hade alla rader träffats)
            Assert.Empty(SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(Search: "[a-z]")));  // och en hakparentes är ingen teckenklass
            Assert.Equal(new[] { c }, SiteSignupStore.List(f.Db, Club, Owner, new SiteSignupStore.Filter(PageId: 12, BlockId: "efgh2", Archived: true)).Select(r => r.Id).ToArray());
            var (by, archived) = SiteSignupStore.Counts(f.Db, Club, Owner);
            Assert.Equal(new[] { 1, 1, 0, 0 }, by); Assert.Equal(1, archived);
            Assert.Equal(1, SiteSignupStore.NewCount(f.Db, Club, Owner));
            Assert.Equal(2, SiteSignupStore.Forms(f.Db, Club, Owner).Count);
        }

        [SqlFact]
        public void Anteckning_arkiv_och_radering_fungerar_och_anteckningen_kapas_vid_500_tecken()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var id = Add(f.Db, D());
            Assert.True(SiteSignupWriter.Note(f.Db, Club, Owner, id, "  Ring på måndag  ", Actor, Now));
            Assert.Equal("Ring på måndag", SiteSignupStore.Get(f.Db, id, Club, Owner)!.AdminNote);
            Assert.True(SiteSignupWriter.Note(f.Db, Club, Owner, id, "   ", Actor, Now));
            Assert.Null(SiteSignupStore.Get(f.Db, id, Club, Owner)!.AdminNote);
            Assert.True(SiteSignupWriter.Archive(f.Db, Club, Owner, id, true, Actor, Now));
            Assert.NotNull(SiteSignupStore.Get(f.Db, id, Club, Owner)!.ArchivedDate);
            Assert.True(SiteSignupWriter.Archive(f.Db, Club, Owner, id, false, Actor, Now));
            Assert.Null(SiteSignupStore.Get(f.Db, id, Club, Owner)!.ArchivedDate);
            Assert.True(SiteSignupWriter.Delete(f.Db, Club, Owner, id, Actor, Now));
            Assert.Null(SiteSignupStore.Get(f.Db, id, Club, Owner));
            Assert.False(SiteSignupWriter.Delete(f.Db, Club, Owner, id, Actor, Now));
        }

        [Fact]
        public void CSV_skyddar_mot_formelinjektion_citerar_och_tar_med_alla_rader()
        {
            var rows = new[]
            {
                new SiteSignupRow { Id = 1, FormTitle = "Kurs", Name = "=HYPERLINK(\"http://elak\")", Email = "a@example.se", Message = "rad ett\nrad två; \"citat\"", CreatedDate = Now, Status = 0 },
                new SiteSignupRow { Id = 2, FormTitle = "Kurs", Name = "+1+1", Email = "-x@example.se", Phone = "@cmd", AdminNote = "\tflik", CreatedDate = Now, Status = 3, HandledDate = Now.AddDays(1), PersonalNumber = "19811218-9876" },
            };
            var csv = SiteSignupStore.ToCsv(rows);
            var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(3, lines.Length);
            Assert.Contains("\"'=HYPERLINK(\"\"http://elak\"\")\"", lines[1]);          // = i början får ett ' och citattecken dubblas
            Assert.Contains("\"rad ett rad två; \"\"citat\"\"\"", lines[1]);            // radbrytning blir mellanslag, semikolon ryms i citat
            Assert.Contains("\"'+1+1\"", lines[2]); Assert.Contains("\"'-x@example.se\"", lines[2]); Assert.Contains("\"'@cmd\"", lines[2]); Assert.Contains("\"'\tflik\"", lines[2]);
            Assert.Contains("\"Godkänd\"", lines[2]); Assert.Contains("\"19811218-9876\"", lines[2]);
            Assert.StartsWith("\"Inkom\";\"Formulär\"", lines[0]);
        }

        [SqlFact]
        public void Gallringen_nollar_personnumret_30_dagar_efter_avgörandet_och_raderar_enligt_reglerna()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            var now = new DateTime(2027, 1, 1, 12, 0, 0);
            int Mk(string email, SiteSignupStatus st, DateTime created, DateTime? handled, DateTime? archived = null)
            {
                var id = Add(f.Db, D("N " + email, email, "19811218-9876"), at: created);
                f.Db.Execute("UPDATE dbo.SiteFormEntry SET Status = @1, HandledDate = @2, ArchivedDate = @3 WHERE Id = @0", id, (int)st, handled, archived);
                return id;
            }
            var decidedRecent = Mk("a@x.se", SiteSignupStatus.Approved, now.AddDays(-20), now.AddDays(-10));          // avgjord för 10 dagar sedan: personnumret kvar
            var decided40 = Mk("b@x.se", SiteSignupStatus.Denied, now.AddDays(-60), now.AddDays(-40));                // avgjord för 40 dagar sedan: personnumret borta, raden kvar
            var decided13m = Mk("c@x.se", SiteSignupStatus.Approved, now.AddMonths(-14), now.AddMonths(-13));         // avgjord för 13 månader sedan: raden borta
            var waiting = Mk("d@x.se", SiteSignupStatus.Queued, now.AddMonths(-18), null);                            // väntar sedan 18 månader: kvar, med personnummer (ingen har avgjort)
            var waiting25m = Mk("e@x.se", SiteSignupStatus.New, now.AddMonths(-25), null);                            // aldrig avgjord, 25 månader: raden borta
            var archivedOld = Mk("f@x.se", SiteSignupStatus.New, now.AddDays(-200), null, now.AddDays(-100));         // arkiverad för 100 dagar sedan: borta
            var archivedNew = Mk("g@x.se", SiteSignupStatus.New, now.AddDays(-200), null, now.AddDays(-10));          // arkiverad för 10 dagar sedan: kvar

            var (pnr, deleted) = SiteRetention.PurgeFormEntries(f.Db, now);
            string? Pnr(int id) => SiteSignupStore.Get(f.Db, id, Club, Owner)?.PersonalNumber;
            Assert.Equal("19811218-9876", Pnr(decidedRecent));
            Assert.NotNull(SiteSignupStore.Get(f.Db, decided40, Club, Owner)); Assert.Null(Pnr(decided40));
            Assert.Null(SiteSignupStore.Get(f.Db, decided13m, Club, Owner));
            Assert.Equal("19811218-9876", Pnr(waiting));
            Assert.Null(SiteSignupStore.Get(f.Db, waiting25m, Club, Owner));
            Assert.Null(SiteSignupStore.Get(f.Db, archivedOld, Club, Owner));
            Assert.NotNull(SiteSignupStore.Get(f.Db, archivedNew, Club, Owner));
            Assert.Equal(2, pnr);       // personnumret nollas även på raden som sedan raderas samma körning (13 månader efter avgörandet)
            Assert.Equal(3, deleted);
        }

        [SqlFact]
        public void Raderas_ägaren_försvinner_dess_anmälningar_men_inte_en_annans()
        {
            using var f = new SiteSqlFixture(); Seed(f.Db);
            Add(f.Db, D(), Owner); Add(f.Db, D(), OtherOwner);
            SiteOwnerData.DeleteAll(f.Db, Club, Owner);
            Assert.Equal(0, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerId = @0", Owner));
            Assert.Equal(1, f.Db.ExecuteScalar<int>("SELECT COUNT(*) FROM dbo.SiteFormEntry WHERE OwnerId = @0", OtherOwner));
        }
    }
}
