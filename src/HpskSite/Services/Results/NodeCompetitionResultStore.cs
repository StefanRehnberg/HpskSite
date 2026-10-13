using System.Collections.Concurrent;
using HpskSite.Models.Results;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Extensions;

namespace HpskSite.Services.Results
{
    /// <summary>
    /// Resultatlistan som <c>competitionResult</c>-nod under tävlingen — dagens lagring, och den som
    /// gäller tills listorna flyttats till tabellen (skalplanen fas C1).
    ///
    /// <para><b>Noden letas upp på ETT sätt:</b> tävlingens barn av typen <c>competitionResult</c>, den
    /// som heter "Resultat" i första hand, annars den första. Tidigare letade ytorna på fem olika sätt
    /// (en del bara bland de 50 första barnen, en del bara på namnet), och kunde alltså hitta olika noder
    /// eller ingen alls för samma tävling.</para>
    ///
    /// <para>Varje skrivning sparar OCH publicerar, som alla skrivare gjorde förut — nodens publicering är
    /// vad som ger tävlingen sin <c>/resultat/</c>-adress.</para>
    /// </summary>
    public class NodeCompetitionResultStore : ICompetitionResultStore
    {
        private static readonly ConcurrentDictionary<int, object> CreateLocks = new();

        private readonly IContentService _contentService;
        private readonly IContentTypeService _contentTypeService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly ILogger<NodeCompetitionResultStore> _logger;
        private HashSet<string>? _aliases;

        public NodeCompetitionResultStore(
            IContentService contentService,
            IContentTypeService contentTypeService,
            IUmbracoContextAccessor umbracoContextAccessor,
            ILogger<NodeCompetitionResultStore> logger)
        {
            _contentService = contentService;
            _contentTypeService = contentTypeService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _logger = logger;
        }

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public CompetitionResultRecord? Get(int competitionId)
        {
            var node = FindNode(competitionId);
            return node is null ? null : FromContent(node, competitionId);
        }

        public IReadOnlyDictionary<int, CompetitionResultRecord> GetMany(IReadOnlyCollection<int> competitionIds)
        {
            var result = new Dictionary<int, CompetitionResultRecord>();
            if (competitionIds is null || competitionIds.Count == 0) return result;

            // Den publicerade cachen när den finns (sidor, listor) — en innehållsläsning per tävling hade
            // gjort klubbens tävlingslista till ett N+1. Alla skrivningar publicerar, så de två är lika.
            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            foreach (var id in competitionIds.Where(i => i > 0).Distinct())
            {
                var comp = ctx?.Content?.GetById(id);
                if (comp is not null)
                {
                    var p = PickPublished(comp.Children().Where(c => c.ContentType.Alias == CompetitionResultFields.NodeAlias));
                    if (p is not null) result[id] = FromPublished(p, id);
                    continue;
                }
                var rec = Get(id);
                if (rec is not null) result[id] = rec;
            }
            return result;
        }

        /// <summary>Nodlagringen läser den publicerade cachen, så GetMany är redan billig här.</summary>
        public IReadOnlyDictionary<int, bool> OfficialByCompetition(IReadOnlyCollection<int> competitionIds)
            => GetMany(competitionIds).ToDictionary(kv => kv.Key, kv => kv.Value.IsOfficial);

        public bool CanStore(CompetitionResultField field)
        {
            _aliases ??= LoadAliases();
            return _aliases.Contains(CompetitionResultFields.Alias(field));
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public CompetitionResultRecord Ensure(int competitionId) =>
            Update(competitionId, _ => { }, createIfMissing: true)
            ?? throw new InvalidOperationException($"Resultatlistan för tävling {competitionId} kunde inte skapas.");

        public CompetitionResultRecord? Update(int competitionId, Action<CompetitionResultRecord> change, bool createIfMissing = false)
        {
            if (competitionId <= 0) throw new ArgumentOutOfRangeException(nameof(competitionId));

            // Låset gäller bara skapandet: två samtidiga "Uppdatera" på en tävling utan lista fick förut
            // två noder, och sedan läste ytorna olika noder beroende på hur de letade.
            lock (CreateLocks.GetOrAdd(competitionId, _ => new object()))
            {
                var node = FindNode(competitionId);
                bool created = false;
                if (node is null)
                {
                    if (!createIfMissing) return null;
                    if (_contentService.GetById(competitionId) is null)
                        throw new InvalidOperationException($"Tävling {competitionId} finns inte.");
                    node = _contentService.Create(CompetitionResultFields.NodeName, competitionId, CompetitionResultFields.NodeAlias);
                    node.SetValue("resultType", CompetitionResultFields.DefaultResultType);
                    node.SetValue(CompetitionResultFields.Alias(CompetitionResultField.IsOfficial), false);
                    node.SetValue(CompetitionResultFields.Alias(CompetitionResultField.LastUpdated), DateTime.Now);
                    created = true;
                }

                var before = FromContent(node, competitionId);
                var after = before.Clone();
                change(after);

                var changed = Diff(before, after).ToList();
                if (!created && changed.Count == 0) return before;

                foreach (var field in changed)
                {
                    if (!CanStore(field)) throw new CompetitionResultFieldMissingException(field);
                    node.SetValue(CompetitionResultFields.Alias(field), ValueOf(after, field));
                }
                if (created || after.ResultType != before.ResultType)
                    node.SetValue("resultType", string.IsNullOrWhiteSpace(after.ResultType) ? CompetitionResultFields.DefaultResultType : after.ResultType);

                _contentService.Save(node);
                var publish = _contentService.Publish(node, new[] { "*" }, -1);
                if (!publish.Success)
                    _logger.LogWarning("Resultatlistan för tävling {CompetitionId} sparades men publicerades inte: {Result}",
                        competitionId, publish.Result);

                var saved = FromContent(node, competitionId);
                saved.IsPublished = publish.Success || node.Published;
                return saved;
            }
        }

        // ── Hjälpare ───────────────────────────────────────────────────────────────────────

        private IContent? FindNode(int competitionId)
        {
            if (competitionId <= 0) return null;
            var children = _contentService.GetPagedChildren(competitionId, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == CompetitionResultFields.NodeAlias)
                .ToList();
            return children.FirstOrDefault(c => c.Name == CompetitionResultFields.NodeName) ?? children.FirstOrDefault();
        }

        private static IPublishedContent? PickPublished(IEnumerable<IPublishedContent> candidates)
        {
            var list = candidates.ToList();
            return list.FirstOrDefault(c => c.Name == CompetitionResultFields.NodeName) ?? list.FirstOrDefault();
        }

        private HashSet<string> LoadAliases()
        {
            var type = _contentTypeService.Get(CompetitionResultFields.NodeAlias);
            return type is null
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(type.CompositionPropertyTypes.Select(p => p.Alias), StringComparer.OrdinalIgnoreCase);
        }

        private static CompetitionResultRecord FromContent(IContent node, int competitionId) => new()
        {
            Id = node.Id,
            CompetitionId = competitionId,
            ResultType = Str(node, "resultType", CompetitionResultFields.DefaultResultType),
            ResultData = Str(node, CompetitionResultField.ResultData),
            IsOfficial = Bool(node, CompetitionResultField.IsOfficial),
            SubIsOfficial = Bool(node, CompetitionResultField.SubIsOfficial),
            MergeConfig = Str(node, CompetitionResultField.MergeConfig),
            SubMergeConfig = Str(node, CompetitionResultField.SubMergeConfig),
            ClassNameOverrides = Str(node, CompetitionResultField.ClassNameOverrides),
            SubClassNameOverrides = Str(node, CompetitionResultField.SubClassNameOverrides),
            ShootOffConfig = Str(node, CompetitionResultField.ShootOffConfig),
            SubShootOffConfig = Str(node, CompetitionResultField.SubShootOffConfig),
            HonoraryAwardConfig = Str(node, CompetitionResultField.HonoraryAwardConfig),
            LastUpdated = Date(node.GetValue(CompetitionResultFields.Alias(CompetitionResultField.LastUpdated))),
            IsPublished = node.Published,
        };

        private static CompetitionResultRecord FromPublished(IPublishedContent p, int competitionId)
        {
            string S(CompetitionResultField f) { try { return p.Value<string>(CompetitionResultFields.Alias(f)) ?? ""; } catch { return ""; } }
            bool B(CompetitionResultField f) { try { return p.Value<bool>(CompetitionResultFields.Alias(f)); } catch { return false; } }
            object? last = null;
            try { last = p.Value(CompetitionResultFields.Alias(CompetitionResultField.LastUpdated)); } catch { }
            return new CompetitionResultRecord
            {
                Id = p.Id,
                CompetitionId = competitionId,
                ResultData = S(CompetitionResultField.ResultData),
                IsOfficial = B(CompetitionResultField.IsOfficial),
                SubIsOfficial = B(CompetitionResultField.SubIsOfficial),
                MergeConfig = S(CompetitionResultField.MergeConfig),
                SubMergeConfig = S(CompetitionResultField.SubMergeConfig),
                ClassNameOverrides = S(CompetitionResultField.ClassNameOverrides),
                SubClassNameOverrides = S(CompetitionResultField.SubClassNameOverrides),
                ShootOffConfig = S(CompetitionResultField.ShootOffConfig),
                SubShootOffConfig = S(CompetitionResultField.SubShootOffConfig),
                HonoraryAwardConfig = S(CompetitionResultField.HonoraryAwardConfig),
                LastUpdated = Date(last),
                IsPublished = true,
            };
        }

        private static string Str(IContent node, CompetitionResultField field) => Str(node, CompetitionResultFields.Alias(field), "");

        private static string Str(IContent node, string alias, string fallback)
        {
            if (!node.HasProperty(alias)) return fallback;
            try { return node.GetValue<string>(alias) ?? fallback; } catch { return node.GetValue(alias)?.ToString() ?? fallback; }
        }

        private static bool Bool(IContent node, CompetitionResultField field)
        {
            var alias = CompetitionResultFields.Alias(field);
            if (!node.HasProperty(alias)) return false;
            var v = node.GetValue(alias);
            return v switch
            {
                null => false,
                bool b => b,
                int i => i != 0,
                long l => l != 0,
                string s => s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }

        /// <summary>DateTime.MinValue är Umbracos "inget värde" för ett datum — läs det som null.</summary>
        private static DateTime? Date(object? v)
        {
            DateTime? d = v switch
            {
                DateTime dt => dt,
                string s when DateTime.TryParse(s, out var p) => p,
                _ => null,
            };
            return d is null || d.Value == DateTime.MinValue || d.Value.Year < 1900 ? null : d;
        }

        private static IEnumerable<CompetitionResultField> Diff(CompetitionResultRecord a, CompetitionResultRecord b)
        {
            foreach (var f in Enum.GetValues<CompetitionResultField>())
                if (!Equals(ValueOf(a, f), ValueOf(b, f))) yield return f;
        }

        private static object? ValueOf(CompetitionResultRecord r, CompetitionResultField f) => f switch
        {
            CompetitionResultField.ResultData => r.ResultData ?? "",
            CompetitionResultField.IsOfficial => r.IsOfficial,
            CompetitionResultField.SubIsOfficial => r.SubIsOfficial,
            CompetitionResultField.MergeConfig => r.MergeConfig ?? "",
            CompetitionResultField.SubMergeConfig => r.SubMergeConfig ?? "",
            CompetitionResultField.ClassNameOverrides => r.ClassNameOverrides ?? "",
            CompetitionResultField.SubClassNameOverrides => r.SubClassNameOverrides ?? "",
            CompetitionResultField.ShootOffConfig => r.ShootOffConfig ?? "",
            CompetitionResultField.SubShootOffConfig => r.SubShootOffConfig ?? "",
            CompetitionResultField.HonoraryAwardConfig => r.HonoraryAwardConfig ?? "",
            CompetitionResultField.LastUpdated => r.LastUpdated,
            _ => null,
        };
    }
}
