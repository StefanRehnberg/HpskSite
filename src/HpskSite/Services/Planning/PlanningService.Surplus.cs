using System.Collections.Generic;
using System.Linq;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>P4 (fråga 15): överskottets underlag — arrangörens val, sparat som JSON. Talen läses alltid om ur bemanningen.</summary>
    public partial class PlanningService
    {
        public SurplusData? Surplus(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            var row = s.Database.SingleOrDefault<PlanningSurplusRow>("SELECT * FROM PlanningSurplus WHERE PlanningId = @0", planningId);
            if (row == null) return null;
            try { return Newtonsoft.Json.JsonConvert.DeserializeObject<SurplusData>(row.DataJson); }
            catch { return null; }
        }

        public void SaveSurplus(int planningId, SurplusData data, int byMemberId)
        {
            data.Fixed = (data.Fixed ?? new()).Where(f => !string.IsNullOrWhiteSpace(f.Label) || f.Amount != 0)
                .Take(PlanningSurplusRules.MaxFixed).Select(f => new SurplusFixed { Label = Clean(f.Label, 100) ?? "", Amount = f.Amount }).ToList();
            data.Key = SurplusKey.Normalize(data.Key);
            data.Orgs = (data.Orgs ?? new()).Where(o => !string.IsNullOrWhiteSpace(o.Key)).Take(200)
                .Select(o => new SurplusOrgChoice { Key = o.Key.Trim(), Name = Clean(o.Name, 100), Include = o.Include, Weight = o.Weight < 0 ? 0 : o.Weight }).ToList();
            data.Note = Clean(data.Note, 2000);
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(data);
            using var s = _scopes.CreateScope(autoComplete: true);
            var db = s.Database;
            var n = db.Execute("UPDATE PlanningSurplus SET DataJson = @1, UpdatedAt = @2, UpdatedBy = @3 WHERE PlanningId = @0", planningId, json, Now, byMemberId);
            if (n == 0) db.Execute("INSERT INTO PlanningSurplus (PlanningId, DataJson, UpdatedAt, UpdatedBy) VALUES (@0, @1, @2, @3)", planningId, json, Now, byMemberId);
            Bump(db, planningId);
        }

        /// <summary>Planeringen och dess delplaneringar (överskottet räknar allas bemanning).</summary>
        public List<int> FamilyIds(int planningId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<int>("SELECT Id FROM Planning WHERE (Id = @0 OR ParentPlanningId = @0) AND DeletedAt IS NULL ORDER BY CASE WHEN Id = @0 THEN 0 ELSE 1 END, Id", planningId);
        }
    }
}
