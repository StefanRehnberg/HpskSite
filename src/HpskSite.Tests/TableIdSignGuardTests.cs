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
    /// ⚠️⚠️ TECKENSPÄRREN (skalplanen fas C, 2026-10-12). Start- och resultatlistornas tabeller ger nya rader
    /// NEGATIVA id:n (<c>NegativeIdentity</c>) så att de aldrig kan krocka med ett Umbraco-nod-id. Följden är
    /// att <c>startListId &lt;= 0</c> eller <c>&gt; 0</c> betyder "finns inte" om varje lista som skapats i
    /// tabellen — samma fälla som gav ekonomins sandlåda tre tysta fel. "Ingen lista" är 0: fråga <c>== 0</c>.
    ///
    /// <para>Ett medvetet undantag (en nodlagring, där ett negativt id aldrig är en nod) får markeringen
    /// <c>id-sign-ok: &lt;skäl&gt;</c> på raden eller raden ovanför.</para>
    /// </summary>
    public class TableIdSignGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins" };

        private static readonly Regex SignCheck = new(
            @"\b(startListId|StartListId|FinalsStartListId|finalsStartListId|resultListId|ResultListId|wantedFsl|fslId)\s*(<=|>=|<|>)\s*0\b",
            RegexOptions.Compiled);

        private static readonly Regex CreatedIdCheck = new(
            @"\b(finalsStartList|startList|saved)\s*\.\s*Id\s*(<=|<|>)\s*0\b",
            RegexOptions.Compiled);

        [Fact]
        public void Inget_startliste_id_provas_pa_sitt_tecken()
        {
            var hits = Scan(line => SignCheck.IsMatch(line) || CreatedIdCheck.IsMatch(line));
            Assert.True(hits.Count == 0,
                "Ett start- eller resultatliste-id prövas på sitt tecken — nya rader har NEGATIVA id:n (NegativeIdentity). " +
                "Fråga == 0 / != 0, eller markera en nodlagring med 'id-sign-ok:':\n" + string.Join("\n", hits));
        }

        private static List<string> Scan(Func<string, bool> match)
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                    if (line.Contains("id-sign-ok:") || (i > 0 && lines[i - 1].Contains("id-sign-ok:"))) continue;
                    if (match(line)) hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {trimmed}");
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
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                        || f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
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
