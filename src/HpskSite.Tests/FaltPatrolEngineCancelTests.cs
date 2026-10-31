using HpskSite.CompetitionTypes.Faltskytte.Engine;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Motorn ska kunna avbrytas utifrån (tidsgräns, avbruten förfrågan) och då kasta, aldrig ge en delvis lista.</summary>
    public class FaltPatrolEngineCancelTests
    {
        static List<EPatrol> Big(int n)
        {
            var ps = new List<EPatrol>(); var id = 1;
            for (var k = 0; k < n / 6; k++)
            {
                var q = new EPatrol { N = k + 1, S = 480 + k * 10, E = 600 + k * 10 };
                for (var i = 0; i < 6; i++) { q.P.Add(new Start(id, "C")); q.P.Add(new Start(id, "B")); id++; if (q.P.Count >= 6) break; }
                ps.Add(q);
            }
            return ps;
        }

        static PlanParams Params() { var p = new PlanParams { N = 10, R = 100, Size = 6, Open = 480, Close = 1400 }; for (var i = 0; i < 10; i++) p.St.Add(new StationCfg()); return p; }

        [Fact]
        public void A_cancelled_token_throws_instead_of_returning_a_partial_list()
        {
            var p = Params(); p.Ct = new CancellationToken(true);
            Assert.Throws<OperationCanceledException>(() => FaltPatrolEngine.Generate("x", Big(300), p));
        }

        [Fact]
        public void Without_cancellation_the_same_input_gives_a_list()
        {
            var g = FaltPatrolEngine.Generate("x", Big(300), Params());
            Assert.NotEmpty(g.Patrols);
        }
    }
}
