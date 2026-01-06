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
    // DISABLED: Migration already applied - WeaponClass column exists in TrainingScores table
    // public class AddWeaponClassToTrainingScoresComposer : IComposer
    // {
    //     public void Compose(IUmbracoBuilder builder)
    //     {
    //         builder.AddNotificationHandler<UmbracoApplicationStartingNotification, AddWeaponClassToTrainingScoresComponent>();
    //     }
    // }

    public class AddWeaponClassToTrainingScoresComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public AddWeaponClassToTrainingScoresComponent(
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
            // Create a migration plan for adding the WeaponClass column
            var migrationPlan = new MigrationPlan("AddWeaponClassColumn");

            // Add the migration to the plan
            migrationPlan.From(string.Empty)
                .To<AddWeaponClassToTrainingScores>("add-weapon-class-column");

            // Execute the migration plan
            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
