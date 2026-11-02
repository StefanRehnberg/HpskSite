using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (A5/H4): vad en sammanslagning får göra med roller.</summary>
    public class MergeRoleRulesTests
    {
        private static readonly Func<int, bool> Administers7 = id => id == 7;

        [Fact]
        public void Klubbadmin_far_inte_foera_over_sajtadmin_eller_kretsadmin()
        {
            Assert.False(MergeRoleRules.MayTransfer("Administrators", false, Administers7));
            Assert.False(MergeRoleRules.MayTransfer("RegionalAdmin_Halland", false, Administers7));
        }

        [Fact]
        public void Klubbadmin_far_foera_over_klubbadmin_bara_for_sin_klubb()
        {
            Assert.True(MergeRoleRules.MayTransfer("ClubAdmin_7", false, Administers7));
            Assert.False(MergeRoleRules.MayTransfer("ClubAdmin_8", false, Administers7));
            Assert.True(MergeRoleRules.MayTransfer("Users", false, Administers7));
        }

        [Fact]
        public void Sajtadmin_far_allt()
        {
            Assert.True(MergeRoleRules.MayTransfer("Administrators", true, _ => false));
            Assert.True(MergeRoleRules.MayTransfer("RegionalAdmin_X", true, _ => false));
            Assert.Null(MergeRoleRules.LoserRefusal("Anna", new[] { "Administrators" }, true, _ => false));
        }

        [Fact]
        public void Forlorare_med_sajtadminroll_far_inte_raderas_av_klubbadmin()
        {
            var why = MergeRoleRules.LoserRefusal("Anna Andersson", new[] { "Users", "Administrators" }, false, Administers7);
            Assert.NotNull(why);
            Assert.Contains("sajtadministratör", why);
            Assert.Contains("Anna Andersson", why);
        }

        [Fact]
        public void Forlorare_som_ar_admin_for_en_annan_klubb_nekas_men_egen_klubb_gar()
        {
            Assert.NotNull(MergeRoleRules.LoserRefusal("B", new[] { "ClubAdmin_8" }, false, Administers7));
            Assert.Null(MergeRoleRules.LoserRefusal("B", new[] { "ClubAdmin_7", "Users" }, false, Administers7));
        }

        [Fact]
        public void Vanlig_forlorare_nekas_inte()
        {
            Assert.Null(MergeRoleRules.LoserRefusal("C", new[] { "Users", "PendingApproval" }, false, Administers7));
            Assert.Null(MergeRoleRules.LoserRefusal("C", Array.Empty<string>(), false, Administers7));
        }
    }
}
