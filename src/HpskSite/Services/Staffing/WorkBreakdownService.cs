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
            var links = db.Fetch<WorkLink>(
                "SELECT * FROM WorkLink WHERE CompetitionId = @0 ORDER BY Id", competitionId);

            var today = DateTime.UtcNow.Date;
            var itemsByArea = items.GroupBy(i => i.WorkAreaId).ToDictionary(g => g.Key, g => g.ToList());
            var linksByItem = links.Where(l => l.WorkItemId.HasValue).GroupBy(l => l.WorkItemId!.Value).ToDictionary(g => g.Key, g => g.ToList());
            var linksByArea = links.Where(l => l.WorkAreaId.HasValue && !l.WorkItemId.HasValue).GroupBy(l => l.WorkAreaId!.Value).ToDictionary(g => g.Key, g => g.ToList());

            var resp = new WorkBreakdownResponse { CanEdit = canEdit };
            // Competition-level documents (no område, no uppgift) — where the big docs live.
            resp.CompLinks = links.Where(l => !l.WorkAreaId.HasValue && !l.WorkItemId.HasValue).Select(ToLinkView).ToList();

            foreach (var a in areas)
            {
                itemsByArea.TryGetValue(a.Id, out var areaItems);
                areaItems ??= new List<WorkItem>();
                var views = areaItems.Select(i =>
                {
                    var v = ToView(i, today);
                    if (linksByItem.TryGetValue(i.Id, out var il)) v.Links = il.Select(ToLinkView).ToList();
                    return v;
                }).ToList();
                resp.Areas.Add(new WorkAreaView
                {
                    Id = a.Id,
                    Name = a.Name,
                    ResponsibleMemberId = a.ResponsibleMemberId,
                    ResponsibleName = a.ResponsibleName,
                    SortOrder = a.SortOrder,
                    Items = views,
                    Links = linksByArea.TryGetValue(a.Id, out var al) ? al.Select(ToLinkView).ToList() : new(),
                    TotalCount = views.Count,
                    DoneCount = views.Count(v => string.Equals(v.Status, WorkItemStatus.Klar, StringComparison.OrdinalIgnoreCase)),
                    OverdueCount = views.Count(v => v.IsOverdue),
                });
            }
            return resp;
        }

        // ---- WorkLink (documents & links) ----

        public List<WorkLink> GetLinks(int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<WorkLink>("SELECT * FROM WorkLink WHERE CompetitionId = @0 ORDER BY Id", competitionId);
        }

        public WorkLink? GetLink(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefault<WorkLink>("SELECT * FROM WorkLink WHERE Id = @0", id);
        }

        public int SaveLink(WorkLink link)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            if (link.CreatedDate == default) link.CreatedDate = DateTime.UtcNow;
            return Convert.ToInt32(scope.Database.Insert(link));
        }

        /// <summary>Delete a link row and return its stored file name (if any) so the caller can remove the file.</summary>
        public string? DeleteLink(int id, int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.SingleOrDefault<WorkLink>("SELECT * FROM WorkLink WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
            if (row == null) return null;
            db.Execute("DELETE FROM WorkLink WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
            return row.StoredFileName;
        }

        private static WorkLinkView ToLinkView(WorkLink l)
        {
            var isFile = !string.IsNullOrEmpty(l.StoredFileName);
            return new WorkLinkView
            {
                Id = l.Id,
                WorkAreaId = l.WorkAreaId,
                WorkItemId = l.WorkItemId,
                Title = l.Title,
                // For a stored file, hand the client the authorized download route; for a link, the raw URL.
                Url = isFile ? $"/umbraco/surface/Staffing/DownloadWorkDocument?id={l.Id}" : l.Url,
                IsFile = isFile,
            };
        }

        /// <summary>
        /// Auto-seed one "Bygg station N" uppgift per configured station (Fältskytte), under a
        /// "Stationsbygge" område, each scoped Station:N (→ Fältkonfigurator). Idempotent: skips a
        /// station that already has a Station:N-scoped task. Returns how many were created.
        /// </summary>
        public int SeedStationTasks(int competitionId, int stationCount, int byMemberId)
        {
            if (stationCount <= 0) return 0;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;

            var area = db.SingleOrDefault<WorkArea>(
                "SELECT * FROM WorkArea WHERE CompetitionId = @0 AND Name = @1", competitionId, "Stationsbygge");
            if (area == null)
            {
                var maxSort = db.ExecuteScalar<int?>("SELECT MAX(SortOrder) FROM WorkArea WHERE CompetitionId = @0", competitionId) ?? 0;
                area = new WorkArea { CompetitionId = competitionId, Name = "Stationsbygge", SortOrder = maxSort + 1, CreatedByMemberId = byMemberId, CreatedDate = DateTime.UtcNow };
                area.Id = Convert.ToInt32(db.Insert(area));
            }

            var existing = db.Fetch<WorkItem>(
                "SELECT * FROM WorkItem WHERE CompetitionId = @0 AND ScopeType = @1", competitionId, "Station");
            var haveStations = existing.Where(i => int.TryParse(i.ScopeKey, out _)).Select(i => i.ScopeKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

            int added = 0, sort = existing.Count;
            for (int n = 1; n <= stationCount; n++)
            {
                if (haveStations.Contains(n.ToString())) continue;
                db.Insert(new WorkItem
                {
                    CompetitionId = competitionId,
                    WorkAreaId = area.Id,
                    Title = $"Bygg station {n}",
                    Status = WorkItemStatus.Planerad,
                    ScopeType = "Station",
                    ScopeKey = n.ToString(),
                    SortOrder = ++sort,
                    CreatedByMemberId = byMemberId,
                    CreatedDate = DateTime.UtcNow,
                    ModifiedDate = DateTime.UtcNow,
                });
                added++;
            }
            return added;
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
        /// <summary>
        /// Seed the built-in prep template (områden + default uppgifter) for a competition, tailored by
        /// discipline (Fält / Springskytte / Precision) and sized klubb/krets/sm. When a competition date
        /// is known, each uppgift's DueDate is anchored relative to it (e.g. "6 v. före"). Only adds områden
        /// that don't already exist by name — safe to run on a competition that already has some structure.
        /// </summary>
        public int SeedTemplate(int competitionId, string? size, string? discipline, DateTime? compDate, int byMemberId)
        {
            var template = PrepTemplates.For(discipline, size);
            int added = 0;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var existingAreas = db.Fetch<WorkArea>("SELECT * FROM WorkArea WHERE CompetitionId = @0", competitionId);
            var existingNames = existingAreas.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var maxSort = existingAreas.Count == 0 ? 0 : existingAreas.Max(a => a.SortOrder);

            foreach (var areaDef in template)
            {
                if (existingNames.Contains(areaDef.Area)) continue;
                var area = new WorkArea
                {
                    CompetitionId = competitionId,
                    Name = areaDef.Area,
                    SortOrder = ++maxSort,
                    CreatedByMemberId = byMemberId,
                    CreatedDate = DateTime.UtcNow,
                };
                area.Id = Convert.ToInt32(db.Insert(area));
                added++;
                int sort = 0;
                foreach (var item in areaDef.Items)
                {
                    DateTime? due = (compDate.HasValue && item.DaysBeforeComp.HasValue)
                        ? compDate.Value.Date.AddDays(-item.DaysBeforeComp.Value)
                        : (DateTime?)null;
                    db.Insert(new WorkItem
                    {
                        CompetitionId = competitionId,
                        WorkAreaId = area.Id,
                        Title = item.Title,
                        DueDate = due,
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

    internal record PrepItem(string Title, int? DaysBeforeComp);
    internal record PrepArea(string Area, List<PrepItem> Items);

    /// <summary>
    /// Built-in preparation templates (spec §5.1), keyed by DISCIPLINE (Fält / Springskytte / Precision)
    /// and sized klubb/krets/sm. System defaults live in code so a fresh install works with zero setup;
    /// the editable per-club/region StaffingTemplate layer is Phase 1.5. Each uppgift carries an optional
    /// DaysBeforeComp so SeedTemplate can anchor deadlines to the competition date. An SM template carries
    /// far more områden/uppgifter than a klubbtävling.
    ///
    /// NOTE: the Springskytte + Fältskytte content is a first pass — sanity-check the crew-specific tasks
    /// with a real arrangör (same caveat as the FunctionaryRoles first-pass role sets).
    /// </summary>
    internal static class PrepTemplates
    {
        public static List<PrepArea> For(string? discipline, string? size)
        {
            var s = (size ?? "klubb").ToLowerInvariant();
            var big = s == "sm";
            var mid = s == "krets" || big;
            var d = (discipline ?? "").ToLowerInvariant();

            if (d is "faltskytte" or "magnumfalt") return Falt(mid, big);
            if (d == "springskytte") return Spring(mid, big);
            return Precision(mid, big);
        }

        private static PrepItem I(string title, int? days = null) => new(title, days);

        // --- Common områden shared by all disciplines ---
        private static PrepArea Admin(bool big) => new("Sanktion & administration", new()
        {
            I("Sök sanktion (SPSF/krets)", big ? 120 : 60),
            I("Boka domare / jury", big ? 90 : 45),
            I("Försäkring och tillstånd", big ? 90 : 45),
            I("Fastställ tidsplan och ansvarsfördelning", big ? 75 : 30),
        });
        private static PrepArea Kansli(bool big) => new("Kansli & inbjudan", new()
        {
            I("Publicera inbjudan", big ? 75 : 35),
            I("Öppna anmälan", big ? 60 : 30),
            I("Bekräfta anmälningar / seedning", big ? 14 : 7),
            I("Publicera startlista", 3),
        });
        private static PrepArea Sekretariat(bool big) => new("Sekretariat", new()
        {
            I("Startlistor och lottning", 3),
            I("Förbered resultatinmatning / live-tavla", 2),
            I("Priser, medaljer och diplom", big ? 30 : 10),
            I("Standardmedaljer / märken att dela ut", big ? 30 : 14),
        });
        private static PrepArea Servering(bool big) => new("Servering", new()
        {
            I("Planera kiosk/servering", big ? 30 : 10),
            I("Inköp", big ? 7 : 3),
            I("Bemanna servering", big ? 14 : 5),
        });
        private static PrepArea Logistik() => new("Boende & logistik", new()
        {
            I("Boka boende för funktionärer", 45),
            I("Transporter", 30),
            I("Måltider funktionärer", 21),
        });

        private static List<PrepArea> Precision(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Mark & bana", new()
            {
                I("Boka/säkra skjutbanan", big ? 90 : 45),
                I("Kontrollera antal eldplatser och kulfång", big ? 30 : 14),
                I("Ljudanläggning / skjutledarkommandon", 7),
                I("Skyltning och avspärrning", 3),
                I("Städ- och återställningsplan", 1),
            }));
            list.Add(new("Materiel", new()
            {
                I("Räkna och beställ tavlor (deltagare × serier)", big ? 45 : 21),
                I("Klister/tejp och markeringsutrustning", 7),
                I("Ladda batterier / kontrollera högtalare", 2),
                I("Första hjälpen", 7),
            }));
            list.Add(Sekretariat(big));
            list.Add(Kansli(big));
            if (mid) list.Add(Servering(big));
            if (big) { list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
            return list;
        }

        private static List<PrepArea> Spring(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Bana", new()
            {
                I("Märk ut varv-slingan", big ? 30 : 14),
                I("Bygg straffrunde-slingan (~60 m)", big ? 30 : 14),
                I("Sprintfigurer och skjutplatser", 7),
                I("Start/mål + tidtagningsgrindar", 3),
                I("Skyltning, avspärrning och gångstråk", 2),
            }));
            list.Add(new("Sjukvård", new()
            {
                I("Sjukvårdsansvarig och första hjälpen", big ? 30 : 14),
                I("Hjärtstartare på plats", big ? 21 : 10),
                I("Vätskestationer", 3),
            }));
            list.Add(new("Materiel", new()
            {
                I("Tidtagningsutrustning (löpande klocka)", big ? 30 : 14),
                I("Varvräknar-blad och nummervästar", 7),
                I("Straffrunde-markörer och koner", 3),
                I("Reservutrustning för tidtagning", 7),
            }));
            list.Add(new("Sekretariat", new()
            {
                I("Startlistor (individuellt + mass-start/stafett)", 3),
                I("Ålders- och könsklasser", 5),
                I("Tid → resultat och live-tavla", 2),
                I("Priser och medaljer", big ? 30 : 10),
            }));
            list.Add(Kansli(big));
            if (mid) list.Add(Servering(big));
            if (big) { list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
            return list;
        }

        private static List<PrepArea> Falt(bool mid, bool big)
        {
            var list = new List<PrepArea> { Admin(big) };
            list.Add(new("Bankonfiguration", new()
            {
                I("Färdigställ stationskonfiguration (Fältkonfigurator)", big ? 60 : 30),
                I("Begär Banläggare-godkännande av konfigurationen", big ? 45 : 21),
                I("Fastställ patrullflöde och utsläppsintervall", 14),
                I("Mörkerbelysning (om mörkerfältskjutning)", 14),
            }));
            list.Add(new("Stationsbygge", new()
            {
                I("Placera ut figurer på rätt avstånd per station", 3),
                I("Skärmar, säkerhetsvinklar och kulfång per station", 3),
                I("Gångstråk och skyltning mellan stationer", 2),
                I("Besiktiga banan före tävling", 1),
            }));
            list.Add(new("Materiel", new()
            {
                I("Beställ figurer per station (Figurkatalogen)", big ? 45 : 21),
                I("Markeringsmateriel per station", 7),
                I("Tidur och QR-kort per station", 3),
                I("Sjukvård och första hjälpen", 7),
            }));
            list.Add(Sekretariat(big));
            list.Add(Kansli(big));
            if (mid) list.Add(Servering(big));
            if (big) { list.Insert(0, TavlingsledningArea(big)); list.Add(Logistik()); list.Add(MarknadArea()); }
            return list;
        }

        private static PrepArea TavlingsledningArea(bool big) => new("Tävlingsledning", new()
        {
            I("Utse tävlingsledare och säkerhetschef", big ? 120 : 60),
            I("Ta fram tidsplan och säkerhetsplan", big ? 90 : 45),
            I("Fördela ansvar per område", big ? 75 : 30),
            I("Ackreditering funktionärer", big ? 21 : 10),
        });
        private static PrepArea MarknadArea() => new("Marknad & media", new()
        {
            I("Program och sponsorer", 60),
            I("Sociala medier", 30),
            I("Fotograf / press", 14),
        });
    }
}
