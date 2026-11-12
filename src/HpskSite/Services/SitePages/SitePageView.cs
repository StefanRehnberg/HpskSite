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

        /// <summary>Nyckeln i <c>HttpContext.Items</c> för "den här sidan är låst för tittaren" (värdet är om den kräver medlemskap). Sätts av findern och filtret, läses av vyerna.</summary>
        public const string LockedItemKey = "Hpsk.SitePageLocked";

        /// <summary>Märk begäran som låst. <paramref name="needsMembership"/>: sidan kräver medlemskap (annars räcker inloggning).</summary>
        public static void MarkLocked(HttpContext http, bool needsMembership) => http.Items[LockedItemKey] = needsMembership;

        /// <summary>Är den här begäran en låst sida för tittaren? Null = nej; annars om sidan kräver medlemskap.</summary>
        public static bool? LockedNeedsMembership(HttpContext? http) =>
            http != null && http.Items.TryGetValue(LockedItemKey, out var v) && v is bool b ? b : null;
    }

    /// <summary>En egen sida i menyn: sidan, etiketten (publicerad titel), adressen, synligheten och ordningen.</summary>
    /// <param name="ParentPageId">Sidan den ligger under i menyn (null = egen nivå).</param>
    public sealed record SiteMenuPage(int PageId, string Label, string Slug, SiteVisibility Visibility, int Sort, int? ParentPageId = null, string? ParentTemplateKey = null);

    /// <summary>En post i den nya menyn.</summary>
    /// <param name="Page">Mallsidan.</param>
    /// <param name="PaneId">Flikpanelens id i dagens skal (<c>clubRanges</c> …); flik-id:t är <c>{PaneId}-tab</c>.</param>
    /// <param name="Href">Adressen. Admin pekar på rotadressens <c>#{PaneId}</c>.</param>
    /// <param name="Current">Den här sidan.</param>
    /// <param name="IsTabButton">En Bootstrap-flik på rotadressen (Hem och Admin växlar där utan omladdning); annars en länk.</param>
    /// <param name="Locked">Sidan finns men tittaren får inte se den (en vanlig medlem skulle): raden visas med hänglås och leder till en ruta, aldrig till sidans innehåll.</param>
    /// <param name="FreePageId">Id:t på en egen sida (null för en systemsida).</param>
    /// <param name="ParentPageId">Posten är en undersida till den egna sidan med det id:t; den följer direkt efter sin förälder i listan.</param>
    public sealed record SiteMenuEntry(SitePageDef Page, string PaneId, string Href, bool Current, bool IsTabButton, bool Locked = false, int? FreePageId = null, int? ParentPageId = null, string? ParentKey = null);

    /// <summary>Vad vänstermenyn (<c>_SiteSideMenu</c>) ritas ur: posterna, ägarens adress, om Hemsidan-knappen och Guide ska visas och en eventuell extern länk längst ned.</summary>
    /// <param name="ListId">Menylistans id (<c>clubPageTabs</c>/<c>regionPageTabs</c>): dagens skript och djuplänkar slår upp flikarna i den.</param>
    /// <param name="Tutorial">Guidefilmens id för administratören (tom = ingen Guide-knapp).</param>
    /// <param name="ExternalLabel">Etiketten på en extern länk längst ned i menyn ("Kretsens hemsida"), eller null.</param>
    public sealed record SiteSideMenuModel(string ListId, string OwnerType, IReadOnlyList<SiteMenuEntry> Items, string OwnerUrl, bool CanEditSite, bool IsAdmin,
        string Tutorial, string? ExternalLabel = null, string? ExternalUrl = null, IReadOnlyList<SiteExternalLink>? Links = null);

    /// <summary>Ikonen för en menypost (Bootstrap Icons): samma som flikraden hade.</summary>
    public static class SiteMenuIcons
    {
        public static string For(string ownerType, string pageKey) => pageKey switch
        {
            "home" => "bi-house-fill",
            "about" => "bi-info-circle",
            "ranges" => "bi-geo-alt",
            "calendar" => ownerType == SiteTemplates.Region ? "bi-calendar3" : "bi-calendar-event",
            "competitions" => "bi-trophy",
            "members" => "bi-people-fill",
            "records" => "bi-award",
            "documents" => "bi-archive",
            "board" => "bi-people",
            "admin" => ownerType == SiteTemplates.Region ? "bi-shield-check" : "bi-gear-fill",
            var k when k.StartsWith("page:", StringComparison.Ordinal) => "bi-file-earmark-text",
            _ => "bi-circle",
        };
    }

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
        private readonly IReadOnlyList<SiteMenuPage> _extra;
        private readonly IReadOnlyList<SiteExternalLink> _links;
        private readonly int? _freePageId;

        private SitePageView(bool enabled, SiteTemplate? template, SitePageDef? page, string ownerUrl, SiteViewer viewer,
            IReadOnlyList<SiteMenuPage>? extra = null, int? freePageId = null, IReadOnlyList<SiteExternalLink>? links = null)
        {
            _links = links ?? Array.Empty<SiteExternalLink>();
            Enabled = enabled;
            _template = template;
            _page = page;
            OwnerUrl = ownerUrl;
            Viewer = viewer;
            _extra = extra ?? Array.Empty<SiteMenuPage>();
            _freePageId = freePageId;
        }

        public bool Enabled { get; }
        public string OwnerUrl { get; }
        public SiteViewer Viewer { get; }
        public string PageKey => _page?.Key ?? "home";
        public bool IsRoot => !Enabled || (_freePageId == null && (_page == null || _page.IsHome));

        /// <summary>
        /// Dagens vy: inget ändras. ⚠️ Tittaren måste vara den VERKLIGA: vyerna ritar panelerna med <see cref="Viewer"/> (SitePanelContext), och en anonym tittare
        /// här gav varje inloggad besökare (även klubbens admin) Hem-sidans utloggade lista med inloggningsruta så länge nya strukturen var AV för ägaren.
        /// </summary>
        public static SitePageView Off(string ownerUrl = "/", SiteViewer? viewer = null) => new(false, null, null, Normalize(ownerUrl), viewer ?? SiteViewer.Anonymous);

        /// <param name="extra">Ägarens egna sidor som ligger i menyn (steg 2); tom när ägaren inte har egen redigering PÅ.</param>
        /// <param name="freePageId">Begäran gäller den här egna sidan (inte en systemsida): ingen systempost är då aktuell.</param>
        public static SitePageView On(string ownerType, string ownerUrl, string? pageKey, SiteViewer viewer,
            IReadOnlyList<SiteMenuPage>? extra = null, int? freePageId = null, SiteTemplate? template = null, IReadOnlyList<SiteExternalLink>? links = null)
        {
            // template: mallen med ägarens omvandlade sidor utbytta (SiteConvertedPages.Apply); utan den är det den vanliga mallen.
            template ??= SiteTemplates.For(ownerType);
            // En okänd nyckel (t.ex. en föråldrad Items-post) är Hem, aldrig en sida som inte finns.
            var page = (pageKey != null ? template.PageByKey(pageKey) : null) ?? template.Pages.First(p => p.IsHome);
            if (page.IsAdmin) page = template.Pages.First(p => p.IsHome); // Admin har ingen egen adress
            return new SitePageView(true, template, page, Normalize(ownerUrl), viewer, extra, freePageId, links);
        }

        /// <summary>
        /// Ägarens externa länkar längst ned i menyn, i ordning, bara de tittaren får se. Samma regel som för egna sidor: varje länks nivå prövas för tittaren vid varje
        /// rendering. En utloggad ser därför bara länkar ägaren själv gett nivån Alla, aldrig någon annan text (en länk på en lägre nivå är osynlig för hen).
        /// </summary>
        public IReadOnlyList<SiteExternalLink> Links()
        {
            if (!Enabled || _links.Count == 0) return Array.Empty<SiteExternalLink>();
            return _links.Where(l => SiteAccess.CanSee(l.Visibility, Viewer)).ToList();
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

        /// <summary>
        /// Menyn för tittaren. Tom när av (då ritar dagens flikrad). Mallens poster i mallens ordning, därefter ägarens egna sidor i menyns
        /// ordning, Admin sist. En egen sida syns bara för den som får se den (sidans synlighet): en utloggad ser bara sidor
        /// ägaren gett nivån Alla, och ingen etikett på lägre nivå läcker till anonyma (plan v3 B3). Ägaren väljer alltså själv vilka sidor som är publika i menyn.
        /// </summary>
        public IReadOnlyList<SiteMenuEntry> Menu()
        {
            if (!Enabled) return Array.Empty<SiteMenuEntry>();
            var result = new List<SiteMenuEntry>();
            SiteMenuEntry? admin = null;
            // Besökarens vy: sidor tittaren får se, och (med hänglås) sidor en vanlig medlem skulle få se. Styrelse- och Dold-sidor syns aldrig för den som inte har rätt.
            foreach (var p in _template!.Pages.OrderBy(x => x.Order))
            {
                var visible = _template.PageVisible(p, Viewer);
                var locked = !visible && SitePageAccessRules.IsLockedFor(_template, p, Viewer);
                if (!visible && !locked) continue;
                var pane = SiteTemplates.LegacyPaneId(_template.OwnerType, p.Key);
                var href = p.IsAdmin ? OwnerUrl + "#" + pane
                         : p.IsHome ? OwnerUrl
                         : OwnerUrl + p.Slug + "/";
                // Hem och Admin är flikar på rotadressen. En utloggad hos en KLUBB har ingen flikpanel att växla till (startsidan ritas utan flikar): Hem är då en vanlig länk.
                var tabButton = IsRoot && (Viewer.LoggedIn || _template.OwnerType == SiteTemplates.Region) && !locked && (p.IsHome || p.IsAdmin);
                var entry = new SiteMenuEntry(p, pane, href, Current: _freePageId == null && p.Key == _page!.Key && !p.IsAdmin, tabButton, locked);
                if (p.IsAdmin) { admin = entry; continue; }
                result.Add(entry);
                // Undersidor till en omgjord systemsida (Om): direkt efter systemposten, i sin ordning. Aldrig under en låst post, och bara de tittaren får se.
                if (locked || p.Key != SitePageTree.ParentableTemplate) continue;
                foreach (var child in _extra.Where(x => x.ParentTemplateKey == p.Key).OrderBy(x => x.Sort).ThenBy(x => x.PageId))
                    if (EntryFor(child) is { } childEntry) result.Add(childEntry with { ParentKey = p.Key });
            }
            // Ägarens egna sidor som träd: sidor på egen nivå i sin ordning, var och en följd av sina undersidor. En undersida visas bara när dess förälder visas, och aldrig under en
            // LÅST förälder (en låst sida visar bara sitt eget namn med ett hänglås, inga etiketter under den). En undersida vars förälder inte finns i menyn (inte publicerad, inte i menyn) visas inte.
            SiteMenuEntry? EntryFor(SiteMenuPage e)
            {
                var visible = SiteAccess.CanSee(e.Visibility, Viewer);
                // En egen sidas etikett visas för en utloggad bara när ägaren gett sidan nivån Alla (plan v3 B3 gäller fortfarande allt annat: ingen fri text på lägre nivå läcker till anonyma); en inloggad som inte är medlem ser en låst medlemssida.
                var locked = !visible && Viewer.LoggedIn && e.Visibility == SiteVisibility.Klubbmedlemmar;
                if (!visible && !locked) return null;
                // En utloggad ser bara en egen sida ägaren gett nivån Alla (visible är då sant); en sida på lägre nivå är osynlig och låst bara för en inloggad.
                var def = new SitePageDef("page:" + e.PageId, e.Slug, e.Label, 1000 + e.Sort, new[] { new SiteBlockDef("page", e.Visibility) });
                return new SiteMenuEntry(def, "sitePage" + e.PageId, OwnerUrl + e.Slug + "/", Current: _freePageId == e.PageId, IsTabButton: false, Locked: locked, FreePageId: e.PageId, ParentPageId: e.ParentPageId);
            }
            foreach (var top in _extra.Where(x => x.ParentPageId == null).OrderBy(x => x.Sort).ThenBy(x => x.PageId))
            {
                var parentEntry = EntryFor(top);
                if (parentEntry == null) continue;
                result.Add(parentEntry);
                if (parentEntry.Locked) continue;
                foreach (var child in _extra.Where(x => x.ParentPageId == top.PageId).OrderBy(x => x.Sort).ThenBy(x => x.PageId))
                    if (EntryFor(child) is { } childEntry) result.Add(childEntry);
            }
            if (admin != null) result.Add(admin);
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
        private readonly SitePageReader? _reader;
        private readonly SiteConvertedSource? _converted;

        public SitePageViewFactory(SiteOwnerService owners, IHttpContextAccessor http, SitePageReader? reader = null, SiteConvertedSource? converted = null)
        {
            _converted = converted;
            _owners = owners;
            _http = http;
            _reader = reader;
        }

        public SitePageView For(string ownerType, int ownerId, string ownerUrl, SiteViewer viewer)
        {
            // Av för ägaren (eller fel): dagens vy. SiteOwnerService kastar aldrig och svarar AV vid fel.
            if (!_owners.IsEnabled(ownerType, ownerId)) return SitePageView.Off(ownerUrl, viewer);
            var req = SitePageRequest.Current(_http.HttpContext);
            var key = req != null && req.OwnerType == ownerType && req.OwnerId == ownerId ? req.PageKey : null;
            int? freeId = req is { IsFreePage: true } && req.OwnerType == ownerType && req.OwnerId == ownerId ? req.PageId : null;
            return SitePageView.On(ownerType, ownerUrl, key, viewer, MenuPages(ownerType, ownerId), freeId, _converted?.Template(ownerType, ownerId), MenuLinks(ownerType, ownerId));
        }

        /// <summary>Ägarens externa länkar — bara när ägaren har egen redigering PÅ, och ett fel i läsningen ger en meny utan dem.</summary>
        private IReadOnlyList<SiteExternalLink> MenuLinks(string ownerType, int ownerId)
        {
            if (_reader == null || !_owners.IsEditingEnabled(ownerType, ownerId)) return Array.Empty<SiteExternalLink>();
            try { return _reader.MenuLinks(ownerType, ownerId); }
            catch { return Array.Empty<SiteExternalLink>(); }
        }

        /// <summary>Ägarens egna sidor i menyn — bara när ägaren har egen redigering PÅ, och ett fel i läsningen ger en meny utan dem (aldrig ett trasigt flikfält).</summary>
        private IReadOnlyList<SiteMenuPage> MenuPages(string ownerType, int ownerId)
        {
            if (_reader == null || !_owners.IsEditingEnabled(ownerType, ownerId)) return Array.Empty<SiteMenuPage>();
            try { return _reader.MenuPages(ownerType, ownerId); }
            catch { return Array.Empty<SiteMenuPage>(); }
        }
    }
}
