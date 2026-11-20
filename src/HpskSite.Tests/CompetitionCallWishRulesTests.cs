using FluentAssertions;
using HpskSite.Models.CompetitionCalls;
using HpskSite.Services.CompetitionCalls;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Önskemålens regler (kretsutlysningen F3): när en klubb får önska, vad som är giltigt och vad klubben ser.</summary>
    public class CompetitionCallWishRulesTests
    {
        private static readonly DateTime Today = new(2026, 11, 20);

        [Fact]
        public void Bara_en_utlyst_utlysning_tar_onskemal()
        {
            CompetitionCallRules.CanWish(CompetitionCallStatus.Utlyst, null, Today, false).Should().BeTrue();
            foreach (var s in new[] { CompetitionCallStatus.Planering, CompetitionCallStatus.Tilldelad, CompetitionCallStatus.Skapad, CompetitionCallStatus.Installd })
                CompetitionCallRules.CanWish(s, null, Today, true).Should().BeFalse($"läget {s}");
        }

        [Fact]
        public void Sista_dagen_galler_klubben_men_inte_kretsen_som_skriver_in_at_den()
        {
            var deadline = new DateTime(2026, 11, 19);
            CompetitionCallRules.CanWish(CompetitionCallStatus.Utlyst, deadline, Today, false).Should().BeFalse();
            CompetitionCallRules.CanWish(CompetitionCallStatus.Utlyst, deadline, Today, true).Should().BeTrue();
            CompetitionCallRules.CanWish(CompetitionCallStatus.Utlyst, Today, Today, false).Should().BeTrue("själva sista dagen räknas");
        }

        [Fact]
        public void Varje_avstangd_onskan_har_en_forklaring()
        {
            CompetitionCallRules.WhyNotWish(CompetitionCallStatus.Utlyst, new DateTime(2026, 11, 1), Today, false).Should().Contain("Sista dag");
            CompetitionCallRules.WhyNotWish(CompetitionCallStatus.Tilldelad, null, Today, false).Should().Contain("utsett");
            CompetitionCallRules.WhyNotWish(CompetitionCallStatus.Planering, null, Today, false).Should().Contain("inte utlyst");
            CompetitionCallRules.WhyNotWish(CompetitionCallStatus.Utlyst, null, Today, false).Should().BeNull();
        }

        [Fact]
        public void Forslaget_till_dag_ska_ligga_i_utlysningens_ar()
        {
            var c = new CompetitionCall { Year = 2027 };
            CompetitionCallRules.ValidateWish(c, new DateTime(2027, 5, 1), null).Should().BeNull();
            CompetitionCallRules.ValidateWish(c, new DateTime(2028, 5, 1), null).Should().Contain("2027");
            CompetitionCallRules.ValidateWish(c, null, new string('x', CompetitionCallRules.MaxWishNote + 1)).Should().Contain("högst");
        }

        [Fact]
        public void Klubbens_lage_styr_kortet()
        {
            CompetitionCallRules.ClubState(CompetitionCallStatus.Utlyst, null, 5, false).Should().Be("open");
            CompetitionCallRules.ClubState(CompetitionCallStatus.Utlyst, null, 5, true).Should().Be("wished");
            CompetitionCallRules.ClubState(CompetitionCallStatus.Tilldelad, 5, 5, true).Should().Be("awarded-me");
            CompetitionCallRules.ClubState(CompetitionCallStatus.Skapad, 6, 5, false).Should().Be("awarded-other");
            CompetitionCallRules.ClubState(CompetitionCallStatus.Planering, null, 5, false).Should().Be("hidden");
        }

        [Fact]
        public void Klubbarna_ser_aldrig_planering_eller_installda()
        {
            CompetitionCallRules.VisibleToClubs(CompetitionCallStatus.Planering).Should().BeFalse();
            CompetitionCallRules.VisibleToClubs(CompetitionCallStatus.Installd).Should().BeFalse();
            CompetitionCallRules.VisibleToClubs(CompetitionCallStatus.Utlyst).Should().BeTrue();
            CompetitionCallRules.VisibleToClubs(CompetitionCallStatus.Tilldelad).Should().BeTrue();
        }
    }
}
