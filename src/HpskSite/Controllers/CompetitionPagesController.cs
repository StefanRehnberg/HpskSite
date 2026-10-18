using HpskSite.Models;
using HpskSite.Services.Addresses;
using HpskSite.Services.Events;
using HpskSite.Services.Results;
using HpskSite.Services.SiteSettings;
using HpskSite.Services.StartLists;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// C3-planen S7: sidorna på de nya adresserna (S6) som ASP.NET Core-routes, i stället för att Umbracos
    /// innehållsupplösare letar fram dem.
    /// <list type="bullet">
    /// <item><c>/tavlingar/</c> och <c>/tavlingar/{år}/</c> — listan och säsongen</item>
    /// <item><c>/{krets}/klubbar/{klubb}/{år}/{slug}/[barn/]</c>, <c>/{krets}/{år}/{slug}/[barn/]</c>,
    /// <c>/sm|ssm|vsm|osm|nsm/{år}/{slug}/[barn/]</c> — tävling (med resultat, startlistor, finaler), serie,
    /// händelse, ur <c>PageAddress</c></item>
    /// <item>årssidan (<c>/halland/2026/</c>) och <c>/sm/</c> → 301 till <c>/tavlingar/</c></item>
    /// </list>
    ///
    /// <para><b>Noderna är fortfarande modellen</b> så länge tävlingarna är noder — vyerna renderas som förut,
    /// med noden. Det som inte är noden själv (en startlista ur lagringen, en händelse ur tabellen) ligger i
    /// <see cref="CompetitionPageContext"/>.</para>
    ///
    /// <para><b>⚠️ En matchad route är vår:</b> Umbracos egen upplösning körs inte. Finns adressen inte svarar
    /// vi med 404-sidan. Därför är krets-segmentet spärrat mot navets eget segment (<c>competitions</c>) — de
    /// gamla adresserna går fortfarande genom Umbracos kedja (<c>CompetitionTreeRedirectFinder</c> m.fl.).</para>
    /// </summary>
    public class CompetitionPagesController : Controller
    {
        private const string Year = "{year:regex(^\\d{{4}}$)}";
        private const string National = "{owner:regex(^(sm|ssm|vsm|osm|nsm)$)}";
        // ⚠️ Inga hakparenteser: rutmallen läser [..] som en ersättningstoken. Mästerskapens namn är uteslutna,
        // annars matchar /sm/2026/… både den här och National — ASP.NET svarar då 500 (AmbiguousMatchException).
        private const string Krets = "{krets:regex(^(?!(competitions|sm|ssm|vsm|osm|nsm)$).+$)}";

        private readonly IUmbracoContextAccessor _ctx;
        private readonly PageAddressService _addresses;
        private readonly PageAddressUrls _urls;
        private readonly IRedirectUrlService _redirects;
        private readonly SiteSettingService _settings;
        private readonly CompetitionPageContext _page;
        private readonly ITemplateService _templates;

        public CompetitionPagesController(IUmbracoContextAccessor ctx, PageAddressService addresses, PageAddressUrls urls,
            IRedirectUrlService redirects, SiteSettingService settings, CompetitionPageContext page, ITemplateService templates)
        {
            _ctx = ctx;
            _addresses = addresses;
            _urls = urls;
            _redirects = redirects;
            _settings = settings;
            _page = page;
            _templates = templates;
        }

        // ── Listan ────────────────────────────────────────────────────────────────────────

        [HttpGet("tavlingar")]
        public IActionResult Hub()
        {
            var hub = HubNode();
            return hub == null ? NotFoundPage() : View("CompetitionsHub", hub);
        }

        [HttpGet("tavlingar/" + Year)]
        public IActionResult Season(string year)
        {
            var season = HubNode()?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionSeason"
                && string.Equals(c.UrlSegment, year, StringComparison.OrdinalIgnoreCase));
            return season == null ? NotFoundPage() : View("CompetitionSeason", season);
        }

        // ── Förbundets mästerskap ─────────────────────────────────────────────────────────

        [HttpGet(National)]
        public IActionResult NationalRoot(string owner) => ToList();

        [HttpGet(National + "/" + Year + "/{slug?}/{child?}")]
        public IActionResult NationalPage(string owner, string year, string? slug, string? child)
            => Serve(PageAddressOwner.ForNational(owner), int.Parse(year), slug, child);

        // ── Kretsens och klubbens ─────────────────────────────────────────────────────────

        [HttpGet(Krets + "/" + Year + "/{slug?}/{child?}")]
        public IActionResult RegionPage(string krets, string year, string? slug, string? child)
            => OwnerPage(new[] { krets }, year, slug, child);

        [HttpGet(Krets + "/klubbar/{klubb}/" + Year + "/{slug?}/{child?}")]
        public IActionResult ClubPage(string krets, string klubb, string year, string? slug, string? child)
            => OwnerPage(new[] { krets, "klubbar", klubb }, year, slug, child);

        private IActionResult OwnerPage(string[] ownerSegments, string year, string? slug, string? child)
        {
            var uc = Umbraco();
            if (uc == null) return NotFoundPage();
            var owner = PageAddressUrls.OwnerOf(Route(uc, ownerSegments));
            if (owner != null) return Serve(owner.Value, int.Parse(year), slug, child);

            // Klubben eller kretsen har bytt adress: följ Umbracos omdirigering av ägarsidan.
            var moved = MovedOwnerPrefix(uc, ownerSegments);
            if (moved == null) return NotFoundPage();
            var rest = string.Join('/', new[] { year, slug, child }.Where(s => !string.IsNullOrEmpty(s)));
            return Permanent(moved + rest + "/");
        }

        private IActionResult Serve(PageAddressOwner owner, int year, string? slug, string? child)
        {
            if (string.IsNullOrEmpty(slug)) return ToList(); // årssidan
            var uc = Umbraco();
            if (uc == null) return NotFoundPage();

            var row = _addresses.Find(owner, year, slug);
            if (row == null) return NotFoundPage();
            if (!row.IsCurrent)
            {
                var current = CurrentUrl(row);
                return current == null ? NotFoundPage() : Permanent(current + (child == null ? "" : child + "/"));
            }

            switch (row.TargetKind)
            {
                case PageAddressKinds.Event:
                    return child == null ? ServeEvent(uc, row.TargetId) : NotFoundPage();
                case PageAddressKinds.Series:
                {
                    var node = uc.Content!.GetById(row.TargetId);
                    return child == null && node?.ContentType.Alias == "competitionSeries" // competitionstore-ok: sidan renderas med noden tills tävlingarna är rader
                        ? View("CompetitionSeries", node) : NotFoundPage();
                }
                case PageAddressKinds.Competition:
                    return ServeCompetition(uc, row.TargetId, child);
            }
            return NotFoundPage();
        }

        private IActionResult ServeCompetition(IUmbracoContext uc, int id, string? child)
        {
            var competition = uc.Content!.GetById(id);
            if (competition?.ContentType.Alias != "competition") return NotFoundPage(); // competitionstore-ok: sidan renderas med noden tills tävlingarna är rader
            if (child == null) return View("Competition", competition);

            var node = competition.Children.FirstOrDefault(c => string.Equals(c.UrlSegment, child, StringComparison.OrdinalIgnoreCase));
            if (node != null) return ViewForNode(node);

            // Resultatlistan och startlistorna ur lagringen, utan egen nod (skalplanen C1/C2).
            if (string.Equals(child, "resultat", StringComparison.OrdinalIgnoreCase))
            {
                var results = HttpContext.RequestServices.GetRequiredService<ICompetitionResultStore>();
                return results.GetMany(new[] { id }).Count == 0 ? NotFoundPage() : View("CompetitionResult", competition);
            }
            var list = HttpContext.RequestServices.GetRequiredService<IStartListStore>().ListPublished(id)
                .FirstOrDefault(r => string.Equals(r.UrlSegment, child, StringComparison.OrdinalIgnoreCase));
            if (list == null) return NotFoundPage();
            _page.StartList = list;
            return View(list.Kind == HpskSite.Models.StartLists.StartListKind.Finals ? "PrecisionFinalsStartList" : "PrecisionStartList", competition); // startliststore-ok: mallnamn, inte doctyp
        }

        private IActionResult ServeEvent(IUmbracoContext uc, int eventId)
        {
            if (!ClubEventStoreSettings.UsesTable(_settings)) return NotFoundPage();
            var ev = HttpContext.RequestServices.GetRequiredService<SqlClubEventStore>().Get(eventId);
            if (ev == null || !ev.IsPublished) return NotFoundPage();
            var owner = uc.Content!.GetById(ev.OwnerId);
            if (owner == null) return NotFoundPage();
            _page.Event = ev;
            return View("ClubSimpleEvent", owner);
        }

        /// <summary>En barnnod (resultatnod, startlista, finalstartlista, anmälningsmappen …) med sin egen mall.</summary>
        private IActionResult ViewForNode(IPublishedContent node)
        {
            var templateId = node.TemplateId ?? 0;
            if (templateId <= 0) return NotFoundPage();
            var template = _templates.GetAsync(templateId).GetAwaiter().GetResult();
            return template == null ? NotFoundPage() : View(template.Alias, node);
        }

        // ── Hjälpare ──────────────────────────────────────────────────────────────────────

        private string? CurrentUrl(PageAddressRow row)
        {
            if (row.TargetKind != PageAddressKinds.Event) return _urls.UrlFor(row.TargetKind, row.TargetId);
            if (!ClubEventStoreSettings.UsesTable(_settings)) return null;
            var ev = HttpContext.RequestServices.GetRequiredService<SqlClubEventStore>().Get(row.TargetId);
            return ev is { IsPublished: true } && !string.IsNullOrEmpty(ev.Url) ? ev.Url : null;
        }

        private string? MovedOwnerPrefix(IUmbracoContext uc, string[] ownerSegs)
        {
            var old = "/" + string.Join('/', ownerSegs);
            var redirect = _redirects.GetMostRecentRedirectUrl(old) ?? _redirects.GetMostRecentRedirectUrl(old + "/");
            if (redirect == null) return null;
            var owner = PageAddressUrls.OwnerOf(uc.Content!.GetById(redirect.ContentKey));
            return owner == null ? null : _urls.OwnerPrefix(owner.Value);
        }

        private IUmbracoContext? Umbraco() => _ctx.TryGetUmbracoContext(out var uc) && uc.Content != null ? uc : null;

        private IPublishedContent? HubNode()
            => Umbraco()?.Content!.GetAtRoot().FirstOrDefault()?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");

        private static IPublishedContent? Route(IUmbracoContext uc, string[] segments)
        {
#pragma warning disable CS0618 // GetByRoute är den publicerade cachens uppslag på adress.
            return uc.Content!.GetByRoute("/" + string.Join('/', segments) + "/");
#pragma warning restore CS0618
        }

        private IActionResult ToList() => Permanent(PageAddressUrls.CompetitionsListPath);

        private IActionResult Permanent(string url) => RedirectPermanent(url + Request.QueryString);

        /// <summary>Sajtens sida för en adress som inte finns — med statuskoden 404.</summary>
        private IActionResult NotFoundPage()
        {
            var root = Umbraco()?.Content!.GetAtRoot().FirstOrDefault();
            if (root == null) return NotFound();
            var view = View("PageNotFound", root);
            view.StatusCode = 404;
            return view;
        }
    }
}
