using System.Globalization;
using HpskSite.Models.Staffing;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Staffing
{
    /// <summary>
    /// Data + orchestration for the day-of functionary roster (Bemanning). Competition-scoped;
    /// scope/shift grouping is done in memory (per-competition volume is tiny). Authorization is the
    /// controller's job. Also owns the Tävlingsledning ↔ competitionManagers mirror: a Tävlingsledning
    /// row with HasAdminAccess=true grants app admin by writing the competition's competitionManagers
    /// int[] (the permission source of truth — no auth rework). See COMPETITION_STAFFING_SYSTEM.md §4.
    /// </summary>
    public class StaffingService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly ILogger<StaffingService> _logger;

        private const string TavlingsledningRole = "tavlingsledning";

        public StaffingService(IScopeProvider scopeProvider, IContentService contentService, IMemberService memberService, ILogger<StaffingService> logger)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _memberService = memberService;
            _logger = logger;
        }

        // ---- reads ----

        public List<StaffAssignment> GetForCompetition(int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE CompetitionId = @0 ORDER BY RoleKey, ScopeKey, StartsAt, Id",
                competitionId);
        }

        public StaffAssignment? GetById(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefault<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE Id = @0", id);
        }

        public int? GetCompetitionIdFor(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int?>(
                "SELECT CompetitionId FROM StaffAssignment WHERE Id = @0", id);
        }

        /// <summary>
        /// Build the grouped roster for a competition, one group per role valid for its discipline
        /// (empty groups included so the admin sees which crew a comp normally needs). Runs the
        /// one-time competitionManagers reconcile first so managers added the old way appear here.
        /// </summary>
        public StaffRosterResponse BuildRoster(int competitionId, string? discipline, bool canEdit)
        {
            ReconcileManagersIntoRoster(competitionId);

            var rows = GetForCompetition(competitionId);
            var resp = new StaffRosterResponse { Discipline = discipline ?? "", CanEdit = canEdit };

            var roles = FunctionaryRoles.ForDiscipline(discipline);
            var byRole = rows.GroupBy(r => r.RoleKey ?? "").ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var role in roles)
            {
                var group = new StaffRoleGroup
                {
                    RoleKey = role.Key,
                    RoleName = role.DisplayName,
                    RolePlural = role.Plural,
                    DefaultScopeType = role.DefaultScopeType,
                    SupportsTargetRange = role.SupportsTargetRange,
                    SupportsFunctionTitle = role.SupportsFunctionTitle,
                    Description = role.Description,
                    Needs = role.Needs,
                };
                if (byRole.TryGetValue(role.Key, out var assignments))
                {
                    group.Assignments = assignments
                        .OrderBy(a => a.ScopeKey, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(a => a.StartsAt ?? DateTime.MinValue)
                        .ThenByDescending(a => a.IsResponsible)
                        .Select(ToView)
                        .ToList();
                }
                resp.Groups.Add(group);
            }

            // Any rows whose role isn't in this discipline's catalog (legacy / discipline changed) — surface
            // them under a catch-all group so they're never silently lost.
            var knownKeys = roles.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var orphans = rows.Where(r => !knownKeys.Contains(r.RoleKey ?? "")).ToList();
            if (orphans.Count > 0)
            {
                resp.Groups.Add(new StaffRoleGroup
                {
                    RoleKey = "_ovriga",
                    RoleName = "Övriga roller",
                    RolePlural = "Övriga roller",
                    Assignments = orphans.Select(ToView).ToList(),
                });
            }

            resp.TotalAssigned = rows.Count;
            return resp;
        }

        // ---- writes ----

        public int Save(SaveStaffAssignmentRequest req, int assignedByMemberId)
        {
            var now = DateTime.UtcNow;
            int savedId;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                StaffAssignment row;
                if (req.Id > 0)
                {
                    row = db.SingleOrDefault<StaffAssignment>("SELECT * FROM StaffAssignment WHERE Id = @0", req.Id)
                          ?? new StaffAssignment { CompetitionId = req.CompetitionId, CreatedDate = now, AssignedByMemberId = assignedByMemberId };
                }
                else
                {
                    row = new StaffAssignment { CompetitionId = req.CompetitionId, CreatedDate = now, AssignedByMemberId = assignedByMemberId };
                }

                row.CompetitionId = req.CompetitionId;
                row.MemberId = req.MemberId is > 0 ? req.MemberId : null;
                row.DisplayName = (req.DisplayName ?? "").Trim();
                row.Phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();
                row.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
                row.RoleKey = (req.RoleKey ?? "").Trim();
                row.FunctionTitle = string.IsNullOrWhiteSpace(req.FunctionTitle) ? null : req.FunctionTitle.Trim();
                row.ScopeType = string.IsNullOrWhiteSpace(req.ScopeType) ? null : req.ScopeType.Trim();
                row.ScopeKey = string.Equals(row.ScopeType, StaffScopeType.All, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : (string.IsNullOrWhiteSpace(req.ScopeKey) ? null : req.ScopeKey.Trim());
                row.TargetFrom = req.TargetFrom is > 0 ? req.TargetFrom : null;
                row.TargetTo = req.TargetTo is > 0 ? req.TargetTo : null;
                row.StartsAt = ParseDateTime(req.StartsAt);
                row.EndsAt = ParseDateTime(req.EndsAt);
                row.IsResponsible = req.IsResponsible;
                row.HasAdminAccess = req.HasAdminAccess && string.Equals(row.RoleKey, TavlingsledningRole, StringComparison.OrdinalIgnoreCase);
                row.Status = NormalizeStatus(req.Status);
                row.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
                row.ModifiedDate = now;

                if (row.Id > 0) db.Update(row);
                else row.Id = Convert.ToInt32(db.Insert(row));
                savedId = row.Id;
            }

            // Mirror to competitionManagers AFTER the write scope has committed, so the ContentService
            // Save+Publish runs in its own transaction (no publish nested inside the raw-SQL scope).
            SyncCompetitionManagers(req.CompetitionId);
            return savedId;
        }

        public void Delete(int id, int competitionId)
        {
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                scope.Database.Execute("DELETE FROM StaffAssignment WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
            }
            SyncCompetitionManagers(competitionId);
        }

        // ---- competitionManagers mirror ----

        /// <summary>
        /// One-time (idempotent) reconcile: seed a Tävlingsledning StaffAssignment for every member in the
        /// competition's existing competitionManagers int[] that has no such row yet. Makes the roster the
        /// full picture before we treat it as the write source. Safe to run on every roster load.
        /// </summary>
        private void ReconcileManagersIntoRoster(int competitionId)
        {
            try
            {
                var content = _contentService.GetById(competitionId);
                if (content == null || !content.HasProperty("competitionManagers")) return;

                var json = content.GetValue<string>("competitionManagers") ?? "[]";
                int[] managerIds;
                try { managerIds = JsonConvert.DeserializeObject<int[]>(json) ?? Array.Empty<int>(); }
                catch { managerIds = Array.Empty<int>(); }
                if (managerIds.Length == 0) return;

                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                var existing = db.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND RoleKey = @1",
                    competitionId, TavlingsledningRole);
                var haveMemberIds = existing.Where(r => r.MemberId.HasValue).Select(r => r.MemberId!.Value).ToHashSet();

                foreach (var mid in managerIds.Distinct())
                {
                    if (mid <= 0 || haveMemberIds.Contains(mid)) continue;
                    var name = ResolveMemberName(mid);
                    var now = DateTime.UtcNow;
                    db.Insert(new StaffAssignment
                    {
                        CompetitionId = competitionId,
                        MemberId = mid,
                        DisplayName = name,
                        RoleKey = TavlingsledningRole,
                        FunctionTitle = "Tävlingsledare",
                        ScopeType = StaffScopeType.All,
                        IsResponsible = true,
                        HasAdminAccess = true,
                        Status = StaffAssignmentStatus.Confirmed,
                        AssignedByMemberId = 0,
                        CreatedDate = now,
                        ModifiedDate = now,
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: manager reconcile failed for competition {CompetitionId}", competitionId);
            }
        }

        /// <summary>
        /// Recompute the competition's competitionManagers int[] from the Tävlingsledning rows that carry
        /// HasAdminAccess=true (+ a member id), and write it back to the content node (Save + Publish so the
        /// published cache IsCompetitionManager reads sees it). The roster is authoritative once reconcile
        /// has run, so this never drops a manager the reconcile hasn't already captured as a row.
        /// </summary>
        private void SyncCompetitionManagers(int competitionId)
        {
            try
            {
                var content = _contentService.GetById(competitionId);
                if (content == null || !content.HasProperty("competitionManagers")) return;

                List<StaffAssignment> rows;
                using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                {
                    rows = scope.Database.Fetch<StaffAssignment>(
                        "SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND RoleKey = @1",
                        competitionId, TavlingsledningRole);
                }
                var wanted = rows.Where(r => r.HasAdminAccess && r.MemberId is > 0)
                                 .Select(r => r.MemberId!.Value)
                                 .Distinct()
                                 .OrderBy(x => x)
                                 .ToArray();

                var currentJson = content.GetValue<string>("competitionManagers") ?? "[]";
                int[] current;
                try { current = JsonConvert.DeserializeObject<int[]>(currentJson) ?? Array.Empty<int>(); }
                catch { current = Array.Empty<int>(); }

                if (current.OrderBy(x => x).SequenceEqual(wanted)) return; // no change → don't republish

                content.SetValue("competitionManagers", JsonConvert.SerializeObject(wanted));
                _contentService.Save(content);
                _contentService.Publish(content, new[] { "*" }, -1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: competitionManagers sync failed for competition {CompetitionId}", competitionId);
            }
        }

        private string ResolveMemberName(int memberId)
        {
            try
            {
                var m = _memberService.GetById(memberId);
                if (m == null) return $"Medlem {memberId}";
                var first = m.GetValue<string>("firstName") ?? "";
                var last = m.GetValue<string>("lastName") ?? "";
                var name = $"{first} {last}".Trim();
                return string.IsNullOrEmpty(name) ? (m.Name ?? $"Medlem {memberId}") : name;
            }
            catch { return $"Medlem {memberId}"; }
        }

        // ---- mapping helpers ----

        private static StaffAssignmentView ToView(StaffAssignment a)
        {
            var role = FunctionaryRoles.Resolve(null, a.RoleKey);
            return new StaffAssignmentView
            {
                Id = a.Id,
                MemberId = a.MemberId,
                DisplayName = a.DisplayName,
                Phone = a.Phone,
                Email = a.Email,
                RoleKey = a.RoleKey,
                RoleName = role?.DisplayName ?? a.RoleKey,
                FunctionTitle = a.FunctionTitle,
                ScopeType = a.ScopeType,
                ScopeKey = a.ScopeKey,
                ScopeLabel = BuildScopeLabel(a),
                TargetFrom = a.TargetFrom,
                TargetTo = a.TargetTo,
                ShiftLabel = BuildShiftLabel(a.StartsAt, a.EndsAt),
                IsResponsible = a.IsResponsible,
                HasAdminAccess = a.HasAdminAccess,
                Status = a.Status,
                Note = a.Note,
            };
        }

        private static string BuildScopeLabel(StaffAssignment a)
        {
            if (string.IsNullOrEmpty(a.ScopeType) || string.Equals(a.ScopeType, StaffScopeType.All, StringComparison.OrdinalIgnoreCase))
                return "Hela tävlingen";
            var label = $"{a.ScopeType} {a.ScopeKey}".Trim();
            if (a.TargetFrom is > 0)
            {
                label += a.TargetTo is > 0 && a.TargetTo != a.TargetFrom
                    ? $" · tavlor {a.TargetFrom}–{a.TargetTo}"
                    : $" · tavla {a.TargetFrom}";
            }
            return label;
        }

        private static string? BuildShiftLabel(DateTime? from, DateTime? to)
        {
            if (from == null && to == null) return null;
            string F(DateTime d) => d.ToString("HH:mm", CultureInfo.GetCultureInfo("sv-SE"));
            if (from != null && to != null)
            {
                // same day → just times; else include the date
                if (from.Value.Date == to.Value.Date) return $"{F(from.Value)}–{F(to.Value)}";
                string D(DateTime d) => d.ToString("d MMM HH:mm", CultureInfo.GetCultureInfo("sv-SE"));
                return $"{D(from.Value)}–{D(to.Value)}";
            }
            if (from != null) return $"från {F(from.Value)}";
            return $"till {F(to!.Value)}";
        }

        private static DateTime? ParseDateTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Flatpickr "Y-m-d H:i" or ISO; keep as unspecified wall-clock time.
            if (DateTime.TryParse(s, CultureInfo.GetCultureInfo("sv-SE"), DateTimeStyles.None, out var dt)) return dt;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out dt)) return dt;
            return null;
        }

        private static string NormalizeStatus(string? s) => s switch
        {
            StaffAssignmentStatus.Invited => StaffAssignmentStatus.Invited,
            StaffAssignmentStatus.Accepted => StaffAssignmentStatus.Accepted,
            StaffAssignmentStatus.Declined => StaffAssignmentStatus.Declined,
            StaffAssignmentStatus.Confirmed => StaffAssignmentStatus.Confirmed,
            _ => StaffAssignmentStatus.Planned,
        };
    }
}
