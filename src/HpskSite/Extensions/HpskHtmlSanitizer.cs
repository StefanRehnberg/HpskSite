using Ganss.Xss;

namespace HpskSite.Extensions
{
    /// <summary>
    /// EN vitlista för all HTML från klubbadmin/tävlingsansvariga som visas för andra (säkerhetsplanen P1).
    /// Allt utanför listan tas bort. Bilder får bara vara egna (relativa adresser) — se <see cref="IsOwnUrl"/>.
    /// </summary>
    public static class HpskHtmlSanitizer
    {
        private static readonly Lazy<HtmlSanitizer> Instance = new(Build);

        private static HtmlSanitizer Build()
        {
            var s = new HtmlSanitizer();
            s.AllowedTags.Clear();
            foreach (var t in new[]
            {
                "p", "br", "strong", "b", "em", "i", "u", "s", "ul", "ol", "li", "a",
                "h2", "h3", "h4", "blockquote", "img", "figure", "figcaption",
                "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption", "colgroup", "col",
                "hr", "span", "div", "sup", "sub", "code", "pre"
            }) s.AllowedTags.Add(t);

            s.AllowedAttributes.Clear();
            foreach (var a in new[]
            {
                "href", "src", "alt", "title", "width", "height", "colspan", "rowspan", "class", "target", "rel"
            }) s.AllowedAttributes.Add(a);

            s.AllowedSchemes.Clear();
            foreach (var sc in new[] { "https", "mailto", "tel" }) s.AllowedSchemes.Add(sc);

            s.AllowedCssProperties.Clear();
            foreach (var c in new[] { "text-align", "width", "height", "float", "margin-left", "margin-right" })
                s.AllowedCssProperties.Add(c);

            s.AllowDataAttributes = false;
            // Länkar som öppnas i ny flik ska aldrig få med sig fönstret.
            s.PostProcessNode += (_, e) =>
            {
                if (e.Node is AngleSharp.Html.Dom.IHtmlImageElement img && !IsOwnImage(img.GetAttribute("src")))
                    img.Remove();
                if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement a && a.GetAttribute("target") != null)
                    a.SetAttribute("rel", "noopener noreferrer");
            };
            s.FilterUrl += (_, e) =>
            {
                if (e.OriginalUrl.StartsWith("//", StringComparison.Ordinal)) e.SanitizedUrl = null;
            };
            return s;
        }

        /// <summary>Relativ adress (egen domän) — "//värd" räknas inte.</summary>
        public static bool IsOwnUrl(string? url) =>
            !string.IsNullOrEmpty(url) && url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal);

        private static bool IsOwnImage(string? src)
        {
            if (IsOwnUrl(src)) return true;
            return Uri.TryCreate(src, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
                   (u.Host == "pistol.nu" || u.Host.EndsWith(".pistol.nu", StringComparison.OrdinalIgnoreCase));
        }

        public static string Sanitize(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            lock (Instance.Value)
            {
                return Instance.Value.Sanitize(html);
            }
        }
    }
}
