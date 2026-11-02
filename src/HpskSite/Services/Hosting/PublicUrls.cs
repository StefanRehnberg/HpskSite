using Microsoft.Extensions.Options;

namespace HpskSite.Services.Hosting
{
    /// <summary>
    /// Sajtens adress utåt, för länkar som lämnar sajten — QR-koder, mejl, kalenderfiler, betallänkar
    /// (skalplanen D4, 2026-10-19).
    ///
    /// <para>Bakom en lastbalanserare är förfrågans Host och Scheme den interna adressen (http, en intern
    /// värd), så en QR-kod byggd ur förfrågan pekar dit. <c>ScaleOut:PublicBaseUrl</c> ger den riktiga
    /// adressen.</para>
    ///
    /// <para>⚠️ Säkerhetsplanen 2026-11-02 (H7): <b>utanför Development byggs adressen aldrig ur Host-huvudet.</b>
    /// Förut föll den tillbaka på <c>Request.Host</c> när <c>PublicBaseUrl</c> var tom — en förfrågan med
    /// <c>Host: evil.example</c> till lösenordsåterställning eller inbjudan gav då ett mejl med en länk till
    /// angriparens domän. Nu används i ordning: <c>ScaleOut:PublicBaseUrl</c>, sedan
    /// <c>EmailSettings:SiteUrl</c> (finns redan i prod), och först i Development förfrågan.</para>
    ///
    /// <para>⚠️ Bygg aldrig <c>$"{Request.Scheme}://{Request.Host}"</c> själv — anropa <see cref="Base"/>.</para>
    /// </summary>
    public class PublicUrls
    {
        private readonly IOptionsMonitor<ScaleOutOptions> _options;
        private readonly IConfiguration _configuration;
        private readonly IHostEnvironment _environment;

        public PublicUrls(IOptionsMonitor<ScaleOutOptions> options, IConfiguration configuration, IHostEnvironment environment)
        {
            _options = options;
            _configuration = configuration;
            _environment = environment;
        }

        /// <summary>"https://pistol.nu" — utan avslutande snedstreck.</summary>
        public string Base(HttpRequest? request)
        {
            var configured = _options.CurrentValue.PublicBaseUrl;
            if (!string.IsNullOrWhiteSpace(configured)) return configured.TrimEnd('/');

            if (!_environment.IsDevelopment())
            {
                var site = UsableSiteUrl(_configuration["EmailSettings:SiteUrl"]);
                if (site != null) return site;
            }

            if (request == null) return "";
            return $"{request.Scheme}://{request.Host}";
        }

        /// <summary>Base + sökväg ("/station?t=…" → "https://pistol.nu/station?t=…").</summary>
        public string Absolute(HttpRequest? request, string pathAndQuery) =>
            Base(request) + (pathAndQuery.StartsWith('/') ? pathAndQuery : "/" + pathAndQuery);

        /// <summary>
        /// Ett användbart <c>EmailSettings:SiteUrl</c>: en absolut http(s)-adress som inte är mallens platshållare
        /// ("https://your-domain.com"). Annars null (då gäller förfrågan, som förut).
        /// </summary>
        public static string? UsableSiteUrl(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
            if (uri.Host.Contains("your-domain", StringComparison.OrdinalIgnoreCase)) return null;
            return value.Trim().TrimEnd('/');
        }
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
