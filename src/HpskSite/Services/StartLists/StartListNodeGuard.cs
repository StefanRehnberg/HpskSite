using HpskSite.Models.StartLists;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// Vägrar att skapa NYA <c>precisionStartList</c>- och <c>finalsStartList</c>-noder när tabellagringen
    /// gäller (skalplanen C2).
    ///
    /// <para>Efter bytet finns en lagring. En nod som ändå skapas — av kod som gått förbi lagringen, eller
    /// i backoffice — hamnar där ingen läser, och listan syns aldrig. Bättre att den vägras högljutt.
    /// Befintliga noder får fortfarande sparas (avpubliceringen och en eventuell återställning behöver det).</para>
    /// </summary>
    public class StartListNodeGuard : INotificationHandler<ContentSavingNotification>
    {
        private readonly SiteSettingService _settings;
        private readonly ILogger<StartListNodeGuard> _logger;

        public StartListNodeGuard(SiteSettingService settings, ILogger<StartListNodeGuard> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void Handle(ContentSavingNotification notification)
        {
            var created = notification.SavedEntities
                .Where(e => !e.HasIdentity && StartListNodes.KindOf(e.ContentType.Alias) != null)
                .ToList();
            if (created.Count == 0) return;
            if (!StartListStoreSettings.UsesTable(_settings)) return;

            _logger.LogError("En ny startlistenod vägrades under nod {ParentId}: startlistorna ligger i tabellen sedan bytet.",
                created[0].ParentId);
            notification.CancelOperation(new EventMessage("Startlistor",
                "Startlistor sparas inte längre som innehållsnoder.", EventMessageType.Error));
        }
    }
}
