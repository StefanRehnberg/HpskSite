using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.ScoreSources;
using HpskSite.CompetitionTypes.Faltskytte.Models;
using HpskSite.Models;
using HpskSite.Services;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Score source for Fältskytte and Magnumfält series.
    ///
    /// Fältskytte stores one row per shooter and station, so a competition total is the summed
    /// station rows. Which number counts depends on the scoring mode of that round, resolved the
    /// same way the result list resolves it (<see cref="FaltskytteScoringMode"/>):
    ///   Normalfält: total = träff, tie-break = figurer (the poängmål total is the next tie-break
    ///               inside a single competition, but a series only carries one secondary number)
    ///   Poängfält:  total = poäng (träff + figurer), tie-break = poängmål-summan
    ///
    /// Shoot-off stations are excluded, as they are in the per-competition result list: a
    /// särskjutning decides placement within one round and must not inflate the series total.
    /// Names and clubs come from the patrol snapshot, so a shooter who changed club mid-series is
    /// credited to the club they actually shot for in each round.
    /// </summary>
    public class FaltskytteSeriesScoreSource : ISeriesScoreSource
    {
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly IContentService _contentService;
        private readonly ClubService _clubService;
        private readonly ILogger<FaltskytteSeriesScoreSource> _logger;

        public FaltskytteSeriesScoreSource(
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            IContentService contentService,
            ClubService clubService,
            ILogger<FaltskytteSeriesScoreSource> logger)
        {
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _contentService = contentService;
            _clubService = clubService;
            _logger = logger;
        }

        public bool Supports(string competitionType)
            => string.Equals(competitionType, "Faltskytte", StringComparison.OrdinalIgnoreCase)
            || string.Equals(competitionType, "MagnumFalt", StringComparison.OrdinalIgnoreCase);

        public async Task<SeriesScoreSet> FetchAsync(IReadOnlyList<int> competitionIds, string competitionType)
        {
            var set = new SeriesScoreSet { ScoreLabel = "Träff", SecondaryLabel = "Fig." };
            if (competitionIds.Count == 0) return set;

            // Per-round scoring mode and shoot-off-only stations, read from each round's own config.
            var modeByCompetition = new Dictionary<int, bool>();          // competitionId -> isPoang
            var shootOffStations = new Dictionary<int, HashSet<int>>();   // competitionId -> station numbers to skip

            foreach (var compId in competitionIds)
            {
                var competition = _contentService.GetById(compId);
                var config = FaltskytteConfigParser.Parse(competition?.GetValue<string>("stationConfig"));
                modeByCompetition[compId] = FaltskytteScoringMode.IsPoang(config, competition?.GetValue<string>("scoringMode"));

                var firstWcConfig = config.WeaponConfigs.Values.FirstOrDefault();
                shootOffStations[compId] = firstWcConfig?.Stations
                    .Where(s => s.IsShootOffOnly)
                    .Select(s => s.Station)
                    .ToHashSet() ?? new HashSet<int>();
            }

            // A series mixing normalfält and poängfält rounds cannot have one honest heading;
            // the first round's mode names the columns, each round still scores by its own mode.
            if (modeByCompetition.TryGetValue(competitionIds[0], out var firstIsPoang) && firstIsPoang)
            {
                set.ScoreLabel = "Poäng";
                set.SecondaryLabel = "Poängmål";
            }

            List<FaltskytteResultEntry> allResults;
            Dictionary<(int, int), (string Name, string Club)> memberInfo;

            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();

                var paramNames = competitionIds.Select((id, i) => $"@{i}").ToArray();
                allResults = await db.FetchAsync<FaltskytteResultEntry>(
                    $"SELECT * FROM [FaltskytteResultEntry] WHERE CompetitionId IN ({string.Join(",", paramNames)}) " +
                    "ORDER BY CompetitionId, MemberId, StationNumber",
                    competitionIds.Cast<object>().ToArray());

                memberInfo = await BuildPatrolSnapshotLookup(db, competitionIds);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Fältskytte results for competitions {CompetitionIds}",
                    string.Join(",", competitionIds));
                return set;
            }

            var clubIdByName = BuildClubIdLookup();
            var syntheticClubIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var resultsByCompetition = allResults.GroupBy(r => r.CompetitionId)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var compId in competitionIds)
            {
                if (!resultsByCompetition.TryGetValue(compId, out var rows))
                {
                    set.ByCompetition[compId] = new List<ShooterCompetitionScore>();
                    continue;
                }

                var skip = shootOffStations[compId];
                if (skip.Count > 0)
                    rows = rows.Where(r => !skip.Contains(r.StationNumber)).ToList();

                var isPoang = modeByCompetition[compId];

                set.ByCompetition[compId] = rows
                    .GroupBy(r => new { r.MemberId, r.ShootingClass })
                    .Select(g =>
                    {
                        var hits = g.Sum(r => r.Hits);
                        var figures = g.Sum(r => r.Figures);
                        var poangmal = g.Sum(r => r.TiebreakerScore ?? 0);

                        var (name, club) = memberInfo.TryGetValue((compId, g.Key.MemberId), out var info)
                            ? info
                            : ("Okänd skytt", "Okänd klubb");

                        return new ShooterCompetitionScore
                        {
                            MemberId = g.Key.MemberId,
                            Name = name,
                            Club = club,
                            ClubId = ResolveClubId(club, clubIdByName, syntheticClubIds),
                            ShootingClass = ShootingClasses.GetById(g.Key.ShootingClass)?.Name ?? g.Key.ShootingClass,
                            TotalScore = isPoang ? hits + figures : hits,
                            XCount = isPoang ? poangmal : figures
                        };
                    })
                    .ToList();
            }

            return set;
        }

        /// <summary>
        /// (CompetitionId, MemberId) -> the name and club the shooter was entered under in that round.
        /// </summary>
        private static async Task<Dictionary<(int, int), (string Name, string Club)>> BuildPatrolSnapshotLookup(
            IUmbracoDatabase db, IReadOnlyList<int> competitionIds)
        {
            var lookup = new Dictionary<(int, int), (string Name, string Club)>();

            var compParams = competitionIds.Select((id, i) => $"@{i}").ToArray();
            var patrols = await db.FetchAsync<FaltskyttePatrol>(
                $"SELECT * FROM [FaltskyttePatrol] WHERE CompetitionId IN ({string.Join(",", compParams)})",
                competitionIds.Cast<object>().ToArray());

            var competitionByPatrolId = patrols.ToDictionary(p => p.Id, p => p.CompetitionId);
            if (competitionByPatrolId.Count == 0) return lookup;

            // Chunked: a long series can hold more patrols than a single IN list may carry.
            foreach (var chunk in competitionByPatrolId.Keys.Chunk(1000))
            {
                var patrolParams = chunk.Select((id, i) => $"@{i}").ToArray();
                var members = await db.FetchAsync<FaltskyttePatrolMember>(
                    $"SELECT * FROM [FaltskyttePatrolMember] WHERE PatrolId IN ({string.Join(",", patrolParams)})",
                    chunk.Cast<object>().ToArray());

                foreach (var m in members)
                {
                    if (!competitionByPatrolId.TryGetValue(m.PatrolId, out var compId)) continue;
                    lookup[(compId, m.MemberId)] = (m.MemberName, m.ClubName);
                }
            }

            return lookup;
        }

        private Dictionary<string, int> BuildClubIdLookup()
        {
            var lookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var club in _clubService.GetAllClubs())
                {
                    if (!string.IsNullOrWhiteSpace(club.Name))
                        lookup.TryAdd(club.Name.Trim(), club.Id);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build club id lookup for Fältskytte series scores");
            }
            return lookup;
        }

        /// <summary>
        /// The patrol snapshot carries a club NAME, but club standings group on id. Unknown names
        /// (a guest club with no page) get a stable negative id so they still group as one club
        /// instead of collapsing into a single "0" bucket with every other unknown.
        /// </summary>
        private static int ResolveClubId(string clubName, Dictionary<string, int> clubIdByName,
                                         Dictionary<string, int> synthetic)
        {
            if (string.IsNullOrWhiteSpace(clubName)) return 0;
            var key = clubName.Trim();
            if (clubIdByName.TryGetValue(key, out var id)) return id;
            if (synthetic.TryGetValue(key, out var existing)) return existing;

            var newId = -(synthetic.Count + 1);
            synthetic[key] = newId;
            return newId;
        }
    }
}
