using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Tävlingsmenyns genvägar (serier med <c>showInMenu</c>, fristående tävlingar med <c>addToMenu</c>).
    ///
    /// <para><b>Förut gick <c>Master.cshtml</c> igenom HELA innehållsträdet vid varje sidvisning</b> för att
    /// hitta dem (C3-planen Q1, 2026-10-13). Sedan S4 läser den lagringarna i stället för trädet, och svaret cachas tills en
    /// tävling eller serie ändras (<see cref="Invalidate"/>) eller i högst fem minuter.</para>
    ///
    /// <para><b>Regeln (Stefan 2026-10-13):</b> bara sajtadmin lägger något i menyn (spärrat där det skrivs),
    /// och bara <b>pågående och kommande</b> visas, närmast först. Förut visades de tre ÄLDSTA serierna,
    /// även avslutade.</para>
    /// </summary>
    public class CompetitionMenuService
    {
        public const string CacheKey = CompetitionNavigationCaches.MenuKey;
        private static readonly TimeSpan CacheFor = CompetitionNavigationCaches.CacheFor;
        private readonly AppCaches _caches;
        private readonly HpskSite.Services.Competitions.CompetitionCacheVersion _cacheVersion;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly HpskSite.Services.Competitions.ICompetitionSeriesStore _series;
        private readonly Umbraco.Cms.Core.Web.IUmbracoContextAccessor _umbracoContextAccessor;

        /// <summary>Scoped: lagringarna är scoped. Svaret cachas ändå över begäranden.</summary>
        public CompetitionMenuService(AppCaches caches,
            HpskSite.Services.Competitions.ICompetitionStore competitions,
            HpskSite.Services.Competitions.ICompetitionSeriesStore series,
            Umbraco.Cms.Core.Web.IUmbracoContextAccessor umbracoContextAccessor,
            HpskSite.Services.Competitions.CompetitionCacheVersion cacheVersion)
        {
            _cacheVersion = cacheVersion;
            _caches = caches;
            _competitions = competitions;
            _series = series;
            _umbracoContextAccessor = umbracoContextAccessor;
        }

        public sealed record MenuItem(string Label, string Url, bool IsSeries);

        public IReadOnlyList<MenuItem> Get(IPublishedContent? competitionsHub)
        {
            if (competitionsHub == null) return Array.Empty<MenuItem>();
            _cacheVersion.EnsureFresh(); // en annan instans kan ha skrivit (C3-planen S5d)
            return _caches.RuntimeCache.GetCacheItem(CacheKey, () => Build(DateTime.Today), CacheFor)
                   ?? (IReadOnlyList<MenuItem>)Array.Empty<MenuItem>();
        }

        public static void Invalidate(AppCaches caches) => CompetitionNavigationCaches.Invalidate(caches);

        /// <summary>
        /// Serierna och tävlingarna ur lagringarna (C3-planen S4) — ingen trädgenomgång. Noden bara för adressen
        /// (S6); en post utan publicerad nod visas inte.
        /// </summary>
        private IReadOnlyList<MenuItem> Build(DateTime today)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return Array.Empty<MenuItem>();
            string? UrlOf(int id) => ctx.Content.GetById(id)?.Url();

            var candidates = new List<(DateTime Key, MenuItem Item, bool IsSeries)>();
            foreach (var x in _series.List())
            {
                if (!x.IsVisible || !x.ShowInMenu) continue;
                var key = MenuOrder.KeyFor(x.SeriesStartDate ?? DateTime.MinValue, x.SeriesEndDate ?? DateTime.MinValue, today);
                if (key == null) continue;
                var url = UrlOf(x.Id);
                if (url == null) continue;
                candidates.Add((key.Value, new MenuItem(x.SeriesName ?? x.Name, url, true), true));
            }
            foreach (var x in _competitions.List())
            {
                if (!x.IsVisible || !x.AddToMenu) continue;
                if (x.SeriesId != null) continue; // omgångar syns via serien
                var key = MenuOrder.KeyFor(x.CompetitionDate ?? DateTime.MinValue, x.CompetitionEndDate ?? DateTime.MinValue, today);
                if (key == null) continue;
                var url = UrlOf(x.Id);
                if (url == null) continue;
                candidates.Add((key.Value, new MenuItem(x.Name, url, false), false));
            }

            var series = candidates.Where(c => c.IsSeries).OrderBy(c => c.Key).Take(3);
            var comps = candidates.Where(c => !c.IsSeries).OrderBy(c => c.Key).Take(5);
            return series.Concat(comps).OrderBy(c => c.Key).Take(6).Select(c => c.Item).ToList();
        }
    }

    /// <summary>Menyns ordning och filter som ren funktion (testbar).</summary>
    public static class MenuOrder
    {
        /// <summary>
        /// Sorteringsnyckeln för en post som pågår eller kommer, eller null om den är avslutad. Pågående
        /// sorteras som "idag", kommande på sitt startdatum — närmast först. Saknas slutdatum gäller
        /// startdagen. Saknas båda visas den inte.
        /// </summary>
        public static DateTime? KeyFor(DateTime start, DateTime end, DateTime today)
        {
            var hasStart = start > DateTime.MinValue.AddYears(1);
            var hasEnd = end > DateTime.MinValue.AddYears(1);
            if (!hasStart && !hasEnd) return null;
            var lastDay = (hasEnd ? end : start).Date;
            if (lastDay < today.Date) return null;
            if (!hasStart || start.Date <= today.Date) return today.Date;
            return start.Date;
        }
    }
}
