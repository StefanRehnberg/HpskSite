using System.Buffers.Binary;
using HpskSite.Models.Competitions;
using HpskSite.Services.Addresses;
using NPoco;
using Umbraco.Cms.Core.Strings;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Tävlingarna i tabellen <c>dbo.Competition</c> (C3-planen S1, klar i §5).
    ///
    /// <para>Kolumnerna är <see cref="CompetitionFieldTable"/>s egenskaper — en kolumn per fält, samma namn.
    /// Inget publiceringsläge (§9a): <see cref="Get"/> och <see cref="GetSaved"/> svarar samma rad.</para>
    ///
    /// <para><see cref="Update"/> läser raden med <c>UPDLOCK, HOLDLOCK</c> i samma transaktion som den skriver
    /// och jämför <c>RowVersion</c> med versionen användaren såg. Använder <see cref="IScopeProvider"/>,
    /// aldrig en egen anslutning (lagringen anropas inifrån andras scope).</para>
    ///
    /// <para><b>Efter varje skrivning</b> (när transaktionen är stängd): ändringsloggen
    /// (<see cref="CompetitionChangeRecorder"/>, som också räknar upp versionsraden) och adressen
    /// (<see cref="CompetitionAddressService"/>) — i tabelläget finns ingen publicering som sätter den.
    /// Ett fel i dem stoppar inte skrivningen; den är redan sparad.</para>
    ///
    /// <para><b>Mjuk radering:</b> <c>Trash</c> sätter <c>DeletedUtc</c>/<c>DeletedBy</c>. En raderad rad syns inte i
    /// någon läsning och går inte att ändra; adressen ligger kvar (sidan svarar 404 tills raden läggs tillbaka).</para>
    /// </summary>
    public class SqlCompetitionStore : ICompetitionStore
    {
        public const string Table = "dbo.Competition";
        private readonly SqlStoreCore<CompetitionInfo> _core;
        private readonly IServiceProvider _services;

        public SqlCompetitionStore(IScopeProvider scopeProvider, CompetitionChangeRecorder recorder,
            IShortStringHelper shortStrings, IServiceProvider services, ILogger<SqlCompetitionStore> logger)
        {
            _services = services;
            _core = new SqlStoreCore<CompetitionInfo>(Table, CompetitionFieldTable.NodeAlias, CompetitionFieldTable.Fields,
                scopeProvider, recorder, shortStrings, logger, new[] { "SeriesId" }, Fill, Meta, SyncAddress);
        }

        public CompetitionInfo? Get(int id) => _core.Get(id);
        public CompetitionInfo? GetSaved(int id) => _core.Get(id);
        public IReadOnlyDictionary<int, CompetitionInfo> GetMany(IReadOnlyCollection<int> ids) => _core.GetMany(ids);
        public IReadOnlyList<CompetitionInfo> List() => _core.List(null);
        public IReadOnlyList<CompetitionInfo> ListBySeries(int seriesId) =>
            seriesId <= 0 ? Array.Empty<CompetitionInfo>() : _core.List(("SeriesId", seriesId));
        public IReadOnlyList<CompetitionInfo> ListAllInSeries(int seriesId) => ListBySeries(seriesId);

        public CompetitionUpdateResult<CompetitionInfo> Update(int id, long? expectedVersion, Action<CompetitionInfo> change) =>
            _core.Update(id, expectedVersion, change, (before, after) => before.SeriesId != after.SeriesId
                ? SeriesColumns(after.SeriesId)
                : new Dictionary<string, object?>());

        /// <summary>Sist i serien, eller utanför serier. Serien måste finnas (och inte vara raderad).</summary>
        public CompetitionUpdateResult<CompetitionInfo> Create(int? seriesId, string name, Action<CompetitionInfo> init)
        {
            var series = seriesId is > 0 ? RequireSeries(seriesId.Value) : (int?)null;
            return _core.Create(name, init, SeriesColumns(series), series.HasValue ? ("SeriesId", series.Value) : null);
        }

        public CompetitionUpdateResult<CompetitionInfo> MoveToSeries(int id, int? seriesId)
        {
            var series = seriesId is > 0 ? RequireSeries(seriesId.Value) : (int?)null;
            return _core.Move(id, SeriesColumns(series), series.HasValue ? ("SeriesId", series.Value) : null,
                c => c.SeriesId == series, c => c.SeriesId ?? -1, series ?? -1);
        }

        public CompetitionUpdateResult<CompetitionInfo> Trash(int id) => _core.Trash(id, c => c.SeriesId ?? -1);

        /// <summary>Raden även när den är raderad. Bara för flytten (§5b).</summary>
        public (CompetitionInfo Record, bool Deleted)? GetForMigration(int id) => _core.GetIncludingDeleted(id);

        /// <summary>
        /// Serie-id:t och trädets förälder i samma form som de flyttade raderna: i en serie är föräldern serien.
        /// </summary>
        private static Dictionary<string, object?> SeriesColumns(int? seriesId) => new()
        {
            ["SeriesId"] = seriesId,
            ["LegacyParentId"] = seriesId ?? -1,
            ["LegacyParentKind"] = seriesId.HasValue ? CompetitionSeriesFieldTable.NodeAlias : "",
        };

        private int RequireSeries(int seriesId) =>
            _core.Exists(SqlCompetitionSeriesStore.Table, seriesId) ? seriesId : throw new InvalidOperationException("Serien finns inte.");

        private void SyncAddress(int id) =>
            _services.GetRequiredService<CompetitionAddressService>().SyncCompetition(id);

        private static void Fill(CompetitionInfo c, IDictionary<string, object?> row, SqlMeta m)
        {
            c.Id = m.Id; c.Key = m.Key; c.Name = m.Name; c.Version = m.Version;
            c.CreateDate = m.CreateDate; c.UpdateDate = m.UpdateDate; c.SortOrder = m.SortOrder;
            c.IsPublished = true; c.ParentId = m.LegacyParentId; c.ParentKind = m.LegacyParentKind;
            c.UrlSegment = m.UrlSegment;
            c.SeriesId = CompetitionFieldValues.ReadInt(SqlStoreCore<CompetitionInfo>.Value(row, "SeriesId"));
        }

        private static SqlMeta Meta(CompetitionInfo c) => new(c.Id, c.Key, c.Name, 0, c.CreateDate, c.UpdateDate, c.SortOrder,
            c.ParentId, c.ParentKind, c.UrlSegment);
    }

    /// <summary>Serierna i <c>dbo.CompetitionSeries</c>. Samma regler som <see cref="SqlCompetitionStore"/>.</summary>
    public class SqlCompetitionSeriesStore : ICompetitionSeriesStore
    {
        public const string Table = "dbo.CompetitionSeries";
        private readonly SqlStoreCore<CompetitionSeriesInfo> _core;
        private readonly IServiceProvider _services;

        public SqlCompetitionSeriesStore(IScopeProvider scopeProvider, CompetitionChangeRecorder recorder,
            IShortStringHelper shortStrings, IServiceProvider services, ILogger<SqlCompetitionSeriesStore> logger)
        {
            _services = services;
            _core = new SqlStoreCore<CompetitionSeriesInfo>(Table, CompetitionSeriesFieldTable.NodeAlias, CompetitionSeriesFieldTable.Fields,
                scopeProvider, recorder, shortStrings, logger, Array.Empty<string>(), Fill, Meta, SyncAddress);
        }

        public CompetitionSeriesInfo? Get(int id) => _core.Get(id);
        public CompetitionSeriesInfo? GetSaved(int id) => _core.Get(id);
        public IReadOnlyDictionary<int, CompetitionSeriesInfo> GetMany(IReadOnlyCollection<int> ids) => _core.GetMany(ids);
        public IReadOnlyList<CompetitionSeriesInfo> List() => _core.List(null);

        public CompetitionUpdateResult<CompetitionSeriesInfo> Update(int id, long? expectedVersion, Action<CompetitionSeriesInfo> change) =>
            _core.Update(id, expectedVersion, change, (_, _) => new Dictionary<string, object?>());

        public CompetitionUpdateResult<CompetitionSeriesInfo> Create(string name, Action<CompetitionSeriesInfo> init) =>
            _core.Create(name, init, new Dictionary<string, object?>(), null);

        public CompetitionUpdateResult<CompetitionSeriesInfo> Trash(int id) => _core.Trash(id, s => s.ParentId);

        /// <summary>Raden även när den är raderad. Bara för flytten (§5b).</summary>
        public (CompetitionSeriesInfo Record, bool Deleted)? GetForMigration(int id) => _core.GetIncludingDeleted(id);

        private void SyncAddress(int id) =>
            _services.GetRequiredService<CompetitionAddressService>().SyncSeries(id);

        private static void Fill(CompetitionSeriesInfo s, IDictionary<string, object?> row, SqlMeta m)
        {
            s.Id = m.Id; s.Key = m.Key; s.Name = m.Name; s.Version = m.Version;
            s.CreateDate = m.CreateDate; s.UpdateDate = m.UpdateDate; s.SortOrder = m.SortOrder;
            s.IsPublished = true; s.ParentId = m.LegacyParentId; s.ParentKind = m.LegacyParentKind;
            s.UrlSegment = m.UrlSegment;
        }

        private static SqlMeta Meta(CompetitionSeriesInfo s) => new(s.Id, s.Key, s.Name, 0, s.CreateDate, s.UpdateDate, s.SortOrder,
            s.ParentId, s.ParentKind, s.UrlSegment);
    }

    public sealed record SqlMeta(int Id, Guid Key, string Name, long Version, DateTime CreateDate, DateTime UpdateDate,
        int SortOrder, int LegacyParentId, string LegacyParentKind, string UrlSegment);

    /// <summary>Det gemensamma för tävlings- och serietabellen.</summary>
    internal sealed class SqlStoreCore<T> where T : class, new()
    {
        /// <summary>Varje läsning och skrivning går förbi de raderade raderna.</summary>
        public const string NotDeleted = "DeletedUtc IS NULL";

        private readonly string _table;
        private readonly string _kind;
        private readonly IReadOnlyList<CompetitionFieldDef<T>> _fields;
        private readonly IScopeProvider _scopeProvider;
        private readonly CompetitionChangeRecorder _recorder;
        private readonly IShortStringHelper _shortStrings;
        private readonly ILogger _logger;
        private readonly string[] _extraColumns;
        private readonly Action<T, IDictionary<string, object?>, SqlMeta> _fill;
        private readonly Func<T, SqlMeta> _meta;
        private readonly Action<int> _syncAddress;

        public SqlStoreCore(string table, string kind, IReadOnlyList<CompetitionFieldDef<T>> fields, IScopeProvider scopeProvider,
            CompetitionChangeRecorder recorder, IShortStringHelper shortStrings, ILogger logger, string[] extraColumns,
            Action<T, IDictionary<string, object?>, SqlMeta> fill, Func<T, SqlMeta> meta, Action<int> syncAddress)
        {
            _table = table;
            _kind = kind;
            _fields = fields;
            _scopeProvider = scopeProvider;
            _recorder = recorder;
            _shortStrings = shortStrings;
            _logger = logger;
            _extraColumns = extraColumns;
            _fill = fill;
            _meta = meta;
            _syncAddress = syncAddress;
        }

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public T? Get(int id)
        {
            if (id <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE Id = @0 AND {NotDeleted}", id).FirstOrDefault();
            return row is null ? null : ToRecord(row);
        }

        public IReadOnlyDictionary<int, T> GetMany(IReadOnlyCollection<int> ids)
        {
            var result = new Dictionary<int, T>();
            var list = ids?.Where(i => i > 0).Distinct().ToList() ?? new List<int>();
            if (list.Count == 0) return result;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // ⚠️ IN (@0) tar slut kring 2100 parametrar och gör det tyst — i bitar om 1000.
            foreach (var chunk in list.Chunk(1000))
                foreach (var row in scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE Id IN (@0) AND {NotDeleted}", chunk))
                {
                    var r = ToRecord(row);
                    result[(int)row["Id"]!] = r;
                }
            return result;
        }

        public IReadOnlyList<T> List((string Column, int Value)? filter)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var rows = filter.HasValue
                ? scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE [{filter.Value.Column}] = @0 AND {NotDeleted} ORDER BY SortOrder, Id", filter.Value.Value)
                : scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE {NotDeleted} ORDER BY Id");
            return rows.Select(ToRecord).ToList();
        }

        /// <summary>
        /// Raden även när den är raderad, och om den är det. BARA för flyttens jämförelse (§5b) — ingen yta får
        /// läsa en raderad rad.
        /// </summary>
        public (T Record, bool Deleted)? GetIncludingDeleted(int id)
        {
            if (id <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE Id = @0", id).FirstOrDefault();
            return row is null ? null : (ToRecord(row), Value(row, "DeletedUtc") != null);
        }

        /// <summary>Finns en icke raderad rad med id:t i <paramref name="table"/> (en av de två tävlingstabellerna)?</summary>
        public bool Exists(string table, int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>($"SELECT COUNT(*) FROM {table} WHERE Id = @0 AND {NotDeleted}", id) > 0;
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public CompetitionUpdateResult<T> Update(int id, long? expectedVersion, Action<T> change,
            Func<T, T, Dictionary<string, object?>> extraChanges)
        {
            if (id <= 0) return NotFound();
            T before, after;
            List<CompetitionFieldDef<T>> changed;
            Dictionary<string, object?> extra;
            string? renamedTo = null;
            using (var scope = _scopeProvider.CreateScope())
            {
                var db = scope.Database;
                var row = db.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0 AND {NotDeleted}", id).FirstOrDefault();
                if (row is null)
                {
                    scope.Complete();
                    return NotFound();
                }

                before = ToRecord(row);
                if (expectedVersion.HasValue && expectedVersion.Value != RowVersion(row))
                {
                    scope.Complete();
                    return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Conflict, Record = before };
                }

                after = ToRecord(row);
                change(after);
                changed = _fields.Where(f => !Equals(f.Get(before), f.Get(after))).ToList();
                extra = extraChanges(before, after);
                // Namnet följer med som i nodlagringen; ett tomt namn skrivs aldrig. Adressdelen följer namnet,
                // som Umbraco gör vid publiceringen (den gamla adressen blir en historikrad i PageAddress).
                var name = _meta(after).Name?.Trim() ?? "";
                if (!string.IsNullOrWhiteSpace(name) && name != _meta(before).Name) renamedTo = name;
                if (changed.Count == 0 && extra.Count == 0 && renamedTo == null)
                {
                    scope.Complete();
                    return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = before };
                }

                var sets = new Dictionary<string, object?>();
                foreach (var f in changed) sets[f.Property] = f.Get(after);
                foreach (var kv in extra) sets[kv.Key] = kv.Value;
                if (renamedTo != null) { sets["Name"] = renamedTo; sets["UrlSegment"] = Segment(renamedTo); }
                sets["UpdateDate"] = DateTime.Now;
                sets["UpdatedBy"] = _recorder.CurrentMemberId();
                Set(db, id, sets);
                scope.Complete();
            }

            var log = changed.ToDictionary(f => f.Alias, f => (f.Get(before), f.Get(after)));
            foreach (var kv in extra.Where(kv => kv.Key == "SeriesId")) log["@parentId"] = (Value(before, kv.Key), kv.Value);
            if (renamedTo != null) log["@name"] = (_meta(before).Name, renamedTo);
            AfterWrite(id, "Update", log);
            return new CompetitionUpdateResult<T>
            {
                Outcome = CompetitionUpdateOutcome.Saved,
                Record = Get(id),
                ChangedFields = changed.Select(f => f.Alias).Concat(extra.Keys.Where(k => _extraColumns.Contains(k))).ToList(),
            };
        }

        /// <summary>
        /// En ny rad. <paramref name="init"/> fyller en tom post; varje fält skrivs (raden har inga saknade värden).
        /// <paramref name="sortGroup"/> = kolumnen och värdet raden sorteras sist bland, null = sorteringsordning 0.
        /// </summary>
        public CompetitionUpdateResult<T> Create(string name, Action<T> init, Dictionary<string, object?> columns,
            (string Column, int Value)? sortGroup)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Namnet saknas.", nameof(name));
            var blank = Blank();
            var after = Blank();
            init(after);
            var finalName = _meta(after).Name?.Trim();
            if (string.IsNullOrWhiteSpace(finalName)) finalName = name.Trim();

            int id;
            using (var scope = _scopeProvider.CreateScope())
            {
                var db = scope.Database;
                var values = new Dictionary<string, object?>(columns);
                foreach (var f in _fields) values[f.Property] = f.Get(after);
                var now = DateTime.Now;
                values["Key"] = Guid.NewGuid();
                values["Name"] = finalName;
                values["UrlSegment"] = Segment(finalName);
                values["CreateDate"] = now;
                values["UpdateDate"] = now;
                values["CreatedBy"] = _recorder.CurrentMemberId();
                values["SortOrder"] = sortGroup.HasValue ? NextSortOrder(db, sortGroup.Value) : 0;
                var cols = values.Keys.ToList();
                id = db.ExecuteScalar<int>(
                    $"INSERT INTO {_table} ({string.Join(", ", cols.Select(c => $"[{c}]"))}) VALUES ({string.Join(", ", cols.Select((_, i) => "@" + i))}); " +
                    "SELECT CAST(SCOPE_IDENTITY() AS int);",
                    cols.Select(c => ToDb(values[c])).ToArray());
                scope.Complete();
            }

            // Samma loggrad som nodlagringen: fälten som fick ett värde, plus de som är sanna när värdet saknas.
            var written = _fields
                .Where(f => f.Shape == CompetitionFieldShape.BoolDefaultTrue || !Equals(f.Get(blank), f.Get(after)))
                .ToList();
            var log = written.ToDictionary(f => f.Alias, f => ((object?)null, f.Get(after)));
            log["@name"] = (null, finalName);
            log["@parentId"] = (null, columns.TryGetValue("SeriesId", out var s) ? s : null);
            AfterWrite(id, "Create", log);
            return new CompetitionUpdateResult<T>
            {
                Outcome = CompetitionUpdateOutcome.Saved,
                Record = Get(id),
                ChangedFields = written.Select(f => f.Alias).ToList(),
            };
        }

        /// <summary>Byter serien (sist i den nya). Ingenting om raden redan ligger där (<paramref name="isThere"/>).</summary>
        public CompetitionUpdateResult<T> Move(int id, Dictionary<string, object?> columns, (string Column, int Value)? sortGroup,
            Func<T, bool> isThere, Func<T, int> parentOf, int newParent)
        {
            var current = Get(id);
            if (current is null) return NotFound();
            if (isThere(current)) return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = current };
            using (var scope = _scopeProvider.CreateScope())
            {
                var sets = new Dictionary<string, object?>(columns)
                {
                    ["SortOrder"] = sortGroup.HasValue ? NextSortOrder(scope.Database, sortGroup.Value) : 0,
                    ["UpdateDate"] = DateTime.Now,
                    ["UpdatedBy"] = _recorder.CurrentMemberId(),
                };
                Set(scope.Database, id, sets);
                scope.Complete();
            }
            AfterWrite(id, "Move", new Dictionary<string, (object?, object?)> { ["@parentId"] = (parentOf(current), newParent) });
            return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = Get(id) };
        }

        /// <summary>Mjuk radering. Raden ligger kvar med <c>DeletedUtc</c>; ingenting annat ändras.</summary>
        public CompetitionUpdateResult<T> Trash(int id, Func<T, int> parentOf)
        {
            var before = Get(id);
            if (before is null) return NotFound();
            using (var scope = _scopeProvider.CreateScope())
            {
                scope.Database.Execute($"UPDATE {_table} SET DeletedUtc = SYSUTCDATETIME(), DeletedBy = @0 WHERE Id = @1 AND {NotDeleted}",
                    ToDb(_recorder.CurrentMemberId()), id);
                scope.Complete();
            }
            _recorder.Record(_kind, id, "Trash", new Dictionary<string, (object?, object?)> { ["@trashed"] = (false, true), ["@parentId"] = (parentOf(before), null) });
            return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = before };
        }

        /// <summary>Loggraden (och versionsraden), sedan adressen. Transaktionen är stängd — inget av det får fälla skrivningen.</summary>
        private void AfterWrite(int id, string action, IReadOnlyDictionary<string, (object? From, object? To)> log)
        {
            _recorder.Record(_kind, id, action, log);
            try { _syncAddress(id); }
            catch (Exception ex) { _logger.LogWarning(ex, "Adressen för {Kind} {Id} kunde inte sättas efter {Action}", _kind, id, action); }
        }

        private void Set(IDatabase db, int id, Dictionary<string, object?> sets)
        {
            var cols = sets.Keys.ToList();
            var args = cols.Select(c => ToDb(sets[c])).ToList();
            args.Add(id);
            db.Execute($"UPDATE {_table} SET {string.Join(", ", cols.Select((c, i) => $"[{c}] = @{i}"))} WHERE Id = @{cols.Count}", args.ToArray());
        }

        private int NextSortOrder(IDatabase db, (string Column, int Value) group) =>
            db.ExecuteScalar<int>($"SELECT ISNULL(MAX(SortOrder) + 1, 0) FROM {_table} WHERE [{group.Column}] = @0 AND {NotDeleted}", group.Value);

        /// <summary>Adressdelen ur namnet med Umbracos regel (samma teckenbyten som nodernas, appsettings RequestHandler).</summary>
        private string Segment(string name) => name.ToUrlSegment(_shortStrings) ?? "";

        private T Blank()
        {
            var r = new T();
            foreach (var f in _fields) f.Set(r, CompetitionFieldValues.Read(f.Shape, null));
            return r;
        }

        private static object? Value(T record, string property) => typeof(T).GetProperty(property)?.GetValue(record);

        private static CompetitionUpdateResult<T> NotFound() => new() { Outcome = CompetitionUpdateOutcome.NotFound };

        private T ToRecord(Dictionary<string, object?> row)
        {
            var r = new T();
            foreach (var f in _fields) f.Set(r, CompetitionFieldValues.Read(f.Shape, Value(row, f.Property)));
            _fill(r, row, new SqlMeta(
                (int)row["Id"]!,
                (Guid)row["Key"]!,
                (string?)Value(row, "Name") ?? "",
                RowVersion(row),
                (DateTime)row["CreateDate"]!,
                (DateTime)row["UpdateDate"]!,
                CompetitionFieldValues.ReadInt(Value(row, "SortOrder")) ?? 0,
                CompetitionFieldValues.ReadInt(Value(row, "LegacyParentId")) ?? -1,
                (string?)Value(row, "LegacyParentKind") ?? "",
                (string?)Value(row, "UrlSegment") ?? ""));
            return r;
        }

        internal static object? Value(IDictionary<string, object?> row, string column) =>
            row.TryGetValue(column, out var v) && v is not DBNull ? v : null;

        private static long RowVersion(IDictionary<string, object?> row) =>
            Value(row, "RowVersion") is byte[] b && b.Length == 8 ? (long)BinaryPrimitives.ReadUInt64BigEndian(b) : 0;

        internal static object? ToDb(object? v) => v switch
        {
            null => DBNull.Value,
            bool b => b,
            _ => v,
        };
    }
}
