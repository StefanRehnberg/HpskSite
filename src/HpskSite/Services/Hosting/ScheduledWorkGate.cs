using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Sync;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Får den här instansen köra schemalagt arbete — påminnelser, svep, utskick, städning? (skalplanen D4)
    ///
    /// <para><b>Med två instanser körs annars varje bakgrundstjänst två gånger:</b> två påminnelsemejl, två
    /// pushar, två svep som skriver samma rader. Den som skickar eller skriver på ett schema frågar här i
    /// början av VARJE varv (rollen kan ändras medan appen kör) och hoppar över varvet vid nej.</para>
    ///
    /// <para><b>Utan <c>ScaleOut:ServerRole</c> är svaret alltid ja</b> — exakt som förut. Med rollen satt
    /// svarar den ja bara på den instans som är <c>SchedulingPublisher</c> (Umbracos egen roll, samma som
    /// Umbracos egna schemalagda jobb följer).</para>
    ///
    /// <para>⚠️ Startkontroller (schemavakter), uppvärmning och tömning av minnesköer ska INTE fråga — de
    /// gäller den egna instansen.</para>
    /// </summary>
    public class ScheduledWorkGate
    {
        private readonly IOptionsMonitor<ScaleOutOptions> _options;
        private readonly IServerRoleAccessor _roles;
        private readonly ILogger<ScheduledWorkGate> _logger;
        private int _loggedRole = -1;

        public ScheduledWorkGate(IOptionsMonitor<ScaleOutOptions> options, IServerRoleAccessor roles, ILogger<ScheduledWorkGate> logger)
        {
            _options = options;
            _roles = roles;
            _logger = logger;
        }

        public bool MayRun(string job)
        {
            if (!_options.CurrentValue.RoleConfigured) return true;
            var role = _roles.CurrentServerRole;
            var may = role is ServerRole.SchedulingPublisher or ServerRole.Single;
            if (Interlocked.Exchange(ref _loggedRole, (int)role) != (int)role)
                _logger.LogInformation("Schemalagda jobb: den här instansen är {Role} — de {Verb} här.",
                    role, may ? "körs" : "körs INTE");
            if (!may) _logger.LogDebug("{Job}: hoppar över varvet — instansen är {Role}.", job, role);
            return may;
        }
    }

    public static class ScheduledWorkGateExtensions
    {
        /// <summary>
        /// För bakgrundstjänsterna (som bara har en scope-fabrik): får varvet köras här? Går spärren inte att
        /// läsa svarar den ja — hellre ett dubbelt varv än en påminnelse som aldrig går ut.
        /// </summary>
        public static bool MayRunScheduled(this IServiceScopeFactory scopes, string job)
        {
            try
            {
                using var scope = scopes.CreateScope();
                return scope.ServiceProvider.GetRequiredService<ScheduledWorkGate>().MayRun(job);
            }
            catch { return true; }
        }
    }
}
