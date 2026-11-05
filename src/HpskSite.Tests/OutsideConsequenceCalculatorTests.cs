using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.CompetitionTypes.Precision.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// S6 Rätta-flödet: konsekvensraderna när en skytt flyttas utanför mästerskapet i en publicerad lista. Handräknade förväntningar —
    /// sex skyttar i C2, de flyttade står på plats 2 (silver) respektive 5.
    /// </summary>
    public class OutsideConsequenceCalculatorTests
    {
        private static PrecisionShooterResult S(int id, string name, int? placement) =>
            new() { MemberId = id, Name = name, ShootingClass = "C2", Placement = placement };

        private static PrecisionFinalResults List(params PrecisionShooterResult[] shooters) => new()
        {
            ClassGroups = new List<PrecisionClassGroup>
            {
                new() { ClassName = "C2", DisplayClassName = "C2", Shooters = shooters.ToList() }
            }
        };

        private static PrecisionMedalCategoryAwards Cat(int participants, params (string medal, int id, string name)[] awards)
        {
            var (count, text) = HpskSite.CompetitionTypes.Common.ChampionshipMedalCount.For(participants, false);
            return new PrecisionMedalCategoryAwards
            {
                CategoryName = "C", WeaponGroup = "C", Participants = participants, MedalCount = count, MedalCountText = text,
                Awards = awards.Select((a, i) => new PrecisionMedalAward { Place = i + 1, Medal = a.medal, MemberId = a.id, Name = a.name }).ToList()
            };
        }

        [Fact]
        public void The_moved_shooter_loses_the_place_and_those_behind_move_up()
        {
            var before = List(S(1, "Ann", 1), S(2, "Bo", 2), S(3, "Cia", 3), S(4, "Dan", 4), S(5, "Eva", 5), S(6, "Fia", 6));
            var after = List(S(1, "Ann", 1), S(2, "Bo", 2), S(3, "Cia", 3), S(4, "Dan", 4), S(5, "Eva", null), S(6, "Fia", 5));

            var c = OutsideConsequenceCalculator.Compute(before, after, 5, "C2");

            Assert.Equal(new[]
            {
                "Eva får ingen placering i C2 (har plats 5 nu).",
                "Fia går från plats 6 till plats 5 i C2."
            }, c.Lines);
            Assert.Equal(2, c.PlacementChanges);
            Assert.Equal(0, c.MedalChanges);
        }

        [Fact]
        public void A_medal_is_lost_and_won_and_the_reduction_is_reported()
        {
            // Före: 6 tävlande (guld, silver, brons). Efter att Bo (silver) flyttats: 5 tävlande — Cia tar silver, Dan brons.
            var before = List(S(1, "Ann", 1), S(2, "Bo", 2), S(3, "Cia", 3), S(4, "Dan", 4));
            before.MedalAwards.Add(Cat(6, ("Guld", 1, "Ann"), ("Silver", 2, "Bo"), ("Brons", 3, "Cia")));
            var after = List(S(1, "Ann", 1), S(2, "Bo", null), S(3, "Cia", 2), S(4, "Dan", 3));
            after.MedalAwards.Add(Cat(5, ("Guld", 1, "Ann"), ("Silver", 3, "Cia"), ("Brons", 4, "Dan")));

            var c = OutsideConsequenceCalculator.Compute(before, after, 2, "C2");

            Assert.Contains("Bo förlorar mästerskapssilver i C.", c.Lines);
            Assert.Contains("Cia går från mästerskapsbrons till mästerskapssilver i C.", c.Lines);
            Assert.Contains("Dan får mästerskapsbrons i C (har ingen medalj nu).", c.Lines);
            Assert.DoesNotContain(c.Lines, l => l.StartsWith("Ann"));          // guldet är oförändrat
            Assert.Equal(3, c.MedalChanges);
        }

        [Fact]
        public void Fewer_participants_can_reduce_the_medal_count_even_for_shooters_who_are_not_moved()
        {
            // 5 tävlande → 3 medaljer; 4 → "Guld + Silver" (SHB C.3.4.1): bara medaljantalet ändras, inte någons medalj.
            var before = List(S(1, "Ann", 1), S(2, "Bo", 2), S(3, "Cia", 3), S(4, "Dan", 4), S(5, "Eva", 5));
            before.MedalAwards.Add(Cat(5, ("Guld", 1, "Ann"), ("Silver", 2, "Bo"), ("Brons", 3, "Cia")));
            var after = List(S(1, "Ann", 1), S(2, "Bo", 2), S(3, "Cia", 3), S(4, "Dan", 4), S(5, "Eva", null));
            after.MedalAwards.Add(Cat(4, ("Guld", 1, "Ann"), ("Silver", 2, "Bo")));

            var c = OutsideConsequenceCalculator.Compute(before, after, 5, "C2");

            Assert.Contains(c.Lines, l => l.StartsWith("Cia förlorar mästerskapsbrons"));
            Assert.Contains(c.Lines, l => l.StartsWith("Medaljantalet i C ändras från 3") && l.Contains("tävlande 5 → 4"));
        }

        [Fact]
        public void The_change_log_action_fits_the_column()
        {
            // CompetitionChange.Action är nvarchar(20); CompetitionChangeRecorder.Record sväljer ett för långt värde tyst
            // (fångades först vid körning mot rehearsal: raden med anledningen saknades).
            Assert.InRange(FaltskytteOutsideCorrectionService.ChangeAction.Length, 1, 20);
        }

        [Fact]
        public void Nothing_changes_when_the_shooter_is_last_and_has_no_medal_effect()
        {
            var before = List(S(1, "Ann", 1), S(2, "Bo", 2));
            var after = List(S(1, "Ann", 1), S(2, "Bo", 2));
            var c = OutsideConsequenceCalculator.Compute(before, after, 2, "C2");
            Assert.Empty(c.Lines);
        }
    }
}
