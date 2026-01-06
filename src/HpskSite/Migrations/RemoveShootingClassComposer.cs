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
    // DISABLED: Migration already applied - ShootingClass column removed from TrainingScores table
    // Also had bugs with synchronous FluentMigrator API in async migration
    // public class RemoveShootingClassComposer : IComposer
    // {
    //     public void Compose(IUmbracoBuilder builder)
    //     {
    //         builder.AddNotificationHandler<UmbracoApplicationStartingNotification, RemoveShootingClassComponent>();
    //     }
    // }

    public class RemoveShootingClassComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public RemoveShootingClassComponent(
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
            // Create a migration plan for removing the ShootingClass column
            var migrationPlan = new MigrationPlan("RemoveShootingClassColumn");

            // Add the migration to the plan
            migrationPlan.From(string.Empty)
                .To<RemoveShootingClassFromTrainingScores>("remove-shootingclass-column");

            // Execute the migration plan
            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
