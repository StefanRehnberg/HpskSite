using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// Startkontroll för anmälningstabellerna (sajtstrukturen fas 1b): larmar Critical om
    /// <c>create-competition-registration-tables.sql</c> inte är körd.
    ///
    /// <para><b>⚠️ Varför den behövs redan innan växeln slås om:</b> skriptet byter primärnyckel på
    /// <c>CompetitionFeeChoice</c>, och koden skriver nu på (tävling, anmälan). Och står växeln på
    /// <c>table</c> utan tabellerna faller varje anmälan.</para>
    ///
    /// <para>Stoppar inte starten — samma avvägning som de andra startkontrollerna.</para>
    /// </summary>
    public class CompetitionRegistrationSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<CompetitionRegistrationSchemaGuardHostedService> _logger;

        public CompetitionRegistrationSchemaGuardHostedService(
            IServiceScopeFactory scopeFactory, ILogger<CompetitionRegistrationSchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>Det som saknas. Tom lista = schemat finns.</summary>
        public static List<string> MissingSchema(IScopeProvider scopeProvider)
        {
            using var scope = scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var missing = new List<string>();
            foreach (var t in new[] { "CompetitionRegistration", "CompetitionRegistrationClass", "CompetitionRegistrationLegacyId", "SiteSetting" })
                if (db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0, 'U') IS NULL THEN 0 ELSE 1 END", "dbo." + t) == 0)
                    missing.Add(t);
            if (db.ExecuteScalar<int>("SELECT CASE WHEN COL_LENGTH('dbo.CompetitionTeam', 'LegacyRegistrationDocId') IS NULL THEN 0 ELSE 1 END") == 0)
                missing.Add("CompetitionTeam.LegacyRegistrationDocId");
            if (db.ExecuteScalar<int>(@"SELECT COUNT(*) FROM sys.index_columns ic
                    JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                    WHERE i.object_id = OBJECT_ID('dbo.CompetitionFeeChoice') AND i.is_primary_key = 1") != 2)
                missing.Add("CompetitionFeeChoice (primärnyckeln tävling + anmälan)");
            return missing;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var missing = MissingSchema(scope.ServiceProvider.GetRequiredService<IScopeProvider>());
                if (missing.Count == 0)
                {
                    _logger.LogInformation("Anmälningslagringen: tabellerna finns.");
                    return;
                }
                _logger.LogCritical(
                    "ANMÄLNINGSTABELLERNA SAKNAS: {Missing}. Kör Migrations/create-competition-registration-tables.sql "
                    + "(guardad, säker att köra om). Står Registrations:Store på 'table' faller varje anmälan.",
                    string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Anmälningstabellernas startkontroll kunde inte genomföras.");
            }
        }
    }
}
