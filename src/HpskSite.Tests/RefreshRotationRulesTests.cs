using HpskSite.Services.Security;
using Xunit;
using static HpskSite.Services.Security.RefreshRotationRules;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (H6): rotation, nådefönster och återanvändningsdetektion.</summary>
    public class RefreshRotationRulesTests
    {
        private static readonly DateTime Now = new(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        [Fact]
        public void Oanvand_och_giltig_roteras() =>
            Assert.Equal(Outcome.Rotate, Decide(Now, Now.AddDays(30), null, null, false));

        [Fact]
        public void Oanvand_men_utgangen_ar_utgangen() =>
            Assert.Equal(Outcome.Expired, Decide(Now, Now.AddSeconds(-1), null, null, false));

        [Fact]
        public void Nyss_roterad_inom_nadefonstret_far_en_ny_efterfoljare()
        {
            // Mobilen skickade, svaret nådde aldrig fram, appen försöker om efter 8 s.
            Assert.Equal(Outcome.GraceRetry, Decide(Now, Now.AddDays(30), Now.AddSeconds(-8), Now.AddSeconds(-8), true));
        }

        [Fact]
        public void Roterad_for_langesedan_ar_aterandvandning_och_aterkallar_familjen()
        {
            Assert.Equal(Outcome.ReuseDetected, Decide(Now, Now.AddDays(30), Now.AddMinutes(-5), Now.AddMinutes(-5), true));
        }

        [Fact]
        public void Aterkallad_utan_efterfoljare_ar_aldrig_nadefonster()
        {
            // Utloggad (återkallad av Logout/familjeåterkallelse): ingen efterföljare finns att falla tillbaka på.
            Assert.Equal(Outcome.ReuseDetected, Decide(Now, Now.AddDays(30), Now.AddSeconds(-2), Now.AddSeconds(-2), false));
        }

        [Fact]
        public void Aterkallad_utan_LastUsedAt_ar_aterandvandning()
        {
            Assert.Equal(Outcome.ReuseDetected, Decide(Now, Now.AddDays(30), Now.AddSeconds(-2), null, true));
        }

        [Fact]
        public void Nadefonstrets_gran_ar_30_sekunder()
        {
            Assert.Equal(Outcome.GraceRetry, Decide(Now, Now.AddDays(1), Now.AddSeconds(-30), Now.AddSeconds(-30), true));
            Assert.Equal(Outcome.ReuseDetected, Decide(Now, Now.AddDays(1), Now.AddSeconds(-31), Now.AddSeconds(-31), true));
        }
    }
}
