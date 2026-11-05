using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// S5: förslaget "skjuter utanför mästerskapet" utifrån var skytten tävlar för. Hellre inget förslag än en gissning:
    /// okänd krets/klubb/landsdel ger inget, och förslaget tystnar om NÅGON av skyttens klubbar hör hemma i mästerskapet.
    /// </summary>
    public class OutOfCompetitionRulesTests
    {
        // Klubbar: 1 = Varberg (Halland), 2 = Kungsbacka (Halland), 3 = Luleå (Norrbotten, långt bort), 4 = Göteborg (grannkrets enligt
        // RegionAdjacency), 5 = okänd. Kretskoderna är de riktiga (enum-namnen i RegionAdjacency).
        private static readonly Dictionary<int, ClubOrigin> Clubs = new()
        {
            [1] = new(1, "Varbergs PK", "Halland", "Hallands krets", "Vast"),
            [2] = new(2, "Kungsbacka PK", "Halland", "Hallands krets", "Vast"),
            [3] = new(3, "Luleå PK", "Norrbotten", "Norrbottens krets", "Nord"),
            [4] = new(4, "Göteborgs PK", "Goteborg", "Göteborgs krets", "Vast"),
        };
        private static ClubOrigin? Origin(int id) => Clubs.TryGetValue(id, out var o) ? o : null;

        private static readonly CompetitionOrigin HallandHost = new(1, "Halland", "Hallands krets", "Vast");

        private static OutOfCompetitionSuggestion? Krets(string? participants, int competesFor, params int[] allClubs) =>
            OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Krets, participants, HallandHost, false, competesFor, allClubs, Origin);

        [Fact]
        public void Everybody_takes_part_means_no_suggestion_ever()
        {
            Assert.Null(Krets(null, 3));
            Assert.Null(Krets("all", 3));
        }

        [Fact]
        public void A_shooter_for_a_club_in_another_district_is_suggested_outside()
        {
            var s = Krets("own", 3);
            Assert.NotNull(s);
            Assert.Equal("region", s!.Kind);
            Assert.Contains("Luleå PK", s.Reason);
            Assert.Contains("Hallands krets", s.Reason);
        }

        [Fact]
        public void A_shooter_for_a_club_in_the_district_is_not_suggested()
        {
            Assert.Null(Krets("own", 2));
        }

        [Fact]
        public void The_club_the_shooter_competes_for_decides_and_the_reason_points_to_the_club_that_would_count()
        {
            // Tävlar för Luleå men är också medlem i Kungsbacka (i kretsen): förslaget GÄLLER (valet avgör — annars kunde en skytt
            // anmäla sig för en klubb utanför kretsen och ändå få kretsens medalj), men skälet pekar ut Kungsbacka.
            var s = Krets("own", 3, 3, 2);
            Assert.NotNull(s);
            Assert.Contains("Kungsbacka PK", s!.Reason);
            Assert.Contains("Tävlar för", s.Reason);
            // Utan någon klubb i kretsen finns inget att peka ut.
            Assert.DoesNotContain("också medlem", Krets("own", 3, 3)!.Reason);
            // Tävlar för Kungsbacka (i kretsen) → inget förslag, även om hen är medlem i Luleå också.
            Assert.Null(Krets("own", 2, 2, 3));
        }

        [Fact]
        public void Qualifies_answers_per_club_for_the_picker()
        {
            var host = HallandHost;
            Assert.True(OutOfCompetitionRules.Qualifies(ChampionshipScopeKind.Krets, "own", host, false, Clubs[2]));
            Assert.False(OutOfCompetitionRules.Qualifies(ChampionshipScopeKind.Krets, "own", host, false, Clubs[3]));
            Assert.Null(OutOfCompetitionRules.Qualifies(ChampionshipScopeKind.Krets, null, host, false, Clubs[3]));   // alla deltar
            Assert.Null(OutOfCompetitionRules.Qualifies(ChampionshipScopeKind.Sm, "own", host, false, Clubs[3]));     // SM: ingen begränsning
        }

        [Fact]
        public void Neighbouring_districts_are_inside_only_with_own_plus_neighbours()
        {
            Assert.NotNull(Krets("own", 4));
            Assert.Null(Krets("own+neighbours", 4));
            Assert.NotNull(Krets("own+neighbours", 3));   // Skåne gränsar inte till Halland i handskriven geografi
        }

        [Fact]
        public void Unknown_club_or_unknown_district_gives_no_suggestion()
        {
            Assert.Null(Krets("own", 0));      // vet inte vem skytten tävlar för
            Assert.Null(Krets("own", 5));      // klubben går inte att lösa upp
            var noRegion = OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Krets, "own",
                new CompetitionOrigin(0, null, null, null), false, 3, new[] { 3 }, Origin);
            Assert.Null(noRegion);
        }

        [Fact]
        public void Landsdel_compares_the_area_not_the_district()
        {
            var host = new CompetitionOrigin(1, "Halland", "Hallands krets", "Vast");
            // Göteborg är samma landsdel (Vast) som Halland → inget förslag; Luleå (Nord) → förslag.
            Assert.Null(OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Landsdel, "own", host, false, 4, new[] { 4 }, Origin));
            var s = OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Landsdel, "own", host, false, 3, new[] { 3 }, Origin);
            Assert.NotNull(s);
            Assert.Equal("area", s!.Kind);
            // Landsdel okänd för tävlingen → inget förslag.
            Assert.Null(OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Landsdel, "own",
                new CompetitionOrigin(1, "Halland", "Hallands krets", null), false, 3, new[] { 3 }, Origin));
        }

        [Fact]
        public void Club_championship_suggests_other_clubs_unless_the_competition_is_club_only()
        {
            var host = new CompetitionOrigin(1, "Halland", "Hallands krets", "Vast");
            Assert.NotNull(OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Klubb, "own", host, false, 2, new[] { 2 }, Origin));
            Assert.Null(OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Klubb, "own", host, false, 1, new[] { 1 }, Origin));
            // Tävlar för Kungsbacka men är också medlem i värdklubben: valet avgör, skälet pekar ut Varberg.
            Assert.Contains("Varbergs PK", OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Klubb, "own", host, false, 2, new[] { 2, 1 }, Origin)!.Reason);
            Assert.Null(OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Klubb, "own", host, true, 2, new[] { 2 }, Origin));
        }

        [Fact]
        public void SM_never_has_a_suggestion()
        {
            Assert.Null(OutOfCompetitionRules.Suggest(ChampionshipScopeKind.Sm, "own", HallandHost, false, 3, new[] { 3 }, Origin));
        }
    }
}
