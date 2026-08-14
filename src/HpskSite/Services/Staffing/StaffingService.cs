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
    /// controller's job.
    ///
    /// <para><b>App access ≠ tävlingsansvarig.</b> ANY roster row (Kassa, Sekretariat, Stationschef, …)
    /// may carry <c>HasAdminAccess=true</c>, which grants that person the right to manage the competition
    /// in pistol.nu (<c>/competitionmanagement</c> and every staff screen under it) — see
    /// <see cref="HasRosterAdminAccess"/>, which <c>AdminAuthorizationService.IsCompetitionManager</c>
    /// unions with the competition's <c>competitionManagers</c> list. The mirror INTO
    /// <c>competitionManagers</c> stays deliberately limited to <b>Tävlingsledning</b> rows
    /// (<see cref="SyncCompetitionManagers"/>), because that property is also the public
    /// "Tävlingsansvariga" list on the competition page — a cashier with app access must not be
    /// published as a competition official. See COMPETITION_STAFFING_SYSTEM.md §4.</para>
    /// </summary>
    public class StaffingService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;
        private readonly IMemberService _memberService;
        private readonly RoleCatalogService _roles;
        private readonly ILogger<StaffingService> _logger;

        private const string TavlingsledningRole = "tavlingsledning";

        public StaffingService(IScopeProvider scopeProvider, IContentService contentService, IMemberService memberService, RoleCatalogService roles, ILogger<StaffingService> logger)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _memberService = memberService;
            _roles = roles;
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
        /// True when this member holds at least one roster row on this competition that was granted app
        /// access ("Ge behörighet att hantera tävlingen"). Role-agnostic on purpose: sekretariat, kassa,
        /// stationschef and tävlingsledning all reach the same management page, so the grant is a property
        /// of the assignment, not of the role. Status is NOT considered — the organiser ticking the box is
        /// the explicit grant; an unanswered or declined invitation doesn't silently revoke it.
        /// Returns false (never throws) if the table isn't there yet, so auth degrades to the legacy
        /// competitionManagers list rather than locking everyone out.
        /// </summary>
        public bool HasRosterAdminAccess(int competitionId, int memberId)
        {
            if (competitionId <= 0 || memberId <= 0) return false;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                return scope.Database.ExecuteScalar<int>(
                    "SELECT COUNT(1) FROM StaffAssignment WHERE CompetitionId = @0 AND MemberId = @1 AND HasAdminAccess = 1",
                    competitionId, memberId) > 0;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: roster app-access lookup failed for competition {CompetitionId}", competitionId);
                return false;
            }
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

            var roles = _roles.ForCompetition(competitionId, discipline);
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
                        .Select(a => ToView(a, competitionId, discipline))
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
                    Assignments = orphans.Select(a => ToView(a, competitionId, discipline)).ToList(),
                });
            }

            // Availability (P3): show each assignee's declared windows so the organiser can plan around them.
            var availByMember = GetAvailabilityForCompetition(competitionId)
                .GroupBy(a => a.MemberId)
                .ToDictionary(g => g.Key, g => g.Select(a => BuildAvailabilityLabel(a.AvailableFrom, a.AvailableTo)).ToList());
            if (availByMember.Count > 0)
                foreach (var grp in resp.Groups)
                    foreach (var a in grp.Assignments)
                        if (a.MemberId is int mid && availByMember.TryGetValue(mid, out var labels))
                            a.AvailabilityLabels = labels;

            // Pass labels (structured shift) — resolve PassId → "Label · 06:00–13:00" on each assignment.
            try
            {
                using var pscope = _scopeProvider.CreateScope(autoComplete: true);
                var passes = pscope.Database.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE CompetitionId = @0", competitionId);
                if (passes.Count > 0)
                {
                    var byId = passes.ToDictionary(p => p.Id, p =>
                    {
                        var lbl = string.IsNullOrWhiteSpace(p.Label) ? p.PassDate.ToString("ddd d MMM", CultureInfo.GetCultureInfo("sv-SE")) : p.Label;
                        var t = (string.IsNullOrEmpty(p.StartTime) && string.IsNullOrEmpty(p.EndTime)) ? null : $"{p.StartTime}–{p.EndTime}";
                        return t == null ? lbl : $"{lbl} · {t}";
                    });
                    var dateById = passes.ToDictionary(p => p.Id, p => p.PassDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    foreach (var g in resp.Groups)
                        foreach (var a in g.Assignments)
                            if (a.PassId is int pid)
                            {
                                if (byId.TryGetValue(pid, out var lbl)) a.PassLabel = lbl;
                                // An explicit StartsAt wins; the pass only supplies the day when there isn't one.
                                if (a.DateKey == null && dateById.TryGetValue(pid, out var dk)) a.DateKey = dk;
                            }
                }
            }
            catch { }

            // Fält convergence (P2): surface station chiefs assigned on the Stationer tab
            // (faltskytteStationManagers JSON) as read-only rows in the stationschef group, so the roster
            // shows the full picture. Deduped by station — a real StaffAssignment for Station:N wins.
            if (FunctionaryRoles.FaltFamily.Contains(discipline ?? "", StringComparer.OrdinalIgnoreCase))
                InjectStationChiefs(competitionId, resp);

            resp.TotalAssigned = rows.Count;
            return resp;
        }

        /// <summary>Add read-only stationschef rows from the competition's faltskytteStationManagers JSON.</summary>
        private void InjectStationChiefs(int competitionId, StaffRosterResponse resp)
        {
            try
            {
                var content = _contentService.GetById(competitionId);
                var json = content?.HasProperty("faltskytteStationManagers") == true
                    ? content.GetValue<string>("faltskytteStationManagers") : null;
                if (string.IsNullOrWhiteSpace(json)) return;

                var group = resp.Groups.FirstOrDefault(g => string.Equals(g.RoleKey, "stationschef", StringComparison.OrdinalIgnoreCase));
                if (group == null) return;
                var haveStations = group.Assignments
                    .Where(a => string.Equals(a.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(a.ScopeKey))
                    .Select(a => a.ScopeKey!).ToHashSet(StringComparer.OrdinalIgnoreCase);

                var obj = Newtonsoft.Json.Linq.JObject.Parse(json);
                foreach (var prop in obj.Properties().OrderBy(p => int.TryParse(p.Name, out var n) ? n : int.MaxValue))
                {
                    var station = prop.Name;
                    if (haveStations.Contains(station)) continue;   // a real assignment already covers it
                    var v = prop.Value as Newtonsoft.Json.Linq.JObject;
                    var name = v?.Value<string>("name");
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var phone = v?.Value<string>("phone");
                    var memberId = v?.Value<int?>("memberId");
                    group.Assignments.Add(new StaffAssignmentView
                    {
                        Id = 0,
                        MemberId = memberId is > 0 ? memberId : null,
                        DisplayName = name!,
                        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone,
                        RoleKey = "stationschef",
                        RoleName = "Stationschef",
                        ScopeType = StaffScopeType.Station,
                        ScopeKey = station,
                        ScopeLabel = $"Station {station}",
                        IsResponsible = true,
                        Status = StaffAssignmentStatus.Confirmed,
                        ReadOnly = true,
                        SourceLabel = "Stationer-fliken",
                    });
                }
                group.Assignments = group.Assignments
                    .OrderBy(a => int.TryParse(a.ScopeKey, out var n) ? n : int.MaxValue)
                    .ThenByDescending(a => a.IsResponsible)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: station-chief injection failed for competition {CompetitionId}", competitionId);
            }
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
                row.PassId = req.PassId is > 0 ? req.PassId : null;
                row.IsResponsible = req.IsResponsible;
                // App access is role-agnostic (a Kassa- or Sekretariatsansvarig needs the same management
                // page as the tävlingsledare). It does require a linked member, though — access is granted
                // to a pistol.nu login, so a free-text helper row can never carry it.
                row.HasAdminAccess = req.HasAdminAccess && row.MemberId is > 0;
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
            if (string.Equals(req.RoleKey, StationschefRole, StringComparison.OrdinalIgnoreCase))
                SyncStationManagers(req.CompetitionId);
            return savedId;
        }

        /// <summary>Lightweight status update (e.g. Planned → Invited after a notification) without a full save.</summary>
        public void SetStatus(int id, int competitionId, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET Status = @0, ModifiedDate = @1 WHERE Id = @2 AND CompetitionId = @3",
                NormalizeStatus(status), DateTime.UtcNow, id, competitionId);
        }

        public void Delete(int id, int competitionId)
        {
            string? roleKey;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                roleKey = scope.Database.ExecuteScalar<string>("SELECT RoleKey FROM StaffAssignment WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
                scope.Database.Execute("DELETE FROM StaffAssignment WHERE Id = @0 AND CompetitionId = @1", id, competitionId);
            }
            SyncCompetitionManagers(competitionId);
            if (string.Equals(roleKey, StationschefRole, StringComparison.OrdinalIgnoreCase))
                SyncStationManagers(competitionId);
        }

        // ---- day-of cockpit: roll-call / upprop (+ overlay hook) ----

        public void SetCheckedIn(int id, int competitionId, bool checkedIn)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET CheckedInAt = @0, ModifiedDate = @1 WHERE Id = @2 AND CompetitionId = @3",
                checkedIn ? DateTime.UtcNow : (DateTime?)null, DateTime.UtcNow, id, competitionId);
        }

        /// <summary>
        /// Scope-centric projection of the roster for the competition-day cockpit: one group per scope unit
        /// (Skjutlag/Station/Klass/Patrull/Bana + "Hela tävlingen"), each listing its planned crew and a
        /// roll-call tally (how many have checked in). Includes read-only Fält station chiefs. The live
        /// "ActiveNow" reconciliation (#1b) is merged client-side against the discipline's load endpoint.
        /// </summary>
        public DayOfCockpitResponse BuildDayOfCockpit(int competitionId, string? discipline, bool canEdit)
        {
            // Reuse BuildRoster so station-chief injection + availability + role resolution are consistent.
            var roster = BuildRoster(competitionId, discipline, canEdit);
            var resp = new DayOfCockpitResponse { Discipline = discipline ?? "", CanEdit = canEdit };

            var groups = new Dictionary<string, DayOfScopeGroup>(StringComparer.OrdinalIgnoreCase);
            foreach (var rg in roster.Groups)
            {
                foreach (var a in rg.Assignments)
                {
                    var isAll = string.IsNullOrEmpty(a.ScopeType) || string.Equals(a.ScopeType, StaffScopeType.All, StringComparison.OrdinalIgnoreCase);
                    var scopeType = isAll ? StaffScopeType.All : a.ScopeType!;
                    var scopeKey = isAll ? null : a.ScopeKey;
                    var mapKey = $"{scopeType}:{scopeKey}";
                    if (!groups.TryGetValue(mapKey, out var g))
                    {
                        g = new DayOfScopeGroup
                        {
                            ScopeType = scopeType,
                            ScopeKey = scopeKey,
                            ScopeLabel = isAll ? "Hela tävlingen" : $"{scopeType} {scopeKey}".Trim(),
                            SortKey = isAll ? int.MaxValue : (int.TryParse(scopeKey, out var n) ? n : int.MaxValue - 1),
                        };
                        groups[mapKey] = g;
                    }
                    g.Planned.Add(new DayOfPersonView
                    {
                        Id = a.Id,
                        MemberId = a.MemberId,
                        Name = a.DisplayName,
                        RoleKey = a.RoleKey,
                        RoleName = a.RoleName,
                        FunctionTitle = a.FunctionTitle,
                        ShiftLabel = a.ShiftLabel,
                        IsResponsible = a.IsResponsible,
                        CheckedIn = a.CheckedIn,
                        ReadOnly = a.ReadOnly,
                        Phone = a.Phone,
                    });
                }
            }

            foreach (var g in groups.Values)
            {
                g.PlannedCount = g.Planned.Count;
                g.CheckedInCount = g.Planned.Count(p => p.CheckedIn);
                g.Planned = g.Planned
                    .OrderByDescending(p => p.IsResponsible)
                    .ThenBy(p => p.RoleName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            resp.Groups = groups.Values.OrderBy(g => g.SortKey).ThenBy(g => g.ScopeType, StringComparer.OrdinalIgnoreCase).ToList();
            resp.TotalPlanned = resp.Groups.Sum(g => g.PlannedCount);
            resp.TotalCheckedIn = resp.Groups.Sum(g => g.CheckedInCount);
            return resp;
        }

        /// <summary>Clone every assignment on one pass to another (e.g. copy Day 1 → Day 2). Same person +
        /// role + scope + target range + responsible, new PassId. Skips rows already present on the target
        /// (deduped by person + role + scope). Returns how many were copied.</summary>
        public int CopyPassAssignments(int competitionId, int fromPassId, int toPassId, int byMemberId)
        {
            if (fromPassId <= 0 || toPassId <= 0 || fromPassId == toPassId) return 0;
            int copied = 0;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                var src = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND PassId = @1", competitionId, fromPassId);
                var dst = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND PassId = @1", competitionId, toPassId);
                string Key(StaffAssignment a) => $"{(a.MemberId is > 0 ? "m" + a.MemberId : "n" + (a.DisplayName ?? "").Trim().ToLowerInvariant())}|{a.RoleKey}|{a.ScopeType}|{a.ScopeKey}";
                var have = dst.Select(Key).ToHashSet();
                var now = DateTime.UtcNow;
                foreach (var a in src)
                {
                    if (have.Contains(Key(a))) continue;
                    db.Insert(new StaffAssignment
                    {
                        CompetitionId = competitionId,
                        MemberId = a.MemberId,
                        DisplayName = a.DisplayName,
                        Phone = a.Phone,
                        Email = a.Email,
                        RoleKey = a.RoleKey,
                        FunctionTitle = a.FunctionTitle,
                        ScopeType = a.ScopeType,
                        ScopeKey = a.ScopeKey,
                        TargetFrom = a.TargetFrom,
                        TargetTo = a.TargetTo,
                        PassId = toPassId,
                        IsResponsible = a.IsResponsible,
                        HasAdminAccess = a.HasAdminAccess,
                        Status = a.Status,
                        Note = a.Note,
                        AssignedByMemberId = byMemberId,
                        CreatedDate = now,
                        ModifiedDate = now,
                    });
                    copied++;
                }
            }
            if (copied > 0) { SyncCompetitionManagers(competitionId); SyncStationManagers(competitionId); }
            return copied;
        }

        // ---- availability + member self-service (P3: sign-up + tillgänglighet) ----

        public List<StaffAvailability> GetAvailabilityForCompetition(int competitionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffAvailability>(
                "SELECT * FROM StaffAvailability WHERE CompetitionId = @0 ORDER BY MemberId, AvailableFrom", competitionId);
        }

        public bool MemberHasAssignment(int competitionId, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM StaffAssignment WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId) > 0;
        }

        public int AddAvailability(int competitionId, int memberId, DateTime? from, DateTime? to, string? note)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return Convert.ToInt32(scope.Database.Insert(new StaffAvailability
            {
                CompetitionId = competitionId,
                MemberId = memberId,
                AvailableFrom = from,
                AvailableTo = to,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                CreatedDate = DateTime.UtcNow,
            }));
        }

        /// <summary>Delete an availability window — only the owning member may.</summary>
        public bool DeleteAvailability(int id, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.SingleOrDefault<StaffAvailability>("SELECT * FROM StaffAvailability WHERE Id = @0", id);
            if (row == null || row.MemberId != memberId) return false;
            db.Execute("DELETE FROM StaffAvailability WHERE Id = @0", id);
            return true;
        }

        /// <summary>Set an assignment's accept/decline status from a tokened external-invite link (the token is
        /// the authorization — no member ownership check). Only Accepted/Declined allowed.</summary>
        public (bool Ok, string? Message) SetInviteResponse(int assignmentId, string status)
        {
            var wanted = status switch
            {
                StaffAssignmentStatus.Accepted => StaffAssignmentStatus.Accepted,
                StaffAssignmentStatus.Declined => StaffAssignmentStatus.Declined,
                _ => null,
            };
            if (wanted == null) return (false, "Ogiltigt svar.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var n = db.Execute("UPDATE StaffAssignment SET Status = @0, ModifiedDate = @1 WHERE Id = @2", wanted, DateTime.UtcNow, assignmentId);
            return n > 0 ? (true, null) : (false, "Uppdraget hittades inte.");
        }

        /// <summary>Resolve display info for the external-invite page (comp name, role, scope, shift, status).</summary>
        public InviteResponseModel? GetInviteInfo(int assignmentId)
        {
            var a = GetById(assignmentId);
            if (a == null) return null;
            var content = _contentService.GetById(a.CompetitionId);
            var compName = content?.GetValue<string>("competitionName") ?? "Tävling";
            var discipline = content?.GetValue<string>("competitionType") ?? "";
            var role = _roles.Resolve(a.CompetitionId, discipline, a.RoleKey);
            return new InviteResponseModel
            {
                Valid = true,
                CompName = compName,
                RoleName = role?.DisplayName ?? a.RoleKey,
                ScopeLabel = BuildScopeLabel(a),
                ShiftLabel = BuildShiftLabel(a.StartsAt, a.EndsAt),
                PersonName = a.DisplayName,
                Status = a.Status,
            };
        }

        /// <summary>A member accepts/declines their OWN assignment. Ownership-checked; no comp-staff access needed.</summary>
        public (bool Ok, string? Message) RespondAsMember(int assignmentId, int memberId, string status)
        {
            var wanted = status switch
            {
                StaffAssignmentStatus.Accepted => StaffAssignmentStatus.Accepted,
                StaffAssignmentStatus.Declined => StaffAssignmentStatus.Declined,
                _ => null,
            };
            if (wanted == null) return (false, "Ogiltigt svar.");
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var row = db.SingleOrDefault<StaffAssignment>("SELECT * FROM StaffAssignment WHERE Id = @0", assignmentId);
            if (row == null) return (false, "Uppdraget hittades inte.");
            if (row.MemberId != memberId) return (false, "Du kan bara svara på dina egna uppdrag.");
            db.Execute("UPDATE StaffAssignment SET Status = @0, ModifiedDate = @1 WHERE Id = @2", wanted, DateTime.UtcNow, assignmentId);
            return (true, null);
        }

        /// <summary>Every assignment the member holds, grouped by competition, with their availability windows.</summary>
        public List<MyCompetitionGroup> GetMyAssignments(int memberId)
        {
            List<StaffAssignment> rows;
            List<StaffAvailability> avail;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                rows = scope.Database.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE MemberId = @0", memberId);
                avail = scope.Database.Fetch<StaffAvailability>(
                    "SELECT * FROM StaffAvailability WHERE MemberId = @0", memberId);
            }

            var compIds = rows.Select(r => r.CompetitionId).Concat(avail.Select(a => a.CompetitionId)).Distinct().ToList();
            var availByComp = avail.GroupBy(a => a.CompetitionId).ToDictionary(g => g.Key, g => g.ToList());
            var rowsByComp = rows.GroupBy(r => r.CompetitionId).ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<MyCompetitionGroup>();
            foreach (var compId in compIds)
            {
                var content = _contentService.GetById(compId);
                if (content == null) continue;
                var name = content.GetValue<string>("competitionName") ?? "Tävling";
                var discipline = content.GetValue<string>("competitionType") ?? "";
                var date = content.GetValue<DateTime?>("competitionDate");

                var grp = new MyCompetitionGroup
                {
                    CompetitionId = compId,
                    CompName = name,
                    CompDate = date is { } d && d != default ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                };
                if (rowsByComp.TryGetValue(compId, out var rs))
                {
                    grp.Assignments = rs
                        .OrderBy(r => r.RoleKey).ThenBy(r => r.ScopeKey, StringComparer.OrdinalIgnoreCase)
                        .Select(r => new MyAssignmentView
                        {
                            Id = r.Id,
                            RoleName = _roles.NameFor(compId, discipline, r.RoleKey),
                            FunctionTitle = r.FunctionTitle,
                            ScopeLabel = BuildScopeLabel(r),
                            ShiftLabel = BuildShiftLabel(r.StartsAt, r.EndsAt),
                            Status = r.Status,
                            IsResponsible = r.IsResponsible,
                        }).ToList();
                }
                if (availByComp.TryGetValue(compId, out var av))
                {
                    grp.Availability = av
                        .OrderBy(a => a.AvailableFrom ?? DateTime.MinValue)
                        .Select(a => new StaffAvailabilityView { Id = a.Id, Label = BuildAvailabilityLabel(a.AvailableFrom, a.AvailableTo), Note = a.Note })
                        .ToList();
                }
                result.Add(grp);
            }
            return result
                .OrderBy(g => g.CompDate == null)                 // dated first
                .ThenBy(g => g.CompDate, StringComparer.Ordinal)
                .ToList();
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
        /// Recompute the competition's competitionManagers int[] from the <b>Tävlingsledning</b> rows that
        /// carry HasAdminAccess=true (+ a member id), and write it back to the content node (Save + Publish
        /// so the published cache reads see it). The roster is authoritative once reconcile has run, so this
        /// never drops a manager the reconcile hasn't already captured as a row.
        ///
        /// <para>Deliberately Tävlingsledning-only: competitionManagers doubles as the <i>public</i>
        /// "Tävlingsansvariga" list on the competition page, so rows from other roles must not land here even
        /// though they carry app access. Their access comes from <see cref="HasRosterAdminAccess"/> instead.</para>
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

        private const string StationschefRole = "stationschef";

        /// <summary>
        /// Fält convergence: write the roster's stationschef rows through into the competition's
        /// faltskytteStationManagers JSON (the canonical store the Stationer tab, station-entry page and
        /// prints all read), so a chief assigned in the Bemanning roster shows up everywhere and there is
        /// no double-entry. Roster rows are authoritative for their station (tagged "_fromRoster"); entries
        /// set on the Stationer tab for stations without a roster row are preserved. Deleting a roster row
        /// drops its entry on the next sync.
        /// </summary>
        private void SyncStationManagers(int competitionId)
        {
            try
            {
                var content = _contentService.GetById(competitionId);
                if (content == null || !content.HasProperty("faltskytteStationManagers")) return;
                var type = content.GetValue<string>("competitionType") ?? "";
                if (!FunctionaryRoles.FaltFamily.Contains(type, StringComparer.OrdinalIgnoreCase)) return;

                var json = content.GetValue<string>("faltskytteStationManagers") ?? "";
                Newtonsoft.Json.Linq.JObject obj;
                try { obj = string.IsNullOrWhiteSpace(json) ? new Newtonsoft.Json.Linq.JObject() : Newtonsoft.Json.Linq.JObject.Parse(json); }
                catch { obj = new Newtonsoft.Json.Linq.JObject(); }

                // Drop previous roster-origin entries; rebuild from current stationschef rows.
                foreach (var p in obj.Properties().ToList())
                    if (p.Value is Newtonsoft.Json.Linq.JObject o && o.Value<bool?>("_fromRoster") == true)
                        p.Remove();

                List<StaffAssignment> rows;
                using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                    rows = scope.Database.Fetch<StaffAssignment>(
                        "SELECT * FROM StaffAssignment WHERE CompetitionId = @0 AND RoleKey = @1", competitionId, StationschefRole);

                foreach (var r in rows)
                {
                    if (!string.Equals(r.ScopeType, StaffScopeType.Station, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(r.ScopeKey))
                        continue;
                    obj[r.ScopeKey!] = new Newtonsoft.Json.Linq.JObject
                    {
                        ["name"] = r.DisplayName,
                        ["phone"] = r.Phone ?? "",
                        ["memberId"] = r.MemberId,
                        ["_fromRoster"] = true,
                    };
                }

                var newJson = obj.ToString(Newtonsoft.Json.Formatting.None);
                if (string.Equals(newJson, json, StringComparison.Ordinal)) return;   // no change → don't republish
                content.SetValue("faltskytteStationManagers", newJson);
                _contentService.Save(content);
                _contentService.Publish(content, new[] { "*" }, -1);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Staffing: station-manager sync failed for competition {CompetitionId}", competitionId);
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

        private StaffAssignmentView ToView(StaffAssignment a, int competitionId, string? discipline)
        {
            var role = _roles.Resolve(competitionId, discipline, a.RoleKey);
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
                PassId = a.PassId,
                DateKey = a.StartsAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IsResponsible = a.IsResponsible,
                HasAdminAccess = a.HasAdminAccess,
                Status = a.Status,
                Note = a.Note,
                CheckedIn = a.CheckedInAt.HasValue,
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

        /// <summary>Human label for an availability window — "lör 13:00–17:00", "12 jun heldag", "Heldag".</summary>
        private static string BuildAvailabilityLabel(DateTime? from, DateTime? to)
        {
            var ci = CultureInfo.GetCultureInfo("sv-SE");
            if (from == null && to == null) return "Heldag";
            string Day(DateTime d) => d.ToString("ddd d MMM", ci);
            string T(DateTime d) => d.ToString("HH:mm", ci);
            if (from != null && to != null)
            {
                if (from.Value.Date == to.Value.Date) return $"{Day(from.Value)} {T(from.Value)}–{T(to.Value)}";
                return $"{Day(from.Value)} {T(from.Value)} – {Day(to.Value)} {T(to.Value)}";
            }
            if (from != null) return $"från {Day(from.Value)} {T(from.Value)}";
            return $"till {Day(to!.Value)} {T(to.Value)}";
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
