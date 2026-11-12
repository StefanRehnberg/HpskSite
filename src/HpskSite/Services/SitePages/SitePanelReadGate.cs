using HpskSite.Services.Events;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// Läsgrinden för en panels DATA (blockbiblioteket, Stefans beslut 2026-11-09: "det borde vara en inställning på respektive block").
    /// Synligheten är en inställning på blocket (panelen) på ägarens sida, och dataanropet följer den: den som inte får se blocket får heller
    /// inte dess data, även om hen anropar adressen direkt. Förut prövade medlemskatalogen och styrelsen bara "inloggad" på servern, vilket
    /// gjorde att ett block ägaren ställt in som <see cref="SiteVisibility.Klubbmedlemmar"/> bara var dolt på sidan men fullt läsbart i anropet.
    ///
    /// <para>Nivån är blockets effektiva nivå på en omgjord sida (<see cref="SiteConvertedPages.Effective"/>: striktast av blockets och sidans,
    /// personblock aldrig Alla) och mallens nivå på en sida som inte är omgjord, alltså exakt dagens beteende tills ägaren ändrar något.
    /// En omgjord sida där panelen tagits bort, eller som inte går att tolka, läses som <see cref="SiteVisibility.Dold"/> (bara ägarens admin).</para>
    /// </summary>
    public sealed class SitePanelReadGate
    {
        private readonly SiteConvertedSource _source;
        private readonly EventViewerFactory _viewers;
        private readonly ILogger<SitePanelReadGate> _logger;

        public SitePanelReadGate(SiteConvertedSource source, EventViewerFactory viewers, ILogger<SitePanelReadGate> logger)
        {
            _source = source;
            _viewers = viewers;
            _logger = logger;
        }

        /// <summary>Nivån för panelen <paramref name="panelKey"/> på ägarens sida <paramref name="pageKey"/>. Ren funktion: prövas utan databas.</summary>
        public static SiteVisibility LevelOf(IReadOnlyDictionary<string, ConvertedPage> pages, string ownerType, string pageKey, string panelKey)
        {
            if (!pages.TryGetValue(pageKey, out var page))
                return SiteConversion.PanelVisibility(ownerType, pageKey, panelKey);   // inte omgjord: mallens nivå, som idag
            if (page.State != ConvertedState.Ok || page.Content == null) return SiteVisibility.Dold;
            var block = page.Content.Blocks.FirstOrDefault(b => b.Type == "panel" && SiteBlockSchema.PanelKeyOf(b.Data) == panelKey);
            return block == null ? SiteVisibility.Dold : SiteConvertedPages.Effective(block, page.PageVisibility, ownerType);
        }

        /// <summary>Får den som frågar läsa panelens data? <paramref name="orOwnerBoard"/>: ägarens styrelse får alltid läsa sin egen styrelse.</summary>
        public async Task<bool> CanReadPanelAsync(string ownerType, int ownerId, string pageKey, string panelKey, bool orOwnerBoard = false)
        {
            if (ownerId <= 0) return false;
            try
            {
                var level = LevelOf(_source.Pages(ownerType, ownerId), ownerType, pageKey, panelKey);
                return await CanAsync(ownerType, ownerId, level, orOwnerBoard);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Läsgrinden för {Owner} {Id} {Page}/{Panel} kunde inte avgöras; nekar.", ownerType, ownerId, pageKey, panelKey);
                return false;
            }
        }

        /// <summary>Samma fråga för data som saknar panel på ägarens sida: en fast nivå.</summary>
        public async Task<bool> CanReadAtLevelAsync(string ownerType, int ownerId, SiteVisibility level, bool orOwnerBoard = false)
        {
            if (ownerId <= 0) return false;
            try { return await CanAsync(ownerType, ownerId, level, orOwnerBoard); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Läsgrinden för {Owner} {Id} kunde inte avgöras; nekar.", ownerType, ownerId);
                return false;
            }
        }

        private async Task<bool> CanAsync(string ownerType, int ownerId, SiteVisibility level, bool orOwnerBoard)
        {
            var viewer = (await _viewers.CurrentAsync()).For(ownerType, ownerId);
            return SiteAccess.CanSee(level, viewer) || (orOwnerBoard && viewer.LoggedIn && (viewer.IsOwnerBoard || viewer.IsOwnerAdmin));
        }
    }
}
