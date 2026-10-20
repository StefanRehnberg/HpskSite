using HpskSite.Services.Events;
using HpskSite.Services.Registrations;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Sätter anmälnings- och händelsetabellernas identitet över varje id som någon pekar på, vid VARJE
    /// start (LAG: id-reglerna, regel 3 — se CLAUDE.md).
    ///
    /// <para><b>Varför vid start och inte bara i flytten:</b> prod flyttades innan alla källor fanns i
    /// <c>IdSources</c> (fakturornas <c>registrationId</c>, lagspeglarna, papperskorgen), och omsättningen
    /// kördes bara i flytten och vid tömning. En regel som hänger på ett manuellt steg körs inte
    /// (genomgången 2026-10-13, A3). <see cref="IdentityFloor.Reseed"/> går bara framåt och låser tabellen,
    /// så den är ofarlig att köra i drift. Läggs en ny referenskolumn till behöver den bara in i
    /// <c>IdSources</c> — nästa start tar den med.</para>
    ///
    /// <para>Stoppar inte starten. Ett fel loggas som Error.</para>
    /// </summary>
    public class IdentityFloorHostedService : IsolatedBackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(2);
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<IdentityFloorHostedService> _logger;

        public IdentityFloorHostedService(IServiceScopeFactory scopeFactory, ILogger<IdentityFloorHostedService> logger)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        /// <summary>Tabellerna och deras källor. Saknas tabellen hoppas den över.</summary>
        internal static readonly (string Table, IdentityFloor.Source[] Sources)[] Tables =
        {
            ("dbo.CompetitionRegistration", TableIdSources.Registrations),
            ("dbo.ClubEvent", TableIdSources.ClubEvents),
            ("dbo.CompetitionStartList", TableIdSources.StartLists),
            ("dbo.CompetitionResultList", TableIdSources.ResultLists),
            ("dbo.Competition", TableIdSources.Competitions),
            ("dbo.CompetitionSeries", TableIdSources.Series),
        };

        /// <summary>Kör omsättningen för alla tabeller. Returnerar (tabell, före, efter) för loggen och testerna.</summary>
        public static List<(string Table, int Before, int After)> Run(IScopeProvider scopeProvider)
        {
            var result = new List<(string, int, int)>();
            foreach (var (table, sources) in Tables)
            {
                using var scope = scopeProvider.CreateScope();
                var db = scope.Database;
                if (db.ExecuteScalar<int?>("SELECT OBJECT_ID(@0, 'U')", table) == null) { scope.Complete(); continue; }
                var before = db.ExecuteScalar<int?>($"SELECT CAST(IDENT_CURRENT('{table}') AS INT)") ?? 0;
                var after = IdentityFloor.Reseed(db, table, sources);
                scope.Complete();
                result.Add((table, before, after));
            }
            return result;
        }

        protected override async Task ExecuteIsolatedAsync(CancellationToken stoppingToken)
        {
            try { await Task.Delay(StartupDelay, stoppingToken); }
            catch (OperationCanceledException) { return; }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                foreach (var (table, before, after) in Run(scope.ServiceProvider.GetRequiredService<IScopeProvider>()))
                {
                    if (after > before)
                        _logger.LogWarning("Identiteten för {Table} flyttad framåt {Before} → {After}: något id pekade över den.",
                            table, before, after);
                    else
                        _logger.LogInformation("Identiteten för {Table}: {Value}, över varje refererat id.", table, after);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Identiteternas startkontroll kunde inte genomföras.");
            }
        }
    }
}
