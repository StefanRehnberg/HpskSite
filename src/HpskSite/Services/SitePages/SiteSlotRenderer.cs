using Umbraco.Cms.Core.Models.PublishedContent;

namespace HpskSite.Services.SitePages
{
    /// <summary>Allt en slot-rendering behöver veta om begäran: ägaren, tittaren (byggd av vyn, som redan räknat ut roller), adressen och ägarens nod (kontaktblock).</summary>
    public sealed record SiteSlotContext(string OwnerType, int OwnerId, SiteViewer Viewer, string OwnerUrl, IPublishedContent? OwnerNode);

    /// <summary>
    /// Klubbens egen text före och efter en systemsidas innehåll (slots, klubbsidorna 2b, plan v3 D3). Läser ägarens PUBLICERADE mallkopia för systemsidan och renderar
    /// blocken i rätt läge med samma <see cref="SiteBlockRenderer"/> som alla andra block: synligheten prövas för tittaren vid varje rendering, och en lagrad rad är data från
    /// en tidigare kodversion (innehållet tolkas om mot kopians aktuella synlighet). Finns ingen kopia, eller ingen publicerad, ger det tomt — systemsidan är då exakt som förut.
    /// Ett fel i läsningen ger också tomt (aldrig en trasig panel). Per begäran: kopian läses en gång även när före och efter båda ritas.
    /// </summary>
    public sealed class SiteSlotRenderer
    {
        private readonly SitePageReader _reader;
        private readonly SiteBlockDataFactory _data;
        private readonly ILogger<SiteSlotRenderer> _logger;
        private readonly Dictionary<string, (SiteVisibility Vis, SiteContentResult Content)?> _cache = new();

        public SiteSlotRenderer(SitePageReader reader, SiteBlockDataFactory data, ILogger<SiteSlotRenderer> logger)
        {
            _reader = reader;
            _data = data;
            _logger = logger;
        }

        /// <param name="templateKey">Systemsidans nyckel (<c>home</c>, <c>about</c> …).</param>
        /// <param name="position"><c>before</c> eller <c>after</c>.</param>
        public string Render(SiteSlotContext ctx, string templateKey, string position)
        {
            if (position != "before" && position != "after") return "";
            if (!SiteTemplates.For(ctx.OwnerType).CanHaveSlots(templateKey)) return "";
            var key = $"{ctx.OwnerType}:{ctx.OwnerId}:{templateKey}";
            if (!_cache.TryGetValue(key, out var loaded))
            {
                try { loaded = _reader.LoadSlots(ctx.OwnerType, ctx.OwnerId, templateKey); }
                catch (Exception ex) { _logger.LogError(ex, "Egen text för {Key} kunde inte läsas.", key); loaded = null; }
                _cache[key] = loaded;
            }
            if (loaded == null) return "";
            var blocks = position == "before" ? loaded.Value.Content.Before : loaded.Value.Content.After;
            if (blocks.Count == 0) return "";
            var rctx = new SiteRenderContext(ctx.Viewer, ctx.OwnerUrl, SiteTemplates.For(ctx.OwnerType), _data.For(ctx.OwnerType, ctx.OwnerId, ctx.OwnerNode));
            return SiteBlockRenderer.RenderAll(blocks, loaded.Value.Vis, rctx);
        }
    }
}
