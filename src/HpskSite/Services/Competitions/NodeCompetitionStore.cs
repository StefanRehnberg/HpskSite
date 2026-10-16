using HpskSite.Models.Competitions;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Tävlingarna som <c>competition</c>-noder — dagens lagring, och den som gäller tills tävlingarna
    /// flyttats till tabellen (C3-planen S1).
    ///
    /// <para><b>Läser RÅVÄRDET</b> (<c>GetSourceValue</c> på det publicerade, <c>GetValue</c> på utkastet)
    /// och tolkar det genom <see cref="CompetitionFieldValues"/> — aldrig Umbracos typade läsning, som
    /// kastar på en FlexibleDropdown med ett rent strängvärde.</para>
    ///
    /// <para><b>Ingen trädgenomgång och inga <c>.Parent</c>/<c>.Children</c></b> (K4): listorna tar id:n
    /// ur en SQL-fråga på dokumenttypen och hämtar noderna ur den publicerade cachen; föräldern tas ur
    /// nodens sökväg.</para>
    ///
    /// <para>Varje skrivning sparar och — när tävlingen var publicerad — publicerar, som redigeringen gör i
    /// dag. Publiceringen görs som användar-id <c>-1</c> (medlemmens id ger
    /// FK_umbracoContentVersion_umbracoUser_id och publiceringen faller).</para>
    /// </summary>
    public class NodeCompetitionStore : ICompetitionStore
    {
        private readonly NodeStoreCore<CompetitionInfo> _core;
        private readonly IContentService _contentService;
        private readonly CompetitionTreePlacement _placement;

        public NodeCompetitionStore(IContentService contentService, IUmbracoContextAccessor umbracoContextAccessor,
            IScopeProvider scopeProvider, ILogger<NodeCompetitionStore> logger, CompetitionTreePlacement placement,
            CompetitionChangeRecorder recorder)
        {
            _contentService = contentService;
            _placement = placement;
            _core = new NodeStoreCore<CompetitionInfo>(CompetitionFieldTable.NodeAlias, CompetitionFieldTable.Fields,
                contentService, umbracoContextAccessor, scopeProvider, logger, Fill, c => c.Name, recorder);
        }

        public CompetitionInfo? Get(int id) => _core.Get(id);
        public CompetitionInfo? GetSaved(int id) => _core.GetSaved(id);
        public IReadOnlyDictionary<int, CompetitionInfo> GetMany(IReadOnlyCollection<int> ids) => _core.GetMany(ids);
        public IReadOnlyList<CompetitionInfo> List() => _core.List(null);
        public IReadOnlyList<CompetitionInfo> ListBySeries(int seriesId) => seriesId <= 0 ? Array.Empty<CompetitionInfo>() : _core.List(seriesId);

        public CompetitionUpdateResult<CompetitionInfo> Update(int id, long? expectedVersion, Action<CompetitionInfo> change) =>
            _core.Update(id, expectedVersion, change);

        public CompetitionUpdateResult<CompetitionInfo> Create(int parentId, string name, Action<CompetitionInfo> init) =>
            _core.Create(parentId, name, init);

        /// <summary>
        /// Serien, eller årsmappen för tävlingens datum (dagens år när datumet saknas) — samma placering
        /// som flytten alltid haft.
        /// </summary>
        public CompetitionUpdateResult<CompetitionInfo> MoveToSeries(int id, int? seriesId)
        {
            var current = _core.GetSaved(id);
            if (current is null) return new CompetitionUpdateResult<CompetitionInfo> { Outcome = CompetitionUpdateOutcome.NotFound };
            int parentId;
            if (seriesId is > 0)
            {
                var series = _contentService.GetById(seriesId.Value);
                if (series is null || series.Trashed || series.ContentType.Alias != CompetitionSeriesFieldTable.NodeAlias)
                    throw new InvalidOperationException("Serien finns inte.");
                parentId = series.Id;
            }
            else
            {
                parentId = _placement.YearFolderId((current.CompetitionDate ?? DateTime.Now).Year);
            }
            return _core.Move(id, parentId);
        }

        public CompetitionUpdateResult<CompetitionInfo> Trash(int id) => _core.Trash(id);

        private static void Fill(CompetitionInfo c, NodeMeta m)
        {
            c.Id = m.Id; c.Key = m.Key; c.Name = m.Name; c.Version = m.Version;
            c.CreateDate = m.CreateDate; c.UpdateDate = m.UpdateDate; c.SortOrder = m.SortOrder;
            c.IsPublished = m.IsPublished; c.ParentId = m.ParentId; c.ParentKind = m.ParentKind;
            c.UrlSegment = m.UrlSegment;
            c.SeriesId = m.ParentKind == CompetitionSeriesFieldTable.NodeAlias ? m.ParentId : null;
        }
    }

    /// <summary>Serierna som <c>competitionSeries</c>-noder. Samma regler som <see cref="NodeCompetitionStore"/>.</summary>
    public class NodeCompetitionSeriesStore : ICompetitionSeriesStore
    {
        private readonly NodeStoreCore<CompetitionSeriesInfo> _core;

        public NodeCompetitionSeriesStore(IContentService contentService, IUmbracoContextAccessor umbracoContextAccessor,
            IScopeProvider scopeProvider, ILogger<NodeCompetitionSeriesStore> logger,
            CompetitionChangeRecorder recorder)
        {
            _core = new NodeStoreCore<CompetitionSeriesInfo>(CompetitionSeriesFieldTable.NodeAlias, CompetitionSeriesFieldTable.Fields,
                contentService, umbracoContextAccessor, scopeProvider, logger, Fill, s => s.Name, recorder);
        }

        public CompetitionSeriesInfo? Get(int id) => _core.Get(id);
        public CompetitionSeriesInfo? GetSaved(int id) => _core.GetSaved(id);
        public IReadOnlyDictionary<int, CompetitionSeriesInfo> GetMany(IReadOnlyCollection<int> ids) => _core.GetMany(ids);
        public IReadOnlyList<CompetitionSeriesInfo> List() => _core.List(null);

        public CompetitionUpdateResult<CompetitionSeriesInfo> Update(int id, long? expectedVersion, Action<CompetitionSeriesInfo> change) =>
            _core.Update(id, expectedVersion, change);

        public CompetitionUpdateResult<CompetitionSeriesInfo> Create(int parentId, string name, Action<CompetitionSeriesInfo> init) =>
            _core.Create(parentId, name, init);

        public CompetitionUpdateResult<CompetitionSeriesInfo> Trash(int id) => _core.Trash(id);

        private static void Fill(CompetitionSeriesInfo s, NodeMeta m)
        {
            s.Id = m.Id; s.Key = m.Key; s.Name = m.Name; s.Version = m.Version;
            s.CreateDate = m.CreateDate; s.UpdateDate = m.UpdateDate; s.SortOrder = m.SortOrder;
            s.IsPublished = m.IsPublished; s.ParentId = m.ParentId; s.ParentKind = m.ParentKind;
            s.UrlSegment = m.UrlSegment;
        }
    }

    /// <summary>Nodens egna uppgifter, gemensamma för tävling och serie.</summary>
    public sealed record NodeMeta(int Id, Guid Key, string Name, long Version, DateTime CreateDate, DateTime UpdateDate,
        int SortOrder, bool IsPublished, int ParentId, string ParentKind, string UrlSegment);

    /// <summary>Det gemensamma för tävlings- och serienoderna — en implementation av läs- och skrivreglerna.</summary>
    internal sealed class NodeStoreCore<T> where T : class, new()
    {
        private readonly string _alias;
        private readonly IReadOnlyList<CompetitionFieldDef<T>> _fields;
        private readonly IContentService _contentService;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger _logger;
        private readonly Action<T, NodeMeta> _fill;
        private readonly Func<T, string> _nameOf;
        private readonly CompetitionChangeRecorder _recorder;

        public NodeStoreCore(string alias, IReadOnlyList<CompetitionFieldDef<T>> fields, IContentService contentService,
            IUmbracoContextAccessor umbracoContextAccessor, IScopeProvider scopeProvider, ILogger logger, Action<T, NodeMeta> fill,
            Func<T, string> nameOf, CompetitionChangeRecorder recorder)
        {
            _nameOf = nameOf;
            _recorder = recorder;
            _alias = alias;
            _fields = fields;
            _contentService = contentService;
            _umbracoContextAccessor = umbracoContextAccessor;
            _scopeProvider = scopeProvider;
            _logger = logger;
            _fill = fill;
        }

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public T? Get(int id)
        {
            if (id <= 0) return null;
            var pub = Cache()?.GetById(id);
            return pub is not null && pub.ContentType.Alias == _alias ? FromPublished(pub) : null;
        }

        public T? GetSaved(int id)
        {
            if (id <= 0) return null;
            var node = _contentService.GetById(id);
            return node is not null && !node.Trashed && node.ContentType.Alias == _alias ? FromContent(node) : null;
        }

        public IReadOnlyDictionary<int, T> GetMany(IReadOnlyCollection<int> ids)
        {
            var result = new Dictionary<int, T>();
            if (ids is null || ids.Count == 0) return result;
            var cache = Cache();
            if (cache is null) return result;
            foreach (var id in ids.Where(i => i > 0).Distinct())
            {
                var pub = cache.GetById(id);
                if (pub is not null && pub.ContentType.Alias == _alias) result[id] = FromPublished(pub);
            }
            return result;
        }

        /// <summary>Alla publicerade av typen (eller bara en förälders), ordnade som trädet: förälder, sortering.</summary>
        public IReadOnlyList<T> List(int? parentId)
        {
            var cache = Cache();
            if (cache is null) return Array.Empty<T>();
            var list = new List<T>();
            foreach (var id in IdsOfType(parentId))
            {
                var pub = cache.GetById(id);
                if (pub is not null && pub.ContentType.Alias == _alias) list.Add(FromPublished(pub));
            }
            return list;
        }

        private List<int> IdsOfType(int? parentId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var sql = @"SELECT n.id FROM umbracoNode n
                        JOIN umbracoContent c ON c.nodeId = n.id
                        JOIN cmsContentType ct ON ct.nodeId = c.contentTypeId
                        WHERE ct.alias = @0 AND n.trashed = 0" + (parentId.HasValue ? " AND n.parentId = @1" : "") +
                      (parentId.HasValue ? " ORDER BY n.sortOrder, n.id" : " ORDER BY n.id");
            return parentId.HasValue
                ? scope.Database.Fetch<int>(sql, _alias, parentId.Value)
                : scope.Database.Fetch<int>(sql, _alias);
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public CompetitionUpdateResult<T> Update(int id, long? expectedVersion, Action<T> change)
        {
            var node = id > 0 ? _contentService.GetById(id) : null;
            if (node is null || node.Trashed || node.ContentType.Alias != _alias)
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.NotFound };

            var before = FromContent(node);
            if (expectedVersion.HasValue && expectedVersion.Value != VersionOf(node))
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Conflict, Record = before };

            var after = FromContent(node);
            change(after);

            var changed = _fields.Where(f => !Equals(f.Get(before), f.Get(after))).ToList();
            // Nodens namn följer med (redigeringen byter det när tävlingens namn ändras). Ett tomt namn
            // skrivs aldrig — en nod utan namn går inte att spara.
            var newName = _nameOf(after);
            var renamed = !string.IsNullOrWhiteSpace(newName) && newName != _nameOf(before);
            if (changed.Count == 0 && !renamed)
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = before };

            WriteFields(node, changed, after);
            if (renamed) node.Name = newName.Trim();

            var wasPublished = node.Published;
            _contentService.Save(node);
            var publishProblem = wasPublished ? Publish(node) : null;

            var log = changed.ToDictionary(f => f.Alias, f => (f.Get(before), f.Get(after)));
            if (renamed) log["@name"] = (_nameOf(before), newName.Trim());
            _recorder.Record(_alias, id, "Update", log);

            var saved = _contentService.GetById(id) ?? node;
            return new CompetitionUpdateResult<T>
            {
                Outcome = CompetitionUpdateOutcome.Saved,
                Record = FromContent(saved),
                ChangedFields = changed.Select(f => f.Alias).ToList(),
                PublishProblem = publishProblem,
            };
        }

        /// <summary>
        /// Skapar en ny nod under <paramref name="parentId"/>, låter <paramref name="init"/> fylla posten och
        /// skriver de fält som fick ett värde. Publiceras alltid, som skapandet alltid gjort.
        ///
        /// <para>⚠️ Fält där ett saknat värde betyder sant (<c>isActive</c>) skrivs alltid uttryckligen: en
        /// läsare utanför lagringen läser annars ett saknat värde som falskt och gömmer tävlingen.</para>
        /// </summary>
        public CompetitionUpdateResult<T> Create(int parentId, string name, Action<T> init)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Namnet saknas.", nameof(name));
            var node = _contentService.Create(name.Trim(), parentId, _alias);

            var blank = FromContent(node);
            var after = FromContent(node);
            init(after);

            var written = _fields
                .Where(f => f.Shape == CompetitionFieldShape.BoolDefaultTrue || !Equals(f.Get(blank), f.Get(after)))
                .ToList();
            WriteFields(node, written, after);
            var newName = _nameOf(after);
            if (!string.IsNullOrWhiteSpace(newName) && newName.Trim() != node.Name) node.Name = newName.Trim();

            var save = _contentService.Save(node);
            if (!save.Success)
            {
                var msg = string.Join(", ", save.EventMessages?.GetAll().Select(e => e.Message) ?? Array.Empty<string>());
                throw new InvalidOperationException($"{_alias} kunde inte sparas: {msg}");
            }
            var publishProblem = Publish(node);

            var log = written.ToDictionary(f => f.Alias, f => ((object?)null, f.Get(after)));
            log["@name"] = (null, node.Name);
            log["@parentId"] = (null, parentId);
            _recorder.Record(_alias, node.Id, "Create", log);

            var saved = _contentService.GetById(node.Id) ?? node;
            return new CompetitionUpdateResult<T>
            {
                Outcome = CompetitionUpdateOutcome.Saved,
                Record = FromContent(saved),
                ChangedFields = written.Select(f => f.Alias).ToList(),
                PublishProblem = publishProblem,
            };
        }

        /// <summary>Flyttar noden under <paramref name="parentId"/> (ingenting om den redan ligger där).</summary>
        public CompetitionUpdateResult<T> Move(int id, int parentId)
        {
            var node = id > 0 ? _contentService.GetById(id) : null;
            if (node is null || node.Trashed || node.ContentType.Alias != _alias)
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.NotFound };
            if (node.ParentId != parentId)
            {
                var from = node.ParentId;
                var moved = _contentService.Move(node, parentId, -1);
                if (!moved.Success)
                    throw new InvalidOperationException($"{_alias} {id} kunde inte flyttas: " +
                        string.Join(", ", moved.EventMessages?.GetAll().Select(e => e.Message) ?? Array.Empty<string>()));
                _recorder.Record(_alias, id, "Move", new Dictionary<string, (object?, object?)> { ["@parentId"] = (from, parentId) });
            }
            var saved = _contentService.GetById(id) ?? node;
            return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = FromContent(saved) };
        }

        /// <summary>
        /// Lägger noden i papperskorgen (avpubliceras av Umbraco). Inget under den raderas — en permanent
        /// radering tog förut med sig anmälningsnav, gamla fakturor och resultatnoden.
        /// </summary>
        public CompetitionUpdateResult<T> Trash(int id)
        {
            var node = id > 0 ? _contentService.GetById(id) : null;
            if (node is null || node.Trashed || node.ContentType.Alias != _alias)
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.NotFound };
            var before = FromContent(node);
            var fromParent = node.ParentId;
            var trashed = _contentService.MoveToRecycleBin(node, -1);
            if (!trashed.Success)
                throw new InvalidOperationException($"{_alias} {id} kunde inte läggas i papperskorgen: " +
                    string.Join(", ", trashed.EventMessages?.GetAll().Select(e => e.Message) ?? Array.Empty<string>()));
            _recorder.Record(_alias, id, "Trash", new Dictionary<string, (object?, object?)> { ["@trashed"] = (false, true), ["@parentId"] = (fromParent, null) });
            return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = before };
        }

        private void WriteFields(IContent node, IEnumerable<CompetitionFieldDef<T>> fields, T record)
        {
            foreach (var f in fields)
            {
                if (!node.HasProperty(f.Alias))
                    throw new InvalidOperationException($"Kan inte spara {f.Alias} — egenskapen saknas på doktypen {_alias}.");
                node.SetValue(f.Alias, CompetitionFieldValues.ToNode(f.Shape, f.Get(record)));
            }
        }

        /// <summary>Publicerar som användar-id -1. Null = publicerad, annars vad som gick fel.</summary>
        private string? Publish(IContent node)
        {
            var publish = _contentService.Publish(node, new[] { "*" }, -1);
            if (publish.Success) return null;
            var invalid = publish.InvalidProperties != null && publish.InvalidProperties.Any()
                ? " Ogiltiga eller obligatoriska fält: " + string.Join(", ", publish.InvalidProperties.Select(p => p.Alias)) + "."
                : "";
            _logger.LogWarning("{Alias} {Id} sparades men publicerades inte: {Result}{Invalid}", _alias, node.Id, publish.Result, invalid);
            return $"Ändringen sparades men kunde inte publiceras ({publish.Result}).{invalid}";
        }

        // ── Mappning ───────────────────────────────────────────────────────────────────────

        private T FromPublished(IPublishedContent pub)
        {
            var r = new T();
            foreach (var f in _fields)
            {
                object? raw = null;
                try { raw = pub.GetProperty(f.Alias)?.GetSourceValue(); }
                catch (Exception ex) { _logger.LogWarning(ex, "{Alias} {Id}: {Field} gick inte att läsa", _alias, pub.Id, f.Alias); }
                f.Set(r, CompetitionFieldValues.Read(f.Shape, raw));
            }
            var parentId = ParentIdFromPath(pub.Path);
            _fill(r, new NodeMeta(pub.Id, pub.Key, pub.Name ?? "", pub.UpdateDate.Ticks, pub.CreateDate, pub.UpdateDate,
                pub.SortOrder, true, parentId, ParentAlias(parentId), pub.UrlSegment ?? ""));
            return r;
        }

        private T FromContent(IContent node)
        {
            var r = new T();
            foreach (var f in _fields)
                f.Set(r, CompetitionFieldValues.Read(f.Shape, node.HasProperty(f.Alias) ? node.GetValue(f.Alias) : null));
            var segment = Cache()?.GetById(node.Id)?.UrlSegment ?? "";
            _fill(r, new NodeMeta(node.Id, node.Key, node.Name ?? "", VersionOf(node), node.CreateDate, node.UpdateDate,
                node.SortOrder, node.Published, node.ParentId, ParentAlias(node.ParentId), segment));
            return r;
        }

        /// <summary>Versionen = senaste sparningens tid. Lika för utkast och publicerat när de är samma.</summary>
        private static long VersionOf(IContent node) => node.UpdateDate.Ticks;

        internal static int ParentIdFromPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return -1;
            var parts = path.Split(',');
            return parts.Length >= 2 && int.TryParse(parts[^2], out var p) ? p : -1;
        }

        private string ParentAlias(int parentId)
        {
            if (parentId <= 0) return "";
            var pub = Cache()?.GetById(parentId);
            if (pub is not null) return pub.ContentType.Alias;
            return _contentService.GetById(parentId)?.ContentType.Alias ?? "";
        }

        private Umbraco.Cms.Core.PublishedCache.IPublishedContentCache? Cache()
        {
            _umbracoContextAccessor.TryGetUmbracoContext(out var ctx);
            return ctx?.Content;
        }
    }
}
