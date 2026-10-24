using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using HpskSite.Models.Planning;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>P4 (2026-10-24, fråga 15): överskottets fördelning — ett underlag, flexibelt, summan alltid exakt.</summary>
    public class PlanningSurplusRulesTests
    {
        private static List<SurplusOrg> Orgs() => new()
        {
            new("club:1", "Varbergs PK", People: 3, Assignments: 6, Days: 4, Include: true, Weight: 0, Manual: false),
            new("club:2", "Falkenbergs PK", People: 1, Assignments: 1, Days: 1, Include: true, Weight: 0, Manual: false),
            new("region:9", "Hallands krets", People: 2, Assignments: 5, Days: 3, Include: true, Weight: 0, Manual: false),
        };

        [Fact]
        public void Fixed_lines_come_first_and_the_rest_by_people()
        {
            var (shares, remaining, err) = PlanningSurplusRules.Distribute(10000, new[] { new SurplusFixed { Label = "Förbundet", Amount = 1000 } }, SurplusKey.People, Orgs());
            err.Should().BeNull();
            remaining.Should().Be(9000);
            shares.Select(s => s.Amount).Should().Equal(4500, 1500, 3000);
        }

        [Fact]
        public void The_sum_is_always_exact_in_whole_kronor()
        {
            var (shares, remaining, _) = PlanningSurplusRules.Distribute(1000, new List<SurplusFixed>(), SurplusKey.Equal, Orgs());
            shares.Sum(s => s.Amount).Should().Be(remaining).And.Be(1000);
            shares.Select(s => s.Amount).Should().Equal(334, 333, 333);
        }

        [Theory]
        [InlineData(SurplusKey.Assignments, 5000, 833, 4167)]
        [InlineData(SurplusKey.Days, 5000, 1250, 3750)]
        public void Other_keys(string key, int first, int second, int third)
        {
            var (shares, _, _) = PlanningSurplusRules.Distribute(10000, new List<SurplusFixed>(), key, Orgs());
            shares.Select(s => s.Amount).Should().Equal(first, second, third);
        }

        [Fact]
        public void Own_shares_and_excluded_organisations()
        {
            var orgs = Orgs().Select((o, i) => o with { Weight = i == 0 ? 2 : 1, Include = i != 2 }).ToList();
            var (shares, _, err) = PlanningSurplusRules.Distribute(900, new List<SurplusFixed>(), SurplusKey.Manual, orgs);
            err.Should().BeNull();
            shares.Select(s => s.Amount).Should().Equal(600, 300, 0);
        }

        [Fact]
        public void Refusals()
        {
            PlanningSurplusRules.Distribute(100, new[] { new SurplusFixed { Amount = 200 } }, SurplusKey.People, Orgs()).Error.Should().Contain("fasta posterna");
            PlanningSurplusRules.Distribute(100, new List<SurplusFixed>(), SurplusKey.Manual, Orgs()).Error.Should().Contain("andel");
            PlanningSurplusRules.Distribute(-1, new List<SurplusFixed>(), SurplusKey.People, Orgs()).Error.Should().NotBeNull();
        }
    }
}
