using HpskSite.CompetitionTypes.Common;
using HpskSite.Models.Competitions;
using HpskSite.Services.Competitions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// S3 (utanför mästerskapet): vapengruppslistan och "vilka deltar" lagras bara i kända former, och växeln per tävling följer
    /// aldrig med en kopia. Neutralt: tomt/okänt = dagens beteende (alla grupper, alla deltar, av).
    /// </summary>
    public class ChampionshipSettingsTests
    {
        [Fact]
        public void Weapon_groups_are_stored_canonically_known_distinct_and_sorted()
        {
            var known = ChampionshipWeaponGroups.KnownCodes;
            Assert.Contains("C", known);
            Assert.Contains("A", known);

            Assert.Equal("A,C", ChampionshipWeaponGroups.Canonicalize(" c , A,C,zzz "));
            Assert.Equal(new[] { "A", "C" }, ChampionshipWeaponGroups.Parse("C,a"));
        }

        [Fact]
        public void An_empty_or_unknown_list_means_all_groups()
        {
            Assert.Null(ChampionshipWeaponGroups.Canonicalize(null));
            Assert.Null(ChampionshipWeaponGroups.Canonicalize("  "));
            Assert.Null(ChampionshipWeaponGroups.Canonicalize("zzz,qqq"));
            Assert.Empty(ChampionshipWeaponGroups.Parse(""));
        }

        [Fact]
        public void Participants_only_keeps_the_known_restrictions()
        {
            Assert.Equal("own", ChampionshipParticipantsSetting.Normalize(" OWN "));
            Assert.Equal("own+neighbours", ChampionshipParticipantsSetting.Normalize("own+neighbours"));
            Assert.Null(ChampionshipParticipantsSetting.Normalize("all"));
            Assert.Null(ChampionshipParticipantsSetting.Normalize("whatever"));
            Assert.Null(ChampionshipParticipantsSetting.Normalize(null));
        }

        [Fact]
        public void The_form_value_is_normalised_on_the_way_in()
        {
            Assert.Equal("A,C", CompetitionFieldBag.ToNodeValue("championshipWeaponGroups", "c,A,zzz"));
            Assert.Null(CompetitionFieldBag.ToNodeValue("championshipWeaponGroups", "zzz"));
            Assert.Equal("own", CompetitionFieldBag.ToNodeValue("championshipParticipants", "own"));
            Assert.Null(CompetitionFieldBag.ToNodeValue("championshipParticipants", "bogus"));
        }

        [Fact]
        public void A_copy_keeps_the_championship_definition()
        {
            var source = new CompetitionInfo
            {
                ChampionshipWeaponGroups = "C", ChampionshipParticipants = "own", HideOutOfCompetitionInPlace = true
            };
            var copy = CompetitionFieldTable.CopyDefinition(source);
            Assert.Equal("C", copy.ChampionshipWeaponGroups);
            Assert.Equal("own", copy.ChampionshipParticipants);
            Assert.True(copy.HideOutOfCompetitionInPlace);
        }
    }
}
