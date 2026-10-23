using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// P2b: bemanningens ägare. Varje fråga på en Staff-tabell går genom <see cref="StaffOwner.Where"/> och
    /// varje rad prövas med <see cref="StaffOwner.Owns"/> — samma regel i SQL och i C#.
    /// </summary>
    public class StaffOwnerTests
    {
        [Fact]
        public void Where_ger_planeringens_rader_eller_tavlingens_som_saknar_planering()
        {
            StaffOwner.Where(0).Should().Be("(PlanningId = @0 OR (PlanningId IS NULL AND CompetitionId = @1))");
            StaffOwner.Where(2, "sa.").Should().Be("(sa.PlanningId = @2 OR (sa.PlanningId IS NULL AND sa.CompetitionId = @3))");
        }

        [Fact]
        public void Parametrarna_matchar_aldrig_nagot_nar_delen_saknas()
        {
            var unlinked = new StaffOwner(12, null);
            unlinked.P.Should().Be(12);
            unlinked.C.Should().Be(-1);

            var legacy = StaffOwner.ForCompetitionOnly(500);
            legacy.P.Should().Be(-1);
            legacy.C.Should().Be(500);
        }

        [Fact]
        public void En_kopplad_planering_ager_sina_rader_och_tavlingens_gamla()
        {
            var o = new StaffOwner(12, 500);
            o.Owns(12, 500).Should().BeTrue();   // planeringens rad
            o.Owns(12, null).Should().BeTrue();  // planeringens rad, losskopplad tidigare
            o.Owns(null, 500).Should().BeTrue(); // tävlingens rad som ännu inte tagits in
            o.Owns(13, 500).Should().BeFalse();  // en annan planerings rad, även med samma tävling
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
        public void En_tavling_utan_planering_ager_inte_en_planerings_rad_med_samma_tavling()
        {
            // Raden har tagits in i en planering: då är det planeringen som äger den, inte tävlingen ensam.
            var o = StaffOwner.ForCompetitionOnly(500);
            o.Owns(null, 500).Should().BeTrue();
            o.Owns(12, 500).Should().BeFalse();
            o.PlanningIdOrNull.Should().BeNull();
        }

        [Fact]
        public void Anmalningslanken_foljer_agaren()
        {
            new StaffOwner(12, 500).SignupPath.Should().Be("/bemanna?p=12");
            new StaffOwner(12, null).SignupPath.Should().Be("/bemanna?p=12");
            StaffOwner.ForCompetitionOnly(500).SignupPath.Should().Be("/bemanna?c=500");
        }

        [Fact]
        public void Minnesnyckeln_skiljer_lagena_at()
        {
            new StaffOwner(12, 500).MemoKey.Should().NotBe(new StaffOwner(12, null).MemoKey);
            StaffOwner.ForCompetitionOnly(500).MemoKey.Should().NotBe(new StaffOwner(12, 500).MemoKey);
        }
    }
}
