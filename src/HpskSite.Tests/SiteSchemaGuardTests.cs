using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna F1: startkontrollen måste känna till varje Site*-tabell koden rör. Migreringsmappen är gitignorerad,
    /// så provet läser koden (SQL i tjänsterna), inte migreringarna.
    /// </summary>
    public class SiteSchemaGuardTests
    {
        private static string SitePagesDir([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite", "Services", "SitePages"));

        [Fact]
        public void Startkontrollen_returnerar_bara_tabeller_som_saknas()
        {
            var missing = SiteOwnerSchemaGuardHostedService.MissingTables(t => t != "dbo.SiteOwner");
            Assert.Equal(new[] { "dbo.SiteOwner" }, missing);
            Assert.Empty(SiteOwnerSchemaGuardHostedService.MissingTables(_ => true));
        }

        [Fact]
        public void Varje_Site_tabell_som_koden_nämner_står_i_startkontrollen()
        {
            var rx = new Regex(@"\bdbo\.(Site\w+)\b", RegexOptions.Compiled);
            var known = SiteOwnerSchemaGuardHostedService.RequiredTables.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknown = new SortedSet<string>();
            foreach (var file in Directory.EnumerateFiles(SitePagesDir(), "*.cs", SearchOption.AllDirectories))
            {
                foreach (var line in File.ReadAllLines(file))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("*")) continue;
                    foreach (Match m in rx.Matches(line))
                        if (!known.Contains("dbo." + m.Groups[1].Value)) unknown.Add(m.Value);
                }
            }
            Assert.True(unknown.Count == 0,
                "Site*-tabell som inte står i SiteOwnerSchemaGuardHostedService.RequiredTables: " + string.Join(", ", unknown));
        }
    }
}
