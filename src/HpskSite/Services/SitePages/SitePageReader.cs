using HpskSite.Models;
using HpskSite.Services.Addresses;
using HpskSite.Models.Events;
using HpskSite.Services.Events;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>En egen sida bakom en adress hos en ägare. <see cref="IsCurrentAddress"/> false = en gammal adress (301 till <see cref="CurrentSlug"/>).</summary>
    public sealed record SiteAddressHit(SitePageRow Page, bool IsCurrentAddress, string CurrentSlug);

    /// <summary>
    /// Läser klubbens och kretsens egna sidor för läsvägen (finder, filter, vy). Synligheten läses ALLTID ur SQL — ingen cache av
    /// sidinnehåll eller synlighet i steg 2 (plan v3 B4); bara findern slår upp adressen, och bara när ägaren har brytare 2 PÅ.
    /// Varje sid-id prövas mot den ägare det gäller (<see cref="SitePageStore"/>).
    /// </summary>
    public sealed class SitePageReader
    {
        private readonly IScopeProvider _scopes;

        public SitePageReader(IScopeProvider scopes) { _scopes = scopes; }

        /// <summary>Sidan bakom <c>/{ägare}/{slug}/</c>, eller null. En raderad eller aldrig publicerad sida är inte "funnen".</summary>
        public SiteAddressHit? Resolve(string ownerType, int ownerId, string slug)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.FirstOrDefault<PageAddressRow>(
                "SELECT TOP 1 Id, OwnerType, OwnerId, UrlYear, Slug, TargetKind, TargetId, IsCurrent FROM dbo.PageAddress WHERE OwnerType = @0 AND OwnerId = @1 AND UrlYear IS NULL AND Slug = @2 AND TargetKind = @3",
                ownerType, ownerId, slug, PageAddressKinds.SitePage);
            if (row == null) return null;
            var page = SitePageStore.GetPage(db, row.TargetId, ownerType, ownerId);
            if (page == null || page.PublishedVersionId == null) return null;
            var current = row.IsCurrent ? row.Slug
                : db.FirstOrDefault<string>("SELECT TOP 1 Slug FROM dbo.PageAddress WHERE TargetKind = @0 AND TargetId = @1 AND IsCurrent = 1", PageAddressKinds.SitePage, page.Id);
            return current == null ? null : new SiteAddressHit(page, row.IsCurrent, current);
        }

        /// <summary>Ägarens egna sidor som ligger i menyn (publicerade, ej raderade). En SQL-fråga; körs bara när ägaren har egen redigering PÅ.</summary>
        public IReadOnlyList<SiteMenuPage> MenuPages(string ownerType, int ownerId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            return SitePageStore.MenuPages(scope.Database, ownerType, ownerId)
                .Select(r => new SiteMenuPage(r.PageId, r.Title, r.Slug, (SiteVisibility)r.Visibility, r.SortOrder)).ToList();
        }

        /// <summary>Sidans rad (för filtret efter inloggningen: synligheten läses färskt), eller null.</summary>
        public SitePageRow? Page(string ownerType, int ownerId, int pageId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            return SitePageStore.GetPage(scope.Database, pageId, ownerType, ownerId);
        }

        /// <summary>Det publicerade innehållet, tolkat mot sidans AKTUELLA synlighet (en lagrad rad är inte betrodd). Null om det saknas eller är ogiltigt.</summary>
        public (SitePageRow Page, SiteContentResult Content)? LoadPublished(string ownerType, int ownerId, int pageId)
        {
            using var scope = _scopes.CreateScope(autoComplete: true);
            var page = SitePageStore.GetPage(scope.Database, pageId, ownerType, ownerId);
            if (page == null) return null;
            var version = SitePageStore.GetPublished(scope.Database, pageId, ownerType, ownerId);
            if (version == null) return null;
            var content = SiteBlockSchema.Parse(version.BlocksJson,
                page.IsFree ? SiteContentKind.FreePage : SiteContentKind.TemplateCopy, page.VisibilityLevel, SiteTemplates.For(ownerType));
            return content.Ok ? (page, content) : null;
        }
    }

    /// <summary>
    /// Live-data för blocken, ENDAST för en ägare (ägar-id kommer från begäran, aldrig från ett block). Dokument som inte hör till
    /// ägaren lämnas aldrig ut; händelser är klubbens publicerade kommande (ReadVisibility per händelse kommer i 2c).
    /// </summary>
    public sealed class SiteBlockDataFactory
    {
        private readonly DocumentService _documents;
        private readonly IClubEventStore _events;
        private readonly PageAddressUrls _urls;

        public SiteBlockDataFactory(DocumentService documents, IClubEventStore events, PageAddressUrls urls)
        {
            _documents = documents;
            _events = events;
            _urls = urls;
        }

        public ISiteBlockData For(string ownerType, int ownerId, IPublishedContent? ownerNode) =>
            new Data(this, ownerType, ownerId, ownerNode);

        /// <summary>Dokumentarkivets nivå som synlighet: Publik = Alla, Inloggade, Klubbmedlemmar, Klubbadministratörer = Dold.</summary>
        internal static SiteVisibility VisibilityOf(int accessLevel) => accessLevel switch
        {
            DocumentAccessLevel.Public => SiteVisibility.Alla,
            DocumentAccessLevel.Authenticated => SiteVisibility.Inloggade,
            DocumentAccessLevel.ClubMembers => SiteVisibility.Klubbmedlemmar,
            _ => SiteVisibility.Dold,   // ClubAdmins och allt okänt: aldrig synligt för fler
        };

        private sealed class Data : ISiteBlockData
        {
            private readonly SiteBlockDataFactory _f;
            private readonly string _ownerType;
            private readonly int _ownerId;
            private readonly IPublishedContent? _node;

            public Data(SiteBlockDataFactory f, string ownerType, int ownerId, IPublishedContent? node)
            { _f = f; _ownerType = ownerType; _ownerId = ownerId; _node = node; }

            public IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids)
            {
                var ownerTypeInt = _ownerType == SiteTemplates.Club ? DocumentOwnerType.Club : DocumentOwnerType.Region;
                var result = new List<SiteDocumentInfo>();
                foreach (var id in ids.Distinct())
                {
                    var d = _f._documents.GetDocument(id);
                    if (d == null || d.OwnerType != ownerTypeInt || d.OwnerId != _ownerId) continue;   // aldrig en annan ägares dokument
                    result.Add(new SiteDocumentInfo(d.Id, d.Title, d.Description, d.FileSize, VisibilityOf(d.AccessLevel)));
                }
                return result;
            }

            public IReadOnlyList<SiteEventInfo> UpcomingEvents(int count)
            {
                if (_ownerType != SiteTemplates.Club) return Array.Empty<SiteEventInfo>();
                var now = DateTime.Now;
                return _f._events.ListForOwner(_ownerId)
                    .Where(e => e.StartsAt.HasValue && e.StartsAt.Value >= now && e.OwnerType == ClubEventRecord.OwnerClub && e.OwnerId == _ownerId)
                    .OrderBy(e => e.StartsAt)
                    .Take(count)
                    .Select(e => new SiteEventInfo(e.DisplayName, e.StartsAt!.Value, string.IsNullOrWhiteSpace(e.Venue) ? null : e.Venue,
                        _f._urls.UrlFor(PageAddressKinds.Event, e.Id) ?? ""))
                    .ToList();
            }

            public SiteContactInfo? Contact() => _node == null ? null : new SiteContactInfo(
                _node.Value<string>("contactPerson"), _node.Value<string>("contactEmail"), _node.Value<string>("contactPhone"));
        }
    }
}
