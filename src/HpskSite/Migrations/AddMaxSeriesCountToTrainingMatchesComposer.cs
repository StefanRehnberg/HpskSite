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
    public class AddMaxSeriesCountToTrainingMatchesComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.AddNotificationHandler<UmbracoApplicationStartingNotification, AddMaxSeriesCountToTrainingMatchesComponent>();
        }
    }

    public class AddMaxSeriesCountToTrainingMatchesComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public AddMaxSeriesCountToTrainingMatchesComponent(
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
            var migrationPlan = new MigrationPlan("AddMaxSeriesCountColumn");

            migrationPlan.From(string.Empty)
                .To<AddMaxSeriesCountToTrainingMatches>("add-max-series-count-column");

            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
