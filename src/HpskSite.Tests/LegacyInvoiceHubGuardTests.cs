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
    /// ⚠️ FAKTURAMAPPSPÄRREN (C3-planen F1, 2026-10-13). Den gamla betalmodellens fakturamapp hittas bara
    /// genom <c>LegacyInvoiceHubs</c>. Mapparna flyttas till ett arkiv innan tävlingarna lämnar trädet; ett
    /// uppslag bland tävlingens barn hittar då ingenting. Ett motiverat undantag får
    /// <c>invoicehub-ok: &lt;skäl&gt;</c> på raden.
    /// </summary>
    public class LegacyInvoiceHubGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly string[] AllowedFiles =
        {
            Path.Combine("Services", "LegacyInvoiceHubs.cs"),
            // Svepet över hela trädet (samlar mapparna var de än ligger) och dess lista över grenar.
            Path.Combine("Services", "InvoiceAdminService.cs"),
            // Skapandet av mappen (bara den gamla modellens två kvarvarande tävlingar) och namnreserven.
            Path.Combine("Services", "PaymentService.cs"),
        };

        private static readonly Regex HubAlias = new("\"registrationInvoicesHub\"", RegexOptions.Compiled);

        [Fact]
        public void Fakturamappen_hittas_bara_genom_LegacyInvoiceHubs()
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
                    var t = lines[i].TrimStart();
                    if (t.StartsWith("//") || t.StartsWith("*") || t.StartsWith("///")) continue;
                    if (lines[i].Contains("invoicehub-ok:")) continue;
                    if (HubAlias.IsMatch(lines[i])) hits.Add($"{rel}:{i + 1}: {t}");
                }
            }
            Assert.True(hits.Count == 0, "Fakturamappen slås upp direkt — gå genom LegacyInvoiceHubs:\n" + string.Join("\n", hits));
        }

        /// <summary>Positivt kontrollprov: mönstret fångar ett direkt uppslag.</summary>
        [Fact]
        public void Monstret_fangar_ett_direkt_uppslag()
        {
            Assert.Matches(HubAlias, "var hub = children.FirstOrDefault(c => c.ContentType.Alias == \"registrationInvoicesHub\");");
            Assert.DoesNotMatch(HubAlias, "var hub = _invoiceHubs.Hub(competitionId);");
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
