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
        private readonly Umbraco.Cms.Infrastructure.Scoping.IScopeProvider _scopes;
        private readonly HpskSite.Services.Storage.IFileStore _store;

        public SiteOwnerCleanupHandler(SiteOwnerService owners, ILogger<SiteOwnerCleanupHandler> logger,
            Umbraco.Cms.Infrastructure.Scoping.IScopeProvider scopes, HpskSite.Services.Storage.IFileStore store)
        {
            _owners = owners;
            _logger = logger;
            _scopes = scopes;
            _store = store;
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
                List<string> files = new();
                try
                {
                    using var scope = _scopes.CreateScope(autoComplete: true);
                    files = SiteFiles.AllForOwner(scope.Database, type, e.Id).Select(f => f.StoredFile).ToList();
                }
                catch (Exception ex) { _logger.LogError(ex, "Bildfilerna för raderad {OwnerType} {OwnerId} kunde inte listas.", type, e.Id); }
                try { _owners.Remove(type, e.Id); }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Sidstrukturens rader för raderad {OwnerType} {OwnerId} kunde inte tas bort.", type, e.Id);
                    continue;   // raderna står kvar: filerna får inte försvinna ur synfältet
                }
                foreach (var f in files)
                {
                    try { _store.DeleteAsync(SiteImages.Area, f).GetAwaiter().GetResult(); }
                    catch (Exception ex) { _logger.LogWarning(ex, "Bildfilen {File} för raderad {OwnerType} {OwnerId} kunde inte tas bort.", f, type, e.Id); }
                }
            }
        }
    }
}
