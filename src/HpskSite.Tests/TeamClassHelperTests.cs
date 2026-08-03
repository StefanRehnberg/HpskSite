using System.Linq;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tests for Springskytte team-class eligibility. The rule these guard (2026-08-03, after the
    /// SM dress rehearsal): an H-lag / H-stafett accepts BOTH genders, a D-lag / D-stafett stays
    /// Dam-only. For lagtävling that is expressed purely as a whitelist of individual classes, so
    /// these tests are the enforcement's only regression net.
    /// </summary>
    public class TeamClassHelperTests
    {
        private static readonly string[] AllSpringskytteClasses =
        {
            "A-H 15", "A-H 18", "A-H jun", "A-H 21", "A-H 35", "A-H 50", "A-H 60", "A-H 65", "A-H 70",
            "A-D 15", "A-D 18", "A-D jun", "A-D 21", "A-D 35", "A-D 50", "A-D 60", "A-D 65", "A-D 70",
            "C-H 15", "C-H 18", "C-H jun", "C-H 21", "C-H 35", "C-H 50", "C-H 60", "C-H 65", "C-H 70",
            "C-D 15", "C-D 18", "C-D jun", "C-D 21", "C-D 35", "C-D 50", "C-D 60", "C-D 65", "C-D 70",
        };

        [Theory]
        [InlineData("A-Herrar", "A-D 21")]
        [InlineData("A-Herrar", "A-D jun")]
        [InlineData("A-Herrar", "A-D 60")]
        [InlineData("C-Herrar", "C-D 21")]
        [InlineData("C-Herrar", "C-D 15")]
        [InlineData("C-Herrar", "C-D 60")]
        public void HerrLag_AcceptsDamShooters(string teamClass, string damClass)
        {
            var compatible = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte: true);
            Assert.Contains(damClass, compatible);
        }

        [Theory]
        [InlineData("A-Herrar", "A-H 21")]
        [InlineData("C-Herrar", "C-H 35")]
        public void HerrLag_StillAcceptsHerrShooters(string teamClass, string herrClass)
        {
            var compatible = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte: true);
            Assert.Contains(herrClass, compatible);
        }

        [Theory]
        [InlineData("A-Damer", "A-H 21")]
        [InlineData("A-Damer", "A-H jun")]
        [InlineData("C-Damer", "C-H 21")]
        [InlineData("C-Damer", "C-H 60")]
        public void DamLag_RejectsHerrShooters(string teamClass, string herrClass)
        {
            var compatible = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte: true);
            Assert.DoesNotContain(herrClass, compatible);
        }

        [Theory]
        [InlineData("A-Damer", "A-D 21")]
        [InlineData("C-Damer", "C-D 35")]
        public void DamLag_AcceptsDamShooters(string teamClass, string damClass)
        {
            var compatible = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte: true);
            Assert.Contains(damClass, compatible);
        }

        [Theory]
        [InlineData("A-Herrar", "C-D 21")]
        [InlineData("A-Herrar", "C-H 21")]
        [InlineData("C-Herrar", "A-D 21")]
        [InlineData("C-Damer", "A-D 21")]
        public void WeaponClassesNeverMix(string teamClass, string otherWeaponClass)
        {
            var compatible = TeamClassHelper.GetCompatibleIndividualClasses(teamClass, isSpringskytte: true);
            Assert.DoesNotContain(otherWeaponClass, compatible);
        }

        [Fact]
        public void VeteranLag_IsMixed_Unchanged()
        {
            var compatible = TeamClassHelper.GetCompatibleIndividualClasses("A-Veteran", isSpringskytte: true);
            Assert.Contains("A-H 65", compatible);
            Assert.Contains("A-D 70", compatible);
        }

        [Fact]
        public void DamClassesAlone_DoNotOfferAHerrLag()
        {
            // A competition running only Dam classes must not offer "A-Herrar" — the Dam classes make
            // a Dam ELIGIBLE for a Herrlag, they must not bring the Herrlag into existence.
            var damOnly = new[] { "A-D 21", "A-D 35", "C-D 21" };
            var offered = TeamClassHelper.GetTeamClasses(damOnly, isSpringskytte: true)
                .Select(tc => tc.TeamClass).ToList();

            Assert.DoesNotContain("A-Herrar", offered);
            Assert.DoesNotContain("C-Herrar", offered);
            Assert.Contains("A-Damer", offered);
            Assert.Contains("C-Damer", offered);
        }

        [Fact]
        public void HerrLag_CompatibleClassesAreFilteredToTheCompetition()
        {
            // Only the classes the competition actually runs may be offered as compatible.
            var partial = new[] { "A-H 21", "A-D 21" };
            var herrLag = TeamClassHelper.GetTeamClasses(partial, isSpringskytte: true)
                .Single(tc => tc.TeamClass == "A-Herrar");

            Assert.Equal(new[] { "A-H 21", "A-D 21" }, herrLag.CompatibleClasses);
        }

        [Fact]
        public void TeamSizes_UnchangedByTheGenderFix()
        {
            // Herrlag = 3+1, Damlag = 2+1 (IsLadiesClass), Veteran = 2+1. Renaming a team class or
            // dropping "Dam" from its name would silently change its size — hence this guard.
            Assert.Equal((3, 1), TeamClassHelper.GetTeamSize("A-Herrar"));
            Assert.Equal((3, 1), TeamClassHelper.GetTeamSize("C-Herrar"));
            Assert.Equal((2, 1), TeamClassHelper.GetTeamSize("A-Damer"));
            Assert.Equal((2, 1), TeamClassHelper.GetTeamSize("C-Damer"));
            Assert.Equal((2, 1), TeamClassHelper.GetTeamSize("A-Veteran"));
        }

        [Fact]
        public void AllSpringskytteTeamClasses_AreOfferedOnAFullCompetition()
        {
            var offered = TeamClassHelper.GetTeamClasses(AllSpringskytteClasses, isSpringskytte: true)
                .Select(tc => tc.TeamClass).ToList();

            Assert.Equal(6, offered.Count);
            Assert.Contains("A-Herrar", offered);
            Assert.Contains("A-Damer", offered);
            Assert.Contains("A-Veteran", offered);
            Assert.Contains("C-Herrar", offered);
            Assert.Contains("C-Damer", offered);
            Assert.Contains("C-Veteran", offered);
        }

        [Theory]
        [InlineData("Stafett Senior Herr", null)]   // mixed — both genders may run
        [InlineData("Stafett Junior", null)]
        [InlineData("Stafett Veteran", null)]
        [InlineData("Stafett Senior Dam", "F")]     // Dam-only
        [InlineData("A-Herrar", null)]              // not a stafett class
        [InlineData("not-a-class", null)]
        public void StafettGenderRestriction(string teamClass, string? expected)
        {
            Assert.Equal(expected, TeamClassHelper.GetStafettGenderRestriction(teamClass));
        }

        [Theory]
        [InlineData("A-Herrar", false)]
        [InlineData("C-Damer", false)]
        [InlineData("A-Veteran", false)]
        [InlineData("Stafett Junior", true)]
        [InlineData("Stafett Senior Herr", true)]
        [InlineData("Stafett Senior Dam", true)]
        [InlineData("Stafett Veteran", true)]
        [InlineData("C Dam", false)]
        public void IsStafettClass(string teamClass, bool expected)
        {
            Assert.Equal(expected, TeamClassHelper.IsStafettClass(teamClass));
        }

        [Theory]
        [InlineData("A-Herrar", "A")]
        [InlineData("A-Damer", "A")]
        [InlineData("A-Veteran", "A")]
        [InlineData("C-Herrar", "C")]
        [InlineData("C-Damer", "C")]
        [InlineData("Stafett Senior Dam", "")]   // relay is always class C — no prefix to read
        [InlineData("C Dam", "")]                // standard-discipline class, not the "X-" shape
        [InlineData("", "")]
        public void GetSpringskytteWeaponGroup(string teamClass, string expected)
        {
            Assert.Equal(expected, TeamClassHelper.GetSpringskytteWeaponGroup(teamClass));
        }

        [Fact]
        public void StandardDisciplines_AreUntouched()
        {
            // TeamClassHelper is shared — the Springskytte change must not leak into precision/fält.
            var cDam = TeamClassHelper.GetCompatibleIndividualClasses("C Dam", isSpringskytte: false);
            Assert.Equal(new[] { "C1_Dam", "C2_Dam", "C3_Dam" }, cDam);

            var cOpen = TeamClassHelper.GetCompatibleIndividualClasses("C Öppen", isSpringskytte: false);
            Assert.Equal(new[] { "C1", "C2", "C3" }, cOpen);
        }
    }
}
