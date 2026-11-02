using System.Collections.Concurrent;
using HpskSite.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;

namespace HpskSite.Services.Security
{
    /// <summary>
    /// Säkerhetsplanen 2026-11-02, release 1c: en action utan markering som anropas av någon som inte är inloggad ska
    /// aldrig vara en tyst överraskning. Läge via <c>Security:DenyByDefault:Mode</c>:
    /// <list type="bullet">
    /// <item><c>Log</c> (förval): loggar "SEC:" EN gång per action och process — ingen nekas. Underlag för vilka actions som
    /// verkligen nås anonymt i prod, så att de kan märkas med <see cref="PublicEndpointAttribute"/> innan nekandet slås på.</item>
    /// <item><c>Enforce</c>: anonyma anrop till omärkta actions får 401 (JSON) — Stefans beslut om tidpunkt, tidigast efter
    /// observationsfönstret.</item>
    /// <item><c>Off</c>: ingen kontroll.</item>
    /// </list>
    /// Inloggad = webbens kaka eller mobilens JWT. Endast sajtens egna controllers; Umbracos (backoffice, API) rörs inte.
    /// </summary>
    public sealed class UnclassifiedEndpointFilter : IAsyncAuthorizationFilter
    {
        private static readonly ConcurrentDictionary<string, byte> Seen = new();
        private readonly IConfiguration _configuration;
        private readonly ILogger<UnclassifiedEndpointFilter> _logger;

        public UnclassifiedEndpointFilter(IConfiguration configuration, ILogger<UnclassifiedEndpointFilter> logger)
        {
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
            var mode = ModeOf(_configuration["Security:DenyByDefault:Mode"]);
            if (mode == "off") return;
            if (context.ActionDescriptor is not ControllerActionDescriptor cad) return;
            if (cad.ControllerTypeInfo.Assembly != typeof(UnclassifiedEndpointFilter).Assembly) return;
            if (EndpointAuthClassifier.Classify(cad.MethodInfo) != EndpointAuthKind.Unclassified) return;

            var http = context.HttpContext;
            if (http.User?.Identity?.IsAuthenticated == true) return;
            // Mobilens JWT autentiseras inte som standardschema — fråga det också.
            var jwt = await http.AuthenticateAsync("JwtBearer");
            if (jwt.Succeeded) return;

            var key = EndpointAuthClassifier.Key(cad.MethodInfo);
            if (Seen.TryAdd(key, 0))
                _logger.LogWarning("SEC: Anonymt anrop till omärkt endpoint {Endpoint} ({Method} {Path}) (mode={Mode})",
                    key, http.Request.Method, http.Request.Path, mode);

            if (mode == "enforce")
                context.Result = new JsonResult(new { success = false, message = "Du måste vara inloggad." }) { StatusCode = 401 };
        }
    }

    public class UnclassifiedEndpointComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder) =>
            builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(o => o.Filters.Add<UnclassifiedEndpointFilter>());
    }
}
