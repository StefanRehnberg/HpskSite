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
    /// ⚠️⚠️ MÄSTERSKAPSSPÄRREN (utanför mästerskapet, S2). Allt som beror på om och hur en tävling är ett mästerskap går genom
    /// <c>ChampionshipRule</c> — aldrig genom en egen läsning av <c>competitionScope</c>. Femtio ställen läste omfattningen var för sig
    /// (några normaliserade, några råa); nästa steg, vilka vapengrupper som är mästerskap och vilka som tävlar utanför, går bara att göra på ETT ställe.
    ///
    /// <para>Fäller bygget på: <c>IsChampionshipScope(</c>, <c>ChampionshipCategory.IsChampionship(</c>, <c>.SplitsGroupC(</c>, <c>ShouldSplitGroupC(</c>,
    /// <c>MedalGrouping.PerWeaponGroup(</c>/<c>CanChoose(</c> och jämförelser mot SM/landsdel/krets/klubb-konstanterna eller strängarna.</para>
    ///
    /// <para><b>Stannar på NIVÅN</b> (adress, märken, kalender, granskningens åtkomst, hänvisningar): filerna i <see cref="LevelFiles"/> frågar
    /// "vilken nivå är tävlingen", inte "hur räknas mästerskapet", och får läsa omfattningen. En ny sådan fil läggs dit MED skäl, eller
    /// raden märks <c>championshiprule-ok: skäl</c>.</para>
    /// </summary>
    public class ChampionshipRuleGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        /// <summary>Reglerna själva och de filer som DEFINIERAR läsningarna.</summary>
        private static readonly string[] RuleFiles =
        {
            Path.Combine("CompetitionTypes", "Common", "ChampionshipRule.cs"),
            Path.Combine("CompetitionTypes", "Common", "ChampionshipCategory.cs"),
            Path.Combine("CompetitionTypes", "Common", "MedalGrouping.cs"),
            Path.Combine("CompetitionTypes", "Common", "Utilities", "CompetitionScopeHelper.cs"),
            Path.Combine("CompetitionTypes", "Precision", "Services", "StandardMedalCalculationService.cs"),   // ShouldSplitGroupC delegerar till regeln
        };

        /// <summary>Nivåfrågor — INTE mästerskapets räkneregler. Varje rad har sitt skäl.</summary>
        private static readonly Dictionary<string, string> LevelFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("CompetitionTypes", "Common", "CompetitionLevel.cs")] = "nivåordlistan (adress, märken)",
            [Path.Combine("Routing", "CompetitionUrlContentFinder.cs")] = "adressen byggs ur nivån",
            [Path.Combine("Services", "Addresses", "CompetitionAddressService.cs")] = "adressen byggs ur nivån",
            [Path.Combine("Services", "MarkenCompetitionService.cs")] = "märken ges per nivå",
            [Path.Combine("Services", "MedalHandoutService.cs")] = "utdelningslistan väljs per nivå (konstanttilldelning)",
            [Path.Combine("Services", "Kretsgranskning", "KretsPlanningService.cs")] = "kretsens planering per nivå",
            [Path.Combine("Services", "Kretsgranskning", "KretsCalendarService.cs")] = "kretskalendern per nivå",
            [Path.Combine("Services", "Kretsgranskning", "CourseReviewAccess.cs")] = "vem som granskar banan, per nivå",
            [Path.Combine("Models", "Kretsgranskning", "CompetitionApplication.cs")] = "vilka nivåer en ansökan kan gälla",
            [Path.Combine("Services", "C3", "LegacyCompetitionReadSnapshot.cs")] = "diagnostik av äldre nodläsning",
            [Path.Combine("Controllers", "AnkeborgSeedController.cs")] = "sätter ett värde (testdata), läser inte",
            [Path.Combine("Controllers", "SsmPrecisionSeedController.cs")] = "sätter ett värde (testdata), läser inte",
        };

        private static readonly Regex[] Forbidden =
        {
            new(@"\bIsChampionshipScope\(", RegexOptions.Compiled),
            new(@"\bChampionshipCategory\.IsChampionship\(", RegexOptions.Compiled),
            new(@"\.SplitsGroupC\(", RegexOptions.Compiled),
            new(@"\bShouldSplitGroupC\(", RegexOptions.Compiled),
            new(@"\bMedalGrouping\.(PerWeaponGroup|CanChoose)\(", RegexOptions.Compiled),
            // rå jämförelse mot konstanterna: == X, != X, is X, or X, Equals(.., X)
            new(@"(==|!=|\bis\b|\bor\b)\s*(HpskSite\.CompetitionTypes\.Common\.Utilities\.|CompetitionTypes\.Common\.Utilities\.)?CompetitionScopeHelper\.(SvensktM|Landsdelsm|Kretsm|Klubbm)asterskap", RegexOptions.Compiled),
            new(@"CompetitionScopeHelper\.(SvensktM|Landsdelsm|Kretsm|Klubbm)asterskap\s*(==|!=)", RegexOptions.Compiled),
            new(@"Equals\([^)]*CompetitionScopeHelper\.(SvensktM|Landsdelsm|Kretsm|Klubbm)asterskap", RegexOptions.Compiled),
            // rå jämförelse mot strängarna
            new("(==|!=)\\s*\"(Svenskt Mästerskap|Landsdelsmästerskap|Kretsmästerskap|Klubbmästerskap)\"", RegexOptions.Compiled),
            new("\\bis\\s+\"(Svenskt Mästerskap|Landsdelsmästerskap|Kretsmästerskap|Klubbmästerskap)\"", RegexOptions.Compiled),
            new("Equals\\(\\s*\"(Svenskt Mästerskap|Landsdelsmästerskap|Kretsmästerskap|Klubbmästerskap)\"", RegexOptions.Compiled),
            // switch: case X: / X =>
            new(@"\bcase\s+(HpskSite\.CompetitionTypes\.Common\.Utilities\.)?CompetitionScopeHelper\.(SvensktM|Landsdelsm|Kretsm|Klubbm)asterskap", RegexOptions.Compiled),
            new(@"CompetitionScopeHelper\.(SvensktM|Landsdelsm|Kretsm|Klubbm)asterskap\s*(=>|\bor\b)", RegexOptions.Compiled),
            // en lista av konstanterna som omfattningen slås upp i
            new(@"(new\s*\[\]|new\s+string\[\])\s*\{[^}]*CompetitionScopeHelper\.(SvensktM|Landsdelsm|Kretsm|Klubbm)asterskap", RegexOptions.Compiled),
        };

        [Fact]
        public void Masterskapsfragor_gar_genom_regeln()
        {
            var hits = Scan();
            Assert.True(hits.Count == 0,
                "Omfattningen läses direkt — gå genom ChampionshipRule (eller märk raden 'championshiprule-ok: skäl' / lägg filen bland nivåfilerna med skäl):\n"
                + string.Join("\n", hits));
        }

        /// <summary>Spärren får inte kunna falla tyst för att den skannar ingenting (golv = 95 % av filerna vid första körningen).</summary>
        [Fact]
        public void Sparren_skannar_tillrackligt_manga_filer()
        {
            var count = SourceFiles(SiteRoot()).Count();
            Assert.True(count >= 1225, $"Spärren skannade bara {count} filer — sökvägen eller undantagen är fel.");
        }

        /// <summary>Positivt kontrollprov: mönstren FÅNGAR det de ska.</summary>
        [Theory]
        [InlineData("if (CompetitionScopeHelper.IsChampionshipScope(scope)) {")]
        [InlineData("var x = ChampionshipCategory.IsChampionship(scope);")]
        [InlineData("var s = ChampionshipCategory.SplitsGroupC(scope, v);")]
        [InlineData("var s = new StandardMedalCalculationService().ShouldSplitGroupC(scope);")]
        [InlineData("var p = MedalGrouping.PerWeaponGroup(competition);")]
        [InlineData("if (!MedalGrouping.CanChoose(scope)) return;")]
        [InlineData("var sm = scope == CompetitionScopeHelper.SvensktMasterskap;")]
        [InlineData("var sm = scope is CompetitionScopeHelper.Kretsmasterskap or CompetitionScopeHelper.Landsdelsmasterskap;")]
        [InlineData("if (string.Equals(scope, CompetitionScopeHelper.Klubbmasterskap, StringComparison.Ordinal))")]
        [InlineData("var sm = scope == \"Svenskt Mästerskap\";")]
        [InlineData("if (scope is \"Svenskt Mästerskap\") {")]
        [InlineData("if (scope.Equals(\"Landsdelsmästerskap\")) {")]
        [InlineData("case CompetitionScopeHelper.Kretsmasterskap:")]
        [InlineData("var x = scope switch { CompetitionScopeHelper.Klubbmasterskap => 1, _ => 0 };")]
        [InlineData("var all = new[] { CompetitionScopeHelper.Kretsmasterskap, other }.Contains(scope);")]
        public void Monstren_fangar_det_de_ska(string line) =>
            Assert.True(Forbidden.Any(r => r.IsMatch(line)), "Mönstret fångar inte: " + line);

        [Theory]
        [InlineData("var r = ChampionshipRule.For(competition).IsChampionship;")]
        [InlineData("var r = ChampionshipRule.ForScope(scope, true).SplitsChampionshipClassC;")]
        [InlineData("CompetitionScopeHelper.All.Contains(x)")]
        [InlineData("comp.SetValue(\"competitionScope\", CompetitionScopeHelper.Klubbmasterskap);")]
        public void Monstren_fangar_inte_regelns_egna_anrop(string line) =>
            Assert.False(Forbidden.Any(r => r.IsMatch(line)), "Mönstret fångar felaktigt: " + line);

        private static List<string> Scan()
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file);
                if (RuleFiles.Any(a => string.Equals(a, rel, StringComparison.OrdinalIgnoreCase))) continue;
                if (LevelFiles.ContainsKey(rel)) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///") || trimmed.StartsWith("@*")) continue;
                    if (line.Contains("championshiprule-ok:")) continue;
                    if (Forbidden.Any(r => r.IsMatch(line))) hits.Add($"{rel}:{i + 1}: {trimmed}");
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
