using HpskSite.Models;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Reads and writes the Standardmedalj ledger (StandardMedalAward) and derives the two
    /// aggregations that matter:
    ///   * Riksmästarklass (klass 3) qualification — points PER DISCIPLINE for a given year.
    ///   * Guldmedalj — points pooled across ALL disciplines, lifetime, minus points already
    ///     consumed by approved Gold applications.
    ///
    /// On-site medals are materialized here (Source = OnSite, auto-Verified); external medals
    /// are self-reported by members and start as Reported until a club admin verifies them.
    /// </summary>
    public class StandardMedalLedgerService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<StandardMedalLedgerService> _logger;

        public StandardMedalLedgerService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<StandardMedalLedgerService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Award reads ───────────────────────────────────────────────

        public async Task<StandardMedalAward?> GetAwardAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<StandardMedalAward>("WHERE Id = @0", id);
        }

        public async Task<List<StandardMedalAward>> GetAwardsByIdsAsync(IEnumerable<int> ids)
        {
            var list = ids?.Distinct().ToList() ?? new List<int>();
            if (list.Count == 0) return new List<StandardMedalAward>();
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<StandardMedalAward>(
                $"WHERE Id IN ({string.Join(",", list)}) ORDER BY CompetitionDate, Id");
        }

        public async Task<StandardMedalAward?> GetByTrainingScoreAsync(int trainingScoreId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<StandardMedalAward>(
                "WHERE TrainingScoreId = @0", trainingScoreId);
        }

        public async Task<List<StandardMedalAward>> GetAwardsForMemberAsync(
            int memberId, int? year = null, bool includeRejected = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("WHERE MemberId = @0", memberId);
            if (year.HasValue)
                sql.Append("AND [Year] = @0", year.Value);
            if (!includeRejected)
                sql.Append("AND Status <> @0", StandardMedals.StatusRejected);
            sql.Append("ORDER BY [Year] DESC, CompetitionDate DESC, Id DESC");
            return await db.FetchAsync<StandardMedalAward>(sql);
        }

        /// <summary>
        /// All non-rejected awards for the given members (used by the club secretary report).
        /// Keyed lookups are the caller's job — this returns a flat list.
        /// </summary>
        public async Task<List<StandardMedalAward>> GetAwardsForMembersAsync(
            IEnumerable<int> memberIds, int? year = null, bool includeRejected = false)
        {
            var ids = memberIds?.Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return new List<StandardMedalAward>();

            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql($"WHERE MemberId IN ({string.Join(",", ids)})");
            if (year.HasValue)
                sql.Append("AND [Year] = @0", year.Value);
            if (!includeRejected)
                sql.Append("AND Status <> @0", StandardMedals.StatusRejected);
            sql.Append("ORDER BY MemberId, [Year] DESC, CompetitionDate DESC, Id DESC");
            return await db.FetchAsync<StandardMedalAward>(sql);
        }

        /// <summary>
        /// All awards in a season year (across all members/clubs). Used by the club secretary
        /// report, which then filters to its own members. Excludes rejected by default.
        /// </summary>
        public async Task<List<StandardMedalAward>> GetAwardsForYearAsync(int year, bool includeRejected = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("WHERE [Year] = @0", year);
            if (!includeRejected)
                sql.Append("AND Status <> @0", StandardMedals.StatusRejected);
            sql.Append("ORDER BY MemberId, CompetitionDate DESC, Id DESC");
            return await db.FetchAsync<StandardMedalAward>(sql);
        }

        /// <summary>Distinct member ids that have any non-rejected award in the given year.</summary>
        public async Task<List<int>> GetMemberIdsWithAwardsAsync(int year)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<int>(
                "SELECT DISTINCT MemberId FROM StandardMedalAward WHERE [Year] = @0 AND Status <> @1",
                year, StandardMedals.StatusRejected);
        }

        // ── Award writes ──────────────────────────────────────────────

        public async Task<int> InsertAwardAsync(StandardMedalAward award)
        {
            award.Points = StandardMedals.PointsFor(award.MedalType);
            award.CreatedAt = DateTime.Now;
            award.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(award);
            return award.Id;
        }

        public async Task UpdateAwardAsync(StandardMedalAward award)
        {
            award.Points = StandardMedals.PointsFor(award.MedalType);
            award.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.UpdateAsync(award);
        }

        /// <summary>
        /// Set an award's verification status (used by club admins on self-reported awards).
        /// </summary>
        public async Task<(bool Success, string? Message)> SetAwardStatusAsync(int awardId, string status, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var award = await db.SingleOrDefaultAsync<StandardMedalAward>("WHERE Id = @0", awardId);
            if (award == null) return (false, "Medaljen hittades inte.");

            award.Status = status;
            if (status == StandardMedals.StatusVerified)
            {
                award.VerifiedByMemberId = actingMemberId;
                award.VerifiedAt = DateTime.Now;
            }
            award.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(award);
            return (true, null);
        }

        /// <summary>
        /// How many awards still reference a given proof file (optionally excluding one award).
        /// Used to make physical proof-file deletion safe when a file is shared across several
        /// awards (one result list backing medals in multiple classes from the same competition).
        /// </summary>
        public async Task<int> CountAwardsUsingProofAsync(string proofRef, int excludeAwardId = 0)
        {
            if (string.IsNullOrEmpty(proofRef)) return 0;
            using var db = _databaseFactory.CreateDatabase();
            return await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM StandardMedalAward WHERE ProofFileRef = @0 AND Id <> @1",
                proofRef, excludeAwardId);
        }

        /// <summary>Null an award's stored proof reference (after the file has been deleted).</summary>
        public async Task ClearAwardProofRefAsync(int awardId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync(
                "UPDATE StandardMedalAward SET ProofFileRef = NULL, UpdatedAt = @1 WHERE Id = @0",
                awardId, DateTime.Now);
        }

        /// <summary>
        /// Correct an award's medal type (e.g. shooter entered Silver instead of Brons). Recomputes
        /// points. Refuses if the award is locked into a Gold application (changing the points would
        /// break that application's reserved 50).
        /// </summary>
        public async Task<(bool Success, string? Message)> SetAwardMedalAsync(int awardId, string medalType)
        {
            if (!StandardMedals.IsMedal(medalType))
                return (false, "Ogiltig medaljtyp.");

            using var db = _databaseFactory.CreateDatabase();
            var award = await db.SingleOrDefaultAsync<StandardMedalAward>("WHERE Id = @0", awardId);
            if (award == null) return (false, "Medaljen hittades inte.");
            if (award.GoldApplicationId.HasValue)
                return (false, "Medaljen ingår i en guldmedaljansökan och kan inte ändras.");

            award.MedalType = medalType;
            award.Points = StandardMedals.PointsFor(medalType);
            award.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(award);
            return (true, null);
        }

        /// <summary>
        /// Delete an award. Refuses if it has been consumed by a Gold application — those
        /// points are locked into a submitted/approved application and must not vanish.
        /// </summary>
        public async Task<(bool Success, string? Message)> DeleteAwardAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            var award = await db.SingleOrDefaultAsync<StandardMedalAward>("WHERE Id = @0", id);
            if (award == null) return (false, "Medaljen hittades inte.");
            if (award.GoldApplicationId.HasValue)
                return (false, "Medaljen ingår i en guldmedaljansökan och kan inte tas bort.");

            await db.ExecuteAsync("DELETE FROM StandardMedalAward WHERE Id = @0", id);
            return (true, null);
        }

        /// <summary>
        /// Per-year medal counts for one discipline, split into total (non-rejected) and the
        /// verified subset. The ledger is the canonical source for the dashboard medal section.
        /// </summary>
        public async Task<Dictionary<int, MedalYearStats>> GetMedalStatsByYearAsync(int memberId, string discipline, IEnumerable<int> years)
        {
            var yearList = years?.Distinct().ToList() ?? new List<int>();
            var result = new Dictionary<int, MedalYearStats>();
            foreach (var y in yearList) result[y] = new MedalYearStats();
            if (yearList.Count == 0) return result;

            using var db = _databaseFactory.CreateDatabase();
            var awards = await db.FetchAsync<StandardMedalAward>(
                "WHERE MemberId = @0 AND Discipline = @1 AND Status <> @2",
                memberId, discipline, StandardMedals.StatusRejected);

            foreach (var a in awards)
            {
                if (!result.TryGetValue(a.Year, out var s))
                {
                    s = new MedalYearStats();
                    result[a.Year] = s;
                }
                bool verified = a.Status == StandardMedals.StatusVerified;
                if (a.MedalType == StandardMedals.Silver)
                {
                    s.SilverCount++;
                    if (verified) s.VerifiedSilver++;
                }
                else if (a.MedalType == StandardMedals.Brons)
                {
                    s.BronzeCount++;
                    if (verified) s.VerifiedBronze++;
                }
            }
            return result;
        }

        // ── Riksmästarklass qualification (per discipline) ─────────────

        /// <summary>
        /// Per-discipline point totals for a single year, for the disciplines that have a
        /// Riksmästarklass. Qualified = points reach the 3-point threshold.
        /// </summary>
        public async Task<List<DisciplineQualification>> GetQualificationAsync(
            int memberId, int year, bool verifiedOnly = false)
        {
            var awards = await GetAwardsForMemberAsync(memberId, year, includeRejected: false);
            if (verifiedOnly)
                awards = awards.Where(a => a.Status == StandardMedals.StatusVerified).ToList();

            var result = new List<DisciplineQualification>();
            foreach (var discipline in StandardMedals.QualificationDisciplines)
            {
                var inDiscipline = awards.Where(a =>
                    string.Equals(a.Discipline, discipline, StringComparison.OrdinalIgnoreCase)).ToList();

                var points = inDiscipline.Sum(a => a.Points);
                result.Add(new DisciplineQualification
                {
                    Discipline = discipline,
                    DisplayName = StandardMedals.DisciplineDisplayName(discipline),
                    Year = year,
                    Points = points,
                    SilverCount = inDiscipline.Count(a => a.MedalType == StandardMedals.Silver),
                    BronsCount = inDiscipline.Count(a => a.MedalType == StandardMedals.Brons),
                    Qualified = points >= StandardMedals.QualificationThreshold
                });
            }
            return result;
        }

        // ── Guldmedalj status (pooled across all disciplines, lifetime) ─

        public async Task<GoldStatus> GetGoldStatusAsync(int memberId, bool verifiedOnly = false)
        {
            using var db = _databaseFactory.CreateDatabase();

            var awards = await GetAwardsForMemberAsync(memberId, includeRejected: false);
            if (verifiedOnly)
                awards = awards.Where(a => a.Status == StandardMedals.StatusVerified).ToList();
            var lifetimePoints = awards.Sum(a => a.Points);

            // Applied (submitted, awaiting approval) AND Approved both reserve their 50 points,
            // so neither can be claimed twice.
            var consumed = await db.ExecuteScalarAsync<int?>(
                @"SELECT SUM(PointsConsumed) FROM StandardMedalGoldApplication
                   WHERE MemberId = @0 AND Status IN (@1, @2)",
                memberId, StandardMedals.GoldStatusApplied, StandardMedals.GoldStatusApproved) ?? 0;

            var goldsAwarded = await db.ExecuteScalarAsync<int>(
                @"SELECT COUNT(*) FROM StandardMedalGoldApplication
                   WHERE MemberId = @0 AND Status = @1",
                memberId, StandardMedals.GoldStatusApproved);

            var available = Math.Max(0, lifetimePoints - consumed);
            return new GoldStatus
            {
                LifetimePoints = lifetimePoints,
                ConsumedPoints = consumed,
                AvailablePoints = available,
                GoldsAwarded = goldsAwarded,
                CanApplyForGold = available >= StandardMedals.GoldThreshold,
                PointsToNextGold = available >= StandardMedals.GoldThreshold
                    ? 0
                    : StandardMedals.GoldThreshold - available
            };
        }

        // ── Guldmedalj applications ───────────────────────────────────

        public async Task<List<StandardMedalGoldApplication>> GetGoldApplicationsForMemberAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<StandardMedalGoldApplication>(
                "WHERE MemberId = @0 ORDER BY SequenceNumber DESC", memberId);
        }

        /// <summary>
        /// Create a Guldmedalj application reserving 50 verified, not-yet-consumed points.
        /// Awards are picked FIFO (oldest first) and tagged with the application id so they
        /// can't be claimed twice; the snapshot of award ids is the proof bundle for SPSF.
        /// Accounting is counter-based (PointsConsumed = 50), so a medal straddling the 50-point
        /// boundary keeps its surplus point available.
        /// </summary>
        public async Task<(bool Success, string? Message, int? ApplicationId)> CreateGoldApplicationAsync(
            int memberId, int clubId, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();

            var awards = await db.FetchAsync<StandardMedalAward>(
                @"WHERE MemberId = @0 AND Status = @1 AND GoldApplicationId IS NULL
                   ORDER BY CompetitionDate, Id",
                memberId, StandardMedals.StatusVerified);

            var availablePoints = awards.Sum(a => a.Points);
            if (availablePoints < StandardMedals.GoldThreshold)
                return (false, $"Endast {availablePoints} verifierade, oanvända poäng tillgängliga ({StandardMedals.GoldThreshold} krävs).", null);

            var bundle = new List<StandardMedalAward>();
            int sum = 0;
            foreach (var a in awards)
            {
                bundle.Add(a);
                sum += a.Points;
                if (sum >= StandardMedals.GoldThreshold) break;
            }

            var existingCount = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM StandardMedalGoldApplication WHERE MemberId = @0 AND Status <> @1",
                memberId, StandardMedals.GoldStatusRejected);

            var app = new StandardMedalGoldApplication
            {
                MemberId = memberId,
                ClubId = clubId,
                SequenceNumber = existingCount + 1,
                Status = StandardMedals.GoldStatusApplied,
                PointsConsumed = StandardMedals.GoldThreshold,
                AwardIdsJson = System.Text.Json.JsonSerializer.Serialize(bundle.Select(b => b.Id).ToList()),
                AppliedByMemberId = actingMemberId,
                AppliedAt = DateTime.Now,
                CreatedAt = DateTime.Now
            };
            await db.InsertAsync(app);

            foreach (var a in bundle)
            {
                a.GoldApplicationId = app.Id;
                a.UpdatedAt = DateTime.Now;
                await db.UpdateAsync(a);
            }

            return (true, null, app.Id);
        }

        public async Task<(bool Success, string? Message)> ApproveGoldApplicationAsync(int applicationId, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var app = await db.SingleOrDefaultAsync<StandardMedalGoldApplication>("WHERE Id = @0", applicationId);
            if (app == null) return (false, "Ansökan hittades inte.");
            if (app.Status == StandardMedals.GoldStatusRejected) return (false, "Ansökan är avvisad och kan inte godkännas.");

            app.Status = StandardMedals.GoldStatusApproved;
            app.ApprovedByMemberId = actingMemberId;
            app.ApprovedAt = DateTime.Now;
            await db.UpdateAsync(app);
            return (true, null);
        }

        /// <summary>Reject/cancel an application and release its reserved awards back to available.</summary>
        public async Task<(bool Success, string? Message)> RejectGoldApplicationAsync(int applicationId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var app = await db.SingleOrDefaultAsync<StandardMedalGoldApplication>("WHERE Id = @0", applicationId);
            if (app == null) return (false, "Ansökan hittades inte.");

            app.Status = StandardMedals.GoldStatusRejected;
            await db.UpdateAsync(app);

            await db.ExecuteAsync(
                "UPDATE StandardMedalAward SET GoldApplicationId = NULL, UpdatedAt = @1 WHERE GoldApplicationId = @0",
                applicationId, DateTime.Now);
            return (true, null);
        }

        public async Task<StandardMedalGoldApplication?> GetGoldApplicationAsync(int applicationId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<StandardMedalGoldApplication>("WHERE Id = @0", applicationId);
        }
    }

    /// <summary>Per-year medal counts for one discipline — total (non-rejected) + verified subset.</summary>
    public class MedalYearStats
    {
        public int SilverCount { get; set; }
        public int BronzeCount { get; set; }
        public int TotalPoints => SilverCount * 2 + BronzeCount;
        public int VerifiedSilver { get; set; }
        public int VerifiedBronze { get; set; }
        public int VerifiedPoints => VerifiedSilver * 2 + VerifiedBronze;
        public int UnverifiedPoints => TotalPoints - VerifiedPoints;
    }

    /// <summary>Per-discipline qualification snapshot for one year.</summary>
    public class DisciplineQualification
    {
        public string Discipline { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int Year { get; set; }
        public int Points { get; set; }
        public int SilverCount { get; set; }
        public int BronsCount { get; set; }
        public bool Qualified { get; set; }
    }

    /// <summary>Lifetime Guldmedalj accounting, pooled across all disciplines.</summary>
    public class GoldStatus
    {
        public int LifetimePoints { get; set; }
        public int ConsumedPoints { get; set; }
        public int AvailablePoints { get; set; }
        public int GoldsAwarded { get; set; }
        public bool CanApplyForGold { get; set; }
        public int PointsToNextGold { get; set; }
    }
}
