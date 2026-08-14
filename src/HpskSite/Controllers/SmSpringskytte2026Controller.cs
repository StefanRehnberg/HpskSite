using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Bespoke event site for SM i Springskytte 2026 (Hallands Pistolskyttekrets, Obbhult 22–23 aug).
    ///
    ///   /sm-springskytte-2026              → hub: welcome, snabbfakta, tidsplan, vägen dit, länkar
    ///   /sm-springskytte-2026/pm           → Tävlings-PM in full
    ///   /sm-springskytte-2026/banor        → löpslingor + skjutområden (kartor)
    ///   /sm-springskytte-2026/service      → servering, parkering, boende
    ///   /sm-springskytte-2026/funktionarer → funktionärsinfo + länkar till schema/bemanning
    ///
    /// Routed controller, no Umbraco node (same pattern as /mitt-schema, /styrelse, /siktbild) — the
    /// content is fixed for this one event, so a doctype + backoffice setup buys nothing and would be
    /// one more thing to get wrong the week before the event.
    ///
    /// The page is PUBLIC and rendered server-side: participants open it at a quarry with poor coverage,
    /// so it must not depend on a client fetch or a login completing.
    ///
    /// The competition NODE (anmälan, startlistor, resultat, live) stays where it is — this site only
    /// links to it. The node is resolved by URL segment rather than a hardcoded id so the same code works
    /// in dev and prod; override with config key "SmSpringskytte2026:CompetitionUrlSegment" if the
    /// competition is ever renamed.
    /// </summary>
    /// <summary>A playable tutorial film, surfaced as a button on the event site.</summary>
    public record SmTutorialLink(string Id, string Title);

    [Route("sm-springskytte-2026")]
    public class SmSpringskytte2026Controller : Controller
    {
        /// <summary>
        /// Where the competition lives in production. Used only when the node can't be resolved (dev
        /// databases don't have it) — the page must never lose the link to anmälan and startlistor,
        /// which is the one thing every reader eventually needs.
        /// </summary>
        private const string FallbackCompetitionUrl = "/competitions/2026/halland/sm-springskytte-2026/";

        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IConfiguration _configuration;

        // The competition node moves rarely; a short cache keeps a mail-out spike off the content tree.
        private static readonly object CacheLock = new();
        private static int _cachedCompetitionId;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        public SmSpringskytte2026Controller(IUmbracoContextAccessor umbracoContextAccessor, IConfiguration configuration)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _configuration = configuration;
        }

        [HttpGet("")]
        public IActionResult Index() => Page("SmSpringskytte2026", "start", "SM i Springskytte 2026");

        [HttpGet("pm")]
        public IActionResult Pm() => Page("SmSpringskytte2026Pm", "pm", "Tävlings-PM — SM i Springskytte 2026");

        [HttpGet("banor")]
        public IActionResult Banor() => Page("SmSpringskytte2026Banor", "banor", "Banor och skjutområden — SM i Springskytte 2026");

        [HttpGet("service")]
        public IActionResult Service() => Page("SmSpringskytte2026Service", "service", "Service, parkering och boende — SM i Springskytte 2026");

        [HttpGet("funktionarer")]
        public IActionResult Funktionarer() => Page("SmSpringskytte2026Funktionarer", "funktionarer", "För funktionärer — SM i Springskytte 2026");

        private IActionResult Page(string view, string active, string title)
        {
            // Master.cshtml inherits UmbracoViewPage and calls Model.Root() / .Url() / .Children, so the
            // views need an IPublishedContent model even though none of this content lives in Umbraco.
            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
                return StatusCode(500, "Umbraco-kontext saknas.");

            var rootNode = ctx.Content.GetAtRoot().FirstOrDefault();
            if (rootNode == null)
                return StatusCode(500, "Ingen rotnod hittades.");

            var competition = ResolveCompetition(ctx);

            var (scheduleTutorials, notisTutorials) = ResolveTutorials(ctx);

            ViewData["ActivePage"] = active;
            ViewData["Title"] = title;
            ViewData["CompetitionId"] = competition?.Id ?? 0;
            ViewData["CompetitionUrl"] = competition?.Url() ?? FallbackCompetitionUrl;
            ViewData["ScheduleTutorials"] = scheduleTutorials;
            ViewData["NotisTutorials"] = notisTutorials;
            ViewData["ContactName"] = competition?.Value<string>("competitionDirector") ?? "";
            ViewData["ContactEmail"] = competition?.Value<string>("contactEmail") ?? "";
            ViewData["ContactPhone"] = competition?.Value<string>("contactPhone") ?? "";

            return View(view, rootNode);
        }

        /// <summary>
        /// Splits the published tutorials into the ones about Mitt schema and the ones about notiser.
        ///
        /// Matched on SUBJECT rather than a hardcoded id list: the catalogue grows (iPhone-notiser is
        /// live, Mitt schema-guiden är på väg, fler kan komma) and a fixed list would silently keep new
        /// films off the page. A tutorial without a youtubeId is skipped — the node is often created
        /// before the film is uploaded, and a button that opens an empty ruta is worse than no button.
        /// </summary>
        private (List<SmTutorialLink> Schedule, List<SmTutorialLink> Notiser) ResolveTutorials(IUmbracoContext ctx)
        {
            var schedule = new List<SmTutorialLink>();
            var notiser = new List<SmTutorialLink>();

            try
            {
                var root = ctx.Content?.GetAtRoot().FirstOrDefault();
                var hub = root?.Children.FirstOrDefault(x => x.ContentType.Alias == "tutorialPage");
                if (hub == null) return (schedule, notiser);

                foreach (var t in hub.Children.Where(c => c.ContentType.Alias == "tutorial"))
                {
                    var id = t.Value<string>("tutorialId") ?? "";
                    var youtubeId = t.Value<string>("youtubeId") ?? "";
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(youtubeId)) continue;

                    var titleValue = t.Value<string>("tutorialTitle");
                    if (string.IsNullOrWhiteSpace(titleValue)) titleValue = t.Name;

                    var haystack = (id + " " + titleValue).ToLowerInvariant();
                    if (haystack.Contains("notis")) notiser.Add(new SmTutorialLink(id, titleValue));
                    else if (haystack.Contains("schema")) schedule.Add(new SmTutorialLink(id, titleValue));
                }
            }
            catch
            {
                // A missing tutorial hub must not take the event site down.
            }

            return (schedule, notiser);
        }

        /// <summary>
        /// Finds the competition node under the competitions hub. Returns null rather than throwing —
        /// every link that needs it is rendered conditionally, so a missing node degrades the page
        /// instead of breaking it.
        /// </summary>
        private IPublishedContent? ResolveCompetition(IUmbracoContext ctx)
        {
            var configured = _configuration.GetValue<int>("SmSpringskytte2026:CompetitionId");
            if (configured > 0)
                return ctx.Content?.GetById(configured);

            lock (CacheLock)
            {
                if (_cachedCompetitionId > 0 && DateTime.UtcNow - _cachedAt < CacheTtl)
                {
                    var cached = ctx.Content?.GetById(_cachedCompetitionId);
                    if (cached != null) return cached;
                }
            }

            var segment = _configuration.GetValue<string>("SmSpringskytte2026:CompetitionUrlSegment")
                          ?? "sm-springskytte-2026";

            var root = ctx.Content?.GetAtRoot().FirstOrDefault();
            var hub = root?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");
            var found = hub?.Descendants()
                .FirstOrDefault(c => c.ContentType.Alias == "competition"
                                     && string.Equals(c.UrlSegment, segment, StringComparison.OrdinalIgnoreCase));

            if (found != null)
            {
                lock (CacheLock)
                {
                    _cachedCompetitionId = found.Id;
                    _cachedAt = DateTime.UtcNow;
                }
            }

            return found;
        }
    }
}
