using Microsoft.Extensions.Options;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Sajtens adress utåt, för länkar som lämnar sajten — QR-koder, mejl, kalenderfiler, betallänkar
    /// (skalplanen D4, 2026-10-19).
    ///
    /// <para>Bakom en lastbalanserare är förfrågans Host och Scheme den interna adressen (http, en intern
    /// värd), så en QR-kod byggd ur förfrågan pekar dit. <c>ScaleOut:PublicBaseUrl</c> ger den riktiga
    /// adressen. <b>Utan inställningen byggs adressen ur förfrågan exakt som förut.</b></para>
    ///
    /// <para>⚠️ Bygg aldrig <c>$"{Request.Scheme}://{Request.Host}"</c> själv — anropa <see cref="Base"/>.</para>
    /// </summary>
    public class PublicUrls
    {
        private readonly IOptionsMonitor<ScaleOutOptions> _options;

        public PublicUrls(IOptionsMonitor<ScaleOutOptions> options) => _options = options;

        /// <summary>"https://pistol.nu" — utan avslutande snedstreck.</summary>
        public string Base(HttpRequest? request)
        {
            var configured = _options.CurrentValue.PublicBaseUrl;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');
            if (request == null) return "";
            return $"{request.Scheme}://{request.Host}";
        }

        /// <summary>Base + sökväg ("/station?t=…" → "https://pistol.nu/station?t=…").</summary>
        public string Absolute(HttpRequest? request, string pathAndQuery) =>
            Base(request) + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);
    }

    public static class PublicUrlExtensions
    {
        /// <summary>
        /// Sajtens adress utåt för den här förfrågan ("https://pistol.nu") — <see cref="PublicUrls.Base"/>.
        /// Används i stället för <c>$"{Request.Scheme}://{Request.Host}"</c>.
        /// </summary>
        public static string PublicBase(this HttpRequest request) =>
            request.HttpContext.RequestServices?.GetService<PublicUrls>()?.Base(request)
            ?? $"{request.Scheme}://{request.Host}";
    }
}
