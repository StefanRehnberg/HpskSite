using System;
using FluentAssertions;
using HpskSite.Services.CompetitionCalls;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>Klubbprogrammet F7: tacka nej-länken i mejlet till en utsedd tävlingsledare — signerad, bara den personens eget utseende, tidsbegränsad.</summary>
    public class CompetitionManagerLinkServiceTests
    {
        private static CompetitionManagerLinkService Make(IDataProtectionProvider? dp = null) =>
            new(dp ?? new EphemeralDataProtectionProvider(), new ConfigurationBuilder().AddInMemoryCollection().Build());

        [Fact]
        public void En_giltig_lank_ger_tillbaka_tavling_och_medlem()
        {
            var s = Make();
            s.Parse(s.Token(5282, 9004)).Should().Be((5282, 9004));
        }

        [Fact]
        public void Tom_eller_manipulerad_lank_ar_ogiltig()
        {
            var s = Make();
            s.Parse(null).Should().BeNull();
            s.Parse("").Should().BeNull();
            s.Parse("abc").Should().BeNull();
            var t = s.Token(5282, 9004);
            s.Parse(t.Substring(0, t.Length - 3) + "AAA").Should().BeNull();
        }

        [Fact]
        public void En_lank_signerad_av_andra_nycklar_eller_annan_purpose_godtas_inte()
        {
            var a = Make(new EphemeralDataProtectionProvider());
            var b = Make(new EphemeralDataProtectionProvider());
            b.Parse(a.Token(1, 2)).Should().BeNull();
            var dp = new EphemeralDataProtectionProvider();
            var other = dp.CreateProtector("Planning.Mail.v1").ToTimeLimitedDataProtector().Protect("decline|1|2", TimeSpan.FromDays(1));
            Make(dp).Parse(other).Should().BeNull();
        }

        [Fact]
        public void En_utgangen_lank_godtas_inte()
        {
            var dp = new EphemeralDataProtectionProvider();
            var expired = dp.CreateProtector("CompetitionManager.Decline.v1").ToTimeLimitedDataProtector().Protect("decline|1|2", TimeSpan.FromMilliseconds(1));
            System.Threading.Thread.Sleep(50);
            Make(dp).Parse(expired).Should().BeNull();
        }

        [Fact]
        public void En_lank_med_annan_betydelse_eller_ogiltiga_id_godtas_inte()
        {
            var dp = new EphemeralDataProtectionProvider();
            var p = dp.CreateProtector("CompetitionManager.Decline.v1").ToTimeLimitedDataProtector();
            var s = Make(dp);
            s.Parse(p.Protect("accept|1|2", TimeSpan.FromDays(1))).Should().BeNull();
            s.Parse(p.Protect("decline|0|2", TimeSpan.FromDays(1))).Should().BeNull();
            s.Parse(p.Protect("decline|1", TimeSpan.FromDays(1))).Should().BeNull();
        }

        [Fact]
        public void Adressen_pekar_pa_tacka_nej_och_ar_url_kodad()
        {
            var s = Make();
            var url = s.DeclineUrl(1, 2, "https://exempel.se/");
            url.Should().StartWith("https://exempel.se/tavlingsledare/tacka-nej?t=");
            var token = Uri.UnescapeDataString(url.Substring(url.IndexOf("t=", StringComparison.Ordinal) + 2));
            s.Parse(token).Should().Be((1, 2));
        }
    }
}
