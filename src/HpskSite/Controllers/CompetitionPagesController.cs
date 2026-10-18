using HpskSite.Models;
using HpskSite.Services.Addresses;
using HpskSite.Services.Competitions;
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
    /// innehållsupplösare letar fram dem. Adressformerna (Stefans beslut 2026-10-18, sorten i adressen):
    /// <list type="bullet">
    /// <item><c>/tavlingar/</c> — listan. <c>/tavlingar/{år}/</c> → 301 till listan.</item>
    /// <item>klubbens: <c>/{krets}/{klubb}/tavlingar/{år}/{slug}/[barn/]</c>, <c>…/handelser/{år}/{slug}/</c>,
    /// <c>…/serier/{år}/{slug}/</c> (klubbens adress är den korta, se <c>ClubShortUrls</c>)</item>
    /// <item>kretsens: <c>/{krets}/tavlingar|handelser|serier/{år}/{slug}/[barn/]</c></item>
    /// <item>förbundets mästerskap: <c>/sm|ssm|vsm|osm|nsm/{år}/{slug}/[barn/]</c>, utan sortsegment</item>
    /// <item>de gamla formerna <c>/{krets}/{år}/{slug}/</c> och <c>/{krets}/klubbar/{klubb}/{år}/{slug}/</c>
    /// → 301 till den nya. Sortsegmentet utan år (<c>/halland/tavlingar/</c>) och <c>/sm/</c> → 301.</item>
    /// </list>
    ///
    /// <para><b>Noderna är fortfarande modellen</b> så länge tävlingarna är noder — vyerna renderas som förut,
    /// med noden. Det som inte är noden själv (en startlista ur lagringen, en händelse ur tabellen) ligger i
    /// <see cref="CompetitionPageContext"/>.</para>
    ///
    /// <para><b>⚠️ En matchad route är vår:</b> Umbracos egen upplösning körs inte. Finns adressen inte svarar
    /// vi med 404-sidan. Därför är krets-segmentet spärrat mot navets eget segment (<c>competitions</c>) och
    /// mot <c>tavlingar</c> — de gamla adresserna går fortfarande genom Umbracos kedja
    /// (<c>CompetitionTreeRedirectFinder</c> m.fl.) — och klubbsegmentet mot katalogen, sortsegmenten och årtal,
    /// så att ingen adress kan matcha två routes.</para>
    /// </summary>
    public class CompetitionPagesController : Controller
    {
        private const string Year = "{year:regex(^\\d{{4}}$)}";
        private const string National = "{owner:regex(^(sm|ssm|vsm|osm|nsm)$)}";
        // ⚠️ Inga hakparenteser: rutmallen läser [..] som en ersättningstoken. Mästerskapens namn är uteslutna,
        // annars matchar /sm/2026/… både den här och National — ASP.NET svarar då 500 (AmbiguousMatchException).
        private const string Krets = "{krets:regex(^(?!(competitions|tavlingar|sm|ssm|vsm|osm|nsm)$).+$)}";
        private const string Klubb = "{klubb:regex(^(?!(klubbar|tavlingar|handelser|serier|\\d{{4}})$).+$)}";
        private const string Kind = "{kind:regex(^(tavlingar|handelser|serier)$)}";

        private readonly IUmbracoContextAccessor _ctx;
        private readonly PageAddressService _addresses;
        private readonly PageAddressUrls _urls;
        private readonly IRedirectUrlService _redirects;
        private readonly SiteSettingService _settings;
        private readonly CompetitionPageContext _page;
        private readonly ITemplateService _templates;
        private readonly HpskSite.Routing.SiteRouteLookup _routes;

        public CompetitionPagesController(IUmbracoContextAccessor ctx, PageAddressService addresses, PageAddressUrls urls,
            IRedirectUrlService redirects, SiteSettingService settings, CompetitionPageContext page, ITemplateService templates,
            HpskSite.Routing.SiteRouteLookup routes)
        {
            _ctx = ctx;
            _addresses = addresses;
            _urls = urls;
            _redirects = redirects;
            _settings = settings;
            _page = page;
            _templates = templates;
            _routes = routes;
        }

        // ── Listan ────────────────────────────────────────────────────────────────────────

        [HttpGet("tavlingar")]
        public IActionResult Hub()
        {
            var hub = HubNode();
            return hub == null ? NotFoundPage() : View("CompetitionsHub", hub);
        }

        /// <summary>Säsongssidan finns inte längre (Stefans beslut 2026-10-18) — listan har årsväljaren.</summary>
        [HttpGet("tavlingar/" + Year)]
        public IActionResult Season(string year) => ToList();

        /// <summary>Reservadressen för en tävling utan värd (<see cref="PageAddressOwner.SiteWide"/>).</summary>
        [HttpGet("tavlingar/" + Year + "/{slug}/{child?}")]
        public IActionResult SitePage(string year, string slug, string? child)
            => Serve(PageAddressOwner.SiteWide, null, int.Parse(year), slug, child);

        // ── Förbundets mästerskap ─────────────────────────────────────────────────────────

        [HttpGet(National)]
        public IActionResult NationalRoot(string owner) => ToList();

        [HttpGet(National + "/" + Year + "/{slug?}/{child?}")]
        public IActionResult NationalPage(string owner, string year, string? slug, string? child)
            => Serve(PageAddressOwner.ForNational(owner), null, int.Parse(year), slug, child);

        // ── Kretsens och klubbens ─────────────────────────────────────────────────────────

        [HttpGet(Krets + "/" + Kind)]
        public IActionResult RegionKindRoot(string krets, string kind) => KindRoot(new[] { krets }, kind);

        [HttpGet(Krets + "/" + Kind + "/" + Year + "/{slug?}/{child?}")]
        public IActionResult RegionPage(string krets, string kind, string year, string? slug, string? child)
            => OwnerPage(new[] { krets }, PageAddressRules.KindOfSegment(kind), year, slug, child);

        [HttpGet(Krets + "/" + Klubb + "/" + Kind)]
        public IActionResult ClubKindRoot(string krets, string klubb, string kind) => KindRoot(new[] { krets, klubb }, kind);

        [HttpGet(Krets + "/" + Klubb + "/" + Kind + "/" + Year + "/{slug?}/{child?}")]
        public IActionResult ClubPage(string krets, string klubb, string kind, string year, string? slug, string? child)
            => OwnerPage(new[] { krets, klubb }, PageAddressRules.KindOfSegment(kind), year, slug, child);

        // ── De gamla formerna, utan sort (före 2026-10-18) → 301 ─────────────────────────

        [HttpGet(Krets + "/" + Year + "/{slug?}/{child?}")]
        public IActionResult LegacyRegionPage(string krets, string year, string? slug, string? child)
            => OwnerPage(new[] { krets }, null, year, slug, child);

        [HttpGet(Krets + "/klubbar/{klubb}/" + Year + "/{slug?}/{child?}")]
        public IActionResult LegacyClubPage(string krets, string klubb, string year, string? slug, string? child)
            => OwnerPage(new[] { krets, "klubbar", klubb }, null, year, slug, child);

        /// <summary>Sortsegmentet utan år: tävlingarna → listan, händelserna och serierna → ägarens sida.</summary>
        private IActionResult KindRoot(string[] ownerSegments, string kindSegment)
        {
            if (PageAddressRules.KindOfSegment(kindSegment) == PageAddressKinds.Competition) return ToList();
            var uc = Umbraco();
            var owner = uc == null ? null : _routes.Owner(uc.Content!, ownerSegments);
            if (owner == null) return NotFoundPage();
            var url = owner.Url();
            return string.IsNullOrEmpty(url) || url == "#" ? NotFoundPage() : Permanent(url);
        }

        /// <param name="kind">Sorten ur adressen, eller null för en adress utan sortsegment (de gamla formerna).</param>
        private IActionResult OwnerPage(string[] ownerSegments, string? kind, string year, string? slug, string? child)
        {
            var uc = Umbraco();
            if (uc == null) return NotFoundPage();
            var owner = PageAddressUrls.OwnerOf(_routes.Owner(uc.Content!, ownerSegments));
            if (owner != null) return Serve(owner.Value, kind, int.Parse(year), slug, child);

            // Klubben eller kretsen har bytt adress: följ Umbracos omdirigering av ägarsidan.
            var moved = MovedOwnerPrefix(uc, ownerSegments);
            if (moved == null) return NotFoundPage();
            var kindPart = kind == null ? "" : PageAddressRules.KindSegment(kind) + "/";
            var rest = string.Join('/', new[] { year, slug, child }.Where(s => !string.IsNullOrEmpty(s)));
            return Permanent(moved + kindPart + rest + "/");
        }

        /// <param name="kind">
        /// Sorten ur adressen. Null = adressen saknar sortsegment: förbundets mästerskap (sorterna delar namnrymd
        /// där) och de gamla formerna, som alltid ger 301 till den aktuella adressen.
        /// </param>
        private IActionResult Serve(PageAddressOwner owner, string? kind, int year, string? slug, string? child)
        {
            if (string.IsNullOrEmpty(slug)) return ToList(); // årssidan
            var uc = Umbraco();
            if (uc == null) return NotFoundPage();

            var row = kind != null ? _addresses.Find(owner, kind, year, slug) : _addresses.FindAnyKind(owner, year, slug);
            if (row == null) return NotFoundPage();
            // En gammal adress — eller den gamla formen utan sort — ger 301 till sidans aktuella adress.
            if (!row.IsCurrent || (kind == null && !owner.OmitsKindSegment))
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
                    if (CompetitionStoreSettings.UsesTable(_settings))
                    {
                        // Tabelläget (C3-planen §5c): seriens sida med den virtuella noden, ur lagringen.
                        var s = HttpContext.RequestServices.GetRequiredService<ICompetitionSeriesStore>().Get(row.TargetId);
                        return child == null && s != null
                            ? View("CompetitionSeries", HttpContext.RequestServices.GetRequiredService<VirtualCompetitionNodes>().Series(s, HubNode()))
                            : NotFoundPage();
                    }
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
            IPublishedContent? competition;
            if (CompetitionStoreSettings.UsesTable(_settings))
            {
                // Tabelläget (C3-planen §5c): sidan har en virtuell nod byggd ur raden — ingen nod behövs, och en
                // gammal nod som ligger kvar till rensningen visar aldrig ett inaktuellt läge.
                var info = HttpContext.RequestServices.GetRequiredService<ICompetitionStore>().Get(id);
                competition = info == null ? null : HttpContext.RequestServices.GetRequiredService<VirtualCompetitionNodes>().Competition(info, HubNode());
                if (competition == null) return NotFoundPage();
                if (child == null) return View("Competition", competition);
            }
            else
            {
                competition = uc.Content!.GetById(id);
                if (competition?.ContentType.Alias != "competition") return NotFoundPage(); // competitionstore-ok: nodläget renderar med noden
                if (child == null) return View("Competition", competition);

                var node = competition.Children.FirstOrDefault(c => string.Equals(c.UrlSegment, child, StringComparison.OrdinalIgnoreCase));
                if (node != null) return ViewForNode(node);
            }

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
            // Umbraco lagrar omdirigeringen på trädets adress, alltså klubbens långa form.
            var forms = new List<string> { "/" + string.Join('/', ownerSegs) };
            if (ownerSegs.Length == 2) forms.Add($"/{ownerSegs[0]}/klubbar/{ownerSegs[1]}");
            var redirect = forms.Select(f => _redirects.GetMostRecentRedirectUrl(f) ?? _redirects.GetMostRecentRedirectUrl(f + "/"))
                .FirstOrDefault(r => r != null);
            if (redirect == null) return null;
            var owner = PageAddressUrls.OwnerOf(uc.Content!.GetById(redirect.ContentKey));
            return owner == null ? null : _urls.OwnerPrefix(owner.Value);
        }

        private IUmbracoContext? Umbraco() => _ctx.TryGetUmbracoContext(out var uc) && uc.Content != null ? uc : null;

        private IPublishedContent? HubNode()
            => Umbraco()?.Content!.GetAtRoot().FirstOrDefault()?.Children.FirstOrDefault(c => c.ContentType.Alias == "competitionsHub");

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
