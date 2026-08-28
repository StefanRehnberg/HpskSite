using HpskSite.Models;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Core.Web;
using Umbraco.Cms.Infrastructure.Persistence;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Materialises qualifying precision series shot at hosted pistol.nu competitions into
    /// <see cref="MarkenSeries"/>, so that ONE table answers "what guldserier does this shooter have".
    ///
    /// <para>
    /// Before this, a competition series was read live out of <c>PrecisionResultEntry</c> by
    /// <see cref="MarkenCandidateService"/> and existed nowhere else. Two faults followed, both
    /// reported by a club admin on 2026-08-28: the club Guldserie-ligan reads <c>MarkenSeries</c> and
    /// therefore never showed competition series, while the Guldfodring read both sources with no
    /// dedup and counted a hand-submitted competition series twice.
    /// </para>
    ///
    /// <para><b>RECONCILE, don't append.</b> Every sync recomputes what the competition results say and
    /// makes the ledger match — inserting what is missing, updating what changed and deleting rows whose
    /// source result is gone or no longer qualifies. That is what makes late corrections propagate:
    /// results are edited days after a competition, and a one-way "insert on save" hook would leave
    /// guldserier standing on scores that no longer exist. It is also idempotent, so it can run on read
    /// without piling anything up.</para>
    /// </summary>
    public class MarkenCompetitionSeriesSync
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly IUmbracoContextAccessor _umbracoContextAccessor;
        private readonly IMemberService _memberService;
        private readonly ILogger<MarkenCompetitionSeriesSync> _logger;

        public MarkenCompetitionSeriesSync(
            IUmbracoDatabaseFactory databaseFactory,
            IUmbracoContextAccessor umbracoContextAccessor,
            IMemberService memberService,
            ILogger<MarkenCompetitionSeriesSync> logger)
        {
            _databaseFactory = databaseFactory;
            _umbracoContextAccessor = umbracoContextAccessor;
            _memberService = memberService;
            _logger = logger;
        }

        /// <summary>What one reconciliation did. Returned so callers can log or assert on it.</summary>
        public record SyncResult(int Inserted, int Updated, int Deleted)
        {
            public static readonly SyncResult None = new(0, 0, 0);
            public bool Changed => Inserted > 0 || Updated > 0 || Deleted > 0;
            public override string ToString() => $"+{Inserted} ~{Updated} -{Deleted}";
        }

        /// <summary>Reconcile one member's materialised series for one year.</summary>
        public Task<SyncResult> SyncMemberYearAsync(int memberId, int year)
            => SyncManyAsync(new[] { memberId }, year);

        /// <summary>
        /// Reconcile EVERY shooter who has precision results, for one year. Used by the club surfaces
        /// (the Guldserie-ligan and the club Märken summary), which must show a member's competition
        /// series even if that member never logs in.
        /// <para>
        /// ⚠️ The member list is discovered from the RESULT table, deliberately not from a club roster
        /// or from <c>GetAllActiveMemberIdsAsync</c>: a shooter whose only guldserier come from
        /// competitions has no badge, no qualification and no submitted series, so any roster built out
        /// of märken data would miss exactly the case this class exists to fix.
        /// </para>
        /// <para>
        /// Bounded but not free (one member lookup each, for birth year and club), so callers cache it —
        /// see <c>MarkenController.EnsureCompetitionSeriesSyncedAsync</c>. 49 shooters / 24 competitions
        /// in dev; a krets-scale site is the same order.
        /// </para>
        /// </summary>
        public async Task<SyncResult> SyncYearFromResultsAsync(int year)
        {
            List<int> ids;
            try
            {
                using var db = _databaseFactory.CreateDatabase();
                ids = await db.FetchAsync<int>("SELECT DISTINCT MemberId FROM PrecisionResultEntry WHERE MemberId > 0");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Marken competition-series sync could not list shooters with results");
                return SyncResult.None;
            }
            return await SyncManyAsync(ids, year);
        }

        /// <summary>
        /// Reconcile a whole roster for one year in ONE pass — two reads plus writes only where
        /// something actually differs. The club Guldserie-ligan and the club Märken summary both need
        /// every member's series to be materialised before they read, and doing that member by member
        /// would be two queries per member on a page load.
        /// </summary>
        public async Task<SyncResult> SyncManyAsync(IEnumerable<int> memberIds, int year)
        {
            var ids = memberIds?.Where(i => i > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0) return SyncResult.None;

            if (!_umbracoContextAccessor.TryGetUmbracoContext(out var ctx) || ctx.Content == null)
            {
                // No published cache (a background thread without an UmbracoContext). Competition year
                // and club are only resolvable through it, so do nothing rather than guess — the next
                // read from a real request reconciles.
                _logger.LogDebug("Marken competition-series sync skipped: no UmbracoContext");
                return SyncResult.None;
            }

            List<ResultRow> rows;
            List<MarkenSeries> existing;
            try
            {
                rows = await ReadResultRowsAsync(ids);
                existing = await ReadMaterialisedAsync(ids, year);
            }
            catch (Exception ex)
            {
                // A missing column (migration not run) must not take down the page that reads märken.
                _logger.LogWarning(ex, "Marken competition-series sync could not read its inputs");
                return SyncResult.None;
            }

            // Resolve each competition once: year, name, and the shooter-independent club.
            var comps = new Dictionary<int, (int Year, string Name, DateTime? Date)>();
            foreach (var r in rows)
            {
                if (comps.ContainsKey(r.CompetitionId)) continue;
                var node = ctx.Content.GetById(r.CompetitionId);
                if (node == null || node.ContentType.Alias != "competition") { comps[r.CompetitionId] = (0, "", null); continue; }
                var compDate = node.Value<DateTime?>("competitionDate");
                comps[r.CompetitionId] = (compDate?.Year ?? 0, node.Name ?? "Tävling", compDate);
            }

            // Birth year drives the age-adjusted gold threshold, so it decides whether a series
            // qualifies at all. One lookup per member, not per row.
            var birthYears = ids.ToDictionary(id => id, id =>
            {
                var m = _memberService.GetById(id);
                return Marken.BirthYearFromPersonNumber(m?.GetValue("personNumber")?.ToString(), year);
            });
            var clubIds = ids.ToDictionary(id => id, GetPrimaryClubId);

            var desired = new Dictionary<int, MarkenSeries>();   // keyed by SourceResultId
            foreach (var r in rows)
            {
                if (!comps.TryGetValue(r.CompetitionId, out var ci) || ci.Year != year) continue;

                var group = Marken.WeaponGroup(r.ShootingClass);
                if (group == null) continue;

                int total = SumShots(r.Shots);
                int threshold = Marken.PrecisionThreshold(group, year, birthYears.GetValueOrDefault(r.MemberId));
                // Only QUALIFYING series are materialised. A precision competition holds 7-10 series per
                // shooter and materialising all of them would bury the ledger in rows that count toward
                // nothing — the Guldfodring and the liga are both about series that reach the gold krav.
                if (total < threshold) continue;

                desired[r.Id] = new MarkenSeries
                {
                    MemberId = r.MemberId,
                    // No club VALIDATED this series — the range officer's entry at the competition is the
                    // validation. The column therefore carries the shooter's OWN club here, which is what
                    // makes the club liga member-based (Stefan's call 2026-08-28): it lists the club's
                    // members' series wherever they were shot, not the series shot at its competitions.
                    ClubId = clubIds.GetValueOrDefault(r.MemberId),
                    BadgeFamily = Marken.FamilyPistolskytte,
                    SeriesType = Marken.SeriesTypePrecision,
                    Year = year,
                    // The COMPETITION's date, not EnteredAt: a range officer may type the row days later,
                    // and the series was shot on the day of the competition.
                    SeriesDate = ci.Date ?? r.EnteredAt,
                    WeaponGroup = group,
                    ClaimedLevel = Marken.LevelGuld,
                    Shots = r.Shots ?? "[]",
                    Total = total,
                    Threshold = threshold,
                    Qualifies = true,
                    Status = Marken.StatusVerified,
                    ValidatedDate = r.EnteredAt,
                    Notes = ci.Name,
                    SourceResultId = r.Id,
                    SourceCompetitionId = r.CompetitionId,
                    CountsTowardGuldfodring = true
                };
            }

            int inserted = 0, updated = 0, deleted = 0;
            using var db = _databaseFactory.CreateDatabase();

            foreach (var (sourceId, want) in desired)
            {
                var have = existing.FirstOrDefault(e => e.SourceResultId == sourceId);
                if (have == null)
                {
                    want.CreatedAt = DateTime.Now;
                    want.UpdatedAt = DateTime.Now;
                    try { await db.InsertAsync(want); inserted++; }
                    catch (Exception ex)
                    {
                        // The unique index is the backstop: a concurrent sync inserting the same source
                        // row loses here, which is exactly right — one result, one series.
                        _logger.LogDebug(ex, "Marken series for result {ResultId} already materialised", sourceId);
                    }
                    continue;
                }

                // A corrected result must move the series with it.
                if (have.Total == want.Total && have.Threshold == want.Threshold
                    && have.WeaponGroup == want.WeaponGroup && have.SeriesDate.Date == want.SeriesDate.Date
                    && have.ClubId == want.ClubId && have.Shots == want.Shots
                    && have.Status == want.Status) continue;

                have.Total = want.Total;
                have.Threshold = want.Threshold;
                have.Qualifies = true;
                have.WeaponGroup = want.WeaponGroup;
                have.SeriesDate = want.SeriesDate;
                have.ClubId = want.ClubId;
                have.Shots = want.Shots;
                have.Status = want.Status;
                have.Notes = want.Notes;
                have.SourceCompetitionId = want.SourceCompetitionId;
                // ⚠️ CountsTowardGuldfodring is NOT overwritten. An admin who excluded this series as a
                // duplicate must not have that decision undone by a re-sync.
                have.UpdatedAt = DateTime.Now;
                await db.UpdateAsync(have);
                updated++;
            }

            // Whatever the results no longer support must go: the result row was deleted, the score was
            // corrected below the krav, the class was changed to a weapon group with a higher krav, or a
            // personnummer arrived and moved the threshold. Leaving it would keep a guldserie standing on
            // a score that does not exist.
            foreach (var stale in existing.Where(e => e.SourceResultId.HasValue && !desired.ContainsKey(e.SourceResultId.Value)))
            {
                await db.ExecuteAsync("DELETE FROM MarkenSeries WHERE Id = @0", stale.Id);
                deleted++;
            }

            var result = new SyncResult(inserted, updated, deleted);
            if (result.Changed)
                _logger.LogInformation("Marken competition-series sync {Result} for {Count} member(s), year {Year}",
                    result, ids.Count, year);
            return result;
        }

        // ── Reads ────────────────────────────────────────────────────────────────────────────────
        // One row per SERIES (5 shots). Chunked because IN (@0) runs out at ~2100 parameters and does
        // so silently.
        private async Task<List<ResultRow>> ReadResultRowsAsync(List<int> ids)
        {
            var all = new List<ResultRow>();
            using var db = _databaseFactory.CreateDatabase();
            foreach (var chunk in Chunk(ids, 1000))
                all.AddRange(await db.FetchAsync<ResultRow>(
                    "SELECT Id, CompetitionId, MemberId, ShootingClass, Shots, EnteredAt " +
                    "FROM PrecisionResultEntry WHERE MemberId IN (@0)", chunk));
            return all;
        }

        private async Task<List<MarkenSeries>> ReadMaterialisedAsync(List<int> ids, int year)
        {
            var all = new List<MarkenSeries>();
            using var db = _databaseFactory.CreateDatabase();
            foreach (var chunk in Chunk(ids, 1000))
                all.AddRange(await db.FetchAsync<MarkenSeries>(
                    "WHERE MemberId IN (@0) AND [Year] = @1 AND SourceResultId IS NOT NULL", chunk, year));
            return all;
        }

        private static IEnumerable<List<int>> Chunk(List<int> ids, int size)
        {
            for (int i = 0; i < ids.Count; i += size) yield return ids.GetRange(i, Math.Min(size, ids.Count - i));
        }

        private int GetPrimaryClubId(int memberId)
        {
            // ⚠️ primaryClubId is a STRING property on the member type — GetValue<int> yields 0 without
            // converting, which is how walk-in registrations ended up with clubId=0 for months.
            var m = _memberService.GetById(memberId);
            var raw = m?.GetValue("primaryClubId")?.ToString();
            return int.TryParse(raw, out var id) ? id : 0;
        }

        private static int SumShots(string? shotsJson)
        {
            if (string.IsNullOrWhiteSpace(shotsJson)) return 0;
            try
            {
                var shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(shotsJson);
                if (shots == null) return 0;
                int total = 0;
                foreach (var s in shots)
                {
                    if (string.IsNullOrWhiteSpace(s)) continue;
                    if (s.Trim().Equals("X", StringComparison.OrdinalIgnoreCase)) total += 10;
                    else if (int.TryParse(s.Trim(), out var v)) total += v;
                }
                return total;
            }
            catch { return 0; }
        }

        private class ResultRow
        {
            public int Id { get; set; }
            public int CompetitionId { get; set; }
            public int MemberId { get; set; }
            public string ShootingClass { get; set; } = "";
            public string Shots { get; set; } = "";
            public DateTime EnteredAt { get; set; }
        }
    }
}
