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

        private static readonly int[] DefaultPointsTable = { 25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1 };

        public List<StrategyParameter> GetParameters() => new()
        {
            new StrategyParameter
            {
                Key = "pointsTable",
                Label = "Poängtabell (JSON-array, t.ex. [25,20,16,13,11,10,9,8,7,6,5,4,3,2,1])",
                Type = "string",
                DefaultValue = "[25,20,16,13,11,10,9,8,7,6,5,4,3,2,1]"
            }
        };

        public SeriesResultData Calculate(SeriesCalculationContext context)
        {
            var pointsTable = ParsePointsTable(context.Parameters);

            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // For each competition+class, rank shooters and assign points
            // Key: (compId, shootingClass, memberId) -> points awarded
            var pointsAwarded = new Dictionary<(int CompId, string ShootingClass, int MemberId), int>();

            foreach (var comp in context.Competitions)
            {
                var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                if (compScores == null || !compScores.Any()) continue;

                var byClass = compScores.GroupBy(s => s.ShootingClass);
                foreach (var classGroup in byClass)
                {
                    // Rank within this competition+class
                    var ranked = classGroup
                        .OrderByDescending(s => s.TotalScore)
                        .ThenByDescending(s => s.XCount)
                        .ToList();

                    // Assign points handling ties: tied shooters share the average
                    int i = 0;
                    while (i < ranked.Count)
                    {
                        int tieStart = i;
                        while (i + 1 < ranked.Count
                               && ranked[i + 1].TotalScore == ranked[tieStart].TotalScore
                               && ranked[i + 1].XCount == ranked[tieStart].XCount)
                        {
                            i++;
                        }

                        // Positions tieStart..i share points
                        int totalPointsForTie = 0;
                        for (int p = tieStart; p <= i; p++)
                        {
                            totalPointsForTie += p < pointsTable.Length ? pointsTable[p] : 0;
                        }
                        int sharedPoints = totalPointsForTie / (i - tieStart + 1);

                        for (int p = tieStart; p <= i; p++)
                        {
                            pointsAwarded[(comp.CompetitionId, classGroup.Key, ranked[p].MemberId)] = sharedPoints;
                        }

                        i++;
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
                    Club = shooterInfo.Club,
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

        private static int[] ParsePointsTable(Dictionary<string, object> parameters)
        {
            if (parameters.TryGetValue("pointsTable", out var ptObj))
            {
                string? jsonStr = null;
                if (ptObj is string s) jsonStr = s;
                else if (ptObj is System.Text.Json.JsonElement el && el.ValueKind == System.Text.Json.JsonValueKind.String)
                    jsonStr = el.GetString();
                else if (ptObj is System.Text.Json.JsonElement el2 && el2.ValueKind == System.Text.Json.JsonValueKind.Array)
                    jsonStr = el2.GetRawText();

                if (!string.IsNullOrEmpty(jsonStr))
                {
                    try
                    {
                        var parsed = System.Text.Json.JsonSerializer.Deserialize<int[]>(jsonStr);
                        if (parsed != null && parsed.Length > 0) return parsed;
                    }
                    catch { /* fall through to default */ }
                }
            }
            return DefaultPointsTable;
        }
    }
}
