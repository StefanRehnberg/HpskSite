using HpskSite.Services.Hosting;
using HpskSite.Services.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02: de små rena hjälparna i hotfix 1a.</summary>
    public class SecurityPlanHelpersTests
    {
        [Theory]
        [InlineData("anna.svensson@gmail.com", "a***@gmail.com")]
        [InlineData("x@y.se", "x***@y.se")]
        [InlineData("", "")]
        [InlineData(null, "")]
        [InlineData("ingen-snabela", "***")]
        public void MaskedEmail_doljer_det_mesta(string? input, string expected) =>
            Assert.Equal(expected, MaskedEmail.Mask(input));

        [Theory]
        [InlineData("https://pistol.nu", "https://pistol.nu")]
        [InlineData("https://pistol.nu/", "https://pistol.nu")]
        [InlineData("http://localhost:18233", "http://localhost:18233")]
        [InlineData("https://your-domain.com", null)]   // mallens platshållare
        [InlineData("", null)]
        [InlineData(null, null)]
        [InlineData("inte-en-adress", null)]
        [InlineData("ftp://pistol.nu", null)]
        public void PublicUrls_UsableSiteUrl(string? input, string? expected) =>
            Assert.Equal(expected, PublicUrls.UsableSiteUrl(input));

        // ── H7: Host-huvudet får aldrig bestämma länkarna i Production ──
        private sealed class Env : Microsoft.Extensions.Hosting.IHostEnvironment
        {
            public string EnvironmentName { get; set; } = "Production";
            public string ApplicationName { get; set; } = "HpskSite";
            public string ContentRootPath { get; set; } = "";
            public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
        }

        private sealed class Opts : Microsoft.Extensions.Options.IOptionsMonitor<ScaleOutOptions>
        {
            public ScaleOutOptions CurrentValue { get; init; } = new();
            public ScaleOutOptions Get(string? name) => CurrentValue;
            public IDisposable? OnChange(Action<ScaleOutOptions, string?> listener) => null;
        }

        private static PublicUrls Urls(string environment, string? siteUrl, string? publicBase = null)
        {
            var cfg = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["EmailSettings:SiteUrl"] = siteUrl })
                .Build();
            return new PublicUrls(new Opts { CurrentValue = new ScaleOutOptions { PublicBaseUrl = publicBase } }, cfg, new Env { EnvironmentName = environment });
        }

        private static Microsoft.AspNetCore.Http.HttpRequest RequestWithHost(string host)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Request.Scheme = "https";
            ctx.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
            return ctx.Request;
        }

        [Fact]
        public void PublicUrls_i_Production_anvander_SiteUrl_aven_med_falsk_Host()
        {
            var urls = Urls("Production", "https://pistol.nu");
            Assert.Equal("https://pistol.nu", urls.Base(RequestWithHost("evil.example")));
            Assert.Equal("https://pistol.nu/x", urls.Absolute(RequestWithHost("evil.example"), "/x"));
        }

        [Fact]
        public void PublicUrls_PublicBaseUrl_gar_fore_SiteUrl()
        {
            var urls = Urls("Production", "https://pistol.nu", "https://www.pistol.nu/");
            Assert.Equal("https://www.pistol.nu", urls.Base(RequestWithHost("evil.example")));
        }

        [Fact]
        public void PublicUrls_i_Development_anvander_forfragan_som_forut()
        {
            var urls = Urls("Development", "http://localhost:18233");
            Assert.Equal("https://localhost:44380", urls.Base(RequestWithHost("localhost:44380")));
        }

        [Fact]
        public void PublicUrls_med_platshallare_som_SiteUrl_faller_tillbaka_pa_forfragan()
        {
            // Ingen användbar adress konfigurerad: som förut (men endast då — prodfilen har SiteUrl).
            var urls = Urls("Production", "https://your-domain.com");
            Assert.Equal("https://pistol.nu", urls.Base(RequestWithHost("pistol.nu")));
        }
    }
}
