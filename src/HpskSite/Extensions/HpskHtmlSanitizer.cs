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
                if (PointsToAnotherHost(e.OriginalUrl)) e.SanitizedUrl = null;
            };
            return s;
        }

        /// <summary>
        /// Relativ adress (egen domän). Webbläsaren normaliserar före tolkning: tabbar, radbrytningar och andra
        /// kontrolltecken tas bort var de än står, och "\" läses som "/". "/\evil.com" och "/&lt;tab&gt;/evil.com"
        /// blir därför "//evil.com" (annan värd). Allt sådant avvisas här (klubbsidorna steg 2, säkerhetsgranskningen K2).
        /// </summary>
        public static bool IsOwnUrl(string? url)
        {
            if (string.IsNullOrEmpty(url) || url[0] != '/') return false;
            foreach (var ch in url)
                if (ch < ' ' || ch == '\u007f' || ch == '\\') return false;
            return !url.StartsWith("//", StringComparison.Ordinal);
        }

        /// <summary>
        /// Adress som en webbläsare skulle tolka som en annan värd än vår: börjar (efter webbläsarens normalisering) med
        /// två snedstreck. Absoluta adresser med schema prövas av schemalistan; här tas bara de relativa formerna.
        /// </summary>
        private static bool PointsToAnotherHost(string? url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            var sb = new System.Text.StringBuilder(url.Length);
            foreach (var ch in url)
            {
                if (ch <= ' ' || ch == '\u007f') continue;
                sb.Append(ch == '\\' ? '/' : ch);
            }
            var n = sb.ToString();
            if (n.StartsWith("//", StringComparison.Ordinal)) return true;
            // En ledande "/" som inte är en ren egen adress (kontrolltecken eller bakstreck mitt i) räknas som misstänkt.
            return url[0] == '/' && !IsOwnUrl(url);
        }

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
