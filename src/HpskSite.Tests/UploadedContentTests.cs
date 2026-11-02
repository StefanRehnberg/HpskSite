using HpskSite.Services.Security;
using HpskSite.Services.Storage;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (A6/H5): vad en uppladdad fil får serveras som.</summary>
    public class UploadedContentTests
    {
        [Theory]
        [InlineData("x.html")]
        [InlineData("X.HTML")]
        [InlineData("a.htm")]
        [InlineData("bild.svg")]
        [InlineData("a.xml")]
        [InlineData("script.js")]
        [InlineData("mapp/x.xhtml")]
        public void Aktivt_innehall_kanns_igen(string name) => Assert.True(UploadedContent.IsActiveContent(name));

        [Theory]
        [InlineData("foto.jpg")]
        [InlineData("foto.JPEG")]
        [InlineData("dok.pdf")]
        [InlineData("a.html.jpg")]   // ändelsen är .jpg — tolkas som en bild
        [InlineData("utan-andelse")]
        [InlineData("")]
        [InlineData(null)]
        public void Vanliga_filer_ar_inte_aktivt_innehall(string? name) => Assert.False(UploadedContent.IsActiveContent(name));

        [Theory]
        [InlineData("a.html", "application/octet-stream")]
        [InlineData("a.svg", "application/octet-stream")]
        [InlineData("a.pdf", "application/pdf")]
        [InlineData("a.png", "image/png")]
        [InlineData("a.jpg", "image/jpeg")]
        [InlineData("a.txt", "text/plain")]
        [InlineData("okand.qqq", "application/octet-stream")]
        public void Content_type_harleds_ur_andelsen(string name, string expected) =>
            Assert.Equal(expected, UploadedContent.ServedContentType(name));

        [Theory]
        [InlineData("application/pdf", true)]
        [InlineData("image/png", true)]
        [InlineData("text/plain", true)]
        [InlineData("text/html", false)]
        [InlineData("image/svg+xml", false)]
        [InlineData("application/octet-stream", false)]
        public void Bara_sakra_typer_visas_inline(string type, bool inline) =>
            Assert.Equal(inline, UploadedContent.IsInlineSafe(type));

        [Theory]
        [InlineData("/images/uploads/abc.jpg", true)]
        [InlineData("/images/UPLOADS/abc.jpg", true)]
        [InlineData("/images/field-targets/1/a.jpg", true)]
        [InlineData("/images/faltskytte/5/x.png", true)]
        [InlineData("/images/faltkonfig/utkast/a.png", true)]
        [InlineData("/images/logo.svg", false)]           // sajtens egna tillgångar utanför uppladdningsområdena
        [InlineData("/images/about/patrullgenerator.png", false)]
        [InlineData("/media/abc/x.svg", false)]
        [InlineData("/", false)]
        public void Uppladdningsomradena_kanns_igen(string path, bool expected) =>
            Assert.Equal(expected, UploadedContentSafetyMiddleware.IsUploadArea(path));

        [Fact]
        public async Task Mellanvaran_nekar_html_i_uppladdningsomradet_och_lamnar_resten()
        {
            var reached = false;
            var mw = new UploadedContentSafetyMiddleware(_ => { reached = true; return Task.CompletedTask; },
                Microsoft.Extensions.Logging.Abstractions.NullLogger<UploadedContentSafetyMiddleware>.Instance);

            var evil = new DefaultHttpContext();
            evil.Request.Path = "/images/uploads/x.html";
            await mw.InvokeAsync(evil);
            Assert.False(reached);
            Assert.Equal(404, evil.Response.StatusCode);

            var ok = new DefaultHttpContext();
            ok.Request.Path = "/images/uploads/x.jpg";
            await mw.InvokeAsync(ok);
            Assert.True(reached);

            reached = false;
            var asset = new DefaultHttpContext();
            asset.Request.Path = "/images/logo.svg";   // utanför uppladdningsområdena: orört
            await mw.InvokeAsync(asset);
            Assert.True(reached);
            Assert.NotEqual(404, asset.Response.StatusCode);
        }
    }
}
