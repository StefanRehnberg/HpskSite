using HpskSite.Models.Competitions;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.PublishedCache;
using Umbraco.Cms.Core.PropertyEditors;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// <b>En tävling eller serie som sidmodell utan nod</b> (C3-planen §5c). Byggd ur posten, med dokumenttypens
    /// RIKTIGA <see cref="IPublishedContentType"/>, så att varje egenskap går genom typens egen konverterare —
    /// <c>Value&lt;IPublishedContent&gt;("invitationFile")</c> ger mediet, en RTE-text sin markup.
    ///
    /// <para><b>Föräldern</b> sätts uttryckligen (serien, annars tävlingsnavet) — <c>Parent</c>-egenskapen.
    /// <b>⚠️ Förlängningsmetoderna <c>Parent()</c>, <c>Children()</c>, <c>Root()</c> och <c>Ancestors()</c> går via
    /// Umbracos navigeringstjänst på nodens nyckel</b>, och en virtuell nod är okänd där (efter rensningen helt).
    /// Vyerna som får en virtuell nod använder därför <c>SiteRoot()</c> och egenskapen <c>Parent</c>, aldrig
    /// navigeringen.</para>
    ///
    /// <para>Barn finns inte: resultatlistan och startlistorna kommer ur sina lagringar.</para>
    /// </summary>
    public sealed class VirtualCompetitionContent : PublishedContentBase
    {
        private readonly IReadOnlyDictionary<string, IPublishedProperty> _properties;
        private readonly IReadOnlyDictionary<string, PublishedCultureInfo> _cultures;

        public VirtualCompetitionContent(IVariationContextAccessor variation, IPublishedContentType type, int id, Guid key,
            string name, string urlSegment, int sortOrder, DateTime createDate, DateTime updateDate, IPublishedContent? parent,
            Func<string, object?> sourceValue)
            : base(variation)
        {
            ContentType = type;
            Id = id;
            Key = key;
            _name = name;
            _urlSegment = urlSegment;
            SortOrder = sortOrder;
            CreateDate = createDate;
            UpdateDate = updateDate;
            _parent = parent;
            _cultures = new Dictionary<string, PublishedCultureInfo>
            {
                [""] = new PublishedCultureInfo("", name, urlSegment, updateDate),
            };
            _properties = type.PropertyTypes.ToDictionary(
                pt => pt.Alias,
                pt => (IPublishedProperty)new VirtualProperty(pt, this, sourceValue(pt.Alias)),
                StringComparer.OrdinalIgnoreCase);
        }

        private readonly string _name;
        private readonly string _urlSegment;
        private readonly IPublishedContent? _parent;

        public override IPublishedContentType ContentType { get; }
        public override Guid Key { get; }
        public override int Id { get; }
        public override string Name => _name;
#pragma warning disable CS0618, CS0672 // Umbraco 17 har gjort dem föråldrade; vyerna läser dem ännu.
        public override string UrlSegment => _urlSegment;
        public override int Level => (_parent?.Level ?? 1) + 1;
        public override string Path => (_parent?.Path ?? "-1") + "," + Id;
        public override IPublishedContent? Parent => _parent;
        public override IEnumerable<IPublishedContent> Children => Array.Empty<IPublishedContent>();
#pragma warning restore CS0618, CS0672
        public override int SortOrder { get; }
        public override int? TemplateId => null;
        public override int CreatorId => 0;
        public override DateTime CreateDate { get; }
        public override int WriterId => 0;
        public override DateTime UpdateDate { get; }
        public override IReadOnlyDictionary<string, PublishedCultureInfo> Cultures => _cultures;
        public override PublishedItemType ItemType => PublishedItemType.Content;
        public override bool IsDraft(string? culture = null) => false;
        public override bool IsPublished(string? culture = null) => true;
        public override IEnumerable<IPublishedProperty> Properties => _properties.Values;
        public override IPublishedProperty? GetProperty(string alias) => _properties.TryGetValue(alias, out var p) ? p : null;

        /// <summary>En egenskap med typens konverterare: källvärdet → mellanvärdet → objektet, en gång.</summary>
        private sealed class VirtualProperty : IPublishedProperty
        {
            private readonly IPublishedElement _owner;
            private readonly object? _source;
            private object? _value;
            private bool _converted;

            public VirtualProperty(IPublishedPropertyType type, IPublishedElement owner, object? source)
            {
                PropertyType = type;
                _owner = owner;
                _source = source;
            }

            public IPublishedPropertyType PropertyType { get; }
            public string Alias => PropertyType.Alias;

            // Varje konverterare svarar inte på varje nivå: RTE-konverteraren kastar för Source
            // (ArgumentOutOfRangeException) — seriesidorna gav 500. Då prövas nästa nivå, sist en enkel tomhetskoll.
            public bool HasValue(string? culture = null, string? segment = null)
            {
                var atSource = TryIsValue(_source, PropertyValueLevel.Source);
                if (atSource.HasValue) return atSource.Value;
                var value = GetValue(culture, segment);
                var atObject = TryIsValue(value, PropertyValueLevel.Object);
                if (atObject.HasValue) return atObject.Value;
                return value switch
                {
                    null => false,
                    string s => !string.IsNullOrWhiteSpace(s),
                    System.Collections.IEnumerable e => e.Cast<object?>().Any(),
                    _ => true,
                };
            }

            private bool? TryIsValue(object? v, PropertyValueLevel level)
            {
                try { return PropertyType.IsValue(v, level); }
                catch (ArgumentOutOfRangeException) { return null; }
                catch (NotSupportedException) { return null; }
            }

            public object? GetSourceValue(string? culture = null, string? segment = null) => _source;

            public object? GetValue(string? culture = null, string? segment = null)
            {
                if (_converted) return _value;
                var inter = PropertyType.ConvertSourceToInter(_owner, _source, false);
                _value = PropertyType.ConvertInterToObject(_owner, PropertyCacheLevel.Element, inter, false);
                _converted = true;
                return _value;
            }

            public object? GetDeliveryApiValue(bool expanding, string? culture = null, string? segment = null)
            {
                var inter = PropertyType.ConvertSourceToInter(_owner, _source, false);
                return PropertyType.ConvertInterToDeliveryApiObject(_owner, PropertyCacheLevel.Element, inter, false, expanding);
            }
        }
    }

    /// <summary>
    /// Bygger de virtuella noderna (C3-planen §5c). Källvärdet för varje egenskap är postens fält i den form noden
    /// lagrade det (<see cref="CompetitionFieldValues.ToNode"/>); ett val (FlexibleDropdown) som JSON-array, som
    /// konverteraren väntar sig. Egenskaper som inte är fält i posten har inget värde.
    /// </summary>
    public class VirtualCompetitionNodes
    {
        private readonly IPublishedContentTypeCache _types;
        private readonly IVariationContextAccessor _variation;
        private readonly ICompetitionSeriesStore _series;
        private readonly ICompetitionStore _competitions;
        private readonly SiteSettings.SiteSettingService _settings;
        private readonly Umbraco.Cms.Core.Web.IUmbracoContextAccessor _ctx;
        private readonly Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService _navigation;

        public VirtualCompetitionNodes(IPublishedContentTypeCache types, IVariationContextAccessor variation, ICompetitionSeriesStore series,
            ICompetitionStore competitions, SiteSettings.SiteSettingService settings, Umbraco.Cms.Core.Web.IUmbracoContextAccessor ctx,
            Umbraco.Cms.Core.Services.Navigation.IDocumentNavigationQueryService navigation)
        {
            _navigation = navigation;
            _types = types;
            _variation = variation;
            _series = series;
            _competitions = competitions;
            _settings = settings;
            _ctx = ctx;
        }

        public bool TableMode => CompetitionStoreSettings.UsesTable(_settings);

        private IPublishedContentCache? Cache => _ctx.TryGetUmbracoContext(out var uc) ? uc.Content : null;

        /// <summary>Tävlingsnavet (en riktig nod, under sajtens rot), eller null. Via navigeringens nycklar.</summary>
        public IPublishedContent? Hub()
        {
            var cache = Cache;
            if (cache == null || !_navigation.TryGetRootKeys(out var roots)) return null;
            var root = roots.FirstOrDefault();
            if (root == Guid.Empty || !_navigation.TryGetChildrenKeysOfType(root, CompetitionTreePlacement.HubAlias, out var hubs)) return null;
            return hubs.Select(k => cache.GetById(k)).FirstOrDefault(n => n != null);
        }

        /// <summary>
        /// Sidans nod för tävlingen: i nodläget den publicerade noden, i tabelläget den virtuella (§5c). Null när
        /// tävlingen inte finns (eller inte är publicerad). Använd den där en nod behövs för adress, RTE eller media.
        /// </summary>
        public IPublishedContent? PageNode(CompetitionInfo c)
        {
            if (TableMode) return Competition(c, Hub());
            var node = Cache?.GetById(c.Id);
            return node?.ContentType.Alias == CompetitionFieldTable.NodeAlias ? node : null;
        }

        public IPublishedContent? PageNode(int competitionId)
        {
            if (!TableMode)
            {
                var node = Cache?.GetById(competitionId);
                return node?.ContentType.Alias == CompetitionFieldTable.NodeAlias ? node : null;
            }
            var c = _competitions.Get(competitionId);
            return c == null ? null : Competition(c, Hub());
        }

        /// <summary>Sidans nod för serien (publicerad nod / virtuell), eller null.</summary>
        public IPublishedContent? SeriesPageNode(CompetitionSeriesInfo s)
        {
            if (TableMode) return Series(s, Hub());
            var node = Cache?.GetById(s.Id);
            return node?.ContentType.Alias == CompetitionSeriesFieldTable.NodeAlias ? node : null;
        }

        /// <summary>Tävlingen med serien (virtuell) eller <paramref name="hub"/> som förälder.</summary>
        public IPublishedContent Competition(CompetitionInfo c, IPublishedContent? hub)
        {
            var type = _types.Get(PublishedItemType.Content, CompetitionFieldTable.NodeAlias);
            var series = c.SeriesId is > 0 ? _series.Get(c.SeriesId.Value) : null;
            var parent = series != null ? Series(series, hub) : hub;
            return new VirtualCompetitionContent(_variation, type, c.Id, c.Key, c.Name, c.UrlSegment, c.SortOrder, c.CreateDate,
                c.UpdateDate, parent, alias => Source(CompetitionFieldTable.Fields, c, alias));
        }

        public IPublishedContent Series(CompetitionSeriesInfo s, IPublishedContent? hub)
        {
            var type = _types.Get(PublishedItemType.Content, CompetitionSeriesFieldTable.NodeAlias);
            return new VirtualCompetitionContent(_variation, type, s.Id, s.Key, s.Name, s.UrlSegment, s.SortOrder, s.CreateDate,
                s.UpdateDate, hub, alias => Source(CompetitionSeriesFieldTable.Fields, s, alias));
        }

        private static object? Source<T>(IReadOnlyList<CompetitionFieldDef<T>> fields, T record, string alias)
        {
            var f = fields.FirstOrDefault(x => string.Equals(x.Alias, alias, StringComparison.OrdinalIgnoreCase));
            if (f == null) return null;
            var value = CompetitionFieldValues.ToNode(f.Shape, f.Get(record));
            return f.Shape == CompetitionFieldShape.Choice && value is string s && !s.TrimStart().StartsWith("[")
                ? Newtonsoft.Json.JsonConvert.SerializeObject(new[] { s })
                : value;
        }
    }
}
