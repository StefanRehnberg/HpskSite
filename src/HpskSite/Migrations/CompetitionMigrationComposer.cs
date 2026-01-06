using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Migrations;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Migrations;
using Umbraco.Cms.Infrastructure.Migrations.Upgrade;

namespace HpskSite.Migrations
{
    // DISABLED: Database migration disabled for mock data approach
    // public class CompetitionMigrationComposer : IComposer
    // {
    //     public void Compose(IUmbracoBuilder builder)
    //     {
    //         builder.AddNotificationHandler<UmbracoApplicationStartingNotification, CompetitionMigrationComponent>();
    //     }
    // }

    public class CompetitionMigrationComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public CompetitionMigrationComponent(
            IMigrationPlanExecutor migrationPlanExecutor,
            ICoreScopeProvider scopeProvider,
            IKeyValueService keyValueService)
        {
            _migrationPlanExecutor = migrationPlanExecutor;
            _scopeProvider = scopeProvider;
            _keyValueService = keyValueService;
        }

        public void Handle(UmbracoApplicationStartingNotification notification)
        {
            // Create a migration plan for the competition system
            var migrationPlan = new MigrationPlan("CompetitionSystem");

            // Add the competition migration to the plan
            migrationPlan.From(string.Empty)
                .To<CompetitionMigration>("competition-db-v8");

            // Execute the migration plan
            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}