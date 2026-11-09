using Ganss.Xss;

namespace HpskSite.Extensions
{
    /// <summary>
    /// Sanerarprofilen för Text-block på klubbens och kretsens egna sidor (klubbsidorna steg 2, plan v2 §3).
    /// Striktare än <see cref="HpskHtmlSanitizer"/>: ingen <c>class</c>, <c>id</c>, <c>style</c>, <c>target</c>, ingen
    /// <c>span</c>/<c>div</c>. Bilder tillåts bara som sajtens egna bildfiler (<see cref="IsOwnImageUrl"/>), aldrig externa.
    /// Tillåtet: stycke, rubrik, fet, kursiv, understruken, listor, citat, tabell, länk, bild.
    ///
    /// <para>Länkar: <c>https</c>, <c>mailto</c>, <c>tel</c> och egna relativa adresser (<see cref="HpskHtmlSanitizer.IsOwnUrl"/>).
    /// En adress med användarinfo (<c>https://pistol.nu@evil.com</c>) eller en relativ adress som webbläsaren tolkar som
    /// en annan värd tas bort. Varje länk får <c>rel="noopener noreferrer nofollow ugc"</c>.</para>
    /// </summary>
    public static class SiteHtmlSanitizer
    {
        public const string LinkRel = "noopener noreferrer nofollow ugc";

        private static readonly Lazy<HtmlSanitizer> Instance = new(Build);

        private static HtmlSanitizer Build()
        {
            var s = new HtmlSanitizer();
            s.AllowedTags.Clear();
            foreach (var t in new[]
            {
                "p", "br", "strong", "b", "em", "i", "u", "s", "ul", "ol", "li", "a",
                "h2", "h3", "h4", "blockquote", "hr", "sup", "sub",
                "table", "thead", "tbody", "tfoot", "tr", "th", "td", "caption",
                "img", "figure"
            }) s.AllowedTags.Add(t);

            s.AllowedAttributes.Clear();
            foreach (var a in new[] { "href", "title", "colspan", "rowspan", "src", "alt" }) s.AllowedAttributes.Add(a);

            s.AllowedSchemes.Clear();
            foreach (var sc in new[] { "https", "mailto", "tel" }) s.AllowedSchemes.Add(sc);

            s.AllowedCssProperties.Clear();
            s.AllowDataAttributes = false;

            s.FilterUrl += (_, e) =>
            {
                // En bild i texten får bara peka på en av sajtens egna bildfiler; allt annat (extern adress, data:) tas bort.
                if (e.Tag is AngleSharp.Html.Dom.IHtmlImageElement) { if (!IsOwnImageUrl(e.OriginalUrl)) e.SanitizedUrl = null; return; }
                if (!IsAllowedLink(e.OriginalUrl)) e.SanitizedUrl = null;
            };
            s.PostProcessNode += (_, e) =>
            {
                if (e.Node is AngleSharp.Html.Dom.IHtmlImageElement img && !IsOwnImageUrl(img.GetAttribute("src")))
                {
                    e.ReplacementNodes.Clear();   // en bild utan godkänd källa är ingen bild
                    img.Remove();
                    return;
                }
                if (e.Node is AngleSharp.Html.Dom.IHtmlAnchorElement a)
                {
                    a.RemoveAttribute("target");
                    a.SetAttribute("rel", LinkRel);
                }
            };
            return s;
        }

        /// <summary>
        /// Får en länk finnas? Webbläsaren normaliserar före tolkning (tab/radbrytning bort, <c>\</c> blir <c>/</c>);
        /// här prövas den normaliserade formen. Ren funktion.
        /// </summary>
        public static bool IsAllowedLink(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var sb = new System.Text.StringBuilder(url.Length);
            foreach (var ch in url)
            {
                if (ch <= ' ' || ch == '\u007f') continue;       // webbläsaren släpper kontrolltecken och mellanrum i schema/värd
                sb.Append(ch == '\\' ? '/' : ch);
            }
            var n = sb.ToString();

            if (n.StartsWith("//", StringComparison.Ordinal)) return false;
            if (n[0] == '/') return HpskHtmlSanitizer.IsOwnUrl(url);   // egen relativ adress, prövad på originalet
            if (n[0] == '#') return true;

            if (n.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) || n.StartsWith("tel:", StringComparison.OrdinalIgnoreCase))
                return true;

            if (!n.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Uri.TryCreate(n, UriKind.Absolute, out var u)) return false;
            return u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo) && !string.IsNullOrEmpty(u.Host);
        }

        /// <summary>Adressen till en av sajtens bildfiler (<c>/umbraco/surface/SiteFile/Image?id=N</c>), exakt den formen och inget annat.</summary>
        public static bool IsOwnImageUrl(string? url) => url != null && OwnImageRx.IsMatch(url);

        private static readonly System.Text.RegularExpressions.Regex OwnImageRx =
            new(@"^/umbraco/surface/SiteFile/Image\?id=[1-9]\d{0,8}$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>Bildfilernas id:n som en text bäddar in (ur den saneras HTML:en). Ren funktion.</summary>
        public static IReadOnlyList<int> InlineFileIds(string? html)
        {
            if (string.IsNullOrEmpty(html)) return Array.Empty<int>();
            return InlineIdRx.Matches(html).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        }

        private static readonly System.Text.RegularExpressions.Regex InlineIdRx =
            new(@"SiteFile/Image\?id=([1-9]\d{0,8})", System.Text.RegularExpressions.RegexOptions.Compiled);

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
