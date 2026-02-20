using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Strategies
{
    /// <summary>
    /// Club team strategy: picks up to N shooters per club per competition, sums their scores.
    /// Supports groupByClass (rank clubs per class or combined), bestOf (best X competitions),
    /// and clubSeriesScoring ("sum" = sum of competition scores, "placement" = placement points per comp).
    /// Produces both an Individual section (all shooters) and a Club section (club standings).
    /// </summary>
    public class ClubTeamBestOfStrategy : ISeriesCalculationStrategy
    {
        public string Id => "ClubTeamBestOf";
        public string Name => "Klubblag bästa X";
        public string Description => "Klubblagstävling. Väljer de N bästa skyttarna per klubb och tävling. Kan räkna bästa X deltävlingar.";

        public List<StrategyParameter> GetParameters() => new()
        {
            new StrategyParameter
            {
                Key = "bestOf",
                Label = "Antal bästa deltävlingar (0 = alla)",
                Type = "int",
                DefaultValue = 0
            },
            new StrategyParameter
            {
                Key = "maxShootersPerClub",
                Label = "Max antal skyttar per klubb och tävling",
                Type = "int",
                DefaultValue = 3
            },
            new StrategyParameter
            {
                Key = "groupByClass",
                Label = "Gruppera per klass (annars kombinerat)",
                Type = "bool",
                DefaultValue = false
            },
            new StrategyParameter
            {
                Key = "clubSeriesScoring",
                Label = "Klubbseriepoäng",
                Type = "select",
                DefaultValue = "sum",
                Options = new List<SelectOption>
                {
                    new() { Value = "sum", Label = "Summa (råpoäng)" },
                    new() { Value = "placement", Label = "Dynamisk placering" },
                    new() { Value = "fixed", Label = "Fast poängtabell" }
                }
            },
            new StrategyParameter
            {
                Key = "clubPointsTable",
                Label = "Klubb-poängtabell",
                Type = "select",
                DefaultValue = "10, 8, 6, 5, 4, 3, 2, 1",
                Options = PointsTablePresets.Club,
                DependsOn = "clubSeriesScoring",
                DependsOnValue = "fixed"
            },
            new StrategyParameter
            {
                Key = "customClubPointsTable",
                Label = "Egen klubb-poängtabell (1:a, 2:a, 3:a... kommaseparerat)",
                Type = "string",
                DefaultValue = "5, 3, 1",
                Placeholder = "t.ex. 5, 3, 1",
                DependsOn = "clubPointsTable",
                DependsOnValue = "custom"
            }
        };

        public SeriesResultData Calculate(SeriesCalculationContext context)
        {
            int bestOf = ParseInt(context.Parameters, "bestOf", 0);
            int maxShootersPerClub = ParseInt(context.Parameters, "maxShootersPerClub", 3);
            bool groupByClass = ParseBool(context.Parameters, "groupByClass", false);
            string clubSeriesScoring = ParseString(context.Parameters, "clubSeriesScoring", "sum");
            var clubPlacementMode = PlacementPointsCalculator.ParseMode(clubSeriesScoring);
            var clubPointsTable = PlacementPointsCalculator.ResolvePointsTable(
                context.Parameters, "clubPointsTable", "customClubPointsTable");

            // Section 1: Individual standings (all shooters, sum all)
            var individualSection = BuildIndividualSection(context);

            // Section 2: Club standings
            var clubSection = BuildClubSection(context, bestOf, maxShootersPerClub, groupByClass, clubPlacementMode, clubPointsTable);

            return new SeriesResultData
            {
                StrategyId = Id,
                StrategyName = bestOf > 0 ? $"Klubblag bästa {bestOf}" : "Klubblag alla",
                CalculatedAt = DateTime.UtcNow,
                Competitions = context.Competitions,
                Sections = new List<SeriesResultSection> { individualSection, clubSection }
            };
        }

        private static SeriesResultSection BuildIndividualSection(SeriesCalculationContext context)
        {
            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

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
                    Club = HpskSite.Helpers.ClubNameHelper.Shorten(shooterInfo.Club),
                    EntityId = key.MemberId,
                    TotalSeriesScore = totalScore,
                    TotalXCount = totalXCount,
                    CompetitionScores = cells
                });
            }

            var classStandings = IndividualSumAllStrategy.RankAndOrderByClass(standingsByClass, context.Competitions);

            return new SeriesResultSection
            {
                SectionType = "Individual",
                Title = "Individuellt",
                ClassStandings = classStandings
            };
        }

        private static SeriesResultSection BuildClubSection(
            SeriesCalculationContext context,
            int bestOf,
            int maxShootersPerClub,
            bool groupByClass,
            PlacementPointsCalculator.Mode placementMode,
            int[] pointsTable)
        {
            if (groupByClass)
                return BuildClubSectionByClass(context, bestOf, maxShootersPerClub, placementMode, pointsTable);
            else
                return BuildClubSectionCombined(context, bestOf, maxShootersPerClub, placementMode, pointsTable);
        }

        /// <summary>
        /// groupByClass = true: For each class, rank clubs separately.
        /// Per competition+class, pick top N shooters from each club, sum their scores = club comp score.
        /// </summary>
        private static SeriesResultSection BuildClubSectionByClass(
            SeriesCalculationContext context,
            int bestOf,
            int maxShootersPerClub,
            PlacementPointsCalculator.Mode placementMode,
            int[] pointsTable)
        {
            var standingsByClass = new Dictionary<string, List<SeriesStandingRow>>();

            // Get all classes
            var allClasses = new HashSet<string>();
            foreach (var (_, scores) in context.CompetitionResults)
                foreach (var s in scores)
                    allClasses.Add(s.ShootingClass);

            foreach (var shootingClass in allClasses)
            {
                // Get all clubs that have shooters in this class
                var clubsInClass = new HashSet<int>();
                var clubNameLookup = new Dictionary<int, string>();

                foreach (var (_, scores) in context.CompetitionResults)
                {
                    foreach (var s in scores.Where(s => s.ShootingClass == shootingClass))
                    {
                        clubsInClass.Add(s.ClubId);
                        clubNameLookup.TryAdd(s.ClubId, s.Club);
                    }
                }

                if (!standingsByClass.ContainsKey(shootingClass))
                    standingsByClass[shootingClass] = new List<SeriesStandingRow>();

                foreach (var clubId in clubsInClass)
                {
                    var compScoresForClub = new Dictionary<int, int>();
                    int bestIndividual = 0;

                    foreach (var comp in context.Competitions)
                    {
                        var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                        if (compScores == null) continue;

                        var clubShooters = compScores
                            .Where(s => s.ClubId == clubId && s.ShootingClass == shootingClass)
                            .OrderByDescending(s => s.TotalScore)
                            .ThenByDescending(s => s.XCount)
                            .Take(maxShootersPerClub)
                            .ToList();

                        if (clubShooters.Count > 0)
                        {
                            compScoresForClub[comp.CompetitionId] = clubShooters.Sum(s => s.TotalScore);
                            var topScore = clubShooters[0].TotalScore;
                            if (topScore > bestIndividual) bestIndividual = topScore;
                        }
                    }

                    var row = BuildClubRow(clubId, clubNameLookup.GetValueOrDefault(clubId, "?"),
                        compScoresForClub, context);
                    row.BestIndividualScore = bestIndividual;
                    standingsByClass[shootingClass].Add(row);
                }

                // Apply scoring mode to all rows in this class
                ApplyClubScoring(standingsByClass[shootingClass], context, bestOf, placementMode, pointsTable);
            }

            var classStandings = IndividualSumAllStrategy.RankAndOrderByClass(standingsByClass, context.Competitions);

            return new SeriesResultSection
            {
                SectionType = "Club",
                Title = "Klubbtävling",
                ClassStandings = classStandings
            };
        }

        /// <summary>
        /// groupByClass = false: All classes combined. A shooter only counts once even if they shot
        /// multiple classes (uses their best score across classes).
        /// All clubs ranked in a single "Kombinerat" group.
        /// </summary>
        private static SeriesResultSection BuildClubSectionCombined(
            SeriesCalculationContext context,
            int bestOf,
            int maxShootersPerClub,
            PlacementPointsCalculator.Mode placementMode,
            int[] pointsTable)
        {
            var clubNameLookup = new Dictionary<int, string>();
            foreach (var (_, scores) in context.CompetitionResults)
                foreach (var s in scores)
                    clubNameLookup.TryAdd(s.ClubId, s.Club);

            var rows = new List<SeriesStandingRow>();

            foreach (var (clubId, clubName) in clubNameLookup)
            {
                var compScoresForClub = new Dictionary<int, int>();
                int bestIndividual = 0;

                foreach (var comp in context.Competitions)
                {
                    var compScores = context.CompetitionResults.GetValueOrDefault(comp.CompetitionId);
                    if (compScores == null) continue;

                    // Group by member, take best score per member (shooter counts once)
                    var bestScorePerMember = compScores
                        .Where(s => s.ClubId == clubId)
                        .GroupBy(s => s.MemberId)
                        .Select(g => g.OrderByDescending(s => s.TotalScore).ThenByDescending(s => s.XCount).First())
                        .OrderByDescending(s => s.TotalScore)
                        .ThenByDescending(s => s.XCount)
                        .Take(maxShootersPerClub)
                        .ToList();

                    if (bestScorePerMember.Count > 0)
                    {
                        compScoresForClub[comp.CompetitionId] = bestScorePerMember.Sum(s => s.TotalScore);
                        var topScore = bestScorePerMember[0].TotalScore;
                        if (topScore > bestIndividual) bestIndividual = topScore;
                    }
                }

                var row = BuildClubRow(clubId, clubName, compScoresForClub, context);
                row.BestIndividualScore = bestIndividual;
                rows.Add(row);
            }

            // Apply scoring mode to all rows
            ApplyClubScoring(rows, context, bestOf, placementMode, pointsTable);

            var classStandings = new Dictionary<string, List<SeriesStandingRow>>
            {
                { "Kombinerat", rows }
            };

            return new SeriesResultSection
            {
                SectionType = "Club",
                Title = "Klubbtävling",
                ClassStandings = IndividualSumAllStrategy.RankAndOrderByClass(classStandings, context.Competitions)
            };
        }

        /// <summary>
        /// Build a club standing row from per-competition scores.
        /// Points and bestOf are applied later via ApplyClubScoring.
        /// </summary>
        private static SeriesStandingRow BuildClubRow(
            int clubId,
            string clubName,
            Dictionary<int, int> compScoresForClub,
            SeriesCalculationContext context)
        {
            var cells = new List<SeriesCompetitionCell>();

            foreach (var comp in context.Competitions)
            {
                if (compScoresForClub.TryGetValue(comp.CompetitionId, out var clubCompScore))
                {
                    cells.Add(new SeriesCompetitionCell
                    {
                        CompetitionId = comp.CompetitionId,
                        Score = clubCompScore,
                        XCount = 0,
                        Points = clubCompScore,
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

            return new SeriesStandingRow
            {
                Name = clubName,
                Club = "",
                EntityId = clubId,
                TotalSeriesScore = 0, // Calculated in ApplyClubScoring
                TotalXCount = 0,
                CompetitionScores = cells
            };
        }

        /// <summary>
        /// Apply scoring mode (sum, dynamic placement, or fixed placement) and bestOf to a group of club rows.
        /// Must be called after all rows are built so placement scoring can rank across clubs.
        /// </summary>
        private static void ApplyClubScoring(
            List<SeriesStandingRow> rows,
            SeriesCalculationContext context,
            int bestOf,
            PlacementPointsCalculator.Mode placementMode,
            int[] pointsTable)
        {
            if (placementMode != PlacementPointsCalculator.Mode.Off)
            {
                // Placement scoring: rank clubs per competition, assign placement points
                foreach (var comp in context.Competitions)
                {
                    var compId = comp.CompetitionId;

                    // Build entries for the calculator: EntityId = row index, Score = club comp score
                    var entries = new List<(int EntityId, int Score, int XCount)>();
                    var cellLookup = new Dictionary<int, SeriesCompetitionCell>();

                    for (int idx = 0; idx < rows.Count; idx++)
                    {
                        var cell = rows[idx].CompetitionScores.FirstOrDefault(c => c.CompetitionId == compId);
                        if (cell?.Score != null)
                        {
                            entries.Add((idx, cell.Score.Value, 0));
                            cellLookup[idx] = cell;
                        }
                    }

                    var points = PlacementPointsCalculator.Calculate(entries, placementMode, pointsTable);

                    foreach (var (idx, pts) in points)
                    {
                        cellLookup[idx].Points = pts;
                    }
                }

                // Apply bestOf on placement points
                if (bestOf > 0)
                {
                    foreach (var row in rows)
                    {
                        var scoredCells = row.CompetitionScores
                            .Where(c => c.Points.HasValue)
                            .OrderByDescending(c => c.Points)
                            .ToList();

                        var countingCompIds = new HashSet<int>(scoredCells.Take(bestOf).Select(c => c.CompetitionId));

                        foreach (var cell in row.CompetitionScores)
                        {
                            if (cell.Points.HasValue && !countingCompIds.Contains(cell.CompetitionId))
                            {
                                cell.Counting = false;
                                cell.Points = null;
                            }
                        }
                    }
                }
            }
            else
            {
                // Sum scoring: Points = Score, apply bestOf on raw scores
                if (bestOf > 0)
                {
                    foreach (var row in rows)
                    {
                        var scoredCells = row.CompetitionScores
                            .Where(c => c.Score.HasValue)
                            .OrderByDescending(c => c.Score)
                            .ToList();

                        var countingCompIds = new HashSet<int>(scoredCells.Take(bestOf).Select(c => c.CompetitionId));

                        foreach (var cell in row.CompetitionScores)
                        {
                            if (cell.Score.HasValue && !countingCompIds.Contains(cell.CompetitionId))
                            {
                                cell.Counting = false;
                                cell.Points = null;
                            }
                        }
                    }
                }
            }

            // Calculate totals
            foreach (var row in rows)
            {
                row.TotalSeriesScore = row.CompetitionScores
                    .Where(c => c.Counting && c.Points.HasValue)
                    .Sum(c => c.Points!.Value);
            }
        }

        #region Parameter parsing helpers

        private static int ParseInt(Dictionary<string, object> parameters, string key, int defaultValue)
        {
            if (!parameters.TryGetValue(key, out var obj)) return defaultValue;
            if (obj is int intVal) return intVal;
            if (obj is long longVal) return (int)longVal;
            if (obj is string strVal && int.TryParse(strVal, out var parsed)) return parsed;
            if (obj is System.Text.Json.JsonElement jsonEl && jsonEl.TryGetInt32(out var jsonInt)) return jsonInt;
            return defaultValue;
        }

        private static bool ParseBool(Dictionary<string, object> parameters, string key, bool defaultValue)
        {
            if (!parameters.TryGetValue(key, out var obj)) return defaultValue;
            if (obj is bool boolVal) return boolVal;
            if (obj is string strVal) return strVal.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (obj is System.Text.Json.JsonElement jsonEl && jsonEl.ValueKind == System.Text.Json.JsonValueKind.True) return true;
            if (obj is System.Text.Json.JsonElement jsonEl2 && jsonEl2.ValueKind == System.Text.Json.JsonValueKind.False) return false;
            return defaultValue;
        }

        private static string ParseString(Dictionary<string, object> parameters, string key, string defaultValue)
        {
            if (!parameters.TryGetValue(key, out var obj)) return defaultValue;
            if (obj is string strVal) return strVal;
            if (obj is System.Text.Json.JsonElement jsonEl && jsonEl.ValueKind == System.Text.Json.JsonValueKind.String)
                return jsonEl.GetString() ?? defaultValue;
            return defaultValue;
        }

        #endregion
    }
}
