using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using Newtonsoft.Json.Linq;
using Xunit;

namespace HpskSite.Tests.Models
{
    public class AreaConfigBlobTests
    {
        private const string Source = @"{
            ""_mode"":""simple"",""_morker"":true,""_scoringMode"":""Poang"",""_attachedConfigId"":42,""_linkedGroups"":[[""A"",""B""]],
            ""C"":{""stations"":[{""station"":1,""name"":""Ett""},{""station"":3,""name"":""Tre""},{""station"":4,""name"":""Fyra"",""_linkedFromConfigId"":7},{""station"":6,""name"":""Sex""}]},
            ""A"":{""Stations"":[{""Station"":4,""name"":""A-fyra""}]}
        }";

        [Fact]
        public void Omradets_stationer_numreras_1_till_N_i_banans_ordning()
        {
            var blob = JObject.Parse(AreaConfigBlob.Build(Source, new[] { 3, 4, 5 }));
            var c = (JArray)blob["C"]!["stations"]!;
            c.Select(s => s.Value<int>("station")).Should().Equal(1, 2);
            c.Select(s => s.Value<string>("name")).Should().Equal("Tre", "Fyra");
        }

        [Fact]
        public void PascalCase_i_kallan_lases_och_skrivs_som_camelCase()
        {
            var blob = JObject.Parse(AreaConfigBlob.Build(Source, new[] { 3, 4, 5 }));
            var a = (JArray)blob["A"]!["stations"]!;
            a.Should().ContainSingle();
            a[0].Value<int>("station").Should().Be(2);
            ((JObject)a[0]).Property("Station").Should().BeNull();
        }

        [Fact]
        public void Lage_morker_och_tavlingstyp_foljer_med_men_aldrig_anslutningen()
        {
            var blob = JObject.Parse(AreaConfigBlob.Build(Source, new[] { 3 }));
            blob.Value<string>("_mode").Should().Be("simple");
            blob.Value<bool>("_morker").Should().BeTrue();
            blob.Value<string>("_scoringMode").Should().Be("Poang");
            blob.Property("_attachedConfigId").Should().BeNull();
            blob.Property("_linkedGroups").Should().BeNull();
        }

        [Fact]
        public void Lankar_till_andra_konfigurationer_tas_bort()
        {
            var blob = JObject.Parse(AreaConfigBlob.Build(Source, new[] { 4 }));
            ((JObject)blob["C"]!["stations"]![0]!).Properties().Should().NotContain(p => p.Name.StartsWith("_linkedFrom"));
        }

        [Fact]
        public void Banans_nummer_lagras_och_lases_tillbaka()
        {
            AreaConfigBlob.CourseStations(AreaConfigBlob.Build(Source, new[] { 3, 4, 5 })).Should().Equal(3, 4, 5);
        }

        [Fact]
        public void Utan_kalla_bara_mappningen()
        {
            var blob = JObject.Parse(AreaConfigBlob.Build(null, new[] { 7, 8 }));
            blob.Properties().Select(p => p.Name).Should().Equal(AreaConfigBlob.CourseStationsKey);
            AreaConfigBlob.CourseStations(blob.ToString()).Should().Equal(7, 8);
        }

        [Fact]
        public void Trasig_kalla_ger_bara_mappningen()
        {
            AreaConfigBlob.CourseStations(AreaConfigBlob.Build("{inte json", new[] { 2 })).Should().Equal(2);
        }

        [Fact]
        public void Vapenklass_utan_omradets_stationer_utelamnas()
        {
            var blob = JObject.Parse(AreaConfigBlob.Build(Source, new[] { 1 }));
            blob.Property("A").Should().BeNull();
            blob["C"]!["stations"]!.Should().ContainSingle();
        }
    }
}
