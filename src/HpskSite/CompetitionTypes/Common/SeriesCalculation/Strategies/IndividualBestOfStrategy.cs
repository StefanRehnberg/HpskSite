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
                        cells.Add(new SeriesCompetitionCell
                        {
                            CompetitionId = comp.CompetitionId,
                            Score = score.TotalScore,
                            XCount = score.XCount,
                            Points = score.TotalScore,
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

                // Determine which competitions count: top N scores
                var scoredCells = cells.Where(c => c.Score.HasValue).OrderByDescending(c => c.Score).ToList();
                var countingCompIds = new HashSet<int>(scoredCells.Take(bestOf).Select(c => c.CompetitionId));

                int totalScore = 0;
                int totalXCount = 0;

                foreach (var cell in cells)
                {
                    if (cell.Score.HasValue && countingCompIds.Contains(cell.CompetitionId))
                    {
                        cell.Counting = true;
                        cell.Points = cell.Score;
                        totalScore += cell.Score.Value;
                        totalXCount += cell.XCount ?? 0;
                    }
                    else
                    {
                        cell.Counting = false;
                        cell.Points = null;
                    }
                }

                standingsByClass[key.ShootingClass].Add(new SeriesStandingRow
                {
                    Name = shooterInfo.Name,
                    Club = shooterInfo.Club,
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
