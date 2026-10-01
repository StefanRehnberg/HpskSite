using FluentAssertions;
using HpskSite.Models;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Kretsens uppdrag (kretsgranskning fas 1): vilka roller får ges till vilken ägartyp.
    /// </summary>
    public class BoardRoleOwnerTests
    {
        [Theory]
        [InlineData(BoardRoleDefinitions.RoleResultatgranskare)]
        [InlineData(BoardRoleDefinitions.RoleBangranskare)]
        [InlineData(BoardRoleDefinitions.RoleTavlingsansvarig)]
        public void KretsUppdrag_GarBaraTillEnKrets(string key)
        {
            BoardRoleDefinitions.AppliesTo(key, DocumentOwnerType.Region).Should().BeTrue();
            BoardRoleDefinitions.AppliesTo(key, DocumentOwnerType.Club).Should().BeFalse();
            BoardRoleDefinitions.IsKretsUppdrag(key).Should().BeTrue();
        }

        [Theory]
        [InlineData("Ordforande")]
        [InlineData("Kassor")]
        [InlineData("Revisor")]
        [InlineData("Valberedning")]
        [InlineData("Custom")]
        public void VanligaRoller_GarTillBadaAgartyperna(string key)
        {
            BoardRoleDefinitions.AppliesTo(key, DocumentOwnerType.Club).Should().BeTrue();
            BoardRoleDefinitions.AppliesTo(key, DocumentOwnerType.Region).Should().BeTrue();
            BoardRoleDefinitions.IsKretsUppdrag(key).Should().BeFalse();
        }

        [Fact]
        public void OkandNyckel_GarInteAtNagon()
        {
            BoardRoleDefinitions.AppliesTo("Distrikt", DocumentOwnerType.Club).Should().BeFalse();
            BoardRoleDefinitions.AppliesTo("Distrikt", DocumentOwnerType.Region).Should().BeFalse();
        }

        [Fact]
        public void OkandAgartyp_GarInteAtNagon()
        {
            BoardRoleDefinitions.AppliesTo("Ordforande", 7).Should().BeFalse();
        }

        [Fact]
        public void KretsUppdrag_ArAldrigStyrelseplatser()
        {
            // Uppdragen får aldrig räknas i beslutsförheten eller kallas till styrelsemöten.
            foreach (var key in BoardRoleDefinitions.KretsUppdragRoleKeys)
                BoardRoleDefinitions.GetDefaultIsBoardMember(key).Should().BeFalse(key);
        }

        [Fact]
        public void VarjeUppdrag_HarEtikettOchUppgift()
        {
            foreach (var key in BoardRoleDefinitions.KretsUppdragRoleKeys)
            {
                BoardRoleDefinitions.AllRoles.Should().Contain(r => r.Key == key, key);
                BoardRoleDefinitions.KretsUppdragTask(key).Should().NotBeNullOrWhiteSpace(key);
            }
        }

        [Fact]
        public void Nycklarna_ArUnika()
        {
            BoardRoleDefinitions.AllRoles.Select(r => r.Key).Should().OnlyHaveUniqueItems();
        }
    }
}
