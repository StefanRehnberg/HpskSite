using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// S8: en skytt som skjuter utanför mästerskapet får ingen placeringspoäng i en serie, tar ingen från de tävlande och
    /// räknas inte i ett klubblags poäng. Gästen har i alla fall högst poäng, så ett fel syns direkt.
    /// </summary>
    public class SeriesOutOfCompetitionTests
    {
        private const string Cls = "C Vet Y";

        private static ShooterCompetitionScore S(int member, string name, int club, int score, bool outside = false) => new()
        {
            MemberId = member, Name = name, Club = "Klubb " + club, ClubId = club, ShootingClass = Cls,
            TotalScore = score, XCount = 0, OutOfCompetition = outside
        };

        // Gästen (id 9, klubb 10) skjuter bäst. A (id 1, klubb 10) och C (id 3, klubb 20) tävlar.
        private static SeriesCalculationContext Ctx(Dictionary<string, object>? p = null) => new()
        {
            SeriesId = 1,
            Competitions = new() { new SeriesCompetitionInfo { CompetitionId = 100, Name = "Omgång 1", Date = new DateTime(2026, 5, 1) } },
            Parameters = p ?? new(),
            CompetitionResults = new()
            {
                [100] = new()
                {
                    S(9, "Gäst", 10, 110, outside: true),
                    S(1, "A", 10, 100),
                    S(3, "C", 20, 90)
                }
            }
        };

        private static Dictionary<int, SeriesStandingRow> Rows(SeriesResultData data, string section = "Individual") =>
            data.Sections.First(s => s.SectionType == section).ClassStandings.SelectMany(c => c.Rows)
                .ToDictionary(r => r.EntityId);

        private static int? PointsOf(SeriesStandingRow r) => r.CompetitionScores.Single().Points;

        [Fact]
        public void Dynamisk_placering_ger_gasten_noll_och_tavlande_poang_som_om_gasten_inte_fanns()
        {
            var rows = Rows(new IndividualDynamicPointsStrategy().Calculate(Ctx()));
            Assert.Equal(0, PointsOf(rows[9]));
            Assert.Equal(2, PointsOf(rows[1]));   // 1:a av två tävlande = 2 (med gästen hade det varit 2 av 3)
            Assert.Equal(1, PointsOf(rows[3]));
        }

        [Fact]
        public void Fast_poangtabell_flyttar_inte_de_tavlande_nedat_for_gasten()
        {
            var p = new Dictionary<string, object> { ["pointsTable"] = "25, 20, 16" };
            var rows = Rows(new IndividualFixedPointsStrategy().Calculate(Ctx(p)));
            Assert.Equal(0, PointsOf(rows[9]));
            Assert.Equal(25, PointsOf(rows[1]));
            Assert.Equal(20, PointsOf(rows[3]));
        }

        [Fact]
        public void Summa_med_placeringspoang_och_basta_x_foljer_samma_regel()
        {
            var p = new Dictionary<string, object> { ["placementPoints"] = "dynamic" };
            var sum = Rows(new IndividualSumAllStrategy().Calculate(Ctx(p)));
            Assert.Equal((0, 2, 1), (PointsOf(sum[9])!.Value, PointsOf(sum[1])!.Value, PointsOf(sum[3])!.Value));
            var best = Rows(new IndividualBestOfStrategy().Calculate(Ctx(p)));
            Assert.Equal(0, PointsOf(best[9]));
            Assert.Equal(2, PointsOf(best[1]));
        }

        [Fact]
        public void Rapoang_utan_placeringspoang_ar_oforandrad_gasten_behaller_sitt_resultat()
        {
            var rows = Rows(new IndividualSumAllStrategy().Calculate(Ctx()));
            Assert.Equal(110, PointsOf(rows[9]));
            Assert.Equal(100, PointsOf(rows[1]));
        }

        [Fact]
        public void Segrar_kan_inte_en_gast_vinna()
        {
            var rows = Rows(new IndividualWinsCountStrategy().Calculate(Ctx()));
            Assert.Equal(0, PointsOf(rows[9]));
            Assert.Equal(1, PointsOf(rows[1]));   // A vinner i stallet for gasten
        }

        [Fact]
        public void Klubblag_raknar_inte_gastens_poang()
        {
            var data = new ClubTeamBestOfStrategy().Calculate(Ctx());
            var club = Rows(data, "Club");
            Assert.Equal(100, PointsOf(club[10]));   // bara A; med gasten hade klubb 10 fatt 210
            Assert.Equal(90, PointsOf(club[20]));
        }
    }
}
