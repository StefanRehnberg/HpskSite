using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    /// <summary>
    /// Awards fixed points based on placement within each class per competition.
    /// Default table: [25,20,16,13,11,10,9,8,7,6,5,4,3,2,1].
    /// Tied shooters share the average of the positions they span.
    /// Series score = sum of points across all competitions.
    /// </summary>
    public class IndividualFixedPointsStrategy : ISeriesCalculationStrategy
    {
        public string Id => "IndividualFixedPoints";
        public string Name => "Individuellt fasta poäng";
        public string Description => "Tilldelar poäng baserat på placering per deltävling (t.ex. 25, 20, 16, 13...). Konfigurerbar poängtabell.";

        public List<StrategyParameter> GetParameters() => new()
        {
            new StrategyParameter
            {
                Key = "pointsTable",
                Label = "Poängtabell",
                Type = "select",
                DefaultValue = "25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1",
                Options = PointsTablePresets.Individual
            },
            new StrategyParameter
            {
                Key = "customPointsTable",
                Label = "Egen poängtabell (1:a, 2:a, 3:a... kommaseparerat)",
                Type = "string",
                DefaultValue = "10, 8, 6, 4, 2",
                Placeholder = "t.ex. 10, 8, 6, 4, 2",
                DependsOn = "pointsTable",
                DependsOnValue = "custom"
            }
        };

        public SeriesResultData Calculate(SeriesCalculationContext context)
        {
            var pointsTable = PlacementPointsCalculator.ResolvePointsTable(
                context.Parameters, "pointsTable", "customPointsTable");

            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // For each competition+class, rank shooters and assign points
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

                    var points = PlacementPointsCalculator.Calculate(entries, PlacementPointsCalculator.Mode.Fixed, pointsTable);

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
