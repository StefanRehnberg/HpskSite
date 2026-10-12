using FluentAssertions;
using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests.Services
{
    public class ClubMembershipSyncHandlerTests
    {
        [Fact]
        public void Huvudklubben_forst_sedan_ovriga_utan_dubbletter()
            => ClubMembershipSyncHandler.ClubIds("2604", "2610, 2604,2614").Should().Equal(2604, 2610, 2614);

        [Fact]
        public void Tomt_och_skrap_ger_inga_klubbar()
            => ClubMembershipSyncHandler.ClubIds("", " ,abc,0,-3").Should().BeEmpty();

        [Fact]
        public void Bara_ovriga_klubbar_utan_huvudklubb()
            => ClubMembershipSyncHandler.ClubIds(null, "2610").Should().Equal(2610);

        [Fact]
        public void Borttagen_klubb_raknas_bara_nar_den_faktiskt_forsvann()
        {
            ClubMembershipSyncHandler.Removed(new[] { 2604, 2610 }, new[] { 2604 }).Should().Equal(2610);
            ClubMembershipSyncHandler.Removed(new[] { 2604, 2610 }, new[] { 2610, 2604 }).Should().BeEmpty("ordningen spelar ingen roll");
            // Byte av huvudklubb = den gamla är lämnad.
            ClubMembershipSyncHandler.Removed(new[] { 2604 }, new[] { 2610 }).Should().Equal(2604);
        }
    }
}
