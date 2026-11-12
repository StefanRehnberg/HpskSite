using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Net.Http.Headers;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Förhandsläget i Hemsidan ("Förhandsvisa" under en besökare i tabellen Vem ser sidan?): en flik som öppnar den RIKTIGA sidan som en annan besökare. Biljetten är
    /// signerad (DataProtection), gäller en halvtimme, är knuten till den admin som bad om den och till en förening, och bär vilken besökare det gäller.
    /// <para><c>Persona</c> är en av <see cref="SitePagePreview"/>s fasta tittare. <c>anon</c> = fliken behandlas som utloggad i alla avseenden (sajten ser ingen inloggning alls).
    /// Övriga besökare gäller bara en egen sida (<c>PagePath</c>): den ritas på servern för den tittaren, och inget annat får öppnas.</para>
    /// </summary>
    public sealed record SitePreviewTicket(string OwnerType, int OwnerId, string Persona, string OwnerPath, string? PagePath, string Member, long ExpiresUnix);

    /// <summary>
    /// Vad ett förhandsläge får göra. Ren kod (inga HTTP-typer) så att varje regel kan provas: bara läsning (GET/HEAD), bara föreningens egna sidor (eller, för en annan besökare än
    /// utloggad, bara EN sida) och en kort lista med läsande dataanrop som sidans paneler behöver. Allt annat, till exempel Ekonomi, Styrelsearbete, Hemsidan och adminsidorna, är
    /// förbjudet. Förbudet gäller även under föreningens adress.
    /// </summary>
    public static class SitePreviewPolicy
    {
        public const string QueryKey = "forhandsvisa";
        public const string HeaderKey = "X-Hpsk-Forhandsvisa";
        public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

        /// <summary>Adressdelar som aldrig får öppnas i förhandsläget, var de än ligger.</summary>
        private static readonly HashSet<string> DeniedSegments = new(StringComparer.OrdinalIgnoreCase)
        {
            "ekonomi", "styrelse", "hemsidan", "admin", "admin-page", "tavlingsplanering", "umbraco", "api", "hubs", "bemanna", "login-register", "user-profile-page",
        };

        /// <summary>Läsande dataanrop som förhandsvisade sidors paneler och bilder använder (utloggade sidor: de fyra paneler som kan visas publikt, plus bilder).</summary>
        private static readonly HashSet<string> DataEndpoints = new(StringComparer.OrdinalIgnoreCase)
        {
            "/umbraco/surface/club/getupcomingevents",
            "/umbraco/surface/shootingrange/getclubranges",
            "/umbraco/surface/competitionrecords/listforclub",
            "/umbraco/surface/competitionrecords/listforregion",
            "/umbraco/surface/competitionrecords/history",
            "/umbraco/surface/sitefile/image",
        };

        public static bool MethodAllowed(string method) =>
            string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) || string.Equals(method, "HEAD", StringComparison.OrdinalIgnoreCase);

        private static string Normalize(string? path)
        {
            var p = (path ?? "").Trim().ToLowerInvariant();
            if (!p.StartsWith('/')) p = "/" + p;
            return p.Length > 1 ? p.TrimEnd('/') : p;
        }

        /// <summary>Förbjudna tecken i en adress: bakåtsteg, kodade tecken, bakstreck och dubbla snedstreck kan inte användas för att ta sig ut ur föreningens adress.</summary>
        private static bool Suspicious(string path) =>
            path.Contains("..") || path.Contains('%') || path.Contains('\\') || path.Contains("//") || path.Any(char.IsControl);

        public static bool PathAllowed(SitePreviewTicket t, string? rawPath)
        {
            if (string.IsNullOrEmpty(rawPath) || Suspicious(rawPath)) return false;
            var path = Normalize(rawPath);
            if (DataEndpoints.Contains(path)) return true;
            if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).Any(DeniedSegments.Contains)) return false;
            var owner = Normalize(t.OwnerPath);
            if (t.Persona == "anon") return path == owner || path.StartsWith(owner + "/", StringComparison.Ordinal);
            return t.PagePath != null && path == Normalize(t.PagePath);   // en annan besökare än utloggad: bara den här sidan
        }
    }

    /// <summary>Skapar och läser förhandsbiljetter. Purpose-strängen byts aldrig (en gammal biljett skulle sluta gälla, men en ny biljett får aldrig kunna läsas som en annan sort).</summary>
    public sealed class SitePreviewTickets
    {
        private const string Purpose = "SitePreview.v1";
        private readonly IDataProtector _protector;

        public SitePreviewTickets(IDataProtectionProvider provider) => _protector = provider.CreateProtector(Purpose);

        public string Issue(string ownerType, int ownerId, string persona, string ownerPath, string? pagePath, string member, DateTimeOffset now) =>
            _protector.Protect(JsonSerializer.Serialize(new SitePreviewTicket(ownerType, ownerId, persona, ownerPath, pagePath, member, now.Add(SitePreviewPolicy.Lifetime).ToUnixTimeSeconds())));

        /// <summary>Biljetten, eller null: förfalskad, ändrad, utgången eller utfärdad åt någon annan än den som är inloggad nu.</summary>
        public SitePreviewTicket? Read(string? token, string? currentMember, DateTimeOffset now)
        {
            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(currentMember) || token.Length > 2048) return null;
            try
            {
                var t = JsonSerializer.Deserialize<SitePreviewTicket>(_protector.Unprotect(token));
                if (t == null || t.ExpiresUnix < now.ToUnixTimeSeconds()) return null;
                if (!string.Equals(t.Member, currentMember, StringComparison.Ordinal)) return null;
                return t;
            }
            catch
            {
                return null;   // ogiltig signatur, trasig text eller fel sort
            }
        }
    }

    /// <summary>Ger förhandsläget till resten av appen via <c>HttpContext.Items</c> (sätts bara av <see cref="SitePreviewMiddleware"/> efter att biljetten godkänts).</summary>
    public static class SitePreviewContext
    {
        private const string TicketKey = "HpskSite.SitePreview";
        private const string TokenKey = "HpskSite.SitePreview.Token";

        public static void Set(HttpContext http, SitePreviewTicket ticket, string token) { http.Items[TicketKey] = ticket; http.Items[TokenKey] = token; }
        public static SitePreviewTicket? Current(HttpContext? http) => http?.Items[TicketKey] as SitePreviewTicket;
        public static string? Token(HttpContext? http) => http?.Items[TokenKey] as string;
    }

    /// <summary>
    /// Körs FÖRE Umbracos kedja. Utan biljett rör den ingenting. Med biljett: den måste vara giltig för just den inloggade, metoden måste vara läsande och adressen tillåten, annars
    /// stoppas begäran (403/405) — aldrig släpps den igenom som admin. För utloggad förhandsvisning tas kakorna bort ur begäran, så sajten ser en riktig utloggad besökare. Svaret
    /// får aldrig sätta kakor i adminens webbläsare (en ny skyddskaka här skulle ersätta adminens egen) och cachas inte.
    /// </summary>
    public sealed class SitePreviewMiddleware
    {
        private readonly RequestDelegate _next;
        public SitePreviewMiddleware(RequestDelegate next) => _next = next;

        public async Task InvokeAsync(HttpContext ctx, SitePreviewTickets tickets)
        {
            var token = ctx.Request.Query[SitePreviewPolicy.QueryKey].FirstOrDefault() ?? ctx.Request.Headers[SitePreviewPolicy.HeaderKey].FirstOrDefault();
            if (string.IsNullOrEmpty(token)) { await _next(ctx); return; }

            var ticket = tickets.Read(token, CookieMember(ctx), DateTimeOffset.UtcNow);
            if (ticket == null) { await Refuse(ctx, 403, "Förhandsvisningen har gått ut eller gäller inte för dig. Öppna den på nytt från Hemsidan."); return; }
            if (!SitePreviewPolicy.MethodAllowed(ctx.Request.Method)) { await Refuse(ctx, 405, "Förhandsvisningen går bara att läsa. Inget kan ändras här."); return; }
            if (!SitePreviewPolicy.PathAllowed(ticket, ctx.Request.Path.Value)) { await Refuse(ctx, 403, "Förhandsvisningen visar bara den här sidan. Stäng fliken och öppna Hemsidan igen."); return; }

            SitePreviewContext.Set(ctx, ticket, token);
            if (ticket.Persona == "anon") ctx.Request.Headers.Remove(HeaderNames.Cookie);   // en riktig utloggad besökare: ingen inloggning syns för något i sajten
            ctx.Response.OnStarting(() =>
            {
                ctx.Response.Headers.Remove(HeaderNames.SetCookie);   // aldrig kakor till adminens webbläsare
                ctx.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
                ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                ctx.Response.Headers["Referrer-Policy"] = "same-origin";
                return Task.CompletedTask;
            });
            await _next(ctx);
        }

        /// <summary>
        /// Vem är inloggad, ur medlemskakan direkt (dekryptering, ingen databas). ⚠️ Aldrig <c>HttpContext.AuthenticateAsync</c> här: det cachar resultatet för hela begäran, så en
        /// utloggad förhandsvisning som tar bort kakan efteråt skulle ändå ses som inloggad av allt som körs senare (Umbraco, vyerna, dataanropen).
        /// </summary>
        private static string? CookieMember(HttpContext ctx)
        {
            try
            {
                var options = ctx.RequestServices.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationOptions>>()
                    .Get(IdentityConstants.ApplicationScheme);
                var raw = options.CookieManager.GetRequestCookie(ctx, options.Cookie.Name!);
                if (string.IsNullOrEmpty(raw)) return null;
                var ticket = options.TicketDataFormat.Unprotect(raw, null);
                if (ticket?.Principal?.Identity?.IsAuthenticated != true) return null;
                if (ticket.Properties.ExpiresUtc is { } expires && expires < DateTimeOffset.UtcNow) return null;
                return ticket.Principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            }
            catch { return null; }
        }

        private static async Task Refuse(HttpContext ctx, int status, string message)
        {
            ctx.Response.StatusCode = status;
            ctx.Response.Headers[HeaderNames.CacheControl] = "private, no-store";
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync(message);
        }
    }
}
