using HpskSite.Services.Hosting;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Startkontroll för klubbsidorna steg 1: larmar om <c>dbo.SiteOwner</c> saknas. Utan tabellen fungerar sajten
    /// som förut (den nya sidstrukturen är AV för alla), men ingen klubb kan slås på. Stoppar inte starten.
    /// En kontroll får aldrig översätta "kunde inte fråga" till "saknas": ett fel loggas som just det.
    /// </summary>
    public class SiteOwnerSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Alla Site*-tabeller sidstrukturen läser eller skriver. Ny tabell = en rad här (SiteSchemaTests fäller
        /// bygget om en migrering i Migrations/ skapar en Site*-tabell som inte står i listan).
        /// </summary>
        public static readonly IReadOnlyList<string> RequiredTables = new[]
        {
            "dbo.SiteOwner", "dbo.SitePage", "dbo.SitePageVersion", "dbo.SiteMenuItem", "dbo.SitePageRef", "dbo.SiteAudit", "dbo.SiteSupportGrant", "dbo.SiteFile", "dbo.SiteFormEntry", "dbo.SiteImageAlbum", "dbo.SiteImageAlbumItem", "dbo.SitePost", "dbo.SitePostRef"
        };

        /// <summary>De tabeller i <see cref="RequiredTables"/> som <paramref name="exists"/> inte känner igen. Ren funktion.</summary>
        public static List<string> MissingTables(Func<string, bool> exists) => RequiredTables.Where(t => !exists(t)).ToList();
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<SiteOwnerSchemaGuardHostedService> _logger;

        public SiteOwnerSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<SiteOwnerSchemaGuardHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var factory = scope.ServiceProvider.GetRequiredService<IUmbracoDatabaseFactory>();
                using var db = factory.CreateDatabase();
                var missing = MissingTables(t =>
                    db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0) IS NULL THEN 0 ELSE 1 END", t) == 1);
                if (missing.Count > 0)
                    _logger.LogCritical(
                        "KLUBBSIDOR: tabellerna {Tables} saknas — den nya sidstrukturen är AV eller halv för klubbar och kretsar. Kör migreringarna i Migrations/ (create-site-*.sql, guardade, säkra att köra om).",
                        string.Join(", ", missing));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startkontrollen för klubbsidornas tabell kunde inte genomföras.");
            }
        }
    }
}
