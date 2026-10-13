using HpskSite.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>A5: klubben i egenskaperna ⇔ medlemskapet inte avslutat.</summary>
    public class ClubAffiliationTests
    {
        [Fact]
        public void With_sets_primary_when_missing()
            => Assert.Equal(("7", ""), ClubAffiliation.With(null, "", 7));

        [Fact]
        public void With_appends_to_others()
            => Assert.Equal(("3", "5,7"), ClubAffiliation.With("3", "5", 7));

        [Fact]
        public void With_is_a_no_op_when_already_there()
        {
            Assert.Equal(("7", "5"), ClubAffiliation.With("7", "5", 7));
            Assert.Equal(("3", "5,7"), ClubAffiliation.With("3", " 5 , 7 ", 7));
        }

        [Fact]
        public void Without_removes_from_others()
            => Assert.Equal(("3", "5"), ClubAffiliation.Without("3", "5,7", 7));

        [Fact]
        public void Without_clears_primary_and_promotes_nothing()
            => Assert.Equal(((string?)null, "5"), ClubAffiliation.Without("7", "5", 7));

        [Fact]
        public void Without_absent_club_changes_nothing()
            => Assert.Equal(("3", "5"), ClubAffiliation.Without("3", "5", 7));

        [Theory]
        [InlineData("Utträdd", true)]
        [InlineData("Avliden", true)]
        [InlineData(" utträdd ", true)]
        [InlineData("Aktiv", false)]
        [InlineData("Vilande", false)]
        [InlineData(null, false)]
        public void IsEnded(string? status, bool ended)
            => Assert.Equal(ended, ClubAffiliation.IsEnded(status));
    }
}
