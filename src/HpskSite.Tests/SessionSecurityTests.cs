using HpskSite;
using HpskSite.Services.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02, paket 2 (P5): kakorna och säkerhetsstämpeln.</summary>
    public class SessionSecurityTests
    {
        [Fact]
        public void Medlemmens_inloggningskaka_ar_Secure_Always_och_namnet_ar_oforandrat()
        {
            var services = new ServiceCollection();
            services.AddOptions();
            // Samma grundläggning som Identity gör för schemat, med kakans förval — hårdningen ska lägga SecurePolicy ovanpå.
            services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, o => o.Cookie.Name = ".AspNetCore.Identity.Application");
            services.AddSessionCookieHardening();
            using var sp = services.BuildServiceProvider();

            var cookie = sp.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme).Cookie;
            Assert.Equal(CookieSecurePolicy.Always, cookie.SecurePolicy);
            Assert.Equal(".AspNetCore.Identity.Application", cookie.Name); // byte av namn loggar ut alla
        }

        [Fact]
        public void Antiforgerykakan_ar_Secure_Always()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSessionCookieHardening();
            using var sp = services.BuildServiceProvider();
            Assert.Equal(CookieSecurePolicy.Always, sp.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.SecurePolicy);
        }

        [Theory]
        [InlineData(null, 43200)]
        [InlineData("", 43200)]
        [InlineData("abc", 43200)]
        [InlineData("0", 43200)]       // ogiltigt: aldrig "validera hela tiden"
        [InlineData("-5", 43200)]
        [InlineData("999999999", 43200)]
        [InlineData("15", 15)]
        [InlineData("1", 1)]
        public void Valideringsintervallet_har_30_dagar_som_forval_och_godtar_bara_rimliga_varden(string? configured, int expectedMinutes) =>
            Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), MemberSecurityStampValidatorOptions.IntervalFrom(configured));

        [Theory]
        [InlineData(false, true, false, false, true)]   // stängdes av
        [InlineData(true, true, false, false, false)]   // godkändes (fick tillbaka rätten)
        [InlineData(false, false, false, false, false)] // var redan avstängd, rörs inte
        [InlineData(true, false, true, true, true)]     // låstes
        [InlineData(true, false, false, true, false)]   // låstes upp
        [InlineData(true, false, true, false, false)]   // var redan låst
        public void Sessionerna_ogiltigforklaras_bara_vid_avstangning_eller_lasning(
            bool approved, bool approvedDirty, bool locked, bool lockedDirty, bool expected) =>
            Assert.Equal(expected, SessionRevocationHandler.ShouldRevoke(approved, approvedDirty, locked, lockedDirty));
    }
}
