using FluentAssertions;
using HpskSite.Services.Staffing;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// "Fördela dem åt mig" väljer blocklängd åt användaren (persontest 6b: 3 av 12 fick ingen plats med 3 timmar och förslaget sa inte att kortare block hjälper).
    /// Regeln: flest placerade personer först, sedan flest fyllda tider; lika betyder att det längre blocket (provat först) behålls.
    /// </summary>
    public class DistributionChoiceTests
    {
        [Fact]
        public void Fler_personer_placerade_vinner_aven_om_farre_tider_fylls()
            => StaffProposalService.IsBetterDistribution(12, 17, 9, 24).Should().BeTrue();

        [Fact]
        public void Lika_manga_personer_ger_fler_fyllda_tider_vinst()
            => StaffProposalService.IsBetterDistribution(12, 18, 12, 17).Should().BeTrue();

        [Fact]
        public void Lika_i_allt_behaller_det_forsta_det_langre_blocket()
            => StaffProposalService.IsBetterDistribution(12, 17, 12, 17).Should().BeFalse();

        [Fact]
        public void Farre_personer_ar_aldrig_battre()
            => StaffProposalService.IsBetterDistribution(9, 30, 12, 2).Should().BeFalse();

        [Fact]
        public void Blocklangderna_som_prövas_går_från_längst_till_kortast_och_ingen_är_under_minimum()
        {
            StaffProposalService.AutoBlockMinutes.Should().BeInDescendingOrder();
            StaffProposalService.AutoBlockMinutes[0].Should().Be(HpskSite.Models.Staffing.SlotSlicer.DefaultBlockMinutes);
            StaffProposalService.AutoBlockMinutes.Should().OnlyContain(m => m >= HpskSite.Models.Staffing.SlotSlicer.MinBlockMinutes);
        }
    }
}
