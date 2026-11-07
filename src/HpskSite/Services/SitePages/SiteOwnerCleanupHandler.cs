using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Städar sidstrukturens rader när en klubb eller krets raderas PERMANENT (klubbsidorna F1). Papperskorgen rörs
    /// inte: en återställd klubb ska ha sin brytare kvar. Idag finns bara <c>SiteOwner</c>; de övriga Site*-tabellerna
    /// (steg 2a) läggs till här, i samma anrop, och följer samma regel: ett nod-id som återanvänds får aldrig ärva.
    /// Ett fel här fäller aldrig raderingen — det loggas.
    /// </summary>
    public class SiteOwnerCleanupHandler : INotificationHandler<ContentDeletedNotification>
    {
        private readonly SiteOwnerService _owners;
        private readonly ILogger<SiteOwnerCleanupHandler> _logger;

        public SiteOwnerCleanupHandler(SiteOwnerService owners, ILogger<SiteOwnerCleanupHandler> logger)
        {
            _owners = owners;
            _logger = logger;
        }

        /// <summary>Alias → ägartyp. Ren funktion för testerna.</summary>
        public static string? OwnerTypeOf(string alias) => alias switch
        {
            "club" => SiteTemplates.Club,
            "regionalPage" => SiteTemplates.Region,
            _ => null
        };

        public void Handle(ContentDeletedNotification notification)
        {
            foreach (var e in notification.DeletedEntities)
            {
                var type = OwnerTypeOf(e.ContentType.Alias);
                if (type == null || e.Id <= 0) continue;
                try { _owners.Remove(type, e.Id); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sidstrukturens rader för raderad {OwnerType} {OwnerId} kunde inte tas bort.", type, e.Id);
                }
            }
        }
    }
}
