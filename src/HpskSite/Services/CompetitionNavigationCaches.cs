using Umbraco.Cms.Core.Cache;

namespace HpskSite.Services
{
    /// <summary>
    /// De cachar som bygger på tävlingarnas plats och namn: menyns genvägar och adressindexet
    /// (C3-planen Q1–Q2, 2026-10-13). Töms tillsammans när en tävling eller serie skrivs. Båda har också
    /// en kort livslängd, så en skrivning som inte går hit syns ändå inom fem minuter.
    /// </summary>
    public static class CompetitionNavigationCaches
    {
        public const string MenuKey = "competition_menu_v1";
        public const string UrlIndexKey = "competition_url_index_v1";

        /// <summary>
        /// Tabellägets adressdel → tävlingar/serier, för gamla trädadresser (<c>CompetitionTreeRedirectFinder</c>).
        /// Förut lästes hela tävlings- och serietabellen vid varje sådan förfrågan (skalplanen, mätningen 2026-10-19).
        /// </summary>
        public const string SlugIndexKey = "competition_slug_index_v1";
        public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

        public static void Invalidate(AppCaches caches)
        {
            caches.RuntimeCache.ClearByKey(MenuKey);
            caches.RuntimeCache.ClearByKey(UrlIndexKey);
            caches.RuntimeCache.ClearByKey(SlugIndexKey);
        }
    }
}
