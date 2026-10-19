using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ Varje källfil måste vara giltig UTF-8 (2026-10-19).
    ///
    /// <para>En fil utan BOM med EN ogiltig UTF-8-byte läser kompilatorn i sin helhet med systemets
    /// kodsida — varje sträng med å/ä/ö i filen blir tyst fel, utan kompileringsfel. Det hände tre gånger
    /// när en kommentar lades in via skalet (sed/heredoc skriver Latin-1): statistiksidans "Öppen" slog
    /// aldrig till i prod (skalplanen B), och anmälningslagringens mapp hade fått ett förvanskat namn.
    /// Skriv svensk text med redigeringsverktyget, aldrig via skalet.</para>
    /// </summary>
    public class SourceEncodingGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "umbraco", "media", "App_Data", "App_Plugins" };

        private static readonly string[] Extensions = { ".cs", ".cshtml", ".js", ".css" };

        [Fact]
        public void Varje_kallfil_ar_giltig_UTF8()
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            var root = SiteRoot();
            var bad = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                try { strict.GetString(File.ReadAllBytes(file)); }
                catch (DecoderFallbackException) { bad.Add(Path.GetRelativePath(root, file)); }
            }
            Assert.True(bad.Count == 0,
                "Filer som inte är giltig UTF-8 (kompilatorn läser då HELA filen fel):\n" + string.Join("\n", bad));
        }

        [Fact]
        public void Kontrollen_fangar_en_Latin1_byte()
        {
            var strict = new UTF8Encoding(false, true);
            var latin1 = Encoding.Latin1.GetBytes("// tomt värde");
            Assert.Throws<DecoderFallbackException>(() => strict.GetString(latin1));
            strict.GetString(Encoding.UTF8.GetBytes("// tomt värde"));
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
                    if (Extensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase)))
                        yield return f;
            }
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "")
        {
            var site = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "HpskSite");
            Assert.True(Directory.Exists(site), $"Site source not found at {site}");
            return site;
        }
    }
}
