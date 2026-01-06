using HpskSite.CompetitionTypes.Common.Interfaces;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Results service for Precision competition type.
    /// Implements IResultsService to handle Precision-specific result generation and ranking.
    /// </summary>
    public class PrecisionResultsService : IResultsService
    {
        private readonly PrecisionScoringService _scoringService;

        public PrecisionResultsService(PrecisionScoringService? scoringService = null)
        {
            _scoringService = scoringService ?? new PrecisionScoringService();
        }

        /// <summary>
        /// Generate results for a completed competition.
        /// </summary>
        public async Task<List<dynamic>> GenerateCompetitionResults(int competitionId)
        {
            // TODO: Implement results generation
            // Retrieve all registrations for competition
            // Calculate scores for each participant
            // Rank participants
            return new List<dynamic>();
        }

        /// <summary>
        /// Generate live leaderboard for ongoing competition.
        /// Shows current standings with partial scores.
        /// </summary>
        public async Task<List<dynamic>> GetLiveLeaderboard(int competitionId)
        {
            // TODO: Implement live leaderboard logic
            // Get current results entered so far
            // Calculate running totals
            // Show incomplete series with partial scores
            return new List<dynamic>();
        }

        /// <summary>
        /// Get detailed results for a single participant.
        /// </summary>
        public async Task<dynamic> GetParticipantResults(int registrationId)
        {
            // TODO: Implement participant results retrieval
            // Get all series results for participant
            // Calculate totals and statistics
            return null;
        }

        /// <summary>
        /// Export results in specified format.
        /// Supports: CSV, Excel, PDF
        /// </summary>
        public async Task<byte[]> ExportResults(int competitionId, string format)
        {
            // TODO: Implement export logic
            var results = await GenerateCompetitionResults(competitionId);

            return format.ToLower() switch
            {
                "csv" => ExportToCsv(results),
                "excel" => ExportToExcel(results),
                "pdf" => ExportToPdf(results),
                _ => new byte[0]
            };
        }

        /// <summary>
        /// Calculate final ranking for competition.
        /// </summary>
        public async Task<List<dynamic>> CalculateFinalRanking(int competitionId)
        {
            var results = await GenerateCompetitionResults(competitionId);

            // Group by shooting class and rank within each class
            var rankings = results
                .GroupBy(r => (string)r.shootingClass)
                .Select(g => new
                {
                    shootingClass = g.Key,
                    rankings = RankParticipants(g.ToList())
                })
                .ToList();

            return rankings.Cast<dynamic>().ToList();
        }

        /// <summary>
        /// Calculate score for a single series.
        /// </summary>
        public dynamic CalculateSeriesScore(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<string[]>(shotsJson) ?? new string[0];
                if (shots.Length == 0)
                    return new { total = 0, innerTens = 0, tens = 0 };

                var shotList = shots.ToList();
                return new
                {
                    total = (int)_scoringService.CalculateSeriesTotal(shotList),
                    innerTens = _scoringService.CalculateInnerTens(shotList),
                    tens = _scoringService.CalculateTens(shotList)
                };
            }
            catch
            {
                return new { total = 0, innerTens = 0, tens = 0 };
            }
        }

        /// <summary>
        /// Calculate overall score for a participant across all series.
        /// </summary>
        public dynamic CalculateOverallScore(List<string> seriesScoresJson)
        {
            var totalPoints = 0;
            var totalInnerTens = 0;
            var totalTens = 0;
            var seriesCount = 0;

            foreach (var seriesJson in seriesScoresJson)
            {
                var score = CalculateSeriesScore(seriesJson);
                totalPoints += (int)score.total;
                totalInnerTens += (int)score.innerTens;
                totalTens += (int)score.tens;
                seriesCount++;
            }

            var maxPossible = _scoringService.GetMaxCompetitionScore(seriesCount);
            var percentage = maxPossible > 0 ? (totalPoints / (decimal)maxPossible) * 100 : 0;

            return new
            {
                totalPoints = totalPoints,
                innerTens = totalInnerTens,
                tens = totalTens,
                seriesCount = seriesCount,
                maxPossible = maxPossible,
                percentage = Math.Round(percentage, 1)
            };
        }

        /// <summary>
        /// Rank participants based on overall score.
        /// Tie-breaking: first by inner tens (X-shots), then by tens.
        /// </summary>
        private List<dynamic> RankParticipants(List<dynamic> participants)
        {
            var ranked = participants
                .OrderByDescending(p => (int)p.totalPoints)
                .ThenByDescending(p => (int)p.innerTens)
                .ThenByDescending(p => (int)p.tens)
                .Select((p, index) => new
                {
                    rank = index + 1,
                    participantName = p.participantName,
                    totalPoints = p.totalPoints,
                    innerTens = p.innerTens,
                    tens = p.tens,
                    percentage = p.percentage
                })
                .ToList();

            return ranked.Cast<dynamic>().ToList();
        }

        /// <summary>
        /// Group results by shooting class.
        /// </summary>
        public dynamic GroupResultsByClass(List<dynamic> results)
        {
            var grouped = results
                .GroupBy(r => (string)r.shootingClass)
                .Select(g => new
                {
                    shootingClass = g.Key,
                    participantCount = g.Count(),
                    results = g.ToList()
                })
                .ToList();

            return grouped;
        }

        /// <summary>
        /// Export results to CSV format.
        /// </summary>
        private byte[] ExportToCsv(List<dynamic> results)
        {
            // TODO: Implement CSV export
            var csv = "Rank,Name,Class,Total,X,10,Percentage\n";

            // Build CSV rows...
            
            return System.Text.Encoding.UTF8.GetBytes(csv);
        }

        /// <summary>
        /// Export results to Excel format.
        /// </summary>
        private byte[] ExportToExcel(List<dynamic> results)
        {
            // TODO: Implement Excel export using a library like EPPlus
            return new byte[0];
        }

        /// <summary>
        /// Export results to PDF format.
        /// </summary>
        private byte[] ExportToPdf(List<dynamic> results)
        {
            // TODO: Implement PDF export using a library like iTextSharp
            return new byte[0];
        }

        /// <summary>
        /// Generate medal winners (gold, silver, bronze) for each class.
        /// </summary>
        public dynamic GetMedalWinners(int competitionId, int numberOfWinners = 3)
        {
            // TODO: Implement medal winner logic
            return new
            {
                competitionId = competitionId,
                medals = new List<dynamic>()
            };
        }

        /// <summary>
        /// Generate statistical summary for competition.
        /// </summary>
        public dynamic GetCompetitionStatistics(int competitionId)
        {
            // TODO: Implement statistics calculation
            return new
            {
                totalParticipants = 0,
                averageScore = 0,
                highestScore = 0,
                lowestScore = 0,
                classCounts = new Dictionary<string, int>()
            };
        }

        /// <summary>
        /// Validate result entry before saving.
        /// </summary>
        public dynamic ValidateResultEntry(string shotsJson)
        {
            try
            {
                var shots = JsonConvert.DeserializeObject<string[]>(shotsJson);
                if (shots == null || shots.Length != 5)
                {
                    return new { isValid = false, error = "Series must contain exactly 5 shots" };
                }

                // Validate each shot
                foreach (var shot in shots)
                {
                    if (!_scoringService.IsValidShotValue(shot))
                    {
                        return new { isValid = false, error = $"Invalid shot value: {shot}" };
                    }
                }

                return new { isValid = true };
            }
            catch (Exception ex)
            {
                return new { isValid = false, error = ex.Message };
            }
        }

        /// <summary>
        /// Generate participant summary with all series results.
        /// </summary>
        public dynamic GetParticipantSummary(int participantId, int competitionId)
        {
            // TODO: Implement participant summary
            return new
            {
                participantId = participantId,
                competitionId = competitionId,
                series = new List<dynamic>(),
                totals = new { }
            };
        }
    }
}
