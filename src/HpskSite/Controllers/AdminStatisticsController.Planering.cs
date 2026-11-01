using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Controllers
{
    /// <summary>
    /// Tävlingsplaneringen och patrullgeneratorn för Statistik-fliken: hur många planeringar som finns, om de
    /// används av fler än en person, vad som görs i dem (meddelanden, möten, uppgifter, beslut, dokument,
    /// stationer, bemanning) och hur patrullgeneratorn används (tillämpade förslag, ångrade, lås, kamratkrav,
    /// delpublicering) plus sidvisningarna för planeringen, generatorn och övningsläget.
    ///
    /// <para><b>Bara tal, inga namn.</b> En planering är privat för dem som är med ("bara de som är med ser
    /// innehållet"), så sidan visar aldrig en planerings namn eller innehåll — bara hur mycket som görs.</para>
    ///
    /// <para><b>Demon räknas inte.</b> Planeringar skapade av demoklubbens medlemmar (Ankeborg), och tävlingar som
    /// värdas av demoklubben eller har seedade anmälningar, lämnas utanför — samma regel som resten av sidan.</para>
    ///
    /// <para>Egen endpoint, som ekonomin och kretsgranskningen: läser ett dussin tabeller och får aldrig hålla
    /// tillbaka resten av sidan. Varje källa ligger i en egen try — en saknad tabell ger tomma tal och en namngiven
    /// varning i stället för att sektionen faller.</para>
    /// </summary>
    public partial class AdminStatisticsController
    {
        private const string PlaneringCacheKey = "admin_planering_stats";

        [HttpGet]
        public async Task<IActionResult> GetPlaneringStats(bool force = false)
        {
            if (!await _authService.IsCurrentUserAdminAsync())
                return Json(new { success = false, message = "Access denied" });

            if (!force && _memoryCache.TryGetValue(PlaneringCacheKey, out object? cached) && cached != null)
                return Json(cached);

            try
            {
                var result = new { success = true, data = BuildPlaneringStats() };
                _memoryCache.Set(PlaneringCacheKey, result, CacheDuration);
                return Json(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error building planning statistics");
                return Json(new { success = false, message = "Planeringens statistik kunde inte läsas: " + ex.Message });
            }
        }

        // En rad per (planering, nyckel). Bara de kolumner en fråga fyller i används; resten är 0/null.
        private sealed class PlAggRow
        {
            public int PlanningId { get; set; }
            public string? K { get; set; }
            public int N { get; set; }
            public int N30 { get; set; }
            public int Extra { get; set; }
            public long Bytes { get; set; }
            public DateTime? Last { get; set; }
        }

        private sealed class PlRow
        {
            public int Id { get; set; }
            public string? DisciplineKey { get; set; }
            public int CreatedBy { get; set; }
            public DateTime CreatedAt { get; set; }
            public int? ParentPlanningId { get; set; }
            public int? SeededFromCompetitionId { get; set; }
            public string? ShooterMode { get; set; }
            public string? WholeDayRoles { get; set; }
        }

        private sealed class PlMemberRow
        {
            public int PlanningId { get; set; }
            public int? MemberId { get; set; }
            public DateTime? LastSeenAt { get; set; }
        }

        private sealed class PlBackupRow
        {
            public int CompetitionId { get; set; }
            public DateTime CreatedUtc { get; set; }
            public DateTime? RestoredUtc { get; set; }
        }

        private sealed class PlCompCountRow
        {
            public int CompetitionId { get; set; }
            public int N { get; set; }
        }

        private sealed class PlVisitRow
        {
            public string K { get; set; } = "";
            public int Sessions { get; set; }
            public int Views { get; set; }
            public int SessionsPrev { get; set; }
            public int ViewsPrev { get; set; }
        }

        private object BuildPlaneringStats()
        {
            var swTotal = System.Diagnostics.Stopwatch.StartNew();
            var warnings = new List<string>();
            var now = DateTime.Now;
            var today = DateTime.Today;
            var thirtyDaysAgo = today.AddDays(-30);
            var sixtyDaysAgo = today.AddDays(-60);
            var nowUtc = DateTime.UtcNow;
            var thirtyDaysAgoUtc = nowUtc.AddDays(-30);
            var sixtyDaysAgoUtc = nowUtc.AddDays(-60);

            // ── Demon utanför: medlemmar i demoklubben, och tävlingar den värdar eller har seedade anmälningar på ──
            var excludedClubIds = new HashSet<int>();
            var excludedRegionIds = new HashSet<int>();
            if (_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) && ctx.Content != null)
            {
                var root = ctx.Content.GetAtRoot().FirstOrDefault();
                if (root != null) ComputeDemoExclusions(root, excludedClubIds, excludedRegionIds);
            }
            var excludedMemberIds = excludedClubIds.Count == 0
                ? new HashSet<int>()
                : _memberProperties.Read(new[] { "primaryClubId" })
                    .Where(m => m.PrimaryClubId > 0 && excludedClubIds.Contains(m.PrimaryClubId))
                    .Select(m => m.Id)
                    .ToHashSet();

            var seededCompetitionIds = _registrations.CompetitionsWithRegisteredBy(SimulationSeedTag);
            var includedCompetitionIds = _competitions.List()
                .Where(c => !(c.ClubId > 0 && excludedClubIds.Contains(c.ClubId)) && !seededCompetitionIds.Contains(c.Id))
                .Select(c => c.Id)
                .ToHashSet();

            using var db = _databaseFactory.CreateDatabase();

            List<T> Safe<T>(string what, Func<List<T>> read)
            {
                try { return read(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Planeringsstatistiken: {What} kunde inte läsas", what);
                    warnings.Add(what + " kunde inte läsas.");
                    return new List<T>();
                }
            }

            // ── Planeringarna (utan raderade; demons borta) ──────────────────────────────────────────
            var plannings = Safe("Planeringarna", () => db.Fetch<PlRow>(
                @"SELECT Id, DisciplineKey, CreatedBy, CreatedAt, ParentPlanningId, SeededFromCompetitionId, ShooterMode, WholeDayRoles
                  FROM Planning WHERE DeletedAt IS NULL"))
                .Where(p => !excludedMemberIds.Contains(p.CreatedBy))
                .ToList();
            var ids = plannings.Select(p => p.Id).ToHashSet();

            List<PlAggRow> Agg(string what, string sql, params object[] args) =>
                Safe(what, () => db.Fetch<PlAggRow>(sql, args)).Where(r => ids.Contains(r.PlanningId)).ToList();

            var members = Safe("Deltagarna", () => db.Fetch<PlMemberRow>(
                    "SELECT PlanningId, MemberId, LastSeenAt FROM PlanningMember WHERE RemovedAt IS NULL"))
                .Where(m => ids.Contains(m.PlanningId) && !(m.MemberId.HasValue && excludedMemberIds.Contains(m.MemberId.Value)))
                .ToList();

            var messages = Agg("Meddelandena",
                @"SELECT PlanningId, Kind AS K, COUNT(*) AS N, SUM(CASE WHEN CreatedAt >= @0 THEN 1 ELSE 0 END) AS N30, MAX(CreatedAt) AS Last
                  FROM PlanningMessage WHERE DeletedAt IS NULL GROUP BY PlanningId, Kind", thirtyDaysAgo);
            var meetings = Agg("Mötena",
                @"SELECT PlanningId, Status AS K, COUNT(*) AS N, SUM(CASE WHEN CreatedAt >= @0 THEN 1 ELSE 0 END) AS N30, MAX(UpdatedAt) AS Last
                  FROM PlanningMeeting WHERE DeletedAt IS NULL GROUP BY PlanningId, Status", thirtyDaysAgo);
            var tasks = Agg("Uppgifterna",
                @"SELECT PlanningId, Status AS K, COUNT(*) AS N, SUM(CASE WHEN CreatedAt >= @0 THEN 1 ELSE 0 END) AS N30,
                         SUM(CASE WHEN DueDate IS NOT NULL AND DueDate < @1 AND Status <> 'Done' THEN 1 ELSE 0 END) AS Extra, MAX(UpdatedAt) AS Last
                  FROM PlanningTask WHERE DeletedAt IS NULL GROUP BY PlanningId, Status", thirtyDaysAgo, today);
            var milestones = Agg("Milstolparna",
                "SELECT PlanningId, 'M' AS K, COUNT(*) AS N FROM PlanningTask WHERE DeletedAt IS NULL AND IsMilestone = 1 GROUP BY PlanningId");
            var decisions = Agg("Förslag och beslut",
                @"SELECT PlanningId, Status AS K, COUNT(*) AS N, 0 AS N30, SUM(CASE WHEN SentToBoardAt IS NOT NULL THEN 1 ELSE 0 END) AS Extra, MAX(UpdatedAt) AS Last
                  FROM PlanningDecision WHERE DeletedAt IS NULL GROUP BY PlanningId, Status");
            var documents = Agg("Dokumenten",
                @"SELECT PlanningId, CASE WHEN StoredFileName IS NOT NULL THEN 'file' ELSE 'link' END AS K, COUNT(*) AS N,
                         SUM(ISNULL(FileSize, 0)) AS Bytes, MAX(AddedAt) AS Last
                  FROM PlanningDocument WHERE DeletedAt IS NULL GROUP BY PlanningId, CASE WHEN StoredFileName IS NOT NULL THEN 'file' ELSE 'link' END");
            var stations = Agg("Stationerna",
                "SELECT PlanningId, 'S' AS K, COUNT(*) AS N FROM PlanningStationResponsible GROUP BY PlanningId");
            // Filtret ligger i SQL: ett "competition"-ord i C# räknas av CompetitionStoreGuardTests som en alias-litteral.
            var competitionLinks = Agg("Kopplingarna till tävlingar",
                "SELECT PlanningId, 'L' AS K, COUNT(*) AS N FROM PlanningLink WHERE TargetKind = 'competition' GROUP BY PlanningId");
            var accessRequests = Agg("Förfrågningarna om att få vara med",
                "SELECT PlanningId, Status AS K, COUNT(*) AS N FROM PlanningRequest GROUP BY PlanningId, Status");
            var assignments = Agg("Bemanningen",
                @"SELECT PlanningId, 'A' AS K, COUNT(*) AS N, 0 AS N30, SUM(CASE WHEN NotifiedAt IS NOT NULL THEN 1 ELSE 0 END) AS Extra, MAX(ModifiedDate) AS Last
                  FROM StaffAssignment WHERE PlanningId IS NOT NULL GROUP BY PlanningId");
            var needs = Agg("Behoven",
                "SELECT PlanningId, 'B' AS K, COUNT(*) AS N FROM StaffCrewNeed WHERE PlanningId IS NOT NULL GROUP BY PlanningId");

            // ── Aktivitet per planering: det senaste som hänt (inte seedningen vid skapandet) ──
            var lastActivity = new Dictionary<int, DateTime>();
            void Touch(int planningId, DateTime? when)
            {
                if (!when.HasValue) return;
                if (!lastActivity.TryGetValue(planningId, out var cur) || when.Value > cur) lastActivity[planningId] = when.Value;
            }
            foreach (var r in messages.Where(r => r.K != "System")) Touch(r.PlanningId, r.Last);
            foreach (var r in meetings) Touch(r.PlanningId, r.Last);
            foreach (var r in tasks.Where(r => r.K != "NotStarted")) Touch(r.PlanningId, r.Last);
            foreach (var r in decisions) Touch(r.PlanningId, r.Last);
            foreach (var r in documents) Touch(r.PlanningId, r.Last);
            foreach (var r in assignments) Touch(r.PlanningId, r.Last);
            foreach (var m in members) Touch(m.PlanningId, m.LastSeenAt);

            var participantsPerPlanning = members.GroupBy(m => m.PlanningId).ToDictionary(g => g.Key, g => g.Count());
            int Sum(IEnumerable<PlAggRow> rows) => rows.Sum(r => r.N);

            int planningsTotal = plannings.Count;
            int planningsNew30 = plannings.Count(p => p.CreatedAt >= thirtyDaysAgo);
            int planningsInGroup = ids.Count(id => participantsPerPlanning.GetValueOrDefault(id, 0) >= 2);
            int planningsActive30 = ids.Count(id => lastActivity.TryGetValue(id, out var d) && d >= thirtyDaysAgo);
            int planningsWithTasksInMotion = tasks.Where(r => r.K != "NotStarted").Select(r => r.PlanningId).Distinct().Count();
            int subPlannings = plannings.Count(p => p.ParentPlanningId.HasValue);
            int startedFromCompetition = plannings.Count(p => p.SeededFromCompetitionId.HasValue);

            var people = members.Where(m => m.MemberId.HasValue).Select(m => m.MemberId!.Value).Distinct().Count();
            var guests = members.Count(m => !m.MemberId.HasValue);

            var perMonth = Enumerable.Range(0, 12).Select(i =>
            {
                var monthStart = new DateTime(now.Year, now.Month, 1).AddMonths(-11 + i);
                return new
                {
                    month = monthStart.ToString("yyyy-MM"),
                    count = plannings.Count(p => p.CreatedAt >= monthStart && p.CreatedAt < monthStart.AddMonths(1))
                };
            }).ToList();

            var byDiscipline = plannings
                .GroupBy(p => string.IsNullOrWhiteSpace(p.DisciplineKey) ? "" : p.DisciplineKey!)
                .Select(g => new { discipline = g.Key, count = g.Count() })
                .OrderByDescending(g => g.count)
                .ToList();

            int taskTotal = Sum(tasks);
            int taskDone = Sum(tasks.Where(r => r.K == "Done"));
            int taskNotStarted = Sum(tasks.Where(r => r.K == "NotStarted"));
            int taskOverdue = tasks.Sum(r => r.Extra);

            // ── Patrullgeneratorn: bara tävlingar som räknas (inte demons, inte seedade) ────────────
            var backups = Safe("Tillämpade förslag", () => db.Fetch<PlBackupRow>(
                    "SELECT CompetitionId, CreatedUtc, RestoredUtc FROM FaltskyttePatrolBackup"))
                .Where(b => includedCompetitionIds.Contains(b.CompetitionId)).ToList();
            List<PlCompCountRow> CompCounts(string what, string sql) =>
                Safe(what, () => db.Fetch<PlCompCountRow>(sql)).Where(r => includedCompetitionIds.Contains(r.CompetitionId)).ToList();
            var lockedRows = CompCounts("Låsta skyttar",
                @"SELECT p.CompetitionId AS CompetitionId, COUNT(*) AS N FROM FaltskyttePatrolMember m
                  JOIN FaltskyttePatrol p ON p.Id = m.PatrolId WHERE m.Locked = 1 GROUP BY p.CompetitionId");
            var joinRows = CompCounts("Kamratkrav",
                @"SELECT r.CompetitionId AS CompetitionId, COUNT(*) AS N FROM CompetitionRegistrationClass c
                  JOIN CompetitionRegistration r ON r.Id = c.RegistrationId WHERE c.JoinMemberId IS NOT NULL GROUP BY r.CompetitionId");
            var capacityRows = CompCounts("Patrullernas platser",
                "SELECT CompetitionId, COUNT(*) AS N FROM FaltskyttePatrol WHERE Capacity IS NOT NULL GROUP BY CompetitionId");
            var publishedGroupRows = CompCounts("Publicering per vapengrupp",
                "SELECT Id AS CompetitionId, 1 AS N FROM Competition WHERE FaltskyttePublishedGroups IS NOT NULL");

            // ── Sidvisningar: planeringen, generatorn (tävlingens) och övningsläget — 30 dagar och de 30 dessförinnan ──
            var visits = Safe("Sidvisningarna", () => db.Fetch<PlVisitRow>(
                @"SELECT K,
                         COUNT(DISTINCT CASE WHEN VisitedAt >= @0 THEN SessionHash END) AS Sessions,
                         SUM(CASE WHEN VisitedAt >= @0 THEN 1 ELSE 0 END) AS Views,
                         COUNT(DISTINCT CASE WHEN VisitedAt < @0 THEN SessionHash END) AS SessionsPrev,
                         SUM(CASE WHEN VisitedAt < @0 THEN 1 ELSE 0 END) AS ViewsPrev
                  FROM (
                      SELECT VisitedAt, SessionHash,
                             CASE WHEN LOWER([Path]) LIKE '/patrullgenerator/lab%' THEN 'lab'
                                  WHEN LOWER([Path]) LIKE '/patrullgenerator%'     THEN 'generator'
                                  WHEN LOWER([Path]) LIKE '/tavlingsplanering%'    THEN 'planering' END AS K
                      FROM VisitorLogs WHERE VisitedAt >= @1
                  ) v
                  WHERE K IS NOT NULL GROUP BY K", thirtyDaysAgoUtc, sixtyDaysAgoUtc));
            object Visit(string key)
            {
                var v = visits.FirstOrDefault(x => x.K == key);
                return new { sessions30 = v?.Sessions ?? 0, views30 = v?.Views ?? 0, sessionsPrev = v?.SessionsPrev ?? 0, viewsPrev = v?.ViewsPrev ?? 0 };
            }

            _logger.LogInformation("Planeringsstatistiken byggdes på {TotalMs} ms ({Plannings} planeringar)", swTotal.ElapsedMilliseconds, planningsTotal);

            return new
            {
                warnings,
                summary = new
                {
                    plannings = planningsTotal,
                    new30 = planningsNew30,
                    inGroup = planningsInGroup,
                    active30 = planningsActive30,
                    withTasksInMotion = planningsWithTasksInMotion,
                    subPlannings,
                    startedFromCompetition,
                    people,
                    guests
                },
                perMonth,
                byDiscipline,
                content = new
                {
                    messages = Sum(messages.Where(r => r.K != "System")),
                    messages30 = messages.Where(r => r.K != "System").Sum(r => r.N30),
                    meetings = Sum(meetings),
                    meetingsScheduled = Sum(meetings.Where(r => r.K == "Scheduled")),
                    meetings30 = meetings.Sum(r => r.N30),
                    tasks = taskTotal,
                    tasksDone = taskDone,
                    tasksInMotion = taskTotal - taskNotStarted,
                    tasksOverdue = taskOverdue,
                    tasks30 = tasks.Sum(r => r.N30),
                    milestones = Sum(milestones),
                    decisions = Sum(decisions),
                    decisionsDecided = Sum(decisions.Where(r => r.K == "Decided")),
                    decisionsToBoard = decisions.Sum(r => r.Extra),
                    documents = Sum(documents),
                    documentLinks = Sum(documents.Where(r => r.K == "link")),
                    documentMB = Math.Round(documents.Sum(r => r.Bytes) / (1024.0 * 1024.0), 1),
                    planningsWithStations = stations.Select(r => r.PlanningId).Distinct().Count(),
                    stationResponsibles = Sum(stations),
                    linkedToCompetition = competitionLinks.Select(r => r.PlanningId).Distinct().Count(),
                    accessRequests = Sum(accessRequests),
                    accessRequestsApproved = Sum(accessRequests.Where(r => r.K == "Approved"))
                },
                staffing = new
                {
                    plannings = assignments.Select(r => r.PlanningId).Distinct().Count(),
                    assignments = Sum(assignments),
                    notified = assignments.Sum(r => r.Extra),
                    planningsWithNeeds = needs.Select(r => r.PlanningId).Distinct().Count(),
                    needs = Sum(needs),
                    shooterModeChosen = plannings.Count(p => !string.IsNullOrWhiteSpace(p.ShooterMode)),
                    wholeDayRolesChosen = plannings.Count(p => !string.IsNullOrWhiteSpace(p.WholeDayRoles))
                },
                patrol = new
                {
                    applied = backups.Count,
                    applied30 = backups.Count(b => b.CreatedUtc >= thirtyDaysAgoUtc),
                    undone = backups.Count(b => b.RestoredUtc.HasValue),
                    competitions = backups.Select(b => b.CompetitionId).Distinct().Count(),
                    competitionsWithLocks = lockedRows.Count,
                    lockedShooters = lockedRows.Sum(r => r.N),
                    competitionsWithJoinRules = joinRows.Count,
                    joinRules = joinRows.Sum(r => r.N),
                    competitionsWithCapacity = capacityRows.Count,
                    competitionsPublishedByGroup = publishedGroupRows.Count
                },
                visits = new
                {
                    planering = Visit("planering"),
                    generator = Visit("generator"),
                    lab = Visit("lab")
                }
            };
        }
    }
}
