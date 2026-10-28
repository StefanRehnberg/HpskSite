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
                owner.P, owner.FamilyComp);
        }

        /// <summary>Tävlingens bemanning — dess planerings. Tom när tävlingen inte har någon planering.</summary>
        public List<StaffAssignment> GetForCompetition(int competitionId) =>
            _owners.ForCompetition(competitionId) is { } owner ? GetForOwner(owner) : new List<StaffAssignment>();

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
        /// Steg 5 (B3): <b>det gemensammas behörighet</b>. Ett uppdrag med "Ge behörighet" i en huvudplanering utan egen tävling
        /// (<c>CompetitionId IS NULL</c>, planeringen är sin familjs rot) gäller varje tävling som är kopplad till en <i>del</i> i
        /// familjen. Parametrar: <c>@0</c> = tävlingen, <c>@1</c> = medlemmen.
        ///
        /// <para><b>Härledd, inte speglad:</b> inga rader skapas i delarnas tävlingar, så Koppla loss eller en borttagen del
        /// släcker behörigheten för just den tävlingen utan städning, och ingenting syns som Tävlingsansvarig (B5 —
        /// <see cref="SyncCompetitionManagers"/> läser bara tävlingens egna rader). Kopplingen är själva godkännandet:
        /// den kräver redan redigerarrätt i tävlingen (<c>PlanningController.LinkCompetition</c>). En ensam planering och en
        /// tävling utanför familjen berörs inte.</para>
        /// </summary>
        public const string FamilyGrantSql = @"EXISTS (
            SELECT 1 FROM StaffAssignment sa
              JOIN Planning r ON r.Id = sa.PlanningId AND r.DeletedAt IS NULL AND r.Id = r.FamilyRootId
             WHERE sa.MemberId = @1 AND sa.HasAdminAccess = 1 AND sa.CompetitionId IS NULL
               AND EXISTS (SELECT 1 FROM Planning f
                             JOIN PlanningLink l ON l.PlanningId = f.Id AND l.TargetKind = N'competition' AND l.TargetId = @0
                            WHERE f.FamilyRootId = r.FamilyRootId AND f.Id <> r.Id AND f.DeletedAt IS NULL))";

        /// <summary>
        /// Kan en rad i den här planeringen bära "Ge behörighet" för familjen? Ja för en huvudplanering utan tävling med minst en del.
        /// <para>(Ett gammalt "avböjt" fanns förut och gav ingen familjebehörighet; det finns inte längre — Stefan 2026-10-28.)</para>
        /// </summary>
        public bool CarriesFamilyGrant(StaffOwner owner)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return CarriesFamilyGrant(scope.Database, owner);
        }

        /// <summary>
        /// Planeringarna (delarna) där medlemmen har en ledarroll — skjutledare eller stationschef. Det är den
        /// rätt att hitta ersättare som <see cref="ReplacementAccessRules"/> ger "för sin del".
        /// </summary>
        public List<int> LedPlanningIds(int memberId)
        {
            if (memberId <= 0) return new List<int>();
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(@"SELECT DISTINCT a.PlanningId FROM StaffAssignment a JOIN Planning p ON p.Id = a.PlanningId AND p.DeletedAt IS NULL
                                               WHERE a.MemberId = @0 AND a.RoleKey IN (@1, @2)",
                memberId, ReplacementAccessRules.SkjutledareRole, ReplacementAccessRules.StationschefRole);
        }

        /// <summary>Kan en rad i den här planeringen bära "Ge behörighet" för familjen? Ja för en huvudplanering utan tävling med minst en del.</summary>
        private static bool CarriesFamilyGrant(NPoco.IDatabase db, StaffOwner owner) =>
            !owner.HasCompetition
            && db.ExecuteScalar<int>(@"SELECT COUNT(1) FROM Planning r JOIN Planning f ON f.FamilyRootId = r.FamilyRootId AND f.Id <> r.Id AND f.DeletedAt IS NULL
                                       WHERE r.Id = @0 AND r.Id = r.FamilyRootId AND r.DeletedAt IS NULL", owner.PlanningId) > 0;

        /// <summary>
        /// True when this member holds at least one roster row on this competition that was granted app
        /// access ("Ge behörighet att hantera tävlingen"). Role-agnostic on purpose: sekretariat, kassa,
        /// stationschef and tävlingsledning all reach the same management page, so the grant is a property
        /// of the assignment, not of the role. Status is NOT considered — the organiser ticking the box is
        /// the explicit grant; an unanswered or declined invitation doesn't silently revoke it.
        /// Returns false (never throws) if the table isn't there yet, so auth degrades to the legacy
        /// competitionManagers list rather than locking everyone out.
        /// </summary>
        public bool HasRosterAdminAccess(int competitionId, int memberId, bool includeFamilyGrant = true)
        {
            if (competitionId <= 0 || memberId <= 0) return false;
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                if (scope.Database.ExecuteScalar<int>(
                        "SELECT COUNT(1) FROM StaffAssignment WHERE CompetitionId = @0 AND MemberId = @1 AND HasAdminAccess = 1",
                        competitionId, memberId) > 0) return true;
                if (!includeFamilyGrant) return false;
                // Steg 5 (B3): det gemensammas behörighet gäller alla veckans tävlingar. Frågan körs bara när den första sagt nej
                // och börjar i IX_StaffAssignment_MemberId — personer utan behörighet i en familj betalar ett indexuppslag.
                return scope.Database.ExecuteScalar<int>("SELECT CASE WHEN " + FamilyGrantSql + " THEN 1 ELSE 0 END", competitionId, memberId) > 0;
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
                var passes = pscope.Database.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE " + owner.Filter(0), owner.P, owner.FamilyComp);
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
                        "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), req.Id, owner.P);
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
                        "SELECT COUNT(1) FROM StaffPass WHERE Id = @0 AND " + StaffOwner.Where(1), req.PassId, owner.P) == 0)
                    return 0;   // ett pass i en annan tävling
                row.PassId = req.PassId is > 0 ? req.PassId : null;
                row.IsResponsible = req.IsResponsible;
                // App access is role-agnostic (a Kassa- or Sekretariatsansvarig needs the same management
                // page as the tävlingsledare). It does require a linked member, though — access is granted
                // to a pistol.nu login, so a free-text helper row can never carry it.
                // ...och ges bara när planeringen är kopplad till en tävling (det finns ingen tävling att hantera annars).
                // Steg 5: ...eller när planeringen är en huvudplanering med delar — då gäller den veckans alla tävlingar (FamilyGrantSql).
                row.HasAdminAccess = req.HasAdminAccess && row.MemberId is > 0 && (owner.HasCompetition || CarriesFamilyGrant(db, owner));
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

        /// <summary>Lightweight status update without a full save.</summary>
        public void SetStatus(int id, StaffOwner owner, string status)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET Status = @0, ModifiedDate = @1 WHERE Id = @2 AND " + StaffOwner.Where(3),
                NormalizeStatus(status), DateTime.UtcNow, id, owner.P);
        }

        /// <summary>
        /// Meddela: märker raderna som meddelade — villkorat i SQL, så att ett dubbelklick (eller två flikar) bara kan ta varje rad en gång.
        /// Returnerar de rader som det här anropet tog (de som inte redan var meddelade). Statusen rörs inte: en placering tilldelas, den begärs inte.
        /// </summary>
        public List<int> ClaimNotified(IReadOnlyCollection<int> ids)
        {
            var claimed = new List<int>();
            if (ids.Count == 0) return claimed;
            var now = DateTime.UtcNow;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            foreach (var id in ids)
                if (scope.Database.Execute(
                        "UPDATE StaffAssignment SET NotifiedAt = @0 WHERE Id = @1 AND NotifiedAt IS NULL", now, id) > 0)
                    claimed.Add(id);
            return claimed;
        }

        /// <summary>Tar tillbaka märket när inget utskick nådde personen (ingen e-post eller push), så att raden kan meddelas igen.</summary>
        public void UnclaimNotified(IReadOnlyCollection<int> ids)
        {
            if (ids.Count == 0) return;
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute("UPDATE StaffAssignment SET NotifiedAt = NULL WHERE Id IN (@ids)", new { ids = ids.ToList() });
        }

        public void Delete(int id, StaffOwner owner)
        {
            string? roleKey;
            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                roleKey = scope.Database.ExecuteScalar<string>("SELECT RoleKey FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P);
                scope.Database.Execute("DELETE FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), id, owner.P);
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
            // Steg 4–6 H4: tävlingens tävlingsansvariga (ur redigeringsmodalen) blir rader först när bemanningen öppnas. Kopplas en
            // planering in och ingen hunnit öppna den skrev speglingen annars [] över listan. Läs in dem som rader FÖRE speglingen.
            try
            {
                int? pid;
                using (var scope = _scopeProvider.CreateScope(autoComplete: true))
                    pid = scope.Database.ExecuteScalar<int?>(
                        "SELECT TOP 1 PlanningId FROM PlanningLink WHERE TargetKind = N'competition' AND TargetId = @0", competitionId);
                if (pid is > 0) ReconcileManagersIntoRoster(new StaffOwner(pid.Value, competitionId));
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: reconcile before mirror failed for competition {CompetitionId}", competitionId); }
            SyncCompetitionManagers(competitionId);
            SyncStationManagers(competitionId);
        }

        // ---- day-of cockpit: roll-call / upprop (+ overlay hook) ----

        public void SetCheckedIn(int id, StaffOwner owner, bool checkedIn)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET CheckedInAt = @0, ModifiedDate = @1 WHERE Id = @2 AND " + owner.Filter(3),
                checkedIn ? DateTime.UtcNow : (DateTime?)null, DateTime.UtcNow, id, owner.P, owner.FamilyComp);
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
                var src = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND PassId = @1", owner.P, fromPassId);
                var dst = db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND PassId = @1", owner.P, toPassId);
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
                "SELECT * FROM StaffAvailability WHERE " + StaffOwner.Where(0) + " ORDER BY MemberId, AvailableFrom", owner.P);
        }

        public bool MemberHasAssignment(StaffOwner owner, int memberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND MemberId = @1", owner.P, memberId) > 0;
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
                "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), assignmentId, owner.P);
            if (a == null) return (false, "Placeringen hittades inte.");

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

        /// <summary>
        /// Steg 2 (Stefan B13, "Flytta raden till en annan del…"): ett uppdrag byter del och behåller sitt id, sin roll, sin
        /// dag och sin tid — ett nytt id vore en annan rad för svarslänkar, påminnelser och kalenderfiler. Anroparen har
        /// prövat rätten i bägge delarna, att de hör till samma familj och att rollen finns i målet.
        /// <list type="bullet">
        /// <item>Passet hör till delen: raden släpper det och behåller dagen (<c>DayDate</c> ur passet när raden saknar egen
        /// dag eller tid) — en dag, aldrig en gissad klocka.</item>
        /// <item>Byter raden tävling upphör behörigheten i tävlingen (B7): den gällde tävlingen raden hade.</item>
        /// <item>Samma person med samma roll, plats och pass i målet (unikt villkor) → ett nej, ingenting skrivs.</item>
        /// </list>
        /// </summary>
        public (bool Ok, string? Message, bool RevokedAccess) MoveToPlanning(int assignmentId, StaffOwner from, StaffOwner to)
        {
            if (from.PlanningId == to.PlanningId) return (true, null, false);
            using var scope = _scopeProvider.CreateScope(autoComplete: false);
            var db = scope.Database;
            var a = db.SingleOrDefault<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), assignmentId, from.P);
            if (a == null) return (false, "Placeringen hittades inte.", false);

            DateTime? dayFromPass = null;
            if (a.PassId is int pid)
                dayFromPass = db.ExecuteScalar<DateTime?>("SELECT PassDate FROM StaffPass WHERE Id = @0", pid);
            var dayDate = a.StartsAt == null ? (a.DayDate ?? dayFromPass) : null;

            // Steg 4–6 H3: behörigheten gällde raden där den låg — den följer aldrig med till en annan del. (Förut bara när tävlingen
            // byttes; en flagga på ett tävlingslöst gemensamt uppdrag flyttades då med och vaknade när delen kopplades.)
            var revoke = a.HasAdminAccess;
            try
            {
                db.Execute(@"UPDATE StaffAssignment SET PlanningId = @0, CompetitionId = @1, PassId = NULL, DayDate = @2,
                        HasAdminAccess = CASE WHEN @3 = 1 THEN 0 ELSE HasAdminAccess END, ModifiedDate = @4 WHERE Id = @5",
                    to.PlanningId, to.CompetitionIdOrNull, dayDate, revoke ? 1 : 0, DateTime.UtcNow, assignmentId);
                scope.Complete();
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            {
                return (false, "Samma person är redan placerad där i den delen.", false);
            }
            return (true, null, revoke);
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
                    "SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND MemberId = @1", owner.P, mid);
            }
            var name = personKey.StartsWith("n:", StringComparison.Ordinal) ? personKey[2..] : personKey;
            return db.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND MemberId IS NULL", owner.P)
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
            if (rows.Count == 0) return new PersonActionResult { Success = false, Message = "Hittade inga placeringar för personen." };

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
                    r.NotifiedAt = null;   // den nya personen har inte fått besked
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
                return new PersonActionResult { Success = false, Message = "Hittade inga placeringar för personen." };
            // P2b, Stefans svar 2: behörigheten gäller den tävling planeringen är kopplad till.
            if (grant && !owner.HasCompetition && !CarriesFamilyGrant(db, owner))
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
                return new PersonActionResult { Success = false, Message = "Hittade inga placeringar för personen." };
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
        /// Steg 6b ("Hitta ersättare"): lämnar <b>ett</b> uppdrag över till en annan medlem. Radens id, dag, tid och roll behålls —
        /// det är samma uppdrag, så kalenderfiler, krockar och historiken följer med. Allt som hörde till personen går: konto,
        /// e-post, telefon, förening, incheckning och behörighet (samma skäl som vid en ersättare, omgång 3 K3). Den nya personen
        /// meddelas (NotifiedAt sätts, statusen blir Planned) och får en ny länk; den tidigare personens namn sparas i <c>OriginalName</c>.
        /// </summary>
        public (bool Ok, string? Message, StaffAssignment? Row, string? OldName, int? OldMemberId) ReplaceHolder(int assignmentId, StaffOwner owner, int newMemberId)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var a = db.SingleOrDefault<StaffAssignment>(
                "SELECT * FROM StaffAssignment WHERE Id = @0 AND " + StaffOwner.Where(1), assignmentId, owner.P);
            if (a == null) return (false, "Placeringen hittades inte.", null, null, null);
            if (a.MemberId == newMemberId) return (false, "Det är redan den personens plats.", null, null, null);
            var m = _memberService.GetById(newMemberId);
            if (m == null) return (false, "Medlemmen hittades inte.", null, null, null);

            var oldName = a.DisplayName; var oldMemberId = a.MemberId;
            var hadAccess = a.HasAdminAccess;
            if (string.IsNullOrEmpty(a.OriginalName)) a.OriginalName = a.DisplayName;
            a.MemberId = newMemberId;
            a.DisplayName = ResolveMemberName(newMemberId);
            a.Email = null; a.Phone = null;                       // null = medlemmens egna (CompetitionPeopleService)
            a.RepresentsClubId = null; a.RepresentsRegionId = null;
            a.Status = StaffAssignmentStatus.Planned;
            a.NotifiedAt = DateTime.UtcNow;                       // den nya personen meddelas av anroparen (StaffReplacementService.NotifyAsync)
            a.CheckedInAt = null;
            a.HasAdminAccess = false;
            a.InviteNonce = Guid.NewGuid();                       // länkar till den förra personen slutar gälla
            a.ModifiedDate = DateTime.UtcNow;
            if (string.Equals(a.OriginalName, a.DisplayName, StringComparison.Ordinal)) a.OriginalName = null;
            try { db.Update(a); }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            {
                return (false, "Den personen är redan placerad där.", null, null, null);   // unikt villkor: samma person, roll, plats och pass
            }
            _logger.LogInformation("Staffing: uppdrag {Id} lämnat över från {Old} till medlem {New}", a.Id, oldName, newMemberId);

            if (hadAccess && owner.HasCompetition) { try { SyncCompetitionManagers(owner.Comp); } catch { } }
            return (true, null, a, oldName, oldMemberId);
        }

        /// <summary>
        /// Skiftschemat för en ruta (roll × station × dag): ändrar tiderna på befintliga placeringar, lägger till nya skift och tar bort skift — i EN
        /// transaktion, så att en gräns som flyttas mellan två skift aldrig lämnar det ena halvt ändrat (Stefan 2026-10-27: "Dela passet" på personens
        /// sida felade och gick inte att hitta; schemat byggs i stället för rutan). Allt prövas före första skrivningen.
        /// <para>Befintliga rader byter bara TID här; en annan person på ett befintligt skift går genom Hitta ersättare, som aviserar båda. Skiften får
        /// överlappa — behövs två samtidigt är det två på samma tid. Ändrade tider på den som redan fått besked (NotifiedAt, självanmäld eller bekräftad) lämnas
        /// tillbaka så att anroparen kan ge besked.</para>
        /// </summary>
        public CellShiftsResult SaveCellShifts(StaffOwner owner, SaveCellShiftsRequest req, int byMemberId)
        {
            if (string.IsNullOrWhiteSpace(req.RoleKey)) return CellShiftsResult.Fail("Roll saknas.");
            if (!DateTime.TryParseExact(req.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
                return CellShiftsResult.Fail("Dagen går inte att läsa.");
            var scopeType = string.IsNullOrWhiteSpace(req.ScopeType) ? null : req.ScopeType.Trim();
            var scopeKey = string.IsNullOrWhiteSpace(req.ScopeKey) ? null : req.ScopeKey.Trim();

            // Tider: båda tomma = hela dagen; annars båda, och slutet efter början.
            (DateTime? Start, DateTime? End)? Times(string? from, string? to, out string? error)
            {
                error = null;
                var f = (from ?? "").Trim(); var t = (to ?? "").Trim();
                if (f.Length == 0 && t.Length == 0) return (null, null);
                if (f.Length == 0 || t.Length == 0) { error = "Skriv både från och till, eller lämna båda tomma för hela dagen."; return null; }
                if (!HpskSite.Models.ClockTime.TryNormalize(f, out var fh) || !HpskSite.Models.ClockTime.TryNormalize(t, out var th) || fh == null || th == null)
                { error = "Klockslagen går inte att läsa. Skriv till exempel 8 eller 8:30."; return null; }
                var a = day.Date + HpskSite.Models.ClockTime.ToTimeSpan(fh)!.Value; var b = day.Date + HpskSite.Models.ClockTime.ToTimeSpan(th)!.Value;
                if (b <= a) { error = $"Sluttiden ({th}) måste vara efter starttiden ({fh})."; return null; }
                return (a, b);
            }

            var upserts = req.Shifts ?? new();
            var deletes = (req.DeleteIds ?? new()).Distinct().ToList();
            var planned = new List<(CellShiftItem Item, DateTime? Start, DateTime? End)>();
            foreach (var it in upserts)
            {
                // Ett befintligt skift utan From och To alls (null, inte tomt) behåller sina tider — bara personen byts.
                if (it.Id > 0 && it.From == null && it.To == null) { planned.Add((it, null, null)); continue; }
                var t = Times(it.From, it.To, out var err);
                if (t == null) return CellShiftsResult.Fail(err!);
                if (it.Id <= 0 && it.MemberId <= 0 && string.IsNullOrWhiteSpace(it.DisplayName))
                    return CellShiftsResult.Fail("Välj eller skriv en person för varje nytt skift.");
                if (it.Id <= 0 && it.MemberId <= 0 && !string.IsNullOrWhiteSpace(it.Email) && !LooksLikeEmail(it.Email))
                    return CellShiftsResult.Fail($"E-postadressen till {(it.DisplayName ?? "personen").Trim()} ser inte rätt ut.");
                planned.Add((it, t.Value.Start, t.Value.End));
            }
            if (upserts.Where(u => u.Id > 0).Select(u => u.Id).Intersect(deletes).Any())
                return CellShiftsResult.Fail("Ett skift kan inte både ändras och tas bort.");

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var ids = upserts.Where(u => u.Id > 0).Select(u => u.Id).Concat(deletes).Distinct().ToList();
            var rows = ids.Count == 0 ? new List<StaffAssignment>()
                : db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE Id IN (@ids) AND " + StaffOwner.Where(1), new { ids }, owner.P);
            if (rows.Count != ids.Count) return CellShiftsResult.Fail("Ett skift finns inte längre. Ladda om sidan.");
            var passDates = rows.Where(r => r.PassId is > 0).Select(r => r.PassId!.Value).Distinct().ToList();
            var passDay = passDates.Count == 0 ? new Dictionary<int, DateTime>()
                : db.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE Id IN (@ids)", new { ids = passDates }).ToDictionary(p => p.Id, p => p.PassDate.Date);
            foreach (var r in rows)
            {
                var rday = r.StartsAt?.Date ?? r.DayDate?.Date ?? (r.PassId is > 0 && passDay.TryGetValue(r.PassId.Value, out var pd) ? pd : (DateTime?)null);
                var sameScope = string.Equals(NormScope(r.ScopeType), NormScope(scopeType), StringComparison.OrdinalIgnoreCase) && (r.ScopeKey ?? "") == (scopeKey ?? "");
                if (!string.Equals(r.RoleKey, req.RoleKey, StringComparison.OrdinalIgnoreCase) || !sameScope || rday != day.Date)
                    return CellShiftsResult.Fail("Ett skift hör inte till den här rutan.");
                if (r.PassId is > 0 && upserts.Any(u => u.Id == r.Id))
                    return CellShiftsResult.Fail("Tiden styrs av passet och ändras där.");
            }

            var now = DateTime.UtcNow;
            var changed = new List<ChangedShift>();
            var replaced = new List<ReplacedShift>();
            foreach (var (it, start, end) in planned)
            {
                if (it.Id > 0)
                {
                    var r = rows.Single(x => x.Id == it.Id);
                    var told = StaffAssignmentStatus.IsTold(r.Status, r.NotifiedAt);
                    var personChanged = it.MemberId > 0 && r.MemberId != it.MemberId;
                    if (personChanged && !told)
                    {
                        // Ingen har fått besked om det här skiftet än: personen byts rakt av, och ingen får något mejl förrän du meddelar.
                        var nm = _memberService.GetById(it.MemberId);
                        if (nm == null) return CellShiftsResult.Fail("Medlemmen hittades inte.");
                        if (string.IsNullOrEmpty(r.OriginalName)) r.OriginalName = r.DisplayName;
                        r.MemberId = it.MemberId; r.DisplayName = ResolveMemberName(it.MemberId);
                        r.Email = null; r.Phone = null; r.RepresentsClubId = null; r.RepresentsRegionId = null;
                        r.Status = StaffAssignmentStatus.Planned; r.NotifiedAt = null; r.CheckedInAt = null; r.HasAdminAccess = false; r.InviteNonce = Guid.NewGuid();
                        if (string.Equals(r.OriginalName, r.DisplayName, StringComparison.Ordinal)) r.OriginalName = null;
                        r.ModifiedDate = now;
                        db.Update(r);
                        personChanged = false;
                    }
                    if (!(it.From == null && it.To == null) && (r.StartsAt != start || r.EndsAt != end))
                    {
                        var before = r.StartsAt != null && r.EndsAt != null ? $"{r.StartsAt:HH\\:mm}–{r.EndsAt:HH\\:mm}" : "hela dagen";
                        r.StartsAt = start; r.EndsAt = end;
                        r.DayDate = start == null ? day.Date : null;
                        r.ModifiedDate = now;
                        db.Update(r);
                        changed.Add(new ChangedShift(r, before));
                    }
                    if (personChanged)
                    {
                        // Den som redan fått besked byts genom samma väg som Hitta ersättare: den nya personen meddelas direkt, båda får besked.
                        var rep = ReplaceHolder(r.Id, owner, it.MemberId);
                        if (!rep.Ok) return CellShiftsResult.Fail(rep.Message ?? "Personen kunde inte bytas.");
                        replaced.Add(new ReplacedShift(rep.Row!, rep.OldName, rep.OldMemberId));
                        changed.RemoveAll(c => c.Row.Id == r.Id);   // den nya personen får "du har fått ett uppdrag", inte "din tid har ändrats"
                    }
                    continue;
                }
                string? name = string.IsNullOrWhiteSpace(it.DisplayName) ? null : it.DisplayName.Trim();
                string? phone = null;
                if (it.MemberId > 0)
                {
                    var m = _memberService.GetById(it.MemberId);
                    if (m == null) return CellShiftsResult.Fail("Medlemmen hittades inte.");
                    name ??= ResolveMemberName(it.MemberId);
                    if (m.HasProperty("phoneNumber")) phone = m.GetValue<string>("phoneNumber");
                }
                if (string.IsNullOrWhiteSpace(name)) return CellShiftsResult.Fail("Välj eller skriv en person för varje nytt skift.");
                db.Insert(new StaffAssignment
                {
                    PlanningId = owner.PlanningIdOrNull,
                    CompetitionId = owner.CompetitionIdOrNull,
                    MemberId = it.MemberId > 0 ? it.MemberId : null,
                    DisplayName = name!,
                    Phone = phone,
                    Email = it.MemberId <= 0 && !string.IsNullOrWhiteSpace(it.Email) ? it.Email.Trim() : null,
                    RoleKey = req.RoleKey,
                    ScopeType = scopeType,
                    ScopeKey = scopeKey,
                    StartsAt = start, EndsAt = end,
                    DayDate = start == null ? day.Date : null,
                    // Ingen har sagt ja än: förfrågan skickas först när arrangören väljer det.
                    Status = StaffAssignmentStatus.Planned,
                    AssignedByMemberId = byMemberId,
                    CreatedDate = now, ModifiedDate = now,
                });
            }
            if (deletes.Count > 0)
                db.Execute("DELETE FROM StaffAssignment WHERE Id IN (@ids) AND " + StaffOwner.Where(1), new { ids = deletes }, owner.P);
            scope.Complete();
            return new CellShiftsResult(true, null, changed, replaced);
        }

        /// <summary>Enkel kontroll av en e-postadress som arrangören skriver in (frivillig uppgift): en adress med @ och en punkt i domänen.</summary>
        public static bool LooksLikeEmail(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            try { var a = new System.Net.Mail.MailAddress(s.Trim()); return a.Address == s.Trim() && a.Host.Contains('.'); }
            catch (FormatException) { return false; }
        }

        public sealed record ChangedShift(StaffAssignment Row, string Before);

        public sealed record ReplacedShift(StaffAssignment Row, string? OldName, int? OldMemberId);

        public sealed record CellShiftsResult(bool Ok, string? Message, List<ChangedShift> Changed, List<ReplacedShift> Replaced)
        {
            public static CellShiftsResult Fail(string message) => new(false, message, new(), new());
        }

        private static string? NormScope(string? t)
            => string.IsNullOrEmpty(t) || string.Equals(t, StaffScopeType.All, StringComparison.OrdinalIgnoreCase) ? null : t;

        /// <summary>
        /// Set contact details on an existing roster row. Used by the grid to rescue an account-less helper
        /// from being unreachable — with an e-mail they can at least be invited via a tokened link.
        /// </summary>
        public void SetContact(int assignmentId, StaffOwner owner, string? email, string? phone)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            scope.Database.Execute(
                "UPDATE StaffAssignment SET Email = @0, Phone = @1, ModifiedDate = @2 WHERE Id = @3 AND " + StaffOwner.Where(4),
                email, phone, DateTime.UtcNow, assignmentId, owner.P);
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

            // Alla personens placeringar i samma planering (den länkade först): mejlet listade dem, sidan ska göra det också.
            var mine = new List<StaffAssignment> { a };
            var passes = new Dictionary<int, StaffPass>();
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                var db = scope.Database;
                if (a.PlanningId is > 0 && (a.MemberId is > 0 || !string.IsNullOrWhiteSpace(a.Email)))
                {
                    var others = a.MemberId is > 0
                        ? db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE PlanningId = @0 AND MemberId = @1 AND Id <> @2", a.PlanningId, a.MemberId, a.Id)
                        : db.Fetch<StaffAssignment>("SELECT * FROM StaffAssignment WHERE PlanningId = @0 AND MemberId IS NULL AND LOWER(LTRIM(RTRIM(Email))) = @1 AND Id <> @2",
                            a.PlanningId, a.Email!.Trim().ToLowerInvariant(), a.Id);
                    mine.AddRange(others.OrderBy(o => o.StartsAt ?? o.DayDate ?? DateTime.MaxValue).ThenBy(o => o.Id));
                }
                var passIds = mine.Where(r => r.PassId is > 0).Select(r => r.PassId!.Value).Distinct().ToList();
                if (passIds.Count > 0)
                    passes = db.Fetch<StaffPass>("SELECT * FROM StaffPass WHERE Id IN (@ids)", new { ids = passIds }).ToDictionary(p => p.Id);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Staffing: placeringarna för sidan Din placering gick inte att läsa ({Id}).", assignmentId); }

            return new InviteResponseModel
            {
                Valid = true,
                CompName = compName,
                RoleName = role?.DisplayName ?? a.RoleKey,
                ScopeLabel = BuildScopeLabel(a),
                ShiftLabel = BuildShiftLabel(a.StartsAt, a.EndsAt),
                PersonName = a.DisplayName,
                Status = StaffAssignmentStatus.Normalize(a.Status),
                Placements = mine.Select(r => new InvitePlacement
                {
                    RoleName = _roles.Resolve(owner, discipline, r.RoleKey)?.DisplayName ?? r.RoleKey,
                    ScopeLabel = BuildScopeLabel(r),
                    When = PlacementNoticeService.WhenText(r, r.PassId is > 0 && passes.TryGetValue(r.PassId.Value, out var ps) ? ps : null),
                }).ToList(),
            };
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
                    WHERE " + owner.Filter(0, "sa.") + @" AND sa.MemberId > 0",
                    owner.P, owner.FamilyComp);
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
                            Status = StaffAssignmentStatus.Normalize(r.Status),
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
                if (db.ExecuteScalar<int>(
                        "SELECT COUNT(*) FROM PlanningLink WHERE PlanningId = @0 AND TargetKind = N'competition' AND TargetId = @1",
                        owner.PlanningId, competitionId) == 0)
                    return;
                var existing = db.Fetch<StaffAssignment>(
                    "SELECT * FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND RoleKey = @1",
                    owner.P, TavlingsledningRole);
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
                Status = StaffAssignmentStatus.Normalize(a.Status),
                NotifiedAt = a.NotifiedAt,
                Note = a.Note,
                CheckedIn = a.CheckedInAt.HasValue,
                RepresentsClubId = a.RepresentsClubId,
                RepresentsRegionId = a.RepresentsRegionId,
            };
        }

        internal static string BuildScopeLabel(StaffAssignment a)
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

        private static string NormalizeStatus(string? s) => StaffAssignmentStatus.Normalize(s);
    }
}
