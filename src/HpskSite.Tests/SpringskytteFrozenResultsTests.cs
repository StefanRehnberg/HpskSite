using System.Text.Json.Nodes;
using HpskSite.CompetitionTypes.Springskytte.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Utanför mästerskapet, F0b: den publika springskyttelistan serveras ur en frusen bild, filtrerad på de
    /// vapengrupper som är publicerade just nu.
    /// </summary>
    public class SpringskytteFrozenResultsTests
    {
        private const string Snap =
            "{\"success\":true,\"classGroups\":[" +
            "{\"weaponClass\":\"A\",\"className\":\"Vapengrupp A - H\",\"shooters\":[{\"name\":\"x\"}]}," +
            "{\"weaponClass\":\"C\",\"className\":\"Vapengrupp C - H\",\"shooters\":[{\"name\":\"y\"}]}]," +
            "\"resultsExist\":true,\"resultsOfficial\":true,\"officialWeaponClasses\":[\"A\",\"C\"],\"supportsPerClassPublish\":true}";

        private static JsonObject Serve(string snap, params string[] official)
            => (JsonObject)JsonNode.Parse(SpringskytteFrozenResults.Serve(snap, official)!)!;

        [Fact]
        public void Only_the_published_weapon_classes_are_served()
        {
            var r = Serve(Snap, "A");
            var groups = (JsonArray)r["classGroups"]!;
            Assert.Single(groups);
            Assert.Equal("A", groups[0]!["weaponClass"]!.GetValue<string>());
            Assert.True(r["resultsOfficial"]!.GetValue<bool>());
            Assert.Equal("A", ((JsonArray)r["officialWeaponClasses"]!).Single()!.GetValue<string>());
        }

        [Fact]
        public void Un_publishing_every_class_hides_everything_without_recomputing()
        {
            var r = Serve(Snap);
            Assert.Empty((JsonArray)r["classGroups"]!);
            Assert.False(r["resultsOfficial"]!.GetValue<bool>());
        }

        [Fact]
        public void Class_match_ignores_case_and_keeps_the_shooters_exactly_as_frozen()
        {
            var r = Serve(Snap, "c");
            var g = (JsonObject)((JsonArray)r["classGroups"]!).Single()!;
            Assert.Equal("y", g["shooters"]![0]!["name"]!.GetValue<string>());
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not json")]
        [InlineData("{\"success\":true}")]
        [InlineData("[1,2]")]
        public void An_unusable_snapshot_falls_back_to_live(string snap)
            => Assert.Null(SpringskytteFrozenResults.Serve(snap, new[] { "A" }));
    }
}
