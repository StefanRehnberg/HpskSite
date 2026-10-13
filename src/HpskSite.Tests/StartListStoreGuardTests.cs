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
    /// ⚠️⚠️ STARTLISTESPÄRREN (skalplanen C2, 2026-10-12). Startlistorna läses och skrivs bara genom
    /// <c>IStartListStore</c>. Varje direktläsning av en <c>precisionStartList</c>-, <c>finalsStartList</c>-
    /// eller hubbnod — eller av listans egenskaper som <c>configurationData</c> — är ett ställe som slutar
    /// fungera när listorna flyttar till tabellen. Före sömmen letade ett sextiotal ställen upp noderna själva.
    ///
    /// <para>Tillåtna filer: lagringen själv och de två ställen som bara KÄNNER TILL doctyperna
    /// (adressbyggaren och fakturasvepets lista över grenar att inte gå ned i).</para>
    /// </summary>
    public class StartListStoreGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly string[] AllowedFiles =
        {
            Path.Combine("Models", "StartLists", "StartListRecord.cs"),
            Path.Combine("Services", "StartLists", "NodeStartListStore.cs"),
            Path.Combine("Routing", "CompetitionUrlProvider.cs"),
            Path.Combine("Services", "InvoiceAdminService.cs"),
            // Den gamla hubbsidan (competitionStartListsHub) — ingenting skapar hubbar längre.
            Path.Combine("Views", "CompetitionStartListsHub.cshtml"),
        };

        /// <summary>Doctypernas alias som stränglitteraler.</summary>
        private static readonly Regex AliasLiteral = new(
            "\"(precisionStartList|PrecisionStartList|precision-start-list|finalsStartList|competitionStartListsHub)\"",
            RegexOptions.Compiled);

        /// <summary>Startlistans egenskaper lästa eller skrivna på en nod.</summary>
        private static readonly Regex PropertyAccess = new(
            "(GetValue|SetValue|Value|HasProperty)\\s*(<[^>]*>)?\\s*\\(\\s*\"(configurationData|startListContent|isOfficialStartList|isOfficialFinalsStartList|perClassConfigData|qualifyingResultsSnapshot|totalFinalists|maxShootersPerTeam|qualificationStartListId)\"",
            RegexOptions.Compiled);

        [Fact]
        public void Startlistornas_noder_lases_bara_genom_lagringen()
        {
            var hits = Scan(line => AliasLiteral.IsMatch(line));
            Assert.True(hits.Count == 0,
                "En startlistenod läses direkt — gå genom IStartListStore:\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Startlistornas_egenskaper_lases_bara_genom_lagringen()
        {
            var hits = Scan(line => PropertyAccess.IsMatch(line));
            Assert.True(hits.Count == 0,
                "Startlistans egenskaper läses eller skrivs på en nod — gå genom IStartListStore:\n" + string.Join("\n", hits));
        }

        /// <summary>Positivt kontrollprov: mönstren FÅNGAR det de ska — annars kan spärren inte falla.</summary>
        [Fact]
        public void Monstren_fangar_det_de_ska()
        {
            Assert.Matches(AliasLiteral, "var lists = children.Where(c => c.ContentType.Alias == \"precisionStartList\");");
            Assert.Matches(AliasLiteral, "if (node.ContentType.Alias == \"finalsStartList\") return;");
            Assert.Matches(AliasLiteral, "var hub = children.FirstOrDefault(c => c.ContentType.Alias == \"competitionStartListsHub\");");
            Assert.Matches(PropertyAccess, "var cfg = node.GetValue<string>(\"configurationData\");");
            Assert.Matches(PropertyAccess, "node.SetValue(\"isOfficialStartList\", true);");
            Assert.Matches(PropertyAccess, "var html = sl.Value<string>(\"startListContent\");");
            Assert.DoesNotMatch(PropertyAccess, "var cfg = record.ConfigurationData;");
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
                    if (line.Contains("startliststore-ok:")) continue;
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
