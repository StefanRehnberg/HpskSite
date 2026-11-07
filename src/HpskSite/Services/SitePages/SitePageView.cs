using Microsoft.AspNetCore.Http;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Finderns besked till vyn: den här begäran gäller sidan <see cref="PageKey"/> hos ägaren (en klubb eller krets).
    /// Ligger i <c>HttpContext.Items</c> under <see cref="ItemKey"/>. Saknas den är begäran ägarens rotadress (Hem).
    /// </summary>
    public sealed record SitePageRequest(string OwnerType, int OwnerId, string PageKey, int? PageId = null)
    {
        public const string ItemKey = "Hpsk.SitePageRequest";

        /// <summary>Nyckeln för en fri sida i <see cref="PageKey"/> (sidan har ingen mallnyckel; <see cref="PageId"/> pekar ut den).</summary>
        public const string FreePageKey = "page";

        /// <summary>Nyckeln för redigeringsytan Hemsidan (<c>/{ägare}/hemsidan/</c>).</summary>
        public const string HemsidanKey = "hemsidan";

        public bool IsHemsidan => PageKey == HemsidanKey;

        /// <summary>En av ägarens egna (fria) sidor, inte en systemsida.</summary>
        public bool IsFreePage => PageId != null && PageKey == FreePageKey;

        public static SitePageRequest? Current(HttpContext? http) =>
            http != null && http.Items.TryGetValue(ItemKey, out var v) ? v as SitePageRequest : null;
    }

    /// <summary>En post i den nya menyn.</summary>
    /// <param name="Page">Mallsidan.</param>
    /// <param name="PaneId">Flikpanelens id i dagens skal (<c>clubRanges</c> …); flik-id:t är <c>{PaneId}-tab</c>.</param>
    /// <param name="Href">Adressen. Admin pekar på rotadressens <c>#{PaneId}</c>.</param>
    /// <param name="Current">Den här sidan.</param>
    /// <param name="IsTabButton">En Bootstrap-flik på rotadressen (Hem och Admin växlar där utan omladdning); annars en länk.</param>
    public sealed record SiteMenuEntry(SitePageDef Page, string PaneId, string Href, bool Current, bool IsTabButton);

    /// <summary>
    /// Allt vyn behöver veta om den nya sidstrukturen för EN klubb eller krets (klubbsidorna plan v4, 2.6).
    ///
    /// <para><b>Av = dagens vy, oförändrad:</b> <see cref="ShowsPane"/> är alltid sant och <see cref="PaneClass"/> ger
    /// dagens klasser, så en vy som anropar den här klassen beter sig exakt som förut.</para>
    ///
    /// <para><b>På:</b> rotadressen visar Hem och (för admin) Admin i samma dokument, med dagens flik-id:n och
    /// hashtolkare; varje annan sida är en egen adress som bara visar sin egen panel. Innehållet i panelerna
    /// bestäms fortfarande av dagens vykod (en roll ser samma sak som förut), så "ny synlighet ≤ gammal" gäller per
    /// konstruktion; mallregistret styr bara vilka sidor som finns, menyn och om en panel får visas för tittaren.</para>
    /// </summary>
    public sealed class SitePageView
    {
        private readonly SiteTemplate? _template;
        private readonly SitePageDef? _page;

        private SitePageView(bool enabled, SiteTemplate? template, SitePageDef? page, string ownerUrl, SiteViewer viewer)
        {
            Enabled = enabled;
            _template = template;
            _page = page;
            OwnerUrl = ownerUrl;
            Viewer = viewer;
        }

        public bool Enabled { get; }
        public string OwnerUrl { get; }
        public SiteViewer Viewer { get; }
        public string PageKey => _page?.Key ?? "home";
        public bool IsRoot => !Enabled || _page == null || _page.IsHome;

        /// <summary>Dagens vy: inget ändras.</summary>
        public static SitePageView Off(string ownerUrl = "/") => new(false, null, null, Normalize(ownerUrl), SiteViewer.Anonymous);

        public static SitePageView On(string ownerType, string ownerUrl, string? pageKey, SiteViewer viewer)
        {
            var template = SiteTemplates.For(ownerType);
            // En okänd nyckel (t.ex. en föråldrad Items-post) är Hem, aldrig en sida som inte finns.
            var page = (pageKey != null ? template.PageByKey(pageKey) : null) ?? template.Pages.First(p => p.IsHome);
            if (page.IsAdmin) page = template.Pages.First(p => p.IsHome); // Admin har ingen egen adress
            return new SitePageView(true, template, page, Normalize(ownerUrl), viewer);
        }

        /// <summary>Ska panelen <paramref name="paneId"/> skrivas ut? Av: alltid. På: bara den här sidans panel (och Admin på roten för ägarens admin).</summary>
        public bool ShowsPane(string paneId)
        {
            if (!Enabled) return true;
            var admin = _template!.Pages.First(p => p.IsAdmin);
            var adminPane = SiteTemplates.LegacyPaneId(_template.OwnerType, admin.Key);
            if (paneId == adminPane) return IsRoot && _template.PageVisible(admin, Viewer);
            return paneId == SiteTemplates.LegacyPaneId(_template.OwnerType, _page!.Key) && _template.PageVisible(_page, Viewer);
        }

        /// <summary>Klasserna efter <c>tab-pane fade</c>: dagens (Hem aktiv) när av, annars den visade panelen aktiv. Admin är aldrig aktiv från start.</summary>
        public string PaneClass(string paneId)
        {
            if (!Enabled) return paneId.EndsWith("Home", StringComparison.Ordinal) ? "show active" : "";
            var admin = _template!.Pages.First(p => p.IsAdmin);
            if (paneId == SiteTemplates.LegacyPaneId(_template.OwnerType, admin.Key)) return "";
            return paneId == SiteTemplates.LegacyPaneId(_template.OwnerType, _page!.Key) ? "show active" : "";
        }

        /// <summary>Menyn för tittaren. Tom när av (då ritar dagens flikrad).</summary>
        public IReadOnlyList<SiteMenuEntry> Menu()
        {
            if (!Enabled) return Array.Empty<SiteMenuEntry>();
            var result = new List<SiteMenuEntry>();
            foreach (var p in _template!.MenuFor(Viewer))
            {
                var pane = SiteTemplates.LegacyPaneId(_template.OwnerType, p.Key);
                var href = p.IsAdmin ? OwnerUrl + "#" + pane
                         : p.IsHome ? OwnerUrl
                         : OwnerUrl + p.Slug + "/";
                var tabButton = IsRoot && (p.IsHome || p.IsAdmin);
                result.Add(new SiteMenuEntry(p, pane, href, Current: p.Key == _page!.Key && !p.IsAdmin, tabButton));
            }
            return result;
        }

        private static string Normalize(string url)
        {
            if (string.IsNullOrEmpty(url) || url == "#") return "/";
            return url.EndsWith('/') ? url : url + "/";
        }
    }

    /// <summary>Bygger <see cref="SitePageView"/> för en vy. Scoped: läser den aktuella begäran.</summary>
    public sealed class SitePageViewFactory
    {
        private readonly SiteOwnerService _owners;
        private readonly IHttpContextAccessor _http;

        public SitePageViewFactory(SiteOwnerService owners, IHttpContextAccessor http)
        {
            _owners = owners;
            _http = http;
        }

        public SitePageView For(string ownerType, int ownerId, string ownerUrl, SiteViewer viewer)
        {
            // Av för ägaren (eller fel): dagens vy. SiteOwnerService kastar aldrig och svarar AV vid fel.
            if (!_owners.IsEnabled(ownerType, ownerId)) return SitePageView.Off(ownerUrl);
            var req = SitePageRequest.Current(_http.HttpContext);
            var key = req != null && req.OwnerType == ownerType && req.OwnerId == ownerId ? req.PageKey : null;
            return SitePageView.On(ownerType, ownerUrl, key, viewer);
        }
    }
}
