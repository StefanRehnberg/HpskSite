using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.Services;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HpskSite.Tests
{
    /// <summary>
    /// Klasser utan sparad inställning fyller det sista skjutlaget som har plats — de fick
    /// tidigare ett eget skjutlag var, och tre små klasser blev tre ensamskyttar
    /// (Sune-genomgång 3, 2026-10-03).
    /// </summary>
    public class FinalsDefaultSkjutlagTests
    {
        private static PrecisionFinalsStartListBuilder Builder() =>
            new(NullLogger<PrecisionFinalsStartListBuilder>.Instance,
                new PrecisionFinalsQualificationService(NullLogger<PrecisionFinalsQualificationService>.Instance));

        private static ClassResultsSnapshot Cls(string name, int shooters) => new()
        {
            ChampionshipClass = name,
            QualifiedShooters = Enumerable.Range(1, shooters).Select(i => new QualifiedShooter
            {
                MemberId = i, Name = name + i, ShootingClass = name, QualificationScore = 400 - i
            }).ToList()
        };

        private static QualifyingResultsSnapshot Snap(params ClassResultsSnapshot[] classes) => new()
        {
            ClassSnapshots = classes.ToDictionary(c => c.ChampionshipClass, c => c)
        };

        [Fact]
        public void Sma_klasser_delar_skjutlag()
        {
            var d = Builder().DefaultSkjutlag(Snap(Cls("C", 7), Cls("C Vet Y", 1), Cls("C Vet Ä", 1), Cls("C Jun", 1)),
                new Dictionary<string, FinalsClassConfig>(), 20);
            Assert.All(d.Values, v => Assert.Equal(1, v));
        }

        [Fact]
        public void Tillagd_klass_hamnar_i_skjutlag_med_plats_bredvid_sparade()
        {
            var cfg = new Dictionary<string, FinalsClassConfig> { ["C"] = new() { SkjutlagNumber = 1 } };
            var d = Builder().DefaultSkjutlag(Snap(Cls("C", 7), Cls("C Jun", 1)), cfg, 20);
            Assert.Equal(1, d["C Jun"]);
            Assert.False(d.ContainsKey("C"));
        }

        [Fact]
        public void Fullt_skjutlag_ger_nytt()
        {
            var cfg = new Dictionary<string, FinalsClassConfig> { ["C"] = new() { SkjutlagNumber = 1, IncludeAllShooters = true } };
            var d = Builder().DefaultSkjutlag(Snap(Cls("C", 19), Cls("C Dam", 3)), cfg, 20);
            Assert.Equal(2, d["C Dam"]);
        }
    }
}
