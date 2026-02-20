using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    /// <summary>
    /// Awards points dynamically based on number of participants in the class for that competition.
    /// 1st place gets N points (where N = participant count), 2nd gets N-1, etc.
    /// Tied shooters share the average of the positions they span.
    /// Series score = sum of points across all competitions.
    /// </summary>
    public class IndividualDynamicPointsStrategy : ISeriesCalculationStrategy
    {
        public string Id => "IndividualDynamicPoints";
        public string Name => "Individuellt dynamiska poäng";
        public string Description => "Poäng baserat på antal deltagare. 1:a får lika många poäng som antal deltagare i klassen, 2:a får ett mindre, osv.";

        public List<StrategyParameter> GetParameters() => new();

        public SeriesResultData Calculate(SeriesCalculationContext context)
        {
            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // For each competition+class, rank shooters and assign dynamic points
            var pointsAwarded = new Dictionary<(int CompId, string ShootingClass, int MemberId), int>();

            foreach (var comp in context.Competitions)
            {
                var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                if (compScores == null || !compScores.Any()) continue;

                foreach (var classGroup in compScores.GroupBy(s => s.ShootingClass))
                {
                    var entries = classGroup
                        .Select(s => (s.MemberId, s.TotalScore, s.XCount))
                        .ToList();

                    var points = PlacementPointsCalculator.Calculate(entries, PlacementPointsCalculator.Mode.Dynamic);

                    foreach (var (memberId, pts) in points)
                    {
                        pointsAwarded[(comp.CompetitionId, classGroup.Key, memberId)] = pts;
                    }
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
                int totalPoints = 0;
                int totalXCount = 0;

                foreach (var comp in context.Competitions)
                {
                    var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                    var score = compScores?.FirstOrDefault(s => s.MemberId == key.MemberId && s.ShootingClass == key.ShootingClass);

                    if (score != null)
                    {
                        var pts = pointsAwarded.GetValueOrDefault((comp.CompetitionId, key.ShootingClass, key.MemberId), 0);
                        totalPoints += pts;
                        totalXCount += score.XCount;

                        cells.Add(new SeriesCompetitionCell
                        {
                            CompetitionId = comp.CompetitionId,
                            Score = score.TotalScore,
                            XCount = score.XCount,
                            Points = pts,
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
                    TotalSeriesScore = totalPoints,
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
