using HpskSite.Services.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Startkontroll för skalplanen B5 (2026-10-19): larmar Critical om någon av tabellerna som tog över
    /// medlemsegenskaperna saknas. Utan <c>MemberToken</c> fungerar ingen återställningslänk, inloggningslänk
    /// eller inbjudan; utan <c>MemberTrainingProgress</c> faller Skyttetrappan; utan
    /// <c>MemberTutorialWatch</c> visar menyn inga sedda filmer. Stoppar inte starten — samma avvägning som
    /// <c>MailReplySchemaGuardHostedService</c>. Kontrollen får aldrig översätta "kunde inte fråga" till
    /// "saknas": ett fel loggas som just det.
    /// </summary>
    public class MemberTablesSchemaGuardHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

        private static readonly (string Table, string Migration, string Consequence)[] Tables =
        {
            ("MemberToken", "create-member-token-table.sql",
                "återställningslänkar, inloggningslänkar och inbjudningar fungerar inte"),
            ("MemberTrainingProgress", "create-member-training-progress-table.sql",
                "Skyttetrappan kan varken visas eller sparas"),
            ("MemberTutorialWatch", "create-member-tutorial-watch-table.sql",
                "sedda filmer sparas inte"),
        };

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<MemberTablesSchemaGuardHostedService> _logger;

        public MemberTablesSchemaGuardHostedService(IServiceScopeFactory scopeFactory, ILogger<MemberTablesSchemaGuardHostedService> logger)
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
                foreach (var (table, migration, consequence) in Tables)
                {
                    var exists = db.ExecuteScalar<int>("SELECT CASE WHEN OBJECT_ID(@0) IS NULL THEN 0 ELSE 1 END", "dbo." + table) == 1;
                    if (!exists)
                        _logger.LogCritical(
                            "MEDLEMSTABELL SAKNAS: {Table} finns inte — {Consequence}. Kör Migrations/{Migration} (guardad, säker att köra om).",
                            table, consequence, migration);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Startkontrollen för medlemstabellerna (skalplanen B5) kunde inte genomföras.");
            }
        }
    }
}
