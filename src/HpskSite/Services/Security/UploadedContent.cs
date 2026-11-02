using Microsoft.AspNetCore.StaticFiles;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Vad en uppladdad fil får serveras som (säkerhetsplanen 2026-11-02, A6/H5).
    ///
    /// <para>Sajtens egna uppladdningar serveras från samma adress som sajten. En fil som webbläsaren tolkar som
    /// HTML eller SVG kör då skript som sajten själv — och kan agera som den inloggade (även en sajtadmin som
    /// öppnar en länk). Därför gäller: <b>Content-Type härleds ur filändelsen på servern, aldrig ur klientens
    /// header</b>, "aktivt innehåll" (HTML, SVG, XML, skript) serveras aldrig som sig självt, och allt som serveras
    /// från ett uppladdningsområde får <c>nosniff</c> och <c>Content-Security-Policy: sandbox</c>.</para>
    /// </summary>
    public static class UploadedContent
    {
        private static readonly FileExtensionContentTypeProvider ContentTypes = new();

        /// <summary>Ändelser som en webbläsare kan köra som sidor eller skript.</summary>
        private static readonly HashSet<string> ActiveContentExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".html", ".htm", ".xhtml", ".shtml", ".mht", ".mhtml", ".svg", ".svgz", ".xml", ".xsl", ".xslt",
            ".js", ".mjs", ".swf", ".hta", ".php", ".asp", ".aspx", ".cshtml", ".jsp", ".cgi", ".shtm"
        };

        /// <summary>Typer som visas direkt i webbläsaren (resten laddas ner).</summary>
        private static readonly HashSet<string> InlineSafeTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/pdf", "image/png", "image/jpeg", "image/gif", "image/webp", "text/plain"
        };

        public static string Extension(string? fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return "";
            try { return Path.GetExtension(fileName).ToLowerInvariant(); }
            catch (ArgumentException) { return ""; }
        }

        /// <summary>HTML, SVG, XML eller skript — aldrig något som får serveras som sig självt från sajtens adress.</summary>
        public static bool IsActiveContent(string? fileName) => ActiveContentExtensions.Contains(Extension(fileName));

        /// <summary>
        /// Content-Type ur filens ändelse. Aktivt innehåll och okända ändelser blir <c>application/octet-stream</c>
        /// (nedladdning), så en fil kan aldrig "kallas" något farligare än den är.
        /// </summary>
        public static string ServedContentType(string? fileName)
        {
            if (IsActiveContent(fileName)) return "application/octet-stream";
            return ContentTypes.TryGetContentType(fileName ?? "", out var ct) ? ct : "application/octet-stream";
        }

        /// <summary>Får filen visas direkt i webbläsaren (inte bara laddas ner)?</summary>
        public static bool IsInlineSafe(string contentType) => InlineSafeTypes.Contains(contentType);

        /// <summary>Huvuden som alltid ska följa med en uppladdad fil som serveras.</summary>
        public static void AddHardeningHeaders(IHeaderDictionary headers)
        {
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Content-Security-Policy"] = "sandbox";
        }
    }
}
