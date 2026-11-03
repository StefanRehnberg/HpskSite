using System.Runtime.CompilerServices;
using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ ANTIFORGERY-BASLINJEN (säkerhetsplanen 2026-11-02, paket 2 / P3). Varje action som KAN anropas med POST/PUT/PATCH/DELETE
    /// (uttryckligt verb, eller inget verb alls — då svarar den på alla) ska ha ett beslut: <c>[ValidateAntiForgeryToken]</c> (anropet
    /// prövas) eller <c>[IgnoreAntiforgeryToken]</c> (medvetet fritt — mejllänk, webbhook, JWT-API — med skäl i en kommentar).
    /// De som ännu saknar beslut står i <c>antiforgery-baseline.txt</c>; filtret <see cref="AntiforgeryLogFilter"/> loggar dem när ett
    /// anrop saknar giltig token. Testet fäller bygget för en NY action utan beslut, och för en rad i listan som inte längre behövs
    /// (listan får bara krympa).
    /// Skapa om: <c>HPSK_REGEN_BASELINE=1 dotnet test -c Release --filter AntiforgeryCoverageTests</c>.
    /// </summary>
    public class AntiforgeryCoverageTests
    {
        private static string BaselinePath([CallerFilePath] string thisFile = "") =>
            Path.Combine(Path.GetDirectoryName(thisFile)!, "antiforgery-baseline.txt");

        private static SortedSet<string> Uncovered() =>
            new(AntiforgeryCoverage.Uncovered(typeof(AntiforgeryLogFilter).Assembly).Select(EndpointAuthClassifier.Key), StringComparer.Ordinal);

        [Fact]
        public void Skrivande_actions_utan_beslut_ar_exakt_baslinjen()
        {
            var actual = Uncovered();
            if (Environment.GetEnvironmentVariable("HPSK_REGEN_BASELINE") == "1")
            {
                File.WriteAllLines(BaselinePath(), actual);
                return;
            }
            var baseline = new SortedSet<string>(
                File.ReadAllLines(BaselinePath()).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()), StringComparer.Ordinal);
            var added = actual.Except(baseline).ToList();
            var gone = baseline.Except(actual).ToList();
            Assert.True(added.Count == 0,
                "Nya skrivande actions utan beslut om antiforgery. Lägg [ValidateAntiForgeryToken] (och skicka token i anropet) eller [IgnoreAntiforgeryToken] med skäl:\n" +
                string.Join("\n", added));
            Assert.True(gone.Count == 0,
                "Dessa har nu ett beslut (eller är borta) — ta bort dem ur antiforgery-baseline.txt:\n" + string.Join("\n", gone));
        }

        [Fact]
        public void Verb_och_beslut_kanns_igen()
        {
            var t = typeof(Fixture);
            Assert.True(AntiforgeryCoverage.IsUnsafeVerb(t.GetMethod(nameof(Fixture.Post))!));
            Assert.False(AntiforgeryCoverage.IsUnsafeVerb(t.GetMethod(nameof(Fixture.Get))!));
            Assert.True(AntiforgeryCoverage.IsUnsafeVerb(t.GetMethod(nameof(Fixture.NoVerb))!)); // inget verb angivet = svarar även på POST
            Assert.False(AntiforgeryCoverage.HasDecision(t.GetMethod(nameof(Fixture.Post))!));
            Assert.True(AntiforgeryCoverage.HasDecision(t.GetMethod(nameof(Fixture.Validated))!));
            Assert.True(AntiforgeryCoverage.HasDecision(t.GetMethod(nameof(Fixture.Ignored))!));
        }

        [Fact]
        public void Lagen_tolkas()
        {
            Assert.Equal("log", AntiforgeryLogFilter.ModeOf(null));
            Assert.Equal("enforce", AntiforgeryLogFilter.ModeOf("Enforce"));
            Assert.Equal("off", AntiforgeryLogFilter.ModeOf("off"));
        }

        public class Fixture : Microsoft.AspNetCore.Mvc.ControllerBase
        {
            [Microsoft.AspNetCore.Mvc.HttpPost] public void Post() { }
            [Microsoft.AspNetCore.Mvc.HttpGet] public void Get() { }
            public void NoVerb() { }
            [Microsoft.AspNetCore.Mvc.HttpPost, Microsoft.AspNetCore.Mvc.ValidateAntiForgeryToken] public void Validated() { }
            [Microsoft.AspNetCore.Mvc.HttpPost, Microsoft.AspNetCore.Mvc.IgnoreAntiforgeryToken] public void Ignored() { }
        }
    }
}
