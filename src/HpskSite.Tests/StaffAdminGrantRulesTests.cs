using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Steg 5, Expertgruppen steg 4–6 K2/K8: vem får dela ut "Ge behörighet".</summary>
    public class StaffAdminGrantRulesTests
    {
        private static string? R(bool hasPlanning = true, bool hasCompetition = false, bool carries = false, bool convener = false, bool ownLink = false, bool organiser = false)
            => StaffAdminGrantRules.Refusal(hasPlanning, hasCompetition, carries, convener, ownLink, organiser);

        [Fact]
        public void En_tavlings_egen_bemanning_utan_planering_ar_som_forut()
            => R(hasPlanning: false, hasCompetition: true).Should().BeNull();

        [Fact]
        public void Det_gemensamma_ges_bara_av_huvudplaneringens_sammankallande()
        {
            R(carries: true, convener: false).Should().Be(StaffAdminGrantRules.NeedsConvener);
            R(carries: true, convener: true).Should().BeNull();
        }

        [Fact]
        public void En_planering_utan_tavling_som_inte_bar_familjebehorighet_vagras_inte_har()
            => R(carries: false).Should().BeNull();   // själva bockningen spärras av tjänsten (ingen tävling att hantera)

        [Fact]
        public void En_planering_med_egen_koppling_ar_som_forut_aven_utan_organisatorsratt()
            => R(hasCompetition: true, ownLink: true, organiser: false).Should().BeNull();

        [Fact]
        public void En_tavling_som_kommer_fran_Bemannar_for_eller_arv_kraver_organisatorsratt()
        {
            R(hasCompetition: true, ownLink: false, organiser: false).Should().Be(StaffAdminGrantRules.NeedsOrganiser);
            R(hasCompetition: true, ownLink: false, organiser: true).Should().BeNull();
        }

        [Fact]
        public void En_sammankallande_i_roten_ger_inte_behorighet_i_en_tavling_som_kommer_via_arv()
            => R(hasCompetition: true, ownLink: false, convener: true, organiser: false).Should().Be(StaffAdminGrantRules.NeedsOrganiser);
    }
}
