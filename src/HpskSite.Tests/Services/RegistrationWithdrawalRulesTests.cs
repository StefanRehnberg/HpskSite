using System;
using FluentAssertions;
using HpskSite.Models.Competitions;
using HpskSite.Models.Registrations;
using HpskSite.Services.Registrations;
using Xunit;

namespace HpskSite.Tests.Services
{
    /// <summary>
    /// Avanmälan (2026-11-16): en skytt kan avanmäla sig SJÄLV fram till tävlingens första dag — även med betald avgift.
    /// Rapporterat: knappen på profilen pekade på en endpoint som inte fanns och tävlingssidan saknade vägen helt.
    /// </summary>
    public class RegistrationWithdrawalRulesTests
    {
        private static readonly DateTime Now = new(2026, 11, 16, 12, 0, 0);

        [Fact]
        public void A_future_competition_can_be_left()
        {
            var (allowed, reason) = RegistrationWithdrawalService.CanWithdraw(new CompetitionInfo { CompetitionDate = Now.AddDays(10) }, Now);
            allowed.Should().BeTrue();
            reason.Should().BeNull();
        }

        [Fact]
        public void A_competition_with_no_date_can_be_left() =>
            RegistrationWithdrawalService.CanWithdraw(new CompetitionInfo { CompetitionDate = null }, Now).Allowed.Should().BeTrue();

        [Theory]
        [InlineData(0)]   // tävlingen börjar nu
        [InlineData(-1)]  // pågår / är över
        [InlineData(-30)]
        public void A_competition_that_has_started_cannot_be_left_by_the_shooter(int daysFromNow)
        {
            var (allowed, reason) = RegistrationWithdrawalService.CanWithdraw(new CompetitionInfo { CompetitionDate = Now.AddDays(daysFromNow) }, Now);
            allowed.Should().BeFalse();
            reason.Should().Contain("Kontakta arrangören");
        }

        [Fact]
        public void An_external_competition_is_left_with_the_organiser() =>
            RegistrationWithdrawalService.CanWithdraw(new CompetitionInfo { CompetitionDate = Now.AddDays(10), IsExternal = true }, Now).Allowed.Should().BeFalse();

        [Fact]
        public void Paid_money_is_what_makes_a_refund_pending()
        {
            // Raden får Pending bara när pengar är inblandade — annars skulle rutan hos arrangören fyllas av avanmälningar utan något att göra.
            var w = new CompetitionWithdrawal { PaidAmount = 150m };
            w.MoneyInvolved.Should().Be(150m);
            new CompetitionWithdrawal { ClaimedAmount = 80m }.MoneyInvolved.Should().Be(80m);
            new CompetitionWithdrawal().MoneyInvolved.Should().Be(0m);
        }

        [Fact]
        public void The_stored_refund_words_never_change()
        {
            // CHECK-villkoret i tabellen (create-competition-withdrawal-table.sql) bär samma tre ord.
            RefundStatuses.None.Should().Be("None");
            RefundStatuses.Pending.Should().Be("Pending");
            RefundStatuses.Done.Should().Be("Done");
        }
    }
}
