using HpskSite.CompetitionTypes.Faltskytte.Engine;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Krav "samma patrull" (PlanParams.Together) och mjuka önskemål om tidig/sen start (PlanParams.Wish) i motorn.
    /// Kontrollen räknas OBEROENDE ur patrullernas listor, inte ur motorns egna mått, och varje påstående har ett motprov (A/B) som visar att det kan falla.
    /// </summary>
    public class FaltPatrolPairTests
    {
        static PlanParams Params(int iters = 4000) => new()
        {
            Interval = 10, R = 100, N = 8, Size = 6, Vila = 15, VilaW = 15, Open = 480, Close = 1400, PauseBlock = 10, PauseExt = 30, Order = "sep", Iters = iters, Auto = true,
        };

        /// <summary>nC skyttar i C (id 1..nC), de första nDouble också i B.</summary>
        static List<EPatrol> Input(int nC, int nDouble)
        {
            var all = new List<Start>();
            for (var i = 1; i <= nC; i++) all.Add(new Start(i, "C"));
            for (var i = 1; i <= nDouble; i++) all.Add(new Start(i, "B"));
            return new List<EPatrol> { new() { N = 1, S = 0, P = all } };
        }

        static Dictionary<string, int> At(PlanResult g)
        {
            var at = new Dictionary<string, int>();
            for (var i = 0; i < g.Patrols.Count; i++) foreach (var z in g.Patrols[i].P) at[z.Key] = i;
            return at;
        }

        static int Split(PlanResult g, Dictionary<string, string> together)
        {
            var at = At(g);
            return together.Count(kv => at.TryGetValue(kv.Key, out var a) && at.TryGetValue(kv.Value, out var b) && a != b);
        }

        static Dictionary<string, string> RandomPairs(Random rnd, int nC, int count)
        {
            var d = new Dictionary<string, string>();
            for (var i = 0; i < count; i++)
            {
                var a = rnd.Next(1, nC + 1); var b = rnd.Next(1, nC + 1);
                if (a != b && !d.ContainsKey(a + "|C")) d[a + "|C"] = b + "|C";
            }
            return d;
        }

        [Fact]
        public void Pairs_stay_in_the_same_patrol_in_random_scenarios_or_the_plan_says_it_cannot()
        {
            var rnd = new Random(31);
            var splitWithoutRequirement = 0; var feasible = 0;
            for (var seed = 1; seed <= 20; seed++)
            {
                var nC = rnd.Next(14, 46); var doubles = rnd.Next(0, 10);
                var together = RandomPairs(rnd, nC, rnd.Next(1, 7));
                var p = Params(); p.Together = together;
                var g = FaltPatrolEngine.Generate("t" + seed, Input(nC, doubles), p);
                var split = Split(g, together);
                if (!g.Infeasible) feasible++;
                if (!g.Infeasible) Assert.True(split == 0, $"seed {seed}: {split} par ligger i olika patruller fast planen säger att den går ihop");
                else Assert.True(g.Q!.Hard > 0, $"seed {seed}: 'går inte' utan skäl");
                Assert.Equal(split, g.Q!.Link);   // måttet stämmer med det man räknar ur patrullerna
                // Motprov: samma lista utan kravet delar ofta paren, så kontrollen ovan har tänder.
                var free = FaltPatrolEngine.Generate("f" + seed, Input(nC, doubles), Params());
                splitWithoutRequirement += Split(free, together);
            }
            Assert.True(splitWithoutRequirement > 0, "utan kravet delades aldrig ett par: scenarierna prövar ingenting");
            Assert.True(feasible >= 16, $"kravet ska normalt gå att uppfylla: bara {feasible} av 20 scenarier gick ihop");   // faller när klumparna inte placeras ihop
        }

        [Fact]
        public void A_chain_and_a_cycle_become_one_group_in_one_patrol()
        {
            // 1 vill med 2, 2 vill med 3 (kedja), 3 vill med 1 (cykel): alla tre i samma patrull.
            var together = new Dictionary<string, string> { ["1|C"] = "13|C", ["13|C"] = "25|C", ["25|C"] = "1|C" };   // inte intilliggande id:n: annars hamnar de ihop av en slump
            var p = Params(); p.Together = together;
            var g = FaltPatrolEngine.Generate("kedja", Input(30, 0), p);
            var at = At(g);
            Assert.Equal(1, new[] { at["1|C"], at["13|C"], at["25|C"] }.Distinct().Count());
            Assert.Equal(0, g.Q!.Link);
        }

        [Fact]
        public void A_group_larger_than_the_patrol_is_reported_not_silently_split()
        {
            var together = new Dictionary<string, string>();
            for (var i = 1; i < 8; i++) together[i + "|C"] = (i + 1) + "|C";   // åtta i en kedja, patrullen rymmer sex
            var p = Params(); p.Together = together;
            var g = FaltPatrolEngine.Generate("for-stor", Input(30, 0), p);
            Assert.True(g.Infeasible);
            Assert.True(g.Q!.Link > 0);
            Assert.Equal(Split(g, together), g.Q.Link);
        }

        [Fact]
        public void A_pair_where_one_also_shoots_B_still_keeps_the_C_start_together_and_the_rest_between_starts()
        {
            var together = new Dictionary<string, string> { ["2|C"] = "20|C" };   // 2 skjuter också B
            var p = Params(); p.Together = together;
            var g = FaltPatrolEngine.Generate("dubbel", Input(24, 6), p);
            var at = At(g);
            Assert.Equal(at["2|C"], at["20|C"]);
            Assert.Equal(0, g.Q!.Under);   // vilan: ingen under minsta vila
        }

        [Fact]
        public void Without_pairs_or_wishes_the_result_is_identical()
        {
            var plain = FaltPatrolEngine.Generate("a", Input(30, 8), Params());
            var empty = Params(); empty.Together = new(); empty.Wish = new();
            var same = FaltPatrolEngine.Generate("a", Input(30, 8), empty);
            Assert.Equal(plain.Patrols.Select(q => q.S + ":" + string.Join(",", q.P.Select(z => z.Key))), same.Patrols.Select(q => q.S + ":" + string.Join(",", q.P.Select(z => z.Key))));
        }

        [Fact]
        public void A_wish_never_adds_hard_failures_and_early_wishers_start_no_later_on_average()
        {
            var rnd = new Random(7);
            var gain = 0;
            for (var seed = 1; seed <= 12; seed++)
            {
                var nC = rnd.Next(24, 48);
                var early = Enumerable.Range(1, nC).Where(_ => rnd.Next(4) == 0).ToList();
                var wish = early.ToDictionary(i => i + "|C", _ => -1);
                var without = FaltPatrolEngine.Generate("w", Input(nC, 6), Params());
                var p = Params(); p.Wish = wish;
                var with = FaltPatrolEngine.Generate("w", Input(nC, 6), p);
                Assert.True(with.Q!.Hard <= without.Q!.Hard, $"seed {seed}: ett mjukt önskemål gav fler hårda fel ({with.Q.Hard} mot {without.Q.Hard})");
                double Avg(PlanResult g) { var at = At(g); return early.Count == 0 ? 0 : early.Average(i => at[i + "|C"]); }
                Assert.True(Avg(with) <= Avg(without) + 1e-9, $"seed {seed}: de som ville starta tidigt startar senare med önskemålet");
                if (early.Count > 0 && Avg(with) < Avg(without) - 1e-9) gain++;
            }
            Assert.True(gain > 0, "önskemålet förbättrade aldrig något: testet prövar ingenting");
        }

        [Fact]
        public void A_late_wish_does_not_take_a_reserved_extra_slot_and_keeps_the_rest()
        {
            var wish = Enumerable.Range(1, 6).ToDictionary(i => i + "|C", _ => 1);
            var p = Params(); p.Wish = wish; p.ExtraSlots = 2;
            var g = FaltPatrolEngine.Generate("sen", Input(30, 6), p);
            Assert.Equal(0, g.Q!.Under);
            Assert.All(g.Patrols, q => Assert.NotEmpty(q.P));   // inga tomma extrapatruller kvar
        }

        [Fact]
        public void Keep_places_a_late_registered_shooter_in_the_patrol_of_the_one_they_must_join()
        {
            // En befintlig lista med fyra per patrull (plats över). 40 anmäls efteråt och måste gå med 14, som står låst i patrull 4:
            // utan kravet hamnar 40 i första patrullen med plats (patrull 1).
            var together = new Dictionary<string, string> { ["40|C"] = "14|C" };
            var p = Params(); p.Together = together;
            var cur = new List<EPatrol>();
            for (var i = 0; i < 5; i++) cur.Add(new EPatrol { N = i + 1, S = 480 + i * 10, E = 580 + i * 10, P = Enumerable.Range(1 + i * 4, 4).Select(id => new Start(id, "C")).ToList() });
            var k = FaltPatrolEngine.RebuildKeep("keep", cur, p, new[] { new Start(40, "C") });
            Assert.True(k.Kept >= 20, "listan ska behållas, inte byggas om");
            Assert.Equal(0, Split(k.G, together));
            Assert.Equal(At(k.G)["14|C"], At(k.G)["40|C"]);
        }

        [Fact]
        public void Keep_unlocks_a_broken_pair_so_that_both_are_placed_together_again()
        {
            var together = new Dictionary<string, string> { ["3|C"] = "14|C" };
            var p = Params(); p.Together = together;
            var cur = new List<EPatrol>();
            for (var i = 0; i < 5; i++) cur.Add(new EPatrol { N = i + 1, S = 480 + i * 10, E = 580 + i * 10, P = Enumerable.Range(1 + i * 4, 4).Select(id => new Start(id, "C")).ToList() });
            var k = FaltPatrolEngine.RebuildKeep("keep", cur, p);
            Assert.True(k.Unlocked >= 2, "ett brutet par ska låsas upp, inte låsas var för sig");
            Assert.Equal(0, Split(k.G, together));
        }
    }
}
