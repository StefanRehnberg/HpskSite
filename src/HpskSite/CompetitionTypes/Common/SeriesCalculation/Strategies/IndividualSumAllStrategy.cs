using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;
using HpskSite.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    public class IndividualSumAllStrategy : ISeriesCalculationStrategy
    {
        public string Id => "IndividualSumAll";
        public string Name => "Individuellt totalsumma";
        public string Description => "Summerar alla tävlingsresultat per skytt. Alla deltävlingar räknas.";

        public List<StrategyParameter> GetParameters() => new()
        {
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
            var placementMode = PlacementPointsCalculator.ParseMode(
                PlacementPointsCalculator.ParseString(context.Parameters, "placementPoints", "off"));
            var pointsTable = PlacementPointsCalculator.ResolvePointsTable(
                context.Parameters, "pointsTable", "customPointsTable");

            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // Pre-calculate placement points per competition+class if enabled
            // Key: (CompId, ShootingClass, MemberId) -> placement points
            var placementPoints = new Dictionary<(int CompId, string ShootingClass, int MemberId), int>();

            if (placementMode != PlacementPointsCalculator.Mode.Off)
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

            bool usePlacement = placementMode != PlacementPointsCalculator.Mode.Off;

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
                        var cellPoints = usePlacement
                            ? placementPoints.GetValueOrDefault((comp.CompetitionId, key.ShootingClass, key.MemberId), 0)
                            : score.TotalScore;

                        cells.Add(new SeriesCompetitionCell
                        {
                            CompetitionId = comp.CompetitionId,
                            Score = score.TotalScore,
                            XCount = score.XCount,
                            Points = cellPoints,
                            Counting = true
                        });
                        totalScore += cellPoints;
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
                    Club = HpskSite.Helpers.ClubNameHelper.Shorten(shooterInfo.Club),
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
                .Select(kvp =>
                {
                    // Convert the raw shooting-class ID (e.g. "C_Vet_Y", "A_opt_2") to its display
                    // name ("C Vet Y", "A Opt 2") so the UI shows user-friendly labels and the
                    // class-order lookup keys match.
                    var sc = ShootingClasses.GetById(kvp.Key);
                    var displayName = sc?.Name ?? kvp.Key;
                    return (DisplayName: displayName, Rows: kvp.Value);
                })
                .OrderBy(item => classOrder.GetValueOrDefault(item.DisplayName, 999))
                .ThenBy(item => item.DisplayName, StringComparer.Ordinal)
                .Select(item =>
                {
                    var rows = item.Rows
                        .OrderByDescending(r => r.TotalSeriesScore)
                        .ThenByDescending(r => r.TotalXCount)
                        .ThenByDescending(r => r.BestIndividualScore ?? 0)
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
                        ClassName = item.DisplayName,
                        Rows = rows
                    };
                })
                .ToList();
        }

        private static Dictionary<string, int> GetClassOrder()
        {
            // Weapon groups ordered A → A Opt → B → C → R → M → L.
            // Within each weapon group, highest skill level first (3 → 2 → 1).
            return new Dictionary<string, int>
            {
                // A group
                { "A3", 1 }, { "A3 Dam", 2 }, { "A3 Jun", 3 },
                { "A2", 4 }, { "A2 Dam", 5 }, { "A2 Jun", 6 },
                { "A1", 7 }, { "A1 Dam", 8 }, { "A1 Jun", 9 },

                // A Opt group (own weapon class)
                { "A Opt 3", 10 },
                { "A Opt 2", 11 },
                { "A Opt 1", 12 },

                // B group
                { "B3", 20 }, { "B3 Dam", 21 }, { "B3 Jun", 22 },
                { "B2", 23 }, { "B2 Dam", 24 }, { "B2 Jun", 25 },
                { "B1", 26 }, { "B1 Dam", 27 }, { "B1 Jun", 28 },
                { "B Vet Y", 29 }, { "B Vet Y Dam", 30 }, { "B Vet Y Jun", 31 },
                { "B Vet Ä", 32 }, { "B Vet Ä Dam", 33 }, { "B Vet Ä Jun", 34 },

                // C group
                { "C3", 40 }, { "C3 Dam", 41 }, { "C3 Jun", 42 },
                { "C2", 43 }, { "C2 Dam", 44 }, { "C2 Jun", 45 },
                { "C1", 46 }, { "C1 Dam", 47 }, { "C1 Jun", 48 },
                { "C Vet Y", 49 }, { "C Vet Y Dam", 50 }, { "C Vet Y Jun", 51 },
                { "C Vet Ä", 52 }, { "C Vet Ä Dam", 53 }, { "C Vet Ä Jun", 54 },
                { "C Jun", 55 }, { "C Dam", 56 },

                // R group
                { "R3", 60 }, { "R2", 61 }, { "R1", 62 },

                // M group
                { "M1", 70 }, { "M2", 71 }, { "M3", 72 },
                { "M4", 73 }, { "M5", 74 }, { "M6", 75 },
                { "M7", 76 }, { "M8", 77 }, { "M9", 78 },

                // L group
                { "L3", 80 }, { "L3 Dam", 81 },
                { "L2", 82 }, { "L2 Dam", 83 },
                { "L1", 84 }, { "L1 Dam", 85 },
                { "L Vet Y", 86 }, { "L Vet Ä", 87 }, { "L Jun", 88 }
            };
        }
    }
}
