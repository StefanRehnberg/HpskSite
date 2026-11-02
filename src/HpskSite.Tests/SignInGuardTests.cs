using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ INLOGGNINGSSPÄRREN (säkerhetsplanen 2026-11-02, H2/H2b). En session får bara skapas där någon har
    /// bevisat vem de är med lösenord — <c>LoginController</c> och <c>AuthApiController</c>. Ett annat anrop av
    /// <c>SignInAsync</c> är en länk eller QR som ger inloggning utan lösenord: det var så en medlem kunde bli
    /// inloggad som en sajtadmin (A1).
    ///
    /// Ett motiverat undantag får <c>signin-ok: &lt;skäl&gt;</c> på SAMMA rad, och skälet ska säga varför kontot
    /// inte är privilegierat eller hur personen har bevisat sig.
    /// </summary>
    public class SignInGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        /// <summary>Filerna där en lösenordsinloggning hör hemma.</summary>
        private static readonly string[] Allowed =
        {
            "Controllers/LoginController.cs",
            "Controllers/Api/AuthApiController.cs",
        };

        private static readonly Regex SessionCreating = new(
            @"\b(SignInAsync|SignInWithClaimsAsync|PasswordSignInAsync|ExternalLoginSignInAsync|TwoFactorSignInAsync)\s*\(",
            RegexOptions.Compiled);

        [Fact]
        public void Ingen_kod_skapar_en_session_utanfor_inloggningen()
        {
            var hits = Scan(SessionCreating);
            Assert.True(hits.Count == 0,
                "Skapar en session utanför LoginController/AuthApiController — en länk eller QR får aldrig ge inloggning utan lösenord. " +
                "Motiverat undantag: `// signin-ok: <skäl>` på raden.\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Spärren_fångar_det_den_ska()
        {
            // Positivkontroll: mönstret träffar de riktiga anropen (annars vore testet ovan alltid grönt).
            Assert.Matches(SessionCreating, "await _signInManager.SignInAsync(identityUser, isPersistent: true);");
            Assert.Matches(SessionCreating, "var r = await _signInManager.PasswordSignInAsync(a, b, true, true);");
            Assert.DoesNotMatch(SessionCreating, "// ingen inloggning här");
        }

        [Fact]
        public void Spärren_skannar_tillräckligt_många_filer()
        {
            var count = SourceFiles(SiteRoot()).Count();
            Assert.True(count > 200, $"Spärren skannade bara {count} källfiler — urvalet är tomt eller fel katalog.");
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
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("@*")) continue;
                    if (lines[i].Contains("signin-ok:")) continue;
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
