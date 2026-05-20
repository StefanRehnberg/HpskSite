using HpskSite.CompetitionTypes.Common.Utilities;
using HpskSite.CompetitionTypes.Precision.Models;
using HpskSite.Models;
using Newtonsoft.Json;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.Services
{
    /// <summary>
    /// Reads, writes, and applies CompetitionShootOffEntry rows for Championship
    /// competitions. Only governs ranking *within* tied medal-tier groups
    /// (ranks 1–3 on identical TotalScore). Other ranks fall through to existing
    /// tie-breakers untouched.
    /// </summary>
    public class ShootOffService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<ShootOffService> _logger;

        public ShootOffService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<ShootOffService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Reads ─────────────────────────────────────────────────────

        public async Task<List<CompetitionShootOffEntry>> GetEntriesForCompetitionAsync(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<CompetitionShootOffEntry>(
                "WHERE CompetitionId = @0 ORDER BY MemberId, ShootingClass, Round, SeriesNumber",
                competitionId);
        }

        // ── Writes ────────────────────────────────────────────────────

        public async Task<(bool Success, string? Message)> SaveEntryAsync(
            int competitionId, int memberId, string shootingClass, int round,
            string shotsJson, int actingMemberId, int seriesNumber = 1)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<CompetitionShootOffEntry>(
                @"WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2
                   AND Round = @3 AND SeriesNumber = @4",
                competitionId, memberId, shootingClass, round, seriesNumber);

            if (existing != null)
            {
                existing.Shots = shotsJson;
                existing.EnteredBy = actingMemberId;
                existing.LastModified = DateTime.Now;
                await db.UpdateAsync(existing);
            }
            else
            {
                await db.InsertAsync(new CompetitionShootOffEntry
                {
                    CompetitionId = competitionId,
                    MemberId = memberId,
                    ShootingClass = shootingClass,
                    Round = round,
                    SeriesNumber = seriesNumber,
                    Shots = shotsJson,
                    EnteredBy = actingMemberId,
                    EnteredAt = DateTime.Now,
                    LastModified = DateTime.Now
                });
            }

            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteEntryAsync(
            int competitionId, int memberId, string shootingClass, int round, int seriesNumber = 1)
        {
            using var db = _databaseFactory.CreateDatabase();
            var affected = await db.ExecuteAsync(
                @"DELETE FROM CompetitionShootOffEntry
                   WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2
                     AND Round = @3 AND SeriesNumber = @4",
                competitionId, memberId, shootingClass, round, seriesNumber);

            return (affected > 0, affected > 0 ? null : "Hittade ingen särskjutningspost att ta bort.");
        }

        // ── Tied-group detection + override application ───────────────

        /// <summary>
        /// Inspect a class group's *already sorted* shooters list and return the medal-tier
        /// runs where two or more consecutive shooters share the same TotalScore AND the run
        /// overlaps ranks 1, 2, or 3.
        /// </summary>
        public static List<TiedMedalGroup> DetectTiedMedalGroups(List<PrecisionShooterResult> sortedShooters, string mergedClassKey)
        {
            var result = new List<TiedMedalGroup>();
            if (sortedShooters == null || sortedShooters.Count < 2) return result;

            int i = 0;
            while (i < sortedShooters.Count)
            {
                int j = i + 1;
                while (j < sortedShooters.Count && sortedShooters[j].TotalScore == sortedShooters[i].TotalScore)
                    j++;

                int groupSize = j - i;
                if (groupSize >= 2)
                {
                    int firstRank = i + 1;
                    int lastRank = j; // inclusive last index = j-1, rank = j
                    bool overlapsMedalTier = firstRank <= 3;
                    if (overlapsMedalTier)
                    {
                        result.Add(new TiedMedalGroup
                        {
                            MergedClassKey = mergedClassKey,
                            MedalTier = MedalTierLabel(firstRank, lastRank),
                            FirstRank = firstRank,
                            LastRank = lastRank,
                            TotalScore = sortedShooters[i].TotalScore,
                            Shooters = sortedShooters.GetRange(i, groupSize)
                        });
                    }
                }

                i = j;
            }

            return result;
        }

        /// <summary>
        /// For each tied medal group passed in, apply shoot-off entries (if any) to re-order
        /// just that contiguous slice of the sorted list. Annotates each affected shooter's
        /// ShootOffScore/ShootOffXCount/ShootOffRound. Sets each group's <c>Resolved</c> flag
        /// to indicate whether the entries fully separated the shooters.
        /// </summary>
        public static void ApplyShootOffOverride(
            List<PrecisionShooterResult> sortedShooters,
            List<TiedMedalGroup> tiedGroups,
            ILookup<int, CompetitionShootOffEntry> entriesByMember)
        {
            foreach (var group in tiedGroups)
            {
                // Build per-shooter cumulative shoot-off totals per round.
                // Dict: memberId -> Dict<round, (total, x, raw entry)>
                var perShooter = new Dictionary<int, Dictionary<int, (int total, int xCount)>>();
                foreach (var s in group.Shooters)
                {
                    var byRound = entriesByMember[s.MemberId]
                        .Where(e => string.Equals(e.ShootingClass, s.ShootingClass, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(e => e.Round)
                        .ToDictionary(
                            g => g.Key,
                            g => (
                                total: g.Sum(e => ParseShotsTotal(e.Shots)),
                                xCount: g.Sum(e => ParseShotsXCount(e.Shots))
                            ));
                    perShooter[s.MemberId] = byRound;
                }

                // Highest round any shooter in this group has any entry for
                int maxRound = perShooter.Values.SelectMany(d => d.Keys).DefaultIfEmpty(0).Max();
                group.RoundsCompleted = maxRound;

                // Compute progressive resolution status for each shooter in the group.
                // Even when maxRound == 0 (no shots entered yet), we still annotate everyone
                // with NextRound = 1 so the UI knows to surface the entry button.
                ComputeProgressiveStatus(group.Shooters, perShooter);

                if (maxRound > 0)
                {
                    // Sub-sort by Round 1 total DESC, then Round 2 total DESC, ... up to maxRound.
                    // A shooter without an entry for a round contributes 0 for that round.
                    var slice = group.Shooters.OrderByDescending(s =>
                    {
                        var rounds = perShooter[s.MemberId];
                        return rounds.TryGetValue(1, out var r) ? r.total : 0;
                    });
                    for (int r = 2; r <= maxRound; r++)
                    {
                        int round = r;
                        slice = slice.ThenByDescending(s =>
                            perShooter[s.MemberId].TryGetValue(round, out var rec) ? rec.total : 0);
                    }
                    var ordered = slice.ToList();

                    // Replace the slice in the sorted list (preserving the indices ranges)
                    for (int k = 0; k < ordered.Count; k++)
                    {
                        sortedShooters[group.FirstRank - 1 + k] = ordered[k];
                    }

                    // Annotate each shooter with their cumulative shoot-off summary.
                    foreach (var s in ordered)
                    {
                        if (!perShooter.TryGetValue(s.MemberId, out var rounds) || rounds.Count == 0)
                        {
                            s.ShootOffScore = null;
                            s.ShootOffXCount = null;
                            s.ShootOffRound = null;
                            s.ShootOffRoundTotals = null;
                            continue;
                        }
                        int highestRound = rounds.Keys.Max();
                        s.ShootOffRound = highestRound;
                        s.ShootOffScore = rounds[highestRound].total;
                        s.ShootOffXCount = rounds[highestRound].xCount;
                        s.ShootOffRoundTotals = rounds.OrderBy(kv => kv.Key).Select(kv => kv.Value.total).ToList();
                    }
                }

                // Group resolved when every shooter in it is uniquely placed.
                group.Resolved = group.Shooters.All(s => s.ShootOffIsResolved);
            }
        }

        // ── Progressive shoot-off resolution ──────────────────────────
        // Real-world Särskjutning rule: tied shooters shoot together; whoever is uniquely
        // separated by their round score keeps their placement and is done. Shooters who
        // still share a score must shoot another round amongst themselves. Different
        // medal positions can be decided in different rounds.

        /// <summary>
        /// Walks each shooter against their original tied opponents round-by-round and sets:
        /// - <see cref="PrecisionShooterResult.ShootOffIsResolved"/> = true when their position
        ///   is uniquely decided by the rounds entered so far,
        /// - <see cref="PrecisionShooterResult.ShootOffNextRound"/> = the round they need to
        ///   shoot next, or null when either resolved or waiting for opponents.
        /// </summary>
        internal static void ComputeProgressiveStatus(
            List<PrecisionShooterResult> groupShooters,
            Dictionary<int, Dictionary<int, (int total, int xCount)>> perShooter)
        {
            const int hardCap = 50; // safety bound — championship shoot-offs do not run beyond a handful of rounds.

            foreach (var s in groupShooters)
            {
                s.ShootOffIsResolved = false;
                s.ShootOffNextRound = null;

                var sRounds = perShooter.TryGetValue(s.MemberId, out var sr) ? sr : new Dictionary<int, (int total, int xCount)>();
                // Opponents start as everyone else in the original tied group.
                var stillTied = groupShooters
                    .Where(t => t.MemberId != s.MemberId)
                    .ToList();

                if (stillTied.Count == 0)
                {
                    // Singleton — already unique. Shouldn't happen because TiedMedalGroup is size ≥ 2.
                    s.ShootOffIsResolved = true;
                    continue;
                }

                for (int r = 1; r <= hardCap; r++)
                {
                    bool sHasR = sRounds.ContainsKey(r);

                    if (!sHasR)
                    {
                        // S hasn't shot round r yet AND opponents remain → S must shoot round r.
                        s.ShootOffNextRound = r;
                        break;
                    }

                    int sScore = sRounds[r].total;
                    var nextRoundOpponents = new List<PrecisionShooterResult>();
                    bool waitingForOthers = false;

                    foreach (var t in stillTied)
                    {
                        var tRounds = perShooter.TryGetValue(t.MemberId, out var trMap)
                            ? trMap : new Dictionary<int, (int total, int xCount)>();
                        if (tRounds.TryGetValue(r, out var tEntry))
                        {
                            if (tEntry.total == sScore)
                                nextRoundOpponents.Add(t); // still tied with S after round r
                            // else: t is separated from S — drop them.
                        }
                        else
                        {
                            // t hasn't shot round r yet; cannot rule out tying with S — keep as still-tied
                            // and remember we're waiting on someone to shoot.
                            waitingForOthers = true;
                            nextRoundOpponents.Add(t);
                        }
                    }

                    stillTied = nextRoundOpponents;

                    if (stillTied.Count == 0)
                    {
                        // S is uniquely placed after round r.
                        s.ShootOffIsResolved = true;
                        break;
                    }

                    if (waitingForOthers)
                    {
                        // S has shot round r and is still tied with someone who hasn't entered round r yet.
                        // S is "waiting" — no action button surfaced.
                        s.ShootOffNextRound = null;
                        break;
                    }

                    // All still-tied opponents shot round r and matched S's score → continue to round r+1.
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────

        private static int ParseShotsTotal(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<List<string>>(shotsJson) ?? new();
                return (int)ScoringUtilities.CalculateTotal(shots);
            }
            catch { return 0; }
        }

        private static int ParseShotsXCount(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<List<string>>(shotsJson) ?? new();
                return ScoringUtilities.CountInnerTens(shots);
            }
            catch { return 0; }
        }

        /// <summary>
        /// Build a medal-tier label that covers every medal slot the tied group occupies.
        /// A 4-way tie at rank 1 (lastRank=4) blocks Guld, Silver, AND Brons; the badge
        /// must say so. Positions beyond rank 3 are not medal slots and are excluded.
        /// </summary>
        private static string MedalTierLabel(int firstRank, int lastRank)
        {
            int last = Math.Min(lastRank, 3);
            var tiers = new List<string>();
            for (int r = firstRank; r <= last; r++)
            {
                tiers.Add(r switch
                {
                    1 => "Guld",
                    2 => "Silver",
                    3 => "Brons",
                    _ => $"Plats {r}"
                });
            }
            return string.Join(" + ", tiers);
        }

        /// <summary>
        /// Build the Swedish definite-form list of medal nouns covered by this tied group,
        /// e.g. "guldet, silvret och bronset" for a 4-way tie at rank 1.
        /// </summary>
        public static string MedalNounsForRange(int firstRank, int lastRank)
        {
            int last = Math.Min(lastRank, 3);
            var nouns = new List<string>();
            for (int r = firstRank; r <= last; r++)
            {
                nouns.Add(r switch
                {
                    1 => "guldet",
                    2 => "silvret",
                    3 => "bronset",
                    _ => $"plats {r}"
                });
            }
            if (nouns.Count == 0) return "";
            if (nouns.Count == 1) return nouns[0];
            if (nouns.Count == 2) return $"{nouns[0]} och {nouns[1]}";
            return string.Join(", ", nouns.Take(nouns.Count - 1)) + " och " + nouns.Last();
        }
    }

    /// <summary>DTO returned by detection — informs the admin UI and the response JSON.</summary>
    public class TiedMedalGroup
    {
        public string MergedClassKey { get; set; } = "";
        public string MedalTier { get; set; } = ""; // "Guld" / "Silver" / "Brons"
        public int FirstRank { get; set; }
        public int LastRank { get; set; }
        public int TotalScore { get; set; }
        public int RoundsCompleted { get; set; }
        public bool Resolved { get; set; }
        public List<PrecisionShooterResult> Shooters { get; set; } = new();
    }
}
