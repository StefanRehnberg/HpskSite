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
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly IMemberService _memberService;
        private readonly RoleCatalogService _roles;
        private readonly StaffOwnerResolver _owners;
        private readonly ILogger<StaffingService> _logger;

        private const string TavlingsledningRole = "tavlingsledning";

        public StaffingService(IScopeProvider scopeProvider, IContentService contentService, IMemberService memberService, RoleCatalogService roles, ILogger<StaffingService> logger,
            HpskSite.Services.Competitions.ICompetitionStore competitions, StaffOwnerResolver owners)
        {
            _owners = owners;
            _scopeProvider = scopeProvider;
            _contentService = contentService;
            _competitions = competitions;
            _memberService = memberService;
            _roles = roles;
            _logger = logger;
        }

        // ---- reads ----

        /// <summary>Ägarens alla rader (P2b: planeringens, och tävlingens som ännu saknar planering).</summary>
        public List<StaffAssignment> GetForOwner(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE " + owner.Filter(0) + " ORDER BY RoleKey, ScopeKey, StartsAt, Id",
                owner.P, owner.C);
        }

        /// <summary>Tävlingens bemanning — dess planerings, om den har en.</summary>
        public List<StaffAssignment> GetForCompetition(int competitionId) => GetForOwner(_owners.ForCompetition(competitionId));

        public StaffAssignment? GetById(int id)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.SingleOrDefault<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE Id = @0", id);
        }

        /// <summary>Ägaren till en rad, eller null när raden inte finns.</summary>
        public StaffOwner? OwnerOf(int id)
        {
            var a = GetById(id);
            return a == null ? null : _owners.OfRow(a.PlanningId, a.CompetitionId);
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
                // En grind som inte kunde fråga svarar nej, men det får aldrig vara tyst: ett databasfel
                // här låser ute kassören och sekretariatet på tävlingsdagen. Samma väg som övriga grindar.
                return HpskSite.Services.Ops.OpsSignals.AuthGateFailed(_logger, ex, "HasRosterAdminAccess", $"{competitionId}/{memberId}");
            }
        }

        /// <summary>
        /// Build the grouped roster for a competition, one group per role valid for its discipline
        /// (empty groups included so the admin sees which crew a comp normally needs). Runs the
        /// one-time competitionManagers reconcile first so managers added the old way appear here.
        /// </summary>
        public StaffRosterResponse BuildRoster(StaffOwner owner, string? discipline, bool canEdit)
        {
            if (owner.HasCompetition) ReconcileManagersIntoRoster(owner);

            var rows = GetForOwner(owner);
            var resp = new StaffRosterResponse { Discipline = discipline ?? "", CanEdit = canEdit };

            var roles = _roles.For(owner, discipline);
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
                        .Select(a => ToView(a, owner, discipline))
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
                    Assignments = orphans.Select(a => ToView(a, owner, discipline)).ToList(),
                });
            }

            // Availability (P3): show each assignee's declared windows so the organiser can plan around them.
            var availByMember = GetAvailabilityForOwner(owner)
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
                var passes = pscope.Database.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE " + owner.Filter(0), owner.P, owner.C);
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
            if (owner.HasCompetition && FunctionaryRoles.FaltFamily.Contains(discipline ?? "", StringComparer.OrdinalIgnoreCase))
                InjectStationChiefs(owner.Comp, resp);

            resp.TotalAssigned = rows.Count;
            return resp;
        }

        /// <summary>Add read-only stationschef rows from the competition's faltskytteStationManagers JSON.</summary>
        private void InjectStationChiefs(int competitionId, StaffRosterResponse resp)
        {
            try
            {
                var json = _competitions.GetSaved(competitionId)?.FaltskytteStationManagers;
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

        public int Save(SaveStaffAssignmentRequest req, StaffOwner owner, int assignedByMemberId)
        {
            var now = DateTime.UtcNow;
            int savedId;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                StaffAssignment row;
                if (req.Id > 0)
                {
                    // Bara en rad i den här tävlingen. Behörigheten prövas för req.CompetitionId, så en rad i en
                    // annan tävling får aldrig hittas här — förut skrevs dess tävling över med anropets, alltså
                    // kunde personal på en tävling flytta en annan tävlings tävlingsledare till sin egen
                    // (rättat 2026-10-21). Ett okänt id skapar inte heller en ny rad längre.
                    var existing = db.SingleOrDefault<StaffAssignment>(
                        "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), req.Id, owner.P, owner.C);
                    if (existing == null) return 0;
                    row = existing;
                }
                else
                {
                    row = new StaffAssignment { CreatedDate = now, AssignedByMemberId = assignedByMemberId };
                }

                // P2b: raden bär planeringen OCH (när den är kopplad) tävlingen.
                row.PlanningId = owner.PlanningIdOrNull;
                row.CompetitionId = owner.CompetitionIdOrNull;
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
                // A timed row already carries its day; DayDate only speaks for untimed ("heldag") rows,
                // so keeping both would let them drift apart.
                row.DayDate = row.StartsAt != null ? null : ParseDate(req.DayDate);
                if (req.PassId is > 0 && db.ExecuteScalar<int>(
                        "SELECT COUNT(1) FROM StaffPass WHERE Id = @0 AND " + StaffOwner.Where(1), req.PassId, owner.P, owner.C) == 0)
                    return 0;   // ett pass i en annan tävling
                row.PassId = req.PassId is > 0 ? req.PassId : null;
                row.IsResponsible = req.IsResponsible;
                // App access is role-agnostic (a Kassa- or Sekretariatsansvarig needs the same management
                // page as the tävlingsledare). It does require a linked member, though — access is granted
                // to a pistol.nu login, so a free-text helper row can never carry it.
                // ...och ges bara när planeringen är kopplad till en tävling (det finns ingen tävling att hantera annars).
                row.HasAdminAccess = req.HasAdminAccess && row.MemberId is > 0 && owner.HasCompetition;
                row.Status = NormalizeStatus(req.Status);
                row.Note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
                row.ModifiedDate = now;

                if (row.Id > 0) db.Update(row);
                else row.Id = Convert.ToInt32(db.Insert(row));
                savedId = row.Id;
            }

            // Mirror to competitionManagers AFTER the write scope has committed, so the ContentService
            // Save+Publish runs in its own transaction (no publish nested inside the raw-SQL scope).
            if (owner.HasCompetition)
            {
                SyncCompetitionManagers(owner.Comp);
                if (string.Equals(req.RoleKey, StationschefRole, StringComparison.OrdinalIgnoreCase))
                    SyncStationManagers(owner.Comp);
            }
            return savedId;
        }

        /// <summary>Lightweight status update (e.g. Planned → Invited after a notification) without a full save.</summary>
        public void SetStatus(int id, StaffOwner owner, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET Status = @0, ModifiedDate = @1 WHERE Id = @2 AND " + StaffOwner.Where(3),
                NormalizeStatus(status), DateTime.UtcNow, id, owner.P, owner.C);
        }

        public void Delete(int id, StaffOwner owner)
        {
            string? roleKey;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                roleKey = scope.Database.ExecuteScalar<string>("SELECT RoleKey FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P, owner.C);
                scope.Database.Execute("DELETE FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P, owner.C);
            }
            if (!owner.HasCompetition) return;
            SyncCompetitionManagers(owner.Comp);
            if (string.Equals(roleKey, StationschefRole, StringComparison.OrdinalIgnoreCase))
                SyncStationManagers(owner.Comp);
        }

        /// <summary>
        /// P2b: speglingarna till tävlingen (tävlingsansvariga och stationschefer) räknas om — efter att en
        /// planering kopplats eller kopplats loss. Läser tävlingens rader, så en losskopplad planerings
        /// personer försvinner ur speglingarna.
        /// </summary>
        public void SyncCompetitionMirrors(int competitionId)
        {
            if (competitionId <= 0) return;
            SyncCompetitionManagers(competitionId);
            SyncStationManagers(competitionId);
        }

        // ---- day-of cockpit: roll-call / upprop (+ overlay hook) ----

        public void SetCheckedIn(int id, StaffOwner owner, bool checkedIn)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET CheckedInAt = @0, ModifiedDate = @1 WHERE Id = @2 AND " + owner.Filter(3),
                checkedIn ? DateTime.UtcNow : (DateTime?)null, DateTime.UtcNow, id, owner.P, owner.C);
        }

        /// <summary>
        /// Scope-centric projection of the roster for the competition-day cockpit: one group per scope unit
        /// (Skjutlag/Station/Klass/Patrull/Bana + "Hela tävlingen"), each listing its planned crew and a
        /// roll-call tally (how many have checked in). Includes read-only Fält station chiefs. The live
        /// "ActiveNow" reconciliation (#1b) is merged client-side against the discipline's load endpoint.
        /// </summary>
        public DayOfCockpitResponse BuildDayOfCockpit(StaffOwner owner, string? discipline, bool canEdit)
        {
            // Reuse BuildRoster so station-chief injection + availability + role resolution are consistent.
            var roster = BuildRoster(owner, discipline, canEdit);
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
        public int CopyPassAssignments(StaffOwner owner, int fromPassId, int toPassId, int byMemberId)
        {
            if (fromPassId <= 0 || toPassId <= 0 || fromPassId == toPassId) return 0;
            int copied = 0;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;
                var src = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND PassId = @2", owner.P, owner.C, fromPassId);
                var dst = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND PassId = @2", owner.P, owner.C, toPassId);
                string Key(StaffAssignment a) => $"{(a.MemberId is > 0 ? "m" + a.MemberId : "n" + (a.DisplayName ?? "").Trim().ToLowerInvariant())}|{a.RoleKey}|{a.ScopeType}|{a.ScopeKey}";
                var have = dst.Select(Key).ToHashSet();
                var now = DateTime.UtcNow;
                foreach (var a in src)
                {
                    if (have.Contains(Key(a))) continue;
                    db.Insert(new StaffAssignment
                    {
                        PlanningId = owner.PlanningIdOrNull,
                        CompetitionId = owner.CompetitionIdOrNull,
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
            if (copied > 0 && owner.HasCompetition) { SyncCompetitionManagers(owner.Comp); SyncStationManagers(owner.Comp); }
            return copied;
        }

        // ---- availability + member self-service (P3: sign-up + tillgänglighet) ----

        public List<StaffAvailability> GetAvailabilityForOwner(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<StaffAvailability>(
                "SELECT * FROM StaffAvailability WHERE " + StaffOwner.Where(0) + " ORDER BY MemberId, AvailableFrom", owner.P, owner.C);
        }

        public bool MemberHasAssignment(StaffOwner owner, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND MemberId = @2", owner.P, owner.C, memberId) > 0;
        }

        public int AddAvailability(StaffOwner owner, int memberId, DateTime? from, DateTime? to, string? note)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return Convert.ToInt32(scope.Database.Insert(new StaffAvailability
            {
                PlanningId = owner.PlanningIdOrNull,
                CompetitionId = owner.CompetitionIdOrNull,
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

        /// <summary>
        /// Move one assignment to another day, keeping its clock time. Exists because the guards that
        /// refuse to delete a day (or hide a role) while crew sit on it are correct but become a WALL
        /// without a way out — the organiser could see the leftover column and had no means to clear it.
        /// <para>Deliberately narrow: it touches StartsAt/EndsAt/PassId and nothing else, so it can never
        /// blank a field the way a partial full-save would.</para>
        /// <para>Returns a refusal for an untimed row on a day with no pass: pinning it would mean
        /// inventing a clock time, and a fake 00:00 becomes a real 23:30 reminder the night before.</para>
        /// </summary>
        public (bool Ok, string? Message) MoveToDay(int assignmentId, StaffOwner owner, DateTime day, int? targetPassId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefault<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), assignmentId, owner.P, owner.C);
            if (a == null) return (false, "Uppdraget hittades inte.");

            DateTime? Shift(DateTime? t) => t is { } v ? day.Date.Add(v.TimeOfDay) : null;

            if (a.StartsAt == null)
            {
                // An all-day row now has an honest home: the day, with no clock time claimed.
                db.Execute("UPDATE StaffAssignment SET DayDate = @0, PassId = @1, ModifiedDate = @2 WHERE Id = @3",
                    day.Date, targetPassId, DateTime.UtcNow, assignmentId);
                return (true, null);
            }

            db.Execute("UPDATE StaffAssignment SET StartsAt = @0, EndsAt = @1, DayDate = NULL, PassId = @2, ModifiedDate = @3 WHERE Id = @4",
                Shift(a.StartsAt), Shift(a.EndsAt), targetPassId, DateTime.UtcNow, assignmentId);
            return (true, null);
        }

        // ---- person-level writes: one human, every row -------------------------------------------

        /// <summary>
        /// Resolve a person key to the rows it owns. "m:{id}" → that member's rows; "n:{name}" → the
        /// member-less rows carrying that name. Keeping this in ONE place is what makes the person actions
        /// agree with <see cref="CompetitionPeopleService"/>, which groups by the same key.
        /// </summary>
        private List<StaffAssignment> RowsForPerson(Umbraco.Cms.Infrastructure.Persistence.IUmbracoDatabase db,
            StaffOwner owner, string personKey)
        {
            if (string.IsNullOrWhiteSpace(personKey)) return new List<StaffAssignment>();
            if (personKey.StartsWith("m:", StringComparison.Ordinal)
                && int.TryParse(personKey[2..], out var mid) && mid > 0)
            {
                return db.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND MemberId = @2", owner.P, owner.C, mid);
            }
            var name = personKey.StartsWith("n:", StringComparison.Ordinal) ? personKey[2..] : personKey;
            return db.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND MemberId IS NULL", owner.P, owner.C)
                .Where(r => string.Equals((r.DisplayName ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// Point every assignment a person holds at a pistol.nu member — or hand them all to a different
        /// person entirely.
        ///
        /// <para><b>Link vs replace is not cosmetic.</b> Linking says "this free-text row IS that member":
        /// same human, so an Accepted answer and a check-in still stand. Replacing says "someone else does
        /// this now": their predecessor's yes means nothing, so statuses reset to Planerad and check-ins
        /// clear. Treating them the same is how a stand-in silently inherits a confirmation they never gave.</para>
        /// </summary>
        public PersonActionResult ApplyPersonIdentity(PersonActionRequest req, StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rows = RowsForPerson(db, owner, req.PersonKey);
            if (rows.Count == 0) return new PersonActionResult { Success = false, Message = "Hittade inga uppdrag för personen." };

            string? name = string.IsNullOrWhiteSpace(req.NewName) ? null : req.NewName.Trim();
            int? memberId = req.MemberId > 0 ? req.MemberId : null;
            string? phone = string.IsNullOrWhiteSpace(req.Phone) ? null : req.Phone.Trim();

            if (memberId is int mid)
            {
                var m = _memberService.GetById(mid);
                if (m == null) return new PersonActionResult { Success = false, Message = "Medlemmen hittades inte." };
                // The member's own name wins — that is the whole point of linking, and it is what the
                // functionary will see in their personal schedule.
                name ??= ResolveMemberName(mid);
                if (phone == null && m.HasProperty("phoneNumber")) phone = m.GetValue<string>("phoneNumber");
            }

            var now = DateTime.UtcNow;
            var n = 0;
            foreach (var r in rows)
            {
                // Keep the first-ever typed name before anything overwrites it, so the change is visible
                // and reversible. Set once — a second rename must not erase the original.
                var changesName = (name != null && !string.Equals(name, r.DisplayName, StringComparison.Ordinal))
                                  || (memberId != null && r.MemberId != memberId);
                if (changesName && string.IsNullOrEmpty(r.OriginalName)) r.OriginalName = r.DisplayName;

                if (req.IsReplacement)
                {
                    // ⚠️ Omgång 3 (Expertgruppen K3): a replacement is a DIFFERENT human — the whole identity goes, not
                    // just the name. Keeping the old MemberId left the work on the sick person's account (their
                    // schedule, their notifications, their "Tacka ja"); keeping Email/Phone sent the request and the
                    // day-of phone number to the wrong person. Null = the member's own (CompetitionPeopleService).
                    r.MemberId = memberId;
                    r.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
                    r.Phone = phone;
                    r.RepresentsClubId = null; r.RepresentsRegionId = null;
                    r.Status = StaffAssignmentStatus.Planned;
                    r.CheckedInAt = null;
                    // App access was granted to a login, not to a slot on the roster.
                    r.HasAdminAccess = false;
                    // Links already sent to the previous person stop working (StaffingInviteToken v2).
                    r.InviteNonce = Guid.NewGuid();
                }
                else
                {
                    if (memberId != null) r.MemberId = memberId;
                    if (phone != null) r.Phone = phone;
                    if (!string.IsNullOrWhiteSpace(req.Email)) r.Email = req.Email.Trim();
                }
                if (name != null) r.DisplayName = name;
                // Renamed back to what it was → there is nothing left to undo, so don't keep claiming
                // "hette X i planen" about a name that is X.
                if (string.Equals(r.OriginalName, r.DisplayName, StringComparison.Ordinal)) r.OriginalName = null;
                r.ModifiedDate = now;
                db.Update(r);
                n++;
            }

            // A replacement or a link can change who holds Tävlingsledning, and that list is also the
            // public "Tävlingsansvariga" block on the competition page.
            if (owner.HasCompetition) { try { SyncCompetitionManagers(owner.Comp); } catch { } }

            return new PersonActionResult { Success = true, Affected = n };
        }

        /// <summary>
        /// Grant or revoke the right to manage this competition in pistol.nu — for the PERSON, across every
        /// assignment they hold.
        ///
        /// <para>The flag is stored per assignment, but it was never read that way: <see
        /// cref="HasRosterAdminAccess"/> asks whether the member has <i>any</i> row with it set, and
        /// <see cref="SyncCompetitionManagers"/> distincts by member. So ticking it on one of five rows
        /// already granted access to the whole competition, and unticking it on that row left the access in
        /// place via the other four — a switch that silently did nothing. Access is to the competition, so
        /// it belongs to the person; this writes every row so the storage finally agrees with the meaning.</para>
        ///
        /// <para>Requires a member id: access is granted to a LOGIN, and a free-text helper has none.</para>
        /// </summary>
        public PersonActionResult SetPersonAdminAccess(StaffOwner owner, string personKey, bool grant)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rows = RowsForPerson(db, owner, personKey);
            if (rows.Count == 0)
                return new PersonActionResult { Success = false, Message = "Hittade inga uppdrag för personen." };
            // P2b, Stefans svar 2: behörigheten gäller den tävling planeringen är kopplad till.
            if (grant && !owner.HasCompetition)
                return new PersonActionResult
                {
                    Success = false,
                    Message = "Planeringen är inte kopplad till någon tävling än. Behörigheten gäller den tävling planeringen kopplas till — koppla den först.",
                };
            if (grant && !rows.Any(r => r.MemberId is > 0))
                return new PersonActionResult
                {
                    Success = false,
                    Message = "Personen saknar konto på pistol.nu. Koppla den till en medlem först — behörigheten ges till en inloggning."
                };

            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                var wanted = grant && r.MemberId is > 0;
                if (r.HasAdminAccess == wanted) continue;
                r.HasAdminAccess = wanted;
                r.ModifiedDate = now;
                db.Update(r);
            }

            if (owner.HasCompetition) SyncCompetitionManagers(owner.Comp);
            return new PersonActionResult { Success = true, Affected = rows.Count };
        }

        /// <summary>
        /// Put back the name the organiser originally typed, and unlink. The escape hatch for a link that
        /// went to the wrong person — which is easy to do and, without this, impossible to notice or reverse.
        /// </summary>
        /// <summary>
        /// P3 steg E (beslut 9.6): vilken förening personen ställer upp för — klubb ELLER krets, eller förvalet
        /// (båda null = medlemmens huvudklubb). Gäller PERSONEN, så alla hens rader skrivs (samma skäl som
        /// behörigheten). Anroparen prövar att föreningen är ett av personens val.
        /// </summary>
        public PersonActionResult SetPersonRepresents(StaffOwner owner, string personKey, int? clubId, int? regionId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rows = RowsForPerson(db, owner, personKey);
            if (rows.Count == 0)
                return new PersonActionResult { Success = false, Message = "Hittade inga uppdrag för personen." };
            var club = clubId > 0 ? clubId : null;
            var region = club.HasValue ? null : (regionId > 0 ? regionId : null);
            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                if (r.RepresentsClubId == club && r.RepresentsRegionId == region) continue;
                r.RepresentsClubId = club; r.RepresentsRegionId = region; r.ModifiedDate = now;
                db.Update(r);
            }
            return new PersonActionResult { Success = true, Affected = rows.Count };
        }

        public PersonActionResult UndoPersonIdentity(StaffOwner owner, string personKey)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rows = RowsForPerson(db, owner, personKey).Where(r => !string.IsNullOrEmpty(r.OriginalName)).ToList();
            if (rows.Count == 0)
                return new PersonActionResult { Success = false, Message = "Det finns inget tidigare namn att gå tillbaka till." };

            var now = DateTime.UtcNow;
            foreach (var r in rows)
            {
                r.DisplayName = r.OriginalName!;
                r.OriginalName = null;
                r.MemberId = null;
                r.HasAdminAccess = false;   // access was granted to a login that is no longer attached
                r.ModifiedDate = now;
                db.Update(r);
            }
            if (owner.HasCompetition) { try { SyncCompetitionManagers(owner.Comp); } catch { } }
            return new PersonActionResult { Success = true, Affected = rows.Count };
        }

        /// <summary>
        /// Split a timed assignment at a clock time and hand the remainder to someone else — "han blev sjuk
        /// en timme in i passet". Doing it by hand is two edits with nothing tying them together, and it is
        /// easy to leave the tail uncovered without noticing.
        /// <para>Refuses on an untimed row: there is no point to split at, and inventing one would invent a
        /// time the person never agreed to.</para>
        /// </summary>
        public (bool Ok, string? Message, int NewId) SplitAssignment(int assignmentId, StaffOwner owner,
            TimeSpan at, int? newMemberId, string? newName, int byMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefault<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), assignmentId, owner.P, owner.C);
            if (a == null) return (false, "Uppdraget hittades inte.", 0);
            if (a.StartsAt == null || a.EndsAt == null)
                return (false, "Passet saknar tider. Sätt från- och till-tid först, annars finns ingen punkt att dela vid.", 0);

            var day = a.StartsAt.Value.Date;
            var cut = day.Add(at);
            if (cut <= a.StartsAt.Value || cut >= a.EndsAt.Value)
                return (false, $"Tiden måste ligga inuti passet ({a.StartsAt:HH\\:mm}–{a.EndsAt:HH\\:mm}).", 0);

            var tailEnd = a.EndsAt.Value;
            var now = DateTime.UtcNow;

            string? name = string.IsNullOrWhiteSpace(newName) ? null : newName.Trim();
            string? phone = null;
            if (newMemberId is int mid && mid > 0)
            {
                var m = _memberService.GetById(mid);
                if (m == null) return (false, "Medlemmen hittades inte.", 0);
                name ??= ResolveMemberName(mid);
                if (m.HasProperty("phoneNumber")) phone = m.GetValue<string>("phoneNumber");
            }
            if (string.IsNullOrWhiteSpace(name))
                return (false, "Ange vem som tar över resten av passet.", 0);

            a.EndsAt = cut;
            a.ModifiedDate = now;
            db.Update(a);

            var tail = new StaffAssignment
            {
                PlanningId = owner.PlanningIdOrNull,
                CompetitionId = owner.CompetitionIdOrNull,
                MemberId = newMemberId is > 0 ? newMemberId : null,
                DisplayName = name!,
                Phone = phone,
                RoleKey = a.RoleKey,
                FunctionTitle = a.FunctionTitle,
                ScopeType = a.ScopeType,
                ScopeKey = a.ScopeKey,
                TargetFrom = a.TargetFrom,
                TargetTo = a.TargetTo,
                StartsAt = cut,
                EndsAt = tailEnd,
                PassId = a.PassId,
                IsResponsible = false,
                HasAdminAccess = false,
                // A stand-in has not agreed to anything yet.
                Status = StaffAssignmentStatus.Planned,
                Note = a.Note,
                AssignedByMemberId = byMemberId,
                CreatedDate = now,
                ModifiedDate = now,
            };
            tail.Id = Convert.ToInt32(db.Insert(tail));
            return (true, null, tail.Id);
        }

        /// <summary>
        /// Set contact details on an existing roster row. Used by the grid to rescue an account-less helper
        /// from being unreachable — with an e-mail they can at least be invited via a tokened link.
        /// </summary>
        public void SetContact(int assignmentId, StaffOwner owner, string? email, string? phone)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET Email = @0, Phone = @1, ModifiedDate = @2 WHERE Id = @3 AND " + StaffOwner.Where(4),
                email, phone, DateTime.UtcNow, assignmentId, owner.P, owner.C);
        }

        /// <summary>Resolve display info for the external-invite page (comp name, role, scope, shift, status).</summary>
        public InviteResponseModel? GetInviteInfo(int assignmentId)
        {
            var a = GetById(assignmentId);
            if (a == null) return null;
            var owner = _owners.OfRow(a.PlanningId, a.CompetitionId);
            if (owner == null) return null;
            var compName = _owners.Name(owner);
            var discipline = _owners.Discipline(owner);
            var role = _roles.Resolve(owner, discipline, a.RoleKey);
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
        /// <summary>
        /// "Under tävlingen" på planeringens deltagarlista: per medlem rollerna i bemanningen, med dagarna
        /// ("Kassa (lör 19 aug, sön 20 aug)"). Avböjda räknas inte. Läser bara.
        /// </summary>
        public Dictionary<int, List<string>> DutiesByMember(StaffOwner owner)
        {
            var result = new Dictionary<int, List<string>>();
            List<DutyRow> rows;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                rows = scope.Database.Fetch<DutyRow>(@"
                    SELECT sa.MemberId, sa.RoleKey, COALESCE(CAST(sa.StartsAt AS DATE), sa.DayDate, CAST(sp.PassDate AS DATE)) AS Day
                    FROM StaffAssignment sa LEFT JOIN StaffPass sp ON sp.Id = sa.PassId
                    WHERE " + owner.Filter(0, "sa.") + @" AND sa.MemberId > 0 AND ISNULL(sa.Status, '') <> @2",
                    owner.P, owner.C, StaffAssignmentStatus.Declined);
            }
            if (rows.Count == 0) return result;
            var names = _roles.For(owner, _owners.Discipline(owner))
                .ToDictionary(r => r.Key, r => r.DisplayName, StringComparer.OrdinalIgnoreCase);
            var sv = CultureInfo.GetCultureInfo("sv-SE");
            foreach (var m in rows.GroupBy(r => r.MemberId))
            {
                result[m.Key] = m.GroupBy(r => r.RoleKey ?? "", StringComparer.OrdinalIgnoreCase)
                    .Select(g =>
                    {
                        var name = names.TryGetValue(g.Key, out var n) ? n : g.Key;
                        var days = g.Where(r => r.Day.HasValue).Select(r => r.Day!.Value.Date).Distinct().OrderBy(d => d)
                            .Select(d => d.ToString("ddd d MMM", sv)).ToList();
                        return days.Count > 0 ? $"{name} ({string.Join(", ", days)})" : name;
                    })
                    .OrderBy(s => s, StringComparer.Create(sv, true)).ToList();
            }
            return result;
        }

        private class DutyRow
        {
            public int MemberId { get; set; }
            public string? RoleKey { get; set; }
            public DateTime? Day { get; set; }
        }

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

            // P2b: grupperat per ägare — en planering utan tävling är en egen grupp, en kopplad planering
            // och dess tävling är samma grupp.
            string KeyOf(int? p, int? c) => p is > 0 ? "p" + p : "c" + c;
            var keys = rows.Select(r => (r.PlanningId, r.CompetitionId)).Concat(avail.Select(a => (a.PlanningId, a.CompetitionId)))
                .GroupBy(x => KeyOf(x.PlanningId, x.CompetitionId)).Select(g => g.First()).ToList();
            var availByComp = avail.GroupBy(a => KeyOf(a.PlanningId, a.CompetitionId)).ToDictionary(g => g.Key, g => g.ToList());
            var rowsByComp = rows.GroupBy(r => KeyOf(r.PlanningId, r.CompetitionId)).ToDictionary(g => g.Key, g => g.ToList());

            var result = new List<MyCompetitionGroup>();
            foreach (var (pid, cid) in keys)
            {
                var owner = _owners.OfRow(pid, cid);
                if (owner == null) continue;
                var key = KeyOf(pid, cid);
                var discipline = _owners.Discipline(owner);
                var (date, _) = _owners.Span(owner);
                if (owner.HasCompetition && _competitions.GetSaved(owner.Comp) == null) continue;
                var compId = owner.Comp;

                var grp = new MyCompetitionGroup
                {
                    CompetitionId = owner.Comp,
                    PlanningId = owner.PlanningId,
                    CompName = _owners.Name(owner),
                    CompDate = date is { } d && d != default ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
                    SignupPath = owner.SignupPath,
                };
                if (rowsByComp.TryGetValue(key, out var rs))
                {
                    grp.Assignments = rs
                        .OrderBy(r => r.RoleKey).ThenBy(r => r.ScopeKey, StringComparer.OrdinalIgnoreCase)
                        .Select(r => new MyAssignmentView
                        {
                            Id = r.Id,
                            RoleName = _roles.NameFor(owner, discipline, r.RoleKey),
                            FunctionTitle = r.FunctionTitle,
                            ScopeLabel = BuildScopeLabel(r),
                            ShiftLabel = BuildShiftLabel(r.StartsAt, r.EndsAt),
                            Status = r.Status,
                            IsResponsible = r.IsResponsible,
                        }).ToList();
                }
                if (availByComp.TryGetValue(key, out var av))
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
        private void ReconcileManagersIntoRoster(StaffOwner owner)
        {
            var competitionId = owner.Comp;
            try
            {
                var content = _competitions.GetSaved(competitionId);
                if (content == null) return;

                var json = content.CompetitionManagers ?? "[]";
                int[] managerIds;
                try { managerIds = JsonConvert.DeserializeObject<int[]>(json) ?? Array.Empty<int>(); }
                catch { managerIds = Array.Empty<int>(); }
                if (managerIds.Length == 0) return;

                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                // Expertgruppen (arkitekten, 2026-10-25): bara den planering som SJÄLV är kopplad till tävlingen får
                // tävlingsledarna. En del som ärver tävlingen (huvudplaneringens, "Bemannar för") fick dem annars — den som
                // råkade laddas först.
                if (owner.HasPlanning && db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition' AND TargetId = @1",
                        owner.PlanningId, competitionId) == 0)
                    return;
                var existing = db.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND RoleKey = @2",
                    owner.P, owner.C, TavlingsledningRole);
                var haveMemberIds = existing.Where(r => r.MemberId.HasValue).Select(r => r.MemberId!.Value).ToHashSet();

                foreach (var mid in managerIds.Distinct())
                {
                    if (mid <= 0 || haveMemberIds.Contains(mid)) continue;
                    var name = ResolveMemberName(mid);
                    var now = DateTime.UtcNow;
                    // INSERT ... WHERE NOT EXISTS, not read-then-Insert. The planning page loads the roster
                    // and the people list in parallel and BOTH call this; with a separate read both saw
                    // "missing" and both inserted, so the tävlingsledare appeared twice. One statement makes
                    // the check and the write atomic.
                    db.Execute(@"
                        INSERT INTO StaffAssignment
                            (CompetitionId, PlanningId, MemberId, DisplayName, RoleKey, FunctionTitle, ScopeType,
                             IsResponsible, HasAdminAccess, Status, AssignedByMemberId, CreatedDate, ModifiedDate)
                        SELECT @0, @8, @1, @2, @3, @4, @5, 1, 1, @6, 0, @7, @7
                        WHERE NOT EXISTS (
                            SELECT 1 FROM StaffAssignment
                            WHERE CompetitionId = @0 AND RoleKey = @3 AND MemberId = @1)",
                        competitionId, mid, name, TavlingsledningRole, "Tävlingsledare",
                        StaffScopeType.All, StaffAssignmentStatus.Confirmed, now, owner.PlanningIdOrNull);
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
                // Genom tävlingslagringen (C3-planen S5).
                var content = _competitions.GetSaved(competitionId);
                if (content == null) return;

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

                var currentJson = content.CompetitionManagers ?? "[]";
                int[] current;
                try { current = JsonConvert.DeserializeObject<int[]>(currentJson) ?? Array.Empty<int>(); }
                catch { current = Array.Empty<int>(); }

                if (current.OrderBy(x => x).SequenceEqual(wanted)) return; // no change → don't republish

                var saved = _competitions.Update(competitionId, null, c => c.CompetitionManagers = JsonConvert.SerializeObject(wanted));
                if (saved.PublishProblem != null)
                    _logger.LogWarning("Staffing: competitionManagers for competition {CompetitionId}: {Problem}", competitionId, saved.PublishProblem);
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
                // Genom tävlingslagringen (C3-planen S5).
                var content = _competitions.GetSaved(competitionId);
                if (content == null) return;
                var type = content.CompetitionType ?? "";
                if (!FunctionaryRoles.FaltFamily.Contains(type, StringComparer.OrdinalIgnoreCase)) return;

                var json = content.FaltskytteStationManagers ?? "";
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
                var saved = _competitions.Update(competitionId, null, c => c.FaltskytteStationManagers = newJson);
                if (saved.PublishProblem != null)
                    _logger.LogWarning("Staffing: station managers for competition {CompetitionId}: {Problem}", competitionId, saved.PublishProblem);
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

        private StaffAssignmentView ToView(StaffAssignment a, StaffOwner owner, string? discipline)
        {
            var role = _roles.Resolve(owner, discipline, a.RoleKey);
            return new StaffAssignmentView
            {
                Id = a.Id,
                MemberId = a.MemberId,
                DisplayName = a.DisplayName,
                OriginalName = a.OriginalName,
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
                // StartsAt.Date → DayDate → (pass date, filled in by BuildRoster). One order, everywhere.
                DateKey = (a.StartsAt ?? a.DayDate)?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IsResponsible = a.IsResponsible,
                HasAdminAccess = a.HasAdminAccess,
                Status = a.Status,
                Note = a.Note,
                CheckedIn = a.CheckedInAt.HasValue,
                RepresentsClubId = a.RepresentsClubId,
                RepresentsRegionId = a.RepresentsRegionId,
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

        /// <summary>"yyyy-MM-dd" only — a day, never a time.</summary>
        private static DateTime? ParseDate(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var d) ? d.Date : null;
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
