using System.Globalization;
using System.Text;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// The ONE place a functionary role is resolved. Merges the built-in <see cref="FunctionaryRoles"/>
    /// catalog with arrangör-named <see cref="StaffRole"/> rows.
    ///
    /// <para><b>Why it exists:</b> the built-in catalog was a closed set and <c>SaveAssignment</c> rejected
    /// everything outside it. Clubs name the same job differently — and sometimes use our word for a
    /// different job, which makes the stored data actively wrong rather than merely awkward. Roles are now
    /// rows you name yourself; the built-ins are suggestions.</para>
    ///
    /// <para><b>Merge rule:</b> built-ins first, then custom rows. A custom row whose key matches a built-in
    /// <i>overrides</i> its display name (so "Startledare" can be called "Starter"); a new key appends.
    /// Ordering is built-in order, then <c>SortOrder</c>, then name.</para>
    ///
    /// <para><b>Never throws.</b> Every read is wrapped: if <c>StaffRole</c> hasn't been created yet the
    /// catalog degrades to the built-ins, exactly as before. And every caller already falls back to the raw
    /// role key when a role can't be resolved, so an unknown key renders as itself instead of blowing up.</para>
    ///
    /// Scoped → the per-competition memo below is a request-lifetime cache.
    /// </summary>
    public class RoleCatalogService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<RoleCatalogService> _logger;

        private readonly Dictionary<string, List<FunctionaryRole>> _memo = new();
        private readonly Dictionary<int, List<StaffRole>> _customMemo = new();

        public RoleCatalogService(IScopeProvider scopeProvider, ILogger<RoleCatalogService> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        // ---------------------------------------------------------------- reads

        /// <summary>Built-ins + this competition's own roles, merged and ordered.</summary>
        public IReadOnlyList<FunctionaryRole> ForCompetition(int competitionId, string? discipline)
        {
            var memoKey = $"{competitionId}|{discipline}";
            if (_memo.TryGetValue(memoKey, out var cached)) return cached;

            var builtIns = FunctionaryRoles.ForDiscipline(discipline);
            var custom = GetCustom(competitionId);

            var merged = new List<FunctionaryRole>();
            var byKey = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            var builtInPos = 0;
            foreach (var b in builtIns)
            {
                // A built-in the arrangör has never placed trails behind everything they HAVE placed,
                // in catalog order. As soon as they drag it, a StaffRole row is written and it moves.
                byKey[b.Key] = merged.Count;
                merged.Add(new FunctionaryRole
                {
                    Key = b.Key,
                    DisplayName = b.DisplayName,
                    PluralName = b.PluralName,
                    DefaultScopeType = b.DefaultScopeType,
                    SupportsTargetRange = b.SupportsTargetRange,
                    SupportsFunctionTitle = b.SupportsFunctionTitle,
                    IsResponsibleByDefault = b.IsResponsibleByDefault,
                    Description = b.Description,
                    Needs = b.Needs,
                    SortOrder = FunctionaryRoles.BuiltInSortBase + builtInPos++,
                });
            }

            // IsActive=0 is a HIDE marker, not a soft delete: an arrangör who uses 5 of the 11 built-ins
            // must be able to get the other 6 out of their grid. Hiding a built-in removes it from the
            // catalog; unhiding is deleting the marker row.
            var hidden = new HashSet<string>(
                custom.Where(c => !c.IsActive).Select(c => c.RoleKey), StringComparer.OrdinalIgnoreCase);

            foreach (var c in custom.Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.DisplayName, StringComparer.CurrentCulture))
            {
                if (!MatchesDiscipline(c, discipline)) continue;
                var role = ToRole(c);
                if (byKey.TryGetValue(c.RoleKey, out var idx))
                    merged[idx] = role;      // override: same key, arrangör's own name
                else
                {
                    byKey[c.RoleKey] = merged.Count;
                    merged.Add(role);
                }
            }

            if (hidden.Count > 0)
                merged = merged.Where(r => !hidden.Contains(r.Key)).ToList();

            _memo[memoKey] = merged;
            return merged;
        }

        /// <summary>Resolve one role. Returns null when the key is unknown — callers fall back to the key.</summary>
        public FunctionaryRole? Resolve(int competitionId, string? discipline, string? roleKey)
        {
            if (string.IsNullOrWhiteSpace(roleKey)) return null;
            foreach (var r in ForCompetition(competitionId, discipline))
                if (string.Equals(r.Key, roleKey, StringComparison.OrdinalIgnoreCase)) return r;
            return null;
        }

        /// <summary>Display name for a role key, falling back to the key itself (never empty).</summary>
        public string NameFor(int competitionId, string? discipline, string? roleKey)
            => Resolve(competitionId, discipline, roleKey)?.DisplayName ?? roleKey ?? "";

        /// <summary>True when this key is arrangör-created (used by the UI to offer rename/delete).</summary>
        public bool IsCustom(int competitionId, string roleKey)
            => GetCustom(competitionId).Any(c => c.IsActive && string.Equals(c.RoleKey, roleKey, StringComparison.OrdinalIgnoreCase));

        public List<StaffRole> GetCustom(int competitionId)
        {
            if (_customMemo.TryGetValue(competitionId, out var cached)) return cached;
            List<StaffRole> rows;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                rows = scope.Database.Fetch<StaffRole>(
                    "SELECT * FROM StaffRole WHERE OwnerType = @0 AND OwnerId = @1 ORDER BY SortOrder, DisplayName",
                    RoleOwnerType.Competition, competitionId);
            }
            catch (Exception ex)
            {
                // Table not created yet → behave exactly like before the feature existed.
                _logger.LogWarning(ex, "RoleCatalog: custom role lookup failed for competition {CompetitionId}", competitionId);
                rows = new List<StaffRole>();
            }
            _customMemo[competitionId] = rows;
            return rows;
        }

        // ---------------------------------------------------------------- writes

        /// <summary>
        /// Create a role from a typed name, or update/override an existing key. Returns the role key.
        /// A blank <see cref="SaveStaffRoleRequest.RoleKey"/> means "new": the key is slugified from the
        /// name and de-duplicated against BOTH the built-ins and this competition's own rows.
        /// </summary>
        public string SaveRole(SaveStaffRoleRequest req, string? discipline, int byMemberId)
        {
            var name = (req.DisplayName ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("Rollen måste ha ett namn.");
            if (name.Length > 100) name = name[..100];

            var key = (req.RoleKey ?? "").Trim();
            if (key.Length == 0)
                key = UniqueKey(req.CompetitionId, discipline, name);

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var existing = scope.Database.SingleOrDefault<StaffRole>(
                "SELECT * FROM StaffRole WHERE OwnerType = @0 AND OwnerId = @1 AND RoleKey = @2",
                RoleOwnerType.Competition, req.CompetitionId, key);

            var now = DateTime.UtcNow;
            if (existing == null)
            {
                scope.Database.Insert(new StaffRole
                {
                    OwnerType = RoleOwnerType.Competition,
                    OwnerId = req.CompetitionId,
                    RoleKey = key,
                    DisplayName = name,
                    PluralName = Blank(req.PluralName),
                    DefaultScopeType = Blank(req.DefaultScopeType),
                    SupportsTargetRange = req.SupportsTargetRange,
                    SupportsFunctionTitle = req.SupportsFunctionTitle,
                    Description = Blank(req.Description),
                    SortOrder = req.SortOrder,
                    IsActive = true,
                    CreatedByMemberId = byMemberId,
                    CreatedDate = now,
                    ModifiedDate = now,
                });
            }
            else
            {
                existing.DisplayName = name;
                existing.PluralName = Blank(req.PluralName);
                existing.DefaultScopeType = Blank(req.DefaultScopeType);
                existing.SupportsTargetRange = req.SupportsTargetRange;
                existing.SupportsFunctionTitle = req.SupportsFunctionTitle;
                existing.Description = Blank(req.Description);
                existing.SortOrder = req.SortOrder;
                existing.IsActive = true;
                existing.ModifiedDate = now;
                scope.Database.Update(existing);
            }

            Forget(req.CompetitionId);
            return key;
        }

        /// <summary>
        /// Persist the arrangör's own running order for the whole grid. Alphabetical is tidy but splits
        /// the groups a plan is read in (Start/Mål together, Skjutplats together), so the order is theirs.
        /// <para>A built-in that gets dragged has no <see cref="StaffRole"/> row yet — one is created on
        /// the spot, carrying its current name, because that is the only place an order can be stored.
        /// Keys not in the list keep their existing order after the ones that are.</para>
        /// </summary>
        public (bool ok, string? message) Reorder(int competitionId, IList<string> roleKeys, string? discipline, int byMemberId)
        {
            if (roleKeys == null || roleKeys.Count == 0) return (false, "Ingen ordning angiven.");

            var known = ForCompetition(competitionId, discipline)
                .ToDictionary(r => r.Key, r => r, StringComparer.OrdinalIgnoreCase);

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var existing = db.Fetch<StaffRole>(
                    "SELECT * FROM StaffRole WHERE OwnerType = @0 AND OwnerId = @1",
                    RoleOwnerType.Competition, competitionId)
                .ToDictionary(r => r.RoleKey, r => r, StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var order = 0;
            foreach (var key in roleKeys)
            {
                if (string.IsNullOrWhiteSpace(key) || !known.TryGetValue(key, out var role)) continue;
                if (existing.TryGetValue(key, out var row))
                {
                    if (row.SortOrder == order) { order++; continue; }
                    row.SortOrder = order++;
                    row.ModifiedDate = now;
                    db.Update(row);
                }
                else
                {
                    db.Insert(new StaffRole
                    {
                        OwnerType = RoleOwnerType.Competition,
                        OwnerId = competitionId,
                        RoleKey = role.Key,
                        DisplayName = role.DisplayName,
                        PluralName = role.PluralName,
                        DefaultScopeType = role.DefaultScopeType,
                        SupportsTargetRange = role.SupportsTargetRange,
                        SupportsFunctionTitle = role.SupportsFunctionTitle,
                        Description = role.Description,
                        SortOrder = order++,
                        IsActive = true,
                        CreatedByMemberId = byMemberId,
                        CreatedDate = now,
                        ModifiedDate = now,
                    });
                }
            }

            Forget(competitionId);
            return (true, null);
        }

        /// <summary>
        /// Remove a role row from this competition's grid — any role, built-in or arrangör-created.
        /// The two used to behave differently (a built-in could only be "hidden"), which put a distinction
        /// that is purely about where we store the name onto the organiser's screen: Kassa had no delete
        /// button and Kassör did. Roles are freely named here, so they are freely removed.
        ///
        /// A built-in isn't a row we can delete, so it is persisted as an IsActive=0 marker instead —
        /// mechanism, not vocabulary. Typing the name again brings it back.
        ///
        /// Crew on the row is never dropped silently: with <paramref name="deleteAssignments"/> false this
        /// returns ok=false with the count so the caller can ask, and nothing is written.
        /// </summary>
        public (bool ok, string? message, int inUse) DeleteRole(
            int competitionId, string roleKey, string? discipline, int byMemberId, bool deleteAssignments)
        {
            if (string.IsNullOrWhiteSpace(roleKey)) return (false, "Roll saknas.", 0);

            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var inUse = db.ExecuteScalar<int>(
                "SELECT COUNT(1) FROM StaffAssignment WHERE CompetitionId = @0 AND RoleKey = @1",
                competitionId, roleKey);
            if (inUse > 0 && !deleteAssignments) return (false, null, inUse);

            var name = Resolve(competitionId, discipline, roleKey)?.DisplayName ?? roleKey;
            var row = db.SingleOrDefault<StaffRole>(
                "SELECT * FROM StaffRole WHERE OwnerType = @0 AND OwnerId = @1 AND RoleKey = @2",
                RoleOwnerType.Competition, competitionId, roleKey);
            var isBuiltIn = FunctionaryRoles.Resolve(discipline, roleKey) != null;

            if (inUse > 0)
                db.Execute("DELETE FROM StaffAssignment WHERE CompetitionId = @0 AND RoleKey = @1",
                    competitionId, roleKey);

            var now = DateTime.UtcNow;
            if (isBuiltIn)
            {
                if (row == null)
                {
                    db.Insert(new StaffRole
                    {
                        OwnerType = RoleOwnerType.Competition,
                        OwnerId = competitionId,
                        RoleKey = roleKey,
                        DisplayName = name,
                        IsActive = false,
                        CreatedByMemberId = byMemberId,
                        CreatedDate = now,
                        ModifiedDate = now,
                    });
                }
                else
                {
                    row.IsActive = false;
                    row.ModifiedDate = now;
                    db.Update(row);
                }
            }
            else if (row != null)
            {
                db.Delete(row);
            }

            Forget(competitionId);
            return (true, null, inUse);
        }

        // ---------------------------------------------------------------- helpers

        private void Forget(int competitionId)
        {
            _customMemo.Remove(competitionId);
            foreach (var k in _memo.Keys.Where(k => k.StartsWith(competitionId + "|", StringComparison.Ordinal)).ToList())
                _memo.Remove(k);
        }

        private string UniqueKey(int competitionId, string? discipline, string name)
        {
            var baseKey = Slugify(name);
            if (baseKey.Length == 0) baseKey = "roll";

            var taken = new HashSet<string>(
                ForCompetition(competitionId, discipline).Select(r => r.Key), StringComparer.OrdinalIgnoreCase);

            if (!taken.Contains(baseKey)) return baseKey;
            for (var i = 2; i < 100; i++)
            {
                var candidate = $"{baseKey}-{i}";
                if (!taken.Contains(candidate)) return candidate;
            }
            return $"{baseKey}-{Guid.NewGuid():N}"[..Math.Min(50, baseKey.Length + 9)];
        }

        /// <summary>
        /// "Läsare/Dragare/Observatör 1" → "lasare-dragare-observator-1". Decomposes to strip diacritics,
        /// but maps å/ä/ö explicitly FIRST — Unicode decomposition turns "ö" into "o", which silently
        /// collides Söder/Soder and, worse, makes "Måldomare" and "Maldomare" the same key.
        /// </summary>
        internal static string Slugify(string name)
        {
            var lowered = name.Trim().ToLowerInvariant()
                .Replace("å", "a").Replace("ä", "a").Replace("ö", "o")
                .Replace("é", "e").Replace("ü", "u").Replace("ø", "o").Replace("æ", "ae");

            var normalized = lowered.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(normalized.Length);
            var lastWasDash = false;
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(ch);
                    lastWasDash = false;
                }
                else if (!lastWasDash && sb.Length > 0)
                {
                    sb.Append('-');
                    lastWasDash = true;
                }
            }
            var slug = sb.ToString().Trim('-');
            return slug.Length > 50 ? slug[..50].Trim('-') : slug;
        }

        private static bool MatchesDiscipline(StaffRole r, string? discipline)
        {
            if (string.IsNullOrWhiteSpace(r.Disciplines)) return true;
            if (string.IsNullOrWhiteSpace(discipline)) return true;
            return r.Disciplines.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(d => string.Equals(d, discipline, StringComparison.OrdinalIgnoreCase));
        }

        private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        private static FunctionaryRole ToRole(StaffRole r)
        {
            string[] needs = Array.Empty<string>();
            if (!string.IsNullOrWhiteSpace(r.NeedsJson))
            {
                try { needs = JsonConvert.DeserializeObject<string[]>(r.NeedsJson) ?? Array.Empty<string>(); }
                catch { /* a malformed checklist must never hide the role */ }
            }
            return new FunctionaryRole
            {
                Key = r.RoleKey,
                DisplayName = r.DisplayName,
                PluralName = r.PluralName ?? "",
                DefaultScopeType = r.DefaultScopeType ?? "",
                SupportsTargetRange = r.SupportsTargetRange,
                SupportsFunctionTitle = r.SupportsFunctionTitle,
                Description = r.Description ?? "",
                Needs = needs,
                SortOrder = r.SortOrder,
            };
        }
    }
}
