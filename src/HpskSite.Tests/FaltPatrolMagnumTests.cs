using HpskSite.CompetitionTypes.Faltskytte.Engine;
using HpskSite.Controllers;
using Xunit;
using Xunit.Abstractions;

namespace HpskSite.Tests
{
    /// <summary>
    /// Magnumfält i patrullmotorn (Magnum-omtaget 2026-11-22). Motorns grupper är C, B, A, R; en magnumskytt (grupp "M") ska ändå få en
    /// riktig plan, med sin egen skjuttid, och en gammal sparad ordning ("CBAR") ska fortsätta gälla.
    /// </summary>
    public class FaltPatrolMagnumTests
    {
        private readonly ITestOutputHelper _out;
        public FaltPatrolMagnumTests(ITestOutputHelper o) => _out = o;

        static PlanParams Params(string order = "sep", string? groupOrder = null, double mSec = 40) => new()
        {
            Interval = 8, R = 84, N = 8, Size = 6, Open = 540, Close = 1380, Vila = 15, VilaW = 15, PauseBlock = 8, PauseExt = 30, NextFirst = true,
            Order = order, GroupOrder = groupOrder, Prio = 1, Improve = false,
            St = Enumerable.Range(0, 8).Select(_ => new StationCfg { Sec = new Dictionary<string, double> { ["C"] = 12, ["B"] = 15, ["A"] = 21, ["R"] = 21, ["M"] = mSec } }).ToList(),
        };

        static List<EPatrol> Starts(params (string g, int n)[] groups)
        {
            var starts = new List<Start>(); var id = 1;
            foreach (var (g, n) in groups) for (var i = 0; i < n; i++) starts.Add(new Start(id++, g));
            return new List<EPatrol> { new() { N = 1, S = 0, P = starts } };
        }

        [Fact]
        public void En_ren_Magnumtavling_far_en_plan_med_alla_skyttar_placerade()
        {
            var g = FaltPatrolEngine.Generate("m", Starts(("M", 14)), Params());
            var placed = g.Patrols.SelectMany(q => q.P).Count();
            _out.WriteLine($"patruller {g.Patrols.Count}, placerade {placed}, ungrouped [{string.Join(",", g.Ungrouped)}], infeasible {g.Infeasible}");
            Assert.Equal(14, placed);
            Assert.True(g.Patrols.Count >= 2);
            Assert.True(g.Patrols.All(q => q.P.Count <= 6));
        }

        [Fact]
        public void Magnumskyttar_blandade_med_vanlig_falt_far_ett_eget_block_sist()
        {
            var g = FaltPatrolEngine.Generate("m", Starts(("C", 12), ("M", 12)), Params());
            var first = g.Patrols.SelectMany(q => q.P.Select(z => (z.G, q.S))).GroupBy(t => t.G).ToDictionary(t => t.Key, t => t.Min(x => x.S));
            _out.WriteLine(string.Join(" ", first.Select(kv => kv.Key + "=" + kv.Value)));
            Assert.Equal(24, g.Patrols.SelectMany(q => q.P).Count());
            Assert.True(first["C"] < first["M"], "magnumblocket startar efter C");
        }

        [Fact]
        public void Magnumskyttar_i_en_grupp_M_far_patruller_av_hogst_sex_utan_att_tappa_nagon()
        {
            // Att M1, M2 och M6 hålls isär eller blandas är ARRANGÖRENS val i patrulldialogen (klasserna väljs en och en, eller flera
            // tillsammans): motorn får då skyttarna med grupp "M" och packar dem. Klasserna ska inte försvinna.
            var g = FaltPatrolEngine.Generate("m", Starts(("M", 21)), Params());
            Assert.Equal(21, g.Patrols.SelectMany(q => q.P).Count());
            Assert.All(g.Patrols, q => Assert.InRange(q.P.Count, 1, 6));
        }

        [Fact]
        public void Stationstiderna_for_Magnum_laser_ur_konfigurationen()
        {
            const string json = "{\"M\":{\"stations\":[{\"station\":1,\"shootingTimeSec\":40},{\"station\":2,\"shootingTimeSec\":55},{\"station\":3,\"shootingTimeSec\":30,\"isShootOffOnly\":true}]}}";
            var st = HpskSite.CompetitionTypes.Faltskytte.Services.FaltPatrolAdapter.StationsFromConfig(json);
            Assert.Equal(2, st.Count);                 // särskjutningsstationen räknas inte
            Assert.Equal(40, st[0].Sec["M"]);
            Assert.Equal(55, st[1].Sec["M"]);
        }

        [Fact]
        public void Magnum_lanar_aldrig_en_annan_grupps_tid_men_vanliga_grupper_lanar_som_forut()
        {
            const string onlyC = "{\"C\":{\"stations\":[{\"station\":1,\"shootingTimeSec\":30}]}}";
            var st = HpskSite.CompetitionTypes.Faltskytte.Services.FaltPatrolAdapter.StationsFromConfig(onlyC);
            Assert.Equal(30, st[0].Sec["B"]);           // oförändrat: B lånar C:s tid när B saknar egen
            Assert.False(st[0].Sec.ContainsKey("M"));   // Magnum får ingen lånad tid: motorn tidtar med 20 s och varnar
        }

        [Fact]
        public void Blandad_konfiguration_ger_varje_grupp_sin_egen_tid()
        {
            const string json = "{\"C\":{\"stations\":[{\"station\":1,\"shootingTimeSec\":14}]},\"M\":{\"stations\":[{\"station\":1,\"shootingTimeSec\":48}]}}";
            var st = HpskSite.CompetitionTypes.Faltskytte.Services.FaltPatrolAdapter.StationsFromConfig(json);
            Assert.Equal(14, st[0].Sec["C"]);
            Assert.Equal(48, st[0].Sec["M"]);
            Assert.Equal(14, st[0].Sec["B"]);           // B lånar fortfarande (any = första egna tiden = C)
        }

        [Fact]
        public void Skjuttiden_for_M_i_konfigurationen_gor_att_gruppen_inte_ar_ogrupperad()
        {
            // Med en skjuttid för M är det inte längre "skjuttid saknas": varningen ska bara visas för grupper utan tid.
            Assert.Empty(FaltPatrolEngine.Generate("m", Starts(("M", 6)), Params(mSec: 40)).Ungrouped);
            var noTime = Params();
            foreach (var st in noTime.St) st.Sec!.Remove("M");
            Assert.Equal(new[] { "M" }, FaltPatrolEngine.Generate("m", Starts(("M", 6)), noTime).Ungrouped);
        }

        [Fact]
        public void En_sparad_ordning_CBAR_gar_fortfarande_att_anvanda()
        {
            Assert.True(FaltPatrolEngine.IsValidGroupOrder("CBAR"));
            Assert.True(FaltPatrolEngine.IsValidGroupOrder("RCBA"));
            Assert.False(FaltPatrolEngine.IsValidGroupOrder("CBA"));
            Assert.False(FaltPatrolEngine.IsValidGroupOrder("CBARX"));
        }
    }
}
