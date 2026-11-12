using System.Runtime.CompilerServices;
using HpskSite.Services.SitePages;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Blockbiblioteket 2026-11-09: synligheten är en inställning på blocket (panelen) och dess DATA följer den. Medlemskatalogen och styrelsen
    /// prövade förut bara "inloggad" på servern, så ett block ägaren ställt in som Klubbmedlemmar var bara dolt på sidan.
    /// </summary>
    public class SitePanelReadGateTests
    {
        private static ConvertedPage Converted(string owner, string pageKey, string panelKey, int panelVis, SiteVisibility page, ConvertedState state = ConvertedState.Ok)
        {
            var json = "{\"schema\":2,\"blocks\":[{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":" + panelVis + ",\"data\":{\"key\":\"" + panelKey + "\",\"zone\":\"full\"}}]}";
            var content = SiteBlockSchema.Parse(json, SiteContentKind.ConvertedPage, page, SiteTemplates.For(owner), pageKey);
            Assert.True(content.Ok, string.Join("; ", content.Errors.Select(e => e.Message)));
            return new ConvertedPage(pageKey, state, page, false, content);
        }

        private static IReadOnlyDictionary<string, ConvertedPage> One(ConvertedPage p) => new Dictionary<string, ConvertedPage> { [p.TemplateKey] = p };
        private static readonly IReadOnlyDictionary<string, ConvertedPage> None = new Dictionary<string, ConvertedPage>();

        [Fact]
        public void Sida_som_inte_är_omgjord_ger_mallens_nivå_alltså_dagens_beteende()
        {
            Assert.Equal(SiteVisibility.Inloggade, SitePanelReadGate.LevelOf(None, "Club", "members", "member-directory"));
            Assert.Equal(SiteVisibility.Inloggade, SitePanelReadGate.LevelOf(None, "Region", "board", "board"));
        }

        [Theory]
        [InlineData(1, SiteVisibility.Inloggade)]
        [InlineData(2, SiteVisibility.Klubbmedlemmar)]
        [InlineData(3, SiteVisibility.Styrelse)]
        [InlineData(4, SiteVisibility.Dold)]
        public void Blockets_egen_inställning_avgör_nivån_på_en_omgjord_sida(int panelVis, SiteVisibility expected)
        {
            var page = Converted("Club", "members", "member-directory", panelVis, SiteVisibility.Inloggade);
            Assert.Equal(expected, SitePanelReadGate.LevelOf(One(page), "Club", "members", "member-directory"));
        }

        [Fact]
        public void Ett_block_kan_aldrig_vara_öppnare_än_sin_sida()
        {
            // Sidan Klubbmedlemmar + blocket Inloggade är ett ogiltigt innehåll: ägaren kan inte öppna datat bakom en stängd sida.
            var json = "{\"schema\":2,\"blocks\":[{\"id\":\"pnl01\",\"type\":\"panel\",\"visibility\":1,\"data\":{\"key\":\"member-directory\",\"zone\":\"full\"}}]}";
            var content = SiteBlockSchema.Parse(json, SiteContentKind.ConvertedPage, SiteVisibility.Klubbmedlemmar, SiteTemplates.For("Club"), "members");
            Assert.False(content.Ok);
            // Samma nivå på båda är giltigt och läses som just den nivån.
            var page = Converted("Club", "members", "member-directory", 2, SiteVisibility.Klubbmedlemmar);
            Assert.Equal(SiteVisibility.Klubbmedlemmar, SitePanelReadGate.LevelOf(One(page), "Club", "members", "member-directory"));
        }

        [Fact]
        public void Utan_panel_eller_med_en_sida_som_inte_går_att_tolka_läser_bara_ägarens_admin()
        {
            var other = Converted("Club", "members", "guldserie", 1, SiteVisibility.Inloggade);
            Assert.Equal(SiteVisibility.Dold, SitePanelReadGate.LevelOf(One(other), "Club", "members", "member-directory"));   // panelen är borttagen
            var broken = Converted("Club", "members", "member-directory", 1, SiteVisibility.Inloggade, ConvertedState.Broken);
            Assert.Equal(SiteVisibility.Dold, SitePanelReadGate.LevelOf(One(broken), "Club", "members", "member-directory"));
            Assert.Equal(SiteVisibility.Dold, SitePanelReadGate.LevelOf(One(new ConvertedPage("members", ConvertedState.Ok, SiteVisibility.Inloggade, false, null)), "Club", "members", "member-directory"));
        }

        [Fact]
        public void Nivåerna_släpper_in_rätt_tittare()
        {
            var logged = new SiteViewer(true, false, false, false);
            var member = new SiteViewer(true, true, false, false);
            var board = new SiteViewer(true, true, true, false);
            var admin = new SiteViewer(true, false, false, true);
            Assert.True(SiteAccess.CanSee(SiteVisibility.Inloggade, logged));
            Assert.False(SiteAccess.CanSee(SiteVisibility.Klubbmedlemmar, logged));
            Assert.True(SiteAccess.CanSee(SiteVisibility.Klubbmedlemmar, member));
            Assert.False(SiteAccess.CanSee(SiteVisibility.Styrelse, member));
            Assert.True(SiteAccess.CanSee(SiteVisibility.Styrelse, board));
            Assert.True(SiteAccess.CanSee(SiteVisibility.Dold, admin));
            Assert.False(SiteAccess.CanSee(SiteVisibility.Inloggade, SiteViewer.Anonymous));
        }

        // ── Källskanning: de tre ändpunkterna som lämnar ut personer måste fråga grinden FÖRE de läser
        private static string Src(string relative, [CallerFilePath] string here = "")
            => File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "HpskSite", relative)));

        private static string Method(string source, string name)
        {
            var start = source.IndexOf("Task<IActionResult> " + name + "(", StringComparison.Ordinal);
            Assert.True(start > 0, name + " hittades inte");
            var next = source.IndexOf("[Http", start, StringComparison.Ordinal);
            return source.Substring(start, (next < 0 ? source.Length : next) - start);
        }

        [Fact]
        public void Medlemskatalogen_frågar_grinden_före_den_läser_klubbens_medlemmar()
        {
            var m = Method(Src("Controllers/ClubController.cs"), "GetClubMembers");
            var gate = m.IndexOf("SitePanelReadGate", StringComparison.Ordinal);
            var read = m.IndexOf("LoadActiveMembers", StringComparison.Ordinal);
            Assert.True(gate > 0 && read > 0 && gate < read, "GetClubMembers måste fråga SitePanelReadGate före LoadActiveMembers");
            Assert.Contains("\"members\", \"member-directory\"", m);
        }

        [Fact]
        public void Styrelsens_ändpunkter_frågar_grinden_före_de_läser()
        {
            var src = Src("Controllers/BoardRoleController.cs");
            var board = Method(src, "GetBoardMembers");
            Assert.True(board.IndexOf("CanReadBoardAsync", StringComparison.Ordinal) is > 0 and var g1 && g1 < board.IndexOf("_boardRoleService.GetBoardMembers", StringComparison.Ordinal));
            Assert.Contains("\"board\", \"board\"", src);
            var terms = Method(src, "GetExpiringTerms");
            Assert.True(terms.IndexOf("SitePanelReadGate", StringComparison.Ordinal) is > 0 and var g2 && g2 < terms.IndexOf("_boardRoleService.GetExpiringRoles", StringComparison.Ordinal));
            Assert.Contains("SiteVisibility.Styrelse", terms);
        }

        [Fact]
        public void Instruktörerna_och_Guldserien_frågar_grinden_före_de_läser()
        {
            var ins = Method(Src("Controllers/CertificationController.cs"), "PublicListForClub");
            Assert.True(ins.IndexOf("SitePanelReadGate", StringComparison.Ordinal) is > 0 and var g1 && g1 < ins.IndexOf("GetMemberIdsInGroupsAsync", StringComparison.Ordinal),
                "PublicListForClub måste fråga SitePanelReadGate före den läser grupper");
            Assert.Contains("\"members\", \"instructors\"", ins);

            var gs = Method(Src("Controllers/MarkenController.cs"), "GetClubGuldserieLeaderboard");
            Assert.True(gs.IndexOf("SitePanelReadGate", StringComparison.Ordinal) is > 0 and var g2 && g2 < gs.IndexOf("GetVerifiedSeriesForClubAsync", StringComparison.Ordinal),
                "GetClubGuldserieLeaderboard måste fråga SitePanelReadGate före den läser serier");
            Assert.Contains("\"members\", \"guldserie\"", gs);
        }
    }
}
