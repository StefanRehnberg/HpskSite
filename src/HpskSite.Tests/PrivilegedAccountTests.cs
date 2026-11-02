using HpskSite.Services.Security;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Säkerhetsplanen 2026-11-02 (H2/H2b/H4): vilka roller är privilegierade och vad en sammanslagning får föra över.</summary>
    public class PrivilegedAccountTests
    {
        [Theory]
        [InlineData("Administrators", true)]
        [InlineData("administrators", true)]
        [InlineData("RegionalAdmin_Halland", true)]
        [InlineData("RegionalAdmin_", true)]
        [InlineData("ClubAdmin_2604", true)]
        [InlineData("Users", false)]
        [InlineData("PendingApproval", false)]
        [InlineData("ClubAdminish", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsPrivilegedRole(string? role, bool expected) =>
            Assert.Equal(expected, PrivilegedAccount.IsPrivilegedRole(role));

        [Fact]
        public void HasPrivilegedRole_finner_en_privilegierad_bland_vanliga()
        {
            Assert.True(PrivilegedAccount.HasPrivilegedRole(new[] { "Users", "ClubAdmin_5" }));
            Assert.False(PrivilegedAccount.HasPrivilegedRole(new[] { "Users", "Trainers" }));
            Assert.False(PrivilegedAccount.HasPrivilegedRole(null));
        }

        [Fact]
        public void Sammanslagning_for_aldrig_over_sajt_eller_kretsadmin()
        {
            Assert.False(PrivilegedAccount.MayTransferInMerge("Administrators", _ => true));
            Assert.False(PrivilegedAccount.MayTransferInMerge("RegionalAdmin_Halland", _ => true));
        }

        [Fact]
        public void Sammanslagning_for_over_klubbadmin_bara_for_klubbar_aktoren_administrerar()
        {
            Assert.True(PrivilegedAccount.MayTransferInMerge("ClubAdmin_7", id => id == 7));
            Assert.False(PrivilegedAccount.MayTransferInMerge("ClubAdmin_8", id => id == 7));
            Assert.False(PrivilegedAccount.MayTransferInMerge("ClubAdmin_abc", _ => true)); // oläsligt id = nej
            Assert.True(PrivilegedAccount.MayTransferInMerge("Users", _ => false));
        }
    }
}
