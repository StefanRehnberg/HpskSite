using System.Globalization;
using HpskSite.Models.Staffing;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Shift/pass model + crew needs + coverage matrix (big-comp staffing). Passes are named day time-blocks;
    /// crew needs say how many of each role a station (or the whole comp) needs; coverage = needed vs filled
    /// per pass/scope/role, computed from the assignments' PassId + scope + role.
    /// </summary>
    public class StaffPassService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly RoleCatalogService _roles;

        public StaffPassService(IScopeProvider scopeProvider, RoleCatalogService roles)
        {
            _scopeProvider = scopeProvider;
            _roles = roles;
        }

        // ---- passes ----

        public List<StaffPassView> GetPasses(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffPass>(
                    "SELECT * FROM StaffPass WHERE " + StaffOwner.Where(0) + " ORDER BY PassDate, SortOrder, StartTime, Id", owner.P, owner.C)
                .Select(ToPassView).ToList();
        }

        public int SavePass(SavePassRequest req, StaffOwner owner, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            StaffPass row;
            if (req.Id > 0)
            {
                // Bara ett pass i den här tävlingen — ett id utifrån får aldrig flytta en annan tävlings pass hit.
                var existing = db.SingleOrDefault<StaffPass>(
                    "SELECT * FROM StaffPass WHERE Id = @0 AND " + StaffOwner.Where(1), req.Id, owner.P, owner.C);
                if (existing == null) return 0;
                row = existing;
            }
            else
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM StaffPass WHERE " + StaffOwner.Where(0), owner.P, owner.C) ?? 0;
                row = new StaffPass { SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            row.PlanningId = owner.PlanningIdOrNull;
            row.CompetitionId = owner.CompetitionIdOrNull;
            row.PassDate = ParseDate(req.Date) ?? row.PassDate;
            row.StartTime = CleanTime(req.StartTime);
            row.EndTime = CleanTime(req.EndTime);
            row.Label = (req.Label ?? "").Trim();
            if (row.Id > 0) db.Update(row); else row.Id = Convert.ToInt32(db.Insert(row));
            return row.Id;
        }

        public void DeletePass(int id, StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("UPDATE StaffAssignment SET PassId = NULL WHERE PassId = @0 AND " + StaffOwner.Where(1), id, owner.P, owner.C);
            db.Execute("DELETE FROM StaffPass WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P, owner.C);
        }

        // ---- crew needs ----

        /// <summary>Alla behovsrader, även dagarnas avvikelser. Räkna med <see cref="CrewNeedRules"/>.</summary>
        public List<StaffCrewNeed> GetAllCrewNeeds(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffCrewNeed>("SELECT * FROM StaffCrewNeed WHERE " + StaffOwner.Where(0), owner.P, owner.C);
        }

        /// <summary>
        /// Omgång 3 (3.2): stationerna för en planering som inte (eller inte ännu) har en fälttävling, stigande — i hela
        /// familjen (huvudplaneringen och delplaneringarna). Stefan 2026-10-25: stationerna 1..N ur stationernas
        /// fältkonfiguration (<c>_stationCount</c> eller de byggda), plus stationer som har en ansvarig.
        /// </summary>
        public List<int> FamilyAreaStations(StaffOwner owner)
        {
            if (owner.PlanningIdOrNull is not int pid || pid <= 0) return new();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var root = HpskSite.Services.Planning.PlanningFamily.Root(db, pid);
            if (root <= 0) return new();
            var set = new SortedSet<int>(db.Fetch<int>(@"SELECT r.Station FROM PlanningStationResponsible r JOIN Planning p ON p.Id = r.PlanningId
                WHERE p.FamilyRootId = @0 AND p.DeletedAt IS NULL", root));
            var blobs = db.Fetch<string>(@"SELECT c.JsonBlob FROM PlanningLink l JOIN Planning p ON p.Id = l.PlanningId
                JOIN FaltskytteConfiguration c ON c.Id = l.TargetId
                WHERE l.TargetKind = N'faltconfig' AND p.FamilyRootId = @0 AND p.DeletedAt IS NULL", root);
            var count = 0;
            foreach (var b in blobs)
            {
                try
                {
                    var o = string.IsNullOrWhiteSpace(b) ? null : Newtonsoft.Json.Linq.JObject.Parse(b);
                    count = Math.Max(count, Math.Max(o?.Value<int?>(HpskSite.Models.Planning.CourseAssembly.StationCountKey) ?? 0,
                        HpskSite.CompetitionTypes.Faltskytte.Services.FaltskytteAttachedConfigState.StationCount(b)));
                }
                catch { /* en oläsbar konfiguration ger inga stationer */ }
            }
            for (var n = 1; n <= Math.Min(count, HpskSite.Models.Planning.PlanningStations.Max); n++) set.Add(n);
            return set.ToList();
        }

        /// <summary>
        /// Skriver om en rolls behov: alla dagars antal och de dagar som avviker (<see cref="CrewNeedRules.Plan"/>).
        /// Bara hela-tävlingen-behovet (ScopeKind All) — stationernas behov rörs inte.
        /// </summary>
        public void SetRoleNeeds(StaffOwner owner, string roleKey, int all, IEnumerable<(DateTime Day, int Count)> days)
            => SetRoleNeeds(owner, roleKey, CrewNeedScope.All, null, all, days, Array.Empty<(int, DateTime, int)>());

        /// <summary>
        /// Omgång 3 (3.2): rollens behov för en sort (hela tävlingen eller varje station), en bestämd station
        /// (<paramref name="scopeKey"/>) och passen som avviker från sin dag. Skriver om just den stationens rader — varken
        /// de andra stationernas eller hela tävlingens rörs.
        /// </summary>
        public void SetRoleNeeds(StaffOwner owner, string roleKey, string scopeKind, string? scopeKey, int all,
            IEnumerable<(DateTime Day, int Count)> days, IEnumerable<(int PassId, DateTime Day, int Count)> passes)
        {
            var key = (roleKey ?? "").Trim();
            if (key.Length == 0) return;
            var kind = string.Equals(scopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase) ? CrewNeedScope.Station : CrewNeedScope.All;
            var sk = string.IsNullOrWhiteSpace(scopeKey) ? null : scopeKey.Trim();
            var dayList = days.ToList();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("DELETE FROM StaffCrewNeed WHERE " + StaffOwner.Where(0) + " AND ScopeKind = @2 AND RoleKey = @3 AND ISNULL(ScopeKey, N'') = @4",
                owner.P, owner.C, kind, key, sk ?? "");
            foreach (var (day, count) in CrewNeedRules.Plan(all, dayList))
                db.Insert(new StaffCrewNeed { PlanningId = owner.PlanningIdOrNull, CompetitionId = owner.CompetitionIdOrNull,
                    ScopeKind = kind, RoleKey = key, Count = count, DayDate = day?.Date, ScopeKey = sk });
            // Ett pass räknas mot SIN dag: utan rad för dagen är det alla dagars antal.
            foreach (var (passId, day, count) in CrewNeedRules.PlanPasses(all, dayList, passes))
                db.Insert(new StaffCrewNeed { PlanningId = owner.PlanningIdOrNull, CompetitionId = owner.CompetitionIdOrNull,
                    ScopeKind = kind, RoleKey = key, Count = count, DayDate = day, ScopeKey = sk, PassId = passId });
        }

        /// <summary>Alla dagars antal per roll (raderna utan dag) — "Skift och behov"-dialogen.</summary>
        public List<CrewNeedRow> GetCrewNeeds(StaffOwner owner, string? discipline)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffCrewNeed>("SELECT * FROM StaffCrewNeed WHERE " + StaffOwner.Where(0) + " AND DayDate IS NULL AND ScopeKey IS NULL AND PassId IS NULL", owner.P, owner.C)
                .Select(n => new CrewNeedRow
                {
                    ScopeKind = n.ScopeKind,
                    RoleKey = n.RoleKey,
                    RoleName = _roles.NameFor(owner, discipline, n.RoleKey),
                    Count = n.Count,
                }).ToList();
        }

        /// <summary>Full replace of the comp's crew needs (only rows with Count &gt; 0 are kept). Dagarnas
        /// avvikelser (DayDate satt) rörs inte — de sätts i rutnätet.</summary>
        public void SaveCrewNeeds(StaffOwner owner, List<CrewNeedRow> needs)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("DELETE FROM StaffCrewNeed WHERE " + StaffOwner.Where(0) + " AND DayDate IS NULL AND ScopeKey IS NULL AND PassId IS NULL", owner.P, owner.C);
            foreach (var n in (needs ?? new()).Where(n => n.Count > 0 && !string.IsNullOrWhiteSpace(n.RoleKey)))
            {
                var kind = string.Equals(n.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase) ? CrewNeedScope.Station : CrewNeedScope.All;
                db.Insert(new StaffCrewNeed { PlanningId = owner.PlanningIdOrNull, CompetitionId = owner.CompetitionIdOrNull, ScopeKind = kind, RoleKey = n.RoleKey.Trim(), Count = n.Count });
            }
        }

        // ---- coverage matrix ----

        public CoverageResponse BuildCoverage(StaffOwner owner, string? discipline, int stationCount)
        {
            var passes = GetPasses(owner);
            List<StaffCrewNeed> needs;
            List<StaffAssignment> assignments;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                needs = db.Fetch<StaffCrewNeed>("SELECT * FROM StaffCrewNeed WHERE " + StaffOwner.Where(0), owner.P, owner.C);
                assignments = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0), owner.P, owner.C);
            }
            bool isFalt = FunctionaryRoles.FaltFamily.Contains(discipline ?? "", StringComparer.OrdinalIgnoreCase);
            int stations = isFalt ? Math.Max(0, stationCount) : 0;

            string RoleName(string key) => _roles.NameFor(owner, discipline, key);
            // Omgång 3 (3.2, "tiden är sanningen"): ett uppdrag täcker passet när det ligger i passet ELLER, utan pass, samma
            // dag och tiderna överlappar (utan tid = hela dagen). Räknas som flest samtidigt under passet.
            var passDays = passes.ToDictionary(p => p.Id, p => DateTime.TryParse(p.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.Date : (DateTime?)null);
            (TimeSpan, TimeSpan)? WindowOf(StaffPassView p) =>
                StaffTimeCoverage.ParseRange(string.IsNullOrEmpty(p.StartTime) || string.IsNullOrEmpty(p.EndTime) ? null : p.StartTime + "-" + p.EndTime);
            DateTime? DayOf(StaffAssignment a) => a.StartsAt?.Date ?? a.DayDate?.Date ?? (a.PassId is int ap && passDays.TryGetValue(ap, out var pd) ? pd : null);
            (TimeSpan, TimeSpan)? SpanOf(StaffAssignment a) =>
                a.StartsAt is DateTime s && a.EndsAt is DateTime e && e > s ? (s.TimeOfDay, e.TimeOfDay) : null;
            int Filled(StaffPassView p, Func<StaffAssignment, bool> mine)
            {
                var w = WindowOf(p);
                var day = passDays.GetValueOrDefault(p.Id);
                var inPass = assignments.Where(a => mine(a) && (a.PassId == p.Id || (w != null && a.PassId == null && day != null && DayOf(a) == day)));
                return StaffTimeCoverage.Peak(inPass.Select(a => a.PassId == p.Id ? w : SpanOf(a)), w);
            }
            int FilledStation(StaffPassView p, string station, string role) => Filled(p, a =>
                string.Equals(a.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ScopeKey, station, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.RoleKey, role, StringComparison.OrdinalIgnoreCase));
            int FilledGeneral(StaffPassView p, string role) => Filled(p, a =>
                string.Equals(a.RoleKey, role, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(a.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase));

            var resp = new CoverageResponse
            {
                Discipline = discipline ?? "",
                StationCount = stations,
                HasNeeds = needs.Any(n => n.Count > 0),
            };

            foreach (var p in passes)
            {
                // Behovet den dag passet ligger (dagarnas avvikelser inräknade).
                DateTime? passDay = DateTime.TryParse(p.Date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pd) ? pd.Date : null;
                var stationNeeds = CrewNeedRules.EffectiveFor(needs, CrewNeedScope.Station, passDay)
                    .Select(x => new StaffCrewNeed { ScopeKind = CrewNeedScope.Station, RoleKey = x.RoleKey, Count = x.Count }).ToList();
                var allNeeds = CrewNeedRules.EffectiveFor(needs, CrewNeedScope.All, passDay)
                    .Select(x => new StaffCrewNeed { ScopeKind = CrewNeedScope.All, RoleKey = x.RoleKey, Count = x.Count }).ToList();
                var cp = new CoveragePass { PassId = p.Id, Label = p.Label, Date = p.Date, TimeLabel = TimeLabel(p) };
                for (int n = 1; n <= stations; n++)
                {
                    var unit = new CoverageUnit { ScopeKey = n.ToString() };
                    foreach (var need in stationNeeds)
                    {
                        var filled = FilledStation(p, n.ToString(), need.RoleKey);
                        unit.Roles.Add(new CoverageRole { RoleKey = need.RoleKey, RoleName = RoleName(need.RoleKey), Needed = need.Count, Filled = filled });
                        unit.Needed += need.Count; unit.Filled += filled;
                    }
                    cp.Stations.Add(unit);
                }
                foreach (var need in allNeeds)
                {
                    var filled = FilledGeneral(p, need.RoleKey);
                    cp.General.Add(new CoverageRole { RoleKey = need.RoleKey, RoleName = RoleName(need.RoleKey), Needed = need.Count, Filled = filled });
                    cp.Needed += need.Count; cp.Filled += filled;
                }
                cp.Needed += cp.Stations.Sum(s => s.Needed);
                cp.Filled += cp.Stations.Sum(s => s.Filled);
                resp.Passes.Add(cp);
            }
            resp.TotalNeeded = resp.Passes.Sum(p => p.Needed);
            resp.TotalFilled = resp.Passes.Sum(p => p.Filled);
            return resp;
        }

        // ---- helpers ----

        private static StaffPassView ToPassView(StaffPass p)
        {
            var tl = TimeLabel2(p.StartTime, p.EndTime);
            var lbl = string.IsNullOrWhiteSpace(p.Label) ? p.PassDate.ToString("ddd d MMM", CultureInfo.GetCultureInfo("sv-SE")) : p.Label;
            return new StaffPassView
            {
                Id = p.Id,
                Date = p.PassDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                StartTime = p.StartTime,
                EndTime = p.EndTime,
                Label = p.Label,
                DisplayLabel = tl == null ? lbl : $"{lbl} · {tl}",
            };
        }

        private static string? TimeLabel(StaffPassView p) => TimeLabel2(p.StartTime, p.EndTime);
        private static string? TimeLabel2(string? from, string? to)
            => (string.IsNullOrEmpty(from) && string.IsNullOrEmpty(to)) ? null : $"{from}–{to}";

        private static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var d)) return d.Date;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.Date;
            return null;
        }

        private static string? CleanTime(string? s)
        {
            s = s?.Trim();
            return string.IsNullOrEmpty(s) ? null : (s.Length > 5 ? s.Substring(0, 5) : s);
        }
    }
}
