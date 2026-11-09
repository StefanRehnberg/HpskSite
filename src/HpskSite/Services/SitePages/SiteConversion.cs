using System.Text.Json.Nodes;

namespace HpskSite.Services.SitePages
{
    /// <summary>
    /// "Ändra den här sidan" (konvertera-planen v2, K2): bygger innehållet i en omvandlad systemsida ur standardlistan i koden.
    /// Ren kod, ingen databas. Resultatet går alltid genom <see cref="SiteBlockSchema.Parse"/> (samma regler som en sparning), så en panel
    /// som inte hör hemma på sidan, en personpanel som är öppen för alla eller en dubblett kan inte uppstå — det vore ett fel i koden här och kastas.
    /// </summary>
    public static class SiteConversion
    {
        /// <summary>
        /// Panelerna på sidan i den ordning de ritas: inloggads standardlista, med de paneler som bara utloggade-listan har och som INTE får tas bort
        /// (obligatoriska) eller är utloggad-bara systemblock (inloggningsrutan, Om-texten på klubbens Hem) insatta efter sin granne i den listan.
        /// Då bevaras både den inloggade och den utloggade sidan exakt som förut.
        /// </summary>
        public static IReadOnlyList<PanelPlacement> Panels(string ownerType, string pageKey)
        {
            var template = SiteTemplates.For(ownerType);
            var result = PanelCatalog.Standard(ownerType, pageKey, true).ToList();
            var outList = PanelCatalog.Standard(ownerType, pageKey, false);
            for (var i = 0; i < outList.Count; i++)
            {
                var p = outList[i];
                if (result.Any(r => r.Key == p.Key)) continue;
                var def = PanelCatalog.Find(ownerType, p.Key);
                if (def == null || !(def.Required || template.IsLoggedOutOnly(pageKey, p.Key))) continue;
                var at = 0;
                for (var j = i - 1; j >= 0; j--)
                {
                    var idx = result.FindIndex(r => r.Key == outList[j].Key);
                    if (idx >= 0) { at = idx + 1; break; }
                }
                result.Insert(at, p);
            }
            return result;
        }

        /// <summary>
        /// Panelerna som en omvandlad sida måste behålla (kan flyttas, aldrig tas bort): de obligatoriska och de som mallen bestämt är bara för utloggade.
        /// Samma urval som <see cref="Panels"/> tar med utöver inloggads standardlista. Prövas vid varje sparning och publicering.
        /// </summary>
        public static IReadOnlyList<PanelDef> MustKeep(string ownerType, string pageKey)
        {
            var template = SiteTemplates.For(ownerType);
            return Panels(ownerType, pageKey).Select(p => PanelCatalog.Find(ownerType, p.Key))
                .Where(d => d != null && (d.Required || template.IsLoggedOutOnly(pageKey, d.Key))).Select(d => d!).ToList();
        }

        /// <summary>Panelens lagrade synlighet: mallens (efter taket för personuppgifter) för sidan. Okänd = Dold (fail-closed).</summary>
        public static SiteVisibility PanelVisibility(string ownerType, string pageKey, string panelKey)
        {
            var def = SiteTemplates.For(ownerType).PageByKey(pageKey)?.Blocks.FirstOrDefault(b => b.Key == panelKey && !b.Retired);
            return def == null ? SiteVisibility.Dold : SiteTemplate.EffectiveVisibility(def);
        }

        /// <summary>
        /// Det öppnaste en panel får vara: mallens nivå, utom för en panel ägaren får visa publikt (<see cref="PanelDef.PublicAllowed"/>), som får vara Alla. Standardnivån är alltid mallens (<see cref="PanelVisibility"/>);
        /// "Visa publikt" är ett aktivt val och ett uttryckligt undantag från "aldrig öppnare än förut". Okänd panel = Dold.
        /// </summary>
        public static SiteVisibility PanelFloor(string ownerType, string pageKey, string panelKey)
        {
            var def = PanelCatalog.Find(ownerType, panelKey);
            if (def is { PublicAllowed: true, PersonalData: false, HostedBy: null } && PanelCatalog.AllowedOn(ownerType, pageKey, panelKey)) return SiteVisibility.Alla;
            return PanelVisibility(ownerType, pageKey, panelKey);
        }

        /// <summary>Kan ägaren välja "Visa publikt" för panelen? Ja när golvet är Alla men standardnivån inte är det.</summary>
        public static bool CanShowPublicly(string ownerType, string pageKey, string panelKey) =>
            PanelFloor(ownerType, pageKey, panelKey) == SiteVisibility.Alla && PanelVisibility(ownerType, pageKey, panelKey) != SiteVisibility.Alla;

        /// <summary>Det öppnaste en OMGJORD sida får vara: den öppnaste av panelernas golv. En sida utan publicerbar panel har samma golv som förut (<see cref="SiteTemplate.MostOpenLevel"/>).</summary>
        public static SiteVisibility PageFloor(string ownerType, string pageKey)
        {
            var template = SiteTemplates.For(ownerType);
            var min = (int)(template.MostOpenLevel(pageKey) ?? SiteVisibility.Dold);
            foreach (var p in Panels(ownerType, pageKey))
                if (!template.IsLoggedOutOnly(pageKey, p.Key)) min = Math.Min(min, (int)PanelFloor(ownerType, pageKey, p.Key));
            return (SiteVisibility)min;
        }

        /// <summary>
        /// Sidans nivå efter omvandlingen: den öppnaste nivån mallens block hade (samma regel som en mallkopia). Ingenting blir öppnare än förut:
        /// varje panel bär sin egen mallnivå och en egen text bär den nivå ägaren gav den.
        /// </summary>
        public static SiteVisibility PageVisibility(string ownerType, string pageKey) =>
            SiteTemplates.For(ownerType).MostOpenLevel(pageKey) ?? SiteVisibility.Dold;

        /// <summary>
        /// Innehållet i den omvandlade sidan: ägarens egen text före (om en mallkopia med publicerad text finns), standardpanelerna, ägarens egen text efter.
        /// Egna block behåller sina id:n och nivåer; ett id som krockar med en panel får ett nytt.
        /// </summary>
        public static SiteContentResult Build(string ownerType, string pageKey, SiteContentResult? slots)
        {
            var template = SiteTemplates.For(ownerType);
            if (template.PageByKey(pageKey) is not { IsAdmin: false }) throw new ArgumentOutOfRangeException(nameof(pageKey), pageKey, "Sidan kan inte göras om.");
            var pageVis = PageVisibility(ownerType, pageKey);

            var panels = Panels(ownerType, pageKey);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var blocks = new JsonArray();

            string Fresh(string wanted)
            {
                var id = wanted;
                for (var n = 2; !used.Add(id); n++) id = ("s" + n + wanted)[..Math.Min(12, ("s" + n + wanted).Length)];
                return id;
            }
            JsonNode Own(SiteBlock b) => new JsonObject
            {
                ["id"] = Fresh(b.Id), ["type"] = b.Type, ["visibility"] = (int)b.Visibility, ["data"] = JsonNode.Parse(b.Data.ToJsonString()),
            };

            // Panelernas id:n först (pnl01…), så att en egen texts id aldrig kan ta ett av dem.
            for (var i = 0; i < panels.Count; i++) used.Add("pnl" + (i + 1).ToString("00"));
            foreach (var b in slots?.Before ?? Array.Empty<SiteBlock>()) blocks.Add(Own(b));
            for (var i = 0; i < panels.Count; i++)
            {
                var p = panels[i];
                blocks.Add(new JsonObject
                {
                    ["id"] = "pnl" + (i + 1).ToString("00"), ["type"] = "panel", ["visibility"] = (int)PanelVisibility(ownerType, pageKey, p.Key),
                    ["data"] = new JsonObject { ["key"] = p.Key, ["zone"] = SiteBlockSchema.ZoneName(p.Zone) },
                });
            }
            foreach (var b in slots?.After ?? Array.Empty<SiteBlock>()) blocks.Add(Own(b));

            var json = new JsonObject { ["schema"] = SiteBlockSchema.ConvertedSchemaVersion, ["blocks"] = blocks }.ToJsonString();
            var parsed = SiteBlockSchema.Parse(json, SiteContentKind.ConvertedPage, pageVis, template, pageKey);
            if (!parsed.Ok)
                throw new InvalidOperationException($"Standardsidan {ownerType}/{pageKey} är inte giltig i schema 2: " + string.Join("; ", parsed.Errors.Select(e => e.Path + " " + e.Message)));
            return parsed;
        }
    }
}
