using FluentAssertions;
using HpskSite.Models.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 3: personnyckeln är EN funktion.</summary>
    public class StaffPersonKeyTests
    {
        [Fact]
        public void En_medlem_har_sitt_id_som_nyckel()
            => StaffPersonKey.For(5515, "Vem som helst").Should().Be("m:5515");

        [Theory]
        [InlineData("Kalle Anka", "n:kalle anka")]
        [InlineData("  Kalle Anka ", "n:kalle anka")]
        [InlineData("KALLE ANKA", "n:kalle anka")]
        public void Utan_konto_namnet_trimmat_i_gemener(string name, string key)
            => StaffPersonKey.For(null, name).Should().Be(key);

        [Theory]
        [InlineData(0)]
        [InlineData(-3)]
        public void Id_noll_eller_negativt_ar_inget_konto(int id)
            => StaffPersonKey.For(id, "Åsa Öberg").Should().Be("n:åsa öberg");

        [Fact]
        public void Tomt_namn_utan_konto_ger_tom_nyckel_efter_prefixet()
            => StaffPersonKey.For(null, null).Should().Be("n:");

        [Theory]
        [InlineData("m:5515", 5515)]
        [InlineData("m:0", 0)]
        [InlineData("m:-4", 0)]
        [InlineData("m:abc", 0)]
        [InlineData("n:5515", 0)]
        [InlineData(null, 0)]
        public void Medlemmens_id_ur_nyckeln(string? key, int expected)
            => StaffPersonKey.MemberIdOf(key).Should().Be(expected);
    }
}
