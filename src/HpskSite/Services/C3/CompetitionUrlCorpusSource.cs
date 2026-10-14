using HpskSite.Models.StartLists;
using HpskSite.Services.Results;
using HpskSite.Services.StartLists;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services.C3
{
    /// <summary>
    /// C3-planen S0 punkt 3: adresskorpusen — varje adress till en tävling, serie, säsong, hubben och
    /// tävlingarnas listor som sajten serverar i dag, plus de lagrade omdirigeringarna och besöksloggens
    /// adresser under <c>/competitions/</c>. Skriptet <c>hpsk-verify/c3/c3-url-corpus.mjs</c> begär varje
    /// adress och sparar status, slutadress, <c>og:url</c> och en hash av innehållet; S6 (adresserna i en
    /// tabell) kräver att varje adress i korpusen fortfarande ger 200 eller 301, aldrig 404.
    ///
    /// <para>Tre former per tävling: den fina adressen (<c>Url()</c>, CompetitionUrlProvider), den stabila
    /// <c>/competitions/{id}/</c> och trädadressen (nodernas segment), som Umbraco fortfarande serverar.</para>
    /// </summary>
    public sealed class CompetitionUrlCorpusSource
    {
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IStartListStore _startLists;
        private readonly ICompetitionResultStore _results;
        private readonly IUmbracoDatabaseFactory _databaseFactory;

        public CompetitionUrlCorpusSource(IUmbracoContextAccessor umbracoContextAccessor, IContentService contentService,
            IContentTypeService contentTypeService, IStartListStore startLists, ICompetitionResultStore results,
            IUmbracoDatabaseFactory databaseFactory)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _startLists = startLists;
            _results = results;
            _databaseFactory = databaseFactory;
        }

        public sealed class Entry
        {
            public string Path { get; set; } = "";
            public string Kind { get; set; } = "";
            public int NodeId { get; set; }
            public string Note { get; set; } = "";
        }

        private sealed class RedirectRow { public string Url { get; set; } = ""; public Guid ContentKey { get; set; } }
        private sealed class VisitRow { public string Path { get; set; } = ""; public int Hits { get; set; } }

        public List<Entry> Build(int visitorTop = 400)
        {
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                throw new InvalidOperationException("Ingen Umbraco-kontext.");
            var cache = ctx.Content;
            var list = new List<Entry>();
            void Add(string? path, string kind, int id, string note = "")
            {
                if (string.IsNullOrWhiteSpace(path) || path == "#") return;
                list.Add(new Entry { Path = path, Kind = kind, NodeId = id, Note = note });
            }

            var hub = PublishedOfType("competitionsHub", cache).FirstOrDefault();
            if (hub != null) Add(hub.Url(), "hub", hub.Id);

            foreach (var season in PublishedOfType("competitionSeason", cache))
            {
                Add(season.Url(), "season", season.Id);
                AddTree(season, "season-tree", Add);
            }
            foreach (var series in PublishedOfType("competitionSeries", cache))
            {
                Add(series.Url(), "series", series.Id);
                AddTree(series, "series-tree", Add);
            }

            var comps = PublishedOfType("competition", cache).ToList();
            var resultRecords = _results.GetMany(comps.Select(c => c.Id).ToList());
            foreach (var comp in comps)
            {
                var url = comp.Url();
                Add(url, "competition", comp.Id);
                Add($"/competitions/{comp.Id}/", "competition-stable", comp.Id);
                var tree = TreePath(comp);
                if (!string.Equals(tree, url, StringComparison.OrdinalIgnoreCase)) Add(tree, "competition-tree", comp.Id);

                if (resultRecords.ContainsKey(comp.Id))
                {
                    Add(url.TrimEnd('/') + "/resultat/", "result", comp.Id);
                    if (!string.Equals(tree, url, StringComparison.OrdinalIgnoreCase)) Add(tree.TrimEnd('/') + "/resultat/", "result-tree", comp.Id);
                }
                foreach (var sl in _startLists.List(comp.Id))
                {
                    Add(sl.PublicUrl(url), sl.Kind == StartListKind.Finals ? "finals" : "startlist", comp.Id, $"lista {sl.Id}{(sl.IsOfficial ? "" : " (inte officiell)")}");
                }
            }

            // Klubbarnas och kretsarnas sidor bär tävlingslistor och kalendrar (referensläget, inte adresserna).
            foreach (var clubId in comps.Select(c => c.Value<int>("clubId")).Where(id => id > 0).Distinct())
            {
                var club = cache.GetById(clubId);
                if (club?.ContentType.Alias == "club") Add(club.Url(), "club", club.Id);
            }
            foreach (var region in PublishedOfType("regionalPage", cache))
                Add(region.Url(), "region", region.Id);

            using var db = _databaseFactory.CreateDatabase();
            foreach (var r in db.Fetch<RedirectRow>("SELECT url AS Url, contentKey AS ContentKey FROM umbracoRedirectUrl"))
            {
                // Lagras som "{rotnodens id}/sökväg" eller "/sökväg", utan avslutande snedstreck.
                var u = r.Url;
                var slash = u.IndexOf('/');
                if (slash > 0 && int.TryParse(u[..slash], out _)) u = u[slash..];
                if (!u.StartsWith('/')) u = "/" + u;
                var target = cache.GetById(r.ContentKey);
                Add(u.EndsWith('/') ? u : u + "/", "redirect", target?.Id ?? 0, target == null ? "mål saknas" : target.ContentType.Alias);
            }

            try
            {
                foreach (var v in db.Fetch<VisitRow>(
                    $@"SELECT TOP {Math.Clamp(visitorTop, 0, 5000)} LEFT(Path, 400) AS Path, COUNT(*) AS Hits FROM VisitorLogs
                       WHERE Path LIKE '/competitions/%' GROUP BY LEFT(Path, 400) ORDER BY COUNT(*) DESC"))
                    Add(v.Path, "visitor", 0, v.Hits + " besök");
            }
            catch { /* besöksloggen saknas i en äldre databas — korpusen klarar sig utan */ }

            return list
                .GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(e => KindRank(e.Kind)).First())
                .OrderBy(e => e.Path, StringComparer.Ordinal)
                .ToList();
        }

        private static int KindRank(string kind) => kind switch
        {
            "competition" or "series" or "season" or "hub" => 0,
            "result" or "startlist" or "finals" => 1,
            "club" or "region" => 1,
            "competition-stable" => 2,
            "competition-tree" or "series-tree" or "season-tree" or "result-tree" => 3,
            "redirect" => 4,
            _ => 5,
        };

        private IEnumerable<IPublishedContent> PublishedOfType(string alias, Umbraco.Cms.Core.PublishedCache.IPublishedContentCache cache)
        {
            var type = _contentTypeService.Get(alias);
            if (type == null) yield break;
            const int pageSize = 500;
            long page = 0, total;
            do
            {
                var batch = _contentService.GetPagedOfType(type.Id, page, pageSize, out total, null, null);
                foreach (var c in batch)
                {
                    if (c.Trashed) continue;
                    var p = cache.GetById(c.Id);
                    if (p != null) yield return p;
                }
                page++;
            } while (page * pageSize < total);
        }

        /// <summary>Trädadressen: segmenten från nivå 2 och nedåt (startsidan döljs i adressen).</summary>
        private static string TreePath(IPublishedContent node)
        {
            var segs = node.AncestorsOrSelf().Where(a => a.Level >= 2).OrderBy(a => a.Level).Select(a => a.UrlSegment);
            return "/" + string.Join("/", segs) + "/";
        }

        private static void AddTree(IPublishedContent node, string kind, Action<string?, string, int, string> add)
        {
            var tree = TreePath(node);
            if (!string.Equals(tree, node.Url(), StringComparison.OrdinalIgnoreCase)) add(tree, kind, node.Id, "");
        }
    }
}
