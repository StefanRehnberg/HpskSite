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
    /// Composer to register the Precision Results refactoring migration
    /// </summary>
    public class RefactorPrecisionResultsComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.AddNotificationHandler<UmbracoApplicationStartingNotification, RefactorPrecisionResultsComponent>();
        }
    }

    /// <summary>
    /// Migration component that executes the precision results refactoring
    /// </summary>
    public class RefactorPrecisionResultsComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public RefactorPrecisionResultsComponent(
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
            // Create a migration plan for the precision results refactoring
            var migrationPlan = new MigrationPlan("PrecisionResultsRefactoring");

            // Add the refactoring migration to the plan
            migrationPlan.From(string.Empty)
                .To<RefactorPrecisionResultsToIdentityBased>("precision-results-identity-based-v1");

            // Execute the migration plan synchronously
            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
