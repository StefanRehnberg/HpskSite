using FluentAssertions;
using HpskSite.Models;
using HpskSite.Services;
using HpskSite.Services.Hosting;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 3b B: de nya tabellerna står i startkontrollen, gallrings-/sammanslagningskartan och tidsregistret.</summary>
    public class MembershipAgeRulesSchemaTests
    {
        [Theory]
        [InlineData("ClubMembershipTypeAge")]
        [InlineData("ClubMemberAgeRuleOptOut")]
        [InlineData("ClubMembershipTypeChange")]
        public void Startkontrollen_namnger_tabellen(string table)
            => MembershipFeeSchemaGuardHostedService.Tables.Should().Contain(table);

        [Theory]
        [InlineData("ClubMemberAgeRuleOptOut")]
        [InlineData("ClubMembershipTypeChange")]
        public void Medlemsraderna_finns_i_gallringskartan_och_foljer_med_en_sammanslagning(string table)
            => MemberDataPurgeService.SubjectTables.Should().Contain(t => t.Table == table && t.Column == "MemberId");

        [Fact]
        public void Aktorerna_ligger_utanfor_kartan()
            => MemberDataPurgeService.SubjectTables.Should().NotContain(t => t.Column == "ByMemberId");

        [Theory]
        [InlineData("ClubMembershipTypeAge.CreatedUtc")]
        [InlineData("ClubMembershipTypeAge.UpdatedUtc")]
        [InlineData("ClubMemberAgeRuleOptOut.CreatedUtc")]
        [InlineData("ClubMembershipTypeChange.ChangedUtc")]
        public void Utc_kolumnerna_ar_registrerade(string column)
            => StoredTimeBasis.UtcColumns.Should().Contain(column);

        [Fact]
        public void Aktionerna_ar_de_lagrade_strangarna()
        {
            MembershipTypeChangeAction.Applied.Should().Be("Applied");
            MembershipTypeChangeAction.Kept.Should().Be("Kept");
            MembershipTypeChangeAction.Unlocked.Should().Be("Unlocked");
            MembershipTypeChangeAction.Undone.Should().Be("Undone");
        }
    }
}
