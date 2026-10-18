using HpskSite.Models.Competitions;
using HpskSite.Services.SiteSettings;
using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Vägrar att skapa NYA <c>competition</c>- och <c>competitionSeries</c>-noder när tabellagringen gäller
    /// (C3-planen §5b).
    ///
    /// <para>Efter bytet finns en lagring. En nod som ändå skapas — av kod som gått förbi lagringen (dev-seedarna),
    /// eller i backoffice — hamnar där ingen läser, och tävlingen syns aldrig. Bättre att den vägras högljutt.
    /// Befintliga noder får fortfarande sparas (rensningen och en eventuell återställning behöver det).</para>
    /// </summary>
    public class CompetitionNodeGuard : INotificationHandler<ContentSavingNotification>
    {
        private readonly SiteSettingService _settings;
        private readonly ILogger<CompetitionNodeGuard> _logger;

        public CompetitionNodeGuard(SiteSettingService settings, ILogger<CompetitionNodeGuard> logger)
        {
            _settings = settings;
            _logger = logger;
        }

        public void Handle(ContentSavingNotification notification)
        {
            var created = notification.SavedEntities
                .Where(e => !e.HasIdentity && (e.ContentType.Alias == CompetitionFieldTable.NodeAlias
                                               || e.ContentType.Alias == CompetitionSeriesFieldTable.NodeAlias))
                .ToList();
            if (created.Count == 0) return;
            if (!CompetitionStoreSettings.UsesTable(_settings)) return;

            _logger.LogError("En ny {Alias}-nod vägrades under nod {ParentId}: tävlingarna ligger i tabellerna sedan bytet.",
                created[0].ContentType.Alias, created[0].ParentId);
            notification.CancelOperation(new EventMessage("Tävlingar",
                "Tävlingar och serier sparas inte längre som innehållsnoder.", EventMessageType.Error));
        }
    }
}
