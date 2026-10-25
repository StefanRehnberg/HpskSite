using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>Stefan 2026-10-25: en konfiguration, ansvar och godkännande per station.</summary>
    public class StationBlobTests
    {
        private static string St(int n, int time) => $"{{\"station\":{n},\"shootingTimeSec\":{time}}}";
        private static string Cfg(params string[] cStations) => "{\"C\":{\"stations\":[" + string.Join(",", cStations) + "]},\"_stationCount\":3}";
        private static int Time(string blob, int n) =>
            ((JArray)JObject.Parse(blob)["C"]!["stations"]!).Single(s => s.Value<int>("station") == n).Value<int>("shootingTimeSec");

        [Fact]
        public void A_save_of_station_3_does_not_touch_station_1_saved_by_someone_else_meanwhile()
        {
            var server = Cfg(St(1, 99), St(3, 30));      // Bertil sparade station 1 = 99 under tiden
            var mine = Cfg(St(1, 10), St(3, 45));        // Varbergs flik: gammal station 1, ny station 3
            var merged = StationBlob.Merge(server, mine, new[] { 3 });
            Time(merged, 1).Should().Be(99);
            Time(merged, 3).Should().Be(45);
            JObject.Parse(merged).Value<int>("_stationCount").Should().Be(3, "meta tas ur servern");
        }

        [Fact]
        public void A_station_missing_in_the_incoming_blob_is_removed()
        {
            var merged = StationBlob.Merge(Cfg(St(1, 10), St(2, 20)), Cfg(St(1, 10)), new[] { 2 });
            ((JArray)JObject.Parse(merged)["C"]!["stations"]!).Select(s => s.Value<int>("station")).Should().Equal(1);
        }

        [Fact]
        public void The_checksum_is_per_station_and_ignores_property_order()
        {
            StationBlob.Checksum(Cfg(St(1, 10), St(2, 20)), 1).Should().Be(StationBlob.Checksum("{\"C\":{\"stations\":[{\"shootingTimeSec\":10,\"station\":1}]}}", 1));
            StationBlob.Checksum(Cfg(St(1, 10)), 1).Should().NotBe(StationBlob.Checksum(Cfg(St(1, 11)), 1));
            StationBlob.Checksum(Cfg(St(1, 10)), 2).Should().Be("");
            StationBlob.Changed(Cfg(St(1, 10), St(2, 20)), Cfg(St(1, 10), St(2, 21))).Should().Equal(2);
        }

        [Fact]
        public void An_approved_station_that_changes_is_a_draft_again_and_blocks_the_whole()
        {
            var blob = Cfg(St(1, 10), St(2, 20), St(3, 30));
            var approvals = new Dictionary<int, StationApproval?>();
            foreach (var n in new[] { 1, 2, 3 }) approvals[n] = new StationApproval(StationBlob.Approved, 5513, "2026-10-25", null, StationBlob.Checksum(blob, n));
            blob = StationBlob.WithApprovals(blob, approvals);
            StationBlob.WholeBlocker(blob, 3).Should().BeNull();
            var changed = StationBlob.Merge(blob, Cfg(St(2, 25)), new[] { 2 });
            StationBlob.Current(changed, 2).Should().BeNull("innehållet ändrades efter godkännandet");
            StationBlob.Current(changed, 1)!.Status.Should().Be(StationBlob.Approved);
            StationBlob.WholeBlocker(changed, 3).Should().Contain("station 2");
        }

        [Fact]
        public void A_returned_station_keeps_its_comment()
        {
            var blob = StationBlob.WithApprovals(Cfg(St(1, 10)), new Dictionary<int, StationApproval?>
                { [1] = new StationApproval(StationBlob.Returned, 5513, "2026-10-25", "Avståndet i C är för långt", null) });
            StationBlob.Current(blob, 1)!.Comment.Should().Be("Avståndet i C är för långt");
            StationBlob.WithApprovals(blob, new Dictionary<int, StationApproval?> { [1] = null }).Should().NotContain(StationBlob.ApprovalKey);
        }
    }
}
