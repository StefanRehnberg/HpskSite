using Umbraco.Cms.Core;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Composer to register and run the RefreshTokens migration
    /// </summary>
    public class RefreshTokensMigrationComposer : ComponentComposer<RefreshTokensMigrationComponent>
    {
    }

    /// <summary>
    /// Component that runs the RefreshTokens migration on startup
    /// </summary>
    public class RefreshTokensMigrationComponent : IComponent
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly IKeyValueService _keyValueService;
        private readonly IRuntimeState _runtimeState;

        public RefreshTokensMigrationComponent(
            IScopeProvider scopeProvider,
            IMigrationPlanExecutor migrationPlanExecutor,
            IKeyValueService keyValueService,
            IRuntimeState runtimeState)
        {
            _scopeProvider = scopeProvider;
            _migrationPlanExecutor = migrationPlanExecutor;
            _keyValueService = keyValueService;
            _runtimeState = runtimeState;
        }

        public void Initialize()
        {
            // Don't run migrations if Umbraco is not fully running
            if (_runtimeState.Level < RuntimeLevel.Run)
            {
                return;
            }

            var plan = new MigrationPlan("RefreshTokens");

            // Add the migration steps
            plan.From(string.Empty)
                .To<CreateRefreshTokensTable>("refresh-tokens-init-v1")
                .To<FixRefreshTokensNullableColumns>("refresh-tokens-fix-nullable-v1")
                .To<RecreateRefreshTokensTable>("refresh-tokens-recreate-v1");

            var upgrader = new Upgrader(plan);
            upgrader.Execute(_migrationPlanExecutor, _scopeProvider, _keyValueService);
        }

        public void Terminate()
        {
        }
    }
}
