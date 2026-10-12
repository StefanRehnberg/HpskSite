using HpskSite.Models.Results;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// Vägrar att skapa NYA <c>competitionResult</c>-noder när tabellagringen gäller (skalplanen C1).
    ///
    /// <para>Efter bytet finns en lagring. En nod som ändå skapas — av kod som gått förbi lagringen, eller
    /// i backoffice — hamnar där ingen läser, och resultaten syns aldrig. Bättre att den vägras högljutt.
    /// Befintliga noder får fortfarande sparas (avpubliceringen och en eventuell återställning behöver det).</para>
    /// </summary>
    public class CompetitionResultNodeGuard : INotificationHandler<ContentSavingNotification>
    {
        private readonly SiteSettingService _settings;
        private readonly ILogger<CompetitionResultNodeGuard> _logger;

        public CompetitionResultNodeGuard(SiteSettingService settings, ILogger<CompetitionResultNodeGuard> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void Handle(ContentSavingNotification notification)
        {
            var created = notification.SavedEntities
                .Where(e => !e.HasIdentity && e.ContentType.Alias == CompetitionResultFields.NodeAlias)
                .ToList();
            if (created.Count == 0) return;
            if (!CompetitionResultStoreSettings.UsesTable(_settings)) return;

            _logger.LogError("En ny resultatnod vägrades under tävling {ParentId}: resultatlistorna ligger i tabellen sedan bytet.",
                created[0].ParentId);
            notification.CancelOperation(new EventMessage("Resultat",
                "Resultatlistor sparas inte längre som innehållsnoder.", EventMessageType.Error));
        }
    }
}
