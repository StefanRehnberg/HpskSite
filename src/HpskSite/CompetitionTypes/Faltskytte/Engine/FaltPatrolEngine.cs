// Patrullgeneratorn för fält, som en REN funktion (steg 3 i planen 2026-10-30): ingen Umbraco, NPoco, scope, databas eller klocka.
// En trogen port av PURE-blocket i notes/patrullgenerator-prototyp/template.html (motorn före C#). Samma slumptal (mulberry32), samma evalueringsordning och samma
// Math.round (halv uppåt) — så golden master-provet (FaltPatrolEngineGoldenTests) kan kräva IDENTISK lista, inte bara närliggande mått.
// Tid = minuter från dagens midnatt som double (dag 2 = +1440). Ändra aldrig motorn utan att köra golden master och prototypens sviter.
using System.Globalization;

namespace HpskSite.CompetitionTypes.Faltskytte.Engine
{
    /// <summary>En start: skyttens id och klass/vapengrupp (C, B, A, R, A_Opt, M…).</summary>
    public readonly record struct Start(int Id, string G)
    {
        public string Key => Id.ToString(CultureInfo.InvariantCulture) + "|" + G;
    }

    /// <summary>En patrull i planen. <c>S</c> start, <c>E</c> slut (bara indata från en befintlig lista använder den), <c>Pre</c> = förstartspatrull.</summary>
    public sealed class EPatrol
    {
        public int N;
        public double S, E;
        public List<Start> P = new();
        public bool Fixed, Pre;
        public EPatrol Copy() => new() { N = N, S = S, E = E, P = new List<Start>(P), Fixed = Fixed, Pre = Pre };
    }

    public sealed class StationCfg { public double? T; public Dictionary<string, double>? Sec; }

    public sealed class PlanParams
    {
        public double Interval = 10, R = 100, Vila = 15, VilaW = 30, Open = 480, PreStart, Close = 1380, Open2 = 540, Close2 = 900, PauseBlock = 10, PauseExt = 30, RoundExtra = 60, JobBuf = 30, GlideMin;
        public int N = 10, Size = 6, DayBreak = 1, Prio = 1, Pre, GlideEvery;
        public bool NextFirst, Day2, Smart = true, ArTogether = true, Auto = true, KeepMode, Improve = true, Published, HasResults;
        public string Order = "auto"; public string KeepPolicy = "full";
        /// <summary>Ordningen mellan vapengrupperna, alla fyra en gång, t.ex. "CBAR", "CABR", "RCBA". Null = "CBAR". Se <see cref="FaltPatrolEngine.OrderOf"/>.</summary>
        public string? GroupOrder;
        /// <summary>Äldre form: en grupp som skjuter först, övriga i grundordningen. Används bara när <see cref="GroupOrder"/> saknas.</summary>
        public string? Champ;
        public Dictionary<string, double> Fill = new() { ["C"] = 12, ["B"] = 15, ["A"] = 21, ["R"] = 21 };
        public List<StationCfg> St = new();
        public Dictionary<string, double>? GroupSize, GroupInterval, GroupStart, Locked, Dns;
        public HashSet<int>? Reserves, Early, Late, PatrolIds, Waived;
        public Dictionary<int, List<(double A, double B)>>? Jobs;
        public int? Iters, Stall, ImproveSeed;
        /// <summary>Extra platser sist i varje block (tomma tas bort efteråt): ger skyttar med flera starter i ett blandat block något att vänta på. 0 = som prototypen.</summary>
        public int ExtraSlots;
        /// <summary>
        /// Hårda krav "samma patrull": startnyckel (id|grupp) → partnerns startnyckel. Kedjor och cykler blir EN klump (union-find) inom samma vapengrupp. En klump placeras som en enhet
        /// (kapacitet = antal medlemmar, vila och uppdrag för alla); går det inte räknas brutna par i <see cref="Qual.Link"/> (ingår i Hard) i stället för att tyst splittras.
        /// Klumpmedlemmar flyttas aldrig av förbättringen. Tomt/null = som förut (golden-fallen oförändrade).
        /// </summary>
        public Dictionary<string, string>? Together;
        /// <summary>
        /// Mjuka önskemål per start (id|grupp): -1 tidig start, +1 sen start. Gäller bara personens första (tidig) respektive sista (sen) start. Ger aldrig förtur som funktionärernas Early/Late (Pri)
        /// och påverkar aldrig Hard eller vilokraven; bara ordningen inom en klass av lika svåra starter.
        /// </summary>
        public Dictionary<string, int>? Wish;
        /// <summary>Avbrott utifran (tidsgrans, avbruten forfragan). Motorn kastar OperationCanceledException; aldrig en delvis lista.</summary>
        public CancellationToken Ct;
        public PlanParams Clone() => (PlanParams)MemberwiseClone();
    }

    public sealed class RunPatrol
    {
        public int N; public double S0, S, End, Dur, ETot, Need, Gap, Short; public List<Start> People = new(); public List<string> Gr = new();
        public double[] Occ = Array.Empty<double>(), A = Array.Empty<double>(); public int I;
    }
    public sealed class RunPair { public (int I, string G) A, B; public double Slack, Idle; public bool Cross; }
    public sealed class RunPerson { public int Id; public List<(int I, string G)> Starts = new(); public List<RunPair> Pairs = new(); public double MinSlack, Wait; }
    public sealed class RunResult
    {
        public List<RunPatrol> Ps = new(); public List<RunPerson> Persons = new(); public double Total; public List<RunPerson> Under = new(), UnderWish = new();
        public int Collisions; public double LastStart, LastEnd; public int NeedAdd, Mixed, Starts;
    }

    public sealed class Qual
    {
        public RunResult? R; public int Hard, Under, Coll, StaffBad, Cap, Link; public double Over, Soft, D1, D2, E1, E2;
    }
    public sealed record DnsOut(int Id, string G, double T);
    public sealed class PauseOut { public int At; public double Min, Ext, Set; public int Hard; public int? Before; }
    public sealed class ImproveStats { public int Tried, Accepted; public double MinGain = double.PositiveInfinity; }

    public sealed class PlanResult
    {
        public string Id = ""; public List<EPatrol> Patrols = new(); public List<DnsOut> Dns = new(); public List<PauseOut> Pauses = new();
        public List<string> Ungrouped = new(); public List<int> Waived = new(); public string? Chosen; public Qual? Q; public bool Infeasible;
        public string? Blocked, Empty, Warning; public List<string>? Invalid; public ImproveStats Stats = new(); public int Pre;
    }

    public sealed class KeepResult { public PlanResult G = new(); public int Kept, Moved, Unlocked, Added, Removed; public string? Blocked; }

    public static class FaltPatrolEngine
    {
        public static readonly string[] Groups = { "C", "B", "A", "R" };
        public const double Day2Offset = 1440;
        public const int MaxStarts = 1500;   // SM 2024 hade ca 1 200 starter över fyra dagar och en dag har som mest ca 400; planeringen görs en dag i taget
        static readonly double[] SoftWish = { 10, 30, 100 };
        static readonly double[] SoftEnd = { 3, 0.5, 0.1 };

        static readonly Dictionary<string, string[][]> Orders = new()
        {
            ["sep"] = new[] { new[] { "C" }, new[] { "B" }, new[] { "A" }, new[] { "R" } },
            ["ar"] = new[] { new[] { "C" }, new[] { "B" }, new[] { "A", "R" } },
            ["cbar"] = new[] { new[] { "C", "B" }, new[] { "A", "R" } },
            ["all"] = new[] { new[] { "C", "B", "A", "R" } },
        };
        static readonly string[] OrderKeys = { "sep", "ar", "cbar", "all" };

        static string Tg(string g) => g == "A_Opt" ? "A" : g;
        static double Round(double x) => Math.Floor(x + 0.5);
        static int GroupIdx(string g) => Array.IndexOf(Groups, g);

        /// <summary>Är texten en giltig ordning: C, B, A och R, var och en exakt en gång (versaler)?</summary>
        public static bool IsValidGroupOrder(string? s) => s != null && s.Length == Groups.Length && Groups.All(g => s.Count(c => c.ToString() == g) == 1);

        /// <summary>
        /// Ordningen mellan vapengrupperna som listan följer: <see cref="PlanParams.GroupOrder"/> om den är giltig, annars den äldre <see cref="PlanParams.Champ"/> (den gruppen först, övriga i grundordningen C, B, A, R),
        /// annars grundordningen. Block, och skyttar med flera starter i samma block, sorteras efter den.
        /// </summary>
        public static string[] OrderOf(PlanParams p)
        {
            if (IsValidGroupOrder(p.GroupOrder)) return p.GroupOrder!.Select(c => c.ToString()).ToArray();
            if (p.Champ != null && Groups.Contains(p.Champ)) return new[] { p.Champ }.Concat(Groups.Where(g => g != p.Champ)).ToArray();
            return Groups;
        }

        // ------------------------------------------------------------ mått

        static double ExtraMin(List<string> grs, Dictionary<string, double> sec, PlanParams p)
        {
            var rounds = new Dictionary<string, double>();
            foreach (var g in grs)
            {
                var k = (p.ArTogether && (g == "A" || g == "R")) ? "AR" : g;
                var v = sec.TryGetValue(g, out var s) && s != 0 ? s : 20;
                rounds[k] = rounds.TryGetValue(k, out var old) ? Math.Max(old, v) : v;
            }
            if (rounds.Count < 2) return 0;
            double sum = 0, max = double.NegativeInfinity;
            foreach (var v in rounds.Values) { sum += v; max = Math.Max(max, v); }
            return ((sum - max) + (rounds.Count - 1) * p.RoundExtra) / 60;
        }

        static Dictionary<string, double> SecOf(PlanParams p, int s) => s < p.St.Count && p.St[s].Sec != null ? p.St[s].Sec! : p.Fill;
        static double TS(PlanParams p, int s) { var v = s < p.St.Count ? p.St[s].T : null; return v > 0 ? v.Value : p.Interval; }

        public static RunResult Run(IReadOnlyList<EPatrol> patrols, PlanParams p)
        {
            var N = p.N;
            double sumT = 0; for (var s = 0; s < N; s++) sumT += TS(p, s);
            var w = Math.Max(0, p.R / N - sumT / N);
            var ps = patrols.Select((x, i) => (x, i)).OrderBy(t => t.x.S).ThenBy(t => t.i).Select(t => new RunPatrol { N = t.x.N, S0 = t.x.S, People = t.x.P }).ToList();
            foreach (var q in ps)
            {
                q.Gr = Groups.Where(g => q.People.Any(z => Tg(z.G) == g)).ToList();
                q.Occ = new double[N]; q.A = new double[N];
                double a = 0, eTot = 0;
                for (var s = 0; s < N; s++)
                {
                    var e = ExtraMin(q.Gr, SecOf(p, s), p);
                    eTot += e; q.Occ[s] = TS(p, s) + e; q.A[s] = a; a += TS(p, s) + e + w;
                }
                q.Dur = a; q.ETot = eTot;
            }
            for (var i = 0; i < ps.Count; i++)
            {
                var q = ps[i]; var nx = i + 1 < ps.Count ? ps[i + 1] : null; double nd = 0;
                for (var s = 0; s < N; s++) nd = Math.Max(nd, q.Occ[s] + q.A[s] - (nx != null ? nx.A[s] : q.A[s]));
                q.Need = nd;
            }
            for (var i = 0; i < ps.Count; i++) { ps[i].S = ps[i].S0; }
            for (var i = 0; i < ps.Count; i++)
            {
                var q = ps[i]; q.End = q.S + q.Dur; var nx = i + 1 < ps.Count ? ps[i + 1] : null;
                q.Gap = nx != null ? nx.S - q.S : 0;
                q.Short = nx != null && q.Gap < q.Need - 0.01 ? q.Need - q.Gap : 0;
                q.I = i;
            }
            var collisions = 0;
            for (var s = 0; s < N; s++)
            {
                var arr = ps.Select((q, i) => (q, a: q.S + q.A[s], o: q.Occ[s], i)).OrderBy(t => t.a).ThenBy(t => t.i).ToList();
                for (var k = 1; k < arr.Count; k++)
                    if (arr[k].a < arr[k - 1].a + arr[k - 1].o - 0.01) collisions++;
            }
            var by = new Dictionary<int, List<(int I, string G)>>(); var order = new List<int>();
            for (var i = 0; i < ps.Count; i++)
                foreach (var z in ps[i].People)
                {
                    if (!by.TryGetValue(z.Id, out var v)) { by[z.Id] = v = new(); order.Add(z.Id); }
                    if (!v.Any(t => t.I == i && t.G == z.G)) v.Add((i, z.G));
                }
            var persons = new List<RunPerson>();
            foreach (var id in order)
            {
                var v = by[id]; if (v.Count < 2) continue;
                v = v.Select((t, ix) => (t, ix)).OrderBy(t => ps[t.t.I].S).ThenBy(t => t.t.I).ThenBy(t => t.ix).Select(t => t.t).ToList();
                var per = new RunPerson { Id = id, Starts = v };
                for (var j = 1; j < v.Count; j++)
                {
                    var a = v[j - 1]; var b = v[j];
                    var idle = ps[b.I].S - ps[a.I].End;
                    per.Pairs.Add(new RunPair { A = a, B = b, Idle = idle, Slack = ps[b.I].S - (ps[a.I].End + p.Vila), Cross = idle > 600 });
                }
                per.MinSlack = per.Pairs.Min(z => z.Slack);
                double wait = 0; foreach (var z in per.Pairs) wait += z.Cross ? 0 : Math.Max(0, z.Slack);
                per.Wait = wait; persons.Add(per);
            }
            double total = 0; foreach (var z in persons) total += z.Wait;
            return new RunResult
            {
                Ps = ps, Persons = persons, Total = total, Collisions = collisions,
                Under = persons.Where(z => z.MinSlack < -0.01).ToList(),
                UnderWish = persons.Where(z => z.MinSlack + p.Vila < p.VilaW - 0.01).ToList(),
                Starts = ps.Sum(q => q.People.Count),
                LastStart = ps.Count > 0 ? ps[^1].S : 0, LastEnd = ps.Count > 0 ? ps.Max(q => q.End) : 0,
                NeedAdd = ps.Count(q => q.Short > 0.01), Mixed = ps.Count(q => q.ETot > 0.001),
            };
        }

        // ------------------------------------------------------------ funktionärer (uppdrag)

        static Dictionary<int, List<(double A, double B)>> MakeStaff(PlanParams p)
        {
            var m = new Dictionary<int, List<(double, double)>>();
            if (p.Jobs != null) foreach (var kv in p.Jobs) if (kv.Value.Count > 0) m[kv.Key] = kv.Value;
            if (p.Waived != null) foreach (var id in p.Waived) m.Remove(id);
            return m;
        }

        static int StaffCheck(RunResult r, PlanParams p, Dictionary<int, List<(double A, double B)>> staff)
        {
            var bad = 0;
            foreach (var kv in staff)
                if (r.Ps.Any(q => q.People.Any(z => z.Id == kv.Key) && kv.Value.Any(j => q.End > j.A - p.JobBuf && q.S < j.B + p.JobBuf))) bad++;
            return bad;
        }

        // ------------------------------------------------------------ kvalitet

        static List<string[]> BlocksFor(IReadOnlyList<EPatrol> patrols, PlanParams p, string order)
        {
            var blocks = Orders[order].Select(b => b.ToArray()).ToList();
            var present = new List<string>();
            foreach (var q in patrols) foreach (var z in q.P) { var g = Tg(z.G); if (!present.Contains(g)) present.Add(g); }
            var known = blocks.SelectMany(b => b).ToHashSet();
            var unk = present.Where(g => !known.Contains(g)).ToArray();
            if (unk.Length > 0) blocks.Add(unk);
            // Blocken följer den valda ordningen mellan vapengrupperna: ett block står där dess först kommande grupp står, och inom ett block kommer grupperna i samma ordning.
            // ("C, B, A och R tillsammans" med ordningen C, A, B, R ger alltså C, sedan A och R tillsammans, sedan B.) Okända grupper ligger sist.
            var ord = OrderOf(p);
            int Rk(string g) { var i = Array.IndexOf(ord, g); return i < 0 ? 99 : i; }
            blocks = blocks.Select((b, i) => (b: b.OrderBy(Rk).ToArray(), i)).OrderBy(t => t.b.Min(Rk)).ThenBy(t => t.i).Select(t => t.b).ToList();
            return blocks.Where(b => b.Any(present.Contains)).ToList();
        }

        static double PatrolCap(EPatrol q, PlanParams p)
        {
            var v = q.P.Select(z => p.GroupSize != null && p.GroupSize.TryGetValue(Tg(z.G), out var x) ? x : 0).Where(x => x > 0).ToList();
            return v.Count > 0 ? v.Min() : p.Size;
        }
        static int OverCap(List<EPatrol> patrols, PlanParams p) => patrols.Sum(q => (int)Math.Max(0, q.P.Count - PatrolCap(q, p)));

        static double RestSoft(IEnumerable<(double Slack, bool Cross)> sl, PlanParams p)
        {
            var wish = false; double wait = 0;
            foreach (var z in sl) { if (z.Slack + p.Vila < p.VilaW - 0.01) wish = true; if (!z.Cross) wait += Math.Max(0, z.Slack); }
            return (wish ? SoftWish[p.Prio] : 0) + wait;
        }

        static Qual QualOf(List<EPatrol> patrols, PlanParams p)
        {
            var cap = OverCap(patrols, p);
            var r = Run(patrols, p);
            double e1 = 0, e2 = 0;
            foreach (var q in r.Ps) { if (q.S < Day2Offset) e1 = Math.Max(e1, q.End); else e2 = Math.Max(e2, q.End); }
            var d1 = Math.Max(0, e1 - p.Close); var d2 = e2 != 0 ? Math.Max(0, e2 - (Day2Offset + p.Close2)) : 0;
            var sb = p.Jobs != null ? StaffCheck(r, p, MakeStaff(p)) : 0;
            double soft = 0; foreach (var z in r.Persons) soft += RestSoft(z.Pairs.Select(y => (y.Slack, y.Cross)), p);
            soft += (r.LastStart - p.Open) * SoftEnd[p.Prio];
            var over = d1 + d2;
            var link = LinkBroken(patrols, p);
            return new Qual { R = r, Hard = r.Under.Count + r.Collisions + sb + cap + link + (over > 0.5 ? 1 : 0), Under = r.Under.Count, Coll = r.Collisions, StaffBad = sb, Cap = cap, Link = link, Over = over, Soft = soft, D1 = d1, D2 = d2, E1 = e1, E2 = e2 };
        }

        /// <summary>Antal krav "samma patrull" där båda finns i planen men ligger i olika patruller. Ett krav vars ena part inte är med (DNS, reserv) räknas inte.</summary>
        static int LinkBroken(List<EPatrol> patrols, PlanParams p)
        {
            if (p.Together == null || p.Together.Count == 0) return 0;
            var at = new Dictionary<string, int>();
            for (var i = 0; i < patrols.Count; i++) foreach (var z in patrols[i].P) at[z.Key] = i;
            var bad = 0;
            foreach (var kv in p.Together) if (at.TryGetValue(kv.Key, out var a) && at.TryGetValue(kv.Value, out var b) && a != b) bad++;
            return bad;
        }

        /// <summary>Union-find över kraven "samma patrull": nyckel → klump-id för varje komponent med minst två startar i samma vapengrupp.</summary>
        static Dictionary<string, int> BuildClumps(PlanParams p, HashSet<string> present)
        {
            var result = new Dictionary<string, int>();
            if (p.Together == null || p.Together.Count == 0) return result;
            var parent = new Dictionary<string, string>();
            string Find(string x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
            foreach (var kv in p.Together)
            {
                if (!present.Contains(kv.Key) || !present.Contains(kv.Value) || kv.Key == kv.Value) continue;
                if (Tg(kv.Key[(kv.Key.IndexOf('|') + 1)..]) != Tg(kv.Value[(kv.Value.IndexOf('|') + 1)..])) continue;   // bara inom samma vapengrupp
                parent.TryAdd(kv.Key, kv.Key); parent.TryAdd(kv.Value, kv.Value);
                var ra = Find(kv.Key); var rb = Find(kv.Value); if (ra != rb) parent[ra] = rb;
            }
            var ids = new Dictionary<string, int>();
            foreach (var k in parent.Keys.OrderBy(x => x, StringComparer.Ordinal)) { var root = Find(k); if (!ids.TryGetValue(root, out var id)) ids[root] = id = ids.Count; result[k] = id; }
            return result;
        }

        static bool Better(Qual a, Qual b) => a.Hard != b.Hard ? a.Hard < b.Hard : Math.Abs(a.Over - b.Over) > 0.5 ? a.Over < b.Over : a.Soft < b.Soft;

        // ------------------------------------------------------------ indata

        public static List<string> Validate(IReadOnlyList<EPatrol> patrols, PlanParams p)
        {
            var e = new List<string>();
            void Num(double v, double lo, double hi, string name) { if (double.IsNaN(v) || double.IsInfinity(v) || v < lo || v > hi) e.Add(name + " måste vara " + lo + "–" + hi); }
            Num(p.Size, 1, 12, "Patrullstorlek"); Num(p.Interval, 1, 120, "Avstånd mellan patruller"); Num(p.Vila, 0, 600, "Minsta vila"); Num(p.VilaW, 0, 600, "Önskad vila");
            if (p.VilaW < p.Vila) e.Add("Önskad vila får inte vara kortare än minsta vila");
            Num(p.N, 1, 40, "Antal stationer"); Num(p.R, 1, 600, "Rundtid"); Num(p.PauseExt, 0, 600, "Förlängd paus"); Num(p.PauseBlock, 0, 600, "Paus mellan block");
            Num(p.RoundExtra, 0, 600, "Omgångstillägg"); Num(p.GlideEvery, 0, 100, "Glid: var n:e patrull"); Num(p.GlideMin, 0, 60, "Glid: minuter");
            Num(p.Open, 0, 1439, "Första start"); Num(p.Close, 1, 2880, "Slut");
            if (p.GroupOrder != null && !IsValidGroupOrder(p.GroupOrder)) e.Add("Ordningen mellan vapengrupperna måste ha C, B, A och R en gång var");
            if (p.Close <= p.Open) e.Add("Dagen måste sluta efter att den börjar");
            var n = patrols.Sum(q => q.P.Count);
            if (n > MaxStarts) e.Add("För många starter (" + n + ", högst " + MaxStarts + ")");
            return e;
        }

        static PlanResult Blocked(IReadOnlyList<EPatrol> patrols, string id, PlanParams p) =>
            new() { Id = id + "-gen", Patrols = patrols.Select(q => q.Copy()).ToList(), Blocked = p.HasResults ? "resultat" : "publicerad" };

        // ------------------------------------------------------------ generate

        /// <summary>Beräknar en startlista. Spärrad lista (publicerad) kommer tillbaka orörd med <c>Blocked</c>; ogiltig indata ger <c>Invalid</c>; inga starter ger <c>Empty</c>.</summary>
        public static PlanResult Generate(string id, IReadOnlyList<EPatrol> patrols, PlanParams p)
        {
            var bad = Validate(patrols, p);
            if (bad.Count > 0) return new PlanResult { Id = id + "-gen", Invalid = bad, Infeasible = true };
            if (p.Published) return Blocked(patrols, id, p);
            {
                var lk = p.Locked; var dk = p.Dns; var rk = p.Reserves; var dnsOut = new List<DnsOut>(); var act = 0; var seen = new HashSet<string>();
                foreach (var q in patrols) foreach (var z in q.P)
                    {
                        var k = z.Key; if (!seen.Add(k)) continue;
                        if (dk != null && dk.TryGetValue(k, out var t)) dnsOut.Add(new DnsOut(z.Id, z.G, t));
                        else if ((lk != null && lk.ContainsKey(k)) || rk == null || !rk.Contains(z.Id)) act++;
                    }
                if (act == 0) return new PlanResult { Id = id + "-gen", Dns = dnsOut, Empty = seen.Count > 0 ? "alla" : "tom", Q = new Qual() };
            }
            var g = p.Order == "auto" ? GenerateAuto(id, patrols, p) : Generate0(id, patrols, p);
            g.Q ??= QualOf(g.Patrols, p);
            g.Infeasible = g.Q.Hard > 0;
            if (p.HasResults) g.Warning = "resultat";
            return g;
        }

        sealed class Ent
        {
            public int Id; public string G = ""; public double? Fixed; public List<(double A, double B)>? Staff; public bool Pat, Early, Late, Pri, HasLater;
            public int Bi, NLater, NextOrd, Rank; public Ent? Prev; public double Tm, Dm;
            /// <summary>Klump = skyttar som ska stå i samma patrull (Together), -1 = ingen. ERank = rank efter att klumpmedlemmar delar rank och ingen ligger före sin egen tidigare start. WishRank: -1 tidig, 1 sen (mjukt önskemål), 0 inget.</summary>
            public int Clump = -1, ERank, WishRank;
        }
        sealed class Meta
        {
            public string[] Grs = Array.Empty<string>(); public List<Ent> List = new(); public int M, Mg; public double D; public List<(double T, double Vac)> Xt = new();
            public int GridFree, Sz, Spare; public double Iv;
        }
        sealed class TimesArr
        {
            public List<double> Arr = new(); public int G; public Dictionary<int, int> MergedOf = new(); public List<int> Rest = new();
        }
        sealed class TryRes { public TimesArr Times = new(); public Dictionary<Ent, int> Slot = new(); public int WishFail, Hard; public double Delta; }

        static PlanResult Generate0(string id, IReadOnlyList<EPatrol> patrols, PlanParams p)
        {
            var blocks = BlocksFor(patrols, p, p.Order);
            var pauseMax = p.PauseExt;
            var useDay2 = p.Day2 && p.DayBreak > 0 && p.DayBreak < blocks.Count;
            var d0 = Math.Max(0, p.PauseBlock - p.Interval);
            var N = p.N;
            double sumT = 0; for (var s = 0; s < N; s++) sumT += TS(p, s);
            var D0 = Math.Max(p.R, sumT);
            var entMap = new Dictionary<string, Ent>(); var entList = new List<Ent>();
            foreach (var q in patrols) foreach (var z in q.P) if (!entMap.ContainsKey(z.Key)) { var e = new Ent { Id = z.Id, G = z.G }; entMap[z.Key] = e; entList.Add(e); }
            entList = entList.Select((e, i) => (e, i)).OrderBy(t => t.e.Id).ThenBy(t => GroupIdx(t.e.G)).ThenBy(t => t.e.G, StringComparer.Ordinal).Select(t => t.e).ToList();
            var lockK = p.Locked ?? new(); var dnsK = p.Dns ?? new(); var resK = p.Reserves ?? new();
            var dnsOut = new List<DnsOut>(); var ents = new List<Ent>();
            foreach (var e in entList)
            {
                var k = e.Id + "|" + e.G;
                if (dnsK.TryGetValue(k, out var dt)) dnsOut.Add(new DnsOut(e.Id, e.G, dt));
                else if (lockK.TryGetValue(k, out var lt)) { e.Fixed = lt; ents.Add(e); }
                else if (resK.Contains(e.Id)) { }
                else ents.Add(e);
            }
            var staff = MakeStaff(p);
            foreach (var e in ents)
            {
                e.Staff = staff.TryGetValue(e.Id, out var sj) ? sj : null;
                e.Pat = p.PatrolIds != null ? p.PatrolIds.Contains(e.Id) : e.Staff != null;
                e.Early = p.Early != null && p.Early.Contains(e.Id);
                e.Late = !e.Early && p.Late != null && p.Late.Contains(e.Id);
                e.Pri = e.Pat || e.Early || e.Late || (p.PatrolIds == null && e.Staff != null);
            }
            // Förstarten skjuter bara blockets första klass (som en riktig förstart: funktionärerna skjuter sin första klass före öppning). Annars kan en funktionärs andra klass hamna där,
            // patrullen blir blandad och långsammare, och patrullen efter får en oförklarlig lucka.
            bool PreEnt(Ent e) => e.Pat && Tg(e.G) == blocks[0][0];
            var staffInB0 = ents.Count(e => blocks[0].Contains(e.G) && PreEnt(e));
            int SzFor(string[] grs) { var v = grs.Select(g => p.GroupSize != null && p.GroupSize.TryGetValue(g, out var x) ? x : 0).Where(x => x > 0).ToList(); return (int)(v.Count > 0 ? v.Min() : p.Size); }
            double IvFor(string[] grs) { var v = grs.Select(g => p.GroupInterval != null && p.GroupInterval.TryGetValue(g, out var x) ? x : 0).Where(x => x > 0).ToList(); return v.Count > 0 ? v.Max() : p.Interval; }
            var pre = p.Pre > 0 ? (int)Math.Min(p.Pre, Math.Ceiling((double)staffInB0 / SzFor(blocks[0]))) : 0;
            bool Forbidden(Ent e, double t, double D) => e.Staff != null && e.Staff.Any(j => t + D > j.A - p.JobBuf && t < j.B + p.JobBuf);
            var meta = new List<Meta>();
            for (var bi = 0; bi < blocks.Count; bi++)
            {
                var grs = blocks[bi];
                var list = ents.Where(e => grs.Contains(Tg(e.G))).ToList();
                foreach (var e in list) e.Bi = bi;
                var staffCnt = list.Count(e => bi == 0 ? PreEnt(e) : e.Pat);
                int sz = SzFor(grs); var iv = IvFor(grs);
                var lockedL = list.Where(e => e.Fixed != null).ToList();
                var xt = lockedL.Select(e => e.Fixed!.Value).Distinct().OrderBy(t => t).Select(t => (t, vac: (double)Math.Max(0, sz - lockedL.Count(e => e.Fixed == t)))).ToList();
                var V = xt.Sum(x => x.vac);
                var gridFree = (int)Math.Max(0, list.Count - lockedL.Count - V);
                var spare = (p.KeepMode && lockedL.Count > 0 ? 1 : 0) + Math.Max(0, p.ExtraSlots);
                var m = (bi == 0 && pre > 0
                    ? pre + (int)Math.Ceiling(Math.Max(0, gridFree - Math.Min(staffCnt, pre * sz)) / (double)sz)
                    : (int)Math.Ceiling(gridFree / (double)sz)) + spare;
                double ex = 0;
                var present = grs.Where(g => list.Any(e => Tg(e.G) == g)).ToList();
                for (var s = 0; s < N; s++) ex += ExtraMin(present, SecOf(p, s), p);
                meta.Add(new Meta { Grs = grs, List = list, M = m, D = D0 + ex, Xt = xt, GridFree = gridFree, Sz = sz, Iv = iv, Spare = spare });
            }
            var goff = new List<int>();
            void SetGoff() { goff.Clear(); var acc = 0; foreach (var b in meta) { goff.Add(acc); acc += b.M + b.Mg; } }
            SetGoff();
            var total = meta.Sum(b => b.M + b.Xt.Count);
            var byPerson = new Dictionary<int, List<Ent>>();
            foreach (var e in ents) { if (!byPerson.TryGetValue(e.Id, out var l)) byPerson[e.Id] = l = new(); l.Add(e); }
            // En skytts starter i samma block följer den valda ordningen mellan vapengrupperna (som GroupIdx, men efter ordningen; okänd grupp ger -1 som förut).
            var groupOrder = OrderOf(p);
            int GroupRank(string g) => Array.IndexOf(groupOrder, g);
            foreach (var kv in byPerson)
            {
                var v = kv.Value.Select((e, i) => (e, i)).OrderBy(t => t.e.Bi).ThenBy(t => GroupRank(Tg(t.e.G))).ThenBy(t => t.i).Select(t => t.e).ToList();
                kv.Value.Clear(); kv.Value.AddRange(v);
                for (var i = 0; i < v.Count; i++)
                {
                    var e = v[i]; e.Prev = i > 0 ? v[i - 1] : null; e.HasLater = i < v.Count - 1; e.NLater = v.Count - 1 - i; e.NextOrd = i < v.Count - 1 ? v[i + 1].Bi * 100 + GroupRank(Tg(v[i + 1].G)) : int.MaxValue;
                    e.Rank = e.Prev != null && e.Prev.Bi == e.Bi ? e.Prev.Rank + 1 : 0;
                }
            }
            // Klumpar (krav "samma patrull") och mjuka önskemål. Utan Together/Wish är det här ett tomt varv: ERank = Rank, Clump = -1, WishRank = 0.
            var clumpOf = BuildClumps(p, ents.Select(e => e.Id + "|" + e.G).ToHashSet());
            foreach (var e in ents)
            {
                e.Clump = clumpOf.TryGetValue(e.Id + "|" + e.G, out var cid) ? cid : -1;
                e.ERank = e.Rank;
                if (p.Wish != null && p.Wish.TryGetValue(e.Id + "|" + e.G, out var w)) e.WishRank = w < 0 && e.Prev == null ? -1 : w > 0 && !e.HasLater ? 1 : 0;
            }
            // Klumpmedlemmar delar rank (den högsta), och ingen start ligger före sin egen tidigare start i samma block.
            for (var pass = 0; pass < 12; pass++)
            {
                var changed = false;
                foreach (var grp in ents.Where(e => e.Clump >= 0).GroupBy(e => e.Clump)) { var r = grp.Max(e => e.ERank); foreach (var e in grp) if (e.ERank != r) { e.ERank = r; changed = true; } }
                foreach (var e in ents) if (e.Prev != null && e.Prev.Bi == e.Bi && e.ERank <= e.Prev.ERank) { e.ERank = e.Prev.ERank + 1; changed = true; }
                if (!changed) break;
            }
            var fixedAll = meta.SelectMany(b => b.Xt.Select(x => x.T)).ToList(); double? fixedMax = fixedAll.Count > 0 ? fixedAll.Max() : null;
            double GlideAt(int k) => p.GlideEvery > 0 && p.GlideMin > 0 && (k + 1) % p.GlideEvery == 0 ? p.GlideMin : 0;
            var gapsAfter = new double[total + 64];
            double IvAt(int i) { var b = 0; for (var bi = 0; bi < goff.Count; bi++) if (goff[bi] <= i) b = bi; return meta[b].Iv; }
            double? GsOf(int bi) { var v = meta[bi].Grs.Select(g => p.GroupStart != null && p.GroupStart.TryGetValue(g, out var x) ? (double?)x : null).Where(x => x != null).Select(x => x!.Value).ToList(); return v.Count > 0 ? v.Min() : null; }

            List<EPatrol> resPatrols = new(); List<PauseOut> pauses = new(); var gOut = new int[meta.Count];
            (List<(double T, List<Start> Chunk, bool Fixed)> Res, List<(double T, List<Start> Chunk, bool Fixed)> Extra) AssignAll()
            {
                var res = new List<(double, List<Start>, bool)>(); var extra = new List<(double, List<Start>, bool)>();
                pauses = new List<PauseOut>();
                (double T, int G, double Glide)? prevLast = null;
                for (var i = 0; i < meta.Count; i++) gOut[i] = meta[i].M + meta[i].Mg;

                TimesArr TimesFor(int bi, double delta)
                {
                    var gs = GsOf(bi);
                    double t = gs != null ? gs.Value : bi == 0 ? p.Open : (useDay2 && bi == p.DayBreak) ? Day2Offset + p.Open2
                        : (prevLast != null ? prevLast.Value.T + meta[bi].Iv + gapsAfter[prevLast.Value.G] + prevLast.Value.Glide + delta : p.Open);
                    if (p.KeepMode && gs == null && fixedMax != null) t = Math.Max(t, fixedMax.Value + meta[bi].Iv + delta);
                    var ta = new TimesArr(); var arr = ta.Arr;
                    var np = bi == 0 ? pre : 0;
                    // Förstartspatrullerna får samma extra luckor som övriga (en patrull med blandade klasser tar längre tid vid stationerna): annars krockar de med varandra.
                    double tp = p.PreStart;
                    for (var k = 0; k < np; k++) { arr.Add(tp); tp += meta[bi].Iv + gapsAfter[goff[bi] + k]; }
                    int u = np, kk = np;
                    while (u < meta[bi].M)
                    {
                        var xi = meta[bi].Xt.FindIndex(x => Math.Abs(x.T - t) < 1e-9);
                        arr.Add(t);
                        if (xi >= 0 && !ta.MergedOf.ContainsKey(xi)) ta.MergedOf[xi] = arr.Count - 1; else u++;
                        t += meta[bi].Iv + gapsAfter[goff[bi] + kk] + GlideAt(kk - np); kk++;
                    }
                    ta.G = arr.Count;
                    for (var xi = 0; xi < meta[bi].Xt.Count; xi++) if (!ta.MergedOf.ContainsKey(xi)) { ta.Rest.Add(xi); arr.Add(meta[bi].Xt[xi].T); }
                    return ta;
                }

                TryRes TryBlock(int bi, double delta)
                {
                    var mb = meta[bi]; var times = TimesFor(bi, delta); var arr = times.Arr;
                    var np = bi == 0 ? pre : 0;
                    var cap = new List<double>();
                    {
                        var ng = mb.GridFree - (np > 0 ? Math.Min(mb.List.Count(e => PreEnt(e) && e.Fixed == null), np * mb.Sz) : 0);
                        var mm2 = mb.M - mb.Spare - np; var bas = mm2 > 0 ? (int)Math.Floor(ng / (double)mm2) : 0; var rem = mm2 > 0 ? ng % mm2 : 0;
                        var mergedAt = new Dictionary<int, int>(); foreach (var kv in times.MergedOf) mergedAt[kv.Value] = kv.Key; var reg = 0;
                        for (var k = 0; k < arr.Count; k++)
                        {
                            if (k < np) cap.Add(mb.Sz);
                            else if (k >= times.G) cap.Add(mb.Xt[times.Rest[k - times.G]].Vac);
                            else if (mergedAt.ContainsKey(k)) cap.Add(mb.Xt[mergedAt[k]].Vac);
                            else { var r = reg++; cap.Add(r >= mm2 ? mb.Sz : bas + (r < rem ? 1 : 0)); }
                        }
                    }
                    var slot = new Dictionary<Ent, int>(); var wishFail = 0; var hard = 0;
                    foreach (var e in mb.List) if (e.Fixed != null)
                        {
                            var xi = mb.Xt.FindIndex(x => x.T == e.Fixed.Value);
                            slot[e] = times.MergedOf.TryGetValue(xi, out var gi) ? gi : times.G + times.Rest.IndexOf(xi);
                        }
                    bool SamePatrol(Ent e, int j) => byPerson[e.Id].Any(o => o != e && o.Bi == bi && slot.TryGetValue(o, out var sj) && sj == j);
                    bool OkSlot(Ent e, int j) => cap[j] > 0 && (j >= np || PreEnt(e)) && !Forbidden(e, arr[j], mb.D) && !SamePatrol(e, j);
                    var maxRank = Math.Max(0, mb.List.Count > 0 ? mb.List.Max(e => e.ERank) : 0);
                    // Väljer plats för en enhet (en skytt, eller en klump som måste stå i samma patrull). Kedjan av reservplaner är den ursprungliga; för en enhet på en medlem är den identisk med förut.
                    int ChooseSlot(List<Ent> M, double aw, double am, bool lateSeek, bool wishLate)
                    {
                        var size = M.Count; var k = -1;
                        bool Cap(int j) => cap[j] > size - 1;
                        bool Seat(int j) => M.All(m => j >= np || PreEnt(m));
                        bool Ok(int j) => Cap(j) && Seat(j) && M.All(m => !Forbidden(m, arr[j], mb.D) && !SamePatrol(m, j));
                        if (lateSeek || wishLate)
                        {
                            // Ett sent önskemål (mjukt) tar aldrig de reserverade extraplatserna sist i blocket; funktionärernas Late gör det som förut.
                            var top = lateSeek ? times.G - 1 : Math.Max(np, times.G - 1 - mb.Spare);
                            foreach (var lim in new[] { aw, am }) { if (k >= 0) break; for (var j = top; j >= np; j--) if (Ok(j) && arr[j] >= lim - 1e-9) { k = j; break; } }
                        }
                        if (k < 0) for (var j = 0; j < arr.Count; j++) if (Ok(j) && arr[j] >= aw - 1e-9) { k = j; break; }
                        if (k < 0) { for (var j = 0; j < arr.Count; j++) if (Ok(j) && arr[j] >= am - 1e-9) { k = j; break; } if (k >= 0) wishFail++; }
                        if (k < 0) for (var j = 0; j < arr.Count; j++) if (Cap(j) && Seat(j) && M.All(m => !SamePatrol(m, j)) && arr[j] >= am - 1e-9) { k = j; break; }
                        if (k < 0) { hard++; for (var j = arr.Count - 1; j >= 0; j--) if (Ok(j)) { k = j; break; } }
                        if (k < 0) for (var j = arr.Count - 1; j >= 0; j--) if (Cap(j) && Seat(j)) { k = j; break; }
                        if (k < 0) for (var j = arr.Count - 1; j >= 0; j--) if (Cap(j)) { k = j; break; }
                        if (k < 0) k = arr.Count - 1;
                        return k;
                    }
                    (List<Ent> M, double aw, double am) ItemOf(List<Ent> M)
                    {
                        double aw = double.NegativeInfinity, am = double.NegativeInfinity;
                        foreach (var e in M)
                        {
                            double pv = double.NegativeInfinity;
                            if (e.Prev != null) pv = e.Prev.Bi < bi ? e.Prev.Tm + e.Prev.Dm : slot.TryGetValue(e.Prev, out var ps2) ? arr[ps2] + mb.D : double.NegativeInfinity;
                            aw = Math.Max(aw, e.Prev != null ? pv + p.VilaW : double.NegativeInfinity); am = Math.Max(am, e.Prev != null ? pv + p.Vila : double.NegativeInfinity);
                        }
                        return (M, aw, am);
                    }
                    for (var rk = 0; rk <= maxRank; rk++)
                    {
                        // Enheter: varje skytt för sig, och varje klump (krav "samma patrull") som EN enhet med alla sina medlemmar. Utan klumpar är alla enheter en medlem och ordningen som förut.
                        var items = mb.List.Where(e => e.ERank == rk && e.Fixed == null && e.Clump < 0).Select(e => ItemOf(new List<Ent> { e })).ToList();
                        foreach (var grp in mb.List.Where(e => e.ERank == rk && e.Fixed == null && e.Clump >= 0).GroupBy(e => e.Clump)) items.Add(ItemOf(grp.ToList()));
                        int Cls((List<Ent> M, double aw, double am) x) => x.M.Any(e => e.Pri) ? -1 : double.IsNegativeInfinity(x.aw) ? (x.M.Any(e => e.HasLater) && p.Smart ? 0 : 2) : 1;
                        // JS-jämförelsen: a || b || c || d där NaN och 0 är falska (-Infinity - -Infinity = NaN faller till id).
                        double Cmp((List<Ent> M, double aw, double am) x, (List<Ent> M, double aw, double am) y)
                        {
                            double d = Cls(x) - Cls(y); if (d != 0 && !double.IsNaN(d)) return d;
                            d = y.M.Count - x.M.Count; if (d != 0) return d;   // klumpar före lösa skyttar: de behöver flera platser i samma patrull
                            d = Cls(x) == 0 ? y.M.Max(e => e.NLater) - x.M.Max(e => e.NLater) : 0; if (d != 0 && !double.IsNaN(d)) return d;
                            if (p.NextFirst && Cls(x) == 0) { d = x.M.Min(e => e.NextOrd) - y.M.Min(e => e.NextOrd); if (d != 0) return d; }
                            d = x.M[0].WishRank - y.M[0].WishRank; if (d != 0) return d;   // mjukt önskemål: tidig före ingen före sen, efter att fler-starter-regeln fått gå först
                            d = x.aw - y.aw; if (d != 0 && !double.IsNaN(d)) return d;
                            return x.M[0].Id - y.M[0].Id;
                        }
                        items = InsertionSort(items, Cmp);
                        foreach (var x in items)
                        {
                            var M = x.M; var size = M.Count;
                            if (size > 1 || M[0].Clump >= 0)
                            {
                                // En klump (även när den ensamma som återstår att placera har en partner som redan ligger fast): alla medlemmar i samma patrull. Ligger en medlem redan fast (manuellt låst) är det den patrullen.
                                var fixedJ = mb.List.Where(e => e.Clump == M[0].Clump && e.Fixed != null).Select(e => slot[e]).Distinct().ToList();
                                var ku = -1;
                                if (fixedJ.Count == 1) { var jf = fixedJ[0]; if (cap[jf] > size - 1 && M.All(m => !SamePatrol(m, jf))) ku = jf; }
                                else if (fixedJ.Count == 0 && Enumerable.Range(0, arr.Count).Any(j => cap[j] > size - 1)) ku = ChooseSlot(M, x.aw, x.am, M.Any(e => e.Late), M.Any(e => e.WishRank > 0));
                                if (ku >= 0)
                                {
                                    foreach (var m in M) { if (Forbidden(m, arr[ku], mb.D)) hard++; slot[m] = ku; }
                                    cap[ku] -= size;
                                    continue;
                                }
                                // Går inte att samla (ingen patrull rymmer dem, eller medlemmar låsta på olika tider): placera dem var för sig. Brottet räknas i Qual.Link, aldrig tyst.
                                foreach (var m in M)
                                {
                                    var xm = ItemOf(new List<Ent> { m });
                                    var km = ChooseSlot(xm.M, xm.aw, xm.am, m.Late, m.WishRank > 0);
                                    if (Forbidden(m, arr[km], mb.D)) hard++;
                                    cap[km]--; slot[m] = km;
                                }
                                continue;
                            }
                            var e1 = M[0];
                            var k = ChooseSlot(M, x.aw, x.am, e1.Late, e1.WishRank > 0);
                            if (Forbidden(e1, arr[k], mb.D)) hard++;
                            cap[k]--; slot[e1] = k;
                        }
                    }
                    return new TryRes { Times = times, Slot = slot, WishFail = wishFail, Hard = hard, Delta = delta };
                }

                for (var bi = 0; bi < meta.Count; bi++)
                {
                    p.Ct.ThrowIfCancellationRequested();
                    TryRes best;
                    var sweep = p.KeepMode && meta[bi].Spare > 0 && GsOf(bi) == null && !(useDay2 && bi == p.DayBreak);
                    if (bi == 0 && !sweep) best = TryBlock(0, 0);
                    else if (!sweep && ((useDay2 && bi == p.DayBreak) || GsOf(bi) != null || prevLast == null)) best = TryBlock(bi, 0);
                    else
                    {
                        double? dHard = null; TryRes? pick = null;
                        var dStart = (bi == 0 || prevLast == null) ? 0 : d0; var step = p.KeepMode ? 5 : 1;
                        for (double d = dStart; d <= dStart + 300; d += step)
                        {
                            p.Ct.ThrowIfCancellationRequested();
                            var r = TryBlock(bi, d);
                            if (r.Hard == 0 && dHard == null) { dHard = d; pick = r; }
                            if (r.Hard == 0 && r.WishFail == 0) { if (d - dHard!.Value <= pauseMax) pick = r; break; }
                            if (dHard != null && d - dHard.Value >= pauseMax) break;
                        }
                        best = pick ?? TryBlock(bi, 0);
                    }
                    foreach (var e in meta[bi].List) { e.Tm = best.Times.Arr[best.Slot[e]]; e.Dm = meta[bi].D; }
                    var parts = best.Times.Arr.Select(_ => new List<Start>()).ToList();
                    foreach (var e in meta[bi].List) parts[best.Slot[e]].Add(new Start(e.Id, e.G));
                    for (var k = 0; k < best.Times.Arr.Count; k++)
                        if (k < best.Times.G) res.Add((best.Times.Arr[k], parts[k], false)); else extra.Add((best.Times.Arr[k], parts[k], true));
                    if (bi > 0 && GsOf(bi) == null && !(useDay2 && bi == p.DayBreak) && best.Delta > d0 + 0.001)
                        pauses.Add(new PauseOut { At = goff[bi], Min = best.Delta + p.Interval, Ext = best.Delta - d0, Set = d0 + p.Interval });
                    if (best.Hard > 0) pauses.Add(new PauseOut { At = goff[bi], Hard = best.Hard });
                    if (meta[bi].M > 0)
                    {
                        prevLast = (best.Times.Arr[best.Times.G - 1], goff[bi] + best.Times.G - 1, GlideAt(best.Times.G - 1 - (bi == 0 ? pre : 0)));
                        gOut[bi] = best.Times.G;
                    }
                }
                return (res, extra);
            }

            List<EPatrol> dsx = new(); var GN = 0;
            for (var it = 0; it < 12; it++)
            {
                var o = AssignAll();
                GN = o.Res.Count;
                { var ch = false; for (var bi = 0; bi < meta.Count; bi++) if (gOut[bi] != meta[bi].M + meta[bi].Mg) { meta[bi].Mg = gOut[bi] - meta[bi].M; ch = true; } if (ch && it < 11) { SetGoff(); continue; } }
                dsx = o.Res.Concat(o.Extra).Select((q, i) => new EPatrol { N = i + 1, S = q.T, E = q.T + D0, P = q.Chunk, Fixed = q.Fixed, Pre = i < pre }).ToList();
                if (!p.Auto) break;
                var r = Run(dsx, p);
                var changed = false;
                foreach (var q in r.Ps) { var i = q.N - 1; if (i >= GN) continue; var ne = Math.Max(gapsAfter[i], q.Need - IvAt(i)); if (ne - gapsAfter[i] > 0.01) { gapsAfter[i] = ne; changed = true; } }
                if (!changed) break;
            }
            var result = new PlanResult { Id = id + "-gen", Patrols = dsx, Pre = pre };
            result.Ungrouped = ents.Select(e => Tg(e.G)).Where(g => GroupIdx(g) < 0).Distinct().OrderBy(g => g, StringComparer.Ordinal).ToList();
            if (p.Waived != null) result.Waived = p.Waived.Where(wid => p.Jobs != null && p.Jobs.ContainsKey(wid) && p.Jobs[wid].Count > 0 && ents.Any(e => e.Id == wid)).OrderBy(x => x).ToList();
            result.Dns = dnsOut;
            var lockedKeys = lockK.Keys.Where(k => !dnsK.ContainsKey(k)).ToHashSet();
            if (p.Improve)
            {
                var P = result.Patrols;
                var snap = P.Select(q => (q.S, q.E, P: new List<Start>(q.P))).ToList();
                var before = QualOf(P, p);
                var pausePart = P.Select((q, i) => i + 1 < GN ? P[i + 1].S - q.S - IvAt(i) - gapsAfter[i] : 0).ToList();
                var ctx = new ImproveCtx
                {
                    BlockStart = goff.ToList(), GridN = GN, LockedKeys = lockedKeys, Pre = pre, Staff = staff,
                    PatIds = ents.Where(e => e.Pat).Select(e => e.Id).ToHashSet(), EarlyIds = ents.Where(e => e.Early || e.Late).Select(e => e.Id).ToHashSet(), HasPatIds = true,
                    LinkKeys = ents.Where(e => e.Clump >= 0).Select(e => e.Id + "|" + e.G).ToHashSet(),
                };
                for (var round = 0; round < 3; round++)
                {
                    var st = Improve(P, ctx, p, D0);
                    result.Stats.Tried += st.Tried; result.Stats.Accepted += st.Accepted; result.Stats.MinGain = Math.Min(result.Stats.MinGain, st.MinGain);
                    if (!p.Auto) break;
                    var r = Run(P, p);
                    foreach (var q in r.Ps) { var i = q.N - 1; if (i < GN) gapsAfter[i] = Math.Max(0, q.Need - IvAt(i)); }
                    for (var i = 1; i < GN; i++) { P[i].S = P[i].P.Any(z => lockedKeys.Contains(z.Key)) ? snap[i].S : P[i - 1].S + IvAt(i - 1) + gapsAfter[i - 1] + pausePart[i - 1]; P[i].E = P[i].S + D0; }
                }
                var after = QualOf(P, p);
                if (Better(before, after)) for (var i = 0; i < P.Count; i++) { P[i].S = snap[i].S; P[i].E = snap[i].E; P[i].P = snap[i].P; }
            }
            var pauseRef = pauses.Select(z => (z, q: z.At < result.Patrols.Count ? result.Patrols[z.At] : null)).ToList();
            if (result.Patrols.Any(q => q.P.Count == 0)) result.Patrols = result.Patrols.Where(q => q.P.Count > 0).ToList();
            result.Patrols = result.Patrols.Select((q, i) => (q, i)).OrderBy(t => t.q.S).ThenBy(t => t.i).Select(t => t.q).ToList();
            for (var i = 0; i < result.Patrols.Count; i++) result.Patrols[i].N = i + 1;
            result.Pauses = pauseRef.Select(t =>
            {
                var j = t.q != null ? result.Patrols.FindIndex(x => x.S >= t.q.S - 1e-9) : -1;
                return new PauseOut { Min = t.z.Min, Ext = t.z.Ext, Set = t.z.Set, Hard = t.z.Hard, At = -1, Before = j >= 0 ? j + 1 : null };
            }).ToList();
            return result;
        }

        static List<T> InsertionSort<T>(List<T> a, Func<T, T, double> cmp)
        {
            var r = new List<T>(a);
            for (var i = 1; i < r.Count; i++)
            {
                var x = r[i]; var j = i - 1;
                while (j >= 0 && cmp(r[j], x) > 0) { r[j + 1] = r[j]; j--; }
                r[j + 1] = x;
            }
            return r;
        }

        // ------------------------------------------------------------ förbättring

        sealed class ImproveCtx
        {
            public List<int> BlockStart = new(); public int GridN, Pre; public HashSet<string> LockedKeys = new(); public Dictionary<int, List<(double A, double B)>> Staff = new();
            public HashSet<int> PatIds = new(), EarlyIds = new(); public bool HasPatIds;
            /// <summary>Starter som ingår i en klump (krav "samma patrull"): förbättringen flyttar dem aldrig, så ett par inte kan slitas isär efter att placeringen lyckats.</summary>
            public HashSet<string> LinkKeys = new();
        }

        sealed class Rng
        {
            uint _x;
            public Rng(uint seed) { _x = seed; }
            public double Next()
            {
                unchecked
                {
                    _x += 0x6D2B79F5u; var t = _x;
                    t = (t ^ (t >> 15)) * (t | 1u);
                    t ^= t + (t ^ (t >> 7)) * (t | 61u);
                    return (t ^ (t >> 14)) / 4294967296.0;
                }
            }
        }

        static string GSet(IEnumerable<Start> ps)
        {
            var l = ps.ToList(); var s = "";
            foreach (var g in Groups) if (l.Any(z => z.G == g)) s += g;
            return s + string.Concat(l.Where(z => GroupIdx(z.G) < 0).Select(z => z.G).OrderBy(x => x, StringComparer.Ordinal));
        }

        static ImproveStats Improve(List<EPatrol> P, ImproveCtx dsx, PlanParams p, double D0)
        {
            var n = P.Count;
            var blk = P.Select((_, i) => i >= dsx.GridN ? 999 : dsx.BlockStart.Count(s => s <= i) - 1).ToArray();
            // Smart placering gäller alltid: förstastarten för en skytt med flera starter flyttas aldrig av förbättringen.
            var pinned = new HashSet<string>();
            bool IsLocked(Start z) => dsx.LockedKeys.Contains(z.Key) || dsx.EarlyIds.Contains(z.Id) || pinned.Contains(z.Key) || dsx.LinkKeys.Contains(z.Key);
            var times = P.Select(q => q.S).ToArray();
            var dur = P.Select(_ => D0).ToArray();
            { var r0 = Run(P, p); foreach (var q in r0.Ps) dur[q.N - 1] = q.Dur; }
            var pos = new Dictionary<int, List<(int Pi, string G)>>(); var posOrder = new List<int>();
            for (var i = 0; i < P.Count; i++) foreach (var z in P[i].P) { if (!pos.TryGetValue(z.Id, out var l)) { pos[z.Id] = l = new(); posOrder.Add(z.Id); } l.Add((i, z.G)); }
            if (p.Smart) foreach (var kv in pos) if (kv.Value.Count > 1) { var e0 = kv.Value.Select((t, ix) => (t, ix)).OrderBy(t => t.t.Pi).ThenBy(t => t.ix).First().t; pinned.Add(kv.Key + "|" + e0.G); }
            bool Bound(int id) => pos[id].Count > 1 || dsx.Staff.ContainsKey(id) || dsx.PatIds.Contains(id) || dsx.Pre > 0;
            double CostOf(int id)
            {
                var v = pos[id]; double c = 0;
                var hasStaff = dsx.Staff.TryGetValue(id, out var jobs); var pre = dsx.Pre;
                foreach (var x in v)
                {
                    if (hasStaff && jobs!.Any(j => times[x.Pi] + dur[x.Pi] > j.A - p.JobBuf && times[x.Pi] < j.B + p.JobBuf)) c += 800;
                    if (!(dsx.HasPatIds ? dsx.PatIds.Contains(id) : hasStaff) && blk[x.Pi] == 0 && x.Pi < pre) c += 800;
                }
                if (v.Count < 2) return c;
                var a = v.Select(x => x.Pi).Select((pi, ix) => (pi, ix)).OrderBy(t => times[t.pi]).ThenBy(t => t.pi).ThenBy(t => t.ix).Select(t => t.pi).ToList();
                var sl = new List<(double, bool)>();
                for (var j = 1; j < a.Count; j++)
                {
                    if (a[j] == a[j - 1]) { c += 5000; continue; }
                    var idle = times[a[j]] - (times[a[j - 1]] + dur[a[j - 1]]);
                    var slack = idle - p.Vila;
                    if (slack < -0.01) c += 500 - slack * 10;
                    sl.Add((slack, idle > 600));
                }
                return c + RestSoft(sl, p);
            }
            var rng = new Rng((uint)(p.ImproveSeed is > 0 ? p.ImproveSeed.Value : 12345));
            var entsAll = new List<(int I, int K)>(); for (var i = 0; i < P.Count; i++) for (var k = 0; k < P[i].P.Count; k++) entsAll.Add((i, k));
            var moves = p.Iters ?? 60000;
            var boundN = posOrder.Count(Bound);
            var stallMax = p.Stall is > 0 ? p.Stall.Value : Math.Min(moves, Math.Max(3000, 50 * boundN));
            var stats = new ImproveStats(); int stall = 0, tried = 0; long lastGood = 0;
            for (long draw = 0; tried < moves && draw < (long)moves * 20; draw++)
            {
                if ((draw & 255) == 0) p.Ct.ThrowIfCancellationRequested();
                if (stall > stallMax || draw - lastGood > (long)stallMax * 30) break;
                var (i, k) = entsAll[(int)Math.Floor(rng.Next() * entsAll.Count)];
                var j = (int)Math.Floor(rng.Next() * n);
                if (j == i || blk[j] != blk[i]) continue;
                if (k >= P[i].P.Count) continue;
                var ez = P[i].P[k];
                if (IsLocked(ez) || P[j].P.Any(z => z.Id == ez.Id)) continue;
                var l = (int)Math.Floor(rng.Next() * P[j].P.Count);
                if (l >= P[j].P.Count) continue;
                var ey = P[j].P[l];
                if (ey.Id == ez.Id || IsLocked(ey) || P[i].P.Any(z => z.Id == ey.Id)) continue;
                if (!Bound(ez.Id) && !Bound(ey.Id)) continue;
                if (ez.G != ey.G)
                {
                    var gi = GSet(P[i].P); var gj = GSet(P[j].P);
                    var ni = GSet(P[i].P.Select((z, t) => t == k ? ey : z)); var nj = GSet(P[j].P.Select((z, t) => t == l ? ez : z));
                    if (gi != ni || gj != nj) continue;
                }
                tried++; stall++;
                var ids = ez.Id == ey.Id ? new[] { ez.Id } : new[] { ez.Id, ey.Id };
                double before = 0; foreach (var id in ids) before += CostOf(id);
                void Fix(int id, int from, int to, string g) { var v = pos[id]; var ix = v.FindIndex(t => t.Pi == from && t.G == g); if (ix >= 0) v[ix] = (to, v[ix].G); }
                Fix(ez.Id, i, j, ez.G); Fix(ey.Id, j, i, ey.G);
                double after = 0; foreach (var id in ids) after += CostOf(id);
                if (after < before - 1e-9)
                {
                    stats.Accepted++; stats.MinGain = Math.Min(stats.MinGain, before - after);
                    stall = 0; lastGood = draw;
                    P[i].P[k] = ey; P[j].P[l] = ez;
                }
                else { Fix(ez.Id, j, i, ez.G); Fix(ey.Id, i, j, ey.G); }
            }
            stats.Tried = tried;
            return stats;
        }

        // ------------------------------------------------------------ uppdelning

        static PlanResult GenerateAuto(string id, IReadOnlyList<EPatrol> patrols, PlanParams p)
        {
            var cands = new List<(PlanResult G, Qual Q, string O)>();
            foreach (var o in OrderKeys)
            {
                p.Ct.ThrowIfCancellationRequested();
                var b = BlocksFor(patrols, p, o);
                if (p.Day2 && p.DayBreak > 0 && p.DayBreak >= b.Count) continue;
                var pp = p.Clone(); pp.Order = o; pp.Improve = false;
                var g = Generate0(id, patrols, pp);
                cands.Add((g, QualOf(g.Patrols, p), o));
            }
            if (cands.Count == 0)
            {
                var pp = p.Clone(); pp.Order = OrderKeys[0]; pp.Improve = false;
                var g = Generate0(id, patrols, pp); cands.Add((g, QualOf(g.Patrols, p), OrderKeys[0]));
            }
            var ranked = InsertionSort(cands.Select((c, i) => (c, i)).ToList(), (a, b) => Better(a.c.Q, b.c.Q) ? -1 : Better(b.c.Q, a.c.Q) ? 1 : a.i - b.i).Select(t => t.c).ToList();
            (PlanResult G, Qual Q, string O)? best = null;
            foreach (var c in ranked.Take(2))
            {
                PlanResult g; Qual q;
                if (!p.Improve) { g = c.G; q = c.Q; }
                else { var pp = p.Clone(); pp.Order = c.O; g = Generate0(id, patrols, pp); q = QualOf(g.Patrols, p); }
                if (best == null || Better(q, best.Value.Q)) best = (g, q, c.O);
            }
            if (p.Improve)
                for (var s = 1; s <= 3; s++)
                {
                    var pp = p.Clone(); pp.Order = best!.Value.O; pp.ImproveSeed = 777 * s + 1;
                    var g = Generate0(id, patrols, pp); var q = QualOf(g.Patrols, p);
                    if (Better(q, best.Value.Q)) best = (g, q, best.Value.O);
                }
            best!.Value.G.Chosen = best.Value.O; best.Value.G.Q = best.Value.Q;
            return best.Value.G;
        }

        // ------------------------------------------------------------ omkörning som behåller det som passar

        /// <summary>
        /// Alla som redan har en start som inte bryter en hård regel (vila, eget uppdrag, förstartskrav, taket) låses på sin nuvarande tid; bara de övriga och de nya placeras.
        /// Spärrad lista (publicerad) kommer tillbaka exakt som den kom in.
        /// </summary>
        public static KeepResult RebuildKeep(string id, IReadOnlyList<EPatrol> patrols, PlanParams p, IReadOnlyList<Start>? add = null, ISet<string>? remove = null)
        {
            if (p.Published) { var b = Blocked(patrols, id, p); return new KeepResult { G = b, Blocked = b.Blocked }; }
            add ??= Array.Empty<Start>(); remove ??= new HashSet<string>();
            var cur = patrols.Select(q => { var c = q.Copy(); c.P = q.P.Where(z => !remove.Contains(z.Key)).ToList(); return c; }).Where(q => q.P.Count > 0).ToList();
            var pl = p.Clone(); pl.KeepMode = true;
            var r = Run(cur, pl);
            var bad = new HashSet<string>();
            var policy = p.KeepPolicy;
            if (policy == "full") foreach (var z in r.Persons) foreach (var pr in z.Pairs) if (pr.Slack < -0.01) bad.Add(z.Id + "|" + pr.B.G);
            var staff = MakeStaff(pl);
            if (staff.Count > 0) foreach (var q in cur) foreach (var z in q.P) if (staff.TryGetValue(z.Id, out var sf) && sf.Any(j => q.E > j.A - pl.JobBuf && q.S < j.B + pl.JobBuf)) bad.Add(z.Key);
            if (policy == "full" && p.PatrolIds != null && p.Pre > 0) foreach (var q in cur) foreach (var z in q.P) if (p.PatrolIds.Contains(z.Id) && !(q.Pre || q.S < p.Open)) bad.Add(z.Key);
            foreach (var q in cur) foreach (var z in q.P.Skip((int)PatrolCap(q, pl))) bad.Add(z.Key);
            // Ett krav "samma patrull" som inte längre håller (partnerna ligger i olika patruller) låser ingen av dem: båda placeras om tillsammans.
            if (p.Together != null)
            {
                var curAt = new Dictionary<string, int>(); for (var i = 0; i < cur.Count; i++) foreach (var z in cur[i].P) curAt[z.Key] = i;
                foreach (var kv in p.Together) if (curAt.TryGetValue(kv.Key, out var ia) && curAt.TryGetValue(kv.Value, out var ib) && ia != ib) { bad.Add(kv.Key); bad.Add(kv.Value); }
            }
            var locked = p.Locked != null ? new Dictionary<string, double>(p.Locked) : new Dictionary<string, double>();
            var kept = new List<string>();
            foreach (var q in cur) foreach (var z in q.P)
                {
                    var k = z.Key;
                    if (locked.ContainsKey(k) || (p.Dns != null && p.Dns.ContainsKey(k))) continue;
                    if (bad.Contains(k) || (p.Reserves != null && p.Reserves.Contains(z.Id))) continue;
                    locked[k] = q.S; kept.Add(k);
                }
            var all = cur.Select(q => q.Copy()).ToList();
            if (add.Count > 0) all.Add(new EPatrol { N = 0, S = 0, E = 0, P = add.ToList() });
            var pg = pl.Clone(); pg.Locked = locked;
            var g = Generate(id, all, pg);
            var at = new Dictionary<string, double>(); foreach (var q in g.Patrols) foreach (var z in q.P) at[z.Key] = q.S;
            var moved = kept.Count(k => !at.TryGetValue(k, out var s) || s != locked[k]);
            return new KeepResult { G = g, Kept = kept.Count, Moved = moved, Unlocked = bad.Count, Added = add.Count, Removed = remove.Count };
        }
    }
}
