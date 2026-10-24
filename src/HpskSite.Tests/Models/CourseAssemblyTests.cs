using System;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HpskSite.Tests.Models
{
    /// <summary>Banan i planeringen: delarna läggs ihop till banan (Stefan 2026-10-24).</summary>
    public class CourseAssemblyTests
    {
        private static readonly DateTime Now = new(2026, 10, 24, 14, 0, 0);

        private static string St(int n, int time, string extra = "") => $"{{\"station\":{n},\"shootingTimeSec\":{time}{extra}}}";
        private static string Cfg(string classes, string meta = "") => "{" + classes + (meta.Length > 0 ? "," + meta : "") + "}";
        private static string Wc(string wc, params string[] stations) => $"\"{wc}\":{{\"stations\":[{string.Join(",", stations)}]}}";

        private static int[] Numbers(string blob, string wc) =>
            ((JArray)JObject.Parse(blob)[wc]!["stations"]!).Select(s => s.Value<int>("station")).ToArray();
        private static int Time(string blob, string wc, int station) =>
            ((JArray)JObject.Parse(blob)[wc]!["stations"]!).Single(s => s.Value<int>("station") == station).Value<int>("shootingTimeSec");

        [Fact]
        public void A_part_with_stations_1_and_5_lands_on_course_stations_1_and_5()
        {
            // Klubb X: delkonfigurationens station 1 och 2 = banans 1 och 5.
            var x = Cfg(Wc("C", St(1, 11), St(2, 15)), "\"_courseStations\":[1,5]");
            var y = Cfg(Wc("C", St(1, 12), St(2, 13), St(3, 14)), "\"_courseStations\":[2,3,4]");
            var r = CourseAssembly.Assemble(null, new[] { new CoursePart(10, "Klubb X", x), new CoursePart(11, "Klubb Y", y) }, 5, Now);
            Numbers(r.Blob, "C").Should().Equal(1, 2, 3, 4, 5);
            Time(r.Blob, "C", 1).Should().Be(11);
            Time(r.Blob, "C", 5).Should().Be(15);
            Time(r.Blob, "C", 3).Should().Be(13);
            r.Missing.Should().BeEmpty();
            r.Warnings.Should().BeEmpty();
        }

        [Fact]
        public void Stations_that_no_part_has_are_kept_from_the_course_itself()
        {
            var course = Cfg(Wc("C", St(1, 99), St(2, 20), St(3, 30)));
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[1]");
            var r = CourseAssembly.Assemble(course, new[] { new CoursePart(10, "X", part) }, 3, Now);
            Time(r.Blob, "C", 1).Should().Be(10, "delen gäller för sina stationer");
            Time(r.Blob, "C", 2).Should().Be(20);
            r.FromCourse.Should().Equal(2, 3);
        }

        [Fact]
        public void A_station_nobody_built_is_named_as_missing()
        {
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[2]");
            var r = CourseAssembly.Assemble(null, new[] { new CoursePart(10, "X", part) }, 4, Now);
            r.Missing.Should().Equal(1, 3, 4);
            JObject.Parse(r.Blob).Value<int>(CourseAssembly.StationCountKey).Should().Be(4);
        }

        [Fact]
        public void A_part_without_a_weapon_class_the_course_has_is_warned_about()
        {
            var course = Cfg(Wc("A", St(1, 9)) + "," + Wc("C", St(1, 9)));
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[1]");
            var r = CourseAssembly.Assemble(course, new[] { new CoursePart(10, "Klubb X", part) }, 1, Now);
            r.Warnings.Should().ContainSingle(w => w.Contains("Klubb X") && w.Contains("vapengrupp A"));
        }

        [Fact]
        public void The_course_keeps_its_own_scoring_mode_and_a_different_part_is_warned_about()
        {
            var course = Cfg(Wc("C", St(1, 9)), "\"_scoringMode\":\"Poang\"");
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[1],\"_scoringMode\":\"Normal\"");
            var r = CourseAssembly.Assemble(course, new[] { new CoursePart(10, "X", part) }, 1, Now);
            JObject.Parse(r.Blob).Value<string>("_scoringMode").Should().Be("Poang");
            r.Warnings.Should().Contain(w => w.Contains("tävlingstyp"));
        }

        [Fact]
        public void An_empty_course_takes_the_scoring_mode_of_its_parts()
        {
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[1],\"_scoringMode\":\"Poang\"");
            var r = CourseAssembly.Assemble(null, new[] { new CoursePart(10, "X", part) }, 1, Now);
            JObject.Parse(r.Blob).Value<string>("_scoringMode").Should().Be("Poang");
            r.Warnings.Should().BeEmpty();
        }

        [Fact]
        public void The_course_records_its_parts_with_a_content_checksum()
        {
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[3]");
            var r = CourseAssembly.Assemble(null, new[] { new CoursePart(10, "X", part) }, 3, Now);
            var from = CourseAssembly.AssembledFrom(r.Blob);
            from.Should().ContainSingle(p => p.ConfigId == 10 && p.Stations.SequenceEqual(new[] { 3 }) && p.Checksum == CourseAssembly.Checksum(part));
            CourseAssembly.AssembledAt(r.Blob).Should().Be("2026-10-24 14:00");
        }

        [Fact]
        public void The_checksum_ignores_property_order_and_where_the_config_is_attached()
        {
            CourseAssembly.Checksum("{\"a\":1,\"b\":{\"x\":1,\"y\":2}}")
                .Should().Be(CourseAssembly.Checksum("{\"b\":{\"y\":2,\"x\":1},\"a\":1,\"_attachedConfigId\":7}"));
            CourseAssembly.Checksum("{\"a\":1}").Should().NotBe(CourseAssembly.Checksum("{\"a\":2}"));
        }

        [Fact]
        public void The_whole_course_can_be_approved_only_when_every_part_is_approved_and_unchanged()
        {
            var part = Cfg(Wc("C", St(1, 10)), "\"_courseStations\":[1]");
            var course = CourseAssembly.Assemble(null, new[] { new CoursePart(10, "Klubb X", part) }, 1, Now).Blob;
            CourseAssembly.ApprovalBlocker(course, _ => ("Klubb X", part, false)).Should().Contain("Inte godkända än: Klubb X");
            CourseAssembly.ApprovalBlocker(course, _ => ("Klubb X", part, true)).Should().BeNull();
            var changed = Cfg(Wc("C", St(1, 12)), "\"_courseStations\":[1]");
            CourseAssembly.ApprovalBlocker(course, _ => ("Klubb X", changed, true)).Should().Contain("Sätt ihop banan igen");
            CourseAssembly.ApprovalBlocker(course, _ => null).Should().Contain("Sätt ihop banan igen");
        }

        [Fact]
        public void A_course_that_was_never_assembled_has_no_approval_blocker()
        {
            CourseAssembly.ApprovalBlocker(Cfg(Wc("C", St(1, 9))), _ => null).Should().BeNull();
        }

        [Fact]
        public void Link_markers_from_a_part_do_not_follow_into_the_course()
        {
            var part = Cfg(Wc("C", St(1, 10, ",\"_linkedFromConfigId\":5")), "\"_courseStations\":[1]");
            var r = CourseAssembly.Assemble(null, new[] { new CoursePart(10, "X", part) }, 1, Now);
            r.Blob.Should().NotContain("_linkedFrom");
        }
    }
}
