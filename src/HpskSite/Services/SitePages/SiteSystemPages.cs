namespace HpskSite.Services.SitePages
{
    /// <summary>Var en vanlig sida (Hem, Om klubben …) står: som förut, ett utkast som ännu inte syns, eller en egen version.</summary>
    public enum SystemPageMode { AsBefore, Draft, Converted }

    /// <summary>
    /// En vanlig sida så som Hemsidan visar den. <see cref="PageId"/>/<see cref="Revision"/> finns när ägaren har en rad för sidan (mallkopia), annars null.
    /// <see cref="PendingChanges"/> = en omgjord sida med ändringar som inte är publicerade. <see cref="CanConvert"/> = knappen "Ändra den här sidan" får visas.
    /// </summary>
    public sealed record SystemPageState(string Key, string Label, SystemPageMode Mode, int? PageId, int? Revision, bool PendingChanges, bool CanConvert, int Level = 0);

    /// <summary>
    /// Hemsidans vy över de vanliga sidorna (K5a): läget som ord-bar enum, ur sidans versioner — samma sanning som <see cref="SitePageStore.IsConverted"/> och
    /// <see cref="SiteConvertedSource"/> (publicerad version i schema 2 = egen version; bara ett utkast i schema 2 = utkast). Ren kod.
    /// </summary>
    public static class SiteSystemPages
    {
        /// <summary>Läget ur schemat i den publicerade och i utkastversionen. En omgjord sida har aldrig ett utkast i schema 1 (skrivgrinden), så det ger "som förut".</summary>
        public static SystemPageMode ModeOf(int? publishedSchema, int? draftSchema) =>
            publishedSchema == SiteBlockSchema.ConvertedSchemaVersion ? SystemPageMode.Converted
            : draftSchema == SiteBlockSchema.ConvertedSchemaVersion ? SystemPageMode.Draft
            : SystemPageMode.AsBefore;

        /// <summary>Läget som kod för klienten (permanent): asBefore, draft, converted.</summary>
        public static string Code(SystemPageMode mode) => mode switch
        {
            SystemPageMode.Converted => "converted",
            SystemPageMode.Draft => "draft",
            _ => "asBefore",
        };

        /// <summary>Orden användaren ser för läget.</summary>
        public static string Word(SystemPageMode mode) => mode switch
        {
            SystemPageMode.Converted => "Egen version",
            SystemPageMode.Draft => "Utkast, syns inte än",
            _ => "Som förut",
        };

        /// <summary>
        /// Alla vanliga sidor (aldrig Admin) med sitt läge. <paramref name="canWrite"/> = ägarens EGEN admin (inte stödläge) och <paramref name="convertEnabled"/> = brytare 3:
        /// bara då får "Ändra den här sidan" visas, och bara för en sida som inte redan är omgjord eller på väg att bli det. Servern prövar allt på nytt vid skrivning.
        /// </summary>
        public static IReadOnlyList<SystemPageState> Describe(string ownerType, IReadOnlyList<SitePageListItem> copies, bool convertEnabled, bool canWrite) =>
            SiteTemplates.For(ownerType).Pages.Where(p => !p.IsAdmin).OrderBy(p => p.Order).Select(p =>
            {
                var copy = copies.FirstOrDefault(c => c.Slug == p.Key);   // ListTemplateCopies lägger mallnyckeln i Slug
                var mode = copy == null ? SystemPageMode.AsBefore : ModeOf(copy.PublishedSchema, copy.DraftSchema);
                var pending = mode == SystemPageMode.Converted && copy!.DraftSchema == SiteBlockSchema.ConvertedSchemaVersion;
                var canConvert = canWrite && convertEnabled && mode == SystemPageMode.AsBefore && PanelCatalog.Standard(ownerType, p.Key, true).Count > 0;
                // Vem ser sidan: en omgjord sidas egen nivå; annars mallens öppnaste nivå (den som öppnast får se något av sidan).
                var level = mode == SystemPageMode.Converted ? copy!.Visibility : (int)(SiteTemplates.For(ownerType).MostOpenLevel(p.Key) ?? SiteVisibility.Alla);
                return new SystemPageState(p.Key, p.Label, mode, copy?.Id, copy?.Revision, pending, canConvert, level);
            }).ToList();

        /// <summary>Panelerna ägaren får välja på en sida, för Hemsidans kort: namn, vad de visar, zon, om de är obligatoriska och om de visar personuppgifter.</summary>
        public static IReadOnlyList<object> PanelsFor(string ownerType, string pageKey) =>
            InPageOrder(ownerType, pageKey).Select(d => (object)new
            {
                key = d.Key, name = d.Name, description = d.Description, zone = ZoneWord(d.Zone), required = d.Required, personalData = d.PersonalData, hostedBy = d.HostedBy, publicAllowed = SiteConversion.CanShowPublicly(ownerType, pageKey, d.Key),
            }).ToList();

        /// <summary>
        /// ALLA paneler som får finnas på en omgjord sida (editorn behöver namn och regler även för dem klubben inte kan välja, som inloggningsrutan): namn, vad de visar,
        /// förvald zon, om de måste behållas (<c>mustKeep</c>: kan flyttas, aldrig tas bort), om de bara är för utloggade (<c>systemOnly</c>: syns, men kan inte ändras eller
        /// läggas till), personuppgifter, och <c>level</c> = den nivå panelen har idag. Ägaren kan göra en panel slutnare än så, aldrig öppnare.
        /// </summary>
        public static IReadOnlyList<object> CatalogFor(string ownerType, string pageKey)
        {
            var template = SiteTemplates.For(ownerType);
            var must = SiteConversion.MustKeep(ownerType, pageKey).Select(d => d.Key).ToHashSet();
            var order = SiteConversion.Panels(ownerType, pageKey).Select(p => p.Key).ToList();
            return PanelCatalog.For(ownerType).Where(d => PanelCatalog.AllowedOn(ownerType, pageKey, d.Key))
                .OrderBy(d => { var i = order.IndexOf(d.Key); return i < 0 ? int.MaxValue : i; })
                .Select(d => (object)new
                {
                    key = d.Key, name = d.Name, description = d.Description, zone = ZoneWord(d.Zone), mustKeep = must.Contains(d.Key), systemOnly = template.IsLoggedOutOnly(pageKey, d.Key),
                    personalData = d.PersonalData, hostedBy = d.HostedBy, level = (int)SiteConversion.PanelVisibility(ownerType, pageKey, d.Key),
                    publicAllowed = SiteConversion.CanShowPublicly(ownerType, pageKey, d.Key),
                }).ToList();
        }

        /// <summary>De valbara panelerna i den ordning sidan visar dem (standardlistan för inloggad, sedan de som bara en annan lista ritar); resten sist.</summary>
        public static IReadOnlyList<PanelDef> InPageOrder(string ownerType, string pageKey)
        {
            var order = PanelCatalog.Standard(ownerType, pageKey, true).Concat(PanelCatalog.Standard(ownerType, pageKey, false)).Select(p => p.Key).Distinct().ToList();
            return PanelCatalog.Selectable(ownerType, pageKey).OrderBy(d => { var i = order.IndexOf(d.Key); return i < 0 ? int.MaxValue : i; }).ToList();
        }

        public static string ZoneWord(PanelZone z) => z switch { PanelZone.Main => "main", PanelZone.Side => "side", _ => "full" };
    }
}
