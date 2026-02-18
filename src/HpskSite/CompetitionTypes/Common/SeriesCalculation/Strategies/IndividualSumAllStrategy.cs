using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    public class IndividualSumAllStrategy : ISeriesCalculationStrategy
    {
        public string Id => "IndividualSumAll";
        public string Name => "Individuellt totalsumma";
        public string Description => "Summerar alla tävlingsresultat per skytt. Alla deltävlingar räknas.";

        public List<StrategyParameter> GetParameters() => new();

        public SeriesResultData Calculate(SeriesCalculationContext context)
        {
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

                var cells = new List<SeriesCompetitionCell>();
                int totalScore = 0;
                int totalXCount = 0;

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
                            Counting = true
                        });
                        totalScore += score.TotalScore;
                        totalXCount += score.XCount;
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
                    TotalSeriesScore = totalScore,
                    TotalXCount = totalXCount,
                    CompetitionScores = cells
                });
            }

            // Rank and order by class
            var classStandings = RankAndOrderByClass(standingsByClass, context.Competitions);

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

        internal static List<SeriesClassStandings> RankAndOrderByClass(
            Dictionary<string, List<SeriesStandingRow>> standingsByClass,
            List<SeriesCompetitionInfo> competitions)
        {
            var classOrder = GetClassOrder();

            return standingsByClass
                .OrderBy(kvp => classOrder.GetValueOrDefault(kvp.Key, 999))
                .Select(kvp =>
                {
                    var rows = kvp.Value
                        .OrderByDescending(r => r.TotalSeriesScore)
                        .ThenByDescending(r => r.TotalXCount)
                        .ThenBy(r => r, Comparer<SeriesStandingRow>.Create((a, b) =>
                            SeriesResultTieBreaker.Compare(a, b, competitions)))
                        .ToList();

                    int rank = 0;
                    int prevScore = -1;
                    int prevXCount = -1;
                    for (int i = 0; i < rows.Count; i++)
                    {
                        if (rows[i].TotalSeriesScore != prevScore || rows[i].TotalXCount != prevXCount)
                        {
                            rank = i + 1;
                        }
                        rows[i].Rank = rank;
                        prevScore = rows[i].TotalSeriesScore;
                        prevXCount = rows[i].TotalXCount;
                    }

                    return new SeriesClassStandings
                    {
                        ClassName = kvp.Key,
                        Rows = rows
                    };
                })
                .ToList();
        }

        private static Dictionary<string, int> GetClassOrder()
        {
            return new Dictionary<string, int>
            {
                { "C1", 1 }, { "C1 Dam", 2 }, { "C1 Jun", 3 },
                { "C2", 4 }, { "C2 Dam", 5 }, { "C2 Jun", 6 },
                { "C3", 7 }, { "C3 Dam", 8 }, { "C3 Jun", 9 },
                { "C Vet Y", 10 }, { "C Vet Y Dam", 11 }, { "C Vet Y Jun", 12 },
                { "C Vet Ä", 13 }, { "C Vet Ä Dam", 14 }, { "C Vet Ä Jun", 15 },
                { "B1", 16 }, { "B1 Dam", 17 }, { "B1 Jun", 18 },
                { "B2", 19 }, { "B2 Dam", 20 }, { "B2 Jun", 21 },
                { "B3", 22 }, { "B3 Dam", 23 }, { "B3 Jun", 24 },
                { "B Vet Y", 25 }, { "B Vet Y Dam", 26 }, { "B Vet Y Jun", 27 },
                { "B Vet Ä", 28 }, { "B Vet Ä Dam", 29 }, { "B Vet Ä Jun", 30 },
                { "A1", 31 }, { "A1 Dam", 32 }, { "A1 Jun", 33 },
                { "A2", 34 }, { "A2 Dam", 35 }, { "A2 Jun", 36 },
                { "A3", 37 }, { "A3 Dam", 38 }, { "A3 Jun", 39 },
                { "R1", 40 }, { "R2", 41 }, { "R3", 42 },
                { "M1", 50 }, { "M2", 51 }, { "M3", 52 },
                { "M4", 53 }, { "M5", 54 }, { "M6", 55 },
                { "M7", 56 }, { "M8", 57 }, { "M9", 58 },
                { "L1", 60 }, { "L2", 61 }, { "L3", 62 }
            };
        }
    }
}
