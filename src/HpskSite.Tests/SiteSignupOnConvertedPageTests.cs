using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Blocket Anmälan/ansökan går att lägga på en egen sida OCH på en omgjord systemsida (till exempel Om klubben), men inte på en systemsida i den
    /// gamla formen (platser före/efter). På en omgjord sida ritas ett riktigt formulär (med sidans id), inte förhandsvisningens avstängda.
    /// </summary>
    public class SiteSignupOnConvertedPageTests
    {
        private sealed class LiveForm : ISiteBlockData
        {
            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids) => Array.Empty<SiteDocumentInfo>();
            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count) => Array.Empty<SiteEventInfo>();
            public SiteContactInfo? Contact() => null;
            public SiteFormContext? Form() => new(4711, "tok", null);
        }

        private const string Signup = "{\"id\":\"anm01\",\"type\":\"signup\",\"visibility\":0,\"data\":{\"title\":\"Intresseanmälan\"}}";

        private static SiteContentResult Parse(string owner, string? page, SiteContentKind kind) =>
            SiteBlockSchema.Parse("{\"schema\":" + (kind == SiteContentKind.ConvertedPage ? 2 : 1) + ",\"blocks\":[" + Signup + "]}", kind, SiteVisibility.Alla, SiteTemplates.For(owner), page);

        public static IEnumerable<object[]> Owners() => new[] { SiteTemplates.Club, SiteTemplates.Region }.Select(o => new object[] { o });

        [Theory, MemberData(nameof(Owners))]
        public void Anmälan_går_att_lägga_på_en_egen_sida_och_på_en_omgjord_systemsida(string owner)
        {
            Assert.True(Parse(owner, null, SiteContentKind.FreePage).Ok);
            Assert.True(Parse(owner, "about", SiteContentKind.ConvertedPage).Ok);
        }

        [Fact]
        public void Beskedet_efter_en_inskickning_bärs_av_en_kaka_och_tål_vad_som_helst_i_den()
        {
            var err = SiteFormFlashCookie.Unpack(SiteFormFlashCookie.Pack("anm01", false, "invalid", new[] { "email", "personalNumber" }));
            Assert.NotNull(err);
            Assert.False(err!.Success);
            Assert.Equal("invalid", err.ErrorCode);
            Assert.Equal(new[] { "email", "personalNumber" }, err.Fields);
            Assert.True(SiteFormFlashCookie.Unpack(SiteFormFlashCookie.Pack("anm01", true, null, null))!.Success);

            // kakan är besökarens egen: okända fält och koder blir aldrig text, trasiga värden ger inget besked
            var odd = SiteFormFlashCookie.Unpack("anm01|err|<b>|email,<script>,x")!;
            Assert.Equal("error", odd.ErrorCode);
            Assert.Equal(new[] { "email" }, odd.Fields);
            Assert.Null(SiteFormFlashCookie.Unpack("a<b|err|invalid|"));
            Assert.Null(SiteFormFlashCookie.Unpack("anm01|ok"));
            Assert.Null(SiteFormFlashCookie.Unpack(new string('x', 300)));
            Assert.Null(SiteFormFlashCookie.Unpack(null));
        }

        [Theory, MemberData(nameof(Owners))]
        public void Anmälan_vägras_på_en_systemsida_i_den_gamla_formen(string owner)
        {
            var r = Parse(owner, "about", SiteContentKind.TemplateCopy);
            Assert.False(r.Ok);
        }

        [Fact]
        public void Varje_fält_som_valideringen_kan_underkänna_har_en_text_så_att_besökaren_ser_vad_som_är_fel()
        {
            var cfg = SiteSignupConfig.FromBlock(Parse(SiteTemplates.Club, null, SiteContentKind.FreePage).Blocks[0]);
            var all = new SiteSignupConfig(cfg.Title, null, new[] { "A" }, true, true, true, true, true, null, null, null);
            var r = SiteSignupRules.Validate(all, new SiteSignupInput("<", "x", "!", "Fel", "123", "", "9", "", new string('x', 1001), "B", false), DateTime.Today);
            Assert.False(r.Ok);
            Assert.All(r.Errors.Select(e => e.Field).Where(f => f.Length > 0), f => Assert.NotNull(SiteSignupRules.FieldText(f)));
            Assert.Null(SiteSignupRules.FieldText("<script>"));   // en okänd nyckel ur adressen blir aldrig text
        }

        [Fact]
        public void Formuläret_lovar_ingen_radering_och_e_postfältet_är_märkt_mot_lösenordshanterare()
        {
            var json = "{\"schema\":1,\"blocks\":[{\"id\":\"anm01\",\"type\":\"signup\",\"visibility\":0,\"data\":{\"title\":\"Bli medlem\",\"pnr\":true}}]}";
            var content = SiteBlockSchema.Parse(json, SiteContentKind.FreePage, SiteVisibility.Alla, SiteTemplates.For(SiteTemplates.Club), null);
            Assert.True(content.Ok);
            var html = SiteBlockRenderer.Render(content.Blocks[0], SiteVisibility.Alla, new SiteRenderContext(SiteViewer.Anonymous, "/x/", SiteTemplates.For(SiteTemplates.Club), new LiveForm()));
            Assert.DoesNotContain("raderas", html);                // en anmälan om medlemskap sparas i föreningens register: inget löfte om radering
            Assert.DoesNotContain("tas bort automatiskt", html);
            Assert.Contains("medlemsregister", html);
            Assert.Matches("type=\"email\"[^>]*data-1p-ignore", html);   // annars erbjuder 1Password en inloggning i ett anmälningsformulär
        }

        [Theory, MemberData(nameof(Owners))]
        public void En_omgjord_sida_ritar_ett_riktigt_formulär_med_sidans_id(string owner)
        {
            var content = Parse(owner, "about", SiteContentKind.ConvertedPage);
            var page = new ConvertedPage("about", ConvertedState.Ok, SiteVisibility.Alla, false, content, 4711);
            var plan = SiteConvertedPages.Plan(page, new SiteRenderContext(SiteViewer.Anonymous, "/x/", SiteTemplates.For(owner), new LiveForm()));
            var html = string.Concat(plan.Items.Select(i => i.Html));
            Assert.Contains("action=\"/umbraco/surface/SiteForm/Submit\"", html);
            Assert.Contains("name=\"PageId\" value=\"4711\"", html);
            Assert.DoesNotContain("<fieldset disabled>", html);
        }
    }
}
