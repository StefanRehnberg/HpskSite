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
    }

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

        /// <summary>Renderar ett block, eller tom sträng när tittaren inte får se det.</summary>
        public static string Render(SiteBlock block, SiteVisibility pageVisibility, SiteRenderContext ctx)
        {
            var effective = SiteAccess.Strictest(block.Visibility, pageVisibility);
            if (SiteBlockSchema.Types.TryGetValue(block.Type, out var type) && type.Personal)
                effective = SiteAccess.CapPersonal(effective);
            if (!SiteAccess.CanSee(effective, ctx.Viewer)) return "";

            return block.Type switch
            {
                "text" => Text(block),
                "heading" => Heading(block),
                "links" => Links(block),
                "pagelink" => PageLink(block, ctx),
                "documents" => Documents(block, effective, ctx),
                "events" => Events(block, ctx),
                "contact" => Contact(block, ctx),
                "image" => Image(block, ctx),
                "news" => News(block, ctx),
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
            return Section(b.Id, $"<div class=\"site-text\">{html}</div>");
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
