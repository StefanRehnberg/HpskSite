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
    /// ⚠️ FINALSERIESPÄRREN (utanför mästerskapet i precisionsfamiljen, förarbete K2). Finalomgång finns bara i Precision
    /// (<c>CompetitionFinals.SupportedTypes</c>), men <c>NumberOfFinalSeries</c> är ett LAGRAT värde som en annan gren kan ha kvar. Kod som
    /// avgör om en final finns — resultat, finalstartlista, prisutdelning, lag — läser därför <c>CompetitionInfo.EffectiveFinalSeries</c> (eller
    /// <c>CompetitionFinals.Effective(...)</c>), aldrig det råa värdet. Annars kan en gäst-/finalregel som bara byggts för precision av misstag
    /// slå på en gren utan final.
    ///
    /// <para>Fäller bygget på en rå <c>.NumberOfFinalSeries</c> i .cs-kod utanför <see cref="DefinitionAndEditFiles"/>. En rad får märkas
    /// <c>finalseries-ok: skäl</c>. Visningsvyer (.cshtml) skannas inte — de visar värdet och bestämmer ingenting.</para>
    /// </summary>
    public class FinalSeriesGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot", "Views" };

        /// <summary>Filer som DEFINIERAR eller REDIGERAR det lagrade värdet. Varje rad har sitt skäl.</summary>
        private static readonly Dictionary<string, string> DefinitionAndEditFiles = new(StringComparer.OrdinalIgnoreCase)
        {
            [Path.Combine("Models", "Competition.cs")] = "Umbraco-modellens egenskap (läsning av noden)",
            [Path.Combine("Models", "Competitions", "CompetitionInfo.cs")] = "definierar det lagrade värdet och EffectiveFinalSeries",
            [Path.Combine("Models", "Competitions", "CompetitionFieldTable.cs")] = "fälttabellen som läser/skriver värdet",
            [Path.Combine("Controllers", "CompetitionAdminController.cs")] = "skriver värdet vid skapande (vägras för gren utan final av CompetitionFinals.Refusal)",
            [Path.Combine("Controllers", "CompetitionEditController.cs")] = "redigeringen: visar och nollar värdet för gren utan final",
        };

        private static readonly Regex Raw = new(@"\.NumberOfFinalSeries\b", RegexOptions.Compiled);

        [Fact]
        public void Finalfragor_gar_genom_EffectiveFinalSeries()
        {
            var hits = Scan();
            Assert.True(hits.Count == 0,
                "Det lagrade antalet finalserier läses direkt — använd CompetitionInfo.EffectiveFinalSeries eller CompetitionFinals.Effective(typ, antal) "
                + "(eller märk raden 'finalseries-ok: skäl'):\n" + string.Join("\n", hits));
        }

        /// <summary>Spärren får inte kunna falla tyst för att den skannar ingenting.</summary>
        [Fact]
        public void Sparren_skannar_tillrackligt_manga_filer()
        {
            var count = SourceFiles(SiteRoot()).Count();
            Assert.True(count >= 900, $"Spärren skannade bara {count} filer — sökvägen eller undantagen är fel.");
        }

        /// <summary>Spärren finns till för att fånga något: minst ett ställe i koden använder det effektiva värdet.</summary>
        [Fact]
        public void Det_effektiva_vardet_anvands_i_final_och_resultatkoden()
        {
            var root = SiteRoot();
            var users = SourceFiles(root)
                .Where(f => File.ReadAllText(f).Contains("EffectiveFinalSeries", StringComparison.Ordinal))
                .Select(f => Path.GetRelativePath(root, f)).ToList();
            Assert.Contains(Path.Combine("CompetitionTypes", "Precision", "Services", "PrecisionResultsBuilder.cs"), users, StringComparer.OrdinalIgnoreCase);
            Assert.Contains(Path.Combine("CompetitionTypes", "Precision", "Services", "PrecisionQualifyingResultsService.cs"), users, StringComparer.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("var n = competition.NumberOfFinalSeries;")]
        [InlineData("int numFinal = comp?.NumberOfFinalSeries ?? 0;")]
        [InlineData("if (competition.NumberOfFinalSeries <= 0) return;")]
        public void Monstret_fangar_ra_lasning(string line) =>
            Assert.True(Raw.IsMatch(line), "Mönstret fångar inte: " + line);

        [Theory]
        [InlineData("var n = competition.EffectiveFinalSeries;")]
        [InlineData("NumberOfFinalSeries = numberOfFinalSeries,")]
        public void Monstret_fangar_inte_det_effektiva_vardet(string line) =>
            Assert.False(Raw.IsMatch(line), "Mönstret fångar felaktigt: " + line);

        private static List<string> Scan()
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file);
                if (DefinitionAndEditFiles.ContainsKey(rel)) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*")) continue;
                    if (!Raw.IsMatch(line)) continue;
                    if (line.Contains("finalseries-ok:")) continue;
                    // CompetitionFinals.Effective(typ, rått värde) är rätt form, även när anropet bryts över två rader.
                    var call = line + (i > 0 ? lines[i - 1] : "");
                    if (call.Contains("CompetitionFinals.Effective(", StringComparison.Ordinal)) continue;
                    hits.Add($"{rel}:{i + 1}: {trimmed}");
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
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
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
