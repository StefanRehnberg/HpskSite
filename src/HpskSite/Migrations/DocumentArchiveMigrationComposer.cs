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
    // DISABLED: Tables created manually via SQL script (CreateDocumentArchiveTables.sql)
    // public class DocumentArchiveMigrationComposer : IComposer
    // {
    //     public void Compose(IUmbracoBuilder builder)
    //     {
    //         builder.AddNotificationHandler<UmbracoApplicationStartingNotification, DocumentArchiveMigrationComponent>();
    //     }
    // }

    /// <summary>
    /// Migration component that executes the DocumentArchive table creation
    /// </summary>
    public class DocumentArchiveMigrationComponent : INotificationHandler<UmbracoApplicationStartingNotification>
    {
        private readonly IMigrationPlanExecutor _migrationPlanExecutor;
        private readonly ICoreScopeProvider _scopeProvider;
        private readonly IKeyValueService _keyValueService;

        public DocumentArchiveMigrationComponent(
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
            var migrationPlan = new MigrationPlan("DocumentArchiveSystem");

            migrationPlan.From(string.Empty)
                .To<CreateDocumentArchiveTables>("documentarchive-db-v1");

            var upgrader = new Upgrader(migrationPlan);
            upgrader.Execute(
                _migrationPlanExecutor,
                _scopeProvider,
                _keyValueService);
        }
    }
}
