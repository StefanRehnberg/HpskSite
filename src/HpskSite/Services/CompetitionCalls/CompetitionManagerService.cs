using HpskSite.Models.CompetitionCalls;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;
using HpskSite.Services.Staffing;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Cms.Core.Services;

namespace HpskSite.Services.CompetitionCalls
{
    /// <summary>
    /// Klubbprogrammet (F7): medlemmar <b>önskar</b> bli tävlingsledare, klubbens admin <b>utser</b>.
    ///
    /// <para><b>⚠️ Utseendet är en rosterrad</b> (<c>StaffAssignment</c>, <c>RoleKey='tavlingsledning'</c>, <c>HasAdminAccess=1</c>) skriven genom
    /// <see cref="StaffingService.Save"/> — samma plats som den befintliga väljaren, som speglar till <c>competitionManagers</c>. Rätten härleds ur rostern:
    /// tas raden bort försvinner rätten. Önskemålet (<see cref="CompetitionManagerWish"/>) är bara en förfrågan och bär ingen rättighet.</para>
    ///
    /// <para>Bemanningen ägs av en planering. Har tävlingen ingen startas dess planering först (som <c>Planning/StartForCompetition</c>), med utseendets
    /// utsedare som sammankallande. Går det inte svarar utseendet med ett fel — rätten får aldrig bli "sparad" utan att rosterraden finns.</para>
    ///
    /// <para>Alla skrivningar är villkorade satser (inte läs-ändra-skriv): dubbelklick och två admins kan inte skapa dubbletter.</para>
    /// </summary>
    public class CompetitionManagerService
    {
        private const string Role = "tavlingsledning";
        private readonly IScopeProvider _scopes;
        private readonly StaffingService _staffing;
        private readonly PlanningService _planning;
        private readonly HpskSite.Services.Competitions.ICompetitionStore _competitions;
        private readonly IMemberService _members;
        private readonly ILogger<CompetitionManagerService> _logger;

        public CompetitionManagerService(IScopeProvider scopes, StaffingService staffing, PlanningService planning,
            HpskSite.Services.Competitions.ICompetitionStore competitions, IMemberService members, ILogger<CompetitionManagerService> logger)
        {
            _scopes = scopes; _staffing = staffing; _planning = planning; _competitions = competitions; _members = members; _logger = logger;
        }

        private static DateTime Now => DateTime.Now;   // svensk tid, som kolumnerna

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public List<CompetitionManagerWish> WishesFor(IReadOnlyCollection<int> competitionIds)
        {
            if (competitionIds.Count == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<CompetitionManagerWish>("SELECT * FROM CompetitionManagerWish WHERE CompetitionId IN (@ids) ORDER BY CreatedAt", new { ids = competitionIds.ToList() });
        }

        public List<CompetitionManagerWish> WishesOfMember(int memberId, IReadOnlyCollection<int> competitionIds)
        {
            if (memberId <= 0 || competitionIds.Count == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<CompetitionManagerWish>("SELECT * FROM CompetitionManagerWish WHERE MemberId = @0 AND CompetitionId IN (@ids)", memberId, new { ids = competitionIds.ToList() });
        }

        public HashSet<int> ClosedAmong(IReadOnlyCollection<int> competitionIds)
        {
            if (competitionIds.Count == 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<int>("SELECT CompetitionId FROM CompetitionManagerClosure WHERE CompetitionId IN (@ids)", new { ids = competitionIds.ToList() }).ToHashSet();
        }

        /// <summary>Klubbarna medlemmen är aktiv i (medlemskapstabellen: annan status än Utträdd/Avliden).</summary>
        public HashSet<int> ClubsOfMember(int memberId)
        {
            if (memberId <= 0) return new();
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Fetch<int>(
                "SELECT ClubId FROM ClubMembership WHERE MemberId = @0 AND MembershipStatus NOT IN (N'Utträdd', N'Avliden')", memberId).ToHashSet();
        }

        /// <summary>Medlemmar med en tävlingsledarrad per tävling (ur rostern, den enda sanningen om rätten).</summary>
        public Dictionary<int, List<int>> ManagersByCompetition(IReadOnlyCollection<int> competitionIds)
        {
            var result = competitionIds.ToDictionary(i => i, _ => new List<int>());
            if (competitionIds.Count == 0) return result;
            using var s = _scopes.CreateScope(autoComplete: true);
            var rows = s.Database.Fetch<(int CompetitionId, int MemberId)>(
                "SELECT CompetitionId, MemberId FROM StaffAssignment WHERE RoleKey = @0 AND HasAdminAccess = 1 AND MemberId > 0 AND CompetitionId IN (@ids)",
                Role, new { ids = competitionIds.ToList() });
            foreach (var r in rows) if (result.TryGetValue(r.CompetitionId, out var l) && !l.Contains(r.MemberId)) l.Add(r.MemberId);
            return result;
        }

        // ── Medlemmens handlingar ──────────────────────────────────────────────────────────

        /// <summary>Önskar. Ett villkorat INSERT (unikt index) — ett dubbelklick ger en rad. Falskt = fanns redan.</summary>
        public bool Wish(int competitionId, int memberId)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Execute(@"
                INSERT INTO CompetitionManagerWish (CompetitionId, MemberId, Status, CreatedAt, UpdatedAt)
                SELECT @0, @1, @2, @3, @3
                WHERE NOT EXISTS (SELECT 1 FROM CompetitionManagerWish WHERE CompetitionId = @0 AND MemberId = @1)
                  AND NOT EXISTS (SELECT 1 FROM CompetitionManagerClosure WHERE CompetitionId = @0)",
                competitionId, memberId, CompetitionManagerWishStatus.Aktiv, now) > 0;
        }

        /// <summary>Ångrar. Rör bara medlemmens EGEN aktiva rad.</summary>
        public bool Unwish(int competitionId, int memberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.Execute("DELETE FROM CompetitionManagerWish WHERE CompetitionId = @0 AND MemberId = @1 AND Status = @2",
                competitionId, memberId, CompetitionManagerWishStatus.Aktiv) > 0;
        }

        // ── Adminens handlingar ────────────────────────────────────────────────────────────

        public sealed record AppointResult(List<int> Appointed, List<int> AlreadyManagers, string? Error, string? Warning = null);

        /// <summary>
        /// Utser. Varje medlem får en rosterrad (om den inte redan finns) och önskemålet blir Utsedd (skapas om medlemmen inte önskat).
        /// Bara medlemmar som är aktiva i tävlingens klubb — kontrollerat av anroparen mot <c>ClubRosterService</c>.
        /// </summary>
        public AppointResult Appoint(int competitionId, IReadOnlyCollection<int> memberIds, int byMemberId)
        {
            var ids = memberIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return new(new(), new(), "Välj minst en medlem.");
            if (ids.Count > CompetitionManagerRules.MaxAppointAtOnce) return new(new(), new(), $"Utse högst {CompetitionManagerRules.MaxAppointAtOnce} åt gången.");
            var comp = _competitions.GetSaved(competitionId);
            if (comp == null) return new(new(), new(), "Tävlingen finns inte.");

            var owner = EnsureOwner(comp, byMemberId, out var ownerError);
            if (owner == null) return new(new(), new(), ownerError);

            var appointed = new List<int>();
            var already = new List<int>();
            foreach (var mid in ids)
            {
                var exists = RosterRowId(owner, mid) > 0;
                if (exists) { already.Add(mid); MarkWish(competitionId, mid, CompetitionManagerWishStatus.Utsedd); continue; }
                var saved = _staffing.Save(new SaveStaffAssignmentRequest
                {
                    MemberId = mid,
                    DisplayName = MemberName(mid),
                    RoleKey = Role,
                    FunctionTitle = "Tävlingsledare",
                    ScopeType = StaffScopeType.All,
                    IsResponsible = true,
                    HasAdminAccess = true,
                    Status = StaffAssignmentStatus.Confirmed,
                }, owner, byMemberId);
                if (saved <= 0) { _logger.LogError("Tävling {Comp}: tävlingsledarraden för medlem {Member} sparades inte.", competitionId, mid); continue; }
                MarkWish(competitionId, mid, CompetitionManagerWishStatus.Utsedd);
                appointed.Add(mid);
            }
            if (appointed.Count == 0 && already.Count == 0) return new(appointed, already, "Utseendet kunde inte sparas. Försök igen om en stund.");

            // Spegling till tävlingens lista (StaffingService.SyncCompetitionManagers sväljer fel): kontrollera att den faktiskt gick fram, annars syns det.
            string? warning = null;
            try
            {
                var after = _competitions.GetSaved(competitionId);
                var missing = appointed.Where(m => after == null || !after.ManagerIds.Contains(m)).ToList();
                if (missing.Count > 0)
                {
                    warning = "Utseendet är sparat, men tävlingens lista över tävlingsledare uppdaterades inte. Öppna tävlingen och kontrollera att personen kommer åt den, eller försök igen.";
                    _logger.LogError("Tävling {Comp}: tävlingsledare {Members} finns i rostern men inte i tävlingens lista.", competitionId, string.Join(",", missing));
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Tävling {Comp}: kontrollen av tävlingens tävlingsledarlista misslyckades.", competitionId); }
            return new(appointed, already, null, warning);
        }

        /// <summary>
        /// Tar bort en tävlingsledare (adminen, eller medlemmen som tackar nej). Raden i rostern tas bort — då försvinner rätten — och önskemålet raderas
        /// (den som tackat nej ska inte stå som önskare). Returnerar om en rosterrad fanns.
        /// </summary>
        public bool RemoveManager(int competitionId, int memberId)
        {
            var owner = ExistingOwner(competitionId);
            var removed = false;
            if (owner != null)
            {
                var id = RosterRowId(owner, memberId);
                if (id > 0) { _staffing.Delete(id, owner); removed = true; }
            }
            // Tävlingsledare som bara finns i tävlingens egen lista (den gamla väljaren, ingen planering ännu) tas bort där.
            try
            {
                var saved = _competitions.GetSaved(competitionId);
                if (saved != null && saved.ManagerIds.Contains(memberId))
                {
                    var rest = saved.ManagerIds.Where(m => m != memberId).ToArray();
                    var r = _competitions.Update(competitionId, null, c => c.CompetitionManagers = Newtonsoft.Json.JsonConvert.SerializeObject(rest));
                    if (r.PublishProblem != null) _logger.LogWarning("Tävling {Comp}: tävlingsledarlistan kunde inte uppdateras: {Problem}", competitionId, r.PublishProblem);
                    else removed = true;
                }
            }
            catch (Exception ex) { _logger.LogError(ex, "Tävling {Comp}: tävlingsledaren {Member} kunde inte tas bort ur tävlingens lista.", competitionId, memberId); throw; }
            using var s = _scopes.CreateScope(autoComplete: true);
            s.Database.Execute("DELETE FROM CompetitionManagerWish WHERE CompetitionId = @0 AND MemberId = @1", competitionId, memberId);
            return removed;
        }

        /// <summary>Antal tävlingsledare just nu (rostern och tävlingens egen lista, färskt ur lagringen).</summary>
        public int ManagerCount(int competitionId)
        {
            var set = new HashSet<int>(ManagersByCompetition(new[] { competitionId })[competitionId]);
            var saved = _competitions.GetSaved(competitionId);
            if (saved != null) foreach (var m in saved.ManagerIds) if (m > 0) set.Add(m);
            return set.Count;
        }

        /// <summary>
        /// Stänger rollen ("Klart, tävlingsledare tillsatt"). Returnerar de som önskat men inte utsetts (de ska få beskedet), eller null om den
        /// redan var stängd (ett dubbelklick mejlar inte två gånger). Önskemålen markeras Avslutad i samma transaktion.
        /// </summary>
        public List<int>? Close(int competitionId, int byMemberId)
        {
            var now = Now;
            using var s = _scopes.CreateScope();
            var db = s.Database;
            var claimed = db.Execute(@"
                INSERT INTO CompetitionManagerClosure (CompetitionId, ClosedAt, ClosedByMemberId)
                SELECT @0, @1, @2 WHERE NOT EXISTS (SELECT 1 FROM CompetitionManagerClosure WHERE CompetitionId = @0)",
                competitionId, now, byMemberId > 0 ? (object)byMemberId : DBNull.Value);
            if (claimed == 0) { s.Complete(); return null; }
            var notice = CompetitionManagerRules.WhoGetsFilledNotice(
                db.Fetch<CompetitionManagerWish>("SELECT * FROM CompetitionManagerWish WHERE CompetitionId = @0", competitionId));
            db.Execute("UPDATE CompetitionManagerWish SET Status = @1, UpdatedAt = @2 WHERE CompetitionId = @0 AND Status = @3",
                competitionId, CompetitionManagerWishStatus.Avslutad, now, CompetitionManagerWishStatus.Aktiv);
            s.Complete();
            return notice;
        }

        /// <summary>Öppnar rollen igen (adminen ångrar Klart). Önskemål som hunnit bli Avslutad blir Aktiva igen.</summary>
        public bool Reopen(int competitionId)
        {
            using var s = _scopes.CreateScope();
            var db = s.Database;
            var n = db.Execute("DELETE FROM CompetitionManagerClosure WHERE CompetitionId = @0", competitionId);
            if (n > 0)
                db.Execute("UPDATE CompetitionManagerWish SET Status = @1, UpdatedAt = @2 WHERE CompetitionId = @0 AND Status = @3",
                    competitionId, CompetitionManagerWishStatus.Aktiv, Now, CompetitionManagerWishStatus.Avslutad);
            s.Complete();
            return n > 0;
        }

        /// <summary>Gallrar önskemål som inte ledde till något när tävlingen är genomförd (Stefans beslut 2026-11-19). Returnerar antalet.</summary>
        public int PurgeAfterCompetition(DateTime today)
        {
            // Datumet läses genom tävlingslagringen (aldrig rå SQL mot dbo.Competition). En raderad tävling saknas i svaret och gallras också.
            List<int> ids;
            using (var s = _scopes.CreateScope(autoComplete: true))
                ids = s.Database.Fetch<int>("SELECT DISTINCT CompetitionId FROM CompetitionManagerWish WHERE Status <> @0", CompetitionManagerWishStatus.Utsedd);
            if (ids.Count == 0) return 0;
            var comps = _competitions.GetMany(ids);
            var gone = ids.Where(i => !comps.ContainsKey(i) || comps[i].CompetitionDate is DateTime d && CompetitionManagerRules.ShouldPurge("", d, today)).ToList();
            if (gone.Count == 0) return 0;
            using var s2 = _scopes.CreateScope(autoComplete: true);
            return s2.Database.Execute("DELETE FROM CompetitionManagerWish WHERE Status <> @0 AND CompetitionId IN (@ids)",
                CompetitionManagerWishStatus.Utsedd, new { ids = gone });
        }

        // ── Internt ────────────────────────────────────────────────────────────────────────

        private void MarkWish(int competitionId, int memberId, string status)
        {
            var now = Now;
            using var s = _scopes.CreateScope(autoComplete: true);
            // Uppdatera, annars skapa — i EN sats så att samtidiga anrop inte ger en dubblett (unikt index som skyddsnät).
            s.Database.Execute(@"
                UPDATE CompetitionManagerWish SET Status = @2, UpdatedAt = @3 WHERE CompetitionId = @0 AND MemberId = @1;
                INSERT INTO CompetitionManagerWish (CompetitionId, MemberId, Status, CreatedAt, UpdatedAt)
                SELECT @0, @1, @2, @3, @3 WHERE NOT EXISTS (SELECT 1 FROM CompetitionManagerWish WHERE CompetitionId = @0 AND MemberId = @1)",
                competitionId, memberId, status, now);
        }

        private int RosterRowId(StaffOwner owner, int memberId)
        {
            using var s = _scopes.CreateScope(autoComplete: true);
            return s.Database.ExecuteScalar<int?>(
                "SELECT TOP 1 Id FROM StaffAssignment WHERE " + StaffOwner.Where(0) + " AND RoleKey = @1 AND MemberId = @2 ORDER BY Id",
                owner.P, Role, memberId) ?? 0;
        }

        private StaffOwner? ExistingOwner(int competitionId)
        {
            var p = _planning.ForCompetition(competitionId);
            return p == null ? null : new StaffOwner(p.Id, competitionId);
        }

        /// <summary>Tävlingens bemanningsägare. Saknas planeringen startas den (utsedaren blir sammankallande), som <c>Planning/StartForCompetition</c>.</summary>
        private StaffOwner? EnsureOwner(HpskSite.Models.Competitions.CompetitionInfo comp, int byMemberId, out string? error)
        {
            error = null;
            var existing = ExistingOwner(comp.Id);
            if (existing != null) return existing;
            var r = _planning.CreateForCompetition(new PlanningService.CompetitionPlanningInput(comp.Id, comp.DisplayName, comp.CompetitionDate,
                comp.CompetitionEndDate ?? comp.CompetitionDate, byMemberId, MemberName(byMemberId), comp.ClubId > 0 ? comp.ClubId : null, null), null);
            if (r.Error != null || r.PlanningId <= 0) { error = r.Error ?? "Tävlingens planering kunde inte startas."; return null; }
            try { _staffing.SyncCompetitionMirrors(comp.Id); }
            catch (Exception ex) { _logger.LogWarning(ex, "Tävling {Comp}: speglingarna kunde inte räknas om efter att planeringen startats.", comp.Id); }
            return new StaffOwner(r.PlanningId, comp.Id);
        }

        private string MemberName(int memberId)
        {
            var m = memberId > 0 ? _members.GetById(memberId) : null;
            var n = $"{m?.GetValue<string>("firstName")} {m?.GetValue<string>("lastName")}".Trim();
            return n.Length > 0 ? n : (m?.Name ?? "Medlem");
        }
    }
}
