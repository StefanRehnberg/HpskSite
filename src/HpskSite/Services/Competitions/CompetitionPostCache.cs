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
    ///
    /// <para><b>⚠️ Listorna läser alla poster i EN fråga</b> (skalplanen, mätningen 2026-10-19). I tabelläget är
    /// <c>Get(id)</c> en SQL-fråga, och en lista som frågade en tävling i taget gjorde 740 frågor för
    /// <c>/tavlingar/</c> och 767 för kretssidan på 20 000-kopian. Från <see cref="BulkAfterMisses"/>:e missen
    /// hämtas alla publicerade tävlingar med <c>List()</c>; en sida som bara visar en eller två tävlingar läser
    /// fortfarande bara dem.</para>
    /// </summary>
    public sealed class CompetitionPostCache
    {
        /// <summary>Efter så här många enskilda läsningar i samma begäran läses resten i en fråga.</summary>
        public const int BulkAfterMisses = 3;

        private readonly ICompetitionStore _store;
        private readonly Dictionary<int, CompetitionInfo> _cache = new();
        private int _misses;
        private bool _bulkLoaded;

        public CompetitionPostCache(ICompetitionStore store) => _store = store;

        public CompetitionInfo Of(IPublishedContent node)
        {
            if (_cache.TryGetValue(node.Id, out var post)) return post;

            if (!_bulkLoaded && ++_misses >= BulkAfterMisses)
            {
                _bulkLoaded = true;
                foreach (var c in _store.List())
                    _cache.TryAdd(c.Id, c);
                if (_cache.TryGetValue(node.Id, out post)) return post;
            }

            post = _store.Get(node.Id) ?? new CompetitionInfo { Id = node.Id, Name = node.Name ?? "" };
            _cache[node.Id] = post;
            return post;
        }
    }
}
