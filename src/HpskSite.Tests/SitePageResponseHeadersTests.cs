using HpskSite.Services.Security;
using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna F1: en sida i den nya sidstrukturen får aldrig lagras av ett delat cacheminne eller en CDN
    /// (innehållet beror på vem som frågar) och indexeras inte. Hem (rotadressen) bär ingen SitePageRequest och rörs inte.
    /// </summary>
    public class SitePageResponseHeadersTests
    {
        private static DefaultHttpContext Ctx(bool sitePage)
        {
            var c = new DefaultHttpContext();
            c.Request.Scheme = "https";
            if (sitePage) c.Items[SitePageRequest.ItemKey] = new SitePageRequest("Club", 9003, "banor");
            return c;
        }

        [Fact]
        public void Sidmotorns_svar_far_private_no_store_och_noindex()
        {
            var c = Ctx(sitePage: true);
            SecurityHeadersMiddleware.Apply(c, isDevelopment: false);
            Assert.Equal("private, no-store", c.Response.Headers["Cache-Control"].ToString());
            Assert.Equal("noindex", c.Response.Headers["X-Robots-Tag"].ToString());
        }

        [Fact]
        public void Sidmotorns_svar_skriver_over_ett_redan_satt_cachehuvud()
        {
            var c = Ctx(sitePage: true);
            c.Response.Headers["Cache-Control"] = "public, max-age=3600";
            SecurityHeadersMiddleware.Apply(c, isDevelopment: false);
            Assert.Equal("private, no-store", c.Response.Headers["Cache-Control"].ToString());
        }

        [Fact]
        public void Andra_svar_rors_inte()
        {
            var c = Ctx(sitePage: false);
            SecurityHeadersMiddleware.Apply(c, isDevelopment: false);
            Assert.False(c.Response.Headers.ContainsKey("Cache-Control"));
            Assert.False(c.Response.Headers.ContainsKey("X-Robots-Tag"));
        }
    }
}
