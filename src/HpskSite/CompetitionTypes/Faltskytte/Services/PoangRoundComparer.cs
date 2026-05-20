using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Poäng Fältskytte shoot-off round comparator.
    /// Within a round: (Hits + Figures) DESC → poängmål-total (TiebreakerScore) DESC.
    /// Display: "{Hits + Figures}p" (e.g. "10p" for 6 hits in 4 figures).
    /// </summary>
    public class PoangRoundComparer : IShootOffRoundComparer
    {
        public int Compare(FaltskytteShootOffEntry a, FaltskytteShootOffEntry b)
        {
            var pa = (a.Hits ?? 0) + (a.Figures ?? 0);
            var pb = (b.Hits ?? 0) + (b.Figures ?? 0);
            var pDiff = pb.CompareTo(pa);
            if (pDiff != 0) return pDiff;

            return (b.TiebreakerScore ?? 0).CompareTo(a.TiebreakerScore ?? 0);
        }

        public string FormatRound(FaltskytteShootOffEntry e)
            => $"{(e.Hits ?? 0) + (e.Figures ?? 0)}p";
    }
}
