using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Normal Fältskytte shoot-off round comparator.
    /// Within a round: Hits DESC → Figures DESC → poängmål-total (TiebreakerScore) DESC.
    /// Display: "{Hits}/{Figures}" (e.g. "5/4").
    /// </summary>
    public class NormalRoundComparer : IShootOffRoundComparer
    {
        public int Compare(FaltskytteShootOffEntry a, FaltskytteShootOffEntry b)
        {
            var hitDiff = (b.Hits ?? 0).CompareTo(a.Hits ?? 0);
            if (hitDiff != 0) return hitDiff;

            var figDiff = (b.Figures ?? 0).CompareTo(a.Figures ?? 0);
            if (figDiff != 0) return figDiff;

            return (b.TiebreakerScore ?? 0).CompareTo(a.TiebreakerScore ?? 0);
        }

        public string FormatRound(FaltskytteShootOffEntry e)
            => $"{e.Hits ?? 0}/{e.Figures ?? 0}";
    }
}
