using Umbraco.Cms.Core.Events;
using Umbraco.Cms.Core.Notifications;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.StartLists
{
    /// <summary>
    /// Tar bort en tävlings start- och resultatlistor ur tabellerna när tävlingen raderas PERMANENT
    /// (skalplanen fas C1/C2).
    ///
    /// <para>Som noder försvann listorna med tävlingen. I tabellerna finns ingen sådan koppling, och utan
    /// städningen låg raderna kvar för alltid — startlistorna bär skyttarnas namn och klubbar. En tävling i
    /// papperskorgen rörs inte: den kan återställas, och då ska listorna finnas kvar.</para>
    /// </summary>
    public class CompetitionTableRowsCleanupHandler : INotificationHandler<ContentDeletedNotification>
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<CompetitionTableRowsCleanupHandler> _logger;

        public CompetitionTableRowsCleanupHandler(IScopeProvider scopeProvider, ILogger<CompetitionTableRowsCleanupHandler> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        public void Handle(ContentDeletedNotification notification)
        {
            var ids = notification.DeletedEntities
                .Where(e => e.ContentType.Alias == "competition")
                .Select(e => e.Id)
                .ToList();
            if (ids.Count == 0) return;

            try
            {
                using var scope = _scopeProvider.CreateScope();
                int lists = 0, results = 0;
                foreach (var id in ids)
                {
                    lists += scope.Database.Execute(@"
IF OBJECT_ID('dbo.CompetitionStartList', 'U') IS NOT NULL
    DELETE FROM dbo.CompetitionStartList WHERE CompetitionId = @0", id);
                    results += scope.Database.Execute(@"
IF OBJECT_ID('dbo.CompetitionResultList', 'U') IS NOT NULL
    DELETE FROM dbo.CompetitionResultList WHERE CompetitionId = @0", id);
                }
                scope.Complete();
                if (lists > 0 || results > 0)
                    _logger.LogInformation("Raderade tävlingar {Ids}: {Lists} startlistor och {Results} resultatlistor borttagna ur tabellerna.",
                        string.Join(", ", ids), lists, results);
            }
            catch (Exception ex)
            {
                // Raderingen av tävlingen har redan gått igenom — en misslyckad städning får inte fälla den.
                _logger.LogError(ex, "Tabellraderna för raderade tävlingar {Ids} kunde inte tas bort.", string.Join(", ", ids));
            }
        }
    }
}
