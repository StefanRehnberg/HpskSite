using System.Runtime.CompilerServices;
using HpskSite.Services.Events;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Expertgruppen 2026-11-09 (S2, S5). Händelselistorna <c>GetClubEvents</c> och <c>GetRegionEvents</c> svarar utan inloggningskrav och
    /// delar en redigeringsprojektion; kontaktuppgifterna får bara följa med för ägarens administratör. Och snabblänken på ett dokument
    /// styr var det visas, aldrig vem som får ladda ned det.
    /// </summary>
    public class EventRowPrivacyTests
    {
        [Fact]
        public void Administratören_får_värdet_alla_andra_får_tomt()
        {
            Assert.Equal("kontakt@klubb.se", EventRowPrivacy.Contact("kontakt@klubb.se", true));
            Assert.Equal("", EventRowPrivacy.Contact("kontakt@klubb.se", false));
            Assert.Equal("", EventRowPrivacy.Contact(null, true));
            Assert.Equal("", EventRowPrivacy.Contact(null, false));
        }

        [Fact]
        public void Varje_kontaktfält_i_projektionen_går_genom_EventRowPrivacy()
        {
            var body = Slice(ReadController("ClubController.cs"), "private object EventEditListRow(");
            foreach (var field in new[] { "ContactPerson", "ContactEmail", "ContactPhone", "SwishNumber" })
            {
                var all = Count(body, $"evt.{field}");
                var guarded = Count(body, $"EventRowPrivacy.Contact(evt.{field},");
                Assert.True(all > 0, $"hittar inte evt.{field} i projektionen (provet måste följa med om fältet byter namn)");
                Assert.True(all == guarded, $"evt.{field} står oskyddad i händelselistans projektion ({guarded} av {all} går genom EventRowPrivacy)");
            }
        }

        [Theory]
        [InlineData("public async Task<IActionResult> GetClubEvents(", "IsClubAdminForClub(clubId)")]
        [InlineData("public async Task<IActionResult> GetRegionEvents(", "IsRegionalAdminForRegion(")]
        public void Båda_listorna_prövar_ägarens_administratör_och_svarar_aldrig_med_undantagstext(string action, string adminCheck)
        {
            var body = Slice(ReadController("ClubController.cs"), action);
            Assert.Contains("var includeContacts = await _authorizationService." + adminCheck, body);
            Assert.Contains("EventEditListRow(evt, eventRanges, includeContacts)", body);
            Assert.DoesNotContain("ex.Message", CodeOnly(body));
        }

        [Fact]
        public void Snabblänken_öppnar_inte_ett_dokument_förbi_åtkomstnivån()
        {
            var body = Slice(ReadController("DocumentController.cs"), "private async Task<bool> CanAccessDocument(");
            // Kommentaren får nämna fältet; koden får inte pröva det.
            Assert.DoesNotContain("ShowInQuickLinks", CodeOnly(body));
        }

        private static string CodeOnly(string text) =>
            string.Join("\n", text.Split('\n').Where(l => !l.TrimStart().StartsWith("//")));

        private static string ReadController(string file, [CallerFilePath] string here = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite", "Controllers", file)));

        /// <summary>Från metodens huvud till nästa metod på samma indrag (åtta mellanslag).</summary>
        private static string Slice(string src, string head)
        {
            var start = src.IndexOf(head, StringComparison.Ordinal);
            Assert.True(start >= 0, $"hittar inte {head} i källan");
            var end = src.IndexOf("\n        }\n", start, StringComparison.Ordinal);
            if (end < 0) end = src.IndexOf("\r\n        }\r\n", start, StringComparison.Ordinal);
            Assert.True(end > start, $"hittar inte slutet på {head}");
            return src.Substring(start, end - start);
        }

        private static int Count(string text, string needle)
        {
            var n = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
            return n;
        }
    }
}
