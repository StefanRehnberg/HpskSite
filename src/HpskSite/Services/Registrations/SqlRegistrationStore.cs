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
            try
            {
                return Load("WHERE r.MemberId = @0", memberId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlem {MemberId}s anmälningar kunde inte läsas", memberId);
                return Array.Empty<RegistrationRecord>();
            }
        }

        public IReadOnlyList<(int MemberId, int CompetitionId)> CompetitionsForMembers(IReadOnlyCollection<int> memberIds)
        {
            var result = new List<(int, int)>();
            var ids = memberIds.Where(i => i > 0).Distinct().ToList();
            if (ids.Count == 0) return result;
            try
            {
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
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Medlemmarnas anmälningar kunde inte läsas");
            }
            return result;
        }

        public IReadOnlyDictionary<int, int> CountByCompetition(IReadOnlyCollection<int>? competitionIds = null)
        {
            var result = new Dictionary<int, int>();
            try
            {
                using var scope = _scopeProvider.CreateScope(autoComplete: true);
                // Alla rader, även inaktiva — samma svar som nodlagringen gav (den räknade noder).
                var rows = scope.Database.Fetch<CountRow>(
                    "SELECT CompetitionId, COUNT(*) AS Cnt FROM dbo.CompetitionRegistration GROUP BY CompetitionId");
                foreach (var r in rows)
                    if (competitionIds is null || competitionIds.Contains(r.CompetitionId))
                        result[r.CompetitionId] = r.Cnt;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Antalet anmälningar per tävling kunde inte läsas");
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
INSERT INTO dbo.CompetitionRegistrationClass (RegistrationId, SortOrder, ClassKey, StartPreference, TeamNumber)
VALUES (@0, @1, @2, @3, @4)",
                        id, i, classes[i].Class, string.IsNullOrWhiteSpace(classes[i].StartPreference) ? "Inget" : classes[i].StartPreference,
                        classes[i].TeamNumber);
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
                    TeamNumber = c.TeamNumber
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
                             $"SELECT RegistrationId, SortOrder, ClassKey, StartPreference, TeamNumber FROM dbo.CompetitionRegistrationClass WHERE RegistrationId IN ({inList}) ORDER BY RegistrationId, SortOrder",
                             batch.Cast<object>().ToArray()))
                {
                    if (!classes.TryGetValue(c.RegistrationId, out var list)) classes[c.RegistrationId] = list = new();
                    list.Add(new ShootingClassEntry { Class = c.ClassKey, StartPreference = c.StartPreference, TeamNumber = c.TeamNumber });
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
