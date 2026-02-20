using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    public class IndividualBestOfStrategy : ISeriesCalculationStrategy
    {
        public string Id => "IndividualBestOf";
        public string Name => "Individuellt bästa N";
        public string Description => "Räknar de N bästa deltävlingsresultaten per skytt. Övriga visas genomstrukna.";

        public List<StrategyParameter> GetParameters() => new()
        {
            new StrategyParameter
            {
                Key = "bestOf",
                Label = "Antal bästa resultat",
                Type = "int",
                DefaultValue = 3
            },
            new StrategyParameter
            {
                Key = "placementPoints",
                Label = "Placeringspoäng",
                Type = "select",
                DefaultValue = "off",
                Options = new List<SelectOption>
                {
                    new() { Value = "off", Label = "Av (råpoäng)" },
                    new() { Value = "dynamic", Label = "Dynamisk (1:a = antal deltagare)" },
                    new() { Value = "fixed", Label = "Fast poängtabell" }
                }
            },
            new StrategyParameter
            {
                Key = "pointsTable",
                Label = "Poängtabell",
                Type = "select",
                DefaultValue = "25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1",
                Options = PointsTablePresets.Individual,
                DependsOn = "placementPoints",
                DependsOnValue = "fixed"
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
            int bestOf = 3;
            if (context.Parameters.TryGetValue("bestOf", out var bestOfObj))
            {
                if (bestOfObj is int intVal) bestOf = intVal;
                else if (bestOfObj is long longVal) bestOf = (int)longVal;
                else if (bestOfObj is string strVal && int.TryParse(strVal, out var parsed)) bestOf = parsed;
                else if (bestOfObj is System.Text.Json.JsonElement jsonEl && jsonEl.TryGetInt32(out var jsonInt)) bestOf = jsonInt;
            }

            var placementMode = PlacementPointsCalculator.ParseMode(
                PlacementPointsCalculator.ParseString(context.Parameters, "placementPoints", "off"));
            var pointsTable = PlacementPointsCalculator.ResolvePointsTable(
                context.Parameters, "pointsTable", "customPointsTable");

            bool usePlacement = placementMode != PlacementPointsCalculator.Mode.Off;

            // Pre-calculate placement points per competition+class if enabled
            var placementPoints = new Dictionary<(int CompId, string ShootingClass, int MemberId), int>();

            if (usePlacement)
            {
                foreach (var comp in context.Competitions)
                {
                    var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                    if (compScores == null || !compScores.Any()) continue;

                    foreach (var classGroup in compScores.GroupBy(s => s.ShootingClass))
                    {
                        var entries = classGroup
                            .Select(s => (s.MemberId, s.TotalScore, s.XCount))
                            .ToList();

                        var points = PlacementPointsCalculator.Calculate(entries, placementMode, pointsTable);

                        foreach (var (memberId, pts) in points)
                        {
                            placementPoints[(comp.CompetitionId, classGroup.Key, memberId)] = pts;
                        }
                    }
                }
            }

            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // Gather all shooters across all competitions, grouped by (MemberId, ShootingClass)
            var shooterKeys = new Dictionary<(int MemberId, string ShootingClass), ShooterCompetitionScore>();

            foreach (var (compId, scores) in context.CompetitionResults)
            {
                foreach (var score in scores)
                {
                    var key = (score.MemberId, score.ShootingClass);
                    if (!shooterKeys.ContainsKey(key))
                    {
                        shooterKeys[key] = score;
                    }
                }
            }

            // Build rows per class
            foreach (var (key, shooterInfo) in shooterKeys)
            {
                if (!standingsByClass.ContainsKey(key.ShootingClass))
                    standingsByClass[key.ShootingClass] = new List<SeriesStandingRow>();

                // Collect all competition cells for this shooter
                var cells = new List<SeriesCompetitionCell>();

                foreach (var comp in context.Competitions)
                {
                    var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                    var score = compScores?.FirstOrDefault(s => s.MemberId == key.MemberId && s.ShootingClass == key.ShootingClass);

                    if (score != null)
                    {
                        var cellPoints = usePlacement
                            ? placementPoints.GetValueOrDefault((comp.CompetitionId, key.ShootingClass, key.MemberId), 0)
                            : score.TotalScore;

                        cells.Add(new SeriesCompetitionCell
                        {
                            CompetitionId = comp.CompetitionId,
                            Score = score.TotalScore,
                            XCount = score.XCount,
                            Points = cellPoints,
                            Counting = true // Will be adjusted below
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
                            Counting = false
                        });
                    }
                }

                // Determine which competitions count: top N by points (placement or raw score)
                var scoredCells = cells.Where(c => c.Points.HasValue)
                    .OrderByDescending(c => c.Points)
                    .ToList();
                var countingCompIds = new HashSet<int>(scoredCells.Take(bestOf).Select(c => c.CompetitionId));

                int totalScore = 0;
                int totalXCount = 0;

                foreach (var cell in cells)
                {
                    if (cell.Points.HasValue && countingCompIds.Contains(cell.CompetitionId))
                    {
                        cell.Counting = true;
                        totalScore += cell.Points.Value;
                        totalXCount += cell.XCount ?? 0;
                    }
                    else
                    {
                        cell.Counting = false;
                    }
                }

                standingsByClass[key.ShootingClass].Add(new SeriesStandingRow
                {
                    Name = shooterInfo.Name,
                    Club = HpskSite.Helpers.ClubNameHelper.Shorten(shooterInfo.Club),
                    EntityId = key.MemberId,
                    TotalSeriesScore = totalScore,
                    TotalXCount = totalXCount,
                    CompetitionScores = cells
                });
            }

            // Reuse ranking logic from SumAll
            var classStandings = IndividualSumAllStrategy.RankAndOrderByClass(standingsByClass, context.Competitions);

            return new SeriesResultData
            {
                StrategyId = Id,
                StrategyName = $"Individuellt bästa {bestOf}",
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
