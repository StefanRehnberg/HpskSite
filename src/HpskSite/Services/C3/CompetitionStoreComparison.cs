using System.Globalization;
using HpskSite.Models.Competitions;
using HpskSite.Services.Competitions;
using Microsoft.Extensions.Hosting;
using NPoco;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.C3
{
    /// <summary>
    /// C3-planen S1: bevisar att posten och lagringarna svarar som de gamla läsningarna.
    ///
    /// <list type="number">
    ///   <item><see cref="CompareWithLegacy"/> — <see cref="CompetitionInfo"/> ur nodlagringen mot de 91
    ///   gamla läsformerna (<see cref="LegacyCompetitionReadSnapshot"/>), fält för fält. Avvikelserna ska vara
    ///   en godkänd mängd.</item>
    ///   <item><see cref="CopyNodesToTable"/> + <see cref="CompareNodeVsSql"/> — differentialtestet per
    ///   fråga: nodlagringen mot tabellagringen. <b>Bara mot databaskopian <c>Umbraco_C3</c></b>, i
    ///   Development.</item>
    /// </list>
    /// </summary>
    public sealed class CompetitionStoreComparison
    {
        public const string CopyDatabase = "Umbraco_C3";

        private readonly LegacyCompetitionReadSnapshot _legacy;
        private readonly NodeCompetitionStore _nodeCompetitions;
        private readonly NodeCompetitionSeriesStore _nodeSeries;
        private readonly SqlCompetitionStore _sqlCompetitions;
        private readonly SqlCompetitionSeriesStore _sqlSeries;
        private readonly IScopeProvider _scopeProvider;
        private readonly IHostEnvironment _env;

        public CompetitionStoreComparison(LegacyCompetitionReadSnapshot legacy, NodeCompetitionStore nodeCompetitions,
            NodeCompetitionSeriesStore nodeSeries, SqlCompetitionStore sqlCompetitions, SqlCompetitionSeriesStore sqlSeries,
            IScopeProvider scopeProvider, IHostEnvironment env)
        {
            _legacy = legacy;
            _nodeCompetitions = nodeCompetitions;
            _nodeSeries = nodeSeries;
            _sqlCompetitions = sqlCompetitions;
            _sqlSeries = sqlSeries;
            _scopeProvider = scopeProvider;
            _env = env;
        }

        // ── 1. Posten mot de gamla läsningarna ─────────────────────────────────────────────

        public sealed record Deviation(int Id, string Alias, string Form, object? Legacy, object? Record);

        /// <summary>Varje fält och varje läsform där posten svarar något annat än den gamla läsningen.</summary>
        public List<Deviation> CompareWithLegacy()
        {
            var deviations = new List<Deviation>();
            foreach (var node in _legacy.Build())
            {
                var id = (int)node["id"]!;
                var alias = (string)node["alias"]!;
                var props = (IDictionary<string, object?>)node["props"]!;
                bool published = (bool)node["inPublishedCache"]!;

                if (alias == CompetitionFieldTable.NodeAlias)
                {
                    var pub = published ? _nodeCompetitions.Get(id) : null;
                    var saved = _nodeCompetitions.GetSaved(id);
                    if (published && pub is null) deviations.Add(new(id, "*", "p", "finns", null));
                    if (saved is null) { deviations.Add(new(id, "*", "c", "finns", null)); continue; }
                    CompareFields(id, props, CompetitionFieldTable.Fields, pub, saved, deviations);
                    CompareMeta(id, node, pub ?? saved, deviations);
                    if (pub is not null && node.TryGetValue("derived", out var d) && d is IDictionary<string, object?> derived)
                        CompareDerived(id, derived, pub, deviations);
                }
                else if (alias == CompetitionSeriesFieldTable.NodeAlias)
                {
                    var pub = published ? _nodeSeries.Get(id) : null;
                    var saved = _nodeSeries.GetSaved(id);
                    if (published && pub is null) deviations.Add(new(id, "*", "p", "finns", null));
                    if (saved is null) { deviations.Add(new(id, "*", "c", "finns", null)); continue; }
                    CompareFields(id, props, CompetitionSeriesFieldTable.Fields, pub, saved, deviations);
                    CompareSeriesMeta(id, node, pub ?? saved, deviations);
                }
            }
            return deviations;
        }

        private static void CompareFields<T>(int id, IDictionary<string, object?> props, IReadOnlyList<CompetitionFieldDef<T>> fields,
            T? pub, T saved, List<Deviation> deviations) where T : class
        {
            foreach (var f in fields)
            {
                if (!props.TryGetValue(f.Alias, out var po) || po is not IDictionary<string, object?> p) continue;
                var reads = p.TryGetValue("reads", out var r) && r is IDictionary<string, object?> rd ? rd : new Dictionary<string, object?>();
                foreach (var (form, legacy) in reads)
                {
                    var isPublishedForm = form.StartsWith("p:", StringComparison.Ordinal);
                    var record = isPublishedForm ? (pub is null ? null : f.Get(pub)) : f.Get(saved);
                    if (isPublishedForm && pub is null) continue;
                    var type = form[2..];
                    if (!SameAs(f.Shape, type, legacy, record))
                        deviations.Add(new(id, f.Alias, form, legacy, Fmt(record)));
                }
            }
        }

        /// <summary>
        /// Om postens värde är det den gamla läsningen gav, med de normaliseringar posten gör med flit:
        /// tomt = null, <c>MinValue</c> = null, en kastande läsning är alltid en avvikelse.
        /// </summary>
        internal static bool SameAs(CompetitionFieldShape shape, string type, object? legacy, object? record)
        {
            if (legacy is string ls && ls.StartsWith("!", StringComparison.Ordinal)) return false;
            switch (type)
            {
                case "untyped":
                case "object":
                    return true; // otypat: formen varierar med konverteraren, jämförs inte
                case "IPublishedContent":
                    return (legacy is null) == (record is null);
                case "bool":
                case "bool?":
                    // bool? ger null för ett saknat värde; postens tolkning av null står i fältets form.
                    if (legacy is null) return true;
                    return Equals(legacy as bool?, record as bool?);
                case "int":
                case "int?":
                    return Convert.ToInt64(legacy ?? 0, CultureInfo.InvariantCulture) == Convert.ToInt64(record ?? 0, CultureInfo.InvariantCulture);
                case "DateTime":
                case "DateTime?":
                    var l = legacy as string;
                    if (l is null || l.StartsWith("0001-01-01", StringComparison.Ordinal)) l = null;
                    return Equals(l, Fmt(record));
                case "decimal":
                case "decimal?":
                    return true; // beloppen jämförs via Fee() i S3; posten bär texten
                case "string":
                    var a = string.IsNullOrEmpty(legacy as string) ? null : (string)legacy!;
                    var b = record switch { null => null, string s => s, _ => Convert.ToString(Fmt(record), CultureInfo.InvariantCulture) };
                    if (shape is CompetitionFieldShape.Bool or CompetitionFieldShape.BoolDefaultTrue or CompetitionFieldShape.Int) return true;
                    return Equals(a, string.IsNullOrEmpty(b) ? null : b);
                default:
                    return true;
            }
        }

        private static void CompareMeta(int id, IDictionary<string, object?> node, CompetitionInfo c, List<Deviation> deviations)
        {
            CheckMeta(id, "name", node["name"], c.Name, deviations);
            CheckMeta(id, "key", node["key"]?.ToString(), c.Key.ToString(), deviations);
            CheckMeta(id, "sortOrder", node["sortOrder"], c.SortOrder, deviations);
            CheckMeta(id, "parentId", node["parentId"], c.ParentId, deviations);
            CheckMeta(id, "parentAlias", node["parentAlias"] ?? "", c.ParentKind, deviations);
            CheckMeta(id, "published", node["published"], c.IsPublished, deviations);
            if ((bool)node["inPublishedCache"]!) CheckMeta(id, "urlSegment", node["urlSegment"] ?? "", c.UrlSegment, deviations);
        }

        private static void CompareSeriesMeta(int id, IDictionary<string, object?> node, CompetitionSeriesInfo s, List<Deviation> deviations)
        {
            CheckMeta(id, "name", node["name"], s.Name, deviations);
            CheckMeta(id, "key", node["key"]?.ToString(), s.Key.ToString(), deviations);
            CheckMeta(id, "sortOrder", node["sortOrder"], s.SortOrder, deviations);
            CheckMeta(id, "parentId", node["parentId"], s.ParentId, deviations);
            CheckMeta(id, "parentAlias", node["parentAlias"] ?? "", s.ParentKind, deviations);
        }

        private static void CompareDerived(int id, IDictionary<string, object?> derived, CompetitionInfo c, List<Deviation> deviations)
        {
            if (derived.TryGetValue("scope", out var scope))
                CheckMeta(id, "derived.scope", string.IsNullOrEmpty(scope as string) ? null : scope, c.CompetitionScope, deviations);
            if (derived.TryGetValue("competitionTypeId", out var typeId)) CheckMeta(id, "derived.competitionTypeId", typeId, c.TypeId, deviations);
            if (derived.TryGetValue("classIds", out var classes))
                CheckMeta(id, "derived.classIds", Fmt(classes), Fmt(c.ClassIds), deviations);
            if (derived.TryGetValue("seriesId", out var seriesId)) CheckMeta(id, "derived.seriesId", seriesId, c.SeriesId, deviations);
            if (derived.TryGetValue("level", out var level))
                CheckMeta(id, "derived.level", string.IsNullOrEmpty(level as string) ? null : level, HpskSite.CompetitionTypes.Common.CompetitionLevel.Normalize(c.CompetitionLevel) is var n && string.IsNullOrEmpty(n) ? null : n, deviations);
        }

        private static void CheckMeta(int id, string what, object? legacy, object? record, List<Deviation> deviations)
        {
            if (!Equals(Fmt(legacy), Fmt(record))) deviations.Add(new(id, what, "meta", Fmt(legacy), Fmt(record)));
        }

        private static object? Fmt(object? v) => v switch
        {
            null => null,
            int i => (long)i,
            long l => l,
            _ => LegacyCompetitionReadSnapshot.Fmt(v),
        };

        // ── 2. Nod mot SQL, per fråga ──────────────────────────────────────────────────────

        public void EnsureCopyDatabase()
        {
            if (!_env.IsDevelopment()) throw new InvalidOperationException("Bara i Development.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var name = scope.Database.ExecuteScalar<string>("SELECT DB_NAME()");
            if (!string.Equals(name, CopyDatabase, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Bara mot databaskopian {CopyDatabase} (den här är {name}).");
        }

        /// <summary>
        /// Tömmer tabellerna och kopierar varje PUBLICERAD tävling och serie dit, med nodens id. Bara på kopian.
        /// Returnerar antalen.
        /// </summary>
        public (int Competitions, int Series) CopyNodesToTable()
        {
            EnsureCopyDatabase();
            var competitions = _nodeCompetitions.List();
            var series = _nodeSeries.List();
            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            db.Execute($"DELETE FROM {SqlCompetitionStore.Table}; DELETE FROM {SqlCompetitionSeriesStore.Table};");
            Insert(db, SqlCompetitionStore.Table, competitions, CompetitionFieldTable.Fields,
                c => Meta(c.Id, c.Key, c.Name, c.CreateDate, c.UpdateDate, c.SortOrder, c.ParentId, c.ParentKind, c.UrlSegment,
                    ("SeriesId", c.SeriesId)));
            Insert(db, SqlCompetitionSeriesStore.Table, series, CompetitionSeriesFieldTable.Fields,
                s => Meta(s.Id, s.Key, s.Name, s.CreateDate, s.UpdateDate, s.SortOrder, s.ParentId, s.ParentKind, s.UrlSegment));
            scope.Complete();
            return (competitions.Count, series.Count);
        }

        private static Dictionary<string, object?> Meta(int id, Guid key, string name, DateTime created, DateTime updated, int sort,
            int parentId, string parentKind, string urlSegment, params (string, object?)[] extra)
        {
            var d = new Dictionary<string, object?>
            {
                ["Id"] = id, ["Key"] = key, ["Name"] = name, ["CreateDate"] = created, ["UpdateDate"] = updated, ["SortOrder"] = sort,
                ["LegacyParentId"] = parentId, ["LegacyParentKind"] = parentKind, ["UrlSegment"] = urlSegment,
            };
            foreach (var (k, v) in extra) d[k] = v;
            return d;
        }

        private static void Insert<T>(IDatabase db, string table, IEnumerable<T> records, IReadOnlyList<CompetitionFieldDef<T>> fields,
            Func<T, Dictionary<string, object?>> meta)
        {
            db.Execute($"SET IDENTITY_INSERT {table} ON");
            try
            {
                foreach (var r in records)
                {
                    var values = meta(r);
                    foreach (var f in fields) values[f.Property] = f.Get(r);
                    var cols = values.Keys.ToList();
                    var sql = $"INSERT INTO {table} ({string.Join(", ", cols.Select(c => "[" + c + "]"))}) VALUES ({string.Join(", ", cols.Select((_, i) => "@" + i))})";
                    db.Execute(sql, cols.Select(c => values[c] ?? DBNull.Value).ToArray());
                }
            }
            finally
            {
                db.Execute($"SET IDENTITY_INSERT {table} OFF");
            }
        }

        public sealed record Check(string Name, bool Ok, string Detail);

        /// <summary>
        /// Prövar <see cref="ICompetitionStore.Update"/> mot BÅDA lagringarna på en tävling: konflikten skriver
        /// ingenting, en ändring skriver bara sitt fält och byter version, ingen ändring skriver ingenting,
        /// okänt id svarar NotFound, och frysningen stoppar växelns skrivning. Återställer allt. Bara på kopian.
        /// </summary>
        public List<Check> UpdateProbe(int id, SwitchingCompetitionStore switching, HpskSite.Services.SiteSettings.SiteSettingService settings)
        {
            EnsureCopyDatabase();
            var checks = new List<Check>();
            foreach (var (label, store) in new (string, ICompetitionStore)[] { ("node", _nodeCompetitions), ("sql", _sqlCompetitions) })
            {
                var start = store.GetSaved(id);
                if (start is null) { checks.Add(new($"{label}: tävlingen finns", false, id.ToString())); continue; }
                var original = start.Venue;

                var conflict = store.Update(id, start.Version + 1, c => c.Venue = "KONFLIKT");
                checks.Add(new($"{label}: fel version ger konflikt", conflict.Outcome == CompetitionUpdateOutcome.Conflict, conflict.Outcome.ToString()));
                checks.Add(new($"{label}: konflikten skrev ingenting", store.GetSaved(id)!.Venue == original, store.GetSaved(id)!.Venue ?? "null"));

                var saved = store.Update(id, start.Version, c => c.Venue = (original ?? "") + " (S1)");
                checks.Add(new($"{label}: rätt version sparar", saved.Ok, saved.Outcome.ToString()));
                checks.Add(new($"{label}: bara venue ändrades", saved.ChangedFields.SequenceEqual(new[] { "venue" }), string.Join(",", saved.ChangedFields)));
                var after = store.GetSaved(id)!;
                checks.Add(new($"{label}: värdet läses tillbaka", after.Venue == (original ?? "") + " (S1)", after.Venue ?? "null"));
                checks.Add(new($"{label}: versionen byttes", after.Version != start.Version, $"{start.Version} → {after.Version}"));
                var published = store.Get(id);
                checks.Add(new($"{label}: det publicerade läget följer", published?.Venue == after.Venue, published?.Venue ?? "null"));
                checks.Add(new($"{label}: övriga fält orörda",
                    CompetitionFieldTable.Fields.Where(f => f.Alias != "venue").All(f => Equals(f.Get(start), f.Get(after))), ""));

                var none = store.Update(id, null, _ => { });
                checks.Add(new($"{label}: ingen ändring skriver ingenting", none.Ok && none.ChangedFields.Count == 0, string.Join(",", none.ChangedFields)));

                var missing = store.Update(999999999, null, c => c.Venue = "x");
                checks.Add(new($"{label}: okänt id svarar NotFound", missing.Outcome == CompetitionUpdateOutcome.NotFound, missing.Outcome.ToString()));

                store.Update(id, null, c => c.Venue = original);
                checks.Add(new($"{label}: återställd", store.GetSaved(id)!.Venue == original, store.GetSaved(id)!.Venue ?? "null"));
            }

            settings.Set(CompetitionStoreSettings.WriteFreezeKey, "1", null);
            try
            {
                var threw = false;
                try { switching.Update(id, null, c => c.Venue = "FRYST"); }
                catch (CompetitionWriteFrozenException) { threw = true; }
                checks.Add(new("växeln: frysningen stoppar skrivningen", threw, ""));
                checks.Add(new("växeln: frysningen skrev ingenting", _nodeCompetitions.GetSaved(id)!.Venue != "FRYST", ""));
                checks.Add(new("växeln: läsning fungerar fryst", switching.Get(id) is not null, ""));
            }
            finally
            {
                settings.Set(CompetitionStoreSettings.WriteFreezeKey, "0", null);
            }
            return checks;
        }

        public sealed record QueryDiff(string Query, string Detail);

        /// <summary>
        /// Samma frågor mot båda lagringarna. Tom lista = lika. Jämför allt utom versionen och
        /// publiceringsläget (bara noden har ett, §9a). <see cref="ICompetitionStore.GetSaved"/> jämförs bara för
        /// tävlingar vars utkast är lika med det publicerade — de andra listas av torrkörningen.
        /// </summary>
        public List<QueryDiff> CompareNodeVsSql()
        {
            EnsureCopyDatabase();
            var diffs = new List<QueryDiff>();

            var nodeList = _nodeCompetitions.List();
            var sqlList = _sqlCompetitions.List();
            CompareLists("Competition.List", nodeList, sqlList, Describe, diffs);

            var ids = nodeList.Select(c => c.Id).Concat(new[] { -1, 0, 999999999 }).ToList();
            foreach (var id in ids)
            {
                Same($"Competition.Get({id})", Describe(_nodeCompetitions.Get(id)), Describe(_sqlCompetitions.Get(id)), diffs);
                var savedNode = _nodeCompetitions.GetSaved(id);
                var pubNode = _nodeCompetitions.Get(id);
                if (savedNode is not null && pubNode is not null && Describe(savedNode) == Describe(pubNode))
                    Same($"Competition.GetSaved({id})", Describe(savedNode), Describe(_sqlCompetitions.GetSaved(id)), diffs);
            }
            var many = _nodeCompetitions.GetMany(ids);
            var manySql = _sqlCompetitions.GetMany(ids);
            Same("Competition.GetMany(alla)", string.Join("\n", many.OrderBy(k => k.Key).Select(k => Describe(k.Value))),
                string.Join("\n", manySql.OrderBy(k => k.Key).Select(k => Describe(k.Value))), diffs);

            var seriesNode = _nodeSeries.List();
            CompareLists("Series.List", seriesNode, _sqlSeries.List(), DescribeSeries, diffs);
            foreach (var s in seriesNode)
            {
                Same($"Series.Get({s.Id})", DescribeSeries(_nodeSeries.Get(s.Id)), DescribeSeries(_sqlSeries.Get(s.Id)), diffs);
                CompareLists($"Competition.ListBySeries({s.Id})", _nodeCompetitions.ListBySeries(s.Id), _sqlCompetitions.ListBySeries(s.Id), Describe, diffs);
            }
            Same("Competition.ListBySeries(0)", Describe(_nodeCompetitions.ListBySeries(0).FirstOrDefault()), Describe(_sqlCompetitions.ListBySeries(0).FirstOrDefault()), diffs);
            return diffs;
        }

        private static void CompareLists<T>(string query, IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T?, string> describe, List<QueryDiff> diffs) where T : class
        {
            if (a.Count != b.Count) { diffs.Add(new(query, $"antal {a.Count} mot {b.Count}")); return; }
            for (int i = 0; i < a.Count; i++)
            {
                var x = describe(a[i]); var y = describe(b[i]);
                if (x != y) { diffs.Add(new(query, $"plats {i}: {FirstDifference(x, y)}")); return; }
            }
        }

        private static void Same(string query, string a, string b, List<QueryDiff> diffs)
        {
            if (a != b) diffs.Add(new(query, FirstDifference(a, b)));
        }

        private static string FirstDifference(string a, string b)
        {
            var la = a.Split('\n'); var lb = b.Split('\n');
            for (int i = 0; i < Math.Max(la.Length, lb.Length); i++)
            {
                var x = i < la.Length ? la[i] : "<saknas>"; var y = i < lb.Length ? lb[i] : "<saknas>";
                if (x != y) return $"nod «{x}» mot sql «{y}»";
            }
            return "";
        }

        /// <summary>En jämförbar text: allt utom versionen och publiceringsläget, en rad per fält.</summary>
        internal static string Describe(CompetitionInfo? c)
        {
            if (c is null) return "null";
            var lines = new List<string>
            {
                $"Id={c.Id}", $"Key={c.Key}", $"Name={c.Name}", $"CreateDate={Fmt(c.CreateDate)}", $"UpdateDate={Fmt(c.UpdateDate)}",
                $"SortOrder={c.SortOrder}", $"ParentId={c.ParentId}", $"ParentKind={c.ParentKind}", $"SeriesId={c.SeriesId}",
                $"UrlSegment={c.UrlSegment}",
            };
            lines.AddRange(CompetitionFieldTable.Fields.Select(f => $"{f.Property}={Fmt(f.Get(c))}"));
            return string.Join("\n", lines);
        }

        internal static string DescribeSeries(CompetitionSeriesInfo? s)
        {
            if (s is null) return "null";
            var lines = new List<string>
            {
                $"Id={s.Id}", $"Key={s.Key}", $"Name={s.Name}", $"CreateDate={Fmt(s.CreateDate)}", $"UpdateDate={Fmt(s.UpdateDate)}",
                $"SortOrder={s.SortOrder}", $"ParentId={s.ParentId}", $"ParentKind={s.ParentKind}", $"UrlSegment={s.UrlSegment}",
            };
            lines.AddRange(CompetitionSeriesFieldTable.Fields.Select(f => $"{f.Property}={Fmt(f.Get(s))}"));
            return string.Join("\n", lines);
        }
    }
}
