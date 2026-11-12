using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna steg 0 (2026-11-07): användarskriven text får aldrig nå en sida utan sanering.
    /// Två invarianter i vyerna:
    /// 1. Rik text från klubbens/kretsens/tävlingens egenskaper (aboutClub, description, welcomeMessage, welcomeText,
    ///    aboutRegion) läses bara genom <c>RteHelper.SafeMarkup/SafeHtml</c> (eller <c>ExtractMarkup</c> för en redigerare).
    ///    En rå <c>Value&lt;string&gt;("aboutClub")</c> som sedan skrivs med <c>Html.Raw</c> är lagrad XSS.
    /// 2. Ett <c>Html.Raw</c> som bär användartext in i ett JavaScript-värde går genom
    ///    <c>JsonSerializer.Serialize(...)</c>, aldrig <c>'@Html.Raw(x.Replace("'", "\\'"))'</c> (ett bakåtstreck före
    ///    apostrofen eller <c>&lt;/script&gt;</c> bryter sig ur strängen).
    /// Undantag: <c>htmlraw-ok: &lt;skäl&gt;</c> på raden.
    /// </summary>
    public class HtmlRawGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly Regex RteRead = new(
            @"(Get)?Value<string>\(""(aboutClub|description|welcomeMessage|welcomeText|aboutRegion)""\)",
            RegexOptions.Compiled);

        private static readonly Regex RawWithQuoteEscape = new(
            @"Html\.Raw\([^\n]*\.Replace\(""'""\s*,\s*""\\\\'""\)",
            RegexOptions.Compiled);

        [Fact]
        public void Rik_text_ur_egenskaperna_läses_bara_genom_saneringen()
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in Views(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (line.Contains("htmlraw-ok:") || !RteRead.IsMatch(line)) continue;
                    var safe = line.Contains("SafeMarkup(") || line.Contains("SafeHtml(") || line.Contains("ExtractMarkup(");
                    if (!safe) hits.Add($"{rel}:{i + 1}: {line.Trim()}");
                }
            }
            Assert.True(hits.Count == 0,
                "Rik text läses rått — gå genom RteHelper.SafeMarkup/SafeHtml (eller ExtractMarkup för en redigerare):\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Användartext_in_i_javascript_går_genom_JsonSerializer()
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in Views(root))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("htmlraw-ok:")) continue;
                    if (RawWithQuoteEscape.IsMatch(lines[i])) hits.Add($"{rel}:{i + 1}: {lines[i].Trim()}");
                }
            }
            Assert.True(hits.Count == 0,
                "Html.Raw med handgjord apostrofflykt — använd @Html.Raw(System.Text.Json.JsonSerializer.Serialize(värde)):\n" + string.Join("\n", hits));
        }

        private static IEnumerable<string> Views(string root)
        {
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                foreach (var sub in Directory.GetDirectories(dir))
                    if (!SkipDirectories.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase)) stack.Push(sub);
                foreach (var f in Directory.GetFiles(dir))
                    if (f.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)) yield return f;
            }
        }

        private static string SiteRoot([CallerFilePath] string thisFile = "") =>
            Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(thisFile))!, "HpskSite");
    }
}
