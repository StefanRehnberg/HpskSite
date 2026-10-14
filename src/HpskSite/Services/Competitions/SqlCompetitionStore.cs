using System.Buffers.Binary;
using HpskSite.Models.Competitions;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Competitions
{
    /// <summary>
    /// Tävlingarna i tabellen <c>dbo.Competition</c> (C3-planen S1). <b>I S1 bara mot en databaskopia</b>,
    /// för differentialtestet nod mot SQL; tabellens slutliga form kommer i planens §5.
    ///
    /// <para>Kolumnerna är <see cref="CompetitionFieldTable"/>s egenskaper — en kolumn per fält, samma namn.
    /// Inget publiceringsläge (§9a): <see cref="Get"/> och <see cref="GetSaved"/> svarar samma rad.</para>
    ///
    /// <para><see cref="Update"/> läser raden med <c>UPDLOCK, HOLDLOCK</c> i samma transaktion som den skriver
    /// och jämför <c>RowVersion</c> med versionen användaren såg. Använder <see cref="IScopeProvider"/>,
    /// aldrig en egen anslutning (lagringen anropas inifrån andras scope).</para>
    /// </summary>
    public class SqlCompetitionStore : ICompetitionStore
    {
        public const string Table = "dbo.Competition";
        private readonly SqlStoreCore<CompetitionInfo> _core;

        public SqlCompetitionStore(IScopeProvider scopeProvider)
        {
            _core = new SqlStoreCore<CompetitionInfo>(Table, CompetitionFieldTable.Fields, scopeProvider,
                new[] { "SeriesId" }, Fill, Meta);
        }

        public CompetitionInfo? Get(int id) => _core.Get(id);
        public CompetitionInfo? GetSaved(int id) => _core.Get(id);
        public IReadOnlyDictionary<int, CompetitionInfo> GetMany(IReadOnlyCollection<int> ids) => _core.GetMany(ids);
        public IReadOnlyList<CompetitionInfo> List() => _core.List(null);
        public IReadOnlyList<CompetitionInfo> ListBySeries(int seriesId) =>
            seriesId <= 0 ? Array.Empty<CompetitionInfo>() : _core.List(("SeriesId", seriesId));

        public CompetitionUpdateResult<CompetitionInfo> Update(int id, long? expectedVersion, Action<CompetitionInfo> change) =>
            _core.Update(id, expectedVersion, change, (before, after) => before.SeriesId != after.SeriesId
                ? new Dictionary<string, object?> { ["SeriesId"] = after.SeriesId }
                : new Dictionary<string, object?>());

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

        public SqlCompetitionSeriesStore(IScopeProvider scopeProvider)
        {
            _core = new SqlStoreCore<CompetitionSeriesInfo>(Table, CompetitionSeriesFieldTable.Fields, scopeProvider,
                Array.Empty<string>(), Fill, Meta);
        }

        public CompetitionSeriesInfo? Get(int id) => _core.Get(id);
        public CompetitionSeriesInfo? GetSaved(int id) => _core.Get(id);
        public IReadOnlyDictionary<int, CompetitionSeriesInfo> GetMany(IReadOnlyCollection<int> ids) => _core.GetMany(ids);
        public IReadOnlyList<CompetitionSeriesInfo> List() => _core.List(null);

        public CompetitionUpdateResult<CompetitionSeriesInfo> Update(int id, long? expectedVersion, Action<CompetitionSeriesInfo> change) =>
            _core.Update(id, expectedVersion, change, (_, _) => new Dictionary<string, object?>());

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
        private readonly string _table;
        private readonly IReadOnlyList<CompetitionFieldDef<T>> _fields;
        private readonly IScopeProvider _scopeProvider;
        private readonly string[] _extraColumns;
        private readonly Action<T, IDictionary<string, object?>, SqlMeta> _fill;
        private readonly Func<T, SqlMeta> _meta;

        public SqlStoreCore(string table, IReadOnlyList<CompetitionFieldDef<T>> fields, IScopeProvider scopeProvider,
            string[] extraColumns, Action<T, IDictionary<string, object?>, SqlMeta> fill, Func<T, SqlMeta> meta)
        {
            _table = table;
            _fields = fields;
            _scopeProvider = scopeProvider;
            _extraColumns = extraColumns;
            _fill = fill;
            _meta = meta;
        }

        public T? Get(int id)
        {
            if (id <= 0) return null;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var row = scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE Id = @0", id).FirstOrDefault();
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
                foreach (var row in scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE Id IN (@0)", chunk))
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
                ? scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE [{filter.Value.Column}] = @0 ORDER BY SortOrder, Id", filter.Value.Value)
                : scope.Database.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} ORDER BY Id");
            return rows.Select(ToRecord).ToList();
        }

        public CompetitionUpdateResult<T> Update(int id, long? expectedVersion, Action<T> change,
            Func<T, T, Dictionary<string, object?>> extraChanges)
        {
            if (id <= 0) return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.NotFound };
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var row = db.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WITH (UPDLOCK, HOLDLOCK) WHERE Id = @0", id).FirstOrDefault();
            if (row is null)
            {
                scope.Complete();
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.NotFound };
            }

            var before = ToRecord(row);
            if (expectedVersion.HasValue && expectedVersion.Value != RowVersion(row))
            {
                scope.Complete();
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Conflict, Record = before };
            }

            var after = ToRecord(row);
            change(after);
            var changed = _fields.Where(f => !Equals(f.Get(before), f.Get(after))).ToList();
            var extra = extraChanges(before, after);
            if (changed.Count == 0 && extra.Count == 0)
            {
                scope.Complete();
                return new CompetitionUpdateResult<T> { Outcome = CompetitionUpdateOutcome.Saved, Record = before };
            }

            var sets = new List<string>();
            var args = new List<object?>();
            foreach (var f in changed) { sets.Add($"[{f.Property}] = @{args.Count}"); args.Add(ToDb(f.Get(after))); }
            foreach (var kv in extra) { sets.Add($"[{kv.Key}] = @{args.Count}"); args.Add(ToDb(kv.Value)); }
            var name = _meta(after).Name;
            if (name != _meta(before).Name) { sets.Add($"[Name] = @{args.Count}"); args.Add(name); }
            sets.Add($"[UpdateDate] = @{args.Count}"); args.Add(DateTime.Now);
            args.Add(id);
            db.Execute($"UPDATE {_table} SET {string.Join(", ", sets)} WHERE Id = @{args.Count - 1}", args.ToArray());

            var saved = db.Fetch<Dictionary<string, object?>>($"SELECT * FROM {_table} WHERE Id = @0", id).First();
            scope.Complete();
            return new CompetitionUpdateResult<T>
            {
                Outcome = CompetitionUpdateOutcome.Saved,
                Record = ToRecord(saved),
                ChangedFields = changed.Select(f => f.Alias).Concat(extra.Keys).ToList(),
            };
        }

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
