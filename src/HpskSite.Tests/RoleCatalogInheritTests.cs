using FluentAssertions;
using HpskSite.Models.Staffing;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Persontestet, scenario 6: en vald Stationschef tappade sin station och rutnätet fick EN rad "0/8" i stället för åtta.</summary>
    public class RoleCatalogInheritTests
    {
        private static FunctionaryRole BuiltIn() => new()
        {
            Key = "stationschef", DisplayName = "Stationschef", DefaultScopeType = StaffScopeType.Station,
            SupportsTargetRange = true, IsResponsibleByDefault = true, Description = "Ansvarar för en station.", Needs = new[] { "Tidur" },
        };

        [Fact]
        public void En_rad_utan_egen_plats_ar_fortfarande_en_stationsroll()
        {
            var row = new FunctionaryRole { Key = "stationschef", DisplayName = "Stationschef", DefaultScopeType = "" };
            RoleCatalogService.Inherit(row, BuiltIn()).DefaultScopeType.Should().Be(StaffScopeType.Station);
        }

        [Fact]
        public void Tavelintervall_ansvar_och_vad_som_behovs_foljer_med()
        {
            var r = RoleCatalogService.Inherit(new FunctionaryRole { Key = "stationschef", DisplayName = "Stationschef" }, BuiltIn());
            r.SupportsTargetRange.Should().BeTrue();
            r.IsResponsibleByDefault.Should().BeTrue();
            r.Needs.Should().Equal("Tidur");
            r.Description.Should().Be("Ansvarar för en station.");
        }

        [Fact]
        public void Radens_egna_varden_gar_alltid_fore()
        {
            var row = new FunctionaryRole { Key = "stationschef", DisplayName = "Banchef", DefaultScopeType = StaffScopeType.All, Description = "Min text", Needs = new[] { "Radio" } };
            var r = RoleCatalogService.Inherit(row, BuiltIn());
            r.DisplayName.Should().Be("Banchef");
            r.DefaultScopeType.Should().Be(StaffScopeType.All);
            r.Description.Should().Be("Min text");
            r.Needs.Should().Equal("Radio");
        }
    }
}
