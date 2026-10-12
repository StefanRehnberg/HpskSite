using HpskSite.Services;
using Umbraco.Cms.Core.Security;

namespace HpskSite.Middleware
{
    /// <summary>
    /// Stämplar inloggade medlemmars webbaktivitet (backoffice undantaget).
    ///
    /// <para><b>⚠️ Billig på varje förfrågan, med flit</b> (skalplanen B1, 2026-10-12). Förut slog den
    /// upp medlemmen på VARJE förfrågan och loggade en Information-rad per förfrågan. Nu: är
    /// förfrågan inte inloggad händer ingenting, och medlemmen slås bara upp när strypningen säger
    /// att det är dags att skriva — högst en gång per medlem och <c>ThrottleMinutes</c>.</para>
    /// </summary>
    public class MemberActivityTrackingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<MemberActivityTrackingMiddleware> _logger;

        public MemberActivityTrackingMiddleware(
            RequestDelegate next,
            ILogger<MemberActivityTrackingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(
            HttpContext context,
            IMemberManager memberManager,
            MemberActivityService activityService)
        {
            try
            {
                var path = context.Request.Path.Value ?? "";
                // ⚠️ Middlewaren ligger i Umbracos PostPipeline, FÖRE autentiseringen — context.User är
                // tom här. Medlemskakan läses därför direkt (dekryptering, ingen databas).
                // ⚠️ Medlemmarnas schema i Umbraco 16 är IdentityConstants.ApplicationScheme
                // ("Identity.Application") — "Umbraco.Members" finns inte (mätt i loggen 2026-10-12).
                var auth = path.StartsWith("/umbraco", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : await Microsoft.AspNetCore.Authentication.AuthenticationHttpContextExtensions
                        .AuthenticateAsync(context, Microsoft.AspNetCore.Identity.IdentityConstants.ApplicationScheme);
                if (auth?.Succeeded == true && auth.Principal != null)
                {
                    var now = DateTime.UtcNow;
                    var idClaim = auth.Principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                    if (int.TryParse(idClaim, out var claimId))
                    {
                        if (activityService.WebUpdateDue(claimId, now))
                            activityService.UpdateActivity(claimId, now);
                    }
                    else
                    {
                        var member = await memberManager.GetCurrentMemberAsync();
                        if (member != null && int.TryParse(member.Id, out var id) && activityService.WebUpdateDue(id, now))
                            activityService.UpdateActivity(id, now);
                    }
                }
            }
            catch (Exception ex)
            {
                // Aktiviteten får aldrig fälla en förfrågan.
                _logger.LogError(ex, "Fel i MemberActivityTrackingMiddleware");
            }

            await _next(context);
        }
    }
}
