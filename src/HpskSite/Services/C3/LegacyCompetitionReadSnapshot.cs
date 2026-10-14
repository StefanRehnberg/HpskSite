using System.Globalization;
using System.Reflection;
using HpskSite.CompetitionTypes.Common;
using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.Models;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.C3
{
    /// <summary>
    /// C3-planen S0 punkt 4: varje tävlings, seriers, säsongs och hubbens fält lästa med de GAMLA
    /// uttrycken — de 91 läsformer (typ, alias) som koden faktiskt använder, mätt med en sökning
    /// 2026-10-14 — både genom den publicerade cachen (<c>Value&lt;T&gt;</c>) och genom utkastet
    /// (<c>GetValue&lt;T&gt;</c>), plus modellklassen <see cref="Competition"/> och de härledda läsningarna
    /// (omfattning, nivå, klasser, värd, serie, adress).
    ///
    /// <para>S1 jämför <c>CompetitionInfo(node)</c> mot den här bilden för alla tävlingar. Avvikelserna
    /// ska vara en godkänd mängd. Bilden ändrar ingenting och sparar ingenting — den är ett svar.</para>
    ///
    /// <para>Ett uttryck som KASTAR är också ett svar (<c>"!Typ: meddelande"</c>): en FlexibleDropdown
    /// som kastar på <c>Value&lt;string&gt;</c> är exakt det S1 måste känna till.</para>
    ///
    /// <para>Uppräkningen går genom innehållstjänsten per dokumenttyp, inte genom trädet.</para>
    /// </summary>
    public sealed class LegacyCompetitionReadSnapshot
    {
        // Läsformerna i koden, (alias → typer). Från en sökning efter Value<T>("alias") och
        // GetValue<T>("alias") över .cs och .cshtml. Ett alias som inte finns på doktypen tas ändå
        // med — koden läser det, och svaret (null eller standardvärdet) är vad koden får.
        private static readonly Dictionary<string, string[]> Reads = new(StringComparer.Ordinal)
        {
            ["addToMenu"] = new[] { "bool" },
            ["allowDualCClassRegistration"] = new[] { "bool" },
            ["allowSelfReporting"] = new[] { "bool" },
            ["allowStafett"] = new[] { "bool" },
            ["allowTeams"] = new[] { "bool" },
            ["autoNotifyParticipants"] = new[] { "bool" },
            ["clubId"] = new[] { "int", "int?" },
            ["competitionDate"] = new[] { "DateTime", "DateTime?" },
            ["competitionDirector"] = new[] { "string" },
            ["competitionEndDate"] = new[] { "DateTime", "DateTime?" },
            ["competitionLevel"] = new[] { "string" },
            ["competitionManagers"] = new[] { "string" },
            ["competitionName"] = new[] { "string" },
            ["competitionScope"] = new[] { "string" },
            ["competitionType"] = new[] { "string", "IPublishedContent" },
            ["contactEmail"] = new[] { "string" },
            ["contactPerson"] = new[] { "string" },
            ["contactPhone"] = new[] { "string" },
            ["description"] = new[] { "string", "object" },
            ["direktplaceringConfig"] = new[] { "string" },
            ["externalRegistrationEmail"] = new[] { "string" },
            ["externalUrl"] = new[] { "string" },
            ["faltskytteResultsOfficial"] = new[] { "bool" },
            ["faltskyttePatrolsPublished"] = new[] { "bool" },
            ["faltskytteSelfServiceResults"] = new[] { "bool" },
            ["faltskytteStationManagers"] = new[] { "string" },
            ["hideFromNavigation"] = new[] { "bool" },
            ["invitationFile"] = new[] { "string", "IPublishedContent" },
            ["isActive"] = new[] { "bool", "bool?", "object" },
            ["isAwardingHonoraryAward"] = new[] { "bool" },
            ["isAwardingStandardMedals"] = new[] { "bool" },
            ["isClubOnly"] = new[] { "bool" },
            ["isExternal"] = new[] { "bool" },
            ["juniorRegistrationFee"] = new[] { "string" },
            ["maxParticipants"] = new[] { "int" },
            ["maxReshoots"] = new[] { "int" },
            ["mergeConfig"] = new[] { "string" },
            ["metaDescription"] = new[] { "string" },
            ["numberOfFinalSeries"] = new[] { "int" },
            ["numberOfSeriesOrStations"] = new[] { "int" },
            ["pageDescription"] = new[] { "string" },
            ["pageTitle"] = new[] { "string" },
            ["patrolIntervalMinutes"] = new[] { "int" },
            ["patrolSize"] = new[] { "int" },
            ["rangeId"] = new[] { "int" },
            ["regionalFederation"] = new[] { "string" },
            ["registrationCloseDate"] = new[] { "DateTime", "DateTime?" },
            ["registrationFee"] = new[] { "string", "decimal", "decimal?" },
            ["registrationOpenDate"] = new[] { "DateTime", "DateTime?" },
            ["registrationRequired"] = new[] { "bool" },
            ["resultListFile"] = new[] { "string", "IPublishedContent" },
            ["rollingStart"] = new[] { "string" },
            ["scoringMode"] = new[] { "string" },
            ["seasonDescription"] = new[] { "string" },
            ["seriesCalculationConfig"] = new[] { "string" },
            ["seriesCalculationStrategy"] = new[] { "string" },
            ["seriesDescription"] = new[] { "string" },
            ["seriesEndDate"] = new[] { "DateTime", "DateTime?" },
            ["seriesLogo"] = new[] { "IPublishedContent" },
            ["seriesName"] = new[] { "string" },
            ["seriesShortDescription"] = new[] { "string" },
            ["seriesSortOrder"] = new[] { "int?" },
            ["seriesStartDate"] = new[] { "DateTime", "DateTime?" },
            ["shootingClassIds"] = new[] { "string" },
            ["showInMenu"] = new[] { "bool" },
            ["showLiveResults"] = new[] { "bool" },
            ["specialInstructions"] = new[] { "string" },
            ["stafettRegistrationFee"] = new[] { "string" },
            ["stationConfig"] = new[] { "string" },
            ["subCompetitionFee"] = new[] { "string" },
            ["subCompetitionFeeMode"] = new[] { "string" },
            ["subCompetitionName"] = new[] { "string" },
            ["swishNumber"] = new[] { "string" },
            ["teamRegistrationFee"] = new[] { "string" },
            ["teamResultSeriesCount"] = new[] { "int" },
            ["venue"] = new[] { "string" },
        };

        public static readonly string[] Aliases = { "competition", "competitionSeries", "competitionSeason", "competitionsHub" };

        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IPublishedValueFallback _fallback;
        private readonly ILogger<LegacyCompetitionReadSnapshot> _logger;

        public LegacyCompetitionReadSnapshot(IUmbracoContextAccessor umbracoContextAccessor, IContentService contentService,
            IContentTypeService contentTypeService, IPublishedValueFallback fallback, ILogger<LegacyCompetitionReadSnapshot> logger)
        {
            _umbracoContextAccessor = umbracoContextAccessor;
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _fallback = fallback;
            _logger = logger;
        }

        /// <summary>Alla noder av de fyra typerna, sorterade på id. <paramref name="onlyId"/> = bara den.</summary>
        public List<Dictionary<string, object?>> Build(int? onlyId = null)
        {
            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            var cache = ctx?.Content;
            var result = new List<Dictionary<string, object?>>();

            foreach (var alias in Aliases)
            {
                var type = _contentTypeService.Get(alias);
                if (type == null) continue;
                foreach (var node in AllOfType(type.Id))
                {
                    if (onlyId.HasValue && node.Id != onlyId.Value) continue;
                    if (node.Trashed) continue;
                    var published = cache?.GetById(node.Id);
                    result.Add(Describe(node, published, cache));
                }
            }
            return result.OrderBy(r => (int)r["id"]!).ToList();
        }

        private IEnumerable<IContent> AllOfType(int contentTypeId)
        {
            const int pageSize = 500;
            long page = 0, total;
            do
            {
                var batch = _contentService.GetPagedOfType(contentTypeId, page, pageSize, out total, null, null);
                foreach (var c in batch) yield return c;
                page++;
            } while (page * pageSize < total);
        }

        private Dictionary<string, object?> Describe(IContent node, IPublishedContent? pub, Umbraco.Cms.Core.PublishedCache.IPublishedContentCache? cache)
        {
            var parent = node.ParentId > 0 ? _contentService.GetById(node.ParentId) : null;
            var d = new Dictionary<string, object?>
            {
                ["id"] = node.Id,
                ["key"] = node.Key,
                ["alias"] = node.ContentType.Alias,
                ["name"] = node.Name,
                ["published"] = node.Published,
                ["inPublishedCache"] = pub != null,
                ["createDate"] = Fmt(node.CreateDate),
                ["updateDate"] = Fmt(node.UpdateDate),
                ["sortOrder"] = node.SortOrder,
                ["level"] = node.Level,
                ["path"] = node.Path,
                ["parentId"] = node.ParentId,
                ["parentAlias"] = parent?.ContentType.Alias,
                ["parentName"] = parent?.Name,
                ["templateId"] = node.TemplateId,
                ["publishDate"] = Fmt(node.PublishDate),
                ["url"] = Safe(() => pub?.Url()),
                ["urlSegment"] = pub?.UrlSegment,
            };

            // Fälten: doktypens egna + det koden läser fast det saknas.
            var props = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            var aliases = node.Properties.Select(p => p.Alias).ToHashSet(StringComparer.Ordinal);
            foreach (var a in Reads.Keys.Where(k => RelevantFor(node.ContentType.Alias, k))) aliases.Add(a);
            var differs = new List<string>();
            foreach (var alias in aliases.OrderBy(a => a, StringComparer.Ordinal))
            {
                var p = new Dictionary<string, object?>();
                var prop = node.Properties.FirstOrDefault(x => x.Alias == alias);
                p["onType"] = prop != null;
                if (prop != null) p["editor"] = prop.PropertyType.PropertyEditorAlias;
                var draft = Safe(() => Fmt(node.GetValue(alias)));
                var source = pub == null ? null : Safe(() => Fmt(pub.GetProperty(alias)?.GetSourceValue()));
                p["draft"] = draft;
                p["source"] = source;
                if (pub != null && !Equals(draft, source)) differs.Add(alias);

                var reads = new SortedDictionary<string, object?>(StringComparer.Ordinal);
                if (Reads.TryGetValue(alias, out var types))
                {
                    foreach (var t in types)
                    {
                        if (pub != null) reads["p:" + t] = ReadPublished(pub, alias, t);
                        if (t != "IPublishedContent" && t != "object") reads["c:" + t] = ReadContent(node, alias, t);
                    }
                }
                if (pub != null) reads["p:untyped"] = Safe(() => Fmt(pub.Value(alias)));
                p["reads"] = reads;
                props[alias] = p;
            }
            d["props"] = props;
            d["draftDiffersFromPublished"] = differs;

            if (node.ContentType.Alias == "competition" && pub != null)
            {
                d["derived"] = Derived(pub, cache);
                d["model"] = ModelProperties(pub);
            }
            else if (node.ContentType.Alias == "competitionSeries" && pub != null)
            {
                d["derived"] = new Dictionary<string, object?>
                {
                    ["roundIds"] = Safe(() => pub.Children().Where(c => c.ContentType.Alias == "competition").Select(c => (object)c.Id).ToList()),
                    ["childAliases"] = Safe(() => pub.Children().Select(c => (object)c.ContentType.Alias).Distinct().ToList()),
                };
            }
            return d;
        }

        private static bool RelevantFor(string docType, string alias) => docType switch
        {
            "competitionSeries" => alias.StartsWith("series", StringComparison.Ordinal) || alias is "isActive" or "showInMenu" or "regionalFederation" or "clubId" or "competitionType",
            "competitionSeason" => alias is "pageTitle" or "seasonDescription",
            "competitionsHub" => alias is "pageTitle" or "pageDescription",
            _ => !alias.StartsWith("season", StringComparison.Ordinal) && alias is not ("pageDescription" or "showInMenu") && !alias.StartsWith("series", StringComparison.Ordinal) || alias == "seriesSortOrder",
        };

        private Dictionary<string, object?> Derived(IPublishedContent comp, Umbraco.Cms.Core.PublishedCache.IPublishedContentCache? cache)
        {
            var d = new Dictionary<string, object?>();
            d["scope"] = Safe(() => CompetitionScopeHelper.ReadScope(comp));
            d["isChampionshipScope"] = Safe(() => CompetitionScopeHelper.IsChampionshipScope(CompetitionScopeHelper.ReadScope(comp)));
            d["level"] = Safe(() => CompetitionLevel.Read(comp));
            d["classIds"] = Safe(() => ShootingClassIdsValue.ReadIds(comp.Value("shootingClassIds")));
            d["competitionTypeId"] = Safe(() => HpskSite.Models.CompetitionTypes.GetFuzzy(comp.Value<string>("competitionType") ?? "")?.Id);
            var clubId = Safe(() => comp.Value<int>("clubId")) as int? ?? 0;
            d["hostClubId"] = clubId;
            d["hostRegion"] = Safe(() => comp.Value<string>("regionalFederation"));
            if (clubId > 0 && cache != null)
            {
                var club = cache.GetById(clubId);
                d["hostClubAlias"] = club?.ContentType.Alias;
                d["hostClubName"] = club?.Name;
                d["hostClubRegionProperty"] = Safe(() => club?.Value<string>("regionalFederation"));
                d["hostClubRegionPageCode"] = Safe(() => club?.Parent?.Parent?.ContentType.Alias == "regionalPage" ? club.Parent.Parent.Value<string>("regionCode") : null);
            }
            var parent = comp.Parent;
            d["seriesId"] = parent?.ContentType.Alias == "competitionSeries" ? parent.Id : (int?)null;
            if (parent?.ContentType.Alias == "competitionSeries")
            {
                d["seriesClubId"] = Safe(() => parent.Value<int>("clubId"));
                d["seriesRegion"] = Safe(() => parent.Value<string>("regionalFederation"));
                d["seriesName"] = Safe(() => parent.Value<string>("seriesName"));
                d["seriesRoundIndex"] = Safe(() => parent.Children().Where(c => c.ContentType.Alias == "competition").ToList().FindIndex(c => c.Id == comp.Id) + 1);
            }
            d["childAliases"] = Safe(() => comp.Children().Select(c => (object)(c.ContentType.Alias + ":" + c.UrlSegment)).OrderBy(x => (string)x).ToList());
            return d;
        }

        private Dictionary<string, object?> ModelProperties(IPublishedContent comp)
        {
            var d = new SortedDictionary<string, object?>(StringComparer.Ordinal);
            Competition model;
            try { model = new Competition(comp, _fallback); }
            catch (Exception ex) { return new Dictionary<string, object?> { ["!"] = ex.GetType().Name + ": " + ex.Message }; }
            foreach (var pi in typeof(Competition).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
            {
                if (pi.GetIndexParameters().Length > 0) continue;
                d[pi.Name] = Safe(() => Fmt(pi.GetValue(model)));
            }
            return new Dictionary<string, object?>(d);
        }

        private static object? ReadPublished(IPublishedContent pub, string alias, string type) => Safe(() => type switch
        {
            "string" => Fmt(pub.Value<string>(alias)),
            "int" => Fmt(pub.Value<int>(alias)),
            "int?" => Fmt(pub.Value<int?>(alias)),
            "bool" => Fmt(pub.Value<bool>(alias)),
            "bool?" => Fmt(pub.Value<bool?>(alias)),
            "DateTime" => Fmt(pub.Value<DateTime>(alias)),
            "DateTime?" => Fmt(pub.Value<DateTime?>(alias)),
            "decimal" => Fmt(pub.Value<decimal>(alias)),
            "decimal?" => Fmt(pub.Value<decimal?>(alias)),
            "IPublishedContent" => Fmt(pub.Value<IPublishedContent>(alias)),
            "object" => Fmt(pub.Value<object>(alias)),
            _ => "?" + type,
        });

        private static object? ReadContent(IContent node, string alias, string type) => Safe(() => type switch
        {
            "string" => Fmt(node.GetValue<string>(alias)),
            "int" => Fmt(node.GetValue<int>(alias)),
            "int?" => Fmt(node.GetValue<int?>(alias)),
            "bool" => Fmt(node.GetValue<bool>(alias)),
            "bool?" => Fmt(node.GetValue<bool?>(alias)),
            "DateTime" => Fmt(node.GetValue<DateTime>(alias)),
            "DateTime?" => Fmt(node.GetValue<DateTime?>(alias)),
            "decimal" => Fmt(node.GetValue<decimal>(alias)),
            "decimal?" => Fmt(node.GetValue<decimal?>(alias)),
            _ => "?" + type,
        });

        private static object? Safe(Func<object?> f)
        {
            try { return f(); }
            catch (Exception ex) { return "!" + ex.GetType().Name + ": " + ex.Message; }
        }

        /// <summary>En jämförbar form: datum utan kultur, decimaler med punkt, innehåll som id.</summary>
        public static object? Fmt(object? v) => v switch
        {
            null => null,
            string s => s,
            bool b => b,
            int i => i,
            long l => l,
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            double db => db.ToString(CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
            Guid g => g.ToString(),
            IPublishedContent pc => "content:" + pc.Id + ":" + pc.ContentType.Alias,
            IEnumerable<IPublishedContent> many => string.Join(",", many.Select(x => "content:" + x.Id)),
            System.Collections.IEnumerable e when v is not string => string.Join("|", e.Cast<object?>().Select(x => Convert.ToString(Fmt(x), CultureInfo.InvariantCulture))),
            Enum en => en.ToString(),
            _ => Convert.ToString(v, CultureInfo.InvariantCulture),
        };
    }
}
