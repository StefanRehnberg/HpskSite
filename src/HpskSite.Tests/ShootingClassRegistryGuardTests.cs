using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ THE GUARD THAT KEEPS SHOOTING CLASSES IN ONE PLACE.
    ///
    /// Every class list, dropdown, label and rule must come from <see cref="ShootingClasses"/>
    /// (server) or <c>_ShootingClassesBootstrap.cshtml</c> (browser). This test scans the site's
    /// source and fails on a hardcoded class literal:
    ///   • a quoted class Id with an underscore ("C_Vet_Y", 'A_opt_1'),
    ///   • a quoted class Name that differs from its Id ("C Vet Ä", "AM1"),
    ///   • three or more quoted plain Ids on one line (a list: "C1", "C2", "C3"),
    ///   • an option written by hand (value="C1").
    /// Comments are ignored. Before this guard existed the same bug — an Id shown to a person, or a
    /// class missing from one of many hand-written lists — was fixed and reintroduced repeatedly.
    ///
    /// A genuine exception needs a stated reason: a file in <see cref="AllowedFiles"/>, or a
    /// marker <c>class-literal-ok: &lt;reason&gt;</c> on the line.
    /// </summary>
    public class ShootingClassRegistryGuardTests
    {
        private static readonly Dictionary<string, string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Models/ShootingClass.cs"] = "the registry itself",
            ["Controllers/AnkeborgSeedController.cs"] = "dev seeder writing fixture data",
            ["Controllers/SsmPrecisionSeedController.cs"] = "dev seeder mapping an external export's codes",
            ["Models/RecordClassRegistry.cs"] = "record-CATEGORY codes (C_VetY, C_Dam) — a separate code system, not class ids",
            ["CompetitionTypes/MagnumPrecision/Services/MagnumPrecisionStandardMedalService.cs"] = "SHB medal thresholds per magnum class",
        };

        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins" };

        private static readonly Regex QuotedLiteral = new(@"([""'`])([^""'`\r\n]{1,12})\1", RegexOptions.Compiled);

        [Fact]
        public void NoHardcodedShootingClassLiterals()
        {
            var special = new HashSet<string>(StringComparer.Ordinal);
            var plain = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sc in ShootingClasses.All)
            {
                if (sc.Id.Contains('_')) special.Add(sc.Id); else plain.Add(sc.Id);
                if (sc.Name != sc.Id) special.Add(sc.Name);
            }
            var optionPattern = new Regex(@"value=""(" + string.Join("|", plain.Select(Regex.Escape)) + @")""");

            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (AllowedFiles.ContainsKey(rel)) continue;

                var text = File.ReadAllText(file, Encoding.UTF8);
                var rawLines = text.Replace("\r\n", "\n").Split('\n');
                var lineNo = 0;
                foreach (var line in CodeLines(text))
                {
                    lineNo++;
                    // The opt-out marker lives in a comment, so it is read off the raw line.
                    if (line == null || rawLines[lineNo - 1].Contains("class-literal-ok:")) continue;

                    var values = QuotedLiteral.Matches(line).Select(m => m.Groups[2].Value).ToList();
                    var specialHits = values.Where(special.Contains).ToList();
                    var plainHits = values.Where(plain.Contains).Distinct().ToList();
                    if (specialHits.Count > 0 || plainHits.Count >= 3 || optionPattern.IsMatch(line))
                        hits.Add($"{rel}:{lineNo}: {line.Trim()}");
                }
            }

            Assert.True(hits.Count == 0,
                "Hardcoded shooting-class literals found. Read them from ShootingClasses / " +
                "window.getShootingClassName instead (see Models/ShootingClass.cs):\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Guard_CatchesTheShapesItClaimsTo()
        {
            // Proves the scanner can fail — a guard that cannot fail is worse than none.
            var lines = CodeLines("var a = \"C_Vet_Y\";\n// \"C_Vet_Y\" in a comment\n/* 'C Vet Ä' */\nvar b = ['C1', 'C2', 'C3'];").ToList();
            Assert.Equal("var a = \"C_Vet_Y\";", lines[0]);
            Assert.Null(lines[1]);
            Assert.Null(lines[2]);
            Assert.Equal(3, QuotedLiteral.Matches(lines[3]!).Count);
        }

        /// <summary>The file's lines with comments blanked out (null = nothing but comment). Line numbers are kept.</summary>
        private static IEnumerable<string?> CodeLines(string text)
        {
            var inBlock = (string?)null; // the closing token of the open block comment
            foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                var sb = new StringBuilder();
                var i = 0;
                int dq = 0, sq = 0; // quotes seen so far on the line
                bool At(string token) => string.CompareOrdinal(raw, i, token, 0, token.Length) == 0;
                while (i < raw.Length)
                {
                    if (inBlock != null)
                    {
                        var end = raw.IndexOf(inBlock, i, StringComparison.Ordinal);
                        if (end < 0) { i = raw.Length; break; }
                        i = end + inBlock.Length;
                        inBlock = null;
                        continue;
                    }
                    if (At("/*")) { inBlock = "*/"; i += 2; continue; }
                    if (At("@*")) { inBlock = "*@"; i += 2; continue; }
                    if (At("<!--")) { inBlock = "-->"; i += 4; continue; }
                    // A line comment: only when it starts the line or follows whitespace, and is not
                    // inside a string (an even number of quotes before it) — keeps "https://" intact.
                    if (At("//") && (sb.Length == 0 || char.IsWhiteSpace(sb[^1])) && dq % 2 == 0 && sq % 2 == 0)
                        break;
                    var ch = raw[i];
                    if (ch == '"') dq++; else if (ch == '\'') sq++;
                    sb.Append(ch);
                    i++;
                }
                var code = sb.ToString();
                var trimmed = code.TrimStart();
                yield return trimmed.Length == 0 || trimmed.StartsWith("*") ? null : code;
            }
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
                    if (f.EndsWith(".cs") || f.EndsWith(".cshtml") || f.EndsWith(".js"))
                        yield return f;
            }
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "")
        {
            // src/HpskSite.Tests/<this file> → src/HpskSite
            var src = Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!;
            var site = Path.Combine(src, "HpskSite");
            Assert.True(Directory.Exists(site), $"Site source not found at {site}");
            return site;
        }
    }
}
