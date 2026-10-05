using HpskSite.Services.Firearms;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// När lånevapen tidigast får bokas (Stefan 2026-10-05): klubbens antal dagar FÖRE tillfället,
    /// från midnatt. <b>0 = samma dag och är förvalet</b> — utan gräns bokade medlemmarna årets alla
    /// tillfällen långt innan de visste om de kunde komma.
    ///
    /// <para>⚠️ 0 betydde tidigare "ingen gräns". Faller testet för förvalet har den gamla
    /// tolkningen kommit tillbaka.</para>
    /// </summary>
    public class LoanWeaponBookingOpensTests
    {
        private static readonly DateTime Occasion = new(2026, 10, 14, 18, 0, 0);

        [Fact]
        public void Forvalet_ar_samma_dag_fran_midnatt()
        {
            var r = new LoanWeaponClubSettings();   // en klubb som aldrig rört inställningen
            Assert.Equal(0, r.HorizonDays);
            Assert.False(r.WithinHorizon(Occasion, new DateTime(2026, 10, 13, 23, 59, 0)));
            Assert.True(r.WithinHorizon(Occasion, new DateTime(2026, 10, 14, 0, 0, 0)));
            Assert.True(r.WithinHorizon(Occasion, new DateTime(2026, 10, 14, 17, 30, 0)));
        }

        [Fact]
        public void Forvalet_stoppar_ett_tillfalle_langt_fram()
        {
            var r = new LoanWeaponClubSettings();
            Assert.False(r.WithinHorizon(new DateTime(2027, 3, 1), new DateTime(2026, 10, 5)));
        }

        [Fact]
        public void Tva_dagar_fore_oppnar_vid_midnatt_tva_dagar_innan()
        {
            var r = new LoanWeaponClubSettings { HorizonDays = 2 };
            Assert.False(r.WithinHorizon(Occasion, new DateTime(2026, 10, 11, 23, 59, 0)));
            Assert.True(r.WithinHorizon(Occasion, new DateTime(2026, 10, 12, 0, 0, 0)));
        }

        [Fact]
        public void OpensOn_ar_tillfallets_dag_minus_dagarna()
        {
            Assert.Equal(new DateTime(2026, 10, 14), new LoanWeaponClubSettings().OpensOn(Occasion));
            Assert.Equal(new DateTime(2026, 10, 7), new LoanWeaponClubSettings { HorizonDays = 7 }.OpensOn(Occasion));
        }

        [Fact]
        public void Ett_passerat_tillfalle_ar_inte_stangt_av_regeln()
        {
            // Regeln säger "tidigast", aldrig "senast" — att det passerat avgör fönsterregeln.
            Assert.True(new LoanWeaponClubSettings().WithinHorizon(Occasion, new DateTime(2026, 10, 20)));
        }

        [Theory]
        [InlineData(0, "från midnatt samma dag som tillfället")]
        [InlineData(1, "från midnatt dagen före tillfället")]
        [InlineData(3, "från midnatt 3 dagar före tillfället")]
        public void Regeln_i_klartext(int days, string text)
        {
            Assert.Equal(text, new LoanWeaponClubSettings { HorizonDays = days }.RuleText);
        }
    }
}
