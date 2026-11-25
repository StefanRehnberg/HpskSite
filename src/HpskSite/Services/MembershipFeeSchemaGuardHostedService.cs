using HpskSite.Services.Hosting;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services
{
    /// <summary>
    /// Startkontroll för den byggda medlemsavgiften: tabellerna och de nya kolumnerna på avgiftsraden. NPoco skriver
    /// <c>PartId</c>/<c>SubjectMemberId</c> på varje rad, så saknas de faller varje avgiftsrad — kontrollen namnger vad som saknas.
    ///
    /// <para>Critical (prod loggar Warning och uppåt). Kunde kontrollen inte genomföras sägs det; "kunde inte fråga" är
    /// aldrig samma sak som "saknas".</para>
    /// </summary>
    public class MembershipFeeSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(85);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MembershipFeeSchemaGuardHostedService> _logger;

        public MembershipFeeSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<MembershipFeeSchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        public static readonly string[] Tables =
            { "ClubMembershipType", "ClubFeeOption", "ClubMembershipOption", "MembershipFeePart", "MembershipFeePartType",
              // Steg 3b: åldersgränser (create-membership-age-rules.sql)
              "ClubMembershipTypeAge", "ClubMemberAgeRuleOptOut", "ClubMembershipTypeChange" };

        public static readonly (string Table, string Column)[] Columns =
            { ("MembershipFeeChargeLine", "PartId"), ("MembershipFeeChargeLine", "SubjectMemberId") };

        /// <summary>Det som saknas, kommaseparerat — eller null när allt finns. Kastar när databasen inte svarar.</summary>
        public static string? Problem(IScopeProvider scopeProvider)
        {
            using var scope = scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var haveTables = db.Fetch<string>("SELECT name FROM sys.tables");
            var missing = Tables.Where(t => !haveTables.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var (table, column) in Columns)
            {
                var n = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM sys.columns WHERE object_id = OBJECT_ID(@0) AND name = @1", "dbo." + table, column);
                if (n == 0) missing.Add($"{table}.{column}");
            }
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var problem = Problem(scope.ServiceProvider.GetRequiredService<IScopeProvider>());
                if (problem == null)
                    _logger.LogInformation("Medlemsavgiften: tabellerna och kolumnerna finns.");
                else
                    _logger.LogCritical(
                        "MEDLEMSAVGIFTENS TABELLER ÄR TRASIGA: {Missing} saknas. Avgiftsraderna kan inte skrivas och klubbens "
                        + "medlemstyper faller tillbaka på standardnamnen. Kör Migrations/create-membership-fee-parts.sql och Migrations/create-membership-age-rules.sql.", problem);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlemsavgiftens startkontroll kunde inte genomföras.");
            }
        }
    }
}
