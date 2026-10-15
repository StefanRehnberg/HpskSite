using HpskSite.Models.Competitions;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Tävlingens post för en publicerad nod, en gång per begäran (scoped). För vyer som fortfarande
    /// går igenom tävlingarna som noder (listorna, navet, säsongen, serien) — noden behålls för adressen och
    /// trädet, fälten läses ur posten (C3-planen S3).
    ///
    /// <para>En nod som inte är en publicerad tävling ger en tom post med nodens id och namn, så att en
    /// vy aldrig behöver null-kontrollera mitt i en rad. Läs därför bara tävlingsnoder genom den.</para>
    /// </summary>
    public sealed class CompetitionPostCache
    {
        private readonly ICompetitionStore _store;
        private readonly Dictionary<int, CompetitionInfo> _cache = new();

        public CompetitionPostCache(ICompetitionStore store) => _store = store;

        public CompetitionInfo Of(IPublishedContent node)
        {
            if (_cache.TryGetValue(node.Id, out var post)) return post;
            post = _store.Get(node.Id) ?? new CompetitionInfo { Id = node.Id, Name = node.Name ?? "" };
            _cache[node.Id] = post;
            return post;
        }
    }
}
