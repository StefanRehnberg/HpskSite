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
