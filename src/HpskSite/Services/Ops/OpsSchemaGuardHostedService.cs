using HpskSite.Services.Hosting;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Ops
{
    /// <summary>
    /// Startkontroll för driftskollen (C3-planen F6): larmar Critical om <c>create-ops-tables.sql</c> inte är
    /// körd. Utan tabellerna räknas felsvaren bara i minnet och larmen sparas inte — och då mejlas inte heller
    /// det här larmet (larmet kan inte sparas). Raden står ändå i loggen.
    /// </summary>
    public class OpsSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<OpsSchemaGuardHostedService> _logger;

        public OpsSchemaGuardHostedService(IUmbracoDatabaseFactory databaseFactory, ILogger<OpsSchemaGuardHostedService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                var missing = new List<string>();
                foreach (var t in new[] { "dbo.RequestErrorLog", "dbo.OpsAlert" })
                    if (db.ExecuteScalar<int?>("SELECT OBJECT_ID(@0, 'U')", t) == null) missing.Add(t);
                if (missing.Count == 0) { _logger.LogInformation("Driftskollen: schemat finns."); return; }
                _logger.LogCritical("DRIFTSKOLLEN SAKNAR SINA TABELLER: {Missing}. Felsvaren räknas inte och larmen sparas inte. "
                    + "Kör Migrations/create-ops-tables.sql (guardad, säker att köra om).", string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Driftskollens startkontroll kunde inte genomföras.");
            }
        }
    }
}
