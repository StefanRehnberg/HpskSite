using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ FILLAGRINGSSPÄRREN (skalplanen D3, 2026-10-19). Sajten får inte skriva filer på serverns disk
    /// förbi lagringen — en sådan fil finns bara på en server, försvinner vid en flytt och syns inte för en
    /// andra instans.
    /// <list type="bullet">
    /// <item>Sajtens egna filer: <c>IFileStore</c> (områden i <c>FileStoreAreas</c>).</item>
    /// <item>Filer under <c>/media/…</c>: <c>MediaFiles</c> (Umbracos mediafilsystem).</item>
    /// </list>
    /// Fäller bygget på filskrivningar och på sökvägar till <c>App_Data</c>/<c>wwwroot</c> utanför de
    /// tillåtna filerna. Ett motiverat undantag får <c>filestore-ok: &lt;skäl&gt;</c> på raden.
    /// </summary>
    public class FileStorageGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        /// <summary>Filerna som FÅR röra disken direkt, och varför.</summary>
        private static readonly string[] Allowed =
        {
            "Services/Storage/DiskFileStore.cs",               // lagringen själv
            "Services/Storage/FileStorageMigrationService.cs", // läser wwwroot/media för flytten
            "Services/AiChat/AiChatLogService.cs",             // de gamla loggfilerna (övergången)
            "Program.cs",                                      // DataProtection-nycklarnas reservkatalog
        };

        private static readonly Regex Writes = new(
            @"FileMode\.(Create|CreateNew|Append|OpenOrCreate)|File\.(WriteAll|AppendAll|Copy\(|Move\(|Delete\()|Directory\.(CreateDirectory|Delete)\(",
            RegexOptions.Compiled);

        private static readonly Regex Paths = new(
            @"""App_Data""|""wwwroot""|WebRootPath",
            RegexOptions.Compiled);

        [Fact]
        public void Ingen_kod_skriver_filer_förbi_lagringen()
        {
            var hits = Scan(Writes);
            Assert.True(hits.Count == 0,
                "Filskrivning förbi lagringen — använd IFileStore (sajtens filer) eller MediaFiles (/media):\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Ingen_kod_bygger_sökvägar_till_App_Data_eller_wwwroot()
        {
            var hits = Scan(Paths);
            Assert.True(hits.Count == 0,
                "Sökväg till App_Data/wwwroot utanför lagringen — lägg ett område i FileStoreAreas i stället:\n" + string.Join("\n", hits));
        }

        private static List<string> Scan(Regex pattern)
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                if (Allowed.Contains(rel, StringComparer.OrdinalIgnoreCase)) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///") || trimmed.StartsWith("@*")) continue;
                    if (lines[i].Contains("filestore-ok:")) continue;
                    if (pattern.IsMatch(lines[i])) hits.Add($"{rel}:{i + 1}: {trimmed}");
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
