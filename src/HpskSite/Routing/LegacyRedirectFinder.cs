using HpskSite.Services.Addresses;
using HpskSite.Services.Competitions;
using HpskSite.Services.Events;
using HpskSite.Services.StartLists;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Umbraco.Cms.Core.Composing;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Routing;
using Umbraco.Extensions;

namespace HpskSite.Routing
{
    /// <summary>
    /// En gammal adress ur <c>dbo.LegacyRedirect</c> (Umbracos omdirigering, kopierad) → 301 till sidans nya adress
    /// (C3-planen §5.5 punkt 2). Sist i kedjan: har Umbraco redan svarat (målnoden publicerad) körs den aldrig.
    /// </summary>
    public class LegacyRedirectFinder : IContentFinder
    {
        private readonly LegacyRedirectService _redirects;
        private readonly IHttpContextAccessor _http;

        public LegacyRedirectFinder(LegacyRedirectService redirects, IHttpContextAccessor http)
        {
            _redirects = redirects;
            _http = http;
        }

        public Task<bool> TryFindContent(IPublishedRequestBuilder request)
        {
            try
            {
                var path = request.Uri.GetAbsolutePathDecoded();
                var row = _redirects.Find(path);
                var sp = _http.HttpContext?.RequestServices;
                if (row == null || sp == null) return Task.FromResult(false);
                var target = Target(row, sp);
                if (string.IsNullOrEmpty(target) || target == "#") return Task.FromResult(false);
                if (LegacyRedirectService.Normalize(target) == LegacyRedirectService.Normalize(path)) return Task.FromResult(false);
                request.SetRedirectPermanent(target + request.Uri.Query);
                return Task.FromResult(true);
            }
            catch
            {
                // En adress vi inte kan avgöra är inte vår.
                return Task.FromResult(false);
            }
        }

        /// <summary>Sidans nya adress, eller null när målet inte längre finns (då svarar sajten 404 som förut).</summary>
        internal static string? Target(LegacyRedirectService.Row row, IServiceProvider sp)
        {
            var links = sp.GetRequiredService<CompetitionLinks>();
            var competitions = sp.GetRequiredService<ICompetitionStore>();
            switch (row.TargetKind)
            {
                case var k when k == Models.Competitions.CompetitionFieldTable.NodeAlias:
                    return competitions.Get(row.TargetId) == null ? null : links.Url(row.TargetId);
                case var k when k == Models.Competitions.CompetitionSeriesFieldTable.NodeAlias:
                    var series = links.SeriesUrl(row.TargetId);
                    return string.IsNullOrEmpty(series) ? null : series;
                case LegacyRedirectService.ResultKind:
                    if (row.CompetitionId is not { } compId || competitions.Get(compId) == null) return null;
                    var url = links.Url(compId);
                    return (url.EndsWith('/') ? url : url + "/") + "resultat/";
                case LegacyRedirectService.QualifyingStartListKind:
                case LegacyRedirectService.FinalsStartListKind:
                    var list = sp.GetRequiredService<IStartListStore>().Get(row.TargetId);
                    if (list == null || competitions.Get(list.CompetitionId) == null) return null;
                    return list.PublicUrl(links.Url(list.CompetitionId));
                case LegacyRedirectService.EventKind:
                    var ev = sp.GetRequiredService<SqlClubEventStore>().Get(row.TargetId);
                    return ev is { IsPublished: true } && !string.IsNullOrEmpty(ev.Url) ? ev.Url : null;
                default:
                    return null;
            }
        }
    }

    public class LegacyRedirectComposer : IComposer
    {
        public void Compose(IUmbracoBuilder builder)
        {
            builder.Services.AddSingleton<LegacyRedirectService>();
            builder.Services.AddHostedService<LegacyRedirectCopyHostedService>();
            builder.ContentFinders().Append<LegacyRedirectFinder>();
        }
    }
}
