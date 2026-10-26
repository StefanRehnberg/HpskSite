using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 4–6, "Inte gjort" 1: skjutledare och stationschef lämnar över uppdrag i sin del.</summary>
    public class ReplacementAccessRulesTests
    {
        private static string? R(bool leads = true, string role = "markor", bool admin = false, bool self = false, bool? inPool = null)
            => ReplacementAccessRules.Refusal(leads, role, admin, self, inPool);

        [Fact]
        public void Den_som_inte_leder_delen_far_inte()
            => R(leads: false).Should().Be(ReplacementAccessRules.NotLeader);

        [Fact]
        public void En_ledare_far_lamna_over_en_vanlig_roll_i_sin_del()
            => R().Should().BeNull();

        [Theory]
        [InlineData("skjutledare")]
        [InlineData("Stationschef")]
        public void Bara_skjutledare_och_stationschef_ar_ledarroller(string role)
            => ReplacementAccessRules.IsLeadRole(role).Should().BeTrue();

        [Theory]
        [InlineData("markor")]
        [InlineData("stationsmarkor")]
        [InlineData("tavlingsledning")]
        [InlineData("")]
        [InlineData(null)]
        public void Ovriga_roller_ger_ingen_ratt(string? role)
            => ReplacementAccessRules.IsLeadRole(role).Should().BeFalse();

        [Fact]
        public void Ett_uppdrag_med_behorighet_lamnas_inte_over_av_en_ledare()
            => R(admin: true).Should().Be(ReplacementAccessRules.NeedsConvener);

        [Fact]
        public void Tavlingsledningen_lamnas_inte_over_av_en_ledare_ens_utan_behorighetsflagga()
            => R(role: "Tavlingsledning").Should().Be(ReplacementAccessRules.NeedsConvener);

        [Fact]
        public void Ledaren_lamnar_inte_over_sitt_eget_uppdrag()
            => R(self: true).Should().Be(ReplacementAccessRules.Yourself);

        [Fact]
        public void Den_som_tar_over_ska_finnas_i_poolen()
        {
            R(inPool: false).Should().Be(ReplacementAccessRules.NotInPool);
            R(inPool: true).Should().BeNull();
            R(inPool: null).Should().BeNull();   // vid läsning av listan finns ingen vald person än
        }

        [Fact]
        public void Utan_ledarratt_sags_inget_om_vad_raden_ar()
            => R(leads: false, admin: true, self: true, inPool: false).Should().Be(ReplacementAccessRules.NotLeader);
    }
}
