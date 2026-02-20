using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    /// <summary>
    /// Counts the number of 1st-place finishes per shooter per class across all competitions.
    /// Points column shows 1 for a win and 0 otherwise. TotalSeriesScore = number of wins.
    /// Tiebreak: most wins, then highest total raw score, then last-competition tiebreaker.
    /// </summary>
    public class IndividualWinsCountStrategy : ISeriesCalculationStrategy
    {
        public string Id => "IndividualWinsCount";
        public string Name => "Individuellt antal segrar";
        public string Description => "Räknar antal segrar (1:a plats) per skytt i varje klass. Flest segrar vinner.";

        public List<StrategyParameter> GetParameters() => new();

        public SeriesResultData Calculate(SeriesCalculationContext context)
        {
            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // First, determine the winner(s) of each competition per class
            // Winner = highest TotalScore in that class for that competition
            var winnersPerCompClass = new Dictionary<(int CompId, string ShootingClass), HashSet<int>>();

            foreach (var comp in context.Competitions)
            {
                var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                if (compScores == null || !compScores.Any()) continue;

                var byClass = compScores.GroupBy(s => s.ShootingClass);
                foreach (var classGroup in byClass)
                {
                    var maxScore = classGroup.Max(s => s.TotalScore);
                    var maxXCount = classGroup.Where(s => s.TotalScore == maxScore).Max(s => s.XCount);
                    var winners = classGroup
                        .Where(s => s.TotalScore == maxScore && s.XCount == maxXCount)
                        .Select(s => s.MemberId)
                        .ToHashSet();
                    winnersPerCompClass[(comp.CompetitionId, classGroup.Key)] = winners;
                }
            }

            // Gather all shooters
            var shooterKeys = new Dictionary<(int MemberId, string ShootingClass), ShooterCompetitionScore>();
            foreach (var (compId, scores) in context.CompetitionResults)
            {
                foreach (var score in scores)
                {
                    var key = (score.MemberId, score.ShootingClass);
                    if (!shooterKeys.ContainsKey(key))
                        shooterKeys[key] = score;
                }
            }

            // Build rows
            foreach (var (key, shooterInfo) in shooterKeys)
            {
                if (!standingsByClass.ContainsKey(key.ShootingClass))
                    standingsByClass[key.ShootingClass] = new List<SeriesStandingRow>();

                var cells = new List<SeriesCompetitionCell>();
                int totalWins = 0;
                int totalXCount = 0;

                foreach (var comp in context.Competitions)
                {
                    var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                    var score = compScores?.FirstOrDefault(s => s.MemberId == key.MemberId && s.ShootingClass == key.ShootingClass);

                    if (score != null)
                    {
                        var isWinner = winnersPerCompClass.TryGetValue((comp.CompetitionId, key.ShootingClass), out var winners)
                            && winners.Contains(key.MemberId);
                        var points = isWinner ? 1 : 0;
                        totalWins += points;
                        totalXCount += score.XCount;

                        cells.Add(new SeriesCompetitionCell
                        {
                            CompetitionId = comp.CompetitionId,
                            Score = score.TotalScore,
                            XCount = score.XCount,
                            Points = points,
                            Counting = true
                        });
                    }
                    else
                    {
                        cells.Add(new SeriesCompetitionCell
                        {
                            CompetitionId = comp.CompetitionId,
                            Score = null,
                            XCount = null,
                            Points = null,
                            Counting = true
                        });
                    }
                }

                standingsByClass[key.ShootingClass].Add(new SeriesStandingRow
                {
                    Name = shooterInfo.Name,
                    Club = HpskSite.Helpers.ClubNameHelper.Shorten(shooterInfo.Club),
                    EntityId = key.MemberId,
                    TotalSeriesScore = totalWins,
                    TotalXCount = totalXCount,
                    CompetitionScores = cells
                });
            }

            var classStandings = IndividualSumAllStrategy.RankAndOrderByClass(standingsByClass, context.Competitions);

            return new SeriesResultData
            {
                StrategyId = Id,
                StrategyName = Name,
                CalculatedAt = DateTime.UtcNow,
                Competitions = context.Competitions,
                Sections = new List<SeriesResultSection>
                {
                    new SeriesResultSection
                    {
                        SectionType = "Individual",
                        Title = "Individuellt",
                        ClassStandings = classStandings
                    }
                }
            };
        }
    }
}
