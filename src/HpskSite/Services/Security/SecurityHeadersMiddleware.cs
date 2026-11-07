using Microsoft.AspNetCore.Http;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Säkerhetsplanen 2026-11-02, paket 2 (P2 steg 1): grundläggande svarshuvuden på varje svar — ingen CSP (det är en egen fas
    /// efter att skripten legat lokalt). Sätts i <c>OnStarting</c>, så även felsvar och 404 får dem, och bara när huvudet
    /// inte redan är satt av någon annan (Umbraco, uppladdningsskyddet).
    /// <list type="bullet">
    /// <item><c>nosniff</c>: webbläsaren gissar aldrig en annan typ än den vi anger.</item>
    /// <item><c>X-Frame-Options: SAMEORIGIN</c>: sajten kan inte läggas i en ram på en annan sajt (klickkapning). Sajtens egna
    /// ramar (resultatlistor, dokument, lektioner) är samma ursprung.</item>
    /// <item><c>Referrer-Policy</c>: hela adressen skickas bara inom sajten, bara ursprunget utåt, ingenting vid nedgradering.</item>
    /// <item><c>Permissions-Policy</c>: kameran bara för sajten själv (QR-läsning), mikrofon, betalning och USB av.</item>
    /// <item><c>Strict-Transport-Security</c>: <c>max-age=86400</c> utan <c>includeSubDomains</c> och utan preload — höjs stegvis av
    /// Stefan, aldrig i utveckling och aldrig på http.</item>
    /// </list>
    /// </summary>
    public sealed class SecurityHeadersMiddleware
    {
        public const int HstsSeconds = 86400;
        private readonly RequestDelegate _next;
        private readonly bool _isDevelopment;

        public SecurityHeadersMiddleware(RequestDelegate next, IHostEnvironment env)
        {
            _next = next;
            _isDevelopment = env.IsDevelopment();
        }

        public Task InvokeAsync(HttpContext context)
        {
            context.Response.OnStarting(() => { Apply(context, _isDevelopment); return Task.CompletedTask; });
            return _next(context);
        }

        public static void Apply(HttpContext context, bool isDevelopment)
        {
            var h = context.Response.Headers;
            SetDefault(h, "X-Content-Type-Options", "nosniff");
            SetDefault(h, "X-Frame-Options", "SAMEORIGIN");
            SetDefault(h, "Referrer-Policy", "strict-origin-when-cross-origin");
            SetDefault(h, "Permissions-Policy", "camera=(self), microphone=(), payment=(), usb=()");
            if (!isDevelopment && context.Request.IsHttps)
                SetDefault(h, "Strict-Transport-Security", $"max-age={HstsSeconds}");

            // Klubbsidornas undersidor (steg 1, luckan som steg 2-granskningen hittade): sidans synlighet beror på vem som
            // frågar, så ett delat cacheminne eller en CDN får aldrig lagra svaret, och sökmotorer indexerar inte undersidor
            // som klubben inte uttryckligen valt att publicera. Sätts alltid (inte bara när huvudet saknas).
            if (context.Items.ContainsKey(HpskSite.Services.SitePages.SitePageRequest.ItemKey))
            {
                h["Cache-Control"] = "private, no-store";
                h["X-Robots-Tag"] = "noindex";
            }
        }

        private static void SetDefault(IHeaderDictionary h, string name, string value)
        {
            if (!h.ContainsKey(name)) h[name] = value;
        }
    }
}
