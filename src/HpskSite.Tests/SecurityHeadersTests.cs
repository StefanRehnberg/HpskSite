using HpskSite.Services.Security;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02, paket 2 (steg 1): svarshuvudena.</summary>
    public class SecurityHeadersTests
    {
        private static DefaultHttpContext Ctx(bool https)
        {
            var c = new DefaultHttpContext();
            c.Request.Scheme = https ? "https" : "http";
            return c;
        }

        [Fact]
        public void Grundhuvudena_sätts()
        {
            var c = Ctx(https: true);
            SecurityHeadersMiddleware.Apply(c, isDevelopment: false);
            var h = c.Response.Headers;
            Assert.Equal("nosniff", h["X-Content-Type-Options"]);
            Assert.Equal("SAMEORIGIN", h["X-Frame-Options"]);
            Assert.Equal("strict-origin-when-cross-origin", h["Referrer-Policy"]);
            Assert.Contains("camera=(self)", h["Permissions-Policy"].ToString());
            Assert.Contains("microphone=()", h["Permissions-Policy"].ToString());
        }

        [Fact]
        public void Hsts_bara_pa_https_utanfor_utveckling_och_utan_underdomaner()
        {
            var prod = Ctx(https: true);
            SecurityHeadersMiddleware.Apply(prod, isDevelopment: false);
            Assert.Equal("max-age=86400", prod.Response.Headers["Strict-Transport-Security"]);

            var http = Ctx(https: false);
            SecurityHeadersMiddleware.Apply(http, isDevelopment: false);
            Assert.False(http.Response.Headers.ContainsKey("Strict-Transport-Security"));

            var dev = Ctx(https: true);
            SecurityHeadersMiddleware.Apply(dev, isDevelopment: true);
            Assert.False(dev.Response.Headers.ContainsKey("Strict-Transport-Security"));
        }

        [Fact]
        public void Ett_redan_satt_huvud_skrivs_inte_over()
        {
            var c = Ctx(https: true);
            c.Response.Headers["X-Frame-Options"] = "DENY";
            SecurityHeadersMiddleware.Apply(c, isDevelopment: false);
            Assert.Equal("DENY", c.Response.Headers["X-Frame-Options"]);
        }
    }
}
