using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Persistence;
using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Services;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.ScoreSources
{
    /// <summary>
    /// Score source for the precision family (Precision, Milsnabb, Duell, Nationell Helmatch,
    /// Magnum Precision). All of these store one row per shooter and series with a JSON array of
    /// shot values, so the competition total is the summed shot value and the tie-break is the
    /// X count.
    /// </summary>
    public class PrecisionFamilySeriesScoreSource : ISeriesScoreSource
    {
        private readonly IUmbracoDatabaseFactory _umbracoDatabaseFactory;
        private readonly IMemberService _memberService;
        private readonly ClubService _clubService;
        private readonly ILogger<PrecisionFamilySeriesScoreSource> _logger;

        private static readonly Dictionary<string, string> TableByType = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Precision"] = "PrecisionResultEntry",
            ["Milsnabb"] = "MilsnabbResultEntry",
            ["Duell"] = "DuellResultEntry",
            ["NationellHelmatch"] = "NationellHelmatchResultEntry",
            ["MagnumPrecision"] = "MagnumPrecisionResultEntry",
        };

        public PrecisionFamilySeriesScoreSource(
            IUmbracoDatabaseFactory umbracoDatabaseFactory,
            IMemberService memberService,
            ClubService clubService,
            ILogger<PrecisionFamilySeriesScoreSource> logger)
        {
            _umbracoDatabaseFactory = umbracoDatabaseFactory;
            _memberService = memberService;
            _clubService = clubService;
            _logger = logger;
        }

        /// <summary>
        /// An empty/unrecognised type falls through to Precision, which is what the series
        /// calculation did before score sources existed â€” legacy series nodes rely on it.
        /// </summary>
        public bool Supports(string competitionType)
            => string.IsNullOrWhiteSpace(competitionType) || TableByType.ContainsKey(competitionType);

        public async Task<SeriesScoreSet> FetchAsync(IReadOnlyList<int> competitionIds, string competitionType)
        {
            var set = new SeriesScoreSet { ScoreLabel = "Totalt", SecondaryLabel = "X" };
            if (competitionIds.Count == 0) return set;

            var tableName = TableByType.TryGetValue(competitionType ?? "", out var t) ? t : "PrecisionResultEntry";
            var rowsByCompetition = new Dictionary<int, List<PrecisionResultEntry>>();

            try
            {
                using var db = _umbracoDatabaseFactory.CreateDatabase();
                var paramNames = competitionIds.Select((id, i) => $"@{i}").ToArray();
                var sql = $"SELECT * FROM [{tableName}] WHERE CompetitionId IN ({string.Join(",", paramNames)}) " +
                          "ORDER BY CompetitionId, MemberId, SeriesNumber";

                var allResults = await db.FetchAsync<PrecisionResultEntry>(sql, competitionIds.Cast<object>().ToArray());
                foreach (var group in allResults.GroupBy(r => r.CompetitionId))
                    rowsByCompetition[group.Key] = group.ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching {Table} rows for competitions {CompetitionIds}",
                    tableName, string.Join(",", competitionIds));
                return set;
            }

            var shooterLookup = BuildShooterLookup(
                rowsByCompetition.Values.SelectMany(r => r).Select(r => r.MemberId).Distinct().ToList());

            foreach (var compId in competitionIds)
            {
                if (!rowsByCompetition.TryGetValue(compId, out var rows))
                {
                    set.ByCompetition[compId] = new List<ShooterCompetitionScore>();
                    continue;
                }

                set.ByCompetition[compId] = rows
                    .GroupBy(r => new { r.MemberId, r.ShootingClass })
                    .Select(g =>
                    {
                        var (name, club, clubId) = shooterLookup.TryGetValue(g.Key.MemberId, out var info)
                            ? info
                            : ("Okänd skytt", "Okänd klubb", 0);

                        return new ShooterCompetitionScore
                        {
                            MemberId = g.Key.MemberId,
                            Name = name,
                            Club = club,
                            ClubId = clubId,
                            ShootingClass = g.Key.ShootingClass,
                            TotalScore = g.Sum(r => CalculateTotalFromShots(r.Shots)),
                            XCount = g.Sum(r => CalculateXCountFromShots(r.Shots))
                        };
                    })
                    .ToList();
            }

            return set;
        }

        private Dictionary<int, (string Name, string Club, int ClubId)> BuildShooterLookup(List<int> memberIds)
        {
            var lookup = new Dictionary<int, (string Name, string Club, int ClubId)>();

            foreach (var memberId in memberIds)
            {
                try
                {
                    var member = _memberService.GetById(memberId);
                    if (member != null)
                    {
                        var name = member.Name ?? "Okänd";
                        var clubId = member.GetValue<int>("primaryClubId");
                        var clubName = clubId > 0
                            ? (_clubService.GetClubNameById(clubId) ?? "Okänd klubb")
                            : "Okänd klubb";
                        lookup[memberId] = (name, clubName, clubId);
                    }
                    else
                    {
                        lookup[memberId] = ("Okänd skytt", "Okänd klubb", 0);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to look up member {MemberId}", memberId);
                    lookup[memberId] = ("Okänd skytt", "Okänd klubb", 0);
                }
            }

            return lookup;
        }

        private static int CalculateTotalFromShots(string shotsJson)
        {
            try
            {
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? Array.Empty<string>();
                return shots.Sum(shot => shot.ToUpper() == "X" ? 10 : (int.TryParse(shot, out int value) ? value : 0));
            }
            catch
            {
                return 0;
            }
        }

        private static int CalculateXCountFromShots(string shotsJson)
        {
            try
            {
                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<string[]>(shotsJson) ?? Array.Empty<string>();
                return shots.Count(shot => shot.ToUpper() == "X");
            }
            catch
            {
                return 0;
            }
        }
    }
}
