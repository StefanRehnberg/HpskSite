using HpskSite.CompetitionTypes.Precision.Models;

namespace HpskSite.CompetitionTypes.Milsnabb.Services
{
    /// <summary>
    /// Milsnabb tiebreaker: count-back by 10-shot pairs (2 series at a time) from last to first.
    /// X count is already handled by the caller (ThenByDescending(s => s.TotalXCount)).
    /// This comparer handles the subsequent count-back when total score AND X count are equal.
    /// </summary>
    public class MilsnabbTieBreaker : IComparer<PrecisionShooterResult>
    {
        public int Compare(PrecisionShooterResult? x, PrecisionShooterResult? y)
        {
            if (x == null || y == null)
                return 0;

            var xScores = x.Results
                .OrderBy(r => r.SeriesNumber)
                .Select(r => CalculateSeriesScore(r.Shots))
                .ToList();

            var yScores = y.Results
                .OrderBy(r => r.SeriesNumber)
                .Select(r => CalculateSeriesScore(r.Shots))
                .ToList();

            int maxSeries = Math.Max(xScores.Count, yScores.Count);

            // Count-back by 10-shot pairs (2 series) from last to first.
            // For 12 series: pairs are (11,12), (9,10), (7,8), (5,6), (3,4), (1,2)
            // Pair index starts from the end and works backward in steps of 2.
            int pairStart = (maxSeries % 2 == 0) ? maxSeries - 2 : maxSeries - 1;

            for (int i = pairStart; i >= 0; i -= 2)
            {
                int xPairScore = GetScoreAt(xScores, i) + GetScoreAt(xScores, i + 1);
                int yPairScore = GetScoreAt(yScores, i) + GetScoreAt(yScores, i + 1);

                if (xPairScore != yPairScore)
                    return xPairScore.CompareTo(yPairScore);
            }

            // If all pairs are equal, they are truly tied
            return 0;
        }

        private static int GetScoreAt(List<int> scores, int index)
        {
            return index >= 0 && index < scores.Count ? scores[index] : 0;
        }

        private static int CalculateSeriesScore(string shotsJson)
        {
            try
            {
                if (string.IsNullOrEmpty(shotsJson))
                    return 0;

                var shots = Newtonsoft.Json.JsonConvert.DeserializeObject<List<string>>(shotsJson);
                if (shots == null || !shots.Any())
                    return 0;

                return shots.Sum(shot =>
                {
                    if (shot == "X" || shot == "x")
                        return 10;
                    if (int.TryParse(shot, out int value))
                        return value;
                    return 0;
                });
            }
            catch
            {
                return 0;
            }
        }
    }
}
