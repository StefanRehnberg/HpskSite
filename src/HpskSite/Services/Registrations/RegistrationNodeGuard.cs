using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// Vägrar att skapa NYA anmälningsnoder när tabellagringen gäller (sajtstrukturen fas 1d).
    ///
    /// <para>Efter bytet finns en lagring. En nod som ändå skapas — av kod som gått förbi lagringen, eller
    /// i backoffice — hamnar där ingen läser, och anmälan syns aldrig. Bättre att den vägras högljutt.
    /// Befintliga noder får fortfarande sparas (avpubliceringen och en eventuell återställning behöver det).</para>
    /// </summary>
    public class RegistrationNodeGuard : INotificationHandler<ContentSavingNotification>
    {
        private readonly SiteSettingService _settings;
        private readonly ILogger<RegistrationNodeGuard> _logger;

        public RegistrationNodeGuard(SiteSettingService settings, ILogger<RegistrationNodeGuard> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void Handle(ContentSavingNotification notification)
        {
            var newRegistrations = notification.SavedEntities
                .Where(e => !e.HasIdentity && e.ContentType.Alias == NodeRegistrationStore.RegistrationAlias)
                .ToList();
            if (newRegistrations.Count == 0) return;

            var store = _settings.Get(RegistrationStoreSettings.StoreKey, RegistrationStoreSettings.StoreNode);
            if (!string.Equals(store, RegistrationStoreSettings.StoreTable, StringComparison.OrdinalIgnoreCase)) return;

            _logger.LogError("En ny anmälningsnod vägrades ({Name}): anmälningarna ligger i tabellerna sedan bytet.",
                newRegistrations[0].Name);
            notification.CancelOperation(new EventMessage("Anmälan",
                "Anmälningar sparas inte längre som innehållsnoder.", EventMessageType.Error));
        }
    }
}
