using HpskSite.Models.Staffing;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Editable per-club/region planning templates (Phase 1.5). A club/region snapshots the prep + crew of a
    /// competition it has planned into a named template, then seeds a fresh comp from it — instead of the
    /// generic built-in defaults. Owner is resolved from the competition's host (club, or region for
    /// region-hosted comps); a club-hosted comp also sees its region's templates.
    /// </summary>
    public class StaffingTemplateService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly WorkBreakdownService _work;

        public StaffingTemplateService(IScopeProvider scopeProvider, IContentService contentService, WorkBreakdownService work)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _work = work;
        }

        private (string OwnerType, string OwnerKey, string? RegionCode) ResolveHost(int competitionId)
        {
            var comp = _contentService.GetById(competitionId);
            var clubId = comp?.GetValue<int>("clubId") ?? 0;
            if (clubId > 0)
            {
                string? region = null;
                try { region = _contentService.GetById(clubId)?.GetValue<string>("regionalFederation"); } catch { }
                return (StaffingTemplateOwner.Club, clubId.ToString(), string.IsNullOrWhiteSpace(region) ? null : region);
            }
            var regionCode = comp?.GetValue<string>("regionalFederation");
            return (StaffingTemplateOwner.Region, regionCode ?? "", string.IsNullOrWhiteSpace(regionCode) ? null : regionCode);
        }

        /// <summary>Templates applicable to a competition: its club's own + its region's (or the region's for
        /// a region-hosted comp), matching the discipline (or "*").</summary>
        public List<StaffingTemplateView> GetForCompetition(int competitionId, string? discipline, bool canManage)
        {
            var host = ResolveHost(competitionId);
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var all = scope.Database.Fetch<StaffingTemplate>("SELECT * FROM StaffingTemplate ORDER BY Name");

            var d = discipline ?? "";
            return all.Where(t =>
                    (string.Equals(t.OwnerType, StaffingTemplateOwner.Club, StringComparison.OrdinalIgnoreCase) && t.OwnerKey == host.OwnerKey && host.OwnerType == StaffingTemplateOwner.Club)
                    || (string.Equals(t.OwnerType, StaffingTemplateOwner.Region, StringComparison.OrdinalIgnoreCase) && host.RegionCode != null && t.OwnerKey == host.RegionCode))
                .Where(t => t.Discipline == "*" || string.Equals(t.Discipline, d, StringComparison.OrdinalIgnoreCase))
                .Select(t =>
                {
                    var rows = Parse(t.RowsJson);
                    return new StaffingTemplateView
                    {
                        Id = t.Id,
                        Name = t.Name,
                        OwnerType = t.OwnerType,
                        Discipline = t.Discipline,
                        AreaCount = rows.Prep.Count,
                        ItemCount = rows.Prep.Sum(p => p.Items.Count),
                        StaffRowCount = rows.Staffing.Count,
                        CanManage = canManage,
                    };
                })
                .ToList();
        }

        public StaffingTemplate? GetById(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefault<StaffingTemplate>("SELECT * FROM StaffingTemplate WHERE Id = @0", id);
        }

        /// <summary>Snapshot a competition's current Förberedelser + roster crew counts into a named template.</summary>
        public int SaveSnapshot(int competitionId, string name, string? ownerTypePref, int byMemberId)
        {
            var host = ResolveHost(competitionId);
            var ownerType = string.Equals(ownerTypePref, StaffingTemplateOwner.Region, StringComparison.OrdinalIgnoreCase) && host.RegionCode != null
                ? StaffingTemplateOwner.Region : host.OwnerType;
            var ownerKey = ownerType == StaffingTemplateOwner.Region ? (host.RegionCode ?? host.OwnerKey) : host.OwnerKey;

            DateTime? compDate = null;
            try { var c = _contentService.GetById(competitionId)?.GetValue<DateTime?>("competitionDate"); if (c is { } cd && cd != default) compDate = cd; } catch { }

            var rows = new StaffingTemplateRows();
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                var areas = db.Fetch<WorkArea>("SELECT * FROM WorkArea WHERE CompetitionId = @0 ORDER BY SortOrder, Id", competitionId);
                var items = db.Fetch<WorkItem>("SELECT * FROM WorkItem WHERE CompetitionId = @0 ORDER BY SortOrder, Id", competitionId);
                var itemsByArea = items.GroupBy(i => i.WorkAreaId).ToDictionary(g => g.Key, g => g.ToList());
                foreach (var a in areas)
                {
                    var ta = new TemplatePrepArea { Area = a.Name };
                    if (itemsByArea.TryGetValue(a.Id, out var its))
                        foreach (var i in its)
                            ta.Items.Add(new TemplatePrepItem
                            {
                                Title = i.Title,
                                DaysBeforeComp = (compDate.HasValue && i.DueDate.HasValue) ? (int)(compDate.Value.Date - i.DueDate.Value.Date).TotalDays : (int?)null,
                            });
                    rows.Prep.Add(ta);
                }

                var assignments = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE CompetitionId = @0", competitionId);
                rows.Staffing = assignments
                    .GroupBy(x => (Role: x.RoleKey ?? "", Scope: string.IsNullOrEmpty(x.ScopeType) ? StaffScopeType.All : x.ScopeType!))
                    .Select(g => new TemplateStaffRow { RoleKey = g.Key.Role, ScopeType = g.Key.Scope, Count = g.Count() })
                    .ToList();

                var row = new StaffingTemplate
                {
                    Name = name.Trim().Length > 120 ? name.Trim()[..120] : name.Trim(),
                    OwnerType = ownerType,
                    OwnerKey = ownerKey,
                    Discipline = _contentService.GetById(competitionId)?.GetValue<string>("competitionType") ?? "*",
                    RowsJson = JsonConvert.SerializeObject(rows),
                    CreatedByMemberId = byMemberId,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                };
                return Convert.ToInt32(db.Insert(row));
            }
        }

        public bool Delete(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Execute("DELETE FROM StaffingTemplate WHERE Id = @0", id) > 0;
        }

        /// <summary>Seed a competition's Förberedelser from a saved template's prep rows (date-anchored).</summary>
        public int SeedFromTemplate(int competitionId, int templateId, DateTime? compDate, int byMemberId)
        {
            var t = GetById(templateId);
            if (t == null) return 0;
            var rows = Parse(t.RowsJson);
            var areas = rows.Prep
                .Select(p => new PrepArea(p.Area, p.Items.Select(i => new PrepItem(i.Title, i.DaysBeforeComp)).ToList()))
                .ToList();
            return _work.SeedAreas(competitionId, areas, compDate, byMemberId);
        }

        private static StaffingTemplateRows Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new StaffingTemplateRows();
            try { return JsonConvert.DeserializeObject<StaffingTemplateRows>(json) ?? new StaffingTemplateRows(); }
            catch { return new StaffingTemplateRows(); }
        }
    }
}
