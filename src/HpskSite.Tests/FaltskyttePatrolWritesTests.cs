using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Gruppmatchning och låsnamn för fältets patruller (patrullgeneratorn steg 1): en blandad patrull ("B+C") ska kunna fyllas av både B- och C-skyttar.</summary>
    public class FaltskyttePatrolWritesTests
    {
        [Theory]
        [InlineData("C", "C", true)]
        [InlineData("B+C", "C", true)]
        [InlineData("B+C", "B", true)]
        [InlineData("A+R", "C", false)]
        [InlineData("", "C", true)]
        [InlineData(null, "A", true)]
        [InlineData("A+R", "A", true)]
        [InlineData("AR", "A", false)]
        [InlineData("b + c", "C", true)]
        public void GroupMatches_treats_the_label_as_a_code_list(string? label, string code, bool expected) =>
            Assert.Equal(expected, FaltskyttePatrolWrites.GroupMatches(label, code));

        [Fact]
        public void GroupMatchSql_matches_exact_first_last_and_middle()
        {
            var sql = FaltskyttePatrolWrites.GroupMatchSql("p.WeaponGroup", "@1");
            Assert.Contains("p.WeaponGroup = @1", sql);
            Assert.Contains("p.WeaponGroup LIKE @1 + '+%'", sql);
            Assert.Contains("p.WeaponGroup LIKE '%+' + @1", sql);
            Assert.Contains("p.WeaponGroup LIKE '%+' + @1 + '+%'", sql);
        }

        [Fact]
        public void LockName_is_one_per_competition_and_stable() =>
            Assert.Equal("falt-startlista:123", FaltskyttePatrolWrites.LockName(123));
    }
}
