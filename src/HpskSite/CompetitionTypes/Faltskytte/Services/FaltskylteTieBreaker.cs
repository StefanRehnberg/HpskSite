using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Tiebreaker for Fältskytte results.
    ///
    /// Normal mode: hits DESC → figures DESC → poångmål DESC → last station hits DESC
    /// Poäng mode:  points DESC → poångmål DESC → last station points DESC
    /// </summary>
    public class FaltskylteTieBreaker : IComparer<FaltskytteShooterResult>
    {
        private readonly bool _isPoangMode;

        public FaltskylteTieBreaker(bool isPoangMode)
        {
            _isPoangMode = isPoangMode;
        }

        public int Compare(FaltskytteShooterResult? x, FaltskytteShooterResult? y)
        {
            if (x == null || y == null) return 0;

            if (_isPoangMode)
                return ComparePoang(x, y);
            else
                return CompareNormal(x, y);
        }

        private static int CompareNormal(FaltskytteShooterResult x, FaltskytteShooterResult y)
        {
            // 1. Total hits (higher is better)
            var cmp = x.TotalHits.CompareTo(y.TotalHits);
            if (cmp != 0) return cmp;

            // 2. Total figures (higher is better)
            cmp = x.TotalFigures.CompareTo(y.TotalFigures);
            if (cmp != 0) return cmp;

            // 3. Poångmål total (higher is better)
            cmp = x.TotalTiebreakerScore.CompareTo(y.TotalTiebreakerScore);
            if (cmp != 0) return cmp;

            // 4. Last station hits (higher is better)
            var xLast = x.Stations.LastOrDefault();
            var yLast = y.Stations.LastOrDefault();
            return (xLast?.Hits ?? 0).CompareTo(yLast?.Hits ?? 0);
        }

        private static int ComparePoang(FaltskytteShooterResult x, FaltskytteShooterResult y)
        {
            // 1. Total points (higher is better)
            var cmp = x.TotalPoints.CompareTo(y.TotalPoints);
            if (cmp != 0) return cmp;

            // 2. Poångmål total (higher is better)
            cmp = x.TotalTiebreakerScore.CompareTo(y.TotalTiebreakerScore);
            if (cmp != 0) return cmp;

            // 3. Last station points (higher is better)
            var xLast = x.Stations.LastOrDefault();
            var yLast = y.Stations.LastOrDefault();
            return (xLast?.Points ?? 0).CompareTo(yLast?.Points ?? 0);
        }
    }
}
