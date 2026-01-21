using HpskSite.Shared.Models;
using HpskSite.CompetitionTypes.Precision.Models;
using NPoco;
using System.Text.Json;
using Umbraco.Cms.Core.Services;
using Umbraco.Cms.Infrastructure.Scoping;
using Umbraco.Extensions;

namespace HpskSite.Services
{
    /// <summary>
    /// Service for retrieving and aggregating results from multiple sources:
    /// - TrainingScores table (training and competition entries)
    /// - Competition Result document type (official competition results)
    /// </summary>
    public class UnifiedResultsService
    {
        private readonly IScopeProvider _scopeProvider;
        private readonly IContentService _contentService;

        public UnifiedResultsService(
            IScopeProvider scopeProvider,
            IContentService contentService)
        {
            _scopeProvider = scopeProvider;
            _contentService = contentService;
        }

        /// <summary>
        /// Get all results for a member from all data sources
        /// </summary>
        public List<UnifiedResultEntry> GetMemberResults(int memberId, int? limit = null, int? skip = null)
        {
            var results = new List<UnifiedResultEntry>();

            // 1. Get results from TrainingScores table
            results.AddRange(GetTrainingScoresResults(memberId));

            // 2. Get results from official competition results documents
            results.AddRange(GetOfficialCompetitionResults(memberId));

            // Sort by date descending
            results = results.OrderByDescending(r => r.Date).ToList();

            // Apply pagination if specified
            if (skip.HasValue)
            {
                results = results.Skip(skip.Value).ToList();
            }

            if (limit.HasValue)
            {
                results = results.Take(limit.Value).ToList();
            }

            return results;
        }

        /// <summary>
        /// Get results from TrainingScores table (training entries only)
        /// </summary>
        private List<UnifiedResultEntry> GetTrainingScoresResults(int memberId)
        {
            var results = new List<UnifiedResultEntry>();

            using (var scope = _scopeProvider.CreateScope(autoComplete: true))
            {
                var db = scope.Database;

                // Query from TrainingScores table - includes both training and competition entries
                // TrainingMatchId determines if entry is from a training match
                // IsCompetition column determines whether entry is shown as training or competition
                var query = @"
                    SELECT
                        Id,
                        MemberId,
                        TrainingDate,
                        WeaponClass,
                        SeriesScores,
                        TotalScore,
                        XCount,
                        Notes,
                        IsCompetition,
                        TrainingMatchId
                    FROM TrainingScores
                    WHERE MemberId = @0
                    ORDER BY TrainingDate DESC";

                var scores = db.Fetch<dynamic>(query, memberId);

                foreach (var score in scores)
                {
                    // Parse series data
                    var series = DeserializeTrainingSeries(score.SeriesScores?.ToString());

                    // Calculate series count - always parse from original JSON to ensure accuracy
                    int seriesCount = 0;

                    // Always parse the original JSON to get accurate seriesCount for all entry methods
                    try
                    {
                        var trainingSeries = System.Text.Json.JsonSerializer.Deserialize<List<TrainingSeries>>(score.SeriesScores?.ToString() ?? "");
                        if (trainingSeries != null && trainingSeries.Count > 0)
                        {
                            // Check for TotalOnly entry method (must have seriesCount property)
                            if (trainingSeries.Count == 1 && trainingSeries[0].EntryMethod == "TotalOnly")
                            {
                                // Get seriesCount value - JSON deserializes as int, not int?, so just cast directly
                                var seriesCountValue = trainingSeries[0].SeriesCount;

                                // Simple check: if not null and greater than 0, use it
                                if (seriesCountValue != null && (int)seriesCountValue > 0)
                                {
                                    seriesCount = (int)seriesCountValue;
                                }
                                else
                                {
                                    // Fallback to array length
                                    seriesCount = trainingSeries.Count;
                                }
                            }
                            else
                            {
                                // For all other entries (shot-by-shot, series-total, or old entries without entryMethod)
                                // Count the actual number of series objects in the array
                                seriesCount = trainingSeries.Count;
                            }
                        }
                        else if (series != null && series.Count > 0)
                        {
                            // Fallback if deserialization returns empty
                            seriesCount = series.Count;
                        }
                    }
                    catch
                    {
                        // Fallback to parsed SeriesDetail count if JSON parsing fails
                        if (series != null && series.Count > 0)
                        {
                            seriesCount = series.Count;
                        }
                    }

                    double averageScore = seriesCount > 0 ? (double)score.TotalScore / seriesCount : 0;

                    // Determine SourceType based on TrainingMatchId and IsCompetition flag
                    bool isCompetition = false;
                    int? trainingMatchId = null;
                    try
                    {
                        isCompetition = score.IsCompetition ?? false;
                        trainingMatchId = score.TrainingMatchId;
                    }
                    catch
                    {
                        // Columns might not exist in older databases, use defaults
                        isCompetition = false;
                        trainingMatchId = null;
                    }

                    // Determine source type:
                    // - IsCompetition = true → "Competition" (competition result, even if from training match)
                    // - TrainingMatchId != null → "TrainingMatch" (from mobile app training match)
                    // - Otherwise → "Training" (manually entered training result)
                    string sourceType;
                    if (isCompetition)
                        sourceType = "Competition";
                    else if (trainingMatchId != null)
                        sourceType = "TrainingMatch";
                    else
                        sourceType = "Training";

                    results.Add(new UnifiedResultEntry
                    {
                        Id = score.Id,
                        Date = score.TrainingDate,
                        SourceType = sourceType,
                        WeaponClass = score.WeaponClass?.ToString() ?? "",
                        TotalScore = score.TotalScore ?? 0,
                        XCount = score.XCount ?? 0,
                        SeriesCount = seriesCount,
                        AverageScore = Math.Round(averageScore, 1),
                        CompetitionName = null,
                        CompetitionId = null,
                        CanEdit = true,  // TrainingScores entries can be edited
                        CanDelete = true,
                        Notes = score.Notes?.ToString(),
                        Series = series ?? new List<SeriesDetail>()
                    });
                }

                // Now get competition results from PrecisionResultEntry table
                results.AddRange(GetPrecisionResultEntries(memberId, db));
            }

            return results;
        }

        /// <summary>
        /// Get competition results from PrecisionResultEntry table
        /// </summary>
        private List<UnifiedResultEntry> GetPrecisionResultEntries(int memberId, IDatabase db)
        {
            var results = new List<UnifiedResultEntry>();

            try
            {
                // Query PrecisionResultEntry table and group by competition
                var query = @"
                    SELECT
                        p.CompetitionId,
                        p.MemberId,
                        p.ShootingClass,
                        MIN(p.EnteredAt) as EnteredAt,
                        COUNT(*) as SeriesCount,
                        STRING_AGG(p.Shots, '|') as AllShots
                    FROM PrecisionResultEntry p
                    WHERE p.MemberId = @0
                    GROUP BY p.CompetitionId, p.MemberId, p.ShootingClass
                    ORDER BY MIN(p.EnteredAt) DESC";

                var competitionRecords = db.Fetch<dynamic>(query, memberId);

                foreach (var compRecord in competitionRecords)
                {
                    int competitionId = compRecord.CompetitionId ?? 0;
                    string shootingClass = compRecord.ShootingClass ?? "";

                    // Extract weapon class from shooting class (e.g., "A3" -> "A")
                    string weaponClass = !string.IsNullOrEmpty(shootingClass) && shootingClass.Length > 0
                        ? shootingClass.Substring(0, 1).ToUpper()
                        : "A";

                    // Get competition name
                    string? competitionName = null;
                    var competitionNode = _contentService.GetById(competitionId);
                    if (competitionNode != null)
                    {
                        competitionName = competitionNode.Name;
                    }

                    // Parse all shots and calculate total score and X-count
                    string allShotsStr = compRecord.AllShots ?? "";
                    var allSeriesShots = allShotsStr.Split('|');
                    int totalScore = 0;
                    int xCount = 0;
                    var seriesList = new List<SeriesDetail>();
                    int seriesNumber = 1;

                    foreach (var seriesShots in allSeriesShots)
                    {
                        if (string.IsNullOrEmpty(seriesShots)) continue;

                        try
                        {
                            var shots = System.Text.Json.JsonSerializer.Deserialize<List<string>>(seriesShots);
                            if (shots != null)
                            {
                                int seriesTotal = 0;
                                int seriesXCount = 0;

                                foreach (var shot in shots)
                                {
                                    if (shot?.ToUpper() == "X")
                                    {
                                        seriesTotal += 10;
                                        seriesXCount++;
                                        xCount++;
                                    }
                                    else if (int.TryParse(shot, out int value))
                                    {
                                        seriesTotal += value;
                                    }
                                }

                                totalScore += seriesTotal;

                                seriesList.Add(new SeriesDetail
                                {
                                    SeriesNumber = seriesNumber++,
                                    Shots = shots,
                                    Total = seriesTotal,
                                    XCount = seriesXCount
                                });
                            }
                        }
                        catch
                        {
                            // Skip invalid JSON
                        }
                    }

                    int seriesCount = compRecord.SeriesCount ?? 1;
                    double averageScore = seriesCount > 0 ? (double)totalScore / seriesCount : 0;

                    results.Add(new UnifiedResultEntry
                    {
                        Id = competitionId,  // Use competition ID
                        Date = compRecord.EnteredAt,
                        SourceType = "Competition",
                        WeaponClass = weaponClass,
                        TotalScore = totalScore,
                        XCount = xCount,
                        SeriesCount = seriesCount,
                        AverageScore = Math.Round(averageScore, 1),
                        CompetitionName = competitionName,
                        CompetitionId = competitionId,
                        CanEdit = false,  // Competition results cannot be edited
                        CanDelete = false,
                        Series = seriesList
                    });
                }
            }
            catch (Exception ex)
            {
                // Log error but don't fail - just return what we have
                Console.WriteLine($"Error fetching PrecisionResultEntry data: {ex.Message}");
            }

            return results;
        }

        /// <summary>
        /// Get results from official competition result documents
        /// </summary>
        private List<UnifiedResultEntry> GetOfficialCompetitionResults(int memberId)
        {
            var results = new List<UnifiedResultEntry>();

            // Note: For now, we'll return empty list and rely on PrecisionResultEntry data from TrainingScores
            // TODO: Implement official competition results extraction from competitionResult document type
            // This requires querying all competition nodes, finding their competitionResult child nodes,
            // and parsing the resultData JSON for the specific member

            return results;
        }

        /// <summary>
        /// Extract a specific member's results from competition result JSON data
        /// </summary>
        private List<MemberCompetitionResult> ExtractMemberFromCompetitionResult(string resultDataJson, int memberId)
        {
            var memberResults = new List<MemberCompetitionResult>();

            try
            {
                var finalResults = JsonSerializer.Deserialize<PrecisionFinalResults>(resultDataJson);
                if (finalResults == null || finalResults.ClassGroups == null)
                {
                    return memberResults;
                }

                // Search through all class groups for this member
                foreach (var classGroup in finalResults.ClassGroups)
                {
                    if (classGroup.Shooters == null) continue;

                    var shooter = classGroup.Shooters.FirstOrDefault(s => s.MemberId == memberId);
                    if (shooter != null && shooter.Results != null)
                    {
                        // Found the member - extract their results
                        foreach (var resultEntry in shooter.Results)
                        {
                            var shots = ParseShotsFromJson(resultEntry.Shots);
                            int total = CalculateTotal(shots);
                            int xCount = CountX(shots);

                            memberResults.Add(new MemberCompetitionResult
                            {
                                Id = resultEntry.Id,
                                ShootingClass = resultEntry.ShootingClass,
                                TotalScore = total,
                                XCount = xCount,
                                SeriesCount = 1,  // Each PrecisionResultEntry is one series
                                AverageScore = total,  // For single series, average = total
                                Series = new List<SeriesDetail>
                                {
                                    new SeriesDetail
                                    {
                                        SeriesNumber = resultEntry.SeriesNumber,
                                        Shots = shots,
                                        Total = total,
                                        XCount = xCount
                                    }
                                }
                            });
                        }
                    }
                }
            }
            catch (JsonException ex)
            {
                // Log error but don't fail - just return empty list
                Console.WriteLine($"Error parsing competition result JSON: {ex.Message}");
            }

            return memberResults;
        }

        /// <summary>
        /// Deserialize TrainingScores series JSON format
        /// </summary>
        private List<SeriesDetail>? DeserializeTrainingSeries(string? seriesJson)
        {
            if (string.IsNullOrEmpty(seriesJson))
            {
                return null;
            }

            try
            {
                var trainingSeries = JsonSerializer.Deserialize<List<TrainingSeries>>(seriesJson);
                if (trainingSeries == null) return null;

                return trainingSeries.Select(s => new SeriesDetail
                {
                    SeriesNumber = s.SeriesNumber,
                    Shots = s.Shots,
                    Total = s.Total,
                    XCount = s.XCount
                }).ToList();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// Parse shots from JSON string format used in PrecisionResultEntry
        /// </summary>
        private List<string> ParseShotsFromJson(string shotsJson)
        {
            try
            {
                var shots = JsonSerializer.Deserialize<List<string>>(shotsJson);
                return shots ?? new List<string>();
            }
            catch (JsonException)
            {
                return new List<string>();
            }
        }

        /// <summary>
        /// Calculate total score from shots
        /// </summary>
        private int CalculateTotal(List<string> shots)
        {
            int total = 0;
            foreach (var shot in shots)
            {
                if (shot.ToUpper() == "X")
                {
                    total += 10;
                }
                else if (int.TryParse(shot, out int value))
                {
                    total += value;
                }
            }
            return total;
        }

        /// <summary>
        /// Count X's in shots
        /// </summary>
        private int CountX(List<string> shots)
        {
            return shots.Count(s => s.ToUpper() == "X");
        }

        /// <summary>
        /// Helper class for extracting member results from competition JSON
        /// </summary>
        private class MemberCompetitionResult
        {
            public int Id { get; set; }
            public string ShootingClass { get; set; } = string.Empty;
            public int TotalScore { get; set; }
            public int XCount { get; set; }
            public int SeriesCount { get; set; }
            public double AverageScore { get; set; }
            public List<SeriesDetail> Series { get; set; } = new List<SeriesDetail>();
        }
    }
}
