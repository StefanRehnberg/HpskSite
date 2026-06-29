using HpskSite.Services.Ranking;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>Small, cheap, login-only summary for the logged-in home "hub". A teaser/doorway into
    /// Min Sida — never a replacement, so it only reads inexpensive data and degrades to nulls/zeros.</summary>
    public class HomeHubSummary
    {
        public int SessionsThisMonth { get; set; }
        public DateTime? LastActivity { get; set; }

        // "Din form" — mirrors Min sida's "Aktuell Form": the weapon class with the highest average
        // over the last 30 days, computed from the SAME UnifiedResultsService source so the home card
        // never disagrees with the dashboard it links to. (Was previously the most-PLAYED class, which
        // showed e.g. A while the dashboard headlined C because C had the higher average.)
        public string? TopWeaponClass { get; set; }
        public decimal TopAveragePerSeries { get; set; }

        // "Topplistan" — the member's best Träningsmatch standing, or null if they have none / too small a field.
        public HubRanking? Ranking { get; set; }

        public bool HasForm => !string.IsNullOrEmpty(TopWeaponClass);
        public bool HasActivity => SessionsThisMonth > 0 || LastActivity.HasValue;
        public bool HasRanking => Ranking != null;
    }

    /// <summary>The member's best Träningsmatch-topplista standing, read from the precomputed snapshot.</summary>
    public class HubRanking
    {
        public string Discipline { get; set; } = "Precision";
        public string WeaponGroup { get; set; } = "";
        public int? ClubRank { get; set; }
        public int? ClubTotal { get; set; }
        public string? ClubName { get; set; }
        public int? ClubMovement { get; set; }   // prior rank - current rank (positive = climbed)
        public int? NationalRank { get; set; }
        public int? NationalTotal { get; set; }
        public bool IsProvisional { get; set; }

        // Headline the club standing when it's a real field (≥2 shooters); otherwise fall back to national.
        public bool ShowClub => ClubRank != null && (ClubTotal ?? 0) >= 2;
        public int PrimaryRank => ShowClub ? ClubRank!.Value : (NationalRank ?? 0);
        public int PrimaryTotal => ShowClub ? (ClubTotal ?? 0) : (NationalTotal ?? 0);
    }

    public class HomeHubService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly UnifiedResultsService _unified;
        private readonly RankingService _ranking;

        public HomeHubService(IUmbracoDatabaseFactory databaseFactory, UnifiedResultsService unified, RankingService ranking)
        {
            _databaseFactory = databaseFactory;
            _unified = unified;
            _ranking = ranking;
        }

        public async Task<HomeHubSummary> GetSummaryAsync(int memberId)
        {
            var s = new HomeHubSummary();
            if (memberId <= 0) return s;

            // activity (cheap, indexed)
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var monthStart = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
                s.SessionsThisMonth = db.ExecuteScalar<int>(
                    "SELECT COUNT(DISTINCT CAST(TrainingDate AS DATE)) FROM TrainingScores WHERE MemberId = @0 AND TrainingDate >= @1",
                    memberId, monthStart);
                s.LastActivity = db.ExecuteScalar<DateTime?>(
                    "SELECT MAX(TrainingDate) FROM TrainingScores WHERE MemberId = @0", memberId);
            }
            catch { /* table missing / transient → no activity shown */ }

            // form: highest-average weapon class over the last 30 days — IDENTICAL computation to the
            // dashboard's recentAverageByClass (same UnifiedResultsService source, same 30-day window,
            // same 1-decimal rounding), so "Din form · X" always agrees with "Aktuell Form" on Min sida.
            try
            {
                var results = _unified.GetMemberResults(memberId);
                if (results != null && results.Count > 0)
                {
                    var recentDate = DateTime.Now.AddDays(-30);
                    var top = results
                        .Where(r => r.Date >= recentDate && !string.IsNullOrEmpty(r.WeaponClass))
                        .GroupBy(r => r.WeaponClass)
                        .Select(g => new { wc = g.Key, avg = Math.Round(g.Average(r => r.AverageScore), 1) })
                        .OrderByDescending(x => x.avg)
                        .ThenBy(x => x.wc)
                        .FirstOrDefault();
                    if (top != null)
                    {
                        s.TopWeaponClass = top.wc;
                        s.TopAveragePerSeries = (decimal)top.avg;
                    }
                }
            }
            catch { /* no results → form card shows the "kom igång" fallback */ }

            // topplista placement: the member's best standing across their classes, from the precomputed
            // ranking snapshot (cheap indexed reads — same source as the Min sida private teaser).
            try
            {
                var lines = _ranking.GetMyRankingContext(memberId);
                var best = lines?
                    .Where(l => (l.ClubRank != null && (l.ClubTotal ?? 0) >= 2)
                             || (l.NationalRank != null && (l.NationalTotal ?? 0) >= 2))
                    .OrderBy(l => l.ClubRank ?? int.MaxValue)
                    .ThenBy(l => l.NationalRank ?? int.MaxValue)
                    .FirstOrDefault();
                if (best != null)
                {
                    s.Ranking = new HubRanking
                    {
                        Discipline = best.Discipline,
                        WeaponGroup = best.WeaponGroup,
                        ClubRank = best.ClubRank,
                        ClubTotal = best.ClubTotal,
                        ClubName = best.ClubName,
                        ClubMovement = best.ClubMovement,
                        NationalRank = best.NationalRank,
                        NationalTotal = best.NationalTotal,
                        IsProvisional = best.IsProvisional
                    };
                }
            }
            catch { /* no snapshot yet → topplista card shows the teaser fallback */ }

            return s;
        }
    }
}
