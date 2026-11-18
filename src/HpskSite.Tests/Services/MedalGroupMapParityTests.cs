using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// S0 (plan v2), commit 1: <see cref="MedalGroupMap"/> svarar EXAKT som <c>ChampionshipCategory.For/BuildLookup</c> gjorde, för varje klass och
    /// båda indelningarna — innan någon anropare byts. Förväntade värden kommer ur den gamla funktionen, som inte ändras i det här steget.
    /// </summary>
    public class MedalGroupMapParityTests
    {
        private static IEnumerable<string> AllClassNames() =>
            ShootingClasses.All.SelectMany(c => new[] { c.Id, c.Name }).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct();

        [Fact]
        public void The_map_gives_the_same_group_as_the_old_function_for_every_class()
        {
            var names = AllClassNames().Concat(new[] { "", "  ", "Okänd klass" }).ToList();
            names.Should().NotBeEmpty();

            foreach (var name in names)
            {
                MedalGroupMap.Shb.GroupFor(name).Should().Be(ChampionshipCategory.For(name, true), $"Shb / {name}");
                MedalGroupMap.WeaponGroup.GroupFor(name).Should().Be(ChampionshipCategory.For(name, false), $"WeaponGroup / {name}");
            }
        }

        [Fact]
        public void The_lookup_is_the_same_as_the_old_one()
        {
            var classes = ShootingClasses.All.Select(c => c.Name).ToList();

            MedalGroupMap.Shb.BuildLookup(classes).Should().BeEquivalentTo(ChampionshipCategory.BuildLookup(classes, true));
            MedalGroupMap.WeaponGroup.BuildLookup(classes).Should().BeEquivalentTo(ChampionshipCategory.BuildLookup(classes, false));
        }

        [Theory]
        [InlineData("Klubbmästerskap", true)]
        [InlineData("Kretsmästerskap", true)]
        [InlineData("Landsdelsmästerskap", true)]
        [InlineData("Svenskt Mästerskap", true)]
        [InlineData("Klubbmästerskap", false)]
        public void The_map_follows_the_rule_for_the_tavling(string scope, bool perWeaponGroup)
        {
            var rule = ChampionshipRule.ForScope(scope, perWeaponGroup);

            // Efter nivåspärren: bara klubb/krets med boolen sann ger en uppsättning per vapengrupp.
            var expected = ChampionshipCategory.SplitsGroupC(scope, perWeaponGroup) ? MedalGroupMap.Shb : MedalGroupMap.WeaponGroup;
            MedalGroupMap.For(rule).Should().BeSameAs(expected);
        }

        [Fact]
        public void C_is_split_only_in_the_shb_map()
        {
            // Klassnamnen är registrets ("C1 Dam" och "C_Vet_Y" är klasser; "C Dam" är ett kategorinamn, inte en klass).
            MedalGroupMap.Shb.GroupFor("C1 Dam").Should().Be("C Dam");
            MedalGroupMap.Shb.GroupFor("C1").Should().Be("C");
            MedalGroupMap.Shb.GroupFor("A2").Should().Be("A");
            MedalGroupMap.WeaponGroup.GroupFor("C1 Dam").Should().Be("C");
            MedalGroupMap.WeaponGroup.GroupFor("C_Vet_Y").Should().Be("C");
            MedalGroupMap.WeaponGroup.GroupFor("A2").Should().Be("A");
        }
    }
}
