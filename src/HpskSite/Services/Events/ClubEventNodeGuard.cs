using HpskSite.Models;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.Events
{
    /// <summary>
    /// Vägrar att skapa NYA händelsenoder när tabellagringen gäller (sajtstrukturen fas 2d). En nod som
    /// ändå skapas — av kod som gått förbi lagringen, eller i backoffice — hamnar där ingen läser.
    /// Befintliga noder får sparas (avpubliceringen och en återställning behöver det).
    /// </summary>
    public class ClubEventNodeGuard : INotificationHandler<ContentSavingNotification>
    {
        private readonly SiteSettingService _settings;
        private readonly ILogger<ClubEventNodeGuard> _logger;

        public ClubEventNodeGuard(SiteSettingService settings, ILogger<ClubEventNodeGuard> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void Handle(ContentSavingNotification notification)
        {
            var created = notification.SavedEntities
                .Where(e => !e.HasIdentity && e.ContentType.Alias == ClubEvents.EventAlias).ToList();
            if (created.Count == 0 || !ClubEventStoreSettings.UsesTable(_settings)) return;

            _logger.LogError("En ny händelsenod vägrades ({Name}): händelserna ligger i tabellen sedan bytet.", created[0].Name);
            notification.CancelOperation(new EventMessage("Händelse",
                "Händelser sparas inte längre som innehållsnoder.", EventMessageType.Error));
        }
    }
}
