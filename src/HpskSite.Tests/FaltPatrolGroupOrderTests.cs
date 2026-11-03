using HpskSite.CompetitionTypes.Faltskytte.Engine;
using HpskSite.Controllers;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Ordningen mellan vapengrupperna (dropdownen "Ordning mellan vapengrupperna"): motorn sorterar blocken, och en skyttes starter, efter den valda ordningen — inte bara "en grupp först".
    /// </summary>
    public class FaltPatrolGroupOrderTests
    {
        static PlanParams Params(string order, string? groupOrder = null, string? champ = null) => new()
        {
            Interval = 8, R = 84, N = 8, Size = 6, Open = 540, Close = 1380, Vila = 15, VilaW = 15, PauseBlock = 8, PauseExt = 30, NextFirst = true,
            Order = order, GroupOrder = groupOrder, Champ = champ, Prio = 1, Improve = false,
            St = Enumerable.Range(0, 8).Select(_ => new StationCfg { Sec = new Dictionary<string, double> { ["C"] = 12, ["B"] = 15, ["A"] = 21, ["R"] = 21 } }).ToList(),
        };

        /// <summary>12 skyttar per grupp, ingen med två grupper: varje grupps första start visar blockens ordning.</summary>
        static List<EPatrol> OneGroupEach()
        {
            var starts = new List<Start>(); var id = 1;
            foreach (var g in new[] { "C", "B", "A", "R" }) for (var i = 0; i < 12; i++) starts.Add(new Start(id++, g));
            return new List<EPatrol> { new() { N = 1, S = 0, P = starts } };
        }

        static string FirstStartOrder(PlanResult g) =>
            string.Concat(g.Patrols.SelectMany(q => q.P.Select(z => (z.G, q.S))).GroupBy(t => t.G).OrderBy(t => t.Min(x => x.S)).Select(t => t.Key));

        static IEnumerable<string> AllOrders()
        {
            IEnumerable<string> Perm(string rest, string done) { if (rest.Length == 0) yield return done; foreach (var c in rest) foreach (var r in Perm(rest.Replace(c.ToString(), ""), done + c)) yield return r; }
            return Perm("CBAR", "");
        }

        public static IEnumerable<object[]> Orders() => AllOrders().Select(o => new object[] { o });

        [Theory]
        [MemberData(nameof(Orders))]
        public void Every_one_of_the_24_orders_puts_the_groups_in_that_order(string order)
        {
            var g = FaltPatrolEngine.Generate("go", OneGroupEach(), Params("sep", order));
            Assert.Equal(order, FirstStartOrder(g));
        }

        [Fact]
        public void No_order_means_C_B_A_R()
        {
            Assert.Equal("CBAR", FirstStartOrder(FaltPatrolEngine.Generate("go", OneGroupEach(), Params("sep"))));
        }

        [Theory]
        [InlineData("A", "ACBR")]
        [InlineData("R", "RCBA")]
        [InlineData("B", "BCAR")]
        public void The_older_single_group_first_still_works_and_equals_the_matching_order(string champ, string order)
        {
            var a = FaltPatrolEngine.Generate("go", OneGroupEach(), Params("sep", champ: champ));
            var b = FaltPatrolEngine.Generate("go", OneGroupEach(), Params("sep", order));
            Assert.Equal(order, FirstStartOrder(a));
            Assert.Equal(string.Join("|", b.Patrols.Select(q => q.S + ":" + string.Join(",", q.P.Select(z => z.Key)))), string.Join("|", a.Patrols.Select(q => q.S + ":" + string.Join(",", q.P.Select(z => z.Key)))));
        }

        [Fact]
        public void Classes_shot_together_stand_where_the_first_of_them_stands_in_the_order()
        {
            // "A och R tillsammans" med C, A, B, R: C, sedan A och R i samma block, sist B.
            var g = FaltPatrolEngine.Generate("go", OneGroupEach(), Params("ar", "CABR"));
            var first = g.Patrols.SelectMany(q => q.P.Select(z => (z.G, q.S))).GroupBy(t => t.G).ToDictionary(t => t.Key, t => t.Min(x => x.S));
            Assert.True(first["C"] < first["A"] && first["A"] < first["B"] && first["R"] < first["B"], "C, A och R, sist B: " + string.Join(" ", first.Select(kv => kv.Key + "=" + kv.Value)));
        }

        [Fact]
        public void A_shooter_with_two_groups_in_the_same_block_shoots_them_in_the_chosen_order()
        {
            // Alla blandade, ordningen A, C, B, R: skyttar som skjuter både C och A ska skjuta A före C.
            var starts = new List<Start>();
            for (var i = 1; i <= 24; i++) starts.Add(new Start(i, "C"));
            for (var i = 1; i <= 12; i++) starts.Add(new Start(i, "A"));
            var input = new List<EPatrol> { new() { N = 1, S = 0, P = starts } };
            var g = FaltPatrolEngine.Generate("go", input, Params("all", "ACBR"));
            double T(int id, string grp) => g.Patrols.First(q => q.P.Any(z => z.Id == id && z.G == grp)).S;
            Assert.All(Enumerable.Range(1, 12), id => Assert.True(T(id, "A") < T(id, "C"), $"skytt {id}: A {T(id, "A")} ska ligga före C {T(id, "C")}"));
        }

        [Fact]
        public void An_invalid_order_is_refused()
        {
            Assert.NotNull(FaltPatrolEngine.Generate("go", OneGroupEach(), Params("sep", "CCBA")).Invalid);
            Assert.NotNull(FaltPatrolEngine.Generate("go", OneGroupEach(), Params("sep", "CBA")).Invalid);
        }

        [Theory]
        [InlineData("CABR", "CABR")]
        [InlineData("cabr", "CABR")]
        [InlineData("A", "ACBR")]
        [InlineData("r", "RCBA")]
        [InlineData("CCBA", null)]
        [InlineData("CBA", null)]
        [InlineData("", null)]
        [InlineData(null, null)]
        public void The_controller_accepts_a_full_order_or_the_older_single_letter(string? input, string? expected)
        {
            Assert.Equal(expected, StaffingController.GroupOrderOf(input));
        }
    }
}
