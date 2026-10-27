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
    /// ⚠️⚠️ ÄGARSPÄRREN (Stefan 2026-10-27: dubbelägarskapet är borta). Bemanningen ägs ALLTID av en planering. Tre sätt att få tillbaka en tävlingsägd rad:
    /// ett ägarläge utan planering (<c>ForCompetitionOnly</c>/<c>HasPlanning</c>), en fråga som släpper in rader utan planering (<c>PlanningId IS NULL</c>), och en roll
    /// som sparas som tävlingsägd (<c>RoleOwnerType.Competition</c>). Det ENDA stället som får känna till de äldre raderna är <c>StaffingPlanningLink</c>: <c>Adopt</c>
    /// tar in dem när en planering skapas eller kopplas, och det är hela migreringen.
    /// </summary>
    public class StaffOwnershipGuardTests
    {
        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        private static readonly string[] LegacyAllowed = { Path.Combine("Services", "Staffing", "StaffingPlanningLink.cs") };

        private static readonly Regex OwnerModes = new(@"\b(ForCompetitionOnly|HasPlanning)\b", RegexOptions.Compiled);
        private static readonly Regex NoPlanningRows = new(@"(?<![A-Za-z])PlanningId\s+IS\s+NULL", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex CompetitionRole = new(@"RoleOwnerType\.Competition\b", RegexOptions.Compiled);

        [Fact]
        public void Inget_agarlage_utan_planering_finns() =>
            AssertNone(Scan(OwnerModes, allowed: Array.Empty<string>()), "Ett ägarläge utan planering har kommit tillbaka — bemanningen ägs alltid av en planering");

        [Fact]
        public void Ingen_fraga_slapper_in_rader_utan_planering_utom_adoptionen() =>
            AssertNone(Scan(NoPlanningRows, LegacyAllowed), "En fråga släpper in rader utan planering — det får bara StaffingPlanningLink.Adopt göra");

        [Fact]
        public void Ingen_roll_sparas_som_tavlingsagd_utom_adoptionen() =>
            AssertNone(Scan(CompetitionRole, LegacyAllowed), "RoleOwnerType.Competition används utanför adoptionen — en roll ägs alltid av en planering");

        /// <summary>Positivt kontrollprov: mönstren FÅNGAR det de ska — annars kan spärren inte falla.</summary>
        [Fact]
        public void Monstren_fangar_det_de_ska()
        {
            Assert.Matches(OwnerModes, "var o = StaffOwner.ForCompetitionOnly(5);");
            Assert.Matches(OwnerModes, "if (!owner.HasPlanning) return null;");
            Assert.Matches(NoPlanningRows, "WHERE CompetitionId = @1 AND PlanningId IS NULL");
            Assert.Matches(NoPlanningRows, "where planningid  is   null");
            Assert.DoesNotMatch(NoPlanningRows, "WHERE ParentPlanningId IS NULL");
            Assert.Matches(CompetitionRole, "OwnerType = RoleOwnerType.Competition");
            Assert.DoesNotMatch(CompetitionRole, "OwnerType = RoleOwnerType.Planning");
        }

        private static void AssertNone(List<string> hits, string message) =>
            Assert.True(hits.Count == 0, message + ":\n" + string.Join("\n", hits));

        private static List<string> Scan(Regex pattern, string[] allowed)
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file);
                if (allowed.Any(a => string.Equals(a, rel, StringComparison.OrdinalIgnoreCase))) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                    if (lines[i].Contains("owner-ok:")) continue;
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
            var src = Path.GetDirectoryName(thisFile)!;
            var site = Path.Combine(Path.GetDirectoryName(src)!, "HpskSite");
            Assert.True(Directory.Exists(site), $"Site source not found at {site}");
            return site;
        }
    }
}
