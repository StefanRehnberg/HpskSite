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
    /// ⚠️⚠️ TÄVLINGSSPÄRREN (C3-planen S1, 2026-10-14). Tävlingen och serien läses och skrivs genom
    /// <c>ICompetitionStore</c> / <c>ICompetitionSeriesStore</c>. Läsarna flyttas dit område för område i S3,
    /// så i dag finns hundratals direktläsningar kvar — spärren låter talen bara gå NED.
    ///
    /// <list type="bullet">
    ///   <item>doktypernas alias som stränglitteral (<c>"competition"</c>, <c>"competitionSeries"</c>) — det är
    ///   så en nod känns igen, letas upp och gås igenom (<c>DescendantsOfType</c>);</item>
    ///   <item>tävlingens och seriens egna egenskaper lästa eller skrivna på en nod;</item>
    ///   <item>och i den nya koden (lagringen och posten) inga API:er som försvinner i Umbraco 17 (K4).</item>
    /// </list>
    ///
    /// <para><b>Sänk taket när en läsning flyttas — höj det aldrig.</b> Ett motiverat undantag får
    /// markeringen <c>competitionstore-ok: &lt;skäl&gt;</c> på raden.</para>
    /// </summary>
    public class CompetitionStoreGuardTests
    {
        /// <summary>Levande <c>"competition"</c>-litteraler 2026-10-14. Sänk — höj aldrig.</summary>
        private const int CompetitionAliasBaseline = 71; // 2026-10-15 S3 omr 8

        /// <summary>Levande <c>"competitionSeries"</c>-litteraler 2026-10-14. Sänk — höj aldrig.</summary>
        private const int SeriesAliasBaseline = 33;

        /// <summary>Levande läsningar och skrivningar av tävlingens egenskaper på en nod 2026-10-14. Sänk — höj aldrig.</summary>
        private const int CompetitionPropertyBaseline = 460; // 2026-10-15 S3 omr 8

        /// <summary>Samma för serien.</summary>
        private const int SeriesPropertyBaseline = 66;

        private static readonly string[] SkipDirectories =
            { "bin", "obj", "node_modules", "lib", "Migrations", "Documentation", "KnowledgeBase", "App_Data", "umbraco", "media", "App_Plugins", "wwwroot" };

        /// <summary>Lagringen, posten och skyddsnätets jämförelser — de enda som ska känna till noderna.</summary>
        private static readonly string[] AllowedDirectories =
        {
            Path.Combine("Services", "Competitions"),
            Path.Combine("Models", "Competitions"),
            Path.Combine("Services", "C3"),
        };

        private static readonly Regex CompetitionAlias = new("\"competition\"", RegexOptions.Compiled);
        private static readonly Regex SeriesAlias = new("\"competitionSeries\"", RegexOptions.Compiled);

        /// <summary>Egenskaper som bara tävlingen har (inte klubbens, händelsens eller kretsens med samma namn).</summary>
        private static readonly Regex CompetitionProperty = new(
            "(GetValue|SetValue|Value|HasProperty|GetProperty)\\s*(<[^>]*>)?\\s*\\(\\s*\"(competitionName|competitionType|competitionDate|competitionEndDate|competitionDirector|competitionLevel|competitionManagers|competitionScope|shootingClassIds|isClubOnly|isExternal|isAwardingStandardMedals|isAwardingHonoraryAward|numberOfSeriesOrStations|numberOfFinalSeries|registrationFee|juniorRegistrationFee|teamRegistrationFee|stafettRegistrationFee|subCompetitionFee|subCompetitionFeeMode|subCompetitionName|stationConfig|direktplaceringConfig|rollingStart|scoringMode|faltskyttePatrolsPublished|faltskytteResultsOfficial|faltskytteSelfServiceResults|faltskytteStationManagers|allowDualCClassRegistration|allowStafett|allowTeams|allowSelfReporting|showLiveResults|maxReshoots|medalsPerWeaponGroup|closeRegistrationOnStartList|invitationFile|resultListFile|registrationOpenDate|registrationCloseDate)\"",
            RegexOptions.Compiled);

        private static readonly Regex SeriesProperty = new(
            "(GetValue|SetValue|Value|HasProperty|GetProperty)\\s*(<[^>]*>)?\\s*\\(\\s*\"(seriesName|seriesStartDate|seriesEndDate|seriesDescription|seriesShortDescription|seriesLogo|seriesCalculationStrategy|seriesCalculationConfig|showInMenu)\"",
            RegexOptions.Compiled);

        /// <summary>API:er som försvinner i Umbraco 17 eller går igenom trädet (K4) — förbjudna i den nya koden.</summary>
        private static readonly Regex ForbiddenApi = new(
            "GetAtRoot\\s*\\(|GetByRoute\\s*\\(|UrlInfo\\.Url\\s*\\(|DescendantsOfType\\s*\\(|\\.(Parent|Children)\\b(?!\\s*\\()",
            RegexOptions.Compiled);

        [Fact]
        public void Tavlingens_alias_minskar() =>
            AssertAtMost(Scan(CompetitionAlias.IsMatch, outsideAllowed: true), CompetitionAliasBaseline, "\"competition\"-litteraler");

        [Fact]
        public void Seriens_alias_minskar() =>
            AssertAtMost(Scan(SeriesAlias.IsMatch, outsideAllowed: true), SeriesAliasBaseline, "\"competitionSeries\"-litteraler");

        [Fact]
        public void Tavlingens_egenskaper_minskar() =>
            AssertAtMost(Scan(CompetitionProperty.IsMatch, outsideAllowed: true), CompetitionPropertyBaseline, "läsningar av tävlingens egenskaper på en nod");

        [Fact]
        public void Seriens_egenskaper_minskar() =>
            AssertAtMost(Scan(SeriesProperty.IsMatch, outsideAllowed: true), SeriesPropertyBaseline, "läsningar av seriens egenskaper på en nod");

        [Fact]
        public void Lagringen_anvander_inga_forsvinnande_api()
        {
            var hits = Scan(ForbiddenApi.IsMatch, outsideAllowed: false)
                .Where(h => !h.StartsWith(Path.Combine("Services", "C3"), StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.True(hits.Count == 0,
                "Lagringen eller posten använder ett API som försvinner i Umbraco 17 eller går igenom trädet:\n" + string.Join("\n", hits));
        }

        /// <summary>Positivt kontrollprov: mönstren FÅNGAR det de ska — annars kan spärren inte falla.</summary>
        [Fact]
        public void Monstren_fangar_det_de_ska()
        {
            Assert.Matches(CompetitionAlias, "if (node.ContentType.Alias == \"competition\")");
            Assert.DoesNotMatch(CompetitionAlias, "if (node.ContentType.Alias == \"competitionSeries\")");
            Assert.Matches(SeriesAlias, "hub.DescendantsOfType(\"competitionSeries\")");
            Assert.Matches(CompetitionProperty, "var d = comp.Value<DateTime>(\"competitionDate\");");
            Assert.Matches(CompetitionProperty, "node.SetValue(\"stationConfig\", json);");
            Assert.DoesNotMatch(CompetitionProperty, "var d = info.CompetitionDate;");
            Assert.Matches(SeriesProperty, "var n = s.Value<string>(\"seriesName\");");
            Assert.Matches(ForbiddenApi, "var p = pub.Parent;");
            Assert.Matches(ForbiddenApi, "foreach (var c in pub.Children)");
            Assert.Matches(ForbiddenApi, "var roots = cache.GetAtRoot();");
            Assert.DoesNotMatch(ForbiddenApi, "var kids = pub.Children();");
        }

        private static void AssertAtMost(List<string> hits, int baseline, string what) =>
            Assert.True(hits.Count <= baseline,
                $"{hits.Count} {what} utanför tävlingslagringen — taket är {baseline}. Gå genom ICompetitionStore:\n" +
                string.Join("\n", hits.Take(400)));

        private static List<string> Scan(Func<string, bool> match, bool outsideAllowed)
        {
            var root = SiteRoot();
            var hits = new List<string>();
            foreach (var file in SourceFiles(root))
            {
                var rel = Path.GetRelativePath(root, file);
                var inAllowed = AllowedDirectories.Any(a => rel.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
                if (outsideAllowed == inAllowed) continue;
                var lines = File.ReadAllLines(file);
                for (var i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;
                    if (line.Contains("competitionstore-ok:")) continue;
                    if (match(line)) hits.Add($"{rel}:{i + 1}: {trimmed}");
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
