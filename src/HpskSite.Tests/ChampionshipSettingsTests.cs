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

        [Fact]
        public void The_offered_weapon_groups_for_a_field_competition_are_the_field_groups_and_nothing_from_other_disciplines()
        {
            var offered = ChampionshipWeaponGroups.OfferedFor("Faltskytte", Array.Empty<string?>(), Array.Empty<string?>());

            Assert.Equal(new[] { "A", "A Opt", "B", "C", "R" }, offered);
            foreach (var other in new[] { "AG", "AM", "AP", "L", "M" })
                Assert.DoesNotContain(other, offered);        // precisionens och springskyttets koder hör inte till ett fältmästerskap
        }

        [Fact]
        public void Used_and_already_stored_groups_are_always_offered_and_unknown_codes_never()
        {
            // En tävling där någon anmälts i en grupp utanför fältets vanliga, och ett gammalt val som inte får försvinna ur listan.
            var offered = ChampionshipWeaponGroups.OfferedFor("Faltskytte", new string?[] { "M", "zzz", "", null }, new string?[] { "L" });

            Assert.Contains("M", offered);
            Assert.Contains("L", offered);
            Assert.DoesNotContain("zzz", offered);
            Assert.Equal(offered.OrderBy(c => c, StringComparer.Ordinal).ToList(), offered);   // ordnade
        }

        [Fact]
        public void A_discipline_without_a_base_list_offers_only_what_is_used_or_stored()
        {
            Assert.Empty(ChampionshipWeaponGroups.OfferedFor("MagnumFalt", Array.Empty<string?>(), Array.Empty<string?>()));
            Assert.Equal(new[] { "M" }, ChampionshipWeaponGroups.OfferedFor("MagnumFalt", new string?[] { "m" }, Array.Empty<string?>()));
        }
    }
}
