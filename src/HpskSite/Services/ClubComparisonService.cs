using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Builds a single site-wide snapshot of per-club metrics, then exposes "comparable
    /// club" medians for any given clubId. Used by both the Club admin Statistik tab and
    /// the Regional admin Statistik tab to show "snitt jämförbar klubb: N" alongside a
    /// club's own number — keeps the comparison aggregation cost amortized across calls.
    ///
    /// Comparable clubs = clubs whose member count is within ±50% of the target club's
    /// member count (excludes the target club itself). Single member clubs get a wider
    /// fallback so they still see a comparison.
    /// </summary>
    public class ClubComparisonService
    {
        private const string CacheKey = "club_comparison_snapshot_v1";
        private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IMemberService _memberService;
        private readonly IMemoryCache _memoryCache;
        private readonly ILogger<ClubComparisonService> _logger;

        public ClubComparisonService(
            IUmbracoContextAccessor umbracoContextAccessor,
            IUmbracoDatabaseFactory databaseFactory,
            IMemberService memberService,
            IMemoryCache memoryCache,
            ILogger<ClubComparisonService> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _databaseFactory = databaseFactory;
            _memberService = memberService;
            _memoryCache = memoryCache;
            _logger = logger;
        }

        public async Task<ClubComparisonSnapshot> GetSnapshotAsync()
        {
            if (_memoryCache.TryGetValue(CacheKey, out ClubComparisonSnapshot? cached) && cached != null)
                return cached;

            var snapshot = await BuildSnapshotAsync();
            _memoryCache.Set(CacheKey, snapshot, CacheDuration);
            return snapshot;
        }

        public ClubComparisonValues GetForClub(int clubId, ClubComparisonSnapshot snapshot)
        {
            var clubSize = snapshot.MembersPerClub.GetValueOrDefault(clubId, 0);
            int lo, hi;

            if (clubSize <= 5)
            {
                // Very small clubs — compare against the smallest tier, otherwise the
                // band collapses and nothing is comparable.
                lo = 0; hi = 10;
            }
            else
            {
                lo = (int)Math.Floor(clubSize * 0.5);
                hi = (int)Math.Ceiling(clubSize * 1.5);
            }

            var comparable = snapshot.MembersPerClub
                .Where(kvp => kvp.Key != clubId && kvp.Value >= lo && kvp.Value <= hi)
                .Select(kvp => kvp.Key)
                .ToList();

            return new ClubComparisonValues
            {
                ComparableClubCount = comparable.Count,
                MembersMedian = Median(comparable.Select(id => snapshot.MembersPerClub.GetValueOrDefault(id, 0))),
                Active30dMedian = Median(comparable.Select(id => snapshot.Active30dPerClub.GetValueOrDefault(id, 0))),
                StepCompletions30dMedian = Median(comparable.Select(id => snapshot.StepCompletions30dPerClub.GetValueOrDefault(id, 0))),
                TrainingMatches30dMedian = Median(comparable.Select(id => snapshot.TrainingMatches30dPerClub.GetValueOrDefault(id, 0))),
                TrainingScores30dMedian = Median(comparable.Select(id => snapshot.TrainingScores30dPerClub.GetValueOrDefault(id, 0))),
                GrowthPct12mMedian = Median(comparable.Select(id => snapshot.GrowthPct12mPerClub.GetValueOrDefault(id, 0)))
            };
        }

        private async Task<ClubComparisonSnapshot> BuildSnapshotAsync()
        {
            var today = DateTime.Today;
            var thirtyDaysAgo = today.AddDays(-30);
            var twelveMonthsAgo = today.AddMonths(-12);

            var snapshot = new ClubComparisonSnapshot();

            try
            {
                var allMembers = _memberService.GetAll(0, int.MaxValue, out _).Where(m => m.IsApproved).ToList();
                var memberToClub = new Dictionary<int, int>(allMembers.Count);

                foreach (var m in allMembers)
                {
                    var clubIdStr = m.GetValue<string>("primaryClubId");
                    if (string.IsNullOrEmpty(clubIdStr) || !int.TryParse(clubIdStr, out int cid) || cid <= 0) continue;

                    memberToClub[m.Id] = cid;
                    snapshot.MembersPerClub[cid] = snapshot.MembersPerClub.GetValueOrDefault(cid, 0) + 1;

                    // Active 30d (web OR mobile)
                    var lastWeb = m.GetValue<DateTime?>("lastActiveDate");
                    var lastMob = m.GetValue<DateTime?>("lastMobileActiveDate");
                    var lastSeen = MaxNullable(lastWeb, lastMob);
                    if (lastSeen.HasValue && lastSeen.Value >= thirtyDaysAgo)
                    {
                        snapshot.Active30dPerClub[cid] = snapshot.Active30dPerClub.GetValueOrDefault(cid, 0) + 1;
                    }

                    // Step completions in last 30d (parsed from completedTrainingSteps JSON,
                    // each entry has a CompletedDate string)
                    var stepsJson = m.GetValue<string>("completedTrainingSteps");
                    if (!string.IsNullOrWhiteSpace(stepsJson))
                    {
                        var stepCount = CountRecentStepCompletions(stepsJson, thirtyDaysAgo);
                        if (stepCount > 0)
                            snapshot.StepCompletions30dPerClub[cid] = snapshot.StepCompletions30dPerClub.GetValueOrDefault(cid, 0) + stepCount;
                    }

                    // 12-month growth: members whose CreateDate is within last 12 months
                    if (m.CreateDate >= twelveMonthsAgo)
                    {
                        snapshot.NewMembers12mPerClub[cid] = snapshot.NewMembers12mPerClub.GetValueOrDefault(cid, 0) + 1;
                    }
                }

                // Compute growth % per club: new12m / (members - new12m).
                foreach (var kvp in snapshot.MembersPerClub)
                {
                    var total = kvp.Value;
                    var newCount = snapshot.NewMembers12mPerClub.GetValueOrDefault(kvp.Key, 0);
                    var prior = total - newCount;
                    snapshot.GrowthPct12mPerClub[kvp.Key] = prior <= 0 ? 100 : (int)Math.Round(newCount * 100.0 / prior);
                }

                // Training matches in last 30d, attributed via member's primary club
                using (var db = _databaseFactory.CreateDatabase())
                {
                    var matchRows = await db.FetchAsync<MatchParticipantRow>(
                        @"SELECT p.MemberId AS MemberId
                          FROM TrainingMatchParticipants p
                          INNER JOIN TrainingMatches m ON m.Id = p.TrainingMatchId
                          WHERE m.CreatedDate >= @0",
                        thirtyDaysAgo);

                    foreach (var r in matchRows)
                    {
                        if (memberToClub.TryGetValue(r.MemberId, out int cid))
                            snapshot.TrainingMatches30dPerClub[cid] = snapshot.TrainingMatches30dPerClub.GetValueOrDefault(cid, 0) + 1;
                    }

                    var scoreRows = await db.FetchAsync<TrainingScoreRow>(
                        @"SELECT MemberId FROM TrainingScores WHERE TrainingDate >= @0",
                        thirtyDaysAgo);

                    foreach (var r in scoreRows)
                    {
                        if (memberToClub.TryGetValue(r.MemberId, out int cid))
                            snapshot.TrainingScores30dPerClub[cid] = snapshot.TrainingScores30dPerClub.GetValueOrDefault(cid, 0) + 1;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to build club comparison snapshot — returning partial data");
            }

            return snapshot;
        }

        private static DateTime? MaxNullable(DateTime? a, DateTime? b)
        {
            if (!a.HasValue) return b;
            if (!b.HasValue) return a;
            return a > b ? a : b;
        }

        private static int CountRecentStepCompletions(string json, DateTime since)
        {
            // completedTrainingSteps is a JSON array of objects like
            //   { "Level": 1, "Step": 2, "CompletedDate": "2025-09-12T00:00:00", ... }
            // We don't need a full parse — just look for ISO date strings and count those
            // that are >= since.  Cheap and resilient to schema drift.
            int count = 0;
            int idx = 0;
            while (true)
            {
                idx = json.IndexOf("CompletedDate", idx, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) break;
                var quote = json.IndexOf('"', idx + "CompletedDate".Length + 1);
                if (quote < 0) break;
                var quoteEnd = json.IndexOf('"', quote + 1);
                if (quoteEnd < 0) break;
                var dateStr = json.Substring(quote + 1, quoteEnd - quote - 1);
                if (DateTime.TryParse(dateStr, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeLocal, out var dt) && dt >= since)
                {
                    count++;
                }
                idx = quoteEnd + 1;
            }
            return count;
        }

        private static int Median(IEnumerable<int> values)
        {
            var sorted = values.OrderBy(v => v).ToList();
            if (sorted.Count == 0) return 0;
            return sorted[sorted.Count / 2];
        }

        private class MatchParticipantRow { public int MemberId { get; set; } }
        private class TrainingScoreRow { public int MemberId { get; set; } }
    }

    public class ClubComparisonSnapshot
    {
        public Dictionary<int, int> MembersPerClub { get; } = new();
        public Dictionary<int, int> Active30dPerClub { get; } = new();
        public Dictionary<int, int> NewMembers12mPerClub { get; } = new();
        public Dictionary<int, int> GrowthPct12mPerClub { get; } = new();
        public Dictionary<int, int> StepCompletions30dPerClub { get; } = new();
        public Dictionary<int, int> TrainingMatches30dPerClub { get; } = new();
        public Dictionary<int, int> TrainingScores30dPerClub { get; } = new();
    }

    public class ClubComparisonValues
    {
        public int ComparableClubCount { get; set; }
        public int MembersMedian { get; set; }
        public int Active30dMedian { get; set; }
        public int StepCompletions30dMedian { get; set; }
        public int TrainingMatches30dMedian { get; set; }
        public int TrainingScores30dMedian { get; set; }
        public int GrowthPct12mMedian { get; set; }
    }
}
