using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation
{
    public static class SeriesResultTieBreaker
    {
        /// <summary>
        /// Compare two rows by their competition scores in reverse chronological order.
        /// Returns negative if a wins, positive if b wins, 0 if tied.
        /// </summary>
        public static int Compare(SeriesStandingRow a, SeriesStandingRow b,
                                  List<SeriesCompetitionInfo> competitions)
        {
            for (int i = competitions.Count - 1; i >= 0; i--)
            {
                var compId = competitions[i].CompetitionId;
                var aScore = a.CompetitionScores.FirstOrDefault(c => c.CompetitionId == compId)?.Score ?? 0;
                var bScore = b.CompetitionScores.FirstOrDefault(c => c.CompetitionId == compId)?.Score ?? 0;
                if (aScore != bScore) return bScore.CompareTo(aScore); // DESC
            }
            return 0;
        }
    }
}
