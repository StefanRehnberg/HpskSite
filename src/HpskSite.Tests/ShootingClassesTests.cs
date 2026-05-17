using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Tests for the authoritative ShootingClasses registry helpers introduced when
    /// promoting "A_opt" to its own weapon class with three levels.
    /// </summary>
    public class ShootingClassesTests
    {
        [Theory]
        [InlineData("A1", "A")]
        [InlineData("A2", "A")]
        [InlineData("A3", "A")]
        [InlineData("A_opt_1", "A_Opt")]
        [InlineData("A_opt_2", "A_Opt")]
        [InlineData("A_opt_3", "A_Opt")]
        [InlineData("B1", "B")]
        [InlineData("C2", "C")]
        [InlineData("C_Vet_Y", "C")]
        [InlineData("C2_Dam", "C")]
        [InlineData("R3", "R")]
        [InlineData("M1", "M")]
        [InlineData("L_Jun", "L")]
        public void GetWeaponClassCode_ResolvesById(string id, string expected)
        {
            Assert.Equal(expected, ShootingClasses.GetWeaponClassCode(id));
        }

        [Theory]
        [InlineData("A Opt 1", "A_Opt")]
        [InlineData("A Opt 2", "A_Opt")]
        [InlineData("A Opt 3", "A_Opt")]
        [InlineData("C Vet Y", "C")]
        public void GetWeaponClassCode_ResolvesByName(string name, string expected)
        {
            Assert.Equal(expected, ShootingClasses.GetWeaponClassCode(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("ZZ_unknown")]
        [InlineData("A_opt")] // legacy id no longer in the registry
        public void GetWeaponClassCode_UnknownReturnsEmpty(string? input)
        {
            Assert.Equal(string.Empty, ShootingClasses.GetWeaponClassCode(input));
        }

        [Fact]
        public void GetWeaponClass_ReturnsEnum()
        {
            Assert.Equal(WeaponClass.A_Opt, ShootingClasses.GetWeaponClass("A_opt_1"));
            Assert.Equal(WeaponClass.A,     ShootingClasses.GetWeaponClass("A1"));
            Assert.Null(ShootingClasses.GetWeaponClass("not-a-class"));
        }

        [Fact]
        public void Registry_ContainsAOptLevels()
        {
            Assert.NotNull(ShootingClasses.GetById("A_opt_1"));
            Assert.NotNull(ShootingClasses.GetById("A_opt_2"));
            Assert.NotNull(ShootingClasses.GetById("A_opt_3"));
            // Old single A_opt entry must be gone — call sites should migrate to one of the 3 levels.
            Assert.Null(ShootingClasses.GetById("A_opt"));
        }

        // ── A-family subgroups (AM/AP/AG): own weapon groups, 1-3 competence ladder ──

        [Theory]
        [InlineData("A_m_1", "A_M")]
        [InlineData("A_m_2", "A_M")]
        [InlineData("A_m_3", "A_M")]
        [InlineData("A_p_1", "A_P")]
        [InlineData("A_p_2", "A_P")]
        [InlineData("A_p_3", "A_P")]
        [InlineData("A_g_1", "A_G")]
        [InlineData("A_g_2", "A_G")]
        [InlineData("A_g_3", "A_G")]
        public void GetWeaponClassCode_ResolvesAFamilySubgroupsById(string id, string expected)
        {
            Assert.Equal(expected, ShootingClasses.GetWeaponClassCode(id));
        }

        [Theory]
        [InlineData("AM1", "A_M")]
        [InlineData("AM2", "A_M")]
        [InlineData("AM3", "A_M")]
        [InlineData("AP1", "A_P")]
        [InlineData("AP2", "A_P")]
        [InlineData("AP3", "A_P")]
        [InlineData("AG1", "A_G")]
        [InlineData("AG2", "A_G")]
        [InlineData("AG3", "A_G")]
        public void GetWeaponClassCode_ResolvesAFamilySubgroupsByName(string name, string expected)
        {
            Assert.Equal(expected, ShootingClasses.GetWeaponClassCode(name));
        }

        [Fact]
        public void Registry_ContainsAFamilySubgroupLevels()
        {
            Assert.NotNull(ShootingClasses.GetById("A_m_1"));
            Assert.NotNull(ShootingClasses.GetById("A_m_2"));
            Assert.NotNull(ShootingClasses.GetById("A_m_3"));
            Assert.NotNull(ShootingClasses.GetById("A_p_1"));
            Assert.NotNull(ShootingClasses.GetById("A_p_2"));
            Assert.NotNull(ShootingClasses.GetById("A_p_3"));
            Assert.NotNull(ShootingClasses.GetById("A_g_1"));
            Assert.NotNull(ShootingClasses.GetById("A_g_2"));
            Assert.NotNull(ShootingClasses.GetById("A_g_3"));
        }

        [Fact]
        public void AFamilySubgroups_HaveDistinctWeaponClassEnums()
        {
            // Each subgroup is its own weapon class — they don't share with A or each other.
            Assert.Equal(WeaponClass.A_M, ShootingClasses.GetWeaponClass("A_m_1"));
            Assert.Equal(WeaponClass.A_P, ShootingClasses.GetWeaponClass("A_p_1"));
            Assert.Equal(WeaponClass.A_G, ShootingClasses.GetWeaponClass("A_g_1"));
            // And NOT A — the registry separation is the whole point of the subgroup design.
            Assert.NotEqual(WeaponClass.A, ShootingClasses.GetWeaponClass("A_m_1"));
            Assert.NotEqual(WeaponClass.A, ShootingClasses.GetWeaponClass("A_p_1"));
            Assert.NotEqual(WeaponClass.A, ShootingClasses.GetWeaponClass("A_g_1"));
        }
    }
}
