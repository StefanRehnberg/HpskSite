using HpskSite.CompetitionTypes.Faltskytte.Services;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Förstarten (funktionärspatruller före öppning) ska följa samma luckor som övriga patruller. Bocksten 2026: 24 funktionärer, uppdelningen "Alla blandade" —
    /// förstartspatrullerna får då blandade klasser (längre runda vid A), men deras starttider räknades som start + n × avstånd utan motorns extra luckor, så de krockade vid varje station.
    /// Samma väg som övningssidan: scenario → byggare → PreviewOf.
    /// </summary>
    public class FaltPatrolEnginePreStartTests
    {
        static readonly DateTime Day = new(2028, 6, 10);

        static PatrolScenario Bocksten() => new()
        {
            Seed = 1, C = 107, B = 17, A = 62, R = 47, Date = "2028-06-10", Stations = 8,
            Also = { new ScenarioAlso { From = "C", To = "A", N = 25 }, new ScenarioAlso { From = "C", To = "R", N = 12 }, new ScenarioAlso { From = "C", To = "B", N = 8 }, new ScenarioAlso { From = "A", To = "R", N = 3 } },
            Multi = { new ScenarioMulti { Groups = "CAR", N = 28 }, new ScenarioMulti { Groups = "CBA", N = 5 }, new ScenarioMulti { Groups = "CBR", N = 4 } },
            Jobs = { new ScenarioJob { N = 24, From = "18:00", To = "18:30" } },
        };

        static PatrolPreview Run(string order)
        {
            var b = ScenarioBuilder.Build(Bocksten());
            var settings = new PatrolPlanSettings { PatrolSize = 6, IntervalMinutes = 8, FirstStart = Day.AddHours(9), WeaponGrouping = "Separate", MultiClassGapMinutes = 0, KeepFitting = true };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            var o = new FaltPlanOptions { MinRest = 15, WishRest = 15, Priority = 1, Order = order, UseExistingGroups = false, Ct = cts.Token, RoundMinutes = 84, PauseBlockMin = 8, PreCount = 4, PreStartMin = 420 };
            return PatrolPlanService.PreviewOf(b, settings, o);
        }

        [Theory]
        [InlineData("all")]
        [InlineData("ar")]
        public void Pre_start_patrols_keep_the_same_gaps_as_the_other_patrols_so_nothing_collides(string order)
        {
            var pv = Run(order);
            var m = pv.Plan.Metrics!;
            Assert.Equal(0, m.Collisions);
            var pre = pv.Plan.Patrols.Take(4).ToList();
            Assert.All(pre, p => Assert.All(p.Members, x => Assert.StartsWith("C", x.ShootingClass)));   // förstarten skjuter bara första klassen: ingen blandad, långsammare patrull
            Assert.Equal(new[] { 420d, 428, 436, 444 }, pre.Select(p => (p.StartTime!.Value - Day).TotalMinutes).ToArray());   // exakt avståndet mellan patruller, ingen oförklarlig lucka
            Assert.Equal(4, pv.Plan.Patrols.Take(4).Count(p => p.StartTime < Day.AddHours(9)));
        }
    }
}
