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
    public class AddTeamSupportToTrainingMatchesComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.AddNotificationHandler<UmbracoApplicationStartingNotification, AddTeamSupportToTrainingMatchesComponent>();
        }
    }

    public class AddTeamSupportToTrainingMatchesComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public AddTeamSupportToTrainingMatchesComponent(
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
            var migrationPlan = new MigrationPlan("AddTeamSupportToTrainingMatches");

            migrationPlan.From(string.Empty)
                .To<AddTeamSupportToTrainingMatches>("add-team-support-to-training-matches-v1");

            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
