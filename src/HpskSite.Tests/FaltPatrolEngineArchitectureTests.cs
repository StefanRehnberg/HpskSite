using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Patrullmotorn är en REN funktion: ingen Umbraco, NPoco, scope, databas eller klocka. Skannar motormappen (med positivt kontrollprov så att skanningen inte kan bli tom).</summary>
    public class FaltPatrolEngineArchitectureTests
    {
        static readonly Regex Forbidden = new(@"\b(Umbraco|NPoco|IScope|IScopeProvider|System\.Data|SqlClient|IDatabase)\b|DateTime\.(Now|UtcNow|Today)|DateTimeOffset\.(Now|UtcNow)|Stopwatch|Task\.Run|HttpContext", RegexOptions.Compiled);

        static string EngineDir()
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "HpskSite", "CompetitionTypes"))) d = d.Parent;
            Assert.NotNull(d);
            return Path.Combine(d!.FullName, "src", "HpskSite", "CompetitionTypes", "Faltskytte", "Engine");
        }

        // Rader utan kommentarer.
        static IEnumerable<string> Code(string file) => File.ReadAllLines(file).Select(l => { var i = l.IndexOf("//", StringComparison.Ordinal); return i >= 0 ? l[..i] : l; });

        [Fact]
        public void Engine_folder_has_files() => Assert.NotEmpty(Directory.GetFiles(EngineDir(), "*.cs"));

        [Fact]
        public void Engine_uses_no_infrastructure_or_clock()
        {
            foreach (var f in Directory.GetFiles(EngineDir(), "*.cs"))
                foreach (var line in Code(f))
                    Assert.False(Forbidden.IsMatch(line), $"{Path.GetFileName(f)}: förbjuden referens i motorn: {line.Trim()}");
        }

        [Theory]
        [InlineData("var x = DateTime.Now;")]
        [InlineData("using Umbraco.Cms.Core;")]
        [InlineData("scope.Database.Fetch<int>(\"x\");  var s = new IScope();")]
        public void The_scan_catches_a_violation(string line) => Assert.True(Forbidden.IsMatch(line));
    }
}
