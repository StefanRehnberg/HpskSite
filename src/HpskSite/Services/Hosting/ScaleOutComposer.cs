using Microsoft.Extensions.Options;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Sync;
using Umbraco.Cms.Infrastructure.DependencyInjection;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Kopplar in det som behövs för flera instanser (skalplanen D4). <b>Utan konfiguration ändras
    /// ingenting</b> — se <see cref="ScaleOutOptions"/>.
    ///
    /// <list type="bullet">
    /// <item><c>ScaleOut:ServerRole</c> → Umbracos serverroll sätts uttryckligen (i stället för att väljas
    /// ur databasen), och <see cref="ScheduledWorkGate"/> låter bara publiceraren köra schemalagt arbete.</item>
    /// <item><c>ConnectionStrings:redis</c> → Redis som andra nivå i cachen (<c>IDistributedCache</c>, som
    /// Umbracos <c>HybridCache</c> använder), och som SignalR:s koppling mellan instanserna (i Program.cs).</item>
    /// </list>
    /// </summary>
    public class ScaleOutComposer : IComposer
    {
        public const string RedisConnectionName = "redis";

        public void Compose(IUmbracoBuilder builder)
        {
            var section = builder.Config.GetSection(ScaleOutOptions.SectionName);
            builder.Services.Configure<ScaleOutOptions>(section);
            var options = section.Get<ScaleOutOptions>() ?? new ScaleOutOptions();

            builder.Services.AddSingleton<ScheduledWorkGate>();
            builder.Services.AddSingleton<PublicUrls>();
            // Cachesignalerna: registreras alltid (pollningen konstruerar dem vid start, så att Notify når dem),
            // men gör ingenting utan ScaleOut:ServerRole.
            builder.Services.AddSingleton<CacheSignals>();
            builder.Services.AddHostedService<CacheSignalPollingService>();

            if (options.RoleConfigured)
            {
                var role = string.Equals(options.ServerRole, ScaleOutOptions.Publisher, StringComparison.OrdinalIgnoreCase)
                    ? ServerRole.SchedulingPublisher
                    : ServerRole.Subscriber;
                builder.SetServerRegistrar(new ConfiguredServerRoleAccessor(role));
            }

            var redis = builder.Config.GetConnectionString(RedisConnectionName);
            if (!string.IsNullOrWhiteSpace(redis))
            {
                // abortConnect=false: en Redis som inte svarar vid start får inte stoppa sajten. Cachen
                // faller då tillbaka på minnet (HybridCache tål ett fel i andra nivån).
                var configuration = redis.Contains("abortConnect", StringComparison.OrdinalIgnoreCase)
                    ? redis : redis + ",abortConnect=false";
                builder.Services.AddStackExchangeRedisCache(o =>
                {
                    o.Configuration = configuration;
                    o.InstanceName = "hpsk:";
                });
            }
        }
    }

    /// <summary>En uttryckligt konfigurerad serverroll (Umbracos rekommendation vid lastbalansering).</summary>
    public sealed class ConfiguredServerRoleAccessor : IServerRoleAccessor
    {
        public ConfiguredServerRoleAccessor(ServerRole role) => CurrentServerRole = role;
        public ServerRole CurrentServerRole { get; }
    }
}
