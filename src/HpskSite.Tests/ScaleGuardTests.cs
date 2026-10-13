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
    /// ⚠️⚠️ SKALSPÄRREN (skalplanen B4, 2026-10-12). Läser sajtens källkod och fäller bygget på
    /// mönster som inte skalar till 20 000 medlemmar:
    /// <list type="bullet">
    /// <item>en rollfråga per medlem i en lista (<c>.Where(m => _memberService.GetAllRoles(m.Id)…)</c>) —
    ///   använd <c>MemberRoleIndex</c> eller <c>GetMembersInRole</c>;</item>
    /// <item>ett fast tak på medlemsläsningen (<c>GetAll(0, 5000, …)</c>) — tyst fel när registret växer;</item>
    /// <item>FLER anrop till <c>_memberService.GetAll(</c> än i dag. Talet ska bara gå NED: varje ställe
    ///   som flyttas till klubbens medlemsregister i SQL sänker <see cref="GetAllBaseline"/>.</item>
    /// </list>
    /// Ett motiverat undantag får markeringen <c>scale-ok: &lt;skäl&gt;</c> på raden.
    /// </summary>
    public class ScaleGuardTests
    {
        /// <summary>Antal levande anrop till <c>MemberService.GetAll(</c> 2026-10-12. Sänk när ett tas bort — höj aldrig.</summary>
        private const int GetAllBaseline = 39; // 2026-10-13: registreringens adminkontakter och godkännandet flyttade

        /// <summary>
        /// Antal levande <c>GetAllMembers()</c> UTAN argument (hela registret) 2026-10-13 — sökfälten i
        /// genomgångens punkt B. Spärren missade dem förut. Sänk när ett tas bort — höj aldrig.
        /// </summary>
        private const int GetAllMembersBaseline = 3;

        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly Regex GetAllCall = new(@"[mM]emberService\.GetAll\(", RegexOptions.Compiled);
        private static readonly Regex FixedCap = new(@"[mM]emberService\.GetAll\(\s*0\s*,\s*\d+\s*,", RegexOptions.Compiled);
        private static readonly Regex RolesInLambda = new(@"\.(Where|Count|Any|All|Select|ToDictionary|First|FirstOrDefault)\([^;]*GetAllRoles\(", RegexOptions.Compiled);
        private static readonly Regex GetAllMembersNoArgs = new(@"[mM]emberService\.GetAllMembers\(\s*\)", RegexOptions.Compiled);
        private static readonly Regex RolesOfVariable = new(@"GetAllRoles\(\s*(\w+)\.Id\s*\)", RegexOptions.Compiled);

        /// <summary>
        /// En rollfråga på <c>x.Id</c> där <c>x</c> är variabeln i en <c>foreach</c> högst 25 rader upp — en
        /// fråga per medlem i en loop. Spärren såg förut bara lambdor och missade
        /// <c>MemberController.GetAdminContactsForClub</c> (genomgången 2026-10-13). Ren funktion.
        /// </summary>
        internal static bool IsRoleQueryPerLoopMember(IReadOnlyList<string> lines, int i)
        {
            var m = RolesOfVariable.Match(lines[i]);
            if (!m.Success) return false;
            var loop = new Regex(@"foreach\s*\(\s*var\s+" + Regex.Escape(m.Groups[1].Value) + @"\s+in\b");
            for (var j = Math.Max(0, i - 25); j < i; j++)
                if (loop.IsMatch(lines[j])) return true;
            return false;
        }

        [Fact]
        public void Ingen_rollfraga_per_medlem_i_en_lista()
        {
            var hits = Scan(line => RolesInLambda.IsMatch(line));
            Assert.True(hits.Count == 0,
                "En rollfråga per medlem i en lista — använd MemberRoleIndex.Load(...) eller GetMembersInRole:\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Ingen_rollfraga_per_medlem_i_en_loop()
        {
            var hits = ScanLines((lines, i) => IsRoleQueryPerLoopMember(lines, i));
            Assert.True(hits.Count == 0,
                "En rollfråga per medlem i en loop — använd MemberRoleIndex.Load(...) eller GetMembersInRole:\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Antalet_GetAllMembers_utan_urval_okar_inte()
        {
            var hits = Scan(line => GetAllMembersNoArgs.IsMatch(line));
            Assert.True(hits.Count <= GetAllMembersBaseline,
                $"{hits.Count} anrop till GetAllMembers() utan urval — taket är {GetAllMembersBaseline}. Det läser hela registret:\n" + string.Join("\n", hits));
        }

        /// <summary>Positivt kontrollprov: varje mönster FÅNGAR det det ska (annars kan spärren inte falla).</summary>
        [Fact]
        public void Monstren_fangar_det_de_ska()
        {
            Assert.Matches(GetAllCall, "var all = _memberService.GetAll(0, int.MaxValue, out _);");
            Assert.Matches(FixedCap, "var all = _memberService.GetAll(0, 5000, out _);");
            Assert.Matches(RolesInLambda, "members.Where(m => _memberService.GetAllRoles(m.Id).Contains(g))");
            Assert.Matches(GetAllMembersNoArgs, "var all = _memberService.GetAllMembers();");
            Assert.DoesNotMatch(GetAllMembersNoArgs, "var some = _memberService.GetAllMembers(ids);");
            var loop = new[] { "foreach (var member in members)", "{", "    var roles = _memberService.GetAllRoles(member.Id);", "}" };
            Assert.True(IsRoleQueryPerLoopMember(loop, 2));
            var notLoop = new[] { "foreach (var role in _memberService.GetAllRoles(loser.Id))", "{", "}" };
            Assert.False(IsRoleQueryPerLoopMember(notLoop, 0));
        }

        [Fact]
        public void Inget_fast_tak_pa_medlemslasningen()
        {
            var hits = Scan(line => FixedCap.IsMatch(line));
            Assert.True(hits.Count == 0,
                "Ett fast tak på GetAll tappar medlemmar tyst när registret växer:\n" + string.Join("\n", hits));
        }

        [Fact]
        public void Antalet_GetAll_okar_inte()
        {
            var hits = Scan(line => GetAllCall.IsMatch(line));
            Assert.True(hits.Count <= GetAllBaseline,
                $"{hits.Count} anrop till MemberService.GetAll( — taket är {GetAllBaseline}. Läs klubbens medlemmar ur " +
                "ClubMembership i stället för hela registret:\n" + string.Join("\n", hits));
        }

        private static List<string> Scan(Func<string, bool> match) => ScanLines((lines, i) => match(lines[i]));

        private static List<string> ScanLines(Func<IReadOnlyList<string>, int, bool> match)
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
                    if (line.Contains("scale-ok:")) continue;
                    if (match(lines, i)) hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {trimmed}");
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
