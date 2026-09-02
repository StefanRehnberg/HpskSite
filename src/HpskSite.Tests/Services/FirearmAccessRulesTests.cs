using System;
using System.Linq;
using System.Reflection;
using HpskSite.Models.Firearms;
using HpskSite.Services.Firearms;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Behörighetsbesluten, prövade UTTÖMMANDE. Två av sanningstabellerna är hela vitsen:
    ///
    ///   1. <b>Läsning kräver BÅDE gruppen och ett aktivt styrelseuppdrag.</b> Ett rent
    ///      gruppmedlemskap lever vidare efter en avgång, och då är behörigheten kvar utan att någon
    ///      märker det.
    ///   2. <b>Att utse kräver klubbadmin OCH styrelseuppdrag.</b> `IsClubAdminForClub` viker in
    ///      klubbens KRETSADMINISTRATÖRER, så utan konjunktionen kunde en kretsadmin utan uppdrag i
    ///      klubben utse den som får läsa klubbens medlemmars vapeninnehav.
    ///
    /// Plus ett strukturellt test: att läsregeln inte har någon sajtadmin-parameter alls. Det är den
    /// enda spärren mot att någon senare "harmoniserar" den med resten av kodbasen genom att lägga
    /// till ett `isSiteAdmin || ...`, vilket tyst skulle bryta det publika löftet.
    /// </summary>
    public class FirearmAccessRulesTests
    {
        // ── Läsning ──────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(false, false, false)] // varken grupp eller uppdrag
        [InlineData(true, false, false)]  // gruppen kvar efter avgången — DET FALL DESIGNEN FINNS FÖR
        [InlineData(false, true, false)]  // styrelsemedlem utan behörigheten
        [InlineData(true, true, true)]    // enda kombinationen som ger läsrätt
        public void ViewerHasAccess_requires_both(bool holdsGroup, bool boardSeat, bool expected)
        {
            Assert.Equal(expected, FirearmAccessRules.ViewerHasAccess(holdsGroup, boardSeat));
        }

        [Fact]
        public void Losing_the_board_seat_revokes_access_immediately()
        {
            // Behörigheten är HÄRLEDD: gruppmedlemskapet är oförändrat, bara styrelseraden är borta.
            // Det är hela skälet att inget städjobb behövs vid en avgång.
            Assert.True(FirearmAccessRules.ViewerHasAccess(holdsGroupForClub: true, hasActiveBoardSeatInSameClub: true));
            Assert.False(FirearmAccessRules.ViewerHasAccess(holdsGroupForClub: true, hasActiveBoardSeatInSameClub: false));
        }

        [Fact]
        public void ViewerHasAccess_takes_no_site_admin_parameter()
        {
            // ⚠️ STRUKTURELLT TEST. Faller det här har någon lagt till en sajtadmin-väg i läsregeln,
            // och då är löftet "bara du och klubbens föreningsintygsansvarige" inte längre sant.
            // Rätt åtgärd är att ta bort parametern, inte att uppdatera testet.
            var method = typeof(FirearmAccessRules).GetMethod(
                nameof(FirearmAccessRules.ViewerHasAccess), BindingFlags.Public | BindingFlags.Static);

            Assert.NotNull(method);
            var names = method!.GetParameters().Select(p => p.Name).ToList();

            Assert.Equal(2, names.Count);
            Assert.DoesNotContain(names, n => (n ?? "").Contains("admin", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(names, n => (n ?? "").Contains("term", StringComparison.OrdinalIgnoreCase));
        }

        // ── Att utse ─────────────────────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(false, false, false, false)] // ingen behörighet alls
        [InlineData(false, true, false, false)]  // ⚠️ KRETSADMINFÄLLAN: klubbadmin utan styrelseuppdrag
        [InlineData(false, false, true, false)]  // styrelsemedlem utan klubbadmin
        [InlineData(false, true, true, true)]    // klubbadmin OCH styrelsemedlem
        [InlineData(true, false, false, true)]   // sajtadmin — den kvarvarande vägen
        [InlineData(true, true, true, true)]
        public void CanAssign_truth_table(bool siteAdmin, bool clubAdmin, bool boardSeat, bool expected)
        {
            Assert.Equal(expected, FirearmAccessRules.CanAssign(siteAdmin, clubAdmin, boardSeat));
        }

        [Fact]
        public void A_regional_admin_without_a_board_seat_cannot_assign()
        {
            // Det konkreta felet: IsClubAdminForClub svarar TRUE för klubbens kretsadministratörer
            // (står i metodens egen dokumentation). Utan andra halvan av konjunktionen kunde en
            // kretsadmin utse den som läser klubbens medlemmars vapeninnehav.
            Assert.False(FirearmAccessRules.CanAssign(
                isSiteAdmin: false, isClubAdmin: true, hasActiveBoardSeatInSameClub: false));
        }

        [Fact]
        public void Assigning_and_reading_are_different_questions()
        {
            // Sajtadmin får UTSE men aldrig LÄSA. Just den delningen är löftet uttryckt i kod.
            Assert.True(FirearmAccessRules.CanAssign(
                isSiteAdmin: true, isClubAdmin: false, hasActiveBoardSeatInSameClub: false));
            Assert.False(FirearmAccessRules.ViewerHasAccess(
                holdsGroupForClub: false, hasActiveBoardSeatInSameClub: false));
        }

        // ── Varningen vid borttagning ────────────────────────────────────────────────────────────

        [Theory]
        [InlineData(true, 1, true)]   // sista läsaren tas bort
        [InlineData(true, 2, false)]  // en av två — ingen händelse
        [InlineData(true, 3, false)]
        [InlineData(false, 1, false)] // personen är inte läsare
        [InlineData(false, 0, false)]
        [InlineData(true, 0, true)]   // inkonsekvent indata: varna hellre än att tiga
        public void RemovalWarning_only_when_the_last_viewer_goes(bool isViewer, int before, bool expected)
        {
            Assert.Equal(expected, FirearmAccessRules.RemovalWouldLeaveClubWithoutViewer(isViewer, before));
        }

        // ── Gruppnamnet ──────────────────────────────────────────────────────────────────────────

        [Fact]
        public void Group_name_is_pinned()
        {
            // ⚠️ Gruppnamnet ÄR behörigheten. Ändras formen tappar varje redan utsedd person sin
            // behörighet, tyst — inget kompileringsfel, ingen felrad, bara en klubb som plötsligt
            // inte kan skriva föreningsintyg. Mönstret följer Skjutledare_{clubId}.
            Assert.Equal("Foreningsintygsansvarig_2604", FirearmAuthorizationService.GroupName(2604));
            Assert.Equal("Foreningsintygsansvarig_", FirearmAuthorizationService.GroupPrefix);
        }

        // ── Utfallet som går vidare till loggen ──────────────────────────────────────────────────

        [Fact]
        public void Read_access_carries_the_reason_into_the_log()
        {
            // Grunden går rakt in i FirearmAccessLog, så behörighetsbeslutet och loggraden inte kan
            // säga emot varandra. En läsning av eget innehav ska INTE visas som en främmande
            // läsning i medlemmens logg.
            var own = new FirearmReadAccess(true, FirearmAccessReason.Owner, 1078, null);
            var byClub = new FirearmReadAccess(true, FirearmAccessReason.Foreningsintyg, 5514, 2604);

            Assert.False(own.IsForeignRead);
            Assert.True(byClub.IsForeignRead);
            Assert.False(FirearmReadAccess.Denied.IsForeignRead);
            Assert.False(FirearmReadAccess.Denied.Allowed);
        }

        [Fact]
        public void Access_reasons_are_pinned()
        {
            // Lagras som strängar i FirearmAccessLog.Reason. En omdöpning gör historiska rader
            // omärkta i medlemmens vy.
            Assert.Equal("Owner", FirearmAccessReason.Owner);
            Assert.Equal("Foreningsintyg", FirearmAccessReason.Foreningsintyg);
            Assert.Equal("ClubWeapon", FirearmAccessReason.ClubWeapon);
            Assert.Equal(3, FirearmAccessReason.All.Length);
        }

        [Fact]
        public void An_unknown_reason_renders_as_itself_not_as_blank()
        {
            // En rad skriven av en framtida version ska visa något — ett tomt fält i en
            // åtkomstlogg läses som "ingen läste".
            Assert.Equal("Owner", FirearmAccessReason.Label("Owner") == "Du själv" ? "Owner" : "fel");
            Assert.Equal("NagotNytt", FirearmAccessReason.Label("NagotNytt"));
        }

        // ── Mandatets slutdatum ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Expired_term_is_flagged_but_never_revokes()
        {
            // En styrelse sitter kvar från mandatets utgång till nästa årsmöte. Skulle luckan stänga
            // läsrätten stod klubben utan läsare i just det fönstret.
            var expired = new FirearmViewerCandidate { TermEndsDate = DateTime.Today.AddDays(-1) };
            var current = new FirearmViewerCandidate { TermEndsDate = DateTime.Today.AddDays(30) };
            var endsToday = new FirearmViewerCandidate { TermEndsDate = DateTime.Today };
            var noTerm = new FirearmViewerCandidate { TermEndsDate = null };

            Assert.True(expired.TermExpired);
            Assert.False(current.TermExpired);
            Assert.False(endsToday.TermExpired);  // sista dagen är INOM mandatet
            Assert.False(noTerm.TermExpired);     // ingen mandattid = aldrig utgången

            // Och regeln bryr sig inte om något av det:
            Assert.True(FirearmAccessRules.ViewerHasAccess(true, true));
        }
    }
}
