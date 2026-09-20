using FluentAssertions;
using HpskSite.CompetitionTypes.Common;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Arrangörens medaljindelning: delas vapengrupp C i sina mästerskapsklasser, eller delas EN
    /// uppsättning medaljer ut per vapengrupp?
    ///
    /// Bakgrunden står i <see cref="MedalGrouping"/>: på dev 5591 satte resultatlistan Andy Haard
    /// först på 582 p medan prisutdelningen gav guld till tvåan på 558 — hans mästerskapsklass
    /// hade två deltagare och därmed noll medaljer, trots att arrangören slagit samman alla
    /// klasser till en grupp.
    /// </summary>
    public class MedalGroupingTests
    {
        // ── Vem får välja ────────────────────────────────────────────────────

        [Theory]
        [InlineData("Klubbmästerskap", true)]
        [InlineData("Kretsmästerskap", true)]
        [InlineData("Landsdelsmästerskap", false)]
        [InlineData("Svenskt Mästerskap", false)]
        [InlineData("Ingen", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void CanChoose_OnlyClubAndRegionChampionships(string? scope, bool expected)
        {
            MedalGrouping.CanChoose(scope).Should().Be(expected);
        }

        [Fact]
        public void CanChoose_ReadsAJsonWrappedScope()
        {
            // ⚠️ competitionScope kan vara en FlexibleDropdown som lagrat värdet som en array.
            // Jämförelserna är Ordinal, så utan avskalningen skulle valet tyst vara omöjligt att
            // göra på just de tävlingar där det sparats den vägen.
            MedalGrouping.CanChoose("[\"Klubbmästerskap\"]").Should().BeTrue();
            MedalGrouping.CanChoose("[\"Svenskt Mästerskap\"]").Should().BeFalse();
        }

        // ── Nivåspärren ──────────────────────────────────────────────────────

        [Fact]
        public void PerWeaponGroup_IsIgnoredAboveRegionLevel()
        {
            // Ett värde som blivit kvar från när tävlingen var ett klubbmästerskap får inte slå
            // igenom när den gjorts om till ett SM — där är de fem C-mästerskapen vad förbundet
            // delar ut.
            MedalGrouping.PerWeaponGroup("Klubbmästerskap", storedValue: true).Should().BeTrue();
            MedalGrouping.PerWeaponGroup("Kretsmästerskap", storedValue: true).Should().BeTrue();
            MedalGrouping.PerWeaponGroup("Landsdelsmästerskap", storedValue: true).Should().BeFalse();
            MedalGrouping.PerWeaponGroup("Svenskt Mästerskap", storedValue: true).Should().BeFalse();
        }

        [Fact]
        public void PerWeaponGroup_DefaultsToTheSplitGrouping()
        {
            // Saknad doctype-egenskap ger falskt, alltså dagens beteende. En deploy utan
            // operatörssteget ändrar ingen befintlig tävling.
            MedalGrouping.PerWeaponGroup("Klubbmästerskap", storedValue: false).Should().BeFalse();
        }

        // ── Vad indelningen betyder för kategorierna ─────────────────────────

        [Fact]
        public void SplitsGroupC_FollowsTheChoiceAtClubAndRegionLevel()
        {
            ChampionshipCategory.SplitsGroupC("Klubbmästerskap", medalsPerWeaponGroup: false)
                .Should().BeTrue("delad indelning är standard och SHB:s huvudregel");
            ChampionshipCategory.SplitsGroupC("Klubbmästerskap", medalsPerWeaponGroup: true)
                .Should().BeFalse("arrangören har valt en uppsättning medaljer per vapengrupp");
        }

        [Fact]
        public void SplitsGroupC_AppliesTheLevelGateItself()
        {
            // ⚠️ Metoden grindar SJÄLV, så ett anropsställe som skickar det lagrade värdet rakt
            // in inte kan ta bort dammästerskapet på ett SM. Utan den här spärren vore
            // nivåregeln beroende av att sex anropsställen kommer ihåg den.
            ChampionshipCategory.SplitsGroupC("Svenskt Mästerskap", medalsPerWeaponGroup: true)
                .Should().BeTrue();
            ChampionshipCategory.SplitsGroupC("Landsdelsmästerskap", medalsPerWeaponGroup: true)
                .Should().BeTrue();
        }

        [Fact]
        public void SplitsGroupC_IsFalseOutsideChampionships()
        {
            // Utanför ett mästerskap finns inga mästerskapsmedaljer alls.
            ChampionshipCategory.SplitsGroupC("Ingen", medalsPerWeaponGroup: false).Should().BeFalse();
            ChampionshipCategory.SplitsGroupC("", medalsPerWeaponGroup: true).Should().BeFalse();
        }

        // ── Utfallet: vem hamnar i vilken medaljgrupp ────────────────────────

        [Fact]
        public void PerWeaponGroup_PutsTheWholeCGroupInOneCategory()
        {
            // Precis fallet från dev 5591: åtta skyttar i vapengrupp C, spridda över C2, C3,
            // C2 Dam, C Vet Y och C Vet Ä.
            var classes = new[] { "C2", "C3", "C2_Dam", "C_Vet_Y", "C_Vet_A" };

            var split = classes
                .Select(c => ChampionshipCategory.For(c, ChampionshipCategory.SplitsGroupC("Klubbmästerskap", false)))
                .Distinct()
                .ToList();
            split.Should().BeEquivalentTo(new[] { "C", "C Dam", "C Vet Y", "C Vet Ä" },
                "delad indelning ger fyra mästerskapsklasser, varav tre har för få deltagare");

            var pooled = classes
                .Select(c => ChampionshipCategory.For(c, ChampionshipCategory.SplitsGroupC("Klubbmästerskap", true)))
                .Distinct()
                .ToList();
            // ⚠️ ContainSingle och inte Equal("C", "…"): FluentAssertions läser Equals andra
            // argument som ytterligare ETT element, inte som ett skäl — påståendet blev då
            // rött på ett korrekt utfall.
            pooled.Should().ContainSingle("hela vapengruppen tävlar om en uppsättning medaljer")
                  .Which.Should().Be("C");
        }

        [Fact]
        public void PerWeaponGroup_DoesNotPoolDifferentWeaponGroups()
        {
            // ⚠️ "Slå ihop alla" betyder per VAPENGRUPP, inte bokstavligen alla: en tävling med
            // både A och C ska fortfarande ha ett guld i A och ett i C.
            var splitC = ChampionshipCategory.SplitsGroupC("Klubbmästerskap", true);
            ChampionshipCategory.For("A2", splitC).Should().Be("A");
            ChampionshipCategory.For("C2", splitC).Should().Be("C");
            ChampionshipCategory.For("B1", splitC).Should().Be("B");
        }

        [Fact]
        public void PooledGroupOfEight_GetsAFullSetOfMedals()
        {
            // Och det är det som gör att segraren får sitt guld: åtta i gruppen i stället för
            // två i sin egen klass.
            ChampionshipMedalCount.For(2, isJuniorCategory: false).Medals.Should().Be(0);
            ChampionshipMedalCount.For(8, isJuniorCategory: false).Medals.Should().Be(3);
        }

        // ── Klartexten ytan visar ────────────────────────────────────────────

        [Fact]
        public void Describe_SaysWhichGroupingWasUsed()
        {
            MedalGrouping.Describe(true).Should().Contain("per vapengrupp");
            MedalGrouping.Describe(false).Should().Contain("mästerskapsklass");
            MedalGrouping.Describe(true).Should().NotBe(MedalGrouping.Describe(false));
        }
    }
}
