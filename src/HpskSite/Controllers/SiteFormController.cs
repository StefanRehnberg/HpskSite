using System.Security.Cryptography;
using System.Text;
using HpskSite.Services.Addresses;
using HpskSite.Services.Hosting;
using HpskSite.Services.Security;
using HpskSite.Services.SitePages;
using Microsoft.AspNetCore.Mvc;
using Umbraco.Cms.Core.Cache;
using Umbraco.Cms.Core.Logging;
using Umbraco.Cms.Core.Routing;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Web.Website.Controllers;
using Umbraco.Extensions;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Tar emot en anmälan från blocket Anmälan/ansökan på en egen sida (klubbsidorna). <b>Öppet för vem som helst som får se blocket</b> — oftast utan konto — så allt här
    /// är byggt för att inte lita på något från besökaren:
    /// <list type="bullet">
    /// <item>Sidan och blocket slås upp ur den PUBLICERADE sidan; bara sidans id och blockets id kommer från besökaren. Formulärets fält, val och datum läses ur blocket, aldrig ur anropet.</item>
    /// <item>Blocket måste vara synligt för just den här besökaren (<see cref="SiteBlockRenderer.BlockVisible"/>, samma regel som ritningen): ett dolt eller stängt block svarar som om det inte fanns (404).</item>
    /// <item>Skyddstoken (antiforgery), ett honungsfält, och spärrar i databasen (<see cref="RateCounterService"/>) per uppkoppling, formulär och ägare, räknade vid FÖRSÖKET före all validering.</item>
    /// <item>Bara det blocket bad om lagras. Ingenting loggas om den som anmäler sig. Samma tack ges vid en dubblett (ingen kan pröva vilka adresser som finns).</item>
    /// </list>
    /// </summary>
    public class SiteFormController : SurfaceController
    {
        public const int PerIpPerHour = 6;
        public const int PerFormPerDay = 400;
        public const int PerOwnerPerDay = 1000;

        private readonly SitePageReader _reader;
        private readonly SiteOwnerService _owners;
        private readonly SiteViewerResolver _viewers;
        private readonly IScopeProvider _scopes;
        private readonly RateCounterService _rate;
        private readonly IContentService _content;
        private readonly ILogger<SiteFormController> _logger;

        public SiteFormController(IUmbracoContextAccessor umbracoContextAccessor, IUmbracoDatabaseFactory databaseFactory, ServiceContext services,
            AppCaches appCaches, IProfilingLogger profilingLogger, IPublishedUrlProvider publishedUrlProvider,
            SitePageReader reader, SiteOwnerService owners, SiteViewerResolver viewers, IScopeProvider scopes, RateCounterService rate, IContentService content, ILogger<SiteFormController> logger)
            : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
        {
            _reader = reader; _owners = owners; _viewers = viewers; _scopes = scopes; _rate = rate; _content = content; _logger = logger;
        }

        public sealed class SubmitModel
        {
            public int PageId { get; set; }
            public string? BlockId { get; set; }
            public string? Name { get; set; }
            public string? Email { get; set; }
            public string? Phone { get; set; }
            public string? Gender { get; set; }
            public string? PersonalNumber { get; set; }
            public string? Address { get; set; }
            public string? PostalCode { get; set; }
            public string? City { get; set; }
            public string? Message { get; set; }
            public string? Choice { get; set; }
            public bool Consent { get; set; }
            /// <summary>Honungsfältet: en människa ser det aldrig och fyller aldrig i det.</summary>
            public string? Website { get; set; }
        }

        /// <summary>Samma sätt som ekonomienkäten läser avsändarens IP: prod ligger bakom en proxy, så <c>X-Forwarded-For</c> först.</summary>
        private string? ClientIp()
        {
            var forwarded = Request.Headers["X-Forwarded-For"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(forwarded)) return forwarded.Split(',')[0].Trim();
            return HttpContext.Connection.RemoteIpAddress?.ToString();
        }

        /// <summary>Spärrnyckeln bär en hash av IP-adressen, aldrig adressen själv (ingen IP-adress ligger kvar i räknartabellen).</summary>
        internal static string IpKey(string ip) => "siteform:ip:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("siteform|" + ip)))[..16];

        [HttpPost]
        [ValidateAntiForgeryToken]
        [PublicEndpoint("Anmälningsformulär på en egen sida: öppet för den som får se blocket (prövas mot den publicerade sidan), med skyddstoken, honungsfält och spärrar; bara blockets egna fält lagras")]
        public async Task<IActionResult> Submit([FromForm] SubmitModel m)
        {
            try
            {
                if (m == null || m.PageId <= 0 || string.IsNullOrEmpty(m.BlockId) || m.BlockId.Length > 12) return NotFound();
                (string OwnerType, int OwnerId)? owner;
                string? slug;
                using (var scope = _scopes.CreateScope(autoComplete: true))
                {
                    owner = SitePageStore.OwnerOfFreePage(scope.Database, m.PageId);
                    slug = owner == null ? null : scope.Database.FirstOrDefault<string>("SELECT TOP 1 Slug FROM dbo.PageAddress WHERE TargetKind = @0 AND TargetId = @1 AND IsCurrent = 1", PageAddressKinds.SitePage, m.PageId);
                }
                if (owner == null) return NotFound();
                var (ownerType, ownerId) = owner.Value;
                if (!_owners.IsEditingEnabled(ownerType, ownerId)) return NotFound();   // brytare 2 av: ytan finns inte

                var loaded = _reader.LoadPublished(ownerType, ownerId, m.PageId);        // null = stoppad, ej publicerad eller ogiltig
                if (loaded == null) return NotFound();
                var block = loaded.Value.Content.Blocks.FirstOrDefault(b => b.Id == m.BlockId && b.Type == "signup");
                if (block == null) return NotFound();

                string? regionCode = ownerType == SiteTemplates.Region ? _content.GetById(ownerId)?.GetValue<string>("regionCode") : null;
                var viewer = await _viewers.ResolveAsync(ownerType, ownerId, regionCode);
                if (!SiteBlockRenderer.BlockVisible(block, loaded.Value.Page.VisibilityLevel, ownerType, viewer)) return NotFound();   // dolt för den här besökaren: som om det inte fanns

                var ownerUrl = UmbracoContext.Content?.GetById(ownerId)?.Url() ?? "/";
                if (!ownerUrl.EndsWith('/')) ownerUrl += "/";
                var pageUrl = slug == null ? ownerUrl : ownerUrl + slug + "/";
                IActionResult Back(bool ok, string? code = null) =>
                    Redirect($"{pageUrl}?anmalan={m.BlockId}&r={(ok ? "ok" : "err")}{(code == null ? "" : "&e=" + code)}#{m.BlockId}-anmalan");

                // Honungsfältet först: en robot får ett tyst tack och äter inte upp en riktig besökares kvot.
                if (!string.IsNullOrWhiteSpace(m.Website)) return Back(true);

                // Spärrarna efter fällan men FÖRE all validering och skrivning, räknade vid försöket (inte vid en lyckad sparning).
                var ip = ClientIp();
                if (!string.IsNullOrWhiteSpace(ip) && _rate.Increment(IpKey(ip), TimeSpan.FromHours(1)) > PerIpPerHour) return Back(false, "rate");
                if (_rate.Increment($"siteform:form:{m.PageId}:{m.BlockId}", TimeSpan.FromDays(1)) > PerFormPerDay) return Back(false, "rate");
                if (_rate.Increment($"siteform:owner:{ownerType}:{ownerId}", TimeSpan.FromDays(1)) > PerOwnerPerDay) return Back(false, "rate");

                var cfg = SiteSignupConfig.FromBlock(block);
                var check = SiteSignupRules.Validate(cfg, new SiteSignupInput(m.Name, m.Email, m.Phone, m.Gender, m.PersonalNumber, m.Address, m.PostalCode, m.City, m.Message, m.Choice, m.Consent), DateTime.Today);
                if (!check.Ok) return Back(false, check.Errors.Any(e => e.Field == "") ? "closed" : "invalid");

                SiteSignupInsert result;
                using (var scope = _scopes.CreateScope())
                {
                    var now = DateTime.Now;
                    result = SiteSignupStore.Insert(scope.Database, ownerType, ownerId, m.PageId, m.BlockId, cfg.Title, check.Data!, now, out var id);
                    if (result == SiteSignupInsert.Ok)
                    {
                        SitePageWriter.Audit(scope.Database, ownerType, ownerId, new SiteActor(null), "form", id, "entry-new", null, now);
                        scope.Complete();
                    }
                }
                return result switch
                {
                    SiteSignupInsert.Ok or SiteSignupInsert.Duplicate => Back(true),   // samma tack vid en dubblett
                    SiteSignupInsert.Full => Back(false, "full"),
                    _ => NotFound(),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "En anmälan kunde inte tas emot (sida {PageId})", m?.PageId);   // ingenting om den som anmäler sig
                return NotFound();
            }
        }
    }
}
