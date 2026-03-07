using HpskSite.CompetitionTypes.Springskytte.Models;

namespace HpskSite.CompetitionTypes.Springskytte.Services
{
    /// <summary>
    /// Tiebreaker for Springskytte competitions.
    ///
    /// Rules:
    /// 1. Lowest TotalTimeSeconds wins
    /// 2. If tied: best shooting result (lowest ShootingScore)
    /// 3. If still tied: most hits at last shooting station, then second-to-last, etc.
    ///
    /// DNS/DNF are always sorted last.
    /// </summary>
    public class SpringskytteTieBreaker : IComparer<SpringskytteShooterResult>
    {
        public int Compare(SpringskytteShooterResult? x, SpringskytteShooterResult? y)
        {
            if (x == null && y == null) return 0;
            if (x == null) return 1;
            if (y == null) return -1;

            // DNS/DNF always last
            var xHasResult = x.Status == null && x.TotalTimeSeconds.HasValue;
            var yHasResult = y.Status == null && y.TotalTimeSeconds.HasValue;

            if (xHasResult && !yHasResult) return -1;
            if (!xHasResult && yHasResult) return 1;
            if (!xHasResult && !yHasResult)
            {
                // Both DNS/DNF: DNS before DNF, then by name
                return CompareStatus(x, y);
            }

            // 1. Lowest total time wins
            var timeDiff = x.TotalTimeSeconds!.Value.CompareTo(y.TotalTimeSeconds!.Value);
            if (timeDiff != 0) return timeDiff;

            // 2. Best shooting result (lowest score = fewer penalties)
            var shootingDiff = x.ShootingScore.CompareTo(y.ShootingScore);
            if (shootingDiff != 0) return shootingDiff;

            // 3. Count back from last stop: most hits at last station wins
            var xHits = x.HitsPerStop;
            var yHits = y.HitsPerStop;
            int maxStops = Math.Max(xHits.Count, yHits.Count);

            for (int i = maxStops - 1; i >= 0; i--)
            {
                int xHitsAtStop = i < xHits.Count ? xHits[i] : 0;
                int yHitsAtStop = i < yHits.Count ? yHits[i] : 0;

                // More hits = better (so y - x for descending)
                var hitsDiff = yHitsAtStop.CompareTo(xHitsAtStop);
                if (hitsDiff != 0) return hitsDiff;
            }

            // Completely tied
            return 0;
        }

        private static int CompareStatus(SpringskytteShooterResult x, SpringskytteShooterResult y)
        {
            // DNF before DNS (DNF at least started)
            int StatusOrder(string? s) => s switch
            {
                "DNF" => 0,
                "DNS" => 1,
                _ => 2
            };

            var diff = StatusOrder(x.Status).CompareTo(StatusOrder(y.Status));
            if (diff != 0) return diff;

            return string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        }
    }
}
