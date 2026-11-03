using System.Collections.Concurrent;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Säkerhetsplanen 2026-11-02, paket 2 (P3): en skrivande action utan eget beslut om antiforgery ska inte lämna en annan sajt
    /// möjlighet att posta åt en inloggad medlem (CSRF). Läge via <c>Security:Antiforgery:Mode</c>:
    /// <list type="bullet">
    /// <item><c>Log</c> (förval): prövar tokenen och loggar "SEC:" EN gång per action och orsak när den saknas eller är ogiltig —
    /// ingen nekas. Underlag för vilka vyer som måste skicka token innan nekandet slås på.</item>
    /// <item><c>Enforce</c>: ett anrop utan giltig token får 400 med en svensk text.</item>
    /// <item><c>Off</c>: ingen prövning.</item>
    /// </list>
    /// Undantag: anrop med <c>Authorization: Bearer</c> (mobilens JWT — en webbläsare skickar aldrig det av sig själv),
    /// anrop utan inloggningskaka, och actions med ett eget beslut (<c>[ValidateAntiForgeryToken]</c> / <c>[IgnoreAntiforgeryToken]</c>).
    /// </summary>
    public sealed class AntiforgeryLogFilter : IAsyncAuthorizationFilter
    {
        private static readonly ConcurrentDictionary<string, byte> Seen = new();
        private readonly IAntiforgery _antiforgery;
        private readonly IConfiguration _configuration;
        private readonly ILogger<AntiforgeryLogFilter> _logger;

        public AntiforgeryLogFilter(IAntiforgery antiforgery, IConfiguration configuration, ILogger<AntiforgeryLogFilter> logger)
        {
            _antiforgery = antiforgery;
            _configuration = configuration;
            _logger = logger;
        }

        public static string ModeOf(string? configured) => configured?.Trim().ToLowerInvariant() switch
        {
            "enforce" => "enforce",
            "off" => "off",
            _ => "log"
        };

        public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
        {
            var mode = ModeOf(_configuration["Security:Antiforgery:Mode"]);
            if (mode == "off") return;
            if (context.ActionDescriptor is not ControllerActionDescriptor cad) return;
            if (cad.ControllerTypeInfo.Assembly != typeof(AntiforgeryLogFilter).Assembly) return;
            if (!AntiforgeryCoverage.IsUnsafeVerb(cad.MethodInfo) || AntiforgeryCoverage.HasDecision(cad.MethodInfo)) return;

            var http = context.HttpContext;
            // En action utan angivet verb svarar på alla — prövningen gäller bara det anrop som faktiskt skriver.
            if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method)
                || HttpMethods.IsOptions(http.Request.Method) || HttpMethods.IsTrace(http.Request.Method)) return;
            var auth = http.Request.Headers.Authorization.ToString();
            if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return;
            // Utan sessionskaka finns inget att kapa: CSRF handlar om att utnyttja en inloggad besökares webbläsare.
            if (!http.Request.Cookies.Keys.Any(k => k.Contains("Identity.Application", StringComparison.Ordinal))) return;

            bool valid;
            try { valid = await _antiforgery.IsRequestValidAsync(http); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SEC: Antiforgery-prövningen kunde inte genomföras för {Endpoint}", EndpointAuthClassifier.Key(cad.MethodInfo));
                return;
            }
            if (valid) return;

            var key = EndpointAuthClassifier.Key(cad.MethodInfo);
            if (Seen.TryAdd(key, 0))
                _logger.LogWarning("SEC: Skrivande anrop utan giltig antiforgery-token till {Endpoint} ({Method} {Path}, referer {Referer}) (mode={Mode})",
                    key, http.Request.Method, http.Request.Path, http.Request.Headers.Referer.ToString(), mode);

            if (mode == "enforce")
            {
                var wantsJson = http.Request.Headers.Accept.ToString().Contains("json", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(http.Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
                const string msg = "Sidan har varit öppen för länge. Gå tillbaka, ladda om sidan och försök igen. Dina uppgifter har inte sparats.";
                context.Result = wantsJson
                    ? new JsonResult(new { success = false, message = msg }) { StatusCode = 400 }
                    : new ContentResult { StatusCode = 400, ContentType = "text/html; charset=utf-8",
                        Content = $"<!doctype html><meta charset=\"utf-8\"><title>Försök igen</title><p>{msg}</p><p><a href=\"javascript:history.back()\">Tillbaka</a> · <a href=\"/login-%26-register/?tab=login\">Logga in igen</a></p>" };
            }
        }
    }

    public class AntiforgeryLogComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder) =>
            builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.Add<AntiforgeryLogFilter>());
    }
}
