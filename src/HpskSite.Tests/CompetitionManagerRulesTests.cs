using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.CompetitionCalls;
using Xunit;

namespace HpskSite.Tests
{
    public class CompetitionManagerRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 20);
        private static CompetitionManagerWish W(int member, string status) => new() { MemberId = member, Status = status };

        [Fact]
        public void Seeking_when_no_manager_and_future()
            => Assert.True(CompetitionManagerRules.IsSeeking(false, Today.AddDays(30), Today, hasAnyManager: false, hasAnyWishRow: false));

        [Fact]
        public void Not_seeking_when_closed()
            => Assert.False(CompetitionManagerRules.IsSeeking(true, Today.AddDays(30), Today, false, true));

        [Fact]
        public void Not_seeking_when_date_passed()
            => Assert.False(CompetitionManagerRules.IsSeeking(false, Today.AddDays(-1), Today, false, false));

        [Fact]
        public void Seeking_without_date_is_allowed()
            => Assert.True(CompetitionManagerRules.IsSeeking(false, null, Today, false, false));

        [Fact]
        public void Existing_managers_without_the_feature_do_not_make_it_seek()
            => Assert.False(CompetitionManagerRules.IsSeeking(false, Today.AddDays(30), Today, hasAnyManager: true, hasAnyWishRow: false));

        [Fact]
        public void Keeps_seeking_after_first_appointment_until_closed()
            => Assert.True(CompetitionManagerRules.IsSeeking(false, Today.AddDays(30), Today, hasAnyManager: true, hasAnyWishRow: true));

        [Theory]
        [InlineData(true, false, null, true, "appointed")]
        [InlineData(true, false, null, false, "open")]
        [InlineData(false, false, null, false, null)]   // söker inte → raden visas inte
        [InlineData(true, false, "Aktiv", false, "wished")]
        [InlineData(false, true, "Aktiv", false, "filled-not-you")]
        public void MemberState_matrix(bool seeking, bool closed, string? wish, bool manager, string? expected)
            => Assert.Equal(expected, CompetitionManagerRules.MemberState(seeking, closed, wish, manager));

        [Fact]
        public void Closed_role_shows_filled_only_to_those_who_wished_never_to_others()
        {
            Assert.Equal("filled-not-you", CompetitionManagerRules.MemberState(false, true, "Avslutad", false));
            Assert.Null(CompetitionManagerRules.MemberState(false, true, null, false));
            Assert.Null(CompetitionManagerRules.MemberState(false, false, null, false));
        }

        [Fact]
        public void Appointed_member_always_sees_appointed_even_when_closed()
            => Assert.Equal("appointed", CompetitionManagerRules.MemberState(false, true, "Utsedd", true));

        [Fact]
        public void Filled_notice_goes_to_active_wishers_only_once_each()
        {
            var who = CompetitionManagerRules.WhoGetsFilledNotice(new[]
            {
                W(1, "Aktiv"), W(1, "Aktiv"), W(2, "Utsedd"), W(3, "Avslutad"), W(4, "Aktiv"),
            });
            Assert.Equal(new[] { 1, 4 }, who.OrderBy(x => x));
        }

        [Theory]
        [InlineData("Aktiv", -1, true)]
        [InlineData("Avslutad", -1, true)]
        [InlineData("Utsedd", -1, false)]   // en utsedd behålls som historik
        [InlineData("Aktiv", 5, false)]     // tävlingen ligger i framtiden
        public void Purge_only_after_the_competition_and_never_for_appointed(string status, int daysFromToday, bool expected)
            => Assert.Equal(expected, CompetitionManagerRules.ShouldPurge(status, Today.AddDays(daysFromToday), Today));

        [Fact]
        public void Why_not_wish_explains_each_refusal()
        {
            Assert.NotNull(CompetitionManagerRules.WhyNotWish(true, false, isMemberOfClub: false, alreadyManager: false));
            Assert.NotNull(CompetitionManagerRules.WhyNotWish(true, false, true, alreadyManager: true));
            Assert.NotNull(CompetitionManagerRules.WhyNotWish(false, true, true, false));
            Assert.NotNull(CompetitionManagerRules.WhyNotWish(false, false, true, false));
            Assert.Null(CompetitionManagerRules.WhyNotWish(true, false, true, false));
        }
    }
}
