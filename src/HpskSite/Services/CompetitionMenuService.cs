using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Tävlingsmenyns genvägar (serier med <c>showInMenu</c>, fristående tävlingar med <c>addToMenu</c>).
    ///
    /// <para><b>Förut gick <c>Master.cshtml</c> igenom HELA innehållsträdet vid varje sidvisning</b> för att
    /// hitta dem (C3-planen Q1, 2026-10-13). Nu går den bara igenom tävlingshubben, och svaret cachas tills en
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

        public CompetitionMenuService(AppCaches caches) => _caches = caches;

        public sealed record MenuItem(string Label, string Url, bool IsSeries);

        public IReadOnlyList<MenuItem> Get(IPublishedContent? competitionsHub)
        {
            if (competitionsHub == null) return Array.Empty<MenuItem>();
            return _caches.RuntimeCache.GetCacheItem(CacheKey, () => Build(competitionsHub, DateTime.Today), CacheFor)
                   ?? (IReadOnlyList<MenuItem>)Array.Empty<MenuItem>();
        }

        public static void Invalidate(AppCaches caches) => CompetitionNavigationCaches.Invalidate(caches);

        private static IReadOnlyList<MenuItem> Build(IPublishedContent hub, DateTime today)
        {
            var candidates = new List<(DateTime Key, MenuItem Item, bool IsSeries)>();
            foreach (var x in hub.Descendants())
            {
                var alias = x.ContentType.Alias;
                if (alias != "competition" && alias != "competitionSeries") continue;
                if (!x.Value<bool>("isActive", fallback: Fallback.ToDefaultValue, defaultValue: true)) continue;

                if (alias == "competitionSeries")
                {
                    if (!x.Value<bool>("showInMenu")) continue;
                    var start = x.Value<DateTime>("seriesStartDate");
                    var end = x.Value<DateTime>("seriesEndDate");
                    var key = MenuOrder.KeyFor(start, end, today);
                    if (key == null) continue;
                    candidates.Add((key.Value, new MenuItem(x.Value<string>("seriesName") ?? x.Name, x.Url(), true), true));
                }
                else
                {
                    if (!x.Value<bool>("addToMenu")) continue;
                    if (x.Parent()?.ContentType.Alias == "competitionSeries") continue; // omgångar syns via serien
                    var date = x.Value<DateTime>("competitionDate");
                    var end = x.Value<DateTime>("competitionEndDate");
                    var key = MenuOrder.KeyFor(date, end, today);
                    if (key == null) continue;
                    candidates.Add((key.Value, new MenuItem(x.Name, x.Url(), false), false));
                }
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
