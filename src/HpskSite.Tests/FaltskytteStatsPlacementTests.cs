using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.CompetitionTypes.Faltskytte.Services;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>Min sida: plats och deltagarmått när en skytt står utanför mästerskapet.</summary>
    public class FaltskytteStatsPlacementTests
    {
        private static FaltskytteShooterResult S(int member, bool outside, int? placement) => new()
        {
            MemberId = member, ShootingClass = "C Vet Y", OutOfCompetition = outside, Placement = placement
        };

        // Bildens grupp: tre tävlande (plats 1, 2, 3) och en gäst utanför mellan 1 och 2 i listan.
        private static List<FaltskytteShooterResult> Group() => new()
        {
            S(1, false, 1), S(9, true, null), S(2, false, 2), S(3, false, 3)
        };

        [Fact]
        public void Utanfor_far_ingen_plats_och_deltagarna_raknar_bara_de_tavlande()
        {
            var r = FaltskytteStatsService.ResolvePlacement(Group(), 9, "C Vet Y", placement: 2, participants: 4, placementsAssigned: true);
            Assert.Equal((0, 3, true), r);
        }

        [Fact]
        public void Tavlande_far_bildens_plats_inte_listindex()
        {
            // Skytt 2 står på index 3 (plats 3 i listan) men är nummer 2 bland de tävlande.
            var r = FaltskytteStatsService.ResolvePlacement(Group(), 2, "C Vet Y", placement: 3, participants: 4, placementsAssigned: true);
            Assert.Equal((2, 3, false), r);
        }

        [Fact]
        public void Utan_tilldelade_platser_ar_allt_som_forut()
        {
            var r = FaltskytteStatsService.ResolvePlacement(Group(), 9, "C Vet Y", placement: 2, participants: 4, placementsAssigned: false);
            Assert.Equal((2, 4, false), r);
        }

        [Fact]
        public void Okand_skytt_lamnas_orord()
        {
            var r = FaltskytteStatsService.ResolvePlacement(Group(), 77, "C Vet Y", placement: 5, participants: 4, placementsAssigned: true);
            Assert.Equal((5, 4, false), r);
        }
    }
}
