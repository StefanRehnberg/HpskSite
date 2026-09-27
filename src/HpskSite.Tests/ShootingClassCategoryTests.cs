using System.Linq;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// The registry's Category and Level are what every rule and view reads instead of parsing ids.
    /// These tests read the NAME only to cross-check the declared data — a class entered with the
    /// wrong category would otherwise silently land in the wrong team, medal group or dropdown.
    /// </summary>
    public class ShootingClassCategoryTests
    {
        [Fact]
        public void EveryClass_CategoryMatchesItsName()
        {
            foreach (var sc in ShootingClasses.All)
            {
                var expected =
                    sc.Name.EndsWith("Dam") ? ClassCategory.Dam :
                    sc.Name.EndsWith("Vet Y") ? ClassCategory.VeteranYounger :
                    sc.Name.EndsWith("Vet Ä") ? ClassCategory.VeteranOlder :
                    sc.Name.EndsWith("Jun") ? ClassCategory.Junior :
                    ClassCategory.Open;
                Assert.True(expected == sc.Category, $"{sc.Id}: declared {sc.Category}, name says {expected}");
            }
        }

        [Fact]
        public void OnlyCandL_HaveSubCategories()
        {
            var offenders = ShootingClasses.All
                .Where(sc => sc.Category != ClassCategory.Open && sc.Weapon != WeaponClass.C && sc.Weapon != WeaponClass.L)
                .Select(sc => sc.Id).ToList();
            Assert.Empty(offenders);
        }

        [Fact]
        public void Level_IsTheCompetenceDigit_AndMissingWhereThereIsNone()
        {
            foreach (var sc in ShootingClasses.All)
            {
                if (sc.Weapon == WeaponClass.M || sc.IsVeteran || sc.Category == ClassCategory.Junior)
                {
                    Assert.True(sc.Level == null, $"{sc.Id} must have no level, has {sc.Level}");
                    continue;
                }
                var digit = sc.Name.First(char.IsDigit) - '0';
                Assert.True(sc.Level == digit, $"{sc.Id}: level {sc.Level}, name says {digit}");
            }
        }

        [Fact]
        public void Magnum_IsNotACompetenceLadder()
        {
            Assert.All(ShootingClasses.ForWeapon(WeaponClass.M), sc => Assert.Null(sc.Level));
        }

        [Theory]
        [InlineData("C_Vet_Y", ClassCategory.VeteranYounger)]
        [InlineData("C Vet Y", ClassCategory.VeteranYounger)]
        [InlineData("c vet ä", ClassCategory.VeteranOlder)]
        [InlineData(" C2_Dam ", ClassCategory.Dam)]
        [InlineData("L_Jun", ClassCategory.Junior)]
        [InlineData("A_opt_2", ClassCategory.Open)]
        public void Resolve_AcceptsIdAndName(string input, ClassCategory expected)
        {
            Assert.Equal(expected, ShootingClasses.GetCategory(input));
        }

        [Fact]
        public void UnknownStrings_ResolveToNothing()
        {
            Assert.Null(ShootingClasses.Resolve("C2+Dam"));
            Assert.Null(ShootingClasses.Resolve("C-H 50"));
            Assert.False(ShootingClasses.TryGetSubCategoryLabel("C2+Dam", out _));
        }

        [Theory]
        [InlineData("C_Vet_Y", "Vet Y")]
        [InlineData("C_Vet_A", "Vet Ä")]
        [InlineData("C1_Dam", "Dam")]
        [InlineData("C_Jun", "Jun")]
        [InlineData("C1", null)]
        public void SubCategoryLabel_WorksOnTheIdForm(string id, string? expected)
        {
            // The medal services' old string test read "C_VET_Y" as an open class.
            Assert.True(ShootingClasses.TryGetSubCategoryLabel(id, out var label));
            Assert.Equal(expected, label);
        }

        [Theory]
        [InlineData(WeaponClass.A_Opt, "A Opt")]
        [InlineData(WeaponClass.A_M, "AM")]
        [InlineData(WeaponClass.C, "C")]
        public void WeaponGroupLabel_IsHumanReadable(WeaponClass weapon, string expected)
        {
            Assert.Equal(expected, ShootingClasses.WeaponGroupLabel(weapon));
        }

        [Fact]
        public void PickerLayout_CoversEveryWeaponGroupInTheRegistry()
        {
            var missing = ShootingClasses.All.Select(sc => sc.Weapon).Distinct()
                .Where(w => !ShootingClassPickerLayout.SectionOrder.Contains(w)).ToList();
            Assert.Empty(missing);
        }
    }
}
