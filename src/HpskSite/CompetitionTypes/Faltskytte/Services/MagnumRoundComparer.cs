using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Magnumfält shoot-off round comparator.
    /// SHB rule: shoot-off uses a specially configured station where all figures are
    /// poängmål and only one hit per figure is counted. The round score is the sum
    /// of <see cref="FaltskytteShootOffEntry.PoangmalScores"/> (mirrored in
    /// <see cref="FaltskytteShootOffEntry.TiebreakerScore"/>). Hits/Figures fields
    /// are not used for Magnum.
    /// Display: "{TiebreakerScore}p" (e.g. "23p").
    /// </summary>
    public class MagnumRoundComparer : IShootOffRoundComparer
    {
        public int Compare(FaltskytteShootOffEntry a, FaltskytteShootOffEntry b)
            => (b.TiebreakerScore ?? 0).CompareTo(a.TiebreakerScore ?? 0);

        public string FormatRound(FaltskytteShootOffEntry e)
            => $"{e.TiebreakerScore ?? 0}p";
    }
}
