using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// En signatur som den ritas: avatar (bild eller initialer), namn och eventuell roll. <see cref="Role"/> är null när personen
    /// inte har någon roll i just den här klubben eller kretsen.
    /// </summary>
    public sealed record SiteSignature(int MemberId, string Name, string? PictureUrl, string? Role);

    /// <summary>
    /// Signaturen i en text (klubbsidornas textblock och nyheter): EN regel för hur den ser ut i lagrad HTML, vem som får skriva in en,
    /// och hur den ritas. Ren kod (ingen databas): vem en medlem är och vilken roll hen har slås upp av <see cref="SiteSignatureService"/>.
    ///
    /// <para><b>I texten lagras bara ett medlemsnummer</b>: <c>&lt;hpsk-signatur member="123"&gt;&lt;/hpsk-signatur&gt;</c>. Namn, bild och roll
    /// hämtas när sidan ritas, så de följer med när personen byter bild eller får en ny roll, och en medlem som lämnat föreningen
    /// försvinner ur texten av sig själv. Saneraren släpper bara igenom just den formen (<see cref="HpskSite.Extensions.SiteHtmlSanitizer"/>).</para>
    ///
    /// <para><b>Man signerar bara som sig själv.</b> Ett nummer som inte är skribentens eget och inte redan stod i texten tas bort vid
    /// sparningen (<see cref="Restrict"/>): annars kunde vem som helst skriva "Ordförande" under en text. Det som redan står kvar
    /// (en annan skribents signatur) bevaras när någon annan redigerar texten.</para>
    /// </summary>
    public static class SiteSignatures
    {
        public const string Tag = "hpsk-signatur";

        // Exakt den form saneraren skriver: ett nummer, inget annat. Läses både ur HTML och (med <) aldrig ur JSON, som tolkas först.
        private static readonly Regex TokenRx = new("<hpsk-signatur member=\"(\\d{1,9})\"></hpsk-signatur>", RegexOptions.Compiled);

        /// <summary>Medlemsnumren i signaturerna i en (saneras) text, utan dubbletter. Tom text ger tom lista.</summary>
        public static IReadOnlyList<int> Ids(string? html)
        {
            if (string.IsNullOrEmpty(html) || !html.Contains(Tag, StringComparison.Ordinal)) return Array.Empty<int>();
            return TokenRx.Matches(html).Select(m => int.Parse(m.Groups[1].Value)).Distinct().ToList();
        }

        /// <summary>Medlemsnummer ur en kommaseparerad lista (<c>"12,45"</c>); allt som inte är ett positivt tal hoppas över. Högst 50.</summary>
        public static List<int> ParseIds(string? csv) =>
            (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) && n > 0 ? n : 0).Where(n => n > 0).Distinct().Take(50).ToList();

        public static bool Has(string? html) => !string.IsNullOrEmpty(html) && html.Contains("<" + Tag, StringComparison.Ordinal);

        /// <summary>
        /// Tar bort de signaturer i <paramref name="html"/> som skribenten inte får skriva in: allt utom skribentens eget nummer och de nummer som
        /// redan stod i texten (<paramref name="alreadyThere"/>). Utan skribent (null) bevaras bara det som redan stod.
        /// </summary>
        public static string Restrict(string? html, int? writer, ISet<int> alreadyThere)
        {
            if (string.IsNullOrEmpty(html) || !html.Contains(Tag, StringComparison.Ordinal)) return html ?? "";
            return TokenRx.Replace(html, m =>
            {
                var id = int.Parse(m.Groups[1].Value);
                return (writer.HasValue && id == writer.Value) || alreadyThere.Contains(id) ? m.Value : "";
            });
        }

        /// <summary>Medlemsnumren i textblocken i en sidas blockJSON (<c>data.html</c> på varje block). Ogiltig JSON ger tom lista.</summary>
        public static HashSet<int> IdsInBlocksJson(string? blocksJson)
        {
            var ids = new HashSet<int>();
            foreach (var html in TextBlocks(Parse(blocksJson))) foreach (var id in Ids(html)) ids.Add(id);
            return ids;
        }

        /// <summary>
        /// <see cref="Restrict"/> över alla textblock i en sidas blockJSON. Returnerar JSON:en oförändrad när ingen signatur fanns eller ändrades
        /// (inget skrivs om i onödan), annars den nya.
        /// </summary>
        public static string RestrictBlocksJson(string blocksJson, int? writer, ISet<int> alreadyThere)
        {
            if (string.IsNullOrEmpty(blocksJson) || !blocksJson.Contains(Tag, StringComparison.Ordinal)) return blocksJson;
            var root = Parse(blocksJson);
            if (root == null) return blocksJson;
            var changed = false;
            foreach (var data in TextData(root))
            {
                var html = data["html"]?.GetValue<string>();
                var restricted = Restrict(html, writer, alreadyThere);
                if (!ReferenceEquals(restricted, html) && restricted != html) { data["html"] = restricted; changed = true; }
            }
            return changed ? root.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) : blocksJson;
        }

        private static JsonNode? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonNode.Parse(json); } catch (System.Text.Json.JsonException) { return null; }
        }

        private static IEnumerable<JsonObject> TextData(JsonNode root)
        {
            // Sidans innehåll är { blocks: [...], before/after: [...] } eller en lista; blocken kan ligga var som helst, så leta efter varje
            // objekt som är ett textblock (type = "text") och ge dess data.
            var stack = new Stack<JsonNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (n is JsonObject o)
                {
                    if (o["type"]?.GetValue<string>() == "text" && o["data"] is JsonObject d && d["html"] != null) yield return d;
                    foreach (var kv in o) if (kv.Value != null) stack.Push(kv.Value);
                }
                else if (n is JsonArray a) foreach (var x in a) if (x != null) stack.Push(x);
            }
        }

        private static IEnumerable<string?> TextBlocks(JsonNode? root) =>
            root == null ? Enumerable.Empty<string?>() : TextData(root).Select(d => d["html"]?.GetValue<string>());

        // ── Utritning ───────────────────────────────────────────────────────────────────────────────────────────────

        private static readonly System.Text.Encodings.Web.HtmlEncoder Encoder =
            System.Text.Encodings.Web.HtmlEncoder.Create(System.Text.Unicode.UnicodeRanges.All);

        private static string E(string? s) => Encoder.Encode(s ?? "");

        /// <summary>Initialer ur ett namn: första och SISTA ordets begynnelsebokstav, som medlemsregistrets avatar. Ren funktion.</summary>
        public static string Initials(string? name)
        {
            var parts = (name ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return "?";
            var s = parts[0][..1];
            if (parts.Length > 1) s += parts[^1][..1];
            return s.ToUpperInvariant();
        }

        /// <summary>
        /// Signaturens HTML: rund avatar (bilden ligger ovanpå initialerna och faller bort av sig själv om den inte går att hämta), namnet
        /// och, när personen har en roll, rollen på en egen rad. Allt kodat. Bilden är bara en egen adress (<see cref="IsOwnPicture"/>).
        /// </summary>
        public static string Render(SiteSignature s)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"hs-sig\"><span class=\"hs-sig-av\" aria-hidden=\"true\">").Append(E(Initials(s.Name)));
            if (IsOwnPicture(s.PictureUrl)) sb.Append("<img src=\"").Append(E(s.PictureUrl)).Append("\" alt=\"\" loading=\"lazy\" onerror=\"this.remove()\">");
            sb.Append("</span><span class=\"hs-sig-who\"><span class=\"hs-sig-name\">").Append(E(s.Name)).Append("</span>");
            if (!string.IsNullOrWhiteSpace(s.Role)) sb.Append("<span class=\"hs-sig-role\">").Append(E(s.Role)).Append("</span>");
            sb.Append("</span></div>");
            return sb.ToString();
        }

        /// <summary>Profilbilden får bara vara en av sajtens egna adresser (relativ) eller en https-adress; aldrig javascript: eller data:.</summary>
        public static bool IsOwnPicture(string? url) =>
            !string.IsNullOrWhiteSpace(url) && url.Length <= 500 && !url.Contains('"') && !url.Contains('\\') &&
            ((url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Byter ut varje signatur i en (saneras) text mot dess utritning. <paramref name="lookup"/> ger null för en medlem som inte får visas
        /// (finns inte, hör inte till föreningen): signaturen försvinner då helt, utan spår och utan tom ruta.
        /// </summary>
        public static string Resolve(string? html, Func<int, SiteSignature?> lookup)
        {
            if (string.IsNullOrEmpty(html) || !html.Contains(Tag, StringComparison.Ordinal)) return html ?? "";
            var cache = new Dictionary<int, string>();
            return TokenRx.Replace(html, m =>
            {
                var id = int.Parse(m.Groups[1].Value);
                if (!cache.TryGetValue(id, out var rendered))
                {
                    var sig = lookup(id);
                    rendered = sig == null ? "" : Render(sig);
                    cache[id] = rendered;
                }
                return rendered;
            });
        }

        /// <summary>Texten utan signaturer, för mejl och utdrag där en avatar inte kan ritas. Namn och roll skrivs som en rad.</summary>
        public static string ToPlainLine(string? html, Func<int, SiteSignature?> lookup)
        {
            if (string.IsNullOrEmpty(html) || !html.Contains(Tag, StringComparison.Ordinal)) return html ?? "";
            return TokenRx.Replace(html, m =>
            {
                var sig = lookup(int.Parse(m.Groups[1].Value));
                return sig == null ? "" : "<p><strong>" + E(sig.Name) + "</strong>" + (string.IsNullOrWhiteSpace(sig.Role) ? "" : "<br>" + E(sig.Role)) + "</p>";
            });
        }
    }
}
