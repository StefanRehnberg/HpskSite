using HpskSite.Models.Competitions;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Nodvakten för tävlingarna och serierna när tabellagringen gäller (C3-planen §5b/§5e):
    /// <list type="bullet">
    /// <item><b>Nya noder vägras</b> — en nod som skapas av kod som gått förbi lagringen (dev-seedarna) eller i
    /// backoffice hamnar där ingen läser, och tävlingen syns aldrig.</item>
    /// <item><b>Publicering vägras</b> — en gammal nod som publiceras igen efter avpubliceringen hade visat ett
    /// inaktuellt läge på de gamla trädadresserna. Att spara en befintlig nod (som ingen läser) och att avpublicera
    /// den får fortfarande göras (rensningen behöver det).</item>
    /// </list>
    /// </summary>
    public class CompetitionNodeGuard : INotificationHandler<ContentSavingNotification>, INotificationHandler<ContentPublishingNotification>
    {
        private readonly SiteSettingService _settings;
        private readonly ILogger<CompetitionNodeGuard> _logger;

        public CompetitionNodeGuard(SiteSettingService settings, ILogger<CompetitionNodeGuard> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        private static bool IsCompetitionKind(IContent e) =>
            e.ContentType.Alias == CompetitionFieldTable.NodeAlias || e.ContentType.Alias == CompetitionSeriesFieldTable.NodeAlias;

        public void Handle(ContentSavingNotification notification)
        {
            var created = notification.SavedEntities.Where(e => !e.HasIdentity && IsCompetitionKind(e)).ToList();
            if (created.Count == 0) return;
            if (!CompetitionStoreSettings.UsesTable(_settings)) return;

            _logger.LogError("En ny {Alias}-nod vägrades under nod {ParentId}: tävlingarna ligger i tabellerna sedan bytet.",
                created[0].ContentType.Alias, created[0].ParentId);
            notification.CancelOperation(new EventMessage("Tävlingar",
                "Tävlingar och serier sparas inte längre som innehållsnoder.", EventMessageType.Error));
        }

        public void Handle(ContentPublishingNotification notification)
        {
            var published = notification.PublishedEntities.Where(IsCompetitionKind).ToList();
            if (published.Count == 0) return;
            if (!CompetitionStoreSettings.UsesTable(_settings)) return;

            _logger.LogError("Publiceringen av {Alias}-nod {Id} vägrades: tävlingarna ligger i tabellerna sedan bytet.",
                published[0].ContentType.Alias, published[0].Id);
            notification.CancelOperation(new EventMessage("Tävlingar",
                "Tävlingar och serier publiceras inte längre som innehållsnoder — de redigeras på sajten.", EventMessageType.Error));
        }
    }
}
