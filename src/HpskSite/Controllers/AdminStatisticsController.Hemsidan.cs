using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Hemsidan (klubbens och kretsens egna sidor) för Statistik-fliken: hur många klubbar och kretsar som slagit
    /// på den, hur många som redigerar och har publicerat egna sidor, samt antal sidor, inlägg och anmälningar.
    /// Bara tal och ägarnamn (klubb/krets är offentliga) — aldrig sidornas innehåll. Demoklubben Ankeborg räknas inte.
    /// Egen endpoint, som planeringen: varje källa i egen try, en saknad tabell ger tomma tal och en varning.
    /// </summary>
    public partial class AdminStatisticsController
    {
        private const string HemsidanCacheKey = "admin_hemsidan_stats";

        [HttpGet]
        [HpskSite.Services.Security.InCodeAuth("IsCurrentUserAdminAsync: sajtadmin")]
        public async Task<IActionResult> GetHemsidanStats(bool force = false)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Access denied" });

            if (!force && _memoryCache.TryGetValue(HemsidanCacheKey, out object? cached) && cached != null)
                return Json(cached);

            try
            {
                var result = new { success = true, data = BuildHemsidanStats() };
                _memoryCache.Set(HemsidanCacheKey, result, CacheDuration);
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building Hemsidan statistics");
                return Json(new { success = false, message = "Hemsidans statistik kunde inte läsas: " + ex.Message });
            }
        }

        private sealed class HsOwnerRow
        {
            public string OwnerType { get; set; } = "";
            public int OwnerId { get; set; }
            public bool IsEnabled { get; set; }
            public bool EditingEnabled { get; set; }
            public bool ConvertEnabled { get; set; }
            public DateTime? EnabledDate { get; set; }
        }

        private sealed class HsPageRow
        {
            public string OwnerType { get; set; } = "";
            public int OwnerId { get; set; }
            public int Pages { get; set; }
            public int Published { get; set; }
            public int Own { get; set; }
            public DateTime? LastUpdate { get; set; }
        }

        private sealed class HsCountRow
        {
            public string OwnerType { get; set; } = "";
            public int OwnerId { get; set; }
            public int N { get; set; }
            public int N30 { get; set; }
        }

        private object BuildHemsidanStats()
        {
            var warnings = new List<string>();
            var excludedClubIds = new HashSet<int>();
            var excludedRegionIds = new HashSet<int>();
            Func<int, string?> nameOf = _ => null;
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                if (root != null) ComputeDemoExclusions(root, excludedClubIds, excludedRegionIds);
                var content = ctx.Content;
                nameOf = id => content.GetById(id)?.Name;
            }

            using var db = _databaseFactory.CreateDatabase();
            List<T> Safe<T>(string what, string sql)
            {
                try { return db.Fetch<T>(sql); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hemsidan statistics: {What} unreadable", what);
                    warnings.Add(what + " kunde inte läsas.");
                    return new List<T>();
                }
            }

            bool Demo(string type, int id) => type == "Club" ? excludedClubIds.Contains(id) : excludedRegionIds.Contains(id);

            var owners = Safe<HsOwnerRow>("Ägarna", "SELECT OwnerType, OwnerId, IsEnabled, EditingEnabled, ConvertEnabled, EnabledDate FROM dbo.SiteOwner")
                .Where(o => !Demo(o.OwnerType, o.OwnerId)).ToList();
            var pages = Safe<HsPageRow>("Sidorna",
                    @"SELECT OwnerType, OwnerId, COUNT(*) AS Pages,
                             SUM(CASE WHEN PublishedVersionId IS NOT NULL THEN 1 ELSE 0 END) AS Published,
                             SUM(CASE WHEN TemplateKey IS NULL THEN 1 ELSE 0 END) AS Own,
                             MAX(UpdatedDate) AS LastUpdate
                      FROM dbo.SitePage WHERE DeletedDate IS NULL GROUP BY OwnerType, OwnerId")
                .Where(p => !Demo(p.OwnerType, p.OwnerId)).ToList();
            var posts = Safe<HsCountRow>("Inläggen",
                    @"SELECT OwnerType, OwnerId, COUNT(*) AS N,
                             SUM(CASE WHEN PublishedDate >= DATEADD(day, -30, SYSDATETIME()) THEN 1 ELSE 0 END) AS N30
                      FROM dbo.SitePost WHERE DeletedDate IS NULL AND PublishedDate IS NOT NULL GROUP BY OwnerType, OwnerId")
                .Where(p => !Demo(p.OwnerType, p.OwnerId)).ToList();
            var forms = Safe<HsCountRow>("Anmälningarna",
                    @"SELECT OwnerType, OwnerId, COUNT(*) AS N,
                             SUM(CASE WHEN CreatedDate >= DATEADD(day, -30, SYSDATETIME()) THEN 1 ELSE 0 END) AS N30
                      FROM dbo.SiteFormEntry GROUP BY OwnerType, OwnerId")
                .Where(p => !Demo(p.OwnerType, p.OwnerId)).ToList();

            var pageBy = pages.ToDictionary(p => (p.OwnerType, p.OwnerId));
            var postBy = posts.ToDictionary(p => (p.OwnerType, p.OwnerId));
            var formBy = forms.ToDictionary(p => (p.OwnerType, p.OwnerId));

            object Summary(string type)
            {
                var on = owners.Where(o => o.OwnerType == type && o.IsEnabled).ToList();
                bool Has(HsOwnerRow o, Func<HsPageRow, bool> f) => pageBy.TryGetValue((o.OwnerType, o.OwnerId), out var p) && f(p);
                return new
                {
                    enabled = on.Count,
                    editing = on.Count(o => o.EditingEnabled),
                    converting = on.Count(o => o.ConvertEnabled),
                    withOwnPages = on.Count(o => Has(o, p => p.Own > 0)),
                    withPublished = on.Count(o => Has(o, p => p.Published > 0)),
                    pages = on.Sum(o => pageBy.TryGetValue((o.OwnerType, o.OwnerId), out var p) ? p.Own : 0),
                    posts = on.Sum(o => postBy.TryGetValue((o.OwnerType, o.OwnerId), out var p) ? p.N : 0),
                    forms = on.Sum(o => formBy.TryGetValue((o.OwnerType, o.OwnerId), out var p) ? p.N : 0),
                    new30 = on.Count(o => o.EnabledDate != null && o.EnabledDate >= DateTime.Now.AddDays(-30)),
                };
            }

            // Tabellen "Vilka som använder den" listar bara dem som byggt egna sidor (sidtalen i kortens summering är oförändrade).
            var list = owners.Where(o => o.IsEnabled && pageBy.TryGetValue((o.OwnerType, o.OwnerId), out var own) && own.Own > 0).Select(o =>
            {
                pageBy.TryGetValue((o.OwnerType, o.OwnerId), out var p);
                postBy.TryGetValue((o.OwnerType, o.OwnerId), out var po);
                formBy.TryGetValue((o.OwnerType, o.OwnerId), out var f);
                return new
                {
                    type = o.OwnerType == "Club" ? "Klubb" : "Krets",
                    name = nameOf(o.OwnerId) ?? ("#" + o.OwnerId),
                    editing = o.EditingEnabled,
                    ownPages = p?.Own ?? 0,
                    published = p?.Published ?? 0,
                    posts = po?.N ?? 0,
                    forms = f?.N ?? 0,
                    enabled = o.EnabledDate?.ToString("yyyy-MM-dd") ?? "",
                    lastUpdate = p?.LastUpdate?.ToString("yyyy-MM-dd") ?? "",
                };
            }).OrderByDescending(r => r.published).ThenBy(r => r.name).ToList();

            return new { club = Summary("Club"), region = Summary("Region"), owners = list, warnings };
        }
    }
}
