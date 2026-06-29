using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>Small, cheap, login-only summary for the logged-in home "hub". A teaser/doorway into
    /// Min Sida — never a replacement, so it only reads inexpensive data and degrades to nulls/zeros.</summary>
    public class HomeHubSummary
    {
        public int SessionsThisMonth { get; set; }
        public DateTime? LastActivity { get; set; }
        public string? TopWeaponClass { get; set; }
        public decimal TopAveragePerSeries { get; set; }
        public int TopMatches { get; set; }
        public bool HasForm => !string.IsNullOrEmpty(TopWeaponClass) && TopMatches > 0;
        public bool HasActivity => SessionsThisMonth > 0 || LastActivity.HasValue;
    }

    public class HomeHubService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IShooterStatisticsService _stats;

        public HomeHubService(IUmbracoDatabaseFactory databaseFactory, IShooterStatisticsService stats)
        {
            _databaseFactory = databaseFactory;
            _stats = stats;
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

            // form: the member's most-played weapon class (cheap; GetStatistics is itself try/caught)
            try
            {
                var all = await _stats.GetAllStatisticsAsync(memberId);
                var top = all?.OrderByDescending(x => x.CompletedMatches).FirstOrDefault();
                if (top != null)
                {
                    s.TopWeaponClass = top.WeaponClass;
                    s.TopAveragePerSeries = top.AveragePerSeries;
                    s.TopMatches = top.CompletedMatches;
                }
            }
            catch { /* no stats → form card shows the "kom igång" fallback */ }

            return s;
        }
    }
}
