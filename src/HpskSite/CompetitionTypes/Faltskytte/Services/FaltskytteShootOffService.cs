using HpskSite.CompetitionTypes.Faltskytte.Models;
using Microsoft.Extensions.Logging;
using NPoco;
using Umbraco.Cms.Infrastructure.Persistence;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// DB IO + tied-group detection + progressive resolution for Fältskytte
    /// Särskjutning. Drives the admin Särskjutning card and the public "Sär"
    /// column.
    ///
    /// The intra-round comparison is delegated to an <see cref="IShootOffRoundComparer"/>
    /// chosen via <see cref="ComparerFor(string?, string?)"/>:
    ///   - "Faltskytte" + "Normal" → <see cref="NormalRoundComparer"/>
    ///   - "Faltskytte" + "Poang"  → <see cref="PoangRoundComparer"/>
    ///   - "MagnumFalt"            → <see cref="MagnumRoundComparer"/>
    /// </summary>
    public class FaltskytteShootOffService
    {
        private readonly IUmbracoDatabaseFactory _databaseFactory;
        private readonly ILogger<FaltskytteShootOffService> _logger;

        public FaltskytteShootOffService(
            IUmbracoDatabaseFactory databaseFactory,
            ILogger<FaltskytteShootOffService> logger)
        {
            _databaseFactory = databaseFactory;
            _logger = logger;
        }

        // ── Comparer factory ──────────────────────────────────────────────

        /// <summary>Pick the round comparer that matches the competition's variation.</summary>
        public static IShootOffRoundComparer ComparerFor(string? competitionType, string? scoringMode)
        {
            if (string.Equals(competitionType, "MagnumFalt", StringComparison.OrdinalIgnoreCase))
                return new MagnumRoundComparer();
            if (string.Equals(scoringMode, "Poang", StringComparison.OrdinalIgnoreCase))
                return new PoangRoundComparer();
            return new NormalRoundComparer();
        }

        // ── DB reads / writes ─────────────────────────────────────────────

        public async Task<List<FaltskytteShootOffEntry>> GetEntriesForCompetitionAsync(int competitionId)
        {
            using var db = _databaseFactory.CreateDatabase();
            return await db.FetchAsync<FaltskytteShootOffEntry>(
                "WHERE CompetitionId = @0 ORDER BY MemberId, ShootingClass, Round",
                competitionId);
        }

        public async Task<(bool Success, string? Message)> SaveEntryAsync(
            int competitionId, int memberId, string shootingClass, int round,
            int? hits, int? figures, string? hitDistribution,
            int? tiebreakerScore, string? poangmalScores,
            int actingMemberId)
        {
            using var db = _databaseFactory.CreateDatabase();
            var existing = await db.SingleOrDefaultAsync<FaltskytteShootOffEntry>(
                @"WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2 AND Round = @3",
                competitionId, memberId, shootingClass, round);

            if (existing != null)
            {
                existing.Hits = hits;
                existing.Figures = figures;
                existing.HitDistribution = hitDistribution;
                existing.TiebreakerScore = tiebreakerScore;
                existing.PoangmalScores = poangmalScores;
                existing.EnteredBy = actingMemberId;
                existing.LastModified = DateTime.UtcNow;
                await db.UpdateAsync(existing);
            }
            else
            {
                await db.InsertAsync(new FaltskytteShootOffEntry
                {
                    CompetitionId = competitionId,
                    MemberId = memberId,
                    ShootingClass = shootingClass,
                    Round = round,
                    Hits = hits,
                    Figures = figures,
                    HitDistribution = hitDistribution,
                    TiebreakerScore = tiebreakerScore,
                    PoangmalScores = poangmalScores,
                    EnteredBy = actingMemberId,
                    EnteredAt = DateTime.UtcNow,
                    LastModified = DateTime.UtcNow
                });
            }
            return (true, null);
        }

        public async Task<(bool Success, string? Message)> DeleteEntryAsync(
            int competitionId, int memberId, string shootingClass, int round)
        {
            using var db = _databaseFactory.CreateDatabase();
            var affected = await db.ExecuteAsync(
                @"DELETE FROM FaltskytteShootOffEntry
                  WHERE CompetitionId = @0 AND MemberId = @1 AND ShootingClass = @2 AND Round = @3",
                competitionId, memberId, shootingClass, round);
            return (affected > 0, affected > 0 ? null : "Hittade ingen särskjutningspost att ta bort.");
        }

        // ── Tied-medal detection ──────────────────────────────────────────

        /// <summary>
        /// Find runs of consecutive shooters in the pre-sorted list that tie on the
        /// scoring-mode score AND overlap medal positions 1–3. Caller has already
        /// applied class merging and the existing tiebreaker sort.
        /// </summary>
        public static List<FaltskytteTiedMedalGroup> DetectTiedMedalGroups(
            List<FaltskytteShooterResult> sortedShooters,
            string scoringMode,
            string competitionType)
        {
            var result = new List<FaltskytteTiedMedalGroup>();
            if (sortedShooters == null || sortedShooters.Count < 2) return result;

            // The "score" for tie detection depends on the variation.
            int Score(FaltskytteShooterResult s)
            {
                if (string.Equals(competitionType, "MagnumFalt", StringComparison.OrdinalIgnoreCase))
                    return s.TotalPoints;
                if (string.Equals(scoringMode, "Poang", StringComparison.OrdinalIgnoreCase))
                    return s.TotalPoints;
                return s.TotalHits;
            }

            int i = 0;
            while (i < sortedShooters.Count)
            {
                int j = i + 1;
                while (j < sortedShooters.Count && Score(sortedShooters[j]) == Score(sortedShooters[i]))
                    j++;

                int groupSize = j - i;
                if (groupSize >= 2)
                {
                    int firstRank = i + 1;
                    int lastRank = j;
                    bool overlapsMedalTier = firstRank <= 3;
                    if (overlapsMedalTier)
                    {
                        result.Add(new FaltskytteTiedMedalGroup
                        {
                            MedalTier = MedalTierLabel(firstRank, lastRank),
                            FirstRank = firstRank,
                            LastRank = lastRank,
                            TiedScore = Score(sortedShooters[i]),
                            Shooters = sortedShooters
                                .GetRange(i, groupSize)
                                .Select(s => new FaltskytteTiedMedalShooter
                                {
                                    MemberId = s.MemberId,
                                    Name = s.Name,
                                    Club = s.Club,
                                    ShootingClass = s.ShootingClass,
                                    TotalHits = s.TotalHits,
                                    TotalFigures = s.TotalFigures,
                                    TotalPoints = s.TotalPoints,
                                    TotalTiebreakerScore = s.TotalTiebreakerScore
                                })
                                .ToList()
                        });
                    }
                }

                i = j;
            }

            return result;
        }

        // ── Override application ──────────────────────────────────────────

        /// <summary>
        /// Re-orders each tied medal-group slice in <paramref name="sortedShooters"/>
        /// based on shoot-off entries, applies progressive resolution, and annotates
        /// each <see cref="FaltskytteShooterResult"/> with ShootOff* fields.
        /// Mirrors the per-shooter status logic used by the precision-family service.
        /// </summary>
        public static void ApplyShootOffOverride(
            List<FaltskytteShooterResult> sortedShooters,
            List<FaltskytteTiedMedalGroup> tiedGroups,
            ILookup<int, FaltskytteShootOffEntry> entriesByMember,
            IShootOffRoundComparer comparer)
        {
            foreach (var group in tiedGroups)
            {
                // Build per-shooter round map keyed by Round.
                var perShooterEntries = new Dictionary<int, Dictionary<int, FaltskytteShootOffEntry>>();
                foreach (var s in group.Shooters)
                {
                    perShooterEntries[s.MemberId] = entriesByMember[s.MemberId]
                        .Where(e => string.Equals(e.ShootingClass, s.ShootingClass, StringComparison.OrdinalIgnoreCase))
                        .GroupBy(e => e.Round)
                        .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.LastModified).First());
                }

                int maxRound = perShooterEntries.Values.SelectMany(d => d.Keys).DefaultIfEmpty(0).Max();
                group.RoundsCompleted = maxRound;

                if (maxRound > 0)
                {
                    var liveShooters = sortedShooters
                        .GetRange(group.FirstRank - 1, group.Shooters.Count)
                        .ToList();

                    // Lex-across-rounds comparator via the strategy.
                    int CompareLex(FaltskytteShooterResult a, FaltskytteShooterResult b)
                    {
                        var aRounds = perShooterEntries.TryGetValue(a.MemberId, out var ar) ? ar : new();
                        var bRounds = perShooterEntries.TryGetValue(b.MemberId, out var br) ? br : new();
                        for (int r = 1; r <= maxRound; r++)
                        {
                            var ae = aRounds.TryGetValue(r, out var av) ? av : new FaltskytteShootOffEntry();
                            var be = bRounds.TryGetValue(r, out var bv) ? bv : new FaltskytteShootOffEntry();
                            int diff = comparer.Compare(ae, be);
                            if (diff != 0) return diff;
                        }
                        return 0;
                    }

                    var ordered = liveShooters
                        .OrderBy(s => s, Comparer<FaltskytteShooterResult>.Create(CompareLex))
                        .ToList();

                    for (int k = 0; k < ordered.Count; k++)
                        sortedShooters[group.FirstRank - 1 + k] = ordered[k];

                    foreach (var s in ordered)
                    {
                        if (!perShooterEntries.TryGetValue(s.MemberId, out var rounds) || rounds.Count == 0)
                        {
                            s.ShootOffRounds = null;
                            continue;
                        }
                        s.ShootOffRounds = rounds
                            .OrderBy(kv => kv.Key)
                            .Select(kv => comparer.FormatRound(kv.Value))
                            .ToList();
                    }
                }

                // Per-shooter resolution status (also mirrors back onto the DTO list).
                ComputeProgressiveStatus(sortedShooters, group, perShooterEntries, comparer);

                // Group resolved when every DTO shooter is uniquely placed.
                group.Resolved = group.Shooters.All(s => s.IsResolved);
            }
        }

        // ── Progressive resolution ────────────────────────────────────────

        internal static void ComputeProgressiveStatus(
            List<FaltskytteShooterResult> sortedShooters,
            FaltskytteTiedMedalGroup group,
            Dictionary<int, Dictionary<int, FaltskytteShootOffEntry>> perShooterEntries,
            IShootOffRoundComparer comparer)
        {
            const int hardCap = 50;
            var liveSlice = sortedShooters
                .GetRange(group.FirstRank - 1, group.Shooters.Count)
                .ToList();

            foreach (var s in liveSlice)
            {
                s.ShootOffIsResolved = false;
                s.ShootOffNextRound = null;

                var sRounds = perShooterEntries.TryGetValue(s.MemberId, out var sr) ? sr : new();
                var stillTied = liveSlice.Where(t => t.MemberId != s.MemberId).ToList();
                if (stillTied.Count == 0)
                {
                    s.ShootOffIsResolved = true;
                    continue;
                }

                for (int r = 1; r <= hardCap; r++)
                {
                    bool sHasR = sRounds.ContainsKey(r);
                    if (!sHasR)
                    {
                        s.ShootOffNextRound = r;
                        break;
                    }

                    var sEntry = sRounds[r];
                    var nextRoundOpponents = new List<FaltskytteShooterResult>();
                    bool waiting = false;

                    foreach (var t in stillTied)
                    {
                        var tRounds = perShooterEntries.TryGetValue(t.MemberId, out var tr) ? tr : new();
                        if (tRounds.TryGetValue(r, out var tEntry))
                        {
                            if (comparer.Compare(sEntry, tEntry) == 0)
                                nextRoundOpponents.Add(t);
                        }
                        else
                        {
                            waiting = true;
                            nextRoundOpponents.Add(t);
                        }
                    }
                    stillTied = nextRoundOpponents;

                    if (stillTied.Count == 0)
                    {
                        s.ShootOffIsResolved = true;
                        break;
                    }
                    if (waiting)
                    {
                        s.ShootOffNextRound = null;
                        break;
                    }
                }
            }

            // Mirror the per-shooter status onto the DTO list so the JSON sent to the
            // admin UI reflects which shooters still need to shoot.
            for (int i = 0; i < group.Shooters.Count; i++)
            {
                var dto = group.Shooters[i];
                var live = liveSlice.FirstOrDefault(l => l.MemberId == dto.MemberId);
                if (live == null) continue;
                dto.IsResolved = live.ShootOffIsResolved;
                dto.NextRound = live.ShootOffNextRound;

                if (perShooterEntries.TryGetValue(dto.MemberId, out var rounds) && rounds.Count > 0)
                {
                    dto.Rounds = rounds
                        .OrderBy(kv => kv.Key)
                        .Select(kv => new FaltskytteShootOffRoundSummary
                        {
                            Round = kv.Key,
                            Display = comparer.FormatRound(kv.Value),
                            Hits = kv.Value.Hits,
                            Figures = kv.Value.Figures,
                            TiebreakerScore = kv.Value.TiebreakerScore,
                            PoangmalScores = kv.Value.PoangmalScores,
                            HitDistribution = kv.Value.HitDistribution
                        })
                        .ToList();
                }
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────

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
}
