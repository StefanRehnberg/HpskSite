using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Expertgruppen 2026-11-09 (test/drift, steg 1): "Visa publikt"-grinden <see cref="SitePaneGate"/> anropas med HÅRDKODADE nycklar i
    /// kontrollerna. Förut bytte ingen svit fel om någon ändrade en nyckel eller lade ett nytt anrop på en panel som aldrig får vara publik;
    /// den rena regeln (<c>SitePublicPanelsTests</c>) prövar bara själva beräkningen. Det här provet binder anropsställena till panelkatalogen.
    /// En utloggad får data bara när grinden säger ja, så varje anrop är en plats där något kan öppnas för alla.
    /// </summary>
    public class SitePaneGateCallSitesTests
    {
        private sealed record Call(string File, string Owner, string PageKey, string PanelKey);

        /// <summary>Alla anrop av grinden i appens källa: ägartyp, sidnyckel, panelnyckel.</summary>
        private static List<Call> CallSites([CallerFilePath] string here = "")
        {
            var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite"));
            var calls = new List<Call>();
            // IsPublic(<SiteTemplates.Club|Region>, <id>, "sida", "panel")
            var direct = new Regex(@"\.IsPublic\(\s*HpskSite\.Services\.SitePages\.SiteTemplates\.(Club|Region)\s*,\s*[^,]+,\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
            // IsPublicForRegionCode(<kod>, "sida", "panel")  (alltid en krets)
            var byCode = new Regex(@"\.IsPublicForRegionCode\(\s*[^,]+,\s*""([^""]+)""\s*,\s*""([^""]+)""\s*\)", RegexOptions.Compiled);
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                         .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)))
            {
                var name = Path.GetRelativePath(root, file);
                var text = File.ReadAllText(file);
                foreach (Match m in direct.Matches(text)) calls.Add(new Call(name, m.Groups[1].Value, m.Groups[2].Value, m.Groups[3].Value));
                foreach (Match m in byCode.Matches(text)) calls.Add(new Call(name, "Region", m.Groups[1].Value, m.Groups[2].Value));
            }
            return calls;
        }

        [Fact]
        public void Provet_hittar_anropen()
            => Assert.True(CallSites().Count >= 4, "regexen hittar inga anrop: grindens signatur har ändrats, uppdatera provet");

        [Fact]
        public void Varje_anrop_pekar_på_en_publicerbar_panel_på_sin_egen_sida()
        {
            foreach (var c in CallSites())
            {
                var def = PanelCatalog.Find(c.Owner, c.PanelKey);
                Assert.True(def != null, $"{c.File}: panelen {c.Owner}/{c.PanelKey} finns inte i katalogen");
                Assert.True(def!.PublicAllowed, $"{c.File}: {c.Owner}/{c.PanelKey} får inte vara publik (PublicAllowed=false) men grinden frågas om den");
                Assert.False(def.PersonalData, $"{c.File}: {c.Owner}/{c.PanelKey} visar personuppgifter och får aldrig öppnas för alla");
                Assert.True(PanelCatalog.AllowedOn(c.Owner, c.PageKey, c.PanelKey), $"{c.File}: panelen {c.PanelKey} ligger inte på sidan {c.PageKey} ({c.Owner})");
            }
        }

        [Fact]
        public void Mängden_anrop_är_den_beslutade_och_en_ny_plats_kräver_ett_medvetet_beslut()
        {
            var actual = CallSites().Select(c => $"{c.File.Replace('\\', '/')}|{c.Owner}/{c.PageKey}/{c.PanelKey}").OrderBy(x => x).ToArray();
            var expected = new[]
            {
                "Controllers/ClubController.cs|Club/calendar/calendar",
                "Controllers/CompetitionRecordsController.cs|Club/records/records",
                "Controllers/CompetitionRecordsController.cs|Region/records/records",
                "Controllers/ShootingRangeController.cs|Club/ranges/ranges",
            }.OrderBy(x => x).ToArray();
            Assert.Equal(expected, actual);
        }

        [Fact]
        public void Varje_panel_vars_data_hämtas_av_ett_anrop_har_en_grind()
        {
            // Banor, kalender och rekord hämtar sin data med AJAX som en utloggad kan ställa direkt; utan grind vore de låsta för alltid
            // (eller, värre, en ny väg förbi grinden). Tävlingar ritas på servern av sidans egen åtkomstprövning och behöver ingen.
            var gated = CallSites().Select(c => $"{c.Owner}/{c.PanelKey}").ToHashSet();
            foreach (var key in new[] { "Club/ranges", "Club/calendar", "Club/records", "Region/records" })
                Assert.Contains(key, gated);
        }
    }
}
