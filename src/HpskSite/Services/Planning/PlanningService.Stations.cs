using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// P4 (Stefan 2026-10-24, fråga 13a): stationerna i fältskytte delas mellan klubbar eller kretsar. Ett område bär sina
    /// stationer (<see cref="PlanningAreaRow.Stations"/>) och sin värd; stationschefen är bemanningens roll
    /// <i>Stationschef</i> per station. Översikten läser huvudplaneringen OCH dess delplaneringar — en förening som fått sina
    /// stationer utdelade till en egen planering finns kvar i bilden.
    /// </summary>
    public partial class PlanningService
    {
        /// <summary>Planeringarna som delar stationernas nummer: huvudplaneringen och dess delplaneringar.</summary>
        private static List<PlanningRow> StationFamily(IUmbracoDatabase db, int planningId)
        {
            var p = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null) return new List<PlanningRow>();
            var root = p.ParentPlanningId > 0 ? p.ParentPlanningId!.Value : p.Id;
            return db.Fetch<PlanningRow>("SELECT * FROM Planning WHERE (Id = @0 OR ParentPlanningId = @0) AND DeletedAt IS NULL", root);
        }

        /// <summary>Stationen som ett annat område i planeringsfamiljen redan har, eller null.</summary>
        private static string? StationConflict(IUmbracoDatabase db, int planningId, int areaId, List<int> mine)
        {
            if (mine.Count == 0) return null;
            var family = StationFamily(db, planningId);
            if (family.Count == 0) return null;
            var names = family.ToDictionary(f => f.Id, f => f.Name);
            var others = db.Fetch<PlanningAreaRow>($@"SELECT * FROM PlanningArea WHERE PlanningId IN ({string.Join(",", names.Keys)})
                AND DeletedAt IS NULL AND Stations IS NOT NULL AND Id <> @0", areaId);
            var hit = PlanningStations.Conflict(mine, others.Select(o => (
                o.PlanningId == planningId ? o.Name : $"{o.Name} ({names[o.PlanningId]})", o.Stations)));
            return hit == null ? null : $"Station {hit.Value.Station} hör redan till området {hit.Value.OtherArea}.";
        }

        public sealed record StationRow(int Number, int? AreaId, string? AreaName, int? HostClubId, int? HostRegionId, string? HostText,
            int PlanningId, string PlanningName, List<string> Chiefs, int OpenTasks);

        /// <summary>
        /// En rad per station: området och värden som ansvarar, stationscheferna ur bemanningen och de öppna uppgifterna på
        /// stationen — i planeringen och dess delplaneringar. <paramref name="stationCount"/> (tävlingens antal stationer) tar
        /// med stationer som ingen ansvarar för ännu.
        /// </summary>
        public List<StationRow> Stations(int planningId, int stationCount)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var p = db.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null) return new List<StationRow>();
            // Huvudplaneringen ser sina delplaneringars stationer; en delplanering ser sina egna.
            var family = db.Fetch<PlanningRow>("SELECT * FROM Planning WHERE (Id = @0 OR ParentPlanningId = @0) AND DeletedAt IS NULL", planningId);
            var ids = string.Join(",", family.Select(f => f.Id));
            var names = family.ToDictionary(f => f.Id, f => f.Name);
            var areas = db.Fetch<PlanningAreaRow>($"SELECT * FROM PlanningArea WHERE PlanningId IN ({ids}) AND DeletedAt IS NULL AND Stations IS NOT NULL");
            var byStation = new Dictionary<int, PlanningAreaRow>();
            foreach (var a in areas.OrderBy(a => a.PlanningId == planningId ? 0 : 1).ThenBy(a => a.SortOrder))
                foreach (var n in PlanningStations.Parse(a.Stations).Stations)
                    byStation.TryAdd(n, a);
            var chiefs = db.Fetch<ChiefRow>($@"SELECT MemberId, ScopeKey, DisplayName FROM StaffAssignment
                WHERE PlanningId IN ({ids}) AND RoleKey = N'stationschef' AND ScopeType = N'Station' AND ScopeKey IS NOT NULL ORDER BY DisplayName");
            var chiefMap = new Dictionary<int, List<string>>();
            foreach (var c in chiefs)
                if (int.TryParse(c.ScopeKey, out var n) && n > 0)
                {
                    if (!chiefMap.TryGetValue(n, out var l)) chiefMap[n] = l = new List<string>();
                    if (!l.Contains(c.DisplayName)) l.Add(c.DisplayName);
                }
            var taskKeys = db.Fetch<string>($@"SELECT ScopeKey FROM PlanningTask WHERE PlanningId IN ({ids}) AND DeletedAt IS NULL
                AND ScopeType = N'Station' AND Status <> N'Done' AND ScopeKey IS NOT NULL");
            var tasks = taskKeys.Select(k => int.TryParse(k, out var n) ? n : 0).Where(n => n > 0).GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
            var all = new SortedSet<int>(byStation.Keys.Concat(chiefMap.Keys).Concat(tasks.Keys));
            for (var i = 1; i <= Math.Min(stationCount, PlanningStations.Max); i++) all.Add(i);
            return all.Select(n =>
            {
                byStation.TryGetValue(n, out var a);
                var pid = a?.PlanningId ?? planningId;
                return new StationRow(n, a?.Id, a?.Name, a?.HostClubId, a?.HostRegionId, a?.HostText, pid, names.GetValueOrDefault(pid, ""),
                    chiefMap.GetValueOrDefault(n) ?? new List<string>(), tasks.GetValueOrDefault(n));
            }).ToList();
        }

        /// <summary>Medlemmarna som är stationschefer i planeringen och dess delplaneringar (för fältkonfigurationen).</summary>
        public List<(int MemberId, string Name, string Station)> StationChiefMembers(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var ids = string.Join(",", db.Fetch<int>("SELECT Id FROM Planning WHERE (Id = @0 OR ParentPlanningId = @0) AND DeletedAt IS NULL", planningId));
            if (ids.Length == 0) return new();
            return db.Fetch<ChiefRow>($@"SELECT MemberId, DisplayName, ScopeKey FROM StaffAssignment
                WHERE PlanningId IN ({ids}) AND RoleKey = N'stationschef' AND ScopeType = N'Station' AND MemberId IS NOT NULL AND MemberId > 0
                ORDER BY DisplayName")
                .GroupBy(r => r.MemberId ?? 0).Select(g => (g.Key, g.First().DisplayName, string.Join(", ", g.Select(x => x.ScopeKey).Distinct()))).ToList();
        }

        private sealed class ChiefRow
        {
            public int? MemberId { get; set; }
            public string? ScopeKey { get; set; }
            public string DisplayName { get; set; } = "";
        }

        // =====================================================================================
        //  Omgång 3 (Expertgruppen 3.3): en delkonfiguration per område
        // =====================================================================================

        /// <summary>Ett område med sina stationer — för delkonfigurationen.</summary>
        public sealed record AreaStations(PlanningAreaRow Area, List<int> Stations);

        public AreaStations? AreaWithStations(int planningId, int areaId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var a = s.Database.SingleOrDefault<PlanningAreaRow>(
                "SELECT * FROM PlanningArea WHERE Id = @0 AND PlanningId = @1 AND DeletedAt IS NULL", areaId, planningId);
            if (a == null) return null;
            var (stations, _) = PlanningStations.Parse(a.Stations);
            return new AreaStations(a, stations);
        }

        /// <summary>Familjens delkonfigurationer: område → konfiguration (en per område).</summary>
        public sealed record AreaConfigLink(int PlanningId, int AreaId, int ConfigId);

        public List<AreaConfigLink> AreaConfigLinks(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var family = StationFamily(s.Database, planningId);
            if (family.Count == 0) return new();
            // Ett borttaget område släpper sin konfiguration ur bilden (raden står kvar; området kan inte väckas igen).
            return s.Database.Fetch<PlanningLinkRow>($@"SELECT l.* FROM PlanningLink l JOIN PlanningArea a ON a.Id = l.AreaId AND a.DeletedAt IS NULL
                    WHERE l.TargetKind = N'{PlanningLinkRow.Faltconfig}' AND l.PlanningId IN ({string.Join(",", family.Select(f => f.Id))})")
                .Select(l => new AreaConfigLink(l.PlanningId, l.AreaId!.Value, l.TargetId)).ToList();
        }

        // =====================================================================================
        //  Banan i planeringen (Stefan 2026-10-24): banans konfiguration, ägd av huvudplaneringen
        // =====================================================================================

        /// <summary>Familjens huvudplanering (stationsnumren gäller hela familjen), eller 0.</summary>
        public int FamilyRootId(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var p = s.Database.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            return p == null ? 0 : p.ParentPlanningId > 0 ? p.ParentPlanningId!.Value : p.Id;
        }

        /// <summary>Banans konfiguration: länken utan område på familjens huvudplanering, eller null.</summary>
        public int? CourseConfigId(int planningId)
        {
            var root = FamilyRootId(planningId);
            if (root == 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.FirstOrDefault<int?>($@"SELECT TOP 1 TargetId FROM PlanningLink
                WHERE TargetKind = N'{PlanningLinkRow.Faltconfig}' AND PlanningId = @0 AND AreaId IS NULL ORDER BY Id", root);
        }

        /// <summary>Kopplar banans konfiguration till familjens huvudplanering. Vägrar en andra.</summary>
        public string? LinkCourseConfig(int planningId, int configId, int byMemberId)
        {
            var root = FamilyRootId(planningId);
            if (root == 0) return "Planeringen finns inte.";
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (db.ExecuteScalar<int>($"SELECT COUNT(*) FROM PlanningLink WITH (UPDLOCK, HOLDLOCK) WHERE TargetKind = N'{PlanningLinkRow.Faltconfig}' AND PlanningId = @0 AND AreaId IS NULL", root) > 0)
                return "Planeringen har redan sina stationer.";
            db.Insert(new PlanningLinkRow
            {
                PlanningId = root, PlanningPartId = 0, TargetKind = PlanningLinkRow.Faltconfig, TargetId = configId, AreaId = null,
                LinkedAt = Now, LinkedBy = byMemberId,
            });
            Bump(db, root);
            return null;
        }

        /// <summary>Högsta stationsnumret i familjens områden, eller 0.</summary>
        public int MaxAreaStation(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var family = StationFamily(s.Database, planningId);
            if (family.Count == 0) return 0;
            var areas = s.Database.Fetch<string>($@"SELECT Stations FROM PlanningArea WHERE PlanningId IN ({string.Join(",", family.Select(f => f.Id))})
                AND DeletedAt IS NULL AND Stations IS NOT NULL");
            return areas.SelectMany(a => PlanningStations.Parse(a).Stations).DefaultIfEmpty(0).Max();
        }

        // =====================================================================================
        //  Stationerna-vyn (Stefan 2026-10-24): stationerna väljs per förening, och en station flyttar
        // =====================================================================================

        /// <summary>Ett område vars stationer ändrades: (område, planering, de nya stationerna).</summary>
        public sealed record AreaStationsChange(int AreaId, int PlanningId, List<int> Stations);

        public sealed record AssignResult(string? Error, List<AreaStationsChange> Changed);

        /// <summary>
        /// Ger området exakt <paramref name="stations"/>. En station som ett annat område i SAMMA planering har flyttas hit;
        /// en station som hör till ett område i en annan planering i familjen (en delplanering eller huvudplaneringen) vägras
        /// — den ändras där. Svaret säger vilka områden som ändrades, så att deras delar kan följa med.
        /// </summary>
        public AssignResult AssignStations(int planningId, int areaId, IReadOnlyCollection<int> stations, int maxStation)
        {
            var wanted = stations.Where(n => n > 0).Distinct().OrderBy(n => n).ToList();
            if (wanted.Count > 0 && maxStation > 0 && wanted.Max() > maxStation)
                return new AssignResult($"Tävlingen har {maxStation} stationer. Ändra antalet stationer först.", new());
            if (wanted.Count > 0 && wanted.Max() > PlanningStations.Max)
                return new AssignResult($"Högst {PlanningStations.Max} stationer.", new());
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var family = StationFamily(db, planningId);
            if (family.Count == 0) return new AssignResult("Planeringen finns inte.", new());
            var names = family.ToDictionary(f => f.Id, f => f.Name);
            var areas = db.Fetch<PlanningAreaRow>($@"SELECT * FROM PlanningArea WITH (UPDLOCK, HOLDLOCK)
                WHERE PlanningId IN ({string.Join(",", names.Keys)}) AND DeletedAt IS NULL");
            var area = areas.FirstOrDefault(a => a.Id == areaId && a.PlanningId == planningId);
            if (area == null) return new AssignResult("Föreningen finns inte i planeringen.", new());
            foreach (var other in areas.Where(a => a.Id != areaId && a.PlanningId != planningId && a.Stations != null))
            {
                var hit = PlanningStations.Parse(other.Stations).Stations.Intersect(wanted).OrderBy(n => n).FirstOrDefault();
                if (hit > 0)
                    return new AssignResult($"Station {hit} hör till {other.Name} i planeringen {names[other.PlanningId]}. Ändra den där.", new());
            }
            var changed = new List<AreaStationsChange>();
            foreach (var other in areas.Where(a => a.Id != areaId && a.PlanningId == planningId && a.Stations != null))
            {
                var mine = PlanningStations.Parse(other.Stations).Stations;
                var left = mine.Except(wanted).ToList();
                if (left.Count == mine.Count) continue;
                other.Stations = left.Count == 0 ? null : PlanningStations.Format(left);
                db.Update(other);
                changed.Add(new AreaStationsChange(other.Id, other.PlanningId, left));
            }
            var before = PlanningStations.Parse(area.Stations).Stations.OrderBy(n => n).ToList();
            if (!before.SequenceEqual(wanted))
            {
                area.Stations = wanted.Count == 0 ? null : PlanningStations.Format(wanted);
                db.Update(area);
                changed.Add(new AreaStationsChange(area.Id, area.PlanningId, wanted));
            }
            if (changed.Count > 0) Bump(db, planningId);
            return new AssignResult(null, changed);
        }

        /// <summary>Familjens områden som har stationer (föreningarna i Stationerna-vyn), huvudplaneringens först.</summary>
        public List<PlanningAreaRow> StationAreas(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var ids = string.Join(",", db.Fetch<int>("SELECT Id FROM Planning WHERE (Id = @0 OR ParentPlanningId = @0) AND DeletedAt IS NULL", planningId));
            if (ids.Length == 0) return new();
            return db.Fetch<PlanningAreaRow>($"SELECT * FROM PlanningArea WHERE PlanningId IN ({ids}) AND DeletedAt IS NULL AND Stations IS NOT NULL")
                .OrderBy(a => a.PlanningId == planningId ? 0 : 1).ThenBy(a => a.PlanningId).ThenBy(a => a.SortOrder).ToList();
        }

        /// <summary>Kopplar en fältkonfiguration till ett område. Vägrar ett andra för samma område.</summary>
        public string? LinkAreaConfig(int planningId, int areaId, int configId, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            if (db.ExecuteScalar<int>($"SELECT COUNT(*) FROM PlanningLink WITH (UPDLOCK, HOLDLOCK) WHERE TargetKind = N'{PlanningLinkRow.Faltconfig}' AND PlanningId = @0 AND AreaId = @1",
                    planningId, areaId) > 0)
                return "Området har redan en delkonfiguration.";
            db.Insert(new PlanningLinkRow
            {
                PlanningId = planningId, PlanningPartId = 0, TargetKind = PlanningLinkRow.Faltconfig, TargetId = configId, AreaId = areaId,
                LinkedAt = Now, LinkedBy = byMemberId,
            });
            Bump(db, planningId);
            return null;
        }
    }
}
