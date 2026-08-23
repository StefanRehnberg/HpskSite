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
    ///   /sm-springskytte-2026/resultat     → resultatlistorna som PDF (samma sida som /startlistor)
    ///   /sm-springskytte-2026/startlistor  → startlistorna som PDF (+ resultat när sådana finns)
    ///
    /// Routed controller, no Umbraco node (same pattern as /mitt-schema, /styrelse, /siktbild) — the
    /// content is fixed for this one event, so a doctype + backoffice setup buys nothing and would be
    /// one more thing to get wrong the week before the event.
    ///
    /// The page is PUBLIC and rendered server-side: participants open it at a quarry with poor coverage,
    /// so it must not depend on a client fetch or a login completing.
    ///
    /// The competition NODE keeps anmälan, avgifter, laganmälan och betalning — this site only links to
    /// it. Tävlingsledningen körde INTE startlistor eller resultat i pistol.nu för det här SM:et
    /// (beslut 2026-08-18) — båda anslås som PDF på /resultat (= /startlistor, samma sida). Resultaten
    /// kom 2026-08-23, och sajten leder nu med dem: det här är en efterhandssida, inte en förhandssida.
    /// The node is resolved by URL segment rather than a hardcoded id so the same code works in dev and
    /// prod; override with config key "SmSpringskytte2026:CompetitionUrlSegment" if it is ever renamed.
    /// </summary>
    /// <summary>A playable tutorial film, surfaced as a button on the event site.</summary>
    public record SmTutorialLink(string Id, string Title);

    /// <summary>
    /// A PDF published on /sm-springskytte-2026/startlistor. <paramref name="UpdatedAt"/> comes from the
    /// file itself so a stale copy can never masquerade as the current one.
    /// </summary>
    public record SmDocument(string Category, string Title, string Url, string Status, DateTime? UpdatedAt, long SizeBytes, int SortKey);

    [Route("sm-springskytte-2026")]
    public class SmSpringskytte2026Controller : Controller
    {
        /// <summary>
        /// Where the competition lives in production. Used only when the node can't be resolved (dev
        /// databases don't have it) — the page must never lose the link to anmälan and startlistor,
        /// which is the one thing every reader eventually needs.
        /// </summary>
        private const string FallbackCompetitionUrl = "/competitions/2026/halland/sm-springskytte-2026/";

        /// <summary>
        /// Where the published PDFs live, relative to wwwroot.
        ///
        /// PUBLISHING A DOCUMENT IS A FILE UPLOAD — NEVER A CODE CHANGE. Everything on screen is read
        /// off the file itself, so a new start list or result list needs no deploy:
        ///   - the heading is the FILE NAME (minus extension, see <see cref="TitleFrom"/>)
        ///   - the section comes from the first word: "Startlista…" / "Resultat…" (anything else lands
        ///     under Övriga dokument rather than being hidden)
        ///   - a "(preliminär)" or "prel" token anywhere in the name renders the Preliminär badge, and
        ///     is stripped from the heading. Drop it and the badge is gone — that is the whole
        ///     preliminär → definitiv switch.
        ///   - "Uppdaterad …" is the file's own timestamp, so a replaced file can never look stale
        ///   - an optional leading number ("1 Startlista …") orders the list and is stripped from the
        ///     heading. Without it the section is alphabetical, which is not chronological.
        /// </summary>
        private const string DocumentFolder = "files/sm-springskytte-2026/dokument";

        private const string CategoryStartLists = "Startlistor";
        private const string CategoryResults = "Resultat";
        private const string CategoryOther = "Övriga dokument";

        /// <summary>Tokens that mark a document as preliminary. Matched case-insensitively.</summary>
        private static readonly string[] PreliminaryTokens = { "(preliminär)", "preliminär", "preliminar", "prel" };

        /// <summary>
        /// Single letters that are weapon groups and must stay capitalised when a kebab-cased file name
        /// is turned into a heading. Deliberately NOT every single letter — "i" is a Swedish word.
        /// </summary>
        private static readonly string[] WeaponGroupLetters = { "a", "b", "c", "r", "l", "m" };

        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IConfiguration _configuration;
        private readonly IWebHostEnvironment _environment;

        // The competition node moves rarely; a short cache keeps a mail-out spike off the content tree.
        private static readonly object CacheLock = new();
        private static int _cachedCompetitionId;
        private static DateTime _cachedAt = DateTime.MinValue;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

        public SmSpringskytte2026Controller(
            IUmbracoContextAccessor umbracoContextAccessor,
            IConfiguration configuration,
            IWebHostEnvironment environment)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _configuration = configuration;
            _environment = environment;
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

        // Två URL:er, samma sida: /startlistor är adressen som stod i utskicken före tävlingen och som
        // därför är delad och bokmärkt, /resultat är den man skriver in efteråt. Ingen redirect — en 302
        // från en länk någon just fått i handen ser ut som att sidan flyttat.
        //
        // ⚠ VYERNA LÄNKAR ALLTID TILL /startlistor, aldrig till /resultat. Razor-vyerna kompileras vid
        // körning och deployas därför utan bygge, medan den här routen är C#. En href till /resultat i en
        // vy kan alltså nå produktion innan routen gör det — och då är menylänken en 404. /resultat är
        // ett alias för inskrivna och delade adresser, inte något sidorna får peka på.
        // Vyn sätter ViewBag.Title själv utifrån vad som faktiskt ligger i mappen.
        [HttpGet("startlistor")]
        [HttpGet("resultat")]
        public IActionResult Startlistor() => Page("SmSpringskytte2026Startlistor", "startlistor", "Resultat och startlistor — SM i Springskytte 2026");

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

            // Slås upp här i C# och inte i vyn: en explicit generic i ett Razor-kodblock parsas som
            // HTML-tagg och fäller vyn utan felmeddelande.
            var documents = ResolveDocuments();
            ViewData["Documents"] = documents;

            // Resultat leder — i navigeringen, i rubrikerna och på hubben — så snart det FINNS resultat
            // att visa. Före tävlingen fanns inga, och en flik som lovade dem hade varit ett tomt löfte;
            // nu är det omvända sant. Ligger i ViewData eftersom navigeringspartialen är delad.
            ViewData["HasResultDocuments"] = documents.Any(d => d.Category == CategoryResults);

            return View(view, rootNode);
        }

        /// <summary>
        /// Läser de publicerade PDF:erna från disk och beskriver dem helt utifrån filnamnet — se
        /// <see cref="DocumentFolder"/>. Ingen kuraterad lista finns kvar: en ny startlista eller
        /// resultatlista publiceras genom att lägga filen i mappen, aldrig genom att deploya kod.
        /// Kastar aldrig: saknad mapp ger tom lista och sidan säger att listorna inte är klara än.
        /// </summary>
        private List<SmDocument> ResolveDocuments()
        {
            var documents = new List<SmDocument>();

            try
            {
                var root = _environment.WebRootPath;
                if (string.IsNullOrEmpty(root)) return documents;

                var folder = Path.Combine(root, DocumentFolder.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(folder)) return documents;

                foreach (var path in Directory.GetFiles(folder, "*.pdf"))
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var (sortKey, withoutNumber) = SplitLeadingNumber(name);
                    var (isPreliminary, withoutStatus) = StripPreliminaryToken(withoutNumber);

                    documents.Add(Describe(
                        CategoryFor(withoutStatus),
                        TitleFrom(withoutStatus),
                        isPreliminary ? "Preliminär" : "",
                        sortKey,
                        path));
                }

                // Ett ledande nummer i filnamnet vinner; annars alfabetiskt på rubriken, vilket är
                // förutsägbart men inte kronologiskt (A-listan är söndag, C-listan lördag).
                documents = documents
                    .OrderBy(d => d.SortKey)
                    .ThenBy(d => d.Title, StringComparer.Create(new System.Globalization.CultureInfo("sv-SE"), true))
                    .ToList();
            }
            catch
            {
                // Diskfel får inte ta ner evenemangssajten.
            }

            return documents;
        }

        /// <summary>
        /// Optional leading order number ("1 Startlista …", "2. Resultat …"). Returns int.MaxValue when
        /// absent so unnumbered files sort after numbered ones instead of jumping to the top.
        /// </summary>
        private static (int SortKey, string Remaining) SplitLeadingNumber(string name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name.TrimStart(), @"^(\d{1,3})[\.\)]?[\s_-]+(.+)$");
            if (match.Success && int.TryParse(match.Groups[1].Value, out var n))
                return (n, match.Groups[2].Value);

            return (int.MaxValue, name);
        }

        /// <summary>
        /// Detects and removes the preliminär marker. Removing it from the heading matters — otherwise
        /// the word shows up twice, once as text and once as a badge.
        /// </summary>
        private static (bool IsPreliminary, string Remaining) StripPreliminaryToken(string name)
        {
            foreach (var token in PreliminaryTokens)
            {
                var index = name.IndexOf(token, StringComparison.OrdinalIgnoreCase);
                if (index < 0) continue;

                // "prel" must be a whole word — it is a substring of "preliminär" and could otherwise
                // fire inside an unrelated word.
                if (token == "prel" && !IsWholeWord(name, index, token.Length)) continue;

                return (true, name.Remove(index, token.Length));
            }

            return (false, name);
        }

        private static bool IsWholeWord(string text, int index, int length)
        {
            var before = index == 0 || !char.IsLetter(text[index - 1]);
            var afterIndex = index + length;
            var after = afterIndex >= text.Length || !char.IsLetter(text[afterIndex]);
            return before && after;
        }

        private static string CategoryFor(string name)
        {
            var trimmed = name.TrimStart(' ', '-', '_');
            if (trimmed.StartsWith("startlista", StringComparison.OrdinalIgnoreCase)) return CategoryStartLists;
            if (trimmed.StartsWith("resultat", StringComparison.OrdinalIgnoreCase)) return CategoryResults;
            return CategoryOther;
        }

        /// <summary>
        /// The file name IS the heading, so whoever uploads it decides the wording. A name written with
        /// real spaces is used verbatim; a kebab-cased one gets its separators turned into spaces (and
        /// its weapon-group letters re-capitalised), so both naming styles read well.
        /// </summary>
        private static string TitleFrom(string name)
        {
            var work = name.Trim(' ', '-', '_');
            if (work.Length == 0) return "Dokument";

            if (!work.Contains(' '))
            {
                var words = work.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(w => WeaponGroupLetters.Contains(w, StringComparer.OrdinalIgnoreCase)
                        ? w.ToUpperInvariant()
                        : w);
                work = string.Join(' ', words);
            }

            // Collapse the double space a stripped "(preliminär)" leaves behind.
            work = System.Text.RegularExpressions.Regex.Replace(work, @"\s{2,}", " ").Trim(' ', '-', '_');
            if (work.Length == 0) return "Dokument";

            return char.ToUpper(work[0]) + work[1..];
        }

        private SmDocument Describe(string category, string title, string status, int sortKey, string path)
        {
            var info = new FileInfo(path);
            return new SmDocument(
                category,
                title,
                // Uri-escaped: file names are allowed to contain spaces and å/ä/ö, since the name is
                // what the reader sees. An unescaped space breaks the href.
                $"/{DocumentFolder}/{Uri.EscapeDataString(Path.GetFileName(path))}",
                status,
                info.LastWriteTime,
                info.Length,
                sortKey);
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
