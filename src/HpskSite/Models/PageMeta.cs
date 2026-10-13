using Microsoft.AspNetCore.Http;

namespace HpskSite.Models
{
    /// <summary>
    /// Sidans metadata — titel, beskrivning, bild, typ och kanonisk adress — för <c>Master.cshtml</c>
    /// (C3-planen F4). En vy eller controller sätter det den vet med <see cref="For"/>; Master läser
    /// samma objekt och faller tillbaka på ViewBag och innehållsnoden.
    ///
    /// ⚠️ Varför inte bara <c>Model</c>: på en routad sida (händelser i tabelläget, <c>/mitt-schema</c>,
    /// tävlingssidorna när de blir controllers) är <c>Model</c> en BÄRARNOD — klubben, startsidan — och
    /// dess <c>metaDescription</c> och <c>Url()</c> beskriver en annan sida. Förut fick en händelse
    /// klubbens beskrivning och <c>og:url</c> till klubben, så en delad länk visade klubbsidan.
    /// </summary>
    public sealed class PageMeta
    {
        public const string ItemKey = "hpsk.pagemeta";

        public string? Title { get; set; }
        public string? Description { get; set; }
        /// <summary>Bildens adress; relativ görs absolut i Master (sociala medier kräver absolut).</summary>
        public string? Image { get; set; }
        public string? Type { get; set; }
        /// <summary>Kanonisk adress (relativ eller absolut). Null = Master avgör.</summary>
        public string? Canonical { get; set; }

        /// <summary>Sidans metadataobjekt för den här begäran — skapas vid första anropet.</summary>
        public static PageMeta For(HttpContext context)
        {
            if (context.Items[ItemKey] is PageMeta m) return m;
            m = new PageMeta();
            context.Items[ItemKey] = m;
            return m;
        }

        /// <summary>Absolut adress på den här värden. Tomt in = null ut; redan absolut lämnas orörd.</summary>
        public static string? Absolute(HttpRequest request, string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && (abs.Scheme == "http" || abs.Scheme == "https"))
                return url;
            var path = url.StartsWith('/') ? url : "/" + url;
            return $"{request.Scheme}://{request.Host}{path}";
        }

        /// <summary>
        /// Är begärans sökväg samma sida som innehållsnodens egen adress? Bara då beskriver nodens
        /// beskrivning och adress den sida som visas. Skiftläge och avslutande snedstreck ignoreras.
        /// </summary>
        public static bool IsNodesOwnPage(string requestPath, string? nodeUrl)
        {
            if (string.IsNullOrEmpty(nodeUrl) || nodeUrl == "#") return false;
            string Norm(string s)
            {
                var p = s;
                if (Uri.TryCreate(s, UriKind.Absolute, out var u)) p = u.AbsolutePath;
                p = p.Trim().TrimEnd('/');
                return p.Length == 0 ? "/" : p.ToLowerInvariant();
            }
            return Norm(requestPath) == Norm(nodeUrl);
        }

        /// <summary>
        /// Standard för den kanoniska adressen: nodens adress när sidan är nodens egen (då faller
        /// frågesträngen bort — <c>?tab=</c> är samma sida), annars sökvägen MED frågesträng
        /// (<c>/mitt-schema?c=</c> och <c>/live?c=</c> är olika sidor).
        /// </summary>
        public static string DefaultCanonical(HttpRequest request, string? nodeUrl) =>
            IsNodesOwnPage(request.Path.Value ?? "/", nodeUrl)
                ? Absolute(request, nodeUrl!)!
                : $"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}";
    }
}
