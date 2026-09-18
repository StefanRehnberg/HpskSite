using HpskSite.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Web;

namespace HpskSite.Services.Ledger
{
    /// <summary>
    /// Svarar på <b>vem som är utställare</b> för något som ska betalas: en klubb eller en krets, som
    /// ett <c>(typ, id)</c>-par.
    ///
    /// <para><b>⚠️⚠️ DET HÄR ÄR VAD SOM BLOCKERADE EVENEMANGSAVGIFTEN.</b> Den gamla fakturan var hårt
    /// knuten till en TÄVLING — <c>ReceiptModelBuilder.Build</c> returnerade null utan
    /// <c>competitionId</c>, och utställare, Swish och bankgiro lästes ur tävlingen. En faktura för
    /// ett klubbevenemang renderade därför ingenting och var osynlig för varje fakturayta. Och
    /// eftersom klubbens OCH kretsens evenemang delar doctype (<c>clubSimpleEvent</c>) räcker det
    /// inte med ett andra <c>clubId</c>-fält — det bara flyttar problemet. Ägaren måste vara ett par,
    /// och det paret måste resolvas på ETT ställe.</para>
    ///
    /// <para><b>⚠️ TVÅ HELT OLIKA VÄGAR, och det är inte en skönhetsfråga.</b> Ett evenemang är ett
    /// BARN till sin klubb eller krets, så där svarar trädet. En tävling ligger under
    /// tävlingsnavet och har ingen sådan förälder — där avgör egenskaperna
    /// (<c>clubId</c>, annars <c>regionalFederation</c>). Använder man trädet på en tävling får man
    /// tävlingsnavet; använder man egenskaperna på ett evenemang finns de inte.</para>
    ///
    /// <para><b>⚠️ Använder <see cref="IUmbracoContextFactory"/>, inte
    /// <c>IUmbracoContextAccessor</c>.</b> Betalningar bekräftas också från bakgrundsarbete, och
    /// <c>GetRequiredUmbracoContext()</c> kastar på en bakgrundstråd — exakt det som gjorde att den
    /// ivriga fakturan tyst aldrig skapades 2026-06-12.</para>
    /// </summary>
    public class LedgerIssuerResolver
    {
        private readonly IUmbracoContextFactory _contextFactory;
        private readonly ILogger<LedgerIssuerResolver> _logger;

        public LedgerIssuerResolver(
            IUmbracoContextFactory contextFactory,
            ILogger<LedgerIssuerResolver> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        /// <summary>Utställaren, eller null när den inte går att avgöra.</summary>
        public readonly record struct Issuer(int Type, int Id, string Name);

        /// <summary>
        /// Utställaren för ett evenemang (<c>clubSimpleEvent</c>) — alltså dess FÖRÄLDERNOD.
        /// <para>Klubbens och kretsens evenemang delar doctype, så föräldern är det enda som
        /// skiljer dem. Den här metoden är hela skälet att evenemangsavgiften nu går att bygga.</para>
        /// </summary>
        public Issuer? ResolveForEvent(int eventNodeId)
        {
            using var cref = _contextFactory.EnsureUmbracoContext();
            var content = cref.UmbracoContext.Content;
            if (content is null) return null;

            var node = content.GetById(eventNodeId);
            if (node is null)
            {
                _logger.LogWarning("Evenemangsnod {Id} hittades inte — utställaren kan inte avgöras.", eventNodeId);
                return null;
            }

            // Klättra uppåt: ett evenemang ligger direkt under sin klubb eller krets, men en
            // mellannod i framtiden ska inte göra uppslagningen fel.
            for (var n = node.Parent; n is not null; n = n.Parent)
            {
                var issuer = FromNode(n);
                if (issuer is not null) return issuer;
            }

            _logger.LogWarning(
                "Evenemang {Id} har ingen klubb eller krets bland sina föräldrar — ingen utställare.",
                eventNodeId);
            return null;
        }

        /// <summary>
        /// Utställaren för en tävling: <c>clubId</c> först, annars <c>regionalFederation</c>.
        /// <para><b>⚠️ BÅDA VÄRDFORMERNA.</b> Ett SM är kretsvärdat (<c>clubId</c> tomt), och en
        /// uppslagning som bara läser <c>clubId</c> låser ute den arrangerande kretsen från sin egen
        /// tävling. Det felet är gjort fyra gånger i den här kodbasen.</para>
        /// </summary>
        public Issuer? ResolveForCompetition(int competitionId)
        {
            using var cref = _contextFactory.EnsureUmbracoContext();
            var content = cref.UmbracoContext.Content;
            if (content is null) return null;

            var comp = content.GetById(competitionId);
            if (comp is null) return null;

            var clubId = comp.Value<int>("clubId");
            if (clubId > 0)
            {
                var club = content.GetById(clubId);
                var fromClub = club is null ? null : FromNode(club);
                if (fromClub is not null) return fromClub;

                _logger.LogWarning(
                    "Tävling {Id} pekar på klubb {ClubId} som inte gick att resolva.", competitionId, clubId);
            }

            // ⚠️ Läses UNTYPED. competitionScope och dess släktingar kan bära ett rent strängvärde
            // från äldre kodvägar, och FlexibleDropdown-konverteraren kastar då. Samma försiktighet
            // som URL-provideren tillämpar.
            var regionCode = comp.Value("regionalFederation")?.ToString();
            if (!string.IsNullOrWhiteSpace(regionCode))
            {
                var region = LookupRegionByCode(content, regionCode!);
                if (region is not null) return FromNode(region);
            }

            _logger.LogWarning(
                "Tävling {Id} har varken klubb eller krets — ingen utställare, alltså ingen betalning.",
                competitionId);
            return null;
        }

        /// <summary>Utställaren direkt ur en klubb- eller kretsnod.</summary>
        public Issuer? ResolveForNode(int nodeId)
        {
            using var cref = _contextFactory.EnsureUmbracoContext();
            var content = cref.UmbracoContext.Content;
            var node = content?.GetById(nodeId);
            return node is null ? null : FromNode(node);
        }

        private static Issuer? FromNode(IPublishedContent node) => node.ContentType.Alias switch
        {
            // ⚠️ Klubbens namn ligger i egenskapen clubName, inte i nodnamnet — "finns inte i dev"
            // har ljugit förut just av det skälet.
            "club" => new Issuer(DocumentOwnerType.Club, node.Id,
                                 node.Value<string>("clubName") ?? node.Name ?? ""),
            "regionalPage" => new Issuer(DocumentOwnerType.Region, node.Id, node.Name ?? ""),
            _ => null
        };

        private static IPublishedContent? LookupRegionByCode(
            Umbraco.Cms.Core.PublishedCache.IPublishedContentCache content, string regionCode)
        {
            var root = content.GetAtRoot().FirstOrDefault();
            return root?.Children.FirstOrDefault(c =>
                c.ContentType.Alias == "regionalPage" &&
                string.Equals(c.Value<string>("regionCode") ?? "", regionCode, StringComparison.OrdinalIgnoreCase));
        }
    }
}
