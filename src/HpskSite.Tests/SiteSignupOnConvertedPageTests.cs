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

        [Theory, MemberData(nameof(Owners))]
        public void Anmälan_vägras_på_en_systemsida_i_den_gamla_formen(string owner)
        {
            var r = Parse(owner, "about", SiteContentKind.TemplateCopy);
            Assert.False(r.Ok);
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
