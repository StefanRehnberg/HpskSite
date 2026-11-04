using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Utanför mästerskapet, S1: "Högsta antal skyttar per patrull" är EN regel på ETT ställe (CapacityOf/LimitOf) och en
    /// VARNING med bekräftelse — aldrig ett hårt stopp.
    /// </summary>
    public class FaltskytteMaxPatrolSizeTests
    {
        [Fact]
        public void The_patrols_own_capacity_wins_over_the_competitions_max()
            => Assert.Equal(4, FaltskyttePatrolWrites.CapacityOf(4, 6, 2));

        [Fact]
        public void The_competitions_max_wins_over_the_old_fallback()
            => Assert.Equal(6, FaltskyttePatrolWrites.CapacityOf(null, 6, 2));

        [Fact]
        public void Without_any_max_the_old_fallback_is_used_so_nothing_changes_for_existing_competitions()
        {
            Assert.Equal(2, FaltskyttePatrolWrites.CapacityOf(null, 0, 2));
            Assert.Equal(6, FaltskyttePatrolWrites.CapacityOf(0, 0, 6));
        }

        [Theory]
        [InlineData(8, 5, 4, 8)]      // angivet vinner
        [InlineData(0, 5, 4, 5)]      // annars tävlingens tak
        [InlineData(0, 0, 4, 4)]      // annars dagens startlista
        [InlineData(0, 0, null, 6)]   // annars 6
        [InlineData(0, 40, 4, 4)]     // ett tak utanför 1-12 (planerarens spann) används inte
        public void The_planner_follows_the_competitions_max_unless_a_size_is_given(int requested, int max, int? inferred, int expected)
            => Assert.Equal(expected, FaltskyttePatrolWrites.PlannerSize(requested, max, inferred));

        [Fact]
        public void No_limit_means_no_warning_ever()
        {
            Assert.Null(FaltskyttePatrolWrites.LimitOf(null, 0));
            Assert.Null(FaltskyttePatrolWrites.OverCapacityMessage(3, 40, null, 1, "Anna"));
        }

        [Fact]
        public void The_patrols_own_limit_or_the_competitions_is_the_limit()
        {
            Assert.Equal(4, FaltskyttePatrolWrites.LimitOf(4, 6));
            Assert.Equal(6, FaltskyttePatrolWrites.LimitOf(null, 6));
        }

        [Fact]
        public void A_patrols_own_capacity_alone_is_not_a_limit_so_zero_means_off_for_already_generated_patrols()
        {
            Assert.Null(FaltskyttePatrolWrites.LimitOf(4, 0));
            Assert.Null(FaltskyttePatrolWrites.OverCapacityMessage(1, 9, FaltskyttePatrolWrites.LimitOf(4, 0), 1, "Anna"));
        }

        [Theory]
        [InlineData(5, 6, 1, false)]   // plats kvar
        [InlineData(6, 6, 1, true)]    // full: en till ger 7 av 6
        [InlineData(7, 6, 1, true)]    // redan över
        [InlineData(4, 6, 3, true)]    // flera på en gång
        [InlineData(4, 6, 2, false)]   // precis fullt är inte över
        [InlineData(6, 6, 0, false)]   // ingen tar plats (flytt inom patrullen / DNS)
        public void Warns_only_when_the_patrol_would_go_over_the_limit(int seated, int limit, int incoming, bool warns)
            => Assert.Equal(warns, FaltskyttePatrolWrites.OverCapacityMessage(3, seated, limit, incoming, "Anna") != null);

        [Fact]
        public void The_question_names_the_patrol_the_numbers_and_the_shooter_and_explains_it_is_a_question()
        {
            var m = FaltskyttePatrolWrites.OverCapacityMessage(3, 6, 6, 1, "Anna Svensson");
            Assert.Equal("Patrull 3 har redan 6 skyttar, och högsta antal per patrull är 6. Vill du lägga till Anna Svensson ändå?", m);
        }

        [Fact]
        public void The_question_says_move_when_moving_and_handles_a_single_shooter()
        {
            var m = FaltskyttePatrolWrites.OverCapacityMessage(1, 1, 1, 1, "Per", "flytta");
            Assert.Equal("Patrull 1 har redan 1 skytt, och högsta antal per patrull är 1. Vill du flytta Per ändå?", m);
        }
    }
}
