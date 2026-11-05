using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Fältets standardmedaljer i registret (<c>StandardMedalAward</c>, Source = OnSite).
    ///
    /// F0c: registret skrevs bara vid Publicera. Uppdatera, särskjutning, sammanslagning och stationskonfiguration
    /// skriver om listans frusna bild men lämnade registret efter — så Min sida, klubbsekreterarens vy och SPSF-rapporten
    /// kunde visa en annan medalj än listan. Nu anropar <see cref="FaltskytteResultArtifactService.RefreshAsync"/> hit
    /// vid varje bildskrivning för en officiell lista; en avpublicering tar bort platsmedaljerna.
    /// (Flyttad ur FaltskytteController utan ändrad beräkning — samma rader, samma nyckel, samma upsert.)
    /// </summary>
    public class FaltskytteMedalLedger
    {
        private readonly StandardMedalMaterializationService _materialization;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<FaltskytteMedalLedger> _logger;

        public FaltskytteMedalLedger(
            StandardMedalMaterializationService materialization,
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<FaltskytteMedalLedger> logger)
        {
            _materialization = materialization;
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        /// <summary>Official = upsert vunna medaljer; annars bort. Blockerar aldrig publiceringen.</summary>
        public async Task MaterializeAsync(HpskSite.Models.Competitions.CompetitionInfo competition, bool isOfficial)
        {
            try
            {
                if (!isOfficial)
                {
                    await _materialization.RemoveOnSiteForCompetitionAsync(competition.Id);
                    return;
                }

                var discipline = competition.CompetitionType ?? StandardMedals.Faltskytte;
                var competitionDate = competition.CompetitionDate;
                var year = competitionDate?.Year ?? DateTime.Now.Year;
                var competitionName = competition.CompetitionName;
                if (string.IsNullOrWhiteSpace(competitionName)) competitionName = competition.Name;

                var medals = await ComputeAsync(competition);
                await _materialization.UpsertOnSiteMedalsAsync(
                    competition.Id, discipline, year, competitionName, competitionDate, medals);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to materialize Fältskytte standard medals for competition {CompetitionId}", competition.Id);
            }
        }

        /// <summary>
        /// De vunna standardmedaljerna för en fält-/magnumfälttävling. Medaljer delas på den platta (skytt, klass)-listan —
        /// klassammanslagning är bara visning. Tom när tävlingen inte delar ut medaljer.
        /// </summary>
        private async Task<List<OnSiteMedal>> ComputeAsync(HpskSite.Models.Competitions.CompetitionInfo competition)
        {
            var result = new List<OnSiteMedal>();

            // BR-PS.1.3: club competitions never award standard medals.
            if (!competition.IsAwardingStandardMedals || competition.IsClubOnly)
                return result;

            var competitionConfig = FaltskytteResultsBuilder.ParseCompetitionConfig(competition);
            var scoringMode = FaltskytteScoringMode.Resolve(competitionConfig, competition.ScoringMode);
            var firstWcConfig = competitionConfig.WeaponConfigs.Values.FirstOrDefault();
            var stationCount = firstWcConfig?.Stations.Count(s => !s.IsShootOffOnly) ?? 0;
            var shootOffOnlyStationNumbers = (firstWcConfig?.Stations
                .Where(s => s.IsShootOffOnly).Select(s => s.Station).ToHashSet()) ?? new HashSet<int>();

            using var db = _databaseFactory.CreateDatabase();
            var allResults = await db.FetchAsync<Models.FaltskytteResultEntry>(
                "WHERE CompetitionId = @0 ORDER BY MemberId, StationNumber", competition.Id);
            if (shootOffOnlyStationNumbers.Count > 0)
                allResults = allResults.Where(r => !shootOffOnlyStationNumbers.Contains(r.StationNumber)).ToList();
            if (!allResults.Any()) return result;

            var shooterResults = allResults
                .GroupBy(r => new { r.MemberId, r.ShootingClass })
                .Select(g =>
                {
                    var stationResults = g.OrderBy(r => r.StationNumber)
                        .Select(r => new Models.FaltskytteStationResult
                        {
                            StationNumber = r.StationNumber,
                            Hits = r.Hits,
                            Figures = r.Figures,
                            TiebreakerScore = r.TiebreakerScore
                        }).ToList();
                    return new Models.FaltskytteShooterResult
                    {
                        MemberId = g.Key.MemberId,
                        ShootingClass = g.Key.ShootingClass,
                        Stations = stationResults,
                        TotalHits = stationResults.Sum(s => s.Hits),
                        TotalFigures = stationResults.Sum(s => s.Figures),
                        TotalPoints = stationResults.Sum(s => s.Points),
                        TotalTiebreakerScore = stationResults.Where(s => s.TiebreakerScore.HasValue).Sum(s => s.TiebreakerScore!.Value)
                    };
                }).ToList();

            // Samma definition som byggaren (FaltskytteResultsBuilder): registret får aldrig dela C annorlunda än listan.
            var isSmOrLdm = HpskSite.CompetitionTypes.Common.ChampionshipRule.For(competition).SplitsStandardMedalGroupC;
            new FaltskytteStandardMedalService().CalculateStandardMedals(shooterResults, scoringMode, stationCount, isSmOrLdm);

            foreach (var s in shooterResults)
            {
                if (StandardMedals.IsMedal(s.StandardMedal))
                    result.Add(new OnSiteMedal(s.MemberId, s.ShootingClass, s.StandardMedal!));
            }
            return result;
        }
    }
}
