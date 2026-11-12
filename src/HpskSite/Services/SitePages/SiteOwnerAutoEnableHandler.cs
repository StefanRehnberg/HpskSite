using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Nya klubbar och kretsar får den nya menyn (brytare 1) direkt när noden sparas första gången (Stefan 2026-11-12:
    /// "ja alla"). En KRETS får även egen redigering och omvandling (ingen avtalsgrind). En KLUBB får redigering och
    /// omvandling först när den godkänt DPA (<c>DpaController.AcceptDpa</c>). Bara när det SAKNAS en rad: en ägare som
    /// en sajtadmin medvetet slagit av (rad med IsEnabled = 0) rörs aldrig. Ett fel här fäller aldrig sparningen.
    /// </summary>
    public class SiteOwnerAutoEnableHandler : INotificationHandler<ContentSavedNotification>
    {
        private readonly SiteOwnerService _owners;
        private readonly ILogger<SiteOwnerAutoEnableHandler> _logger;

        public SiteOwnerAutoEnableHandler(SiteOwnerService owners, ILogger<SiteOwnerAutoEnableHandler> logger)
        {
            _owners = owners;
            _logger = logger;
        }

        public void Handle(ContentSavedNotification notification)
        {
            foreach (var e in notification.SavedEntities)
            {
                var type = SiteOwnerCleanupHandler.OwnerTypeOf(e.ContentType.Alias);
                if (type == null || e.Id <= 0 || e.Trashed) continue;
                try
                {
                    if (_owners.ListAll().Any(r => r.OwnerType == type && r.OwnerId == e.Id)) continue;
                    _owners.Set(type, e.Id, true, null);
                    if (type == SiteTemplates.Region)
                    {
                        _owners.SetEditing(type, e.Id, true, null);
                        _owners.SetConversion(type, e.Id, true, null);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Nya menyn kunde inte slås på automatiskt för ny {OwnerType} {OwnerId}.", type, e.Id);
                }
            }
        }
    }
}
