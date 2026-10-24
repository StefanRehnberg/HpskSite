using System;
using FluentAssertions;
using HpskSite.Models.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// P4 (2026-10-24): SM-annonsen i Nationellt Pistolskytte. Den stora SM-veckan annonseras i nr 2, ett SM senare på året
    /// (springskytte) i nr 3. Manusstoppen uppskattas ur förbundets utgivningsplan 2026.
    /// </summary>
    public class NpIssuesTests
    {
        [Theory]
        [InlineData(1, "2026-02-23")] // utgivningsplanen 2026
        [InlineData(2, "2026-05-04")]
        [InlineData(3, "2026-08-10")]
        [InlineData(4, "2026-11-02")]
        public void The_2026_plan_is_reproduced(int issue, string expected) =>
            NpIssues.ManuscriptDeadline(2026, issue).ToString("yyyy-MM-dd").Should().Be(expected);

        [Fact]
        public void The_big_SM_week_goes_in_issue_2()
        {
            var r = NpIssues.ForCompetition(new DateTime(2028, 7, 10));
            (r.Year, r.Issue).Should().Be((2028, 2));
            r.Deadline.Should().Be(new DateTime(2028, 5, 1)); // första måndagen i maj 2028 är den 1 maj
        }

        [Fact]
        public void An_SM_later_in_the_year_goes_in_issue_3()
        {
            var r = NpIssues.ForCompetition(new DateTime(2027, 10, 9));
            (r.Year, r.Issue).Should().Be((2027, 3));
            r.Deadline.Should().Be(new DateTime(2027, 8, 9));
        }

        [Fact]
        public void An_issue_that_comes_out_too_close_to_the_competition_is_not_chosen()
        {
            // Nr 3 2026 kommer ut runt 21 september — för sent för en tävling 30 september (mindre än två veckor).
            NpIssues.ForCompetition(new DateTime(2026, 9, 30)).Issue.Should().Be(2);
        }

        [Fact]
        public void Early_in_the_year_it_is_last_years_issue_4()
        {
            var r = NpIssues.ForCompetition(new DateTime(2027, 2, 20));
            (r.Year, r.Issue).Should().Be((2026, 4));
        }
    }
}
