using HpskSite.Services.Security;

namespace HpskSite.Services.Storage
{
    /// <summary>
    /// Skyddar sajtens uppladdningsområden (<c>/images/{field-targets|faltskytte|faltkonfig|uploads}/…</c>)
    /// (säkerhetsplanen 2026-11-02, A6/H5a): en fil med aktivt innehåll (HTML, SVG, XML, skript) serveras
    /// ALDRIG — svaret är 404 — och allt annat som serveras därifrån får <c>nosniff</c> och
    /// <c>Content-Security-Policy: sandbox</c>.
    ///
    /// <para>Gäller både disk (statiska filer) och blobb (<see cref="PublicImageMiddleware"/>), eftersom den ligger
    /// FÖRE båda. Bakgrund: bilduppladdningen tog ändelsen ur klientens filnamn, så en klubbadmin kunde ladda upp
    /// <c>x.html</c> med skript och få det serverat från sajtens egen adress.</para>
    /// </summary>
    public class UploadedContentSafetyMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<UploadedContentSafetyMiddleware> _logger;

        public UploadedContentSafetyMiddleware(RequestDelegate next, ILogger<UploadedContentSafetyMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? "";
            if (IsUploadArea(path))
            {
                if (UploadedContent.IsActiveContent(path))
                {
                    _logger.LogWarning("SEC: Förfrågan om aktivt innehåll i ett uppladdningsområde nekades ({Path})", path);
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }
                context.Response.OnStarting(() =>
                {
                    UploadedContent.AddHardeningHeaders(context.Response.Headers);
                    return Task.CompletedTask;
                });
            }
            await _next(context);
        }

        /// <summary>Ligger sökvägen under ett av de publika uppladdningsområdena?</summary>
        public static bool IsUploadArea(string path)
        {
            const string root = "/images/";
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return false;
            var rest = path.Substring(root.Length);
            var slash = rest.IndexOf('/');
            if (slash <= 0) return false;
            var area = rest.Substring(0, slash);
            return FileStoreAreas.PublicAreas.Any(a => string.Equals(a, area, StringComparison.OrdinalIgnoreCase));
        }
    }
}
