using System.Reflection;
using System.Runtime.CompilerServices;
using HpskSite.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klubbsidorna 2b (plan v2 §6): varje action i <see cref="SiteEditController"/> (a) är märkt för endpoint-baslinjen, (b) kräver antiforgery om den
    /// skriver, och (c) går genom <c>Gate(...)</c> — ägarprövningen (rätt sort, brytare 2, ägarens EGEN admin) — innan den gör något. En ny action
    /// som glömmer något av det fäller bygget.
    /// </summary>
    public class SiteEditControllerGuardTests
    {
        private static IEnumerable<MethodInfo> Actions() =>
            typeof(SiteEditController).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => typeof(IActionResult).IsAssignableFrom(m.ReturnType) || typeof(Task<IActionResult>).IsAssignableFrom(m.ReturnType));

        [Fact]
        public void Det_finns_actions_att_pröva()
            => Assert.True(Actions().Count() >= 9);

        [Fact]
        public void Varje_action_är_märkt_i_endpoint_baslinjen()
        {
            foreach (var m in Actions())
                Assert.True(m.GetCustomAttributes().Any(a => a.GetType().Name == "InCodeAuthAttribute"), $"{m.Name} saknar [InCodeAuth(...)]");
        }

        [Fact]
        public void Varje_skrivande_action_kräver_antiforgery_och_ingen_läsande_gör_det_inte_behövs()
        {
            foreach (var m in Actions())
            {
                var isPost = m.GetCustomAttributes<HttpPostAttribute>().Any();
                var isGet = m.GetCustomAttributes<HttpGetAttribute>().Any();
                Assert.True(isPost ^ isGet, $"{m.Name} måste vara exakt ett av [HttpGet]/[HttpPost]");
                if (isPost) Assert.True(m.GetCustomAttributes<ValidateAntiForgeryTokenAttribute>().Any(), $"{m.Name} skriver men saknar [ValidateAntiForgeryToken]");
            }
        }

        [Fact]
        public void Varje_action_går_genom_Gate_före_något_annat()
        {
            var src = File.ReadAllText(SourcePath());
            foreach (var m in Actions())
            {
                var start = src.IndexOf($"public async Task<IActionResult> {m.Name}(", StringComparison.Ordinal);
                Assert.True(start >= 0, $"hittar inte {m.Name} i källan");
                var open = src.IndexOf('{', start);
                var firstCode = src.Substring(open + 1, Math.Min(400, src.Length - open - 1)).TrimStart();
                Assert.True(firstCode.StartsWith("var g = await Gate(", StringComparison.Ordinal), $"{m.Name} måste börja med 'var g = await Gate(...)' (ägarprövningen)");
            }
        }

        [Fact]
        public void Gate_släpper_in_ägarens_egen_admin_och_sajtadmin_men_kretsadmin_bara_i_stödläge()
        {
            var src = File.ReadAllText(SourcePath());
            Assert.Contains("_auth.IsOwnClubAdmin(ownerId)", src);
            Assert.Contains("_auth.IsOwnRegionAdmin(", src);
            Assert.DoesNotContain("IsClubAdminForClub", src);        // släpper in kretsens och sajtens admin utan loggning: kretsadmin går via stödläget
            Assert.DoesNotContain("IsRegionalAdminForRegion", src);
            // Sajtadmin har tillgång till allt (Stefan 2026-11-12), men frågan ställs på EN plats (SiteViewerResolver), aldrig direkt i kontrollern.
            Assert.DoesNotContain("IsCurrentUserAdminAsync", src);
            Assert.Contains("_viewers.IsSiteAdminAsync()", src);
            // Personuppgifterna i Anmälningar är undantaget: bara föreningens egen admin (OwnOnly stänger även sajtadmin ute).
            Assert.Contains("mode != GateMode.OwnOnly && await _viewers.IsSiteAdminAsync()", src);
        }

        private static string SourcePath([CallerFilePath] string here = "") =>
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite", "Controllers", "SiteEditController.cs"));
    }
}
