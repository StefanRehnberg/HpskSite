using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using HpskSite.Models;
using HpskSite.Services;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Per-championship-class snapshot of qualifying results. Each class is frozen
    /// independently — admin can freeze C as soon as C qualifying is done, even if A
    /// still has pending shooters. The full snapshot blob (dict keyed by class name)
    /// lives on the qualifying precisionStartList node.
    ///
    /// Staleness is per-class: each frozen class stores a SHA of the PrecisionResultEntry
    /// rows for shooters in that class. Re-computing and comparing detects post-freeze
    /// edits in that specific class.
    /// </summary>
    public class PrecisionQualifyingResultsService
    {
        private readonly IContentService _contentService;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly PrecisionFinalsQualificationService _qualificationService;
        private readonly ILogger<PrecisionQualifyingResultsService> _logger;

        public PrecisionQualifyingResultsService(
            IContentService contentService,
            IUmbracoDatabaseFactory databaseFactory,
            PrecisionFinalsQualificationService qualificationService,
            ILogger<PrecisionQualifyingResultsService> logger)
        {
            _contentService = contentService;
            _databaseFactory = databaseFactory;
            _qualificationService = qualificationService;
            _logger = logger;
        }

        public QualifyingResultsSnapshot GetSnapshot(int competitionId)
        {
            var qualifyingNode = FindQualifyingStartListNode(competitionId);
            if (qualifyingNode == null) return new QualifyingResultsSnapshot { CompetitionId = competitionId };

            var json = qualifyingNode.GetValue<string>("qualifyingResultsSnapshot");
            if (string.IsNullOrWhiteSpace(json)) return new QualifyingResultsSnapshot { CompetitionId = competitionId };

            try
            {
                var snap = JsonConvert.DeserializeObject<QualifyingResultsSnapshot>(json);
                return snap ?? new QualifyingResultsSnapshot { CompetitionId = competitionId };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not deserialize qualifying snapshot for competition {CompetitionId}", competitionId);
                return new QualifyingResultsSnapshot { CompetitionId = competitionId };
            }
        }

        public async Task<(bool ok, string message, ClassResultsSnapshot? classSnapshot)> FreezeClassResultsAsync(int competitionId, string groupName, string frozenBy)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null)
                return (false, "Tävlingen hittades inte.", null);

            var numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries");
            if (numberOfFinalSeries <= 0)
                return (false, "Denna tävling har inga finalserier konfigurerade.", null);

            var qualifyingNode = FindQualifyingStartListNode(competitionId);
            if (qualifyingNode == null)
                return (false, "Ingen kvalstartlista hittades. Generera startlistan först.", null);

            // Build full rankings using the result list's merge config.
            var rankings = await BuildFullRankingsAsync(competitionId, competition);
            var groupRanking = rankings.FirstOrDefault(r => string.Equals(r.ChampionshipClass, groupName, StringComparison.Ordinal));
            if (groupRanking == null || groupRanking.QualifiedShooters.Count == 0)
                return (false, $"Inga kvalresultat hittades för gruppen {groupName}.", null);

            var checksum = await ComputeGroupChecksumAsync(competitionId, groupRanking);

            var classSnap = new ClassResultsSnapshot
            {
                ChampionshipClass = groupName,
                FrozenAt = DateTime.Now,
                FrozenBy = frozenBy,
                ChecksumAtFreeze = checksum,
                QualifiedShooters = groupRanking.QualifiedShooters
            };

            var snapshot = GetSnapshot(competitionId);
            snapshot.CompetitionId = competitionId;
            snapshot.ClassSnapshots[groupName] = classSnap;

            qualifyingNode.SetValue("qualifyingResultsSnapshot", JsonConvert.SerializeObject(snapshot));

            // Save only — qualifyingResultsSnapshot is admin-only and read via the draft
            // (IContent.GetValue). Publishing is expensive (NuCache rebuild + content events,
            // ~30s under load) and isn't needed here since the snapshot isn't displayed to
            // public visitors. The next public update of the start list is the admin's
            // explicit re-publish from the regular Publicera button.
            var saveResult = _contentService.Save(qualifyingNode);
            if (!saveResult.Success)
                return (false, "Kunde inte spara låst grupp.", null);

            _logger.LogInformation("Froze group {Group} for competition {CompetitionId} by {FrozenBy} ({ShooterCount} shooters)",
                groupName, competitionId, frozenBy, classSnap.QualifiedShooters.Count);

            return (true, $"Gruppen {groupName} låst ({classSnap.QualifiedShooters.Count} skyttar).", classSnap);
        }

        public async Task<(bool ok, string message)> UnfreezeClassAsync(int competitionId, string groupName)
        {
            var qualifyingNode = FindQualifyingStartListNode(competitionId);
            if (qualifyingNode == null)
                return (false, "Ingen kvalstartlista hittades.");

            var snapshot = GetSnapshot(competitionId);
            if (!snapshot.ClassSnapshots.Remove(groupName))
                return (true, "Gruppen var inte låst.");

            qualifyingNode.SetValue("qualifyingResultsSnapshot", JsonConvert.SerializeObject(snapshot));
            // Save only (see SaveFinalsConfig comment for rationale).
            var saveResult = _contentService.Save(qualifyingNode);
            if (!saveResult.Success)
                return (false, "Kunde inte spara.");

            await Task.CompletedTask;
            return (true, $"Gruppen {groupName} upplåst.");
        }

        public async Task<Dictionary<string, bool>> ComputeStalenessAsync(int competitionId, QualifyingResultsSnapshot snapshot)
        {
            var result = new Dictionary<string, bool>();
            if (snapshot.ClassSnapshots.Count == 0) return result;

            var rankingsByGroup = (await BuildFullRankingsAsync(competitionId, _contentService.GetById(competitionId)!))
                .ToDictionary(r => r.ChampionshipClass, r => r);

            foreach (var (group, classSnap) in snapshot.ClassSnapshots)
            {
                if (!rankingsByGroup.TryGetValue(group, out var ranking))
                {
                    result[group] = true;
                    continue;
                }
                var currentChecksum = await ComputeGroupChecksumAsync(competitionId, ranking);
                result[group] = !string.Equals(currentChecksum, classSnap.ChecksumAtFreeze, StringComparison.Ordinal);
            }
            return result;
        }

        public async Task<List<ChampionshipClassQualification>> GetAvailableClassRankingsAsync(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new();
            return await BuildFullRankingsAsync(competitionId, competition);
        }

        /// <summary>
        /// Pulls the result list's merge config from competitionResult.mergeConfig (if present)
        /// and returns the source→combined-name lookup. Empty dict if no result list or no
        /// merges — caller treats each sub-class as its own group.
        /// </summary>
        public Dictionary<string, string> GetMergeLookup(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return new();

            var resultPage = _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionResult" && c.Name == "Resultat");
            if (resultPage == null) return new();

            var mergeJson = resultPage.GetValue<string>("mergeConfig");
            if (string.IsNullOrWhiteSpace(mergeJson)) return new();

            try
            {
                var merges = JsonConvert.DeserializeObject<List<ClassMergeAction>>(mergeJson);
                return ClassMergingService.BuildMergeGroupLookup(merges);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not parse mergeConfig for competition {CompetitionId}", competitionId);
                return new();
            }
        }

        private async Task<List<ChampionshipClassQualification>> BuildFullRankingsAsync(int competitionId, IContent competition)
        {
            var numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries");
            var numberOfSeries = competition.GetValue<int>("numberOfSeriesOrStations");
            var qualSeriesCount = numberOfFinalSeries > 0 ? (numberOfSeries - numberOfFinalSeries) : numberOfSeries;

            var results = await GetQualifyingResultsAsync(competitionId, qualSeriesCount);
            var shooterInfo = GetShooterInfoFromStartList(competitionId);
            var mergeLookup = GetMergeLookup(competitionId);
            return _qualificationService.BuildFullClassRankings(results, shooterInfo, mergeLookup);
        }

        private async Task<List<PrecisionResultEntry>> GetQualifyingResultsAsync(int competitionId, int qualSeriesCount)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<PrecisionResultEntry>(
                @"SELECT * FROM PrecisionResultEntry
                  WHERE CompetitionId = @0 AND SeriesNumber <= @1
                  ORDER BY MemberId, SeriesNumber",
                competitionId, qualSeriesCount);
        }

        private async Task<string> ComputeGroupChecksumAsync(int competitionId, ChampionshipClassQualification ranking)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return "";

            var numberOfFinalSeries = competition.GetValue<int>("numberOfFinalSeries");
            var numberOfSeries = competition.GetValue<int>("numberOfSeriesOrStations");
            var qualSeriesCount = numberOfFinalSeries > 0 ? (numberOfSeries - numberOfFinalSeries) : numberOfSeries;

            using var db = _databaseFactory.CreateDatabase();
            var results = await db.FetchAsync<PrecisionResultEntry>(
                @"SELECT * FROM PrecisionResultEntry
                  WHERE CompetitionId = @0 AND SeriesNumber <= @1
                  ORDER BY MemberId, SeriesNumber",
                competitionId, qualSeriesCount);

            // Scope checksum to the shooters actually in this result-list group.
            var memberIds = ranking.QualifiedShooters.Select(s => s.MemberId).ToHashSet();
            var scoped = results.Where(r => memberIds.Contains(r.MemberId)).ToList();

            var sb = new StringBuilder();
            foreach (var r in scoped.OrderBy(r => r.MemberId).ThenBy(r => r.SeriesNumber))
            {
                sb.Append(r.MemberId).Append('|')
                  .Append(r.SeriesNumber).Append('|')
                  .Append(r.Shots ?? "").Append('\n');
            }
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash);
        }

        private IContent? FindQualifyingStartListNode(int competitionId)
        {
            var competition = _contentService.GetById(competitionId);
            if (competition == null) return null;

            return _contentService.GetPagedChildren(competition.Id, 0, 50, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");
        }

        private Dictionary<int, (string Name, string Club)> GetShooterInfoFromStartList(int competitionId)
        {
            var dict = new Dictionary<int, (string, string)>();
            var qualifyingNode = FindQualifyingStartListNode(competitionId);
            if (qualifyingNode == null) return dict;

            var configData = qualifyingNode.GetValue<string>("configurationData");
            if (string.IsNullOrEmpty(configData)) return dict;

            try
            {
                var startListData = JsonConvert.DeserializeObject<StartListConfiguration>(configData);
                if (startListData?.Teams == null) return dict;

                foreach (var team in startListData.Teams)
                {
                    if (team.Shooters == null) continue;
                    foreach (var shooter in team.Shooters)
                    {
                        if (!dict.ContainsKey(shooter.MemberId))
                            dict[shooter.MemberId] = (shooter.Name ?? "Unknown", shooter.Club ?? "Unknown");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse qualifying start list configurationData for shooter info, competition {CompetitionId}", competitionId);
            }

            return dict;
        }
    }
}
