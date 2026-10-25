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
            CourseAssembly.ApprovalBlocker(course, _ => ("Klubb X", changed, true)).Should().Contain("Sätt ihop stationerna igen");
            CourseAssembly.ApprovalBlocker(course, _ => null).Should().Contain("Sätt ihop stationerna igen");
        }

        [Fact]
        public void Built_stations_are_the_numbers_with_content_in_any_weapon_class()
        {
            var named = ",\"name\":\"Älgen\"";
            CourseAssembly.BuiltStations(Cfg(Wc("A", St(2, 9, named)) + "," + Wc("C", St(1, 9, named), St(2, 9)), "\"_stationCount\":6"))
                .Should().Equal(1, 2);
            CourseAssembly.BuiltStations(null).Should().BeEmpty();
        }

        [Fact]
        public void A_station_with_only_the_configurators_defaults_is_not_built()
        {
            // Planeringstestet k2: "5 av 8 byggda" — konfiguratorn sparar varje station med en tom figur.
            var empty = ",\"targetGroups\":[{\"group\":1,\"figures\":[{\"figureNumber\":1,\"behavior\":\"Fast\"}]}]";
            var withTarget = ",\"targetGroups\":[{\"group\":1,\"figures\":[{\"figureNumber\":1,\"targetName\":\"C 1/4\"}]}]";
            CourseAssembly.BuiltStations(Cfg(Wc("C", St(1, 30, empty), St(2, 30, withTarget)))).Should().Equal(2);
        }

        // ---------------------------------------------------------------- en förening byter stationer (Stationerna-vyn)

        [Fact]
        public void A_part_that_gets_a_new_station_keeps_its_own_work_and_takes_the_new_one_from_the_whole()
        {
            // Klubb X hade tävlingens 1 och 5 (delens 1 och 2) och får 3.
            var part = Cfg(Wc("C", St(1, 11), St(2, 15)), "\"_courseStations\":[1,5],\"_scoringMode\":\"Normal\"");
            var course = Cfg(Wc("C", St(1, 91), St(3, 93), St(5, 95)));
            var r = AreaConfigBlob.Resync(part, course, new[] { 1, 3, 5 });
            AreaConfigBlob.CourseStations(r).Should().Equal(1, 3, 5);
            Numbers(r, "C").Should().Equal(1, 2, 3);
            Time(r, "C", 1).Should().Be(11, "delens eget arbete står kvar");
            Time(r, "C", 2).Should().Be(93, "den nya stationen kommer ur helheten");
            Time(r, "C", 3).Should().Be(15);
            JObject.Parse(r).Value<string>("_scoringMode").Should().Be("Normal");
        }

        [Fact]
        public void A_part_that_loses_a_station_drops_it_and_is_numbered_again()
        {
            var part = Cfg(Wc("C", St(1, 12), St(2, 13), St(3, 14)), "\"_courseStations\":[2,3,4]");
            var r = AreaConfigBlob.Resync(part, null, new[] { 2, 4 });
            AreaConfigBlob.CourseStations(r).Should().Equal(2, 4);
            Numbers(r, "C").Should().Equal(1, 2);
            Time(r, "C", 2).Should().Be(14);
        }

        [Fact]
        public void A_new_station_that_the_whole_has_not_built_is_left_empty()
        {
            var part = Cfg(Wc("C", St(1, 12)), "\"_courseStations\":[2]");
            var r = AreaConfigBlob.Resync(part, null, new[] { 2, 7 });
            AreaConfigBlob.CourseStations(r).Should().Equal(2, 7);
            Numbers(r, "C").Should().Equal(1);
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
