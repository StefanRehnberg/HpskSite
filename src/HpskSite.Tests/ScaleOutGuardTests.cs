using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using FluentAssertions;
using HpskSite.Services.Firearms;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Skalplanen D4 (2026-10-19): bakom en lastbalanserare är förfrågans Scheme och Host den INTERNA
    /// adressen, så en QR-kod eller mejllänk byggd ur dem pekar fel. Allt går genom
    /// <c>Request.PublicBase()</c> (som utan <c>ScaleOut:PublicBaseUrl</c> svarar som förut).
    /// Undantag: <c>scaleout-ok: &lt;skäl&gt;</c> på raden.
    /// </summary>
    public class ScaleOutGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly Regex SchemeFromRequest = new(@"\b(Request|req|request)\.Scheme\b", RegexOptions.Compiled);

        [Fact]
        public void Ingen_adress_byggs_ur_förfrågans_scheme_och_host()
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in Files(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (rel == "Services/Hosting/PublicUrls.cs") continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("///") || t.StartsWith("*") || lines[i].Contains("scaleout-ok:")) continue;
                    if (SchemeFromRequest.IsMatch(lines[i])) hits.Add($"{rel}:{i + 1}: {t}");
                }
            }
            Assert.True(hits.Count == 0, "Adress byggd ur förfrågan — använd Request.PublicBase():\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Etikettadressen_är_densamma_ur_bas_som_ur_schema_och_värd() =>
            FirearmLabelCode.Url("https://pistol.nu/", "AB12CD34EF")
                .Should().Be(FirearmLabelCode.Url("https", "pistol.nu", "AB12CD34EF"));

        private static IEnumerable<string> Files(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var sub in Directory.GetDirectories(dir))
                    if (!SkipDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
                foreach (var f in Directory.GetFiles(dir))
                    if (f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase))
                        yield return f;
            }
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "") =>
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "HpskSite");
    }
}
