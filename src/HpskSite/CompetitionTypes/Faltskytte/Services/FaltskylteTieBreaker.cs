using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Tiebreaker for Fältskytte results.
    ///
    /// Normal mode: hits DESC → figures DESC → poångmål DESC → last station hits DESC
    /// Poäng mode:  points DESC → poångmål DESC → last station points DESC
    /// Magnumfält (Normal-läge, SHB E.6.11.2.1): hits → figures → poängmål → poäng på sista stationen, näst sista osv. →
    ///              träff på sista stationen, näst sista osv. (Stefan 2026-11-22: träff först).
    /// </summary>
    public class FaltskylteTieBreaker : IComparer<FaltskytteShooterResult>
    {
        private readonly bool _isPoangMode;
        private readonly bool _magnum;

        public FaltskylteTieBreaker(bool isPoangMode, bool magnum = false)
        {
            _isPoangMode = isPoangMode;
            _magnum = magnum && !isPoangMode;
        }

        public int Compare(FaltskytteShooterResult? x, FaltskytteShooterResult? y)
        {
            if (x == null || y == null) return 0;

            if (_isPoangMode)
                return ComparePoang(x, y);
            if (_magnum)
                return CompareMagnum(x, y);
            return CompareNormal(x, y);
        }

        private static int CompareMagnum(FaltskytteShooterResult x, FaltskytteShooterResult y)
        {
            var cmp = x.TotalHits.CompareTo(y.TotalHits);
            if (cmp != 0) return cmp;
            cmp = x.TotalFigures.CompareTo(y.TotalFigures);
            if (cmp != 0) return cmp;
            cmp = x.TotalTiebreakerScore.CompareTo(y.TotalTiebreakerScore);
            if (cmp != 0) return cmp;

            // 3. Poäng på sista stationen, näst sista osv.  4. Träff på sista stationen, näst sista osv.
            var xs = x.Stations.OrderByDescending(s => s.StationNumber).ToList();
            var ys = y.Stations.OrderByDescending(s => s.StationNumber).ToList();
            var n = Math.Max(xs.Count, ys.Count);
            for (var i = 0; i < n; i++)
            {
                cmp = (i < xs.Count ? xs[i].TiebreakerScore ?? 0 : 0).CompareTo(i < ys.Count ? ys[i].TiebreakerScore ?? 0 : 0);
                if (cmp != 0) return cmp;
            }
            for (var i = 0; i < n; i++)
            {
                cmp = (i < xs.Count ? xs[i].Hits : 0).CompareTo(i < ys.Count ? ys[i].Hits : 0);
                if (cmp != 0) return cmp;
            }
            return 0;
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
