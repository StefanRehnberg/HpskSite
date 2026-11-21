using System.Text.Json;
using FluentAssertions;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;
using static HpskSite.CompetitionTypes.Faltskytte.Services.ShbFieldRules;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ C#-REGLERNA MOT KONFIGURATORNS EGEN JAVASCRIPT. Fixturen är 400 slumpade stationer som
    /// <c>Fixtures/shb-parity-gen.mjs</c> kört genom <c>faltSuggestShootingTime</c> i
    /// <c>_FaltskytteConfiguratorScript.cshtml</c>. Ändras regeln i konfiguratorn: kör om skriptet
    /// (<c>node Fixtures/shb-parity-gen.mjs</c>) och rätta <see cref="ShbFieldRules"/> tills testet är
    /// grönt — annars säger granskarens checklista något annat än banläggarens skärm.
    /// </summary>
    public class ShbFieldRulesParityTests
    {
        private record FigureJ(int? sizeGroup, int targetsPerFigure);
        private record GroupJ(int? distance, List<FigureJ> figures);
        private record StationJ(int shootingTimeSec, string? supportHand, string? weaponStartPosition,
            int minShotsPerFigure, int maxShotsPerFigure, List<GroupJ> targetGroups);
        private record CaseJ(StationJ st, string wc, bool morker, bool poang, int? expected);

        private record TableJ(Dictionary<string, string> buckets, Dictionary<string, string> labels);

        private static TableJ Table()
        {
            var asm = typeof(ShbFieldRulesParityTests).Assembly;
            var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("shb-parity-table.json"));
            using var s = asm.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<TableJ>(s)!;
        }

        /// <summary>Väljarens rubrik, byggd ur C#-tabellen: "A&amp;R 70 m · B 80 m · C 90 m · M 180 m"; tom för grupp utan rad.</summary>
        private static string ExpectedLabel(int g)
        {
            var parts = new List<string>();
            foreach (var (text, wc) in new[] { ("A&R", "A"), ("B", "B"), ("C", "C"), ("M", "M") })
            {
                var d = MaxDistance(g, wc);
                if (d is > 0) parts.Add($"{text} {d} m");
            }
            return string.Join(" · ", parts);
        }

        [Fact]
        public void FigurvaljarensRubriker_ArSammaSomCSharpTabellen_ForGrupp0Till15()
        {
            var t = Table();
            t.labels.Should().HaveCount(16);
            for (var g = 0; g <= 15; g++)
                t.labels[g.ToString()].Should().Be(ExpectedLabel(g), $"rubriken för storleksgrupp {g}");
            t.labels["15"].Should().BeEmpty();
            t.labels["1"].Should().EndWith("M 180 m");
            t.labels["14"].Should().Be("A&R 7 m · B 8 m · C 9 m · M 17 m");
        }

        [Fact]
        public void VapenklassensHink_ArSammaIJsOchCSharp()
        {
            var t = Table();
            t.buckets.Should().HaveCountGreaterThan(10);
            foreach (var (wc, bucket) in t.buckets)
                Bucket(wc == "" ? null : wc).Should().Be(bucket, $"hink för vapenklass '{wc}'");
        }

        [Fact]
        public void TabellenSomGesUtTillWebblasaren_ArDenFullaCSharpTabellen()
        {
            using var doc = JsonDocument.Parse(TableJson());
            var rows = doc.RootElement.GetProperty("maxDistances");
            var seen = 0;
            for (var g = 0; g <= 15; g++)
            {
                var has = rows.TryGetProperty(g.ToString(), out var row);
                has.Should().Be(MaxDistance(g, "A") != null, $"grupp {g} ska finnas i JSON exakt när C# har en rad");
                if (!has) continue;
                seen++;
                foreach (var (key, wc) in new[] { ("AR", "A"), ("B", "B"), ("C", "C"), ("M", "M") })
                {
                    int? json = row.GetProperty(key).ValueKind == JsonValueKind.Null ? null : row.GetProperty(key).GetInt32();
                    json.Should().Be(MaxDistance(g, wc), $"grupp {g} {key}");
                }
            }
            seen.Should().Be(15);
            var times = doc.RootElement.GetProperty("maxTimePerShot");
            times.GetProperty("AR").GetDouble().Should().Be(2.0);
            times.GetProperty("M").GetDouble().Should().Be(20);
        }

        private static List<CaseJ> Cases()
        {
            var asm = typeof(ShbFieldRulesParityTests).Assembly;
            var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("shb-parity-cases.json"));
            using var s = asm.GetManifestResourceStream(name)!;
            return JsonSerializer.Deserialize<List<CaseJ>>(s)!;
        }

        [Fact]
        public void SammaSkjuttidSomKonfiguratorn_FörVarjeFall()
        {
            var cases = Cases();
            cases.Should().HaveCountGreaterThan(300);
            var wrong = new List<string>();
            foreach (var c in cases)
            {
                var st = new Station(c.st.shootingTimeSec, c.st.supportHand, c.st.weaponStartPosition,
                    c.st.minShotsPerFigure, c.st.maxShotsPerFigure,
                    c.st.targetGroups.Select(g => new TargetGroup(g.distance,
                        g.figures.Select(f => new Figure(f.sizeGroup, f.targetsPerFigure)).ToList())).ToList());
                var got = MinimumShootingTime(st, c.wc, c.morker, c.poang);
                if (got != c.expected) wrong.Add($"{c.wc} mörker={c.morker} poäng={c.poang}: C# {got?.ToString() ?? "null"} ≠ JS {c.expected?.ToString() ?? "null"} — {JsonSerializer.Serialize(c.st)}");
            }
            wrong.Should().BeEmpty(string.Join("\n", wrong.Take(5)));
        }
    }
}
