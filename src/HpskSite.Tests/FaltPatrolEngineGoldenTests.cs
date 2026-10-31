using System.Text.Json;
using HpskSite.CompetitionTypes.Faltskytte.Engine;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Golden master: C#-motorn ger samma lista som förra gången på anonymiserade riktiga listor (id och klass, inga namn). C#-motorn är sin egen referens: prototypen (notes/patrullgenerator-prototyp)
    /// var facit till och med 2026-10-31 men följs inte längre (Stefan 2026-10-31), så NextFirst och senare regler finns bara här.
    /// Faller ett fall: undersök om ändringen är avsiktlig. Är den det, skapa om facit med HPSK_REGEN_GOLDEN=1 (Regenerate_golden_from_the_engine_only_when_asked) och granska git diff på filen.
    /// Mot facit kör motorn som produktionen (NextFirst på). ExtraSlots-slingan ligger i adaptern och prövas av FaltPatrolAdapterTests.
    /// </summary>
    public class FaltPatrolEngineGoldenTests
    {
        static readonly string GoldenPath = Path.Combine(AppContext.BaseDirectory, "Faltskytte", "golden-patrol-engine.json");

        public static IEnumerable<object[]> Cases()
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(GoldenPath));
            foreach (var c in doc.RootElement.EnumerateArray()) yield return new object[] { c.GetProperty("name").GetString()! };
        }

        static double D(JsonElement e) => e.ValueKind == JsonValueKind.Null ? 0 : e.GetDouble();

        static PlanParams ParamsOf(JsonElement j)
        {
            var p = new PlanParams();
            foreach (var pr in j.EnumerateObject())
            {
                var v = pr.Value;
                switch (pr.Name)
                {
                    case "interval": p.Interval = D(v); break;
                    case "R": p.R = D(v); break;
                    case "N": p.N = (int)D(v); break;
                    case "vila": p.Vila = D(v); break;
                    case "vilaW": p.VilaW = D(v); break;
                    case "open": p.Open = D(v); break;
                    case "preStart": p.PreStart = D(v); break;
                    case "close": p.Close = D(v); break;
                    case "open2": p.Open2 = D(v); break;
                    case "close2": p.Close2 = D(v); break;
                    case "pauseBlock": p.PauseBlock = D(v); break;
                    case "pauseExt": p.PauseExt = D(v); break;
                    case "roundExtra": p.RoundExtra = D(v); break;
                    case "jobBuf": p.JobBuf = D(v); break;
                    case "glideEvery": p.GlideEvery = (int)D(v); break;
                    case "glideMin": p.GlideMin = D(v); break;
                    case "size": p.Size = (int)D(v); break;
                    case "dayBreak": p.DayBreak = (int)D(v); break;
                    case "prio": p.Prio = (int)D(v); break;
                    case "pre": p.Pre = (int)D(v); break;
                    case "day2": p.Day2 = v.GetBoolean(); break;
                    case "smart": p.Smart = v.GetBoolean(); break;
                    case "arTogether": p.ArTogether = v.GetBoolean(); break;
                    case "auto": p.Auto = v.GetBoolean(); break;
                    case "improve": p.Improve = v.GetBoolean(); break;
                    case "published": p.Published = v.GetBoolean(); break;
                    case "hasResults": p.HasResults = v.GetBoolean(); break;
                    case "order": p.Order = v.GetString()!; break;
                    case "champ": p.Champ = v.ValueKind == JsonValueKind.Null ? null : v.GetString(); break;
                    case "keepPolicy": p.KeepPolicy = v.GetString()!; break;
                    case "iters": p.Iters = (int)D(v); break;
                    case "fill": p.Fill = v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()); break;
                    case "groupSize": p.GroupSize = v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()); break;
                    case "groupInterval": p.GroupInterval = v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()); break;
                    case "groupStart": p.GroupStart = v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()); break;
                    case "locked": p.Locked = v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()); break;
                    case "dns": p.Dns = v.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()); break;
                    case "reserves": p.Reserves = v.EnumerateArray().Select(x => x.GetInt32()).ToHashSet(); break;
                    case "early": p.Early = v.EnumerateArray().Select(x => x.GetInt32()).ToHashSet(); break;
                    case "late": p.Late = v.EnumerateArray().Select(x => x.GetInt32()).ToHashSet(); break;
                    case "patrolIds": p.PatrolIds = v.EnumerateArray().Select(x => x.GetInt32()).ToHashSet(); break;
                    case "waived": p.Waived = v.EnumerateArray().Select(x => x.GetInt32()).ToHashSet(); break;
                    case "jobs": p.Jobs = v.EnumerateObject().ToDictionary(x => int.Parse(x.Name), x => x.Value.EnumerateArray().Select(a => (a[0].GetDouble(), a[1].GetDouble())).ToList()); break;
                    case "st": p.St = v.EnumerateArray().Select(s => new StationCfg { T = s.GetProperty("t").ValueKind == JsonValueKind.Null ? null : s.GetProperty("t").GetDouble(), Sec = s.GetProperty("sec").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.GetDouble()) }).ToList(); break;
                }
            }
            return p;
        }

        static List<EPatrol> PatrolsOf(JsonElement j) => j.EnumerateArray().Select(q => new EPatrol
        {
            N = q.GetProperty("n").GetInt32(), S = D(q.GetProperty("s")), E = q.TryGetProperty("e", out var e) ? D(e) : 0,
            P = q.GetProperty("p").EnumerateArray().Select(z => new Start(z[0].GetInt32(), z[1].GetString()!)).ToList(),
        }).ToList();

        /// <summary>Kör ett fall som produktionen gör (NextFirst på, som i adaptern). Ger resultatet och, för "keep"-fall, de tre räknarna.</summary>
        static (PlanResult G, int? Kept, int? Moved, int? Unlocked) Run(JsonElement c)
        {
            var ds = c.GetProperty("ds"); var id = ds.GetProperty("id").GetString()!;
            var patrols = PatrolsOf(ds.GetProperty("patrols"));
            var p = ParamsOf(c.GetProperty("params")); p.NextFirst = true;
            if (c.GetProperty("kind").GetString() == "keep")
            {
                var ch = c.GetProperty("change");
                var add = ch.GetProperty("add").EnumerateArray().Select(z => new Start(z[0].GetInt32(), z[1].GetString()!)).ToList();
                var rem = ch.GetProperty("remove").EnumerateArray().Select(x => x.GetString()!).ToHashSet();
                var k = FaltPatrolEngine.RebuildKeep(id, patrols, p, add, rem);
                return (k.G, k.Kept, k.Moved, k.Unlocked);
            }
            return (FaltPatrolEngine.Generate(id, patrols, p), null, null, null);
        }

        static string SourceGoldenPath([System.Runtime.CompilerServices.CallerFilePath] string here = "") => Path.Combine(Path.GetDirectoryName(here)!, "Faltskytte", "golden-patrol-engine.json");

        /// <summary>
        /// Skapar om facit ur C#-motorn. Körs bara med HPSK_REGEN_GOLDEN=1, och ENDAST efter att en avsiktlig ändring i motorns beteende har granskats
        /// (jämför före och efter med git diff på filen). Indata (listor och inställningar) rörs inte, bara "out".
        /// </summary>
        [Fact]
        public void Regenerate_golden_from_the_engine_only_when_asked()
        {
            if (Environment.GetEnvironmentVariable("HPSK_REGEN_GOLDEN") != "1") return;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(GoldenPath));
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                w.WriteStartArray();
                foreach (var c in doc.RootElement.EnumerateArray())
                {
                    var (g, kept, moved, unlocked) = Run(c);
                    w.WriteStartObject();
                    foreach (var pr in c.EnumerateObject()) { if (pr.Name == "out") continue; w.WritePropertyName(pr.Name); pr.Value.WriteTo(w); }
                    w.WritePropertyName("out"); w.WriteStartObject();
                    w.WritePropertyName("patrols"); w.WriteStartArray();
                    foreach (var q in g.Patrols)
                    {
                        w.WriteStartObject(); w.WriteNumber("n", q.N); w.WriteNumber("s", q.S);
                        w.WritePropertyName("p"); w.WriteStartArray();
                        foreach (var s in q.P) { w.WriteStartArray(); w.WriteNumberValue(s.Id); w.WriteStringValue(s.G); w.WriteEndArray(); }
                        w.WriteEndArray(); w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteBoolean("infeasible", g.Infeasible); w.WriteNumber("hard", g.Q?.Hard ?? 0); w.WriteNumber("soft", g.Q?.Soft ?? 0);
                    if (g.Blocked == null) w.WriteNull("blocked"); else w.WriteString("blocked", g.Blocked);
                    if (kept != null) { w.WriteNumber("kept", kept.Value); w.WriteNumber("moved", moved!.Value); w.WriteNumber("unlocked", unlocked!.Value); }
                    w.WriteEndObject(); w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            File.WriteAllBytes(SourceGoldenPath(), ms.ToArray());
        }

        [Theory]
        [MemberData(nameof(Cases))]
        public void Engine_matches_the_golden_master(string name)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(GoldenPath));
            var c = doc.RootElement.EnumerateArray().First(x => x.GetProperty("name").GetString() == name);
            var exp = c.GetProperty("out");
            var (g, kept, moved, unlocked) = Run(c);
            if (kept != null)
            {
                Assert.Equal(exp.GetProperty("kept").GetInt32(), kept);
                Assert.Equal(exp.GetProperty("moved").GetInt32(), moved);
                Assert.Equal(exp.GetProperty("unlocked").GetInt32(), unlocked);
            }

            var ep = exp.GetProperty("patrols").EnumerateArray().ToList();
            Assert.Equal(ep.Count, g.Patrols.Count);
            for (var i = 0; i < ep.Count; i++)
            {
                Assert.True(Math.Abs(D(ep[i].GetProperty("s")) - g.Patrols[i].S) < 1e-9, $"{name}: patrull {i + 1} start {g.Patrols[i].S} väntade {D(ep[i].GetProperty("s"))}");
                var want = ep[i].GetProperty("p").EnumerateArray().Select(z => z[0].GetInt32() + "|" + z[1].GetString()).ToList();
                var got = g.Patrols[i].P.Select(z => z.Key).ToList();
                Assert.True(want.SequenceEqual(got), $"{name}: patrull {i + 1} har andra skyttar: väntade [{string.Join(",", want)}] fick [{string.Join(",", got)}]");
            }
            Assert.Equal(exp.GetProperty("infeasible").GetBoolean(), g.Infeasible);
            Assert.Equal(exp.GetProperty("hard").GetInt32(), g.Q?.Hard ?? 0);
            if (g.Q != null) Assert.True(Math.Abs(D(exp.GetProperty("soft")) - g.Q.Soft) < 1e-6, $"{name}: soft {g.Q.Soft} väntade {D(exp.GetProperty("soft"))}");
            Assert.Equal(exp.GetProperty("blocked").ValueKind == JsonValueKind.Null ? null : exp.GetProperty("blocked").GetString(), g.Blocked);
        }
    }
}
