using HpskSite.Models;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Strings;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// <see cref="ISiteOwnerFacts"/> mot Umbraco-trädet och dokumentarkivet. Läses FÖRE skrivtransaktionen (Umbraco-värden läses
    /// aldrig i en skrivtransaktion). Trädet läses med <see cref="IContentService"/> — samma källa som namnspärren
    /// (<c>PageAddressReservedNames</c>) — och segmenten räknas som Umbraco gör: <c>CleanStringForUrlSegment(namn)</c>.
    /// Papperskorgens och avpublicerade noder räknas med: en sida får inte ta en adress som en nod kan få tillbaka.
    /// </summary>
    public sealed class SiteOwnerFacts : ISiteOwnerFacts
    {
        private readonly IContentService _content;
        private readonly IShortStringHelper _strings;
        private readonly DocumentService _documents;

        public SiteOwnerFacts(IContentService content, IShortStringHelper strings, DocumentService documents)
        {
            _content = content;
            _strings = strings;
            _documents = documents;
        }

        public bool DocumentBelongsTo(int docId, string ownerType, int ownerId)
        {
            var d = _documents.GetDocument(docId);
            var typeInt = ownerType == SiteTemplates.Club ? DocumentOwnerType.Club : DocumentOwnerType.Region;
            return d != null && d.OwnerType == typeInt && d.OwnerId == ownerId;
        }

        public IReadOnlyList<string> ChildNodeSegments(string ownerType, int ownerId) =>
            Children(ownerId).Select(c => Segment(c.Name)).Where(s => s.Length > 0).ToList();

        public IReadOnlyList<string> RegionClubSegments(string ownerType, int ownerId)
        {
            if (ownerType != SiteTemplates.Region) return Array.Empty<string>();
            var directory = Children(ownerId).FirstOrDefault(c => c.ContentType.Alias == "clubsPage");
            return directory == null
                ? Array.Empty<string>()
                : Children(directory.Id).Where(c => c.ContentType.Alias == "club").Select(c => Segment(c.Name)).Where(s => s.Length > 0).ToList();
        }

        private IEnumerable<Umbraco.Cms.Core.Models.IContent> Children(int parentId) =>
            _content.GetPagedChildren(parentId, 0, 2000, out _);

        private string Segment(string? name) => _strings.CleanStringForUrlSegment(name ?? "");
    }
}
