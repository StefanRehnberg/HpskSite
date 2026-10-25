using System;
using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Stationerna i fältskytte. Stefan 2026-10-25: EN konfiguration på stationernas planering, och en ANSVARIG per station
    /// (<see cref="PlanningStationResponsibleRow"/>, <c>PlanningService.StationResponsible.cs</c>) — inte områden.
    /// Stationschefen är bemanningens roll <i>Stationschef</i> per station.
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

        public sealed record StationRow(int Number, PlanningStationResponsibleRow? Responsible, int PlanningId, List<string> Chiefs, int OpenTasks);

        /// <summary>
        /// En rad per station: den ansvarige (<see cref="PlanningStationResponsibleRow"/>), stationscheferna ur bemanningen
        /// och de öppna uppgifterna på stationen — i planeringen och dess delplaneringar. <paramref name="stationCount"/>
        /// (tävlingens antal stationer) tar med stationer som ingen ansvarar för ännu.
        /// </summary>
        public List<StationRow> Stations(int planningId, int stationCount)
        {
            var owner = StationOwnerId(planningId);
            if (owner == 0) return new List<StationRow>();
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var family = StationFamily(db, planningId);
            var ids = string.Join(",", family.Select(f => f.Id).DefaultIfEmpty(planningId));
            var byStation = db.Fetch<PlanningStationResponsibleRow>("SELECT * FROM PlanningStationResponsible WHERE PlanningId = @0", owner)
                .ToDictionary(r => r.Station);
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
            return all.Select(n => new StationRow(n, byStation.GetValueOrDefault(n), owner,
                chiefMap.GetValueOrDefault(n) ?? new List<string>(), tasks.GetValueOrDefault(n))).ToList();
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

        /// <summary>
        /// Stefan 2026-10-25 ("vilken gäller?"): stationerna har EN plats — den planering i familjen som är fältskytte.
        /// Planeringen själv om den har fältgren; annars familjens enda fältplanering (en SM-veckas Fält-delplanering);
        /// annars huvudplaneringen (en planering utan gren). 0 = finns inte.
        /// </summary>
        public int StationOwnerId(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var p = s.Database.SingleOrDefault<PlanningRow>("SELECT * FROM Planning WHERE Id = @0 AND DeletedAt IS NULL", planningId);
            if (p == null) return 0;
            static bool Falt(PlanningRow r) => HpskSite.Models.Staffing.FunctionaryRoles.FaltFamily.Contains(r.DisciplineKey ?? "", StringComparer.OrdinalIgnoreCase);
            var family = StationFamily(s.Database, planningId);
            var rootId = p.ParentPlanningId > 0 ? p.ParentPlanningId!.Value : p.Id;
            // En fälttävling vars klubbar fått var sin delplanering: huvudplaneringen äger stationerna.
            if (family.FirstOrDefault(f => f.Id == rootId) is { } root && Falt(root)) return root.Id;
            // En vecka utan gren: fältets delplanering (planeringen själv om den är fält, annars familjens enda).
            if (Falt(p)) return p.Id;
            var falt = family.Where(Falt).ToList();
            if (falt.Count == 1) return falt[0].Id;
            return rootId;
        }

        /// <summary>
        /// Stationernas konfiguration: länken utan område på stationernas planering (<see cref="StationOwnerId"/>). Stationer
        /// som skapats på huvudplaneringen innan en Fält-delplanering fanns följer med dit (betan, 2026-10-24).
        /// </summary>
        public int? CourseConfigId(int planningId)
        {
            var owner = StationOwnerId(planningId);
            if (owner == 0) return null;
            using var s = _scopes.CreateScope(autoComplete: true);
            var family = StationFamily(s.Database, planningId).Select(f => f.Id).ToList();
            var links = s.Database.Fetch<PlanningLinkRow>($@"SELECT * FROM PlanningLink
                WHERE TargetKind = N'{PlanningLinkRow.Faltconfig}' AND AreaId IS NULL AND PlanningId IN ({string.Join(",", family.DefaultIfEmpty(owner))}) ORDER BY Id");
            return (links.FirstOrDefault(l => l.PlanningId == owner) ?? links.FirstOrDefault())?.TargetId;
        }

        /// <summary>Projektet i Fältkonfiguratorn som planeringens stationer ligger i, eller null.</summary>
        public int? ProjectLinkId(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.FirstOrDefault<int?>($@"SELECT TOP 1 l.TargetId FROM PlanningLink l JOIN FaltskytteProject fp ON fp.Id = l.TargetId
                WHERE l.TargetKind = N'{PlanningLinkRow.Faltproject}' AND l.PlanningId = @0 ORDER BY l.Id", planningId);
        }

        /// <summary>Kopplar ett projekt i Fältkonfiguratorn till planeringen.</summary>
        public void LinkProject(int planningId, int projectId, int byMemberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Insert(new PlanningLinkRow
            {
                PlanningId = planningId, PlanningPartId = 0, TargetKind = PlanningLinkRow.Faltproject, TargetId = projectId, AreaId = null,
                LinkedAt = Now, LinkedBy = byMemberId,
            });
        }

        /// <summary>Kopplar stationernas konfiguration till stationernas planering. Vägrar en andra i familjen.</summary>
        public string? LinkCourseConfig(int planningId, int configId, int byMemberId)
        {
            if (CourseConfigId(planningId) is > 0) return "Planeringen har redan sina stationer.";
            var root = StationOwnerId(planningId);
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

        /// <summary>Högsta stationen som har en ansvarig, på stationernas planering, eller 0.</summary>
        public int MaxAssignedStation(int planningId)
        {
            var owner = StationOwnerId(planningId);
            if (owner == 0) return 0;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int?>("SELECT MAX(Station) FROM PlanningStationResponsible WHERE PlanningId = @0", owner) ?? 0;
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
