using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HpskSite.Extensions;

namespace HpskSite.Services.SitePages
{
    /// <summary>Ett dokument som ett dokumentblock kan visa. <see cref="Visibility"/> är dokumentets egen nivå (fyra nivåer i arkivet).</summary>
    public sealed record SiteDocumentInfo(int Id, string Title, string? Description, long FileSize, SiteVisibility Visibility);

    /// <summary>En händelse i händelseblocket.</summary>
    public sealed record SiteEventInfo(string Title, DateTime Start, string? Location, string Url, string? Type = null);

    /// <summary>En nyhet i nyhetsblocket (publicerad, synlig för tittaren). <see cref="Date"/> är nyhetens datum, annars skapad.</summary>
    public sealed record SiteNewsInfo(string Title, DateTime Date, string Url);

    /// <summary>Klubbens kontaktuppgifter (läses live, lagras aldrig i blocket).</summary>
    public sealed record SiteContactInfo(string? Person, string? Email, string? Phone);

    /// <summary>
    /// Data som blocken visar men inte lagrar. Läses live vid rendering. Alla svar gäller ENDAST ägaren renderingen gäller
    /// (en implementation tar aldrig ett ägar-id från blocket). Ett dokument som inte hör till ägaren returneras inte.
    /// </summary>
    public interface ISiteBlockData
    {
        IReadOnlyList<SiteDocumentInfo> Documents(IReadOnlyList<int> ids);
        IReadOnlyList<SiteEventInfo> UpcomingEvents(int count);

        /// <summary>Kommande händelser, valfritt bara av en typ. Standard: hämtar fler och gallrar (en implementation som kan filtrera i lagringen bör göra det).</summary>
        IReadOnlyList<SiteEventInfo> UpcomingEvents(int count, string? eventType) =>
            eventType == null ? UpcomingEvents(count) : UpcomingEvents(200).Where(e => e.Type == eventType).Take(count).ToList();
        SiteContactInfo? Contact();

        /// <summary>Bilderna bland <paramref name="ids"/> som hör till ägaren och inte är raderade. Standard: inga (en implementation utan bilder visar inga bildblock).</summary>
        IReadOnlyList<SiteImageInfo> Images(IReadOnlyList<int> ids) => Array.Empty<SiteImageInfo>();

        /// <summary>Ägarens senaste publicerade nyheter som tittaren får se (händelsens egen läsnivå prövas i lagringen). Standard: inga.</summary>
        IReadOnlyList<SiteNewsInfo> News(int count) => Array.Empty<SiteNewsInfo>();

        /// <summary>Kretsens klubbar (bara för en kretsägare). Standard: inga.</summary>
        IReadOnlyList<SiteRegionClub> RegionClubs() => Array.Empty<SiteRegionClub>();

        /// <summary>Skjutbanor kretsens klubbar använder; begränsade banor ingår aldrig. Standard: inga.</summary>
        IReadOnlyList<SiteRangeInfo> RegionRanges() => Array.Empty<SiteRangeInfo>();

        /// <summary>Kretsens egna serier. Standard: inga.</summary>
        IReadOnlyList<SiteSeriesInfo> RegionSeries(int count) => Array.Empty<SiteSeriesInfo>();

        /// <summary>Kretsens siffror, eller null utanför en krets.</summary>
        SiteRegionStats? RegionStats() => null;

        /// <summary>Föreningens presentationstext som HTML (klubbens aboutClub, kretsens aboutRegion, annars beskrivningen), eller null när den saknas. Standard: ingen.</summary>
        string? AboutHtml() => null;

        /// <summary>Ägarens kommande tävlingar som tittaren får se (klubbens egna, kretsens egna och klubbarnas icke klubbinterna). Standard: inga.</summary>
        IReadOnlyList<SiteCompetitionInfo> UpcomingCompetitions(int count) => Array.Empty<SiteCompetitionInfo>();

        /// <summary>Ägarens gällande rekord (klubbens respektive kretsens). Standard: inga.</summary>
        IReadOnlyList<SiteRecordInfo> Records() => Array.Empty<SiteRecordInfo>();

        /// <summary>Skjutbanor: klubbens egna eller, för en krets, banorna kretsens klubbar använder. Begränsade banor ingår aldrig. Standard: inga.</summary>
        IReadOnlyList<SiteRangeInfo> OwnerRanges() => Array.Empty<SiteRangeInfo>();

        /// <summary>Ägarens regerande mästare (namn på personer: bara för inloggade, se blockets personflagga). Standard: inga.</summary>
        IReadOnlyList<SiteChampionInfo> Champions() => Array.Empty<SiteChampionInfo>();

        /// <summary>Vad ett anmälningsblock behöver veta om begäran (sidans id, skyddstoken, besked efter en inskickning). Null = en förhandsvisning: formuläret visas men går inte att skicka. Standard: null.</summary>
        SiteFormContext? Form() => null;

        /// <summary>Ägarens siffror: klubbens medlemmar, banor och kommande tävlingar, eller kretsens klubbar, banor, serier och tävlingar. Standard: inga.</summary>
        IReadOnlyList<SiteStatItem> Stats() => Array.Empty<SiteStatItem>();
    }

    /// <summary>En kommande tävling i tävlingsblocket.</summary>
    public sealed record SiteCompetitionInfo(string Title, DateTime Date, string Url);

    /// <summary>Ett gällande rekord: gren, klass, resultat, innehavare och datum. <see cref="Result"/> är den färdiga strängen med enheten på ("285 / 300"): talet reser aldrig utan sin enhet.</summary>
    public sealed record SiteRecordInfo(string Discipline, string Class, bool IsTeam, string Result, string Holder, DateTime Date);

    /// <summary>En regerande mästare: senaste året per gren, typ och klass. <see cref="Result"/> är den färdiga strängen med enheten på.</summary>
    public sealed record SiteChampionInfo(int Year, string Discipline, string Class, bool IsTeam, string Result, string Holder);

    /// <summary>Ett tal i siffrorutan (medlemmar, banor, kommande tävlingar …).</summary>
    public sealed record SiteStatItem(string Label, int Value);

    /// <summary>Vad en rendering behöver veta: tittaren, ägarens adress och mall, samt live-data.</summary>
    public sealed record SiteRenderContext(SiteViewer Viewer, string OwnerUrl, SiteTemplate Template, ISiteBlockData Data);

    /// <summary>
    /// Renderar sidans block till HTML (klubbsidorna 2a). <b>Allt som skrivs ut är kodat</b> (<see cref="WebUtility.HtmlEncode"/>) utom
    /// textblockets HTML, som redan saneras vid skrivning och saneras OM här (en lagrad rad är data från en tidigare kodversion,
    /// inte en betrodd källa). Synligheten prövas vid VARJE rendering: blockets nivå får aldrig vara öppnare än sidans
    /// (<see cref="SiteAccess.Strictest"/>), ett personblock är aldrig <c>Alla</c> (<see cref="SiteAccess.CapPersonal"/>), och
    /// ett dokument visas bara om <c>Strictest(block, dokument)</c> släpper in tittaren. Ren kod: ingen databas, ingen Umbraco.
    /// </summary>
    public static class SiteBlockRenderer
    {
        private static readonly Regex TableOpen = new("<table\\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex TableClose = new("</table>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>Renderar alla block (fri sida) i ordning. Block tittaren inte får se utelämnas helt (ingen tom ruta, ingen etikett).</summary>
        public static string RenderAll(IEnumerable<SiteBlock> blocks, SiteVisibility pageVisibility, SiteRenderContext ctx)
        {
            var sb = new StringBuilder();
            foreach (var b in blocks) sb.Append(Render(b, pageVisibility, ctx));
            return sb.ToString();
        }

        /// <summary>Blockets verkliga nivå: striktast av blockets och sidans, personblock aldrig Alla. EN beräkning för alla block, även paneler.</summary>
        private static SiteVisibility Effective(SiteBlock block, SiteVisibility pageVisibility, SiteRenderContext ctx) => Effective(block, pageVisibility, ctx.Template.OwnerType);

        private static SiteVisibility Effective(SiteBlock block, SiteVisibility pageVisibility, string ownerType)
        {
            var effective = SiteAccess.Strictest(block.Visibility, pageVisibility);
            if (SiteBlockSchema.IsPersonalBlock(block.Type, ownerType, block.Data))
                effective = SiteAccess.CapPersonal(effective);
            return effective;
        }

        /// <summary>
        /// Får tittaren se blocket (vilket block som helst, även en panel)? Samma beräkning som ritningen, men utan att rita: underlaget för "Vem ser sidan?" i Hemsidan.
        /// Sidans egen åtkomst prövas separat (<see cref="SitePageAccessRules"/>).
        /// </summary>
        public static bool BlockVisible(SiteBlock block, SiteVisibility pageVisibility, string ownerType, SiteViewer viewer) =>
            SiteAccess.CanSee(Effective(block, pageVisibility, ownerType), viewer);

        /// <summary>
        /// Får tittaren se panelblocket? Vyn som ritar en omvandlad sida frågar HIT före varje panel (aldrig en egen prövning), så panelerna följer
        /// samma regler och samma läcktest som övriga block. Ett block som inte är en panel ger false.
        /// </summary>
        public static bool PanelVisible(SiteBlock block, SiteVisibility pageVisibility, SiteRenderContext ctx) =>
            block.Type == "panel" && SiteAccess.CanSee(Effective(block, pageVisibility, ctx), ctx.Viewer);

        /// <summary>Renderar ett block, eller tom sträng när tittaren inte får se det.</summary>
        public static string Render(SiteBlock block, SiteVisibility pageVisibility, SiteRenderContext ctx)
        {
            var effective = Effective(block, pageVisibility, ctx);
            if (!SiteAccess.CanSee(effective, ctx.Viewer)) return "";
            if (SiteBlockSchema.IsRetired(block.Type)) return "";   // en utgången typ ritas aldrig, men sidan som har den läses och sparas som förut

            return block.Type switch
            {
                "panel" => "",   // en panel är en delvy: vyn ritar den (efter PanelVisible), den här rena koden skriver aldrig ut den
                "text" => Text(block),
                "heading" => Heading(block),
                "links" => Links(block),
                "pagelink" => PageLink(block, ctx),
                "documents" => Documents(block, effective, ctx),
                "events" => Events(block, ctx),
                "contact" => Contact(block, ctx),
                "image" => Image(block, ctx),
                "news" => News(block, ctx),
                "about" => About(block, ctx),
                "gallery" => Gallery(block, ctx),
                "signup" => Signup(block, ctx),
                "competitions" => Competitions(block, ctx),
                "records" => Records(block, ctx),
                "ranges" => Ranges(block, ctx),
                "champions" => Champions(block, ctx),
                "stats" => Stats(block, ctx),
                "region-clubs" => RegionClubs(block, ctx),
                "region-ranges" => RegionRanges(block, ctx),
                "region-series" => RegionSeries(block, ctx),
                "region-stats" => RegionStats(block, ctx),
                _ => "",   // okänd typ renderas aldrig (BlockRegistry avvisar den redan vid läsning)
            };
        }

        // Kodar < > & " ' osv. men låter å/ä/ö stå som tecken (WebUtility.HtmlEncode gör dem till numeriska entiteter).
        private static readonly System.Text.Encodings.Web.HtmlEncoder Encoder =
            System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

        private static string E(string? s) => Encoder.Encode(s ?? "");

        private static string Section(string id, string inner) => $"<section class=\"site-block mb-4\" data-block=\"{E(id)}\">{inner}</section>";

        private static string Text(SiteBlock b)
        {
            var html = SiteHtmlSanitizer.Sanitize(b.Data["html"]?.GetValue<string>());
            if (html.Length == 0) return "";
            // Tabeller görs rörliga på telefon (saneraren tar bort class, så servern lägger omslaget).
            html = TableOpen.Replace(html, "<div class=\"table-responsive\"><table class=\"table table-sm\"");
            html = TableClose.Replace(html, "</table></div>");
            // Bilder i texten: aldrig bredare än sidan, och en figur utan webbläsarens inbyggda sidomarginal.
            html = html.Replace("<img ", "<img class=\"img-fluid\" loading=\"lazy\" ", StringComparison.Ordinal)
                       .Replace("<figure>", "<figure class=\"mb-3\">", StringComparison.Ordinal);
            return Section(b.Id, $"<div class=\"site-text\">{html}</div>");
        }

        /// <summary>Föreningens presentationstext, live ur noden. Saneras igen här (texten kommer ur en RTE-egenskap, inte ur en betrodd källa). Tom text ger inget block.</summary>
        private static string About(SiteBlock b, SiteRenderContext ctx)
        {
            var html = SiteHtmlSanitizer.Sanitize(ctx.Data.AboutHtml());
            // Tom text (bara taggar och blanksteg, till exempel <p> </p> ur en RTE) ger inget block, och aldrig en ensam rubrik.
            if (html.Length == 0 || (string.IsNullOrWhiteSpace(Regex.Replace(html, "<[^>]+>", "")) && !html.Contains("<img", StringComparison.OrdinalIgnoreCase))) return "";
            html = TableOpen.Replace(html, "<div class=\"table-responsive\"><table class=\"table table-sm\"");
            html = TableClose.Replace(html, "</table></div>");
            html = html.Replace("<img ", "<img class=\"img-fluid\" loading=\"lazy\" ", StringComparison.Ordinal)
                       .Replace("<figure>", "<figure class=\"mb-3\">", StringComparison.Ordinal);
            var title = b.Data["title"]?.GetValue<string>();
            var head = string.IsNullOrWhiteSpace(title) ? "" : $"<h2 class=\"h4\">{E(title)}</h2>";
            return Section(b.Id, head + $"<div class=\"site-text\">{html}</div>");
        }

        private static string Heading(SiteBlock b)
        {
            var text = b.Data["text"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text)) return "";
            var level = b.Data["level"]?.GetValue<int>() == 3 ? 3 : 2;
            return Section(b.Id, $"<h{level}>{E(text)}</h{level}>");
        }

        private static string Links(SiteBlock b)
        {
            var sb = new StringBuilder("<div class=\"d-flex flex-wrap gap-2\">");
            var any = false;
            foreach (var it in b.Data["items"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray())
            {
                var label = it?["label"]?.GetValue<string>();
                var url = it?["url"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(label) || !SiteHtmlSanitizer.IsAllowedLink(url)) continue;   // prövas igen vid rendering
                sb.Append($"<a class=\"btn btn-outline-primary\" href=\"{E(url)}\" rel=\"{SiteHtmlSanitizer.LinkRel}\">{E(label)}</a>");
                any = true;
            }
            return any ? Section(b.Id, sb.Append("</div>").ToString()) : "";
        }

        private static string PageLink(SiteBlock b, SiteRenderContext ctx)
        {
            var page = ctx.Template.PageByKey(b.Data["key"]?.GetValue<string>() ?? "");
            if (page == null || page.IsAdmin || !ctx.Template.PageVisible(page, ctx.Viewer)) return "";   // en sida tittaren inte får se länkas inte
            var owner = ctx.OwnerUrl.EndsWith('/') ? ctx.OwnerUrl : ctx.OwnerUrl + "/";
            var href = page.IsHome ? owner : owner + page.Slug + "/";
            var label = b.Data["label"]?.GetValue<string>();
            return Section(b.Id, $"<a class=\"btn btn-outline-primary\" href=\"{E(href)}\">{E(string.IsNullOrWhiteSpace(label) ? page.Label : label)}</a>");
        }

        private static string Documents(SiteBlock b, SiteVisibility blockLevel, SiteRenderContext ctx)
        {
            var ids = b.Data["refs"]!.AsArray().Select(r => int.Parse(r!.GetValue<string>()["doc:".Length..])).ToList();
            var visible = ctx.Data.Documents(ids)
                .Where(d => ids.Contains(d.Id) && SiteAccess.CanSee(SiteAccess.Strictest(blockLevel, d.Visibility), ctx.Viewer))   // dokumentets egen nivå gäller också
                .ToList();
            if (visible.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<div class=\"list-group\">");
            foreach (var d in visible)
                sb.Append($"<a class=\"list-group-item list-group-item-action\" href=\"/umbraco/surface/Document/DownloadDocument?id={d.Id}\" rel=\"noopener\">{E(d.Title)}" +
                          (string.IsNullOrWhiteSpace(d.Description) ? "" : $"<div class=\"small text-muted\">{E(d.Description)}</div>") + "</a>");
            return Section(b.Id, sb.Append("</div>").ToString());
        }

        private static string Image(SiteBlock b, SiteRenderContext ctx)
        {
            var id = SiteBlockSchema.FileIdOf(b);
            if (id == null) return "";
            var info = ctx.Data.Images(new[] { id.Value }).FirstOrDefault(i => i.Id == id.Value);
            if (info == null) return "";   // en bild som inte hör till ägaren (eller är raderad) visas aldrig
            var decorative = b.Data["decorative"]?.GetValue<bool>() == true;
            var alt = decorative ? "" : b.Data["alt"]?.GetValue<string>() ?? "";
            var caption = b.Data["caption"]?.GetValue<string>();
            var img = $"<img class=\"img-fluid rounded\" src=\"{E(SiteImages.UrlFor(info.Id))}\" width=\"{info.Width}\" height=\"{info.Height}\" alt=\"{E(alt)}\"" +
                      (decorative ? " role=\"presentation\"" : "") + " loading=\"lazy\" decoding=\"async\" style=\"height:auto\">";
            var cap = string.IsNullOrWhiteSpace(caption) ? "" : $"<figcaption class=\"small text-muted mt-1\">{E(caption)}</figcaption>";
            return Section(b.Id, $"<figure class=\"site-image mb-0\">{img}{cap}</figure>");
        }

        private static string News(SiteBlock b, SiteRenderContext ctx)
        {
            var count = Math.Clamp(b.Data["count"]?.GetValue<int>() ?? 5, 1, 10);
            var items = ctx.Data.News(count).Take(count).ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<ul class=\"list-unstyled\">");
            foreach (var n in items)
            {
                var href = SiteHtmlSanitizer.IsAllowedLink(n.Url) ? $"<a href=\"{E(n.Url)}\">{E(n.Title)}</a>" : E(n.Title);
                sb.Append($"<li class=\"mb-2\"><span class=\"text-muted me-2\">{E(n.Date.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")))}</span>{href}</li>");
            }
            return Section(b.Id, sb.Append("</ul>").ToString());
        }

        // Kretsens block: bara på en kretsägare (klubbens mall avvisar dem vid skrivning, och renderingen prövar OCKSÅ: en lagrad rad är inte betrodd).
        private static string RegionList(SiteBlock b, string defaultTitle, IEnumerable<string> itemsHtml)
        {
            var items = itemsHtml.ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder($"<h3>{E(string.IsNullOrWhiteSpace(title) ? defaultTitle : title)}</h3><ul class=\"list-unstyled mb-0\">");
            foreach (var i in items) sb.Append("<li class=\"mb-1\">").Append(i).Append("</li>");
            return Section(b.Id, sb.Append("</ul>").ToString());
        }

        private static string LinkOrText(string name, string url) =>
            SiteHtmlSanitizer.IsAllowedLink(url) ? $"<a href=\"{E(url)}\">{E(name)}</a>" : E(name);

        private static string RegionClubs(SiteBlock b, SiteRenderContext ctx) =>
            ctx.Template.OwnerType != SiteTemplates.Region ? "" :
            RegionList(b, "Klubbar i kretsen", ctx.Data.RegionClubs().Select(c => LinkOrText(c.Name, c.Url)));

        private static string RegionRanges(SiteBlock b, SiteRenderContext ctx) =>
            ctx.Template.OwnerType != SiteTemplates.Region ? "" :
            RegionList(b, "Skjutbanor", ctx.Data.RegionRanges().Select(r =>
                LinkOrText(r.Name, r.Url) + (string.IsNullOrWhiteSpace(r.City) ? "" : $" <span class=\"text-muted\">{E(r.City)}</span>")));

        private static string RegionSeries(SiteBlock b, SiteRenderContext ctx)
        {
            if (ctx.Template.OwnerType != SiteTemplates.Region) return "";
            var count = Math.Clamp(b.Data["count"]?.GetValue<int>() ?? 10, 1, 20);
            var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            return RegionList(b, "Serier", ctx.Data.RegionSeries(count).Take(count).Select(s =>
                LinkOrText(s.Name, s.Url) + (s.Start == null ? "" : $" <span class=\"text-muted\">{E(s.Start.Value.ToString("d MMM yyyy", sv))}" +
                    (s.End == null ? "" : $" – {E(s.End.Value.ToString("d MMM yyyy", sv))}") + "</span>")));
        }

        private static string RegionStats(SiteBlock b, SiteRenderContext ctx)
        {
            if (ctx.Template.OwnerType != SiteTemplates.Region) return "";
            var s = ctx.Data.RegionStats();
            if (s == null) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<div class=\"row row-cols-2 row-cols-md-4 g-3 text-center\">");
            void Card(int n, string label) => sb.Append($"<div class=\"col\"><div class=\"border rounded p-3\"><div class=\"h3 mb-0\">{n}</div><div class=\"small text-muted\">{E(label)}</div></div></div>");
            Card(s.Clubs, "Klubbar");
            Card(s.Ranges, "Skjutbanor");
            Card(s.Series, "Serier");
            Card(s.UpcomingCompetitions, "Kommande tävlingar");
            return Section(b.Id, sb.Append("</div>").ToString());
        }

        private static string Events(SiteBlock b, SiteRenderContext ctx)
        {
            var count = Math.Clamp(b.Data["count"]?.GetValue<int>() ?? 5, 1, 10);
            var evType = b.Data["type"]?.GetValue<string>();
            var events = ctx.Data.UpcomingEvents(count, evType).Where(e => evType == null || e.Type == evType).Take(count).ToList();   // filtret prövas även här: en implementation som ignorerar typen kan inte släppa igenom fel händelser
            if (events.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<ul class=\"list-unstyled\">");
            foreach (var e in events)
            {
                var href = SiteHtmlSanitizer.IsAllowedLink(e.Url) ? $"<a href=\"{E(e.Url)}\">{E(e.Title)}</a>" : E(e.Title);
                sb.Append($"<li class=\"mb-2\"><strong>{E(e.Start.ToString("d MMM yyyy HH:mm", System.Globalization.CultureInfo.GetCultureInfo("sv-SE")))}</strong> {href}" +
                          (string.IsNullOrWhiteSpace(e.Location) ? "" : $" <span class=\"text-muted\">{E(e.Location)}</span>") + "</li>");
            }
            return Section(b.Id, sb.Append("</ul>").ToString());
        }

        /// <summary>
        /// Anmälan/ansökan: ett vanligt HTML-formulär (inget JS) som postar till <c>SiteForm/Submit</c> med skyddstoken, blockets och sidans id och ett dolt honungsfält.
        /// Det som skrivs ut är alltid kodat. Före öppningsdagen och efter stängningsdagen visas ett besked i stället för formuläret, och efter en inskickning visas tacket (bara
        /// för detta block). Allt innehåll i formuläret kommer ur blockets inställningar; servern läser dem om från den PUBLICERADE sidan när något skickas in.
        /// </summary>
        private static string Signup(SiteBlock b, SiteRenderContext ctx)
        {
            var cfg = SiteSignupConfig.FromBlock(b);
            var form = ctx.Data.Form();
            var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            string Id(string f) => E(b.Id) + "-" + f;
            var sb = new StringBuilder($"<div id=\"{Id("anmalan")}\" class=\"site-signup\">");
            sb.Append($"<h3>{E(cfg.Title)}</h3>");
            if (cfg.Intro != null) sb.Append($"<p>{E(cfg.Intro)}</p>");

            var flash = form?.Flash is { } f && f.BlockId == b.Id ? f : null;
            if (flash is { Success: true })
            {
                sb.Append($"<div class=\"alert alert-success\" role=\"status\">{E(cfg.Thanks ?? SiteSignupConfig.DefaultThanks)}</div>")
                  .Append("<script>try{sessionStorage.removeItem('site_signup_'+" + System.Text.Json.JsonSerializer.Serialize(b.Id) + ")}catch(x){}</script>");   // tackat visas: det sparade är inte längre till nytta
                return Section(b.Id, sb.Append("</div>").ToString());
            }
            if (flash != null)
            {
                var detail = flash.Fields is { Count: > 0 } ? "<ul class=\"mb-0 mt-1\">" + string.Concat(flash.Fields.Select(k => $"<li>{E(SiteSignupRules.FieldText(k) ?? "")}</li>")) + "</ul>" : "";
                sb.Append($"<div class=\"alert alert-danger\" role=\"alert\">{E(SiteSignupRules.ErrorText(flash.ErrorCode) ?? "Det gick inte att ta emot anmälan.")}{detail}</div>");
            }

            var state = cfg.StateOn(DateTime.Today);
            if (state == SiteSignupOpenState.NotYet) { sb.Append($"<div class=\"alert alert-info\">Anmälan öppnar {E(cfg.Opens!.Value.ToString("d MMMM yyyy", sv))}.</div>"); return Section(b.Id, sb.Append("</div>").ToString()); }
            if (state == SiteSignupOpenState.Closed) { sb.Append("<div class=\"alert alert-info\">Anmälan har stängt.</div>"); return Section(b.Id, sb.Append("</div>").ToString()); }

            var live = form?.Token != null && form.PageId > 0;
            sb.Append(live ? "<form method=\"post\" action=\"/umbraco/surface/SiteForm/Submit\" class=\"mt-2\">" : "<form class=\"mt-2\"><fieldset disabled>");   // förhandsvisning: en avstängd fieldset gör att inget kan skickas (inget JS)
            if (live)
            {
                sb.Append($"<input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{E(form!.Token)}\">")
                  .Append($"<input type=\"hidden\" name=\"PageId\" value=\"{form.PageId}\"><input type=\"hidden\" name=\"BlockId\" value=\"{E(b.Id)}\">")
                  // Honungsfält: osynligt för en människa, ifyllt av en robot (ett tyst tack, ingen lagring).
                  .Append("<div style=\"position:absolute;left:-10000px;top:auto;width:1px;height:1px;overflow:hidden\" aria-hidden=\"true\"><label>Lämna detta fält tomt<input type=\"text\" name=\"Website\" tabindex=\"-1\" autocomplete=\"off\"></label></div>");
            }
            else sb.Append("<p class=\"text-muted small\">Förhandsvisning: så här ser formuläret ut för besökaren. Det går inte att skicka härifrån.</p>");

            void Field(string name, string label, string input, string? help = null) =>
                sb.Append($"<div class=\"mb-3\"><label class=\"form-label\" for=\"{Id(name)}\">{E(label)}</label>{input}{(help == null ? "" : $"<div class=\"form-text\">{E(help)}</div>")}</div>");

            if (cfg.Choices.Count > 0)
            {
                sb.Append("<fieldset class=\"mb-3\"><legend class=\"form-label fs-6\">Vad gäller din anmälan? <span class=\"text-danger\" aria-hidden=\"true\">*</span></legend>");
                for (var i = 0; i < cfg.Choices.Count; i++)
                    sb.Append($"<div class=\"form-check\"><input class=\"form-check-input\" type=\"radio\" name=\"Choice\" id=\"{Id("val" + i)}\" value=\"{E(cfg.Choices[i])}\" required><label class=\"form-check-label\" for=\"{Id("val" + i)}\">{E(cfg.Choices[i])}</label></div>");
                sb.Append("</fieldset>");
            }
            Field("name", "Namn *", $"<input class=\"form-control\" type=\"text\" name=\"Name\" id=\"{Id("name")}\" maxlength=\"120\" autocomplete=\"name\" required>");
            Field("email", "E-post *", $"<input class=\"form-control\" type=\"email\" name=\"Email\" id=\"{Id("email")}\" maxlength=\"200\" autocomplete=\"email\" data-1p-ignore data-lpignore=\"true\" data-bwignore required>");   // ett anmälningsformulär är ingen inloggning: lösenordshanterare ska inte erbjuda sig
            if (cfg.Phone) Field("phone", "Mobilnummer *", $"<input class=\"form-control\" type=\"tel\" name=\"Phone\" id=\"{Id("phone")}\" maxlength=\"20\" autocomplete=\"tel\" required>");
            if (cfg.PersonalNumber)
                Field("pnr", "Personnummer *", $"<input class=\"form-control\" type=\"text\" name=\"PersonalNumber\" id=\"{Id("pnr")}\" maxlength=\"13\" inputmode=\"numeric\" pattern=\"[0-9]{{8}}-?[0-9]{{4}}\" placeholder=\"ÅÅÅÅMMDD-NNNN\" autocomplete=\"off\" required>",
                    "Föreningen använder personnumret för att kunna registrera dig.");
            if (cfg.Address)
            {
                Field("address", "Adress *", $"<input class=\"form-control\" type=\"text\" name=\"Address\" id=\"{Id("address")}\" maxlength=\"200\" autocomplete=\"street-address\" required>");
                sb.Append($"<div class=\"row\"><div class=\"col-5 col-md-3 mb-3\"><label class=\"form-label\" for=\"{Id("postal")}\">Postnummer *</label><input class=\"form-control\" type=\"text\" name=\"PostalCode\" id=\"{Id("postal")}\" maxlength=\"6\" inputmode=\"numeric\" pattern=\"[0-9]{{3}} ?[0-9]{{2}}\" autocomplete=\"postal-code\" required></div>")
                  .Append($"<div class=\"col-7 col-md-9 mb-3\"><label class=\"form-label\" for=\"{Id("city")}\">Ort *</label><input class=\"form-control\" type=\"text\" name=\"City\" id=\"{Id("city")}\" maxlength=\"80\" autocomplete=\"address-level2\" required></div></div>");
            }
            if (cfg.Gender)
                Field("gender", "Kön", $"<select class=\"form-select\" name=\"Gender\" id=\"{Id("gender")}\"><option value=\"\"></option>{string.Concat(SiteSignupRules.Genders.Select(g => $"<option value=\"{E(g)}\">{E(g)}</option>"))}</select>");
            if (cfg.Message) Field("message", "Meddelande", $"<textarea class=\"form-control\" name=\"Message\" id=\"{Id("message")}\" rows=\"4\" maxlength=\"1000\"></textarea>");

            sb.Append($"<div class=\"form-check mb-3\"><input class=\"form-check-input\" type=\"checkbox\" name=\"Consent\" value=\"true\" id=\"{Id("consent")}\" required><label class=\"form-check-label\" for=\"{Id("consent")}\">Jag godkänner att uppgifterna sparas hos föreningen för att hantera min anmälan. Gäller anmälan medlemskap blir uppgifterna en del av föreningens medlemsregister. <a href=\"/integritetspolicy\" target=\"_blank\" rel=\"noopener\">Integritetspolicy</a></label></div>")
              .Append("<button type=\"submit\" class=\"btn btn-primary\">Skicka anmälan</button>")
              .Append(live ? "</form>" : "</fieldset></form>");
            // Efter ett fel ska besökaren slippa skriva om allt: fälten sparas i webbläsaren (sessionStorage, aldrig i adressen eller på servern)
            // när formuläret skickas och läggs tillbaka när sidan visar felrutan. Utan JS fungerar formuläret som förut. Samtycket kryssas aldrig i åt besökaren.
            if (live)
                sb.Append("<script>(function(){var d=document.currentScript.parentNode,f=d.querySelector('form'),k='site_signup_'+" + System.Text.Json.JsonSerializer.Serialize(b.Id) + ";"
                  + "try{if(d.querySelector('.alert-danger')){var s=JSON.parse(sessionStorage.getItem(k)||'[]');s.forEach(function(p){var e=f.elements[p[0]];if(!e)return;"
                  + "if(e.length&&e[0]&&e[0].type==='radio'){for(var i=0;i<e.length;i++)if(e[i].value===p[1])e[i].checked=true;}else if(!e.value)e.value=p[1];});}"
                  + "else sessionStorage.removeItem(k);}catch(x){}"
                  + "f.addEventListener('submit',function(){try{var s=[];for(var i=0;i<f.elements.length;i++){var e=f.elements[i];"
                  + "if(!e.name||/^(Consent|Website|__RequestVerificationToken|PageId|BlockId)$/.test(e.name))continue;if(e.type==='radio'){if(e.checked)s.push([e.name,e.value]);}else if(e.value)s.push([e.name,e.value]);}"
                  + "sessionStorage.setItem(k,JSON.stringify(s));}catch(x){}});})();</script>");
            return Section(b.Id, sb.Append("</div>").ToString());
        }

        /// <summary>
        /// Bildgalleri: miniatyrer i ett rutnät, var och en en länk till originalet (öppnas i ny flik, inget JS). Bara bilder som hör till ägaren och inte är raderade visas
        /// (<see cref="ISiteBlockData.Images"/>); inga bilder kvar ger inget block. Beskrivningen (alt) faller tillbaka på bildtexten, annars är bilden dekoration.
        /// </summary>
        private static string Gallery(SiteBlock b, SiteRenderContext ctx)
        {
            var items = SiteBlockSchema.GalleryItems(b);
            if (items.Count == 0) return "";
            var infos = ctx.Data.Images(items.Select(i => i.Id).Distinct().ToList()).GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
            var shown = items.Where(i => infos.ContainsKey(i.Id)).ToList();
            if (shown.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<div class=\"row row-cols-2 row-cols-md-3 g-2 site-gallery\">");
            foreach (var it in shown)
            {
                var info = infos[it.Id];
                var alt = string.IsNullOrWhiteSpace(it.Alt) ? it.Caption ?? "" : it.Alt!;
                var srcset = string.Join(", ", SiteImages.ThumbnailWidths.Select(w => $"{SiteImages.ThumbUrlFor(it.Id, w)} {w}w"));
                sb.Append("<div class=\"col\"><figure class=\"mb-0\">")
                  .Append($"<a href=\"{E(SiteImages.UrlFor(it.Id))}\" target=\"_blank\" rel=\"noopener\"{(alt.Length == 0 ? " aria-label=\"Öppna bilden i full storlek\"" : "")}>")
                  .Append($"<img class=\"img-fluid rounded\" src=\"{E(SiteImages.ThumbUrlFor(it.Id, 640))}\" srcset=\"{E(srcset)}\" sizes=\"(min-width: 768px) 33vw, 50vw\" width=\"{info.Width}\" height=\"{info.Height}\" alt=\"{E(alt)}\" loading=\"lazy\" decoding=\"async\" style=\"aspect-ratio:4/3;object-fit:cover;width:100%;height:auto\">")
                  .Append("</a>");
                if (!string.IsNullOrWhiteSpace(it.Caption)) sb.Append($"<figcaption class=\"small text-muted mt-1\">{E(it.Caption)}</figcaption>");
                sb.Append("</figure></div>");
            }
            return Section(b.Id, sb.Append("</div>").ToString());
        }

        private static string Competitions(SiteBlock b, SiteRenderContext ctx)
        {
            var count = Math.Clamp(b.Data["count"]?.GetValue<int>() ?? 5, 1, 10);
            var items = ctx.Data.UpcomingCompetitions(count).Take(count).ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<ul class=\"list-unstyled\">");
            var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            foreach (var c in items)
                sb.Append($"<li class=\"mb-2\"><strong>{E(c.Date.ToString("d MMM yyyy", sv))}</strong> {LinkOrText(c.Title, c.Url)}</li>");
            return Section(b.Id, sb.Append("</ul>").ToString());
        }

        private static string Records(SiteBlock b, SiteRenderContext ctx)
        {
            var items = ctx.Data.Records().ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<div class=\"table-responsive\"><table class=\"table table-sm align-middle\"><thead><tr><th>Gren</th><th>Klass</th><th class=\"text-end\">Resultat</th><th>Innehavare</th><th>Datum</th></tr></thead><tbody>");
            var sv = System.Globalization.CultureInfo.GetCultureInfo("sv-SE");
            foreach (var r in items)
                sb.Append($"<tr><td>{E(r.Discipline)}{(r.IsTeam ? " <span class=\"text-muted small\">(lag)</span>" : "")}</td><td>{E(r.Class)}</td><td class=\"text-end\">{E(r.Result)}</td><td>{E(r.Holder)}</td><td>{E(r.Date.ToString("d MMM yyyy", sv))}</td></tr>");
            return Section(b.Id, sb.Append("</tbody></table></div>").ToString());
        }

        private static string Champions(SiteBlock b, SiteRenderContext ctx)
        {
            var items = ctx.Data.Champions().ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<div class=\"table-responsive\"><table class=\"table table-sm align-middle\"><thead><tr><th>År</th><th>Gren</th><th>Klass</th><th>Mästare</th><th class=\"text-end\">Resultat</th></tr></thead><tbody>");
            foreach (var c in items)
                sb.Append($"<tr><td>{c.Year}</td><td>{E(c.Discipline)}{(c.IsTeam ? " <span class=\"text-muted small\">(lag)</span>" : "")}</td><td>{E(c.Class)}</td><td>{E(c.Holder)}</td><td class=\"text-end\">{E(c.Result)}</td></tr>");
            return Section(b.Id, sb.Append("</tbody></table></div>").ToString());
        }

        private static string Stats(SiteBlock b, SiteRenderContext ctx)
        {
            var items = ctx.Data.Stats().ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<div class=\"row row-cols-2 row-cols-md-4 g-3 text-center\">");
            foreach (var s in items)
                sb.Append($"<div class=\"col\"><div class=\"border rounded p-3\"><div class=\"h3 mb-0\">{s.Value}</div><div class=\"small text-muted\">{E(s.Label)}</div></div></div>");
            return Section(b.Id, sb.Append("</div>").ToString());
        }

        private static string Ranges(SiteBlock b, SiteRenderContext ctx)
        {
            var items = ctx.Data.OwnerRanges().ToList();
            if (items.Count == 0) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder($"<h3>{E(string.IsNullOrWhiteSpace(title) ? "Skjutbanor" : title)}</h3><ul class=\"list-unstyled mb-0\">");
            foreach (var r in items)
                sb.Append("<li class=\"mb-1\">").Append(LinkOrText(r.Name, r.Url)).Append(string.IsNullOrWhiteSpace(r.City) ? "" : $" <span class=\"text-muted\">{E(r.City)}</span>").Append("</li>");
            return Section(b.Id, sb.Append("</ul>").ToString());
        }

        private static string Contact(SiteBlock b, SiteRenderContext ctx)
        {
            var c = ctx.Data.Contact();
            if (c == null || (string.IsNullOrWhiteSpace(c.Person) && string.IsNullOrWhiteSpace(c.Email) && string.IsNullOrWhiteSpace(c.Phone))) return "";
            var title = b.Data["title"]?.GetValue<string>();
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(title)) sb.Append($"<h3>{E(title)}</h3>");
            sb.Append("<address class=\"mb-0\">");
            if (!string.IsNullOrWhiteSpace(c.Person)) sb.Append($"{E(c.Person)}<br>");
            if (!string.IsNullOrWhiteSpace(c.Email)) sb.Append($"<a href=\"mailto:{E(c.Email)}\">{E(c.Email)}</a><br>");
            if (!string.IsNullOrWhiteSpace(c.Phone)) sb.Append($"<a href=\"tel:{E(c.Phone)}\">{E(c.Phone)}</a>");
            return Section(b.Id, sb.Append("</address>").ToString());
        }
    }
}
