using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.CompetitionTypes.Common;
using NPoco;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// ⚠️⚠️ FINALEN MÅSTE LÄSA GRENENS EGEN RESULTATTABELL.
    ///
    /// Kretsmästerskapet 3468 (2026-10-03): finalstartlistan gick inte att skapa, och skjutledaren
    /// fick ställa om tävlingen till en tävling utan final för att kunna skjuta finalserierna.
    /// Bakom det låg två fel:
    ///   1. Finalkedjan (grundomgångens rangordning, låsningens kontrollsumma, "placera om efter
    ///      resultat") läste hårdkodat PrecisionResultEntry. För Magnumprecision, Duell,
    ///      Standardpistol, Sportpistol, Milsnabb och Nationell helmatch fanns därför aldrig något
    ///      underlag — finalen var omöjlig i sex grenar, tyst.
    ///   2. Finalkortet lästes in en gång när sidan öppnades och sa "klicka Uppdatera" (på fel flik)
    ///      både när inga resultat fanns och när servern kraschade.
    ///
    /// Testerna här gör båda formerna omöjliga att återinföra utan att något blir rött.
    /// </summary>
    public class FinalsResultTableGuardTests
    {
        private static readonly string[] OwnShapeDisciplines = { "Springskytte", "Faltskytte", "MagnumFalt" };

        [Theory]
        [InlineData("Precision")]
        [InlineData("MagnumPrecision")]
        [InlineData("Duell")]
        [InlineData("Milsnabb")]
        [InlineData("NationellHelmatch")]
        [InlineData("Standardpistol")]
        [InlineData("Sportpistol")]
        public void Läsaren_läser_samma_tabell_som_registret_säger(string type)
        {
            var entity = PrecisionFamilyResultReader.EntityTypeFor(type);
            var table = entity.GetCustomAttribute<TableNameAttribute>()?.Value;

            Assert.Equal(CompetitionResultTables.For(type), table);
        }

        [Fact]
        public void Varje_gren_i_registret_har_en_gren_i_läsaren()
        {
            // En ny gren i CompetitionResultTables som saknas i läsaren skulle tyst läsa
            // PrecisionResultEntry — exakt felet som gjorde finalen omöjlig.
            var registryTypes = typeof(CompetitionResultTables)
                .GetField("ByType", BindingFlags.NonPublic | BindingFlags.Static)!
                .GetValue(null) as System.Collections.Generic.IDictionary<string, string>;
            Assert.NotNull(registryTypes);

            var missing = registryTypes!.Keys
                .Where(k => !OwnShapeDisciplines.Contains(k, StringComparer.OrdinalIgnoreCase))
                .Where(k => !PrecisionFamilyResultReader.SupportedTypes.Contains(k, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                "Grenar utan läsgren i PrecisionFamilyResultReader: " + string.Join(", ", missing));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Tom_typ_är_precision(string? type)
        {
            // Äldre tävlingsnoder bär ingen competitionType och ÄR precision.
            Assert.Equal("PrecisionResultEntry",
                PrecisionFamilyResultReader.EntityTypeFor(type).GetCustomAttribute<TableNameAttribute>()?.Value);
        }

        [Theory]
        [InlineData(10, 3, 7)]   // kretsmästerskapet: 7 grundserier + 3 finalserier
        [InlineData(10, 0, 10)]  // ingen final: alla serier är grundomgång
        [InlineData(12, 2, 10)]
        [InlineData(3, 3, 0)]    // felinställd: aldrig negativt
        [InlineData(2, 3, 0)]
        public void Grundomgångens_längd_är_totalen_minus_finalen(int total, int finals, int expected)
        {
            Assert.Equal(expected, PrecisionFamilyResultReader.QualifyingSeriesCount(total, finals));
        }

        /// <summary>
        /// Finalkedjans filer får inte läsa precisionens tabell direkt. Kommentarer räknas inte.
        /// </summary>
        [Theory]
        [InlineData("CompetitionTypes/Precision/Services/PrecisionQualifyingResultsService.cs")]
        [InlineData("CompetitionTypes/Precision/Services/PrecisionFinalsQualificationService.cs")]
        [InlineData("CompetitionTypes/Precision/Services/PrecisionFinalsStartListBuilder.cs")]
        [InlineData("CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs")]
        public void Finalkedjan_läser_inte_precisionstabellen_hårdkodat(string relativePath)
        {
            var path = Path.Combine(SiteRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Filen finns inte: {path}");

            var forbidden = new Regex(@"FROM\s+\[?PrecisionResultEntry\]?|Fetch(Async)?<PrecisionResultEntry>",
                RegexOptions.IgnoreCase);
            var hits = File.ReadAllLines(path)
                .Select((line, i) => (line, no: i + 1))
                .Where(x => !x.line.TrimStart().StartsWith("//") && !x.line.TrimStart().StartsWith("///")
                            && !x.line.TrimStart().StartsWith("*"))
                .Where(x => forbidden.IsMatch(x.line))
                .Select(x => $"{relativePath}:{x.no}: {x.line.Trim()}")
                .ToList();

            Assert.True(hits.Count == 0,
                "Läs grundomgången genom PrecisionFamilyResultReader / GetQualifyingResultsAsync:\n" +
                string.Join("\n", hits));
        }

        /// <summary>
        /// Finalkortet: ett serverfel får inte se ut som "inga resultat", och kortet måste läsa om
        /// sig själv. Vyn är runtime-kompilerad, så det här är strukturkontroller av källan.
        /// </summary>
        [Fact]
        public void Finalkortet_skiljer_fel_från_tomt_och_läser_om_sig_själv()
        {
            var path = Path.Combine(SiteRoot(), "Views", "Partials", "CompetitionFinalsStartListManagement.cshtml");
            var src = File.ReadAllText(path);

            Assert.DoesNotContain("klicka\n                    <strong>Uppdatera</strong>", src);
            Assert.DoesNotContain("Resultatlistan måste genereras först", src);
            Assert.Contains("id=\"fnLoadError\"", src);
            Assert.Contains("failed: true", src);
            Assert.Contains("shown.bs.tab", src);
            Assert.Contains("visibilitychange", src);
        }

        // ── Vilka grenar har finalomgång? (SHB C.3.6.1.1 — bara precision) ──────────────────────

        [Theory]
        [InlineData("Precision", true)]
        [InlineData("precision", true)]
        [InlineData("", true)]            // äldre noder utan typ ÄR precision
        [InlineData(null, true)]
        [InlineData("MagnumPrecision", false)]
        [InlineData("Duell", false)]
        [InlineData("Milsnabb", false)]
        [InlineData("NationellHelmatch", false)]
        [InlineData("Standardpistol", false)]
        [InlineData("Sportpistol", false)]
        [InlineData("Springskytte", false)]
        [InlineData("Faltskytte", false)]
        [InlineData("MagnumFalt", false)]
        public void Bara_precision_har_finalomgång(string? type, bool expected)
        {
            Assert.Equal(expected, CompetitionFinals.Supports(type));
        }

        [Fact]
        public void Varje_gren_utom_precision_vägras_finalserier_och_skälet_namnger_grenen_och_SHB()
        {
            foreach (var t in HpskSite.Models.CompetitionTypes.All.Where(t => t.Id != "Precision"))
            {
                var refusal = CompetitionFinals.Refusal(t.Id, 3);
                Assert.NotNull(refusal);
                Assert.Contains(t.Name, refusal);
                Assert.Contains("C.3.6.1.1", refusal);
                Assert.Null(CompetitionFinals.Refusal(t.Id, 0));   // 0 är alltid tillåtet
            }
            Assert.Null(CompetitionFinals.Refusal("Precision", 3));
        }

        [Theory]
        [InlineData("Precision", 3, 3)]
        [InlineData("Duell", 3, 0)]       // gammalt felaktigt värde gäller inte
        [InlineData("Precision", -1, 0)]
        public void Effektiva_finalserier(string type, int stored, int expected)
        {
            Assert.Equal(expected, CompetitionFinals.Effective(type, stored));
        }

        // ── Hur många går till final (SHB C.3.6.1.1) — samma tal på kortet som i guiden ────────

        [Theory]
        [InlineData(new[] { 350, 340, 330 }, 3, 3)]                 // färre än gränsen: alla
        [InlineData(new[] { 350, 340, 330, 320 }, 2, 2)]            // ren gräns
        [InlineData(new[] { 350, 340, 340, 340, 320 }, 2, 4)]       // lika med den siste följer med
        [InlineData(new[] { 350, 340 }, 0, 1)]                      // aldrig under 1
        [InlineData(new int[0], 10, 0)]
        public void Finalister_med_lika_poäng(int[] scoresDesc, int rawCutoff, int expected)
        {
            Assert.Equal(expected, HpskSite.CompetitionTypes.Precision.Services.PrecisionFinalsStartListBuilder
                .FinalistsWithTies(scoresDesc, rawCutoff));
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
