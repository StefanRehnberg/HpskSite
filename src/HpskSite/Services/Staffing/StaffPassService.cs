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

        public StaffPassService(IScopeProvider scopeProvider) => _scopeProvider = scopeProvider;

        // ---- passes ----

        public List<StaffPassView> GetPasses(int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffPass>(
                    "SELECT * FROM StaffPass WHERE CompetitionId = @0 ORDER BY PassDate, SortOrder, StartTime, Id", competitionId)
                .Select(ToPassView).ToList();
        }

        public int SavePass(SavePassRequest req, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            StaffPass row;
            if (req.Id > 0)
                row = db.SingleOrDefault<StaffPass>("SELECT * FROM StaffPass WHERE Id = @0", req.Id)
                      ?? new StaffPass { CompetitionId = req.CompetitionId, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            else
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM StaffPass WHERE CompetitionId = @0", req.CompetitionId) ?? 0;
                row = new StaffPass { CompetitionId = req.CompetitionId, SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            row.CompetitionId = req.CompetitionId;
            row.PassDate = ParseDate(req.Date) ?? row.PassDate;
            row.StartTime = CleanTime(req.StartTime);
            row.EndTime = CleanTime(req.EndTime);
            row.Label = (req.Label ?? "").Trim();
            if (row.Id > 0) db.Update(row); else row.Id = Convert.ToInt32(db.Insert(row));
            return row.Id;
        }

        public void DeletePass(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("UPDATE StaffAssignment SET PassId = NULL WHERE PassId = @0 AND CompetitionId = @1", id, competitionId);
            db.Execute("DELETE FROM StaffPass WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
        }

        // ---- crew needs ----

        public List<CrewNeedRow> GetCrewNeeds(int competitionId, string? discipline)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffCrewNeed>("SELECT * FROM StaffCrewNeed WHERE CompetitionId = @0", competitionId)
                .Select(n => new CrewNeedRow
                {
                    ScopeKind = n.ScopeKind,
                    RoleKey = n.RoleKey,
                    RoleName = FunctionaryRoles.Resolve(discipline, n.RoleKey)?.DisplayName ?? n.RoleKey,
                    Count = n.Count,
                }).ToList();
        }

        /// <summary>Full replace of the comp's crew needs (only rows with Count &gt; 0 are kept).</summary>
        public void SaveCrewNeeds(int competitionId, List<CrewNeedRow> needs)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("DELETE FROM StaffCrewNeed WHERE CompetitionId = @0", competitionId);
            foreach (var n in (needs ?? new()).Where(n => n.Count > 0 && !string.IsNullOrWhiteSpace(n.RoleKey)))
            {
                var kind = string.Equals(n.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase) ? CrewNeedScope.Station : CrewNeedScope.All;
                db.Insert(new StaffCrewNeed { CompetitionId = competitionId, ScopeKind = kind, RoleKey = n.RoleKey.Trim(), Count = n.Count });
            }
        }

        // ---- coverage matrix ----

        public CoverageResponse BuildCoverage(int competitionId, string? discipline, int stationCount)
        {
            var passes = GetPasses(competitionId);
            List<StaffCrewNeed> needs;
            List<StaffAssignment> assignments;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                needs = db.Fetch<StaffCrewNeed>("SELECT * FROM StaffCrewNeed WHERE CompetitionId = @0 AND Count > 0", competitionId);
                assignments = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND PassId IS NOT NULL", competitionId);
            }

            var stationNeeds = needs.Where(n => string.Equals(n.ScopeKind, CrewNeedScope.Station, StringComparison.OrdinalIgnoreCase)).ToList();
            var allNeeds = needs.Where(n => string.Equals(n.ScopeKind, CrewNeedScope.All, StringComparison.OrdinalIgnoreCase)).ToList();
            bool isFalt = FunctionaryRoles.FaltFamily.Contains(discipline ?? "", StringComparer.OrdinalIgnoreCase);
            int stations = isFalt ? Math.Max(0, stationCount) : 0;

            string RoleName(string key) => FunctionaryRoles.Resolve(discipline, key)?.DisplayName ?? key;
            // filled counts keyed for fast lookup
            int FilledStation(int passId, string station, string role) => assignments.Count(a => a.PassId == passId
                && string.Equals(a.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.ScopeKey, station, StringComparison.OrdinalIgnoreCase)
                && string.Equals(a.RoleKey, role, StringComparison.OrdinalIgnoreCase));
            int FilledGeneral(int passId, string role) => assignments.Count(a => a.PassId == passId
                && string.Equals(a.RoleKey, role, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(a.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase));

            var resp = new CoverageResponse
            {
                Discipline = discipline ?? "",
                StationCount = stations,
                HasNeeds = needs.Count > 0,
            };

            foreach (var p in passes)
            {
                var cp = new CoveragePass { PassId = p.Id, Label = p.Label, Date = p.Date, TimeLabel = TimeLabel(p) };
                for (int n = 1; n <= stations; n++)
                {
                    var unit = new CoverageUnit { ScopeKey = n.ToString() };
                    foreach (var need in stationNeeds)
                    {
                        var filled = FilledStation(p.Id, n.ToString(), need.RoleKey);
                        unit.Roles.Add(new CoverageRole { RoleKey = need.RoleKey, RoleName = RoleName(need.RoleKey), Needed = need.Count, Filled = filled });
                        unit.Needed += need.Count; unit.Filled += filled;
                    }
                    cp.Stations.Add(unit);
                }
                foreach (var need in allNeeds)
                {
                    var filled = FilledGeneral(p.Id, need.RoleKey);
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
