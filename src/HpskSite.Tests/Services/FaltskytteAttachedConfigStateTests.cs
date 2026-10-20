using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Har den anslutna konfigurationen ändrats sedan den anslöts? (Michael, tävling 5450,
    /// 2026-10-20: konfigurationen gick från 8 till 6 stationer, tävlingen visade fortfarande 8
    /// och ingen yta sa varför.) Kopian förs medvetet aldrig över — bara skillnaden avgörs här.
    /// </summary>
    public class FaltskytteAttachedConfigStateTests
    {
        private static string Stations(int n, string extra = "") =>
            "{" + extra + "\"C\":{\"stations\":[" +
            string.Join(",", Enumerable.Range(1, n).Select(i => "{\"station\":" + i + ",\"shootingTimeSec\":16}")) +
            "]},\"_scoringMode\":\"Normal\"}";

        [Fact]
        public void Kopian_med_attachedId_skiljer_sig_inte_fran_sin_kalla()
        {
            var config = Stations(8);
            var snapshot = Stations(8, "\"_attachedConfigId\":42,");
            FaltskytteAttachedConfigState.Differs(snapshot, config).Should().BeFalse();
            FaltskytteAttachedConfigState.AttachedId(snapshot).Should().Be(42);
        }

        [Fact]
        public void Andrat_stationsantal_ar_en_skillnad()
        {
            var snapshot = Stations(8, "\"_attachedConfigId\":42,");
            var config = Stations(6);
            FaltskytteAttachedConfigState.Differs(snapshot, config).Should().BeTrue();
            FaltskytteAttachedConfigState.StationCount(snapshot).Should().Be(8);
            FaltskytteAttachedConfigState.StationCount(config).Should().Be(6);
        }

        [Fact]
        public void Andrat_innehall_med_samma_antal_ar_en_skillnad()
        {
            var snapshot = Stations(6, "\"_attachedConfigId\":42,");
            var config = Stations(6).Replace("\"shootingTimeSec\":16}]", "\"shootingTimeSec\":18}]");
            FaltskytteAttachedConfigState.Differs(snapshot, config).Should().BeTrue();
        }

        [Fact]
        public void Nyckelordningen_spelar_ingen_roll()
        {
            var snapshot = "{\"_attachedConfigId\":42,\"_scoringMode\":\"Normal\",\"C\":{\"stations\":[{\"station\":1}]}}";
            var config = "{\"C\":{\"stations\":[{\"station\":1}]},\"_scoringMode\":\"Normal\"}";
            FaltskytteAttachedConfigState.Differs(snapshot, config).Should().BeFalse();
        }

        [Fact]
        public void Sarskjutningsstationer_raknas_inte()
        {
            var json = "{\"C\":{\"stations\":[{\"station\":1},{\"station\":2},{\"station\":9,\"isShootOffOnly\":true}]}}";
            FaltskytteAttachedConfigState.StationCount(json).Should().Be(2);
        }

        [Fact]
        public void Utan_ansluten_konfiguration_finns_inget_id()
        {
            FaltskytteAttachedConfigState.AttachedId(Stations(3)).Should().BeNull();
            FaltskytteAttachedConfigState.AttachedId(null).Should().BeNull();
            FaltskytteAttachedConfigState.AttachedId("inte json").Should().BeNull();
        }
    }
}
