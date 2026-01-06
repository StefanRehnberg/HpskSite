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
    /// <summary>
    /// Composer to register the TrainingMatchJoinRequests migration
    /// </summary>
    public class TrainingMatchJoinRequestsMigrationComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.AddNotificationHandler<UmbracoApplicationStartingNotification, TrainingMatchJoinRequestsMigrationComponent>();
        }
    }

    /// <summary>
    /// Migration component that executes the TrainingMatchJoinRequests table creation
    /// </summary>
    public class TrainingMatchJoinRequestsMigrationComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public TrainingMatchJoinRequestsMigrationComponent(
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
            // Create a migration plan for the join requests system
            var migrationPlan = new MigrationPlan("TrainingMatchJoinRequestsSystem");

            // Add the migration to the plan
            migrationPlan.From(string.Empty)
                .To<CreateTrainingMatchJoinRequestsTable>("trainingmatch-joinrequests-v1");

            // Execute the migration plan
            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
