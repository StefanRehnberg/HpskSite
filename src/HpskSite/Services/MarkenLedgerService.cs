using HpskSite.Models;
using HpskSite.Models.ViewModels.Training;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Reads and writes the Märken ledger — awarded badges (<see cref="MemberBadge"/>) and the
    /// yearly Guldfodringar (<see cref="MemberBadgeQualification"/>) — and derives the årtalsmärke
    /// ladder from the count of fulfilled, signed-off qualification years.
    ///
    /// The system of record is the signed-off ledger row. The candidate engine
    /// (<see cref="MarkenCandidateService"/>) only proposes; nothing counts until it's here and
    /// Verified.
    /// </summary>
    public class MarkenLedgerService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<MarkenLedgerService> _logger;

        public MarkenLedgerService(IUmbracoDatabaseFactory databaseFactory, ILogger<MarkenLedgerService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Badge reads/writes ────────────────────────────────────────

        public async Task<MemberBadge?> GetBadgeAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<MemberBadge>("WHERE Id = @0", id);
        }

        public async Task<List<MemberBadge>> GetBadgesForMemberAsync(int memberId, string? family = null, bool includeRejected = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("WHERE MemberId = @0", memberId);
            if (!string.IsNullOrEmpty(family))
                sql.Append("AND BadgeFamily = @0", family);
            if (!includeRejected)
                sql.Append("AND Status <> @0", Marken.StatusRejected);
            sql.Append("ORDER BY BadgeFamily, LevelOrdinal DESC, AchievedYear DESC, Id DESC");
            return await db.FetchAsync<MemberBadge>(sql);
        }

        public async Task<int> InsertBadgeAsync(MemberBadge badge)
        {
            badge.LevelOrdinal = badge.LevelOrdinal > 0 ? badge.LevelOrdinal : Marken.LevelOrdinal(badge.Level);
            badge.CreatedAt = DateTime.Now;
            badge.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(badge);
            return badge.Id;
        }

        public async Task UpdateBadgeAsync(MemberBadge badge)
        {
            badge.LevelOrdinal = badge.LevelOrdinal > 0 ? badge.LevelOrdinal : Marken.LevelOrdinal(badge.Level);
            badge.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.UpdateAsync(badge);
        }

        public async Task<(bool Success, string? Message)> SetBadgeStatusAsync(int badgeId, string status, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var badge = await db.SingleOrDefaultAsync<MemberBadge>("WHERE Id = @0", badgeId);
            if (badge == null) return (false, "Märket hittades inte.");

            badge.Status = status;
            if (status == Marken.StatusVerified)
            {
                badge.SignedOffByMemberId = actingMemberId;
                badge.SignedOffDate = DateTime.Now;
            }
            badge.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(badge);
            return (true, null);
        }

        /// <summary>Set/replace the national registration number on a Guld badge.</summary>
        public async Task<(bool Success, string? Message)> SetUniqueNumberAsync(int badgeId, string? uniqueNumber)
        {
            using var db = _databaseFactory.CreateDatabase();
            var badge = await db.SingleOrDefaultAsync<MemberBadge>("WHERE Id = @0", badgeId);
            if (badge == null) return (false, "Märket hittades inte.");
            if (badge.Level != Marken.LevelGuld)
                return (false, "Endast guldmärket har ett registreringsnummer.");

            badge.UniqueNumber = string.IsNullOrWhiteSpace(uniqueNumber) ? null : uniqueNumber.Trim();
            badge.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(badge);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteBadgeAsync(int badgeId)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM MemberBadge WHERE Id = @0", badgeId);
            return (true, null);
        }

        // ── Qualification (Guldfodring) reads/writes ──────────────────

        public async Task<MemberBadgeQualification?> GetQualificationAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<MemberBadgeQualification>("WHERE Id = @0", id);
        }

        public async Task<MemberBadgeQualification?> GetQualificationForYearAsync(int memberId, string family, int year)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<MemberBadgeQualification>(
                "WHERE MemberId = @0 AND BadgeFamily = @1 AND [Year] = @2", memberId, family, year);
        }

        public async Task<List<MemberBadgeQualification>> GetQualificationsForMemberAsync(int memberId, string? family = null, bool includeRejected = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("WHERE MemberId = @0", memberId);
            if (!string.IsNullOrEmpty(family))
                sql.Append("AND BadgeFamily = @0", family);
            if (!includeRejected)
                sql.Append("AND Status <> @0", Marken.StatusRejected);
            sql.Append("ORDER BY [Year] DESC");
            return await db.FetchAsync<MemberBadgeQualification>(sql);
        }

        /// <summary>
        /// Insert or update the year's qualification row, recomputing <c>Fulfilled</c>.
        /// Used by both the candidate engine (auto-fill parts) and the sign-off endpoints.
        /// </summary>
        public async Task<int> UpsertQualificationAsync(MemberBadgeQualification q)
        {
            q.Fulfilled = q.Part1Met && q.Part2Met;
            q.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();

            var existing = await db.SingleOrDefaultAsync<MemberBadgeQualification>(
                "WHERE MemberId = @0 AND BadgeFamily = @1 AND [Year] = @2", q.MemberId, q.BadgeFamily, q.Year);

            if (existing == null)
            {
                q.CreatedAt = DateTime.Now;
                await db.InsertAsync(q);
                return q.Id;
            }

            q.Id = existing.Id;
            q.CreatedAt = existing.CreatedAt;
            await db.UpdateAsync(q);
            return q.Id;
        }

        public async Task<(bool Success, string? Message)> SetQualificationStatusAsync(int id, string status, int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var q = await db.SingleOrDefaultAsync<MemberBadgeQualification>("WHERE Id = @0", id);
            if (q == null) return (false, "Guldfodringen hittades inte.");

            q.Status = status;
            if (status == Marken.StatusVerified)
            {
                if (!q.Fulfilled)
                    return (false, "Båda delarna måste vara klara innan guldfodringen kan signeras.");
                q.SignedOffByMemberId = actingMemberId;
                q.SignedOffDate = DateTime.Now;
            }
            q.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(q);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteQualificationAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM MemberBadgeQualification WHERE Id = @0", id);
            return (true, null);
        }

        // ── Märke series (Guldserier / Snabbserier) ───────────────────

        public async Task<MarkenSeries?> GetSeriesAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<MarkenSeries>("WHERE Id = @0", id);
        }

        public async Task<int> InsertSeriesAsync(MarkenSeries s)
        {
            s.CreatedAt = DateTime.Now;
            s.UpdatedAt = DateTime.Now;
            using var db = _databaseFactory.CreateDatabase();
            await db.InsertAsync(s);
            return s.Id;
        }

        public async Task<List<MarkenSeries>> GetSeriesForMemberAsync(int memberId, int? year = null, string? family = Marken.FamilyPistolskytte)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("WHERE MemberId = @0", memberId);
            if (!string.IsNullOrEmpty(family)) sql.Append("AND BadgeFamily = @0", family);
            if (year.HasValue) sql.Append("AND [Year] = @0", year.Value);
            sql.Append("ORDER BY SeriesDate DESC, Id DESC");
            return await db.FetchAsync<MarkenSeries>(sql);
        }

        /// <summary>Verified, qualifying Guld precision series for a year — the building blocks of a Guldfodring's precision part.</summary>
        public async Task<List<MarkenSeries>> GetVerifiedQualifyingPrecisionAsync(int memberId, int year, string family = Marken.FamilyPistolskytte)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<MarkenSeries>(
                @"WHERE MemberId = @0 AND BadgeFamily = @1 AND [Year] = @2 AND SeriesType = @3
                   AND Status = @4 AND Qualifies = 1 AND ClaimedLevel = @5
                   ORDER BY SeriesDate, Id",
                memberId, family, year, Marken.SeriesTypePrecision, Marken.StatusVerified, Marken.LevelGuld);
        }

        /// <summary>True if the member has a Verified Guld snabbserie this year (satisfies a Guldfodring's speed part).</summary>
        public async Task<MarkenSeries?> GetVerifiedSpeedAsync(int memberId, int year, string level = Marken.LevelGuld, string family = Marken.FamilyPistolskytte)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.SingleOrDefaultAsync<MarkenSeries>(
                @"WHERE MemberId = @0 AND BadgeFamily = @1 AND [Year] = @2 AND SeriesType = @3
                   AND Status = @4 AND ClaimedLevel = @5",
                memberId, family, year, Marken.SeriesTypeSpeed, Marken.StatusVerified, level);
        }

        /// <summary>All Verified series for a family (all years) — used by the series-proof analyzer.</summary>
        public async Task<List<MarkenSeries>> GetVerifiedSeriesByFamilyAsync(int memberId, string family)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<MarkenSeries>(
                "WHERE MemberId = @0 AND BadgeFamily = @1 AND Status = @2 ORDER BY [Year], Id",
                memberId, family, Marken.StatusVerified);
        }

        /// <summary>All Verified series for a member (all families) — the discipline-based analyzers
        /// (e.g. Elit reads precision + snabbpistol series regardless of which button entered them).</summary>
        public async Task<List<MarkenSeries>> GetAllVerifiedSeriesAsync(int memberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<MarkenSeries>(
                "WHERE MemberId = @0 AND Status = @1 ORDER BY [Year], Id", memberId, Marken.StatusVerified);
        }

        /// <summary>All Verified series validated for a club (any member) — feeds the club Guldserie-ligan.</summary>
        public async Task<List<MarkenSeries>> GetVerifiedSeriesForClubAsync(int clubId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<MarkenSeries>(
                "WHERE ClubId = @0 AND Status = @1 ORDER BY [Year], Id", clubId, Marken.StatusVerified);
        }

        /// <summary>Pending series awaiting validation, scoped to a set of clubs (the queue). Pass null for all (site admin).</summary>
        public async Task<List<MarkenSeries>> GetPendingSeriesAsync(IEnumerable<int>? clubIds)
        {
            using var db = _databaseFactory.CreateDatabase();
            if (clubIds == null) // site admin — everything pending
                return await db.FetchAsync<MarkenSeries>(
                    "WHERE Status = @0 ORDER BY CreatedAt", Marken.SeriesStatusPending);

            var ids = clubIds.Distinct().ToList();
            if (ids.Count == 0) return new List<MarkenSeries>();
            return await db.FetchAsync<MarkenSeries>(
                $"WHERE Status = @0 AND ClubId IN ({string.Join(",", ids)}) ORDER BY CreatedAt",
                Marken.SeriesStatusPending);
        }

        public async Task<(bool Success, string? Message)> SetSeriesStatusAsync(int id, string status, int validatorMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var s = await db.SingleOrDefaultAsync<MarkenSeries>("WHERE Id = @0", id);
            if (s == null) return (false, "Serien hittades inte.");

            s.Status = status;
            if (status == Marken.StatusVerified)
            {
                s.ValidatedByMemberId = validatorMemberId;
                s.ValidatedDate = DateTime.Now;
            }
            s.UpdatedAt = DateTime.Now;
            await db.UpdateAsync(s);
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteSeriesAsync(int id)
        {
            using var db = _databaseFactory.CreateDatabase();
            await db.ExecuteAsync("DELETE FROM MarkenSeries WHERE Id = @0", id);
            return (true, null);
        }

        // ── Skyttetrappan → Pistolskyttemärket valör link ─────────────

        /// <summary>
        /// Materialize Pistolskyttemärket base valörer (Brons/Silver/Guld) from completed Skyttetrappan
        /// levels 1/2/3. A valör is awarded once <b>all steps of its level</b> are completed; the badge
        /// captures the real completion date and the approver who cleared the last step. Idempotent —
        /// skips any valör the member already holds (incl. a rejected one, so it isn't resurrected).
        /// Returns the number of badges created.
        /// </summary>
        public async Task<int> SyncTrappaBadgesAsync(int memberId, List<StepCompletion>? completedSteps, int? actingApproverId)
        {
            if (completedSteps == null || completedSteps.Count == 0) return 0;

            var existing = await GetBadgesForMemberAsync(memberId, Marken.FamilyPistolskytte, includeRejected: true);
            int inserted = 0;

            var levelToValor = new (int LevelId, string Level)[]
            {
                (1, Marken.LevelBrons),
                (2, Marken.LevelSilver),
                (3, Marken.LevelGuld)
            };

            foreach (var (levelId, level) in levelToValor)
            {
                if (existing.Any(b => b.Level == level)) continue; // already held (or rejected) — don't duplicate/resurrect

                var def = TrainingDefinitions.GetLevel(levelId);
                if (def == null || def.Steps.Count == 0) continue;

                var stepsInLevel = completedSteps.Where(c => c.LevelId == levelId).ToList();
                bool allDone = def.Steps.All(s => stepsInLevel.Any(c => c.StepNumber == s.StepNumber));
                if (!allDone) continue;

                var last = stepsInLevel.OrderByDescending(c => c.CompletedDate).First();
                var badge = new MemberBadge
                {
                    MemberId = memberId,
                    BadgeFamily = Marken.FamilyPistolskytte,
                    Level = level,
                    LevelOrdinal = Marken.LevelOrdinal(level),
                    Discipline = "Precision",
                    AchievedYear = last.CompletedDate.Year,
                    AchievedDate = last.CompletedDate,
                    Source = Marken.SourceTrappa,
                    Status = Marken.StatusVerified,
                    SignedOffByMemberId = actingApproverId,
                    SignedOffDate = last.CompletedDate,
                    Notes = $"Automatiskt från Skyttetrappan ({def.Name})"
                            + (string.IsNullOrWhiteSpace(last.InstructorName) ? "" : $" – godkänd av {last.InstructorName}"),
                    EnteredByMemberId = actingApproverId ?? 0
                };
                await InsertBadgeAsync(badge);
                existing.Add(badge); // keep the in-memory list consistent for the loop
                inserted++;
            }

            return inserted;
        }

        // ── Årtalsmärke derivation ────────────────────────────────────

        /// <summary>
        /// Count of fulfilled, signed-off (Verified) Guldfodring-years for a member/family — the
        /// number that drives the årtalsmärke ladder. Set <paramref name="includeUnverified"/> for
        /// the member's own optimistic progress view.
        /// </summary>
        public async Task<int> GetFulfilledYearCountAsync(int memberId, string family, bool includeUnverified = false)
        {
            using var db = _databaseFactory.CreateDatabase();
            var sql = new Sql("SELECT COUNT(*) FROM MemberBadgeQualification WHERE MemberId = @0 AND BadgeFamily = @1 AND Fulfilled = 1", memberId, family);
            if (includeUnverified)
                sql.Append("AND Status <> @0", Marken.StatusRejected);
            else
                sql.Append("AND Status = @0", Marken.StatusVerified);
            return await db.ExecuteScalarAsync<int>(sql);
        }

        public async Task<ArtalsmarkeStatus> GetArtalsmarkeStatusAsync(int memberId, string family, bool includeUnverified = false)
        {
            int years = await GetFulfilledYearCountAsync(memberId, family, includeUnverified);
            // Family-aware ladder (Pistolskytte uses its own 17-step; the rest use their family ladder).
            var (name, nextAt) = MarkenFamilies.Artalsmarke(family, years);
            var nextName = nextAt > 0 ? MarkenFamilies.Artalsmarke(family, nextAt).Name : "";
            return new ArtalsmarkeStatus
            {
                FulfilledYears = years,
                CurrentName = name,
                NextName = nextName,
                NextAtYears = nextAt
            };
        }

        /// <summary>
        /// Idempotently ensure a verified badge at <paramref name="level"/> for a family (used by the
        /// auto-award of competition-driven valörer). Skips if any badge at that level already exists
        /// (incl. rejected, so it isn't resurrected).
        /// </summary>
        public async Task EnsureBadgeAsync(int memberId, string family, string level, int year, string source)
        {
            var existing = await GetBadgesForMemberAsync(memberId, family, includeRejected: true);
            if (existing.Any(b => b.Level == level)) return;
            await InsertBadgeAsync(new MemberBadge
            {
                MemberId = memberId,
                BadgeFamily = family,
                Level = level,
                LevelOrdinal = Marken.LevelOrdinal(level),
                AchievedYear = year,
                AchievedDate = DateTime.Now,
                Source = source,
                Status = Marken.StatusVerified,
                SignedOffDate = DateTime.Now,
                EnteredByMemberId = 0
            });
        }

        /// <summary>Idempotently materialize a Fulfilled + Verified årtalsmärke year for a family.</summary>
        public async Task EnsureFulfilledYearAsync(int memberId, string family, int year)
        {
            var q = await GetQualificationForYearAsync(memberId, family, year);
            if (q != null && q.Fulfilled && q.Status == Marken.StatusVerified) return;
            q ??= new MemberBadgeQualification { MemberId = memberId, BadgeFamily = family, Year = year, EnteredByMemberId = 0 };
            q.Part1Met = true;
            q.Part2Met = true;
            q.Fulfilled = true;
            q.Status = Marken.StatusVerified;
            q.SignedOffDate ??= DateTime.Now;
            await UpsertQualificationAsync(q);
        }

        // ── Club secretary reads ──────────────────────────────────────

        /// <summary>
        /// Member ids in the given list that have any non-rejected badge or qualification — so the
        /// secretary tab can show only members with märke activity.
        /// </summary>
        public async Task<HashSet<int>> GetMemberIdsWithActivityAsync(IEnumerable<int> memberIds)
        {
            var ids = memberIds?.Distinct().ToList() ?? new List<int>();
            var result = new HashSet<int>();
            if (ids.Count == 0) return result;

            using var db = _databaseFactory.CreateDatabase();
            var inClause = string.Join(",", ids);
            foreach (var id in await db.FetchAsync<int>(
                $"SELECT DISTINCT MemberId FROM MemberBadge WHERE MemberId IN ({inClause}) AND Status <> @0", Marken.StatusRejected))
                result.Add(id);
            foreach (var id in await db.FetchAsync<int>(
                $"SELECT DISTINCT MemberId FROM MemberBadgeQualification WHERE MemberId IN ({inClause}) AND Status <> @0", Marken.StatusRejected))
                result.Add(id);
            return result;
        }

        /// <summary>Every member id that has any non-rejected badge or qualification (the club summary filters by club).</summary>
        public async Task<List<int>> GetAllActiveMemberIdsAsync()
        {
            using var db = _databaseFactory.CreateDatabase();
            var ids = new HashSet<int>();
            foreach (var id in await db.FetchAsync<int>(
                "SELECT DISTINCT MemberId FROM MemberBadge WHERE Status <> @0", Marken.StatusRejected))
                ids.Add(id);
            foreach (var id in await db.FetchAsync<int>(
                "SELECT DISTINCT MemberId FROM MemberBadgeQualification WHERE Status <> @0", Marken.StatusRejected))
                ids.Add(id);
            return ids.ToList();
        }

        /// <summary>Count of reported-but-unverified badges + qualifications for a member (the sign-off queue size).</summary>
        public async Task<int> GetPendingCountAsync(int memberId, string? family = null)
        {
            using var db = _databaseFactory.CreateDatabase();
            int n = await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM MemberBadge WHERE MemberId = @0 AND Status = @1" + (family != null ? " AND BadgeFamily = @2" : ""),
                family != null ? new object[] { memberId, Marken.StatusReported, family } : new object[] { memberId, Marken.StatusReported });
            n += await db.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM MemberBadgeQualification WHERE MemberId = @0 AND Status = @1 AND Fulfilled = 1" + (family != null ? " AND BadgeFamily = @2" : ""),
                family != null ? new object[] { memberId, Marken.StatusReported, family } : new object[] { memberId, Marken.StatusReported });
            return n;
        }
    }

    /// <summary>Årtalsmärke ladder snapshot for one member/family.</summary>
    public class ArtalsmarkeStatus
    {
        public int FulfilledYears { get; set; }
        public string CurrentName { get; set; } = "";
        public string NextName { get; set; } = "";
        public int NextAtYears { get; set; }
    }
}
