using System.Runtime.CompilerServices;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Dialogen Medlemskap (Stefan 2026-11-26): klubbadmin OCH kassören hanterar medlemmens typ, status och tillhörighet;
    /// bara kassören arbetar med avgifter. Beslutet är en ren funktion, och en vaktkontroll ser till att avgiftsändpunkterna
    /// inte råkar öppnas för klubbadmin.
    /// </summary>
    public class MembershipAccessTests
    {
        [Theory]
        [InlineData(true, false, true)]    // klubbadmin
        [InlineData(false, true, true)]    // rent kassör
        [InlineData(true, true, true)]
        [InlineData(false, false, false)]  // styrelse, revisor, vanlig medlem
        public void Klubbadmin_eller_kassor_far_hantera_medlemskapet(bool admin, bool treasurer, bool expected)
            => Assert.Equal(expected, MembershipAccess.Decide(admin, treasurer));

        [Theory]
        [InlineData(true, false, true)]    // revisor som också är klubbadmin
        [InlineData(false, true, true)]    // revisor som också är kassör i registret (av misstag)
        [InlineData(true, true, true)]
        [InlineData(false, false, true)]
        public void Revisorn_far_aldrig_hantera_medlemskapet_vilken_annan_roll_hen_an_har(bool admin, bool treasurer, bool auditor)
            => Assert.False(MembershipAccess.Decide(admin, treasurer, auditor));

        [Fact]
        public void Controllern_prover_revisorn_forst()
        {
            var src = Src(Path.Combine("Services", "MembershipAccess.cs"));
            var i = src.IndexOf("CanManageMemberAsync(int clubId)", StringComparison.Ordinal);
            Assert.True(i >= 0);
            var body = src.Substring(i, Math.Min(700, src.Length - i));
            Assert.True(body.IndexOf("IsAuditorAsync", StringComparison.Ordinal) < body.IndexOf("IsClubAdminForClub", StringComparison.Ordinal),
                "Revisorsspärren måste ligga före administratörsprövningen.");
        }

        private static string Src(string rel, [CallerFilePath] string thisFile = "") =>
            File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "HpskSite", rel)));

        // Metoderna i MembershipFeeAdminController som hör till MEDLEMSKAPET (typ, ålder, förbund per medlem) och
        // metoderna som är PENGAR. Ändras en grind ska det vara ett beslut, inte en olycka.
        private static readonly string[] MemberMethods =
            { "SaveAgeLimits", "ApplyAgeProposals", "UndoAgeProposals", "SetAgeRuleOptOut", "SaveMemberType", "DeleteMemberType", "SetMembersType", "SetMemberOptions" };

        private static readonly string[] FeeMethods =
            { "SaveCategory", "DeleteCategory", "SaveFeePart", "DeleteFeePart", "SaveFeeOption", "DeleteFeeOption", "GenerateCharges", "EnsureClubCharge" };

        private static string BodyStart(string src, string method)
        {
            var i = src.IndexOf("public async Task<IActionResult> " + method + "(", StringComparison.Ordinal);
            Assert.True(i >= 0, "Metoden finns inte: " + method);
            return src.Substring(i, Math.Min(500, src.Length - i));
        }

        [Fact]
        public void Typ_alder_och_forbund_per_medlem_grindas_pa_klubbadmin_eller_kassor()
        {
            var src = Src(Path.Combine("Controllers", "MembershipFeeAdminController.cs"));
            foreach (var m in MemberMethods)
            {
                var b = BodyStart(src, m);
                Assert.Contains("_memberAccess.CanManageMemberAsync(clubId)", b);
                Assert.DoesNotContain("_feeAccess.CanWriteAsync(clubId)", b);
            }
        }

        [Fact]
        public void Avgifterna_forblir_kassorens()
        {
            var src = Src(Path.Combine("Controllers", "MembershipFeeAdminController.cs"));
            foreach (var m in FeeMethods)
            {
                var b = BodyStart(src, m); // kastar om metoden bytt namn: ett prov som aldrig hittar sitt mål kan aldrig falla
                Assert.Contains("_feeAccess.CanWriteAsync", b);
                Assert.DoesNotContain("CanManageMemberAsync", b);
            }
        }

        [Fact]
        public void Dialogen_lamnar_aldrig_ut_priser_eller_belopp_till_klubbadmin()
        {
            var src = Src(Path.Combine("Controllers", "ClubMembershipController.Card.cs"));
            // Förbunden skickas som id och namn, aldrig med pris.
            Assert.Contains("options = _feeSettings.GetOptions(clubId, includeInactive: false).Select(o => new { id = o.Id, name = o.Name })", src);
            // Avgiftens text (belopp) bara när anroparen får skriva avgiften.
            Assert.Contains("if (canSeeFees) chargeText", src);
        }

        [Fact]
        public void Ingen_klubbandring_utan_ratt_att_redigera_personen()
        {
            var src = Src(Path.Combine("Controllers", "ClubMembershipController.Card.cs"));
            Assert.Contains("CanEditMemberAsync(memberId)", src);
            // Redigera medlem får aldrig tömma klubbarna genom att utelämna fälten.
            var admin = Src(Path.Combine("Controllers", "MemberAdminController.cs"));
            Assert.Contains("Request.Form.ContainsKey(\"primaryClubId\")", admin);
        }
    }
}
