using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Addresses
{
    /// <summary>
    /// Bygger en sidas publika adress ur adresstabellen: ägarens adress + sortens segment + år + slug.
    /// Ägarens adress läses varje gång ur den publicerade cachen, så att en klubb som byter adress tar alla
    /// sina sidor med sig.
    ///
    /// <para><b>Sorten står i adressen</b> (Stefans beslut 2026-10-18): <c>/halland/haaplinge-goass/tavlingar/2026/x/</c>,
    /// <c>…/handelser/2026/x/</c>, <c>…/serier/2026/x/</c>. Förbundets mästerskap (<c>/sm/2026/x/</c>) har inget
    /// sortsegment — där finns bara tävlingar och serier, och de delar namnrymd.</para>
    /// </summary>
    public class PageAddressUrls
    {
        public const string CompetitionsListPath = "/tavlingar/";

        private readonly PageAddressService _addresses;
        private readonly IUmbracoContextAccessor _ctx;

        public PageAddressUrls(PageAddressService addresses, IUmbracoContextAccessor ctx)
        {
            _addresses = addresses;
            _ctx = ctx;
        }

        /// <summary>Den aktuella adressen, eller null när sidan saknar adress (eller ägaren inte går att läsa).</summary>
        public string? UrlFor(string kind, int id)
        {
            var row = _addresses.Current(kind, id);
            return row == null ? null : UrlOf(row);
        }

        public string? UrlOf(PageAddressRow row)
        {
            var prefix = OwnerPrefix(row.Owner);
            return prefix == null ? null : PageAddressRules.Compose(prefix, row.Owner.IsNational, row.TargetKind, row.UrlYear, row.Slug);
        }

        /// <summary>"/halland/haaplinge-goass/", "/halland/", "/sm/" — med snedstreck sist. Null = går inte att läsa.</summary>
        public string? OwnerPrefix(PageAddressOwner owner)
        {
            if (owner.IsNational) return "/" + owner.Type.ToLowerInvariant() + "/";
            if (!_ctx.TryGetUmbracoContext(out var uc) || uc.Content == null) return null;
            var node = uc.Content.GetById(owner.Id);
            var expected = owner.Type == PageAddressOwner.Region ? "regionalPage" : "club";
            if (node?.ContentType.Alias != expected) return null;
            var url = node.Url();
            if (string.IsNullOrEmpty(url) || url == "#") return null;
            return url.EndsWith('/') ? url : url + "/";
        }

        /// <summary>Ägaren bakom en nod (klubb eller krets), eller null.</summary>
        public static PageAddressOwner? OwnerOf(IPublishedContent? node) => node?.ContentType.Alias switch
        {
            "club" => new PageAddressOwner(PageAddressOwner.Club, node.Id),
            "regionalPage" => new PageAddressOwner(PageAddressOwner.Region, node.Id),
            _ => null,
        };
    }
}
