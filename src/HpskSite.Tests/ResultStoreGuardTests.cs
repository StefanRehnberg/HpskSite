using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ RESULTATSPÄRREN (skalplanen C1, 2026-10-12). Resultatlistan läses och skrivs bara genom
    /// <c>ICompetitionResultStore</c>. Varje direktläsning av <c>competitionResult</c>-noden — eller av
    /// dess egenskaper som <c>resultData</c> — är ett ställe som slutar fungera när listorna flyttar
    /// till tabellen, och tidigare letade ytorna upp noden på fem olika sätt.
    ///
    /// <para>Tillåtna filer: lagringen själv och de två ställen som bara KÄNNER TILL doctypen
    /// (adressbyggaren och fakturasvepets lista över grenar att inte gå ned i).</para>
    /// </summary>
    public class ResultStoreGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly string[] AllowedFiles =
        {
            Path.Combine("Models", "Results", "CompetitionResultRecord.cs"),
            Path.Combine("Services", "Results", "NodeCompetitionResultStore.cs"),
            Path.Combine("Routing", "CompetitionUrlProvider.cs"),
            Path.Combine("Services", "InvoiceAdminService.cs"),
            // Den gamla hubbsidan (competitionResultsHub) — ingenting skapar hubbar längre.
            Path.Combine("Views", "CompetitionResultsHub.cshtml"),
        };

        /// <summary>Doctypens alias som en stränglitteral.</summary>
        private static readonly Regex AliasLiteral = new("\"competitionResult\"", RegexOptions.Compiled);

        /// <summary>Resultatlistans egenskaper lästa eller skrivna på en nod.</summary>
        private static readonly Regex PropertyAccess = new(
            "(GetValue|SetValue|Value|HasProperty)\\s*(<[^>]*>)?\\s*\\(\\s*\"(resultData|subCompetitionIsOfficial|subCompetitionMergeConfig|classNameOverrides|subCompetitionClassNameOverrides|faltskytteShootOffConfig|subCompetitionFaltskytteShootOffConfig|honoraryAwardConfig)\"",
            RegexOptions.Compiled);

        [Fact]
        public void Resultatnoden_lases_bara_genom_lagringen()
        {
            var hits = Scan(line => AliasLiteral.IsMatch(line));
            Assert.True(hits.Count == 0,
                "competitionResult-noden läses direkt — gå genom ICompetitionResultStore:\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Resultatlistans_egenskaper_lases_bara_genom_lagringen()
        {
            var hits = Scan(line => PropertyAccess.IsMatch(line));
            Assert.True(hits.Count == 0,
                "Resultatlistans egenskaper läses eller skrivs på en nod — gå genom ICompetitionResultStore:\n" + string.Join("\n", hits));
        }

        private static List<string> Scan(Func<string, bool> match)
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file);
                if (AllowedFiles.Any(a => string.Equals(a, rel, StringComparison.OrdinalIgnoreCase))) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                    if (line.Contains("resultstore-ok:")) continue;
                    if (match(line)) hits.Add($"{rel}:{i + 1}: {trimmed}");
                }
            }
            return hits;
        }

        private static IEnumerable<string> SourceFiles(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var sub in Directory.GetDirectories(dir))
                    if (!SkipDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        stack.Push(sub);
                foreach (var f in Directory.GetFiles(dir))
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                        yield return f;
            }
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "")
        {
            var src = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
            var site = Path.Combine(src, "HpskSite");
            Assert.True(Directory.Exists(site), $"Site source not found at {site}");
            return site;
        }
    }
}
