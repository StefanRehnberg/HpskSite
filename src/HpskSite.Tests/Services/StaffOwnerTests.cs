using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Bemanningens ägare. Bemanningen ägs av en planering, alltid (Stefan 2026-10-27: dubbelägarskapet är borta). Varje fråga på en Staff-tabell går genom
    /// <see cref="StaffOwner.Where"/> och varje rad prövas med <see cref="StaffOwner.Owns"/> — samma regel i SQL och i C#.
    /// </summary>
    public class StaffOwnerTests
    {
        [Fact]
        public void Where_ger_bara_planeringens_rader_och_ingen_tavlingsagd_rad()
        {
            StaffOwner.Where(0).Should().Be("(PlanningId = @0)");
            StaffOwner.Where(2, "sa.").Should().Be("(sa.PlanningId = @2)");
            StaffOwner.Where(0).Should().NotContain("CompetitionId", "en rad utan planering ägs av ingen: den väntar på att planeringen startas och tar in den");
        }

        [Fact]
        public void Where_har_en_parameter_planeringen_och_Filter_har_tva_planeringen_och_familjens_tavling()
        {
            new StaffOwner(12, 500).P.Should().Be(12);
            new StaffOwner(12, null).FamilyComp.Should().Be(-1, "en planering utan tävling matchar aldrig en tävlings rader");
            new StaffOwner(12, 500).FamilyComp.Should().Be(-1, "utan IncludeFamily läser vyn bara planeringens egna rader");
            new StaffOwner(12, 500) { IncludeFamily = true }.FamilyComp.Should().Be(500);
            new StaffOwner(12, null) { IncludeFamily = true }.FamilyComp.Should().Be(-1);
        }

        [Fact]
        public void En_planering_ager_bara_rader_med_sitt_eget_id()
        {
            var o = new StaffOwner(12, 500);
            o.Owns(12, 500).Should().BeTrue();    // planeringens rad
            o.Owns(12, null).Should().BeTrue();   // planeringens rad, losskopplad tidigare
            o.Owns(null, 500).Should().BeFalse(); // en tävlingsägd rad som ännu inte tagits in: ingen äger den än
            o.Owns(13, 500).Should().BeFalse();   // en annan planerings rad, även med samma tävling
            o.Owns(null, 501).Should().BeFalse();
        }

        [Fact]
        public void En_planering_utan_tavling_ager_bara_sina_egna_rader()
        {
            var o = new StaffOwner(12, null);
            o.Owns(12, null).Should().BeTrue();
            o.Owns(null, 500).Should().BeFalse();
            o.Owns(null, null).Should().BeFalse();
            o.HasCompetition.Should().BeFalse();
            o.CompetitionIdOrNull.Should().BeNull();
        }

        [Fact]
        public void Tavlingens_vyer_ser_planeringens_rader_men_aldrig_rader_utan_planering()
        {
            // Samma text för alla ägare (alltid två parametrar); familjeläget avgörs av andra parametern (FamilyComp, -1 = ingen familj).
            new StaffOwner(12, 500) { IncludeFamily = true }.Filter(0).Should().Be("(PlanningId = @0 OR (PlanningId IS NOT NULL AND CompetitionId = @1))");
            new StaffOwner(12, 500).Filter(0).Should().Be("(PlanningId = @0 OR (PlanningId IS NOT NULL AND CompetitionId = @1))");
            new StaffOwner(12, 500).Filter(0, "sa.").Should().Contain("sa.CompetitionId = @1").And.Contain("sa.PlanningId IS NOT NULL", "rader utan planering väntar på adoptionen och syns inte i någon vy");
        }

        [Fact]
        public void Planeringens_id_ar_alltid_med_vid_insattning()
        {
            new StaffOwner(12, 500).PlanningIdOrNull.Should().Be(12);
            new StaffOwner(12, null).PlanningIdOrNull.Should().Be(12);
        }

        [Fact]
        public void Anmalningslanken_foljer_planeringen()
        {
            new StaffOwner(12, 500).SignupPath.Should().Be("/bemanna?p=12");
            new StaffOwner(12, null).SignupPath.Should().Be("/bemanna?p=12");
        }

        [Fact]
        public void Minnesnyckeln_skiljer_lagena_at()
        {
            new StaffOwner(12, 500).MemoKey.Should().NotBe(new StaffOwner(12, null).MemoKey);
            new StaffOwner(12, 500) { IncludeFamily = true }.MemoKey.Should().NotBe(new StaffOwner(12, 500).MemoKey);
        }
    }
}
