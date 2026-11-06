using HpskSite.Models;
using HpskSite.Models.Registrations;
using Umbraco.Cms.Infrastructure.Scoping;

namespace HpskSite.Services.Registrations
{
    /// <summary>
    /// Anmälningarna i tabellerna <c>CompetitionRegistration</c> + <c>CompetitionRegistrationClass</c>
    /// (sajtstrukturen fas 1c). Väljs av <see cref="SwitchingRegistrationStore"/> när växeln
    /// <c>Registrations:Store</c> står på <c>table</c>.
    ///
    /// <para><b>Inget Umbraco-lås, ingen publicering.</b> En anmälan är två tabellrader i en
    /// transaktion — det globala innehållslåset (-333) som varje nodsparning köar i är borta.</para>
    ///
    /// <para><b>⚠️ Id:n.</b> Migrerade rader bär nodens id (IDENTITY_INSERT). Äldre noder som slogs ihop
    /// vid flytten (en nod per klass) slås upp via <c>CompetitionRegistrationLegacyId</c>, så gamla
    /// fakturor och liggarrader som bär ett sådant id fortfarande hittar sin anmälan.</para>
    ///
    /// <para><b>Klasserna</b> lagras med klassens Id (visningsnamn normaliseras), i anmälans ordning.
    /// Okända strängar (Springskyttes "C-H 50") lagras som de är. Samma klass två gånger slås ihop —
    /// det unika indexet hade annars vägrat hela sparningen.</para>
    ///
    /// <para>Använder <see cref="IScopeProvider"/>, aldrig en egen anslutning: lagringen anropas ibland
    /// inifrån någon annans scope, och en egen anslutning där låser (se minnet om bryggan i ekonomin).</para>
    /// </summary>
    public class SqlRegistrationStore : ICompetitionRegistrationStore
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly ILogger<SqlRegistrationStore> _logger;

        public SqlRegistrationStore(IScopeProvider scopeProvider, ILogger<SqlRegistrationStore> logger)
        {
            _scopeProvider = scopeProvider;
            _logger = logger;
        }

        private const string RowColumns = @"Id, CompetitionId, MemberId, MemberName, ClubId, LegacyClubText, IsActive,
IsSubCompetition, IsCheckedIn, RegistrationDate, CreatedDate, RegisteredBy, LegacyInvoiceId, MemberEmail, Notes, ShooterNotes";

        // ── Läsning ────────────────────────────────────────────────────────────────────────

        public IReadOnlyList<RegistrationRecord> List(int competitionId)
        {
            if (competitionId <= 0) return Array.Empty<RegistrationRecord>();
            return Load($"WHERE r.CompetitionId = @0", competitionId);
        }

        public RegistrationRecord? Get(int competitionId, int registrationId)
        {
            if (competitionId <= 0 || registrationId <= 0) return null;
            var direct = Load("WHERE r.Id = @0 AND r.CompetitionId = @1", registrationId, competitionId).FirstOrDefault();
            if (direct != null) return direct;
            return Load(@"WHERE r.Id = (SELECT l.RegistrationId FROM dbo.CompetitionRegistrationLegacyId l
                                       WHERE l.LegacyNodeId = @0 AND l.CompetitionId = @1)", registrationId, competitionId)
                .FirstOrDefault();
        }

        public RegistrationRecord? GetById(int registrationId)
        {
            if (registrationId <= 0) return null;
            var direct = Load("WHERE r.Id = @0", registrationId).FirstOrDefault();
            if (direct != null) return direct;
            return Load(@"WHERE r.Id = (SELECT l.RegistrationId FROM dbo.CompetitionRegistrationLegacyId l
                                       WHERE l.LegacyNodeId = @0)", registrationId).FirstOrDefault();
        }

        public RegistrationRecord? FindForMember(int competitionId, int memberId)
            => ListForMemberInCompetition(competitionId, memberId).FirstOrDefault();

        public IReadOnlyList<RegistrationRecord> ListForMemberInCompetition(int competitionId, int memberId)
        {
            if (competitionId <= 0 || memberId <= 0) return Array.Empty<RegistrationRecord>();
            // Den aktiva först: efter flytten finns högst en aktiv per (tävling, medlem).
            return Load("WHERE r.CompetitionId = @0 AND r.MemberId = @1", competitionId, memberId)
                .OrderByDescending(r => r.IsActive).ThenBy(r => r.Id).ToList();
        }

        public IReadOnlyList<RegistrationRecord> ListForMember(int memberId)
        {
            if (memberId <= 0) return Array.Empty<RegistrationRecord>();
            // ⚠️ Ett läsfel kastas — det är inte "inga anmälningar". Förut svarade de tre läsningarna här
            // tomt vid fel, och dubblettsammanslagningen flyttade då ingenting medan raderingen av en
            // tävling räknade noll (genomgången 2026-10-13, A7). En yta som hellre visar tomt fångar själv.
            return Load("WHERE r.MemberId = @0", memberId);
        }

        public IReadOnlyList<(int MemberId, int CompetitionId)> CompetitionsForMembers(IReadOnlyCollection<int> memberIds)
        {
            var result = new List<(int, int)>();
            var ids = memberIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return result;
            // Ett läsfel kastas (se ListForMember).
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // I portioner: IN (@0) blir en parameter per id, och SQL Server tar slut vid ~2100.
            foreach (var batch in ids.Chunk(1000))
            {
                var inList = string.Join(",", batch.Select((_, i) => "@" + i));
                var rows = scope.Database.Fetch<PairRow>(
                    $"SELECT DISTINCT MemberId, CompetitionId FROM dbo.CompetitionRegistration WHERE MemberId IN ({inList})",
                    batch.Cast<object>().ToArray());
                result.AddRange(rows.Select(r => (r.MemberId, r.CompetitionId)));
            }
            return result;
        }

        public IReadOnlySet<int> CompetitionsWithRegisteredBy(string registeredBy)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            return scope.Database.Fetch<int>(
                "SELECT DISTINCT CompetitionId FROM dbo.CompetitionRegistration WHERE RegisteredBy = @0", registeredBy ?? "")
                .ToHashSet();
        }

        public IReadOnlyDictionary<int, int> CountByCompetition(IReadOnlyCollection<int>? competitionIds = null)
        {
            var result = new Dictionary<int, int>();
            // Ett läsfel kastas (se ListForMember) — tävlingsraderingen bygger på det här talet.
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            // Alla rader, även inaktiva — samma svar som nodlagringen gav (den räknade noder).
            if (competitionIds is null)
            {
                foreach (var r in scope.Database.Fetch<CountRow>(
                             "SELECT CompetitionId, COUNT(*) AS Cnt FROM dbo.CompetitionRegistration GROUP BY CompetitionId"))
                    result[r.CompetitionId] = r.Cnt;
                return result;
            }
            // Bara de efterfrågade tävlingarna — förut aggregerades hela tabellen vid varje sidladdning.
            // I portioner av samma skäl som ovan.
            foreach (var batch in competitionIds.Where(i => i > 0).Distinct().Chunk(1000))
            {
                var inList = string.Join(",", batch.Select((_, i) => "@" + i));
                foreach (var r in scope.Database.Fetch<CountRow>(
                             $"SELECT CompetitionId, COUNT(*) AS Cnt FROM dbo.CompetitionRegistration WHERE CompetitionId IN ({inList}) GROUP BY CompetitionId",
                             batch.Cast<object>().ToArray()))
                    result[r.CompetitionId] = r.Cnt;
            }
            return result;
        }

        // ── Skrivning ──────────────────────────────────────────────────────────────────────

        public int Save(RegistrationRecord r)
        {
            ArgumentNullException.ThrowIfNull(r);
            if (r.CompetitionId <= 0) throw new InvalidOperationException("Anmälan saknar tävling.");
            var classes = NormalizeClasses(r.Classes);
            var now = DateTime.Now;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            int id;
            try
            {
                if (r.Id <= 0)
                {
                    var created = r.CreatedDate == default ? now : r.CreatedDate;
                    id = db.ExecuteScalar<int>(@"
INSERT INTO dbo.CompetitionRegistration
    (CompetitionId, MemberId, MemberName, ClubId, LegacyClubText, IsActive, IsSubCompetition, IsCheckedIn,
     RegistrationDate, CreatedDate, UpdatedDate, RegisteredBy, LegacyInvoiceId, MemberEmail, Notes, ShooterNotes)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8, @9, @10, @11, @12, @13, @14, @15);
SELECT CAST(SCOPE_IDENTITY() AS INT);",
                        r.CompetitionId, r.MemberId, r.MemberName ?? "", r.ClubId, NullIfEmpty(r.LegacyClubText),
                        r.IsActive, r.IsSubCompetition, r.IsCheckedIn,
                        r.RegistrationDate == default ? now : r.RegistrationDate, created, now,
                        r.RegisteredBy ?? "", r.InvoiceId is int inv && inv > 0 ? inv : (int?)null,
                        NullIfEmpty(r.MemberEmail), NullIfEmpty(r.Notes), NullIfEmpty(r.ShooterNotes));
                }
                else
                {
                    id = r.Id;
                    // ⚠️ Samma tre regler som nodlagringen: ClubId 0 tar aldrig bort en klubb, en tom
                    // fakturalänk nollar inte den lagrade, och raden måste höra till tävlingen.
                    var updated = db.Execute(@"
UPDATE dbo.CompetitionRegistration SET
    MemberId = @2, MemberName = @3,
    ClubId = CASE WHEN @4 > 0 THEN @4 ELSE ClubId END,
    IsActive = @5, IsSubCompetition = @6, IsCheckedIn = @7,
    RegistrationDate = CASE WHEN @8 IS NULL THEN RegistrationDate ELSE @8 END,
    RegisteredBy = @9,
    LegacyInvoiceId = COALESCE(@10, LegacyInvoiceId),
    UpdatedDate = @11
WHERE Id = @0 AND CompetitionId = @1",
                        id, r.CompetitionId, r.MemberId, r.MemberName ?? "", r.ClubId,
                        r.IsActive, r.IsSubCompetition, r.IsCheckedIn,
                        r.RegistrationDate == default ? (DateTime?)null : r.RegistrationDate,
                        r.RegisteredBy ?? "", r.InvoiceId, now);
                    if (updated == 0)
                        throw new InvalidOperationException($"Anmälan {id} finns inte i tävling {r.CompetitionId}.");
                    db.Execute("DELETE FROM dbo.CompetitionRegistrationClass WHERE RegistrationId = @0", id);
                }

                for (var i = 0; i < classes.Count; i++)
                {
                    db.Execute(@"
INSERT INTO dbo.CompetitionRegistrationClass (RegistrationId, SortOrder, ClassKey, StartPreference, TeamNumber, JoinMemberId)
VALUES (@0, @1, @2, @3, @4, @5)",
                        id, i, classes[i].Class, string.IsNullOrWhiteSpace(classes[i].StartPreference) ? "Inget" : classes[i].StartPreference,
                        classes[i].TeamNumber, classes[i].JoinMemberId);
                }

                // Beslutet "Utanför mästerskapet" bor i en EGEN tabell som Save aldrig skriver (klassraderna raderas och sätts in
                // på nytt här, besluten ska överleva det). Försvinner klassraden (klassbyte, borttagen klass) hör beslutet inte
                // längre till något och städas — det är det enda sättet ett klassbyte nollställer flaggan. Varje städat beslut
                // loggas i beslutsloggen (Reason 'class-removed').
                if (r.Id > 0)
                {
                    var cleanup = BuildDecisionCleanup(id, now, r.CompetitionId, r.MemberId, classes.Select(c => c.Class).ToList());
                    db.Execute(cleanup.LogSql, cleanup.Args);
                    db.Execute(cleanup.DeleteSql, cleanup.Args);
                }
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627
                                                                    && ex.Message.Contains("UX_CompetitionRegistration_ActiveMember"))
            {
                throw new InvalidOperationException($"{r.MemberName} är redan anmäld till tävlingen.", ex);
            }

            scope.Complete();
            r.Id = id;
            return id;
        }

        public OutOfCompetitionOutcome SetOutOfCompetition(int competitionId, int registrationId, string classKey, bool? value,
            int? decidedByMemberId, string reason = "organiser")
        {
            var raw = (classKey ?? "").Trim();
            if (raw.Length == 0) return OutOfCompetitionOutcome.ClassNotFound;
            var key = ShootingClasses.Resolve(raw)?.Id ?? raw;
            var why = Truncate(string.IsNullOrWhiteSpace(reason) ? "organiser" : reason.Trim(), 40);
            var now = DateTime.Now;
            var by = decidedByMemberId is > 0 ? decidedByMemberId : null;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Låset serialiserar mot Save (som städar beslut vars klassrad försvunnit) — annars kunde Save radera beslutet mitt i.
            var owner = db.Fetch<int>(
                "SELECT MemberId FROM dbo.CompetitionRegistration WITH (UPDLOCK) WHERE Id = @0 AND CompetitionId = @1",
                registrationId, competitionId);
            if (owner.Count == 0) return OutOfCompetitionOutcome.RegistrationNotFound;
            var memberId = owner[0];

            var old = db.Fetch<bool>(
                "SELECT OutOfCompetition FROM dbo.CompetitionRegistrationClassDecision WITH (UPDLOCK, HOLDLOCK) WHERE RegistrationId = @0 AND ClassKey = @1",
                registrationId, key);
            bool? oldValue = old.Count == 0 ? null : old[0];

            if (value == null)
            {
                if (oldValue == null) return OutOfCompetitionOutcome.Unchanged;   // även en föräldralös tom rad: inget att ta bort
                db.Execute("DELETE FROM dbo.CompetitionRegistrationClassDecision WHERE RegistrationId = @0 AND ClassKey = @1", registrationId, key);
            }
            else
            {
                // Ett beslut kan bara sättas på en klassrad som finns.
                var classExists = db.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM dbo.CompetitionRegistrationClass WHERE RegistrationId = @0 AND ClassKey = @1", registrationId, key) > 0;
                if (!classExists) return OutOfCompetitionOutcome.ClassNotFound;
                if (oldValue == value) return OutOfCompetitionOutcome.Unchanged;

                if (oldValue == null)
                    db.Execute(@"
INSERT INTO dbo.CompetitionRegistrationClassDecision (RegistrationId, ClassKey, OutOfCompetition, SetByMemberId, SetAt)
VALUES (@0, @1, @2, @3, @4)", registrationId, key, value.Value, by, now);
                else
                    db.Execute(@"
UPDATE dbo.CompetitionRegistrationClassDecision SET OutOfCompetition = @2, SetByMemberId = @3, SetAt = @4
WHERE RegistrationId = @0 AND ClassKey = @1", registrationId, key, value.Value, by, now);
            }

            db.Execute(@"
INSERT INTO dbo.ChampionshipDecisionLog (CompetitionId, RegistrationId, MemberId, ClassKey, OldValue, NewValue, Reason, SetByMemberId, SetAt)
VALUES (@0, @1, @2, @3, @4, @5, @6, @7, @8)",
                competitionId, registrationId, memberId, key, oldValue, value, why, by, now);

            scope.Complete();
            return OutOfCompetitionOutcome.Changed;
        }

        public int ClearOutOfCompetition(int competitionId, int registrationId, string reason)
        {
            var why = Truncate(string.IsNullOrWhiteSpace(reason) ? "cleared" : reason.Trim(), 40);
            var now = DateTime.Now;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;
            var owner = db.Fetch<int>(
                "SELECT MemberId FROM dbo.CompetitionRegistration WITH (UPDLOCK) WHERE Id = @0 AND CompetitionId = @1", registrationId, competitionId);
            if (owner.Count == 0) return 0;

            db.Execute(@"
INSERT INTO dbo.ChampionshipDecisionLog (CompetitionId, RegistrationId, MemberId, ClassKey, OldValue, NewValue, Reason, SetByMemberId, SetAt)
SELECT @1, RegistrationId, @2, ClassKey, OutOfCompetition, NULL, @3, NULL, @4
FROM dbo.CompetitionRegistrationClassDecision WHERE RegistrationId = @0", registrationId, competitionId, owner[0], why, now);
            var removed = db.Execute("DELETE FROM dbo.CompetitionRegistrationClassDecision WHERE RegistrationId = @0", registrationId);
            scope.Complete();
            return removed;
        }

        public bool StampOutOfCompetition(int competitionId, int registrationId, string classKey, string reason)
            => StampDecision(competitionId, registrationId, classKey, reason, true);

        public bool StampInCompetition(int competitionId, int registrationId, string classKey, string reason)
            => StampDecision(competitionId, registrationId, classKey, reason, false);

        /// <summary>
        /// Skriver ETT beslut (utanför = 1, tävlar = 0) för en klassrad som inte har något, och loggar det. Skriver aldrig över ett beslut.
        /// Båda stämplingarna (förslaget som blir "utanför", och frysningen av "tävlar") går hit.
        /// </summary>
        private bool StampDecision(int competitionId, int registrationId, string classKey, string reason, bool outside)
        {
            var raw = (classKey ?? "").Trim();
            if (raw.Length == 0) return false;
            var key = ShootingClasses.Resolve(raw)?.Id ?? raw;
            var why = Truncate(string.IsNullOrWhiteSpace(reason) ? "stamped" : reason.Trim(), 40);
            var now = DateTime.Now;

            using var scope = _scopeProvider.CreateScope();
            var db = scope.Database;

            // Samma lås som SetOutOfCompetition: en samtidig arrangörsändring avgör, stämplingen väntar och ser då beslutet.
            var owner = db.Fetch<int>(
                "SELECT MemberId FROM dbo.CompetitionRegistration WITH (UPDLOCK) WHERE Id = @0 AND CompetitionId = @1",
                registrationId, competitionId);
            if (owner.Count == 0) return false;

            var hasDecision = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.CompetitionRegistrationClassDecision WITH (UPDLOCK, HOLDLOCK) WHERE RegistrationId = @0 AND ClassKey = @1",
                registrationId, key) > 0;
            if (hasDecision) return false;

            var classExists = db.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM dbo.CompetitionRegistrationClass WHERE RegistrationId = @0 AND ClassKey = @1", registrationId, key) > 0;
            if (!classExists) return false;

            db.Execute(@"
INSERT INTO dbo.CompetitionRegistrationClassDecision (RegistrationId, ClassKey, OutOfCompetition, SetByMemberId, SetAt)
VALUES (@0, @1, @2, NULL, @3)", registrationId, key, outside, now);
            db.Execute(@"
INSERT INTO dbo.ChampionshipDecisionLog (CompetitionId, RegistrationId, MemberId, ClassKey, OldValue, NewValue, Reason, SetByMemberId, SetAt)
VALUES (@0, @1, @2, @3, NULL, @6, @4, NULL, @5)", competitionId, registrationId, owner[0], key, why, now, outside);

            scope.Complete();
            return true;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

        public bool Delete(int competitionId, int registrationId)
        {
            if (competitionId <= 0 || registrationId <= 0) return false;
            using var scope = _scopeProvider.CreateScope();
            // Klasserna och de sammanslagna äldre id:na följer med (ON DELETE CASCADE).
            var n = scope.Database.Execute(
                "DELETE FROM dbo.CompetitionRegistration WHERE Id = @0 AND CompetitionId = @1", registrationId, competitionId);
            scope.Complete();
            return n > 0;
        }

        // ── Hjälpare ───────────────────────────────────────────────────────────────────────

        /// <summary>
        /// SQL + parametrar som städar beslut vars klassrad inte längre finns (första satsen loggar, andra raderar). @0 anmälan, @1 tid,
        /// @2 tävling, @3 medlem, från @4 de klassnycklar som BEHÅLLS. Ingen klass kvar = alla beslut hör till raderade klasser.
        /// </summary>
        internal static (string LogSql, string DeleteSql, object[] Args) BuildDecisionCleanup(
            int registrationId, DateTime now, int competitionId, int memberId, IReadOnlyList<string> keptClassKeys)
        {
            var notIn = keptClassKeys.Count == 0
                ? ""
                : " AND ClassKey NOT IN (" + string.Join(",", keptClassKeys.Select((_, k) => "@" + (k + 4))) + ")";
            var args = new List<object> { registrationId, now, competitionId, memberId };
            args.AddRange(keptClassKeys);
            var log = $@"
INSERT INTO dbo.ChampionshipDecisionLog (CompetitionId, RegistrationId, MemberId, ClassKey, OldValue, NewValue, Reason, SetByMemberId, SetAt)
SELECT @2, RegistrationId, @3, ClassKey, OutOfCompetition, NULL, 'class-removed', NULL, @1
FROM dbo.CompetitionRegistrationClassDecision WHERE RegistrationId = @0{notIn}";
            var delete = $"DELETE FROM dbo.CompetitionRegistrationClassDecision WHERE RegistrationId = @0{notIn}";
            return (log, delete, args.ToArray());
        }

        /// <summary>Klassens Id i stället för visningsnamnet, i ordning, utan dubbletter.</summary>
        public static List<ShootingClassEntry> NormalizeClasses(IEnumerable<ShootingClassEntry>? classes)
        {
            var result = new List<ShootingClassEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in classes ?? Enumerable.Empty<ShootingClassEntry>())
            {
                var raw = (c?.Class ?? "").Trim();
                if (raw.Length == 0) continue;
                var key = ShootingClasses.Resolve(raw)?.Id ?? raw;
                if (!seen.Add(key)) continue;
                result.Add(new ShootingClassEntry
                {
                    Class = key,
                    StartPreference = string.IsNullOrWhiteSpace(c!.StartPreference) ? "Inget" : c.StartPreference.Trim(),
                    TeamNumber = c.TeamNumber,
                    JoinMemberId = c.JoinMemberId is > 0 ? c.JoinMemberId : null
                });
            }
            return result;
        }

        private List<RegistrationRecord> Load(string where, params object[] args)
        {
            using var scope = _scopeProvider.CreateScope(autoComplete: true);
            var db = scope.Database;
            var rows = db.Fetch<RegistrationRow>($"SELECT {RowColumns} FROM dbo.CompetitionRegistration r {where} ORDER BY r.Id", args);
            if (rows.Count == 0) return new List<RegistrationRecord>();

            var classes = new Dictionary<int, List<ShootingClassEntry>>();
            foreach (var batch in rows.Select(x => x.Id).Chunk(1000))
            {
                var inList = string.Join(",", batch.Select((_, i) => "@" + i));
                foreach (var c in db.Fetch<ClassRow>(
                             $@"SELECT c.RegistrationId, c.SortOrder, c.ClassKey, c.StartPreference, c.TeamNumber, c.JoinMemberId,
       d.OutOfCompetition, d.SetByMemberId AS DecisionSetBy, d.SetAt AS DecisionSetAt
FROM dbo.CompetitionRegistrationClass c
LEFT JOIN dbo.CompetitionRegistrationClassDecision d ON d.RegistrationId = c.RegistrationId AND d.ClassKey = c.ClassKey
WHERE c.RegistrationId IN ({inList}) ORDER BY c.RegistrationId, c.SortOrder",
                             batch.Cast<object>().ToArray()))
                {
                    if (!classes.TryGetValue(c.RegistrationId, out var list)) classes[c.RegistrationId] = list = new();
                    list.Add(new ShootingClassEntry
                    {
                        Class = c.ClassKey, StartPreference = c.StartPreference, TeamNumber = c.TeamNumber, JoinMemberId = c.JoinMemberId,
                        OutOfCompetition = c.OutOfCompetition, OutOfCompetitionSetBy = c.DecisionSetBy, OutOfCompetitionSetAt = c.DecisionSetAt
                    });
                }
            }

            return rows.Select(x => new RegistrationRecord
            {
                Id = x.Id,
                CompetitionId = x.CompetitionId,
                MemberId = x.MemberId,
                MemberName = x.MemberName ?? "",
                ClubId = x.ClubId,
                LegacyClubText = x.LegacyClubText ?? "",
                Classes = classes.TryGetValue(x.Id, out var cl) ? cl : new List<ShootingClassEntry>(),
                RegistrationDate = x.RegistrationDate,
                CreatedDate = x.CreatedDate,
                RegisteredBy = x.RegisteredBy ?? "",
                IsActive = x.IsActive,
                IsSubCompetition = x.IsSubCompetition,
                IsCheckedIn = x.IsCheckedIn,
                InvoiceId = x.LegacyInvoiceId,
                MemberEmail = x.MemberEmail ?? "",
                Notes = x.Notes ?? "",
                ShooterNotes = x.ShooterNotes ?? ""
            }).ToList();
        }

        private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        private sealed class RegistrationRow
        {
            public int Id { get; set; }
            public int CompetitionId { get; set; }
            public int MemberId { get; set; }
            public string? MemberName { get; set; }
            public int ClubId { get; set; }
            public string? LegacyClubText { get; set; }
            public bool IsActive { get; set; }
            public bool IsSubCompetition { get; set; }
            public bool IsCheckedIn { get; set; }
            public DateTime RegistrationDate { get; set; }
            public DateTime CreatedDate { get; set; }
            public string? RegisteredBy { get; set; }
            public int? LegacyInvoiceId { get; set; }
            public string? MemberEmail { get; set; }
            public string? Notes { get; set; }
            public string? ShooterNotes { get; set; }
        }

        private sealed class ClassRow
        {
            public int RegistrationId { get; set; }
            public int SortOrder { get; set; }
            public string ClassKey { get; set; } = "";
            public string StartPreference { get; set; } = "Inget";
            public int? TeamNumber { get; set; }
            public int? JoinMemberId { get; set; }
            public bool? OutOfCompetition { get; set; }
            public int? DecisionSetBy { get; set; }
            public DateTime? DecisionSetAt { get; set; }
        }

        private sealed class PairRow
        {
            public int MemberId { get; set; }
            public int CompetitionId { get; set; }
        }

        private sealed class CountRow
        {
            public int CompetitionId { get; set; }
            public int Cnt { get; set; }
        }
    }
}
