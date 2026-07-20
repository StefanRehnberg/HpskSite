using System.Globalization;
using HpskSite.Models.Staffing;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Data layer for the preparation work-breakdown (Förberedelser): områden (WorkArea) + uppgifter
    /// (WorkItem). Competition-scoped; per-område progress + overdue are computed on read. Independent
    /// of start lists — pure planning. Authorization is the controller's job.
    /// </summary>
    public class WorkBreakdownService
    {
        private readonly IScopeProvider _scopeProvider;

        public WorkBreakdownService(IScopeProvider scopeProvider)
        {
            _scopeProvider = scopeProvider;
        }

        public WorkBreakdownResponse Build(int competitionId, bool canEdit)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var areas = db.Fetch<WorkArea>(
                "SELECT * FROM WorkArea WHERE CompetitionId = @0 ORDER BY SortOrder, Id", competitionId);
            var items = db.Fetch<WorkItem>(
                "SELECT * FROM WorkItem WHERE CompetitionId = @0 ORDER BY SortOrder, Id", competitionId);

            var today = DateTime.UtcNow.Date;
            var itemsByArea = items.GroupBy(i => i.WorkAreaId).ToDictionary(g => g.Key, g => g.ToList());

            var resp = new WorkBreakdownResponse { CanEdit = canEdit };
            foreach (var a in areas)
            {
                itemsByArea.TryGetValue(a.Id, out var areaItems);
                areaItems ??= new List<WorkItem>();
                var views = areaItems.Select(i => ToView(i, today)).ToList();
                resp.Areas.Add(new WorkAreaView
                {
                    Id = a.Id,
                    Name = a.Name,
                    ResponsibleMemberId = a.ResponsibleMemberId,
                    ResponsibleName = a.ResponsibleName,
                    SortOrder = a.SortOrder,
                    Items = views,
                    TotalCount = views.Count,
                    DoneCount = views.Count(v => string.Equals(v.Status, WorkItemStatus.Klar, StringComparison.OrdinalIgnoreCase)),
                    OverdueCount = views.Count(v => v.IsOverdue),
                });
            }
            return resp;
        }

        // ---- WorkArea writes ----

        public int SaveArea(SaveWorkAreaRequest req, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            WorkArea row;
            if (req.Id > 0)
            {
                row = db.SingleOrDefault<WorkArea>("SELECT * FROM WorkArea WHERE Id = @0", req.Id)
                      ?? new WorkArea { CompetitionId = req.CompetitionId, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            else
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM WorkArea WHERE CompetitionId = @0", req.CompetitionId) ?? 0;
                row = new WorkArea { CompetitionId = req.CompetitionId, SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            row.CompetitionId = req.CompetitionId;
            row.Name = (req.Name ?? "").Trim();
            row.ResponsibleMemberId = req.ResponsibleMemberId is > 0 ? req.ResponsibleMemberId : null;
            row.ResponsibleName = string.IsNullOrWhiteSpace(req.ResponsibleName) ? null : req.ResponsibleName.Trim();

            if (row.Id > 0) db.Update(row);
            else row.Id = Convert.ToInt32(db.Insert(row));
            return row.Id;
        }

        public void DeleteArea(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            db.Execute("DELETE FROM WorkItem WHERE WorkAreaId = @0 AND CompetitionId = @1", id, competitionId);
            db.Execute("DELETE FROM WorkArea WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
        }

        // ---- WorkItem writes ----

        public int SaveItem(SaveWorkItemRequest req, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            WorkItem row;
            if (req.Id > 0)
            {
                row = db.SingleOrDefault<WorkItem>("SELECT * FROM WorkItem WHERE Id = @0", req.Id)
                      ?? new WorkItem { CompetitionId = req.CompetitionId, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            else
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM WorkItem WHERE WorkAreaId = @0", req.WorkAreaId) ?? 0;
                row = new WorkItem { CompetitionId = req.CompetitionId, SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
            }
            row.CompetitionId = req.CompetitionId;
            row.WorkAreaId = req.WorkAreaId;
            row.Title = (req.Title ?? "").Trim();
            row.Description = string.IsNullOrWhiteSpace(req.Description) ? null : req.Description.Trim();
            row.AssignedMemberId = req.AssignedMemberId is > 0 ? req.AssignedMemberId : null;
            row.AssignedName = string.IsNullOrWhiteSpace(req.AssignedName) ? null : req.AssignedName.Trim();
            row.DueDate = ParseDate(req.DueDate);
            row.Status = NormalizeStatus(req.Status);
            row.ScopeType = string.IsNullOrWhiteSpace(req.ScopeType) ? null : req.ScopeType.Trim();
            row.ScopeKey = string.IsNullOrWhiteSpace(req.ScopeKey) ? null : req.ScopeKey.Trim();
            row.ModifiedDate = DateTime.UtcNow;

            if (row.Id > 0) db.Update(row);
            else row.Id = Convert.ToInt32(db.Insert(row));
            return row.Id;
        }

        public void DeleteItem(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("DELETE FROM WorkItem WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
        }

        /// <summary>Quick status toggle from the checkbox (Planerad/Klar) without opening the editor.</summary>
        public void SetItemStatus(int id, int competitionId, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE WorkItem SET Status = @0, ModifiedDate = @1 WHERE Id = @2 AND CompetitionId = @3",
                NormalizeStatus(status), DateTime.UtcNow, id, competitionId);
        }

        /// <summary>
        /// Seed the built-in prep template (områden + default uppgifter) for a competition, sized
        /// klubb/krets/sm. Only adds områden that don't already exist by name — safe to run on a
        /// competition that already has some structure.
        /// </summary>
        public int SeedTemplate(int competitionId, string? size, int byMemberId)
        {
            var template = PrepTemplates.ForSize(size);
            int added = 0;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var existingAreas = db.Fetch<WorkArea>("SELECT * FROM WorkArea WHERE CompetitionId = @0", competitionId);
            var existingNames = existingAreas.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var maxSort = existingAreas.Count == 0 ? 0 : existingAreas.Max(a => a.SortOrder);

            foreach (var (areaName, itemTitles) in template)
            {
                if (existingNames.Contains(areaName)) continue;
                var area = new WorkArea
                {
                    CompetitionId = competitionId,
                    Name = areaName,
                    SortOrder = ++maxSort,
                    CreatedByMemberId = byMemberId,
                    CreatedDate = DateTime.UtcNow,
                };
                area.Id = Convert.ToInt32(db.Insert(area));
                added++;
                int sort = 0;
                foreach (var title in itemTitles)
                {
                    db.Insert(new WorkItem
                    {
                        CompetitionId = competitionId,
                        WorkAreaId = area.Id,
                        Title = title,
                        Status = WorkItemStatus.Planerad,
                        SortOrder = ++sort,
                        CreatedByMemberId = byMemberId,
                        CreatedDate = DateTime.UtcNow,
                        ModifiedDate = DateTime.UtcNow,
                    });
                }
            }
            return added;
        }

        // ---- helpers ----

        private static WorkItemView ToView(WorkItem i, DateTime today)
        {
            bool overdue = i.DueDate.HasValue
                && i.DueDate.Value.Date < today
                && !string.Equals(i.Status, WorkItemStatus.Klar, StringComparison.OrdinalIgnoreCase);
            return new WorkItemView
            {
                Id = i.Id,
                WorkAreaId = i.WorkAreaId,
                Title = i.Title,
                Description = i.Description,
                AssignedMemberId = i.AssignedMemberId,
                AssignedName = i.AssignedName,
                DueDate = i.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                Status = i.Status,
                ScopeType = i.ScopeType,
                ScopeKey = i.ScopeKey,
                IsOverdue = overdue,
                SortOrder = i.SortOrder,
            };
        }

        private static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var dt)) return dt.Date;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt.Date;
            return null;
        }

        private static string NormalizeStatus(string? s) => s switch
        {
            WorkItemStatus.Pagar => WorkItemStatus.Pagar,
            WorkItemStatus.Blockerad => WorkItemStatus.Blockerad,
            WorkItemStatus.Klar => WorkItemStatus.Klar,
            _ => WorkItemStatus.Planerad,
        };
    }

    /// <summary>
    /// Built-in preparation templates (spec §5.1). System defaults live in code so a fresh install
    /// works with zero setup; the editable per-club/region StaffingTemplate layer is Phase 1.5.
    /// An SM template carries far more områden/uppgifter than a klubbtävling.
    /// </summary>
    internal static class PrepTemplates
    {
        public static List<(string Area, string[] Items)> ForSize(string? size)
        {
            var s = (size ?? "klubb").ToLowerInvariant();
            return s switch
            {
                "sm" => Sm(),
                "krets" => Krets(),
                _ => Klubb(),
            };
        }

        private static List<(string, string[])> Klubb() => new()
        {
            ("Mark & bana", new[] { "Boka/säkra skjutbanan", "Kontrollera skyltar och avspärrning", "Städa banan efter tävling" }),
            ("Materiel", new[] { "Räkna och beställa tavlor", "Kontrollera markeringsutrustning", "Ladda batterier / kontrollera högtalare" }),
            ("Sekretariat", new[] { "Skriv ut startlistor", "Förbered resultatinmatning", "Anslag och information" }),
            ("Kansli & inbjudan", new[] { "Publicera inbjudan", "Öppna anmälan", "Bekräfta anmälningar" }),
        };

        private static List<(string, string[])> Krets() => new()
        {
            ("Mark & bana", new[] { "Boka/säkra skjutbanan", "Bygg och kontrollera stationer", "Skyltning och avspärrning", "Städ- och återställningsplan" }),
            ("Materiel", new[] { "Beställ tavlor och figurer", "Kontrollera markeringsutrustning", "Tidtagning och ljudanläggning", "Sjukvårdsmateriel" }),
            ("Sekretariat", new[] { "Startlistor och lottning", "Resultatinmatning och rättning", "Prisbord och diplom" }),
            ("Kansli & inbjudan", new[] { "Publicera inbjudan (SPSF/krets)", "Öppna anmälan", "Sanktionera tävlingen" }),
            ("Servering", new[] { "Planera kiosk/servering", "Inköp fika", "Bemanna servering" }),
        };

        private static List<(string, string[])> Sm() => new()
        {
            ("Tävlingsledning", new[] { "Utse tävlingsledare och säkerhetschef", "Ta fram tidsplan", "Fördela ansvar per område" }),
            ("Mark & bana", new[] { "Säkra tävlingsarena flera dagar", "Bygg och besiktiga stationer/banor", "Skyltning, parkering, avspärrning", "Säkerhetsplan och genomgång", "Återställningsplan" }),
            ("Materiel", new[] { "Beställ tavlor och figurer i god tid", "Markeringsutrustning per station", "Tidtagning, ljud och kommunikation", "Sjukvård och första hjälpen", "Reservutrustning" }),
            ("Sekretariat", new[] { "Startlistor, lottning och seedning", "Resultatinmatning och live-tavla", "Prisutdelning och medaljer", "Diplom och gravyr" }),
            ("Kansli & inbjudan", new[] { "Publicera inbjudan (SPSF)", "Öppna anmälan och betalning", "Sanktionera mästerskapet", "Ackreditering funktionärer" }),
            ("Boende & logistik", new[] { "Boka boende för funktionärer", "Transporter", "Måltider" }),
            ("Servering", new[] { "Planera servering flera dagar", "Inköp", "Bemanningsschema servering" }),
            ("Marknad & media", new[] { "Program och sponsorer", "Sociala medier", "Fotograf/press" }),
        };
    }
}
