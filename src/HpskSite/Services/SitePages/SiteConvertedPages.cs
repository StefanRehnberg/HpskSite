using Microsoft.AspNetCore.Html;
using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Services.SitePages
{
    public enum ConvertedState { Ok, Broken }

    /// <summary>
    /// En omvandlad systemsida (schema 2) så som den ligger publicerad. <see cref="Broken"/> = raden är omvandlad men går inte att tolka:
    /// vyn visar då ett felkort (aldrig standardsidan, som kan visa paneler ägaren dolt — plan v2 beslut 4). <see cref="PageVisibility"/> är sidans
    /// AKTUELLA nivå ur SQL, <see cref="Suspended"/> = nödbromsen (ägarens egna block stoppas, panelerna ritas med sina lagrade nivåer).
    /// </summary>
    public sealed record ConvertedPage(string TemplateKey, ConvertedState State, SiteVisibility PageVisibility, bool Suspended, SiteContentResult? Content);

    /// <summary>En post i en omvandlad sida som vyn ritar: en panel (<see cref="PanelKey"/> satt) eller ett eget block (färdig, kodad <see cref="Html"/>).</summary>
    public sealed record ConvertedItem(string? PanelKey, PanelZone Zone, string Html);

    /// <summary>Det vyn ritar för en omvandlad sida. <see cref="Broken"/> = visa felkortet.</summary>
    public sealed record ConvertedPlan(IReadOnlyList<ConvertedItem> Items, bool Broken);

    /// <summary>
    /// Rena regler för omvandlade sidor (konvertera-planen v2, K3/K4). En omvandlad sidas synlighet i menyn, flikarna och åtkomstprövningen kommer ur
    /// DESS block, inte mallens: sidan syns för den som får se minst ett av blocken. Mallen byts ut per sida mot en kopia vars blocklista är den
    /// omvandlade sidans — då räcker alla befintliga prövningar (<see cref="SiteTemplate.PageVisible"/>, <see cref="SitePageAccessRules"/>, menyn)
    /// utan en egen gren.
    /// </summary>
    public static class SiteConvertedPages
    {
        /// <summary>Blockets verkliga nivå (samma beräkning som <c>SiteBlockRenderer</c>): striktast av blockets och sidans, personblock aldrig Alla.</summary>
        public static SiteVisibility Effective(SiteBlock block, SiteVisibility pageVisibility, string ownerType)
        {
            var level = SiteAccess.Strictest(block.Visibility, pageVisibility);
            return SiteBlockSchema.IsPersonalBlock(block.Type, ownerType, block.Data) ? SiteAccess.CapPersonal(level) : level;
        }

        /// <summary>Sidans definition efter omvandlingen. En sida som inte går att tolka behåller mallens definition (felkortet visas för dem som får se sidan).</summary>
        public static SitePageDef EffectiveDef(SitePageDef def, ConvertedPage page, string ownerType)
        {
            if (page.State != ConvertedState.Ok || page.Content == null) return def;
            var blocks = page.Content.Blocks
                .Where(b => b.Type == "panel" || !page.Suspended)   // nödbromsen stoppar ägarens egna block, inte panelerna
                .Select(b => new SiteBlockDef(b.Id, Effective(b, page.PageVisibility, ownerType)))
                .ToList();
            return def with { Blocks = blocks };
        }

        /// <summary>
        /// Är panelen öppen för ALLA på den omgjorda sidan? Sidan måste vara tolkbar och själv vara öppen för alla, och panelens egen nivå (efter sidan och personuppgiftstaket) vara Alla.
        /// </summary>
        public static bool IsPanelPublic(ConvertedPage page, string ownerType, string panelKey) =>
            page.State == ConvertedState.Ok && page.Content != null && page.PageVisibility == SiteVisibility.Alla
            && page.Content.Blocks.Any(b => b.Type == "panel" && SiteBlockSchema.PanelKeyOf(b.Data) == panelKey && Effective(b, page.PageVisibility, ownerType) == SiteVisibility.Alla);

        /// <summary>Mallen med de omvandlade sidorna utbytta. Inga omvandlade sidor = samma mall (inget kopieras).</summary>
        public static SiteTemplate Apply(SiteTemplate template, IReadOnlyDictionary<string, ConvertedPage> pages)
        {
            if (pages.Count == 0) return template;
            return template with
            {
                Pages = template.Pages.Select(p => !p.IsAdmin && pages.TryGetValue(p.Key, out var c) ? EffectiveDef(p, c, template.OwnerType) : p).ToList(),
            };
        }

        /// <summary>
        /// Vad vyn ritar. Paneler som tittaren inte får se utelämnas helt (<see cref="SiteBlockRenderer.PanelVisible"/> är den enda frågan), likaså egna block som
        /// ger tomt. <c>quicklink-docs</c> ligger i Snabblänkar för den som ser dem; den ritas fristående bara när tittaren inte ser Snabblänkar.
        /// </summary>
        public static ConvertedPlan Plan(ConvertedPage page, SiteRenderContext ctx)
        {
            if (page.State != ConvertedState.Ok || page.Content == null) return new ConvertedPlan(Array.Empty<ConvertedItem>(), true);
            var items = new List<ConvertedItem>();
            foreach (var b in page.Content.Blocks)
            {
                if (b.Type == "panel")
                {
                    if (!SiteBlockRenderer.PanelVisible(b, page.PageVisibility, ctx)) continue;
                    var key = SiteBlockSchema.PanelKeyOf(b.Data)!;
                    var zone = b.Data["zone"]?.GetValue<string>() switch { "main" => PanelZone.Main, "side" => PanelZone.Side, _ => PanelZone.Full };
                    items.Add(new ConvertedItem(key, zone, ""));
                }
                else if (!page.Suspended)
                {
                    var html = SiteBlockRenderer.Render(b, page.PageVisibility, ctx);
                    // Ägarens egna block på Hem kan ligga i huvud- eller sidokolumnen (data.zone); överallt annars hela bredden.
                    var zone = b.Data["zone"]?.GetValue<string>() switch { "main" => PanelZone.Main, "side" => PanelZone.Side, _ => PanelZone.Full };
                    if (html.Length > 0) items.Add(new ConvertedItem(null, zone, html));
                }
            }
            if (ctx.Template.OwnerType == SiteTemplates.Club && items.Any(i => i.PanelKey == "quicklinks"))
                items.RemoveAll(i => i.PanelKey == "quicklink-docs");   // ritas av Snabblänkar
            return new ConvertedPlan(items, false);
        }
    }

    /// <summary>
    /// Läser ägarens omvandlade sidor EN gång per begäran (en SQL-fråga) och bara när egen redigering (brytare 1 och 2) är PÅ: brytare 2 AV = standardsidan.
    /// Delas av menyn, åtkomstfiltret och ritningen så att de alltid är överens.
    /// </summary>
    public sealed class SiteConvertedSource
    {
        private readonly SiteOwnerService _owners;
        private readonly SitePageReader _reader;
        private readonly ILogger<SiteConvertedSource> _logger;
        private readonly Dictionary<string, IReadOnlyDictionary<string, ConvertedPage>> _cache = new();

        public SiteConvertedSource(SiteOwnerService owners, SitePageReader reader, ILogger<SiteConvertedSource> logger)
        {
            _owners = owners;
            _reader = reader;
            _logger = logger;
        }

        private static readonly IReadOnlyDictionary<string, ConvertedPage> None = new Dictionary<string, ConvertedPage>();

        /// <summary>Ägarens omvandlade sidor per mallnyckel. Tomt när brytare 2 är AV, när inget är omvandlat eller när läsningen misslyckas (loggat).</summary>
        public IReadOnlyDictionary<string, ConvertedPage> Pages(string ownerType, int ownerId)
        {
            if (!_owners.IsEditingEnabled(ownerType, ownerId)) return None;
            var key = SiteOwnerService.Key(ownerType, ownerId);
            if (_cache.TryGetValue(key, out var cached)) return cached;
            IReadOnlyDictionary<string, ConvertedPage> loaded;
            try { loaded = _reader.ConvertedPages(ownerType, ownerId); }
            catch (Exception ex) { _logger.LogError(ex, "Omvandlade sidor för {Key} kunde inte läsas.", key); loaded = None; }
            _cache[key] = loaded;
            return loaded;
        }

        public SiteTemplate Template(string ownerType, int ownerId) => SiteConvertedPages.Apply(SiteTemplates.For(ownerType), Pages(ownerType, ownerId));
    }

    /// <summary>
    /// Grinden för "Visa publikt" (steg 2d): är panelen <paramref name="panelKey"/> på ägarens sida PUBLICERAD för alla, även utloggade? En panels dataanrop (banor, kalender, rekord)
    /// frågar HIT innan den svarar en utloggad; svaret är nej om egen redigering är AV, sidan inte är omgjord, inte går att tolka, eller panelen inte är öppen för alla. Aldrig ett undantag: fel = nej.
    /// </summary>
    public sealed class SitePaneGate
    {
        private readonly SiteConvertedSource _source;
        private readonly Umbraco.Cms.Core.Services.IContentService _content;

        public SitePaneGate(SiteConvertedSource source, Umbraco.Cms.Core.Services.IContentService content)
        {
            _source = source;
            _content = content;
        }

        public bool IsPublic(string ownerType, int ownerId, string pageKey, string panelKey)
        {
            if (ownerId <= 0) return false;
            try { return _source.Pages(ownerType, ownerId).TryGetValue(pageKey, out var page) && SiteConvertedPages.IsPanelPublic(page, ownerType, panelKey); }
            catch { return false; }
        }

        /// <summary>Samma fråga för en krets, som anropen pekar ut med kretskoden.</summary>
        public bool IsPublicForRegionCode(string regionCode, string pageKey, string panelKey)
        {
            if (string.IsNullOrWhiteSpace(regionCode)) return false;
            try
            {
                var root = _content.GetRootContent().FirstOrDefault();
                if (root == null) return false;
                var region = _content.GetPagedChildren(root.Id, 0, int.MaxValue, out _)
                    .FirstOrDefault(c => c.ContentType.Alias == "regionalPage" && (c.GetValue<string>("regionCode") ?? "").Equals(regionCode, StringComparison.OrdinalIgnoreCase));
                return region != null && IsPublic(SiteTemplates.Region, region.Id, pageKey, panelKey);
            }
            catch { return false; }
        }
    }

    /// <summary>Ritar en omvandlad sida ur sina block (efter att <c>_SitePanels</c> frågat). Null = sidan är inte omvandlad: standardlistan ritas.</summary>
    public sealed class SiteConvertedRenderer
    {
        private readonly SiteConvertedSource _source;
        private readonly SiteBlockDataFactory _data;
        private readonly ILogger<SiteConvertedRenderer> _logger;

        public SiteConvertedRenderer(SiteConvertedSource source, SiteBlockDataFactory data, ILogger<SiteConvertedRenderer> logger)
        {
            _source = source;
            _data = data;
            _logger = logger;
        }

        public ConvertedPlan? Plan(SitePanelContext ctx, string ownerUrl)
        {
            if (!_source.Pages(ctx.OwnerType, ctx.OwnerId).TryGetValue(ctx.PageKey, out var page)) return null;
            if (page.State == ConvertedState.Broken)
            {
                _logger.LogError("Den omvandlade sidan {Owner}/{Page} går inte att tolka — visar felkort.", SiteOwnerService.Key(ctx.OwnerType, ctx.OwnerId), ctx.PageKey);
                return new ConvertedPlan(Array.Empty<ConvertedItem>(), true);
            }
            try
            {
                var rctx = new SiteRenderContext(ctx.Viewer, ownerUrl, SiteTemplates.For(ctx.OwnerType), _data.For(ctx.OwnerType, ctx.OwnerId, ctx.Node, ctx.Viewer));
                return SiteConvertedPages.Plan(page, rctx);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Den omvandlade sidan {Owner}/{Page} kunde inte ritas — visar felkort.", SiteOwnerService.Key(ctx.OwnerType, ctx.OwnerId), ctx.PageKey);
                return new ConvertedPlan(Array.Empty<ConvertedItem>(), true);
            }
        }
    }
}
