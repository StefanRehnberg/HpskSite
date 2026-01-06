using HpskSite.CompetitionTypes.Common.Interfaces;
using HpskSite.CompetitionTypes.Precision.ViewModels;
using Newtonsoft.Json;

namespace HpskSite.CompetitionTypes.Precision.Services
{
    /// <summary>
    /// Start list service for Precision competition type.
    /// Implements IStartListService to handle Precision-specific start list generation.
    /// </summary>
    public class PrecisionStartListService : IStartListService
    {
        private readonly PrecisionScoringService _scoringService;

        public PrecisionStartListService(PrecisionScoringService? scoringService = null)
        {
            _scoringService = scoringService ?? new PrecisionScoringService();
        }

        /// <summary>
        /// Generate start list for a competition.
        /// Uses default mixed team strategy.
        /// </summary>
        public async Task<dynamic> GenerateStartList(int competitionId)
        {
            // Default strategy for Precision is mixed teams
            return await GenerateStartListWithStrategy(competitionId, "mixed");
        }

        /// <summary>
        /// Generate start list with specific grouping algorithm.
        /// Supported strategies: "mixed", "byClass", "random", "byClub"
        /// </summary>
        public async Task<dynamic> GenerateStartListWithStrategy(int competitionId, string groupingStrategy)
        {
            // TODO: Implement strategy-based start list generation
            // For now, return placeholder structure
            return new
            {
                competitionId = competitionId,
                strategy = groupingStrategy,
                generatedAt = DateTime.UtcNow,
                teams = new List<dynamic>(),
                totalShooters = 0,
                status = "generated"
            };
        }

        /// <summary>
        /// Validate start list before publishing.
        /// </summary>
        public async Task<dynamic> ValidateStartList(int startListId)
        {
            var validationErrors = new List<string>();
            var validationWarnings = new List<string>();

            // TODO: Implement validation rules
            // Check: all teams have at least 1 shooter
            // Check: no duplicate shooters in same team
            // Check: all shooters have valid class
            // Check: timing is reasonable

            return new
            {
                isValid = validationErrors.Count == 0,
                errors = validationErrors,
                warnings = validationWarnings
            };
        }

        /// <summary>
        /// Publish start list (make it official).
        /// </summary>
        public async Task<bool> PublishStartList(int startListId)
        {
            // TODO: Implement publication logic
            // Mark start list as official
            // Notify participants
            return false;
        }

        /// <summary>
        /// Get current start list for a competition.
        /// </summary>
        public async Task<dynamic> GetCurrentStartList(int competitionId)
        {
            // TODO: Implement retrieval logic
            // Find most recent published start list for competition
            return null;
        }

        /// <summary>
        /// Update start list (e.g., after registration changes).
        /// </summary>
        public async Task<dynamic> UpdateStartList(int competitionId)
        {
            // TODO: Implement update logic
            // Regenerate start list with current registrations
            return await GenerateStartList(competitionId);
        }

        /// <summary>
        /// Generate mixed teams start list.
        /// Shuffles shooters into balanced teams with mixed weapon classes.
        /// </summary>
        public dynamic GenerateMixedTeams(List<dynamic> registrations, int maxPerTeam, 
            string firstStartTime, int intervalMinutes)
        {
            // TODO: Extract and refactor from controller
            return new { teams = new List<dynamic>() };
        }

        /// <summary>
        /// Generate class-separated start list.
        /// Groups teams by shooting class.
        /// </summary>
        public dynamic GenerateClassSeparatedTeams(List<dynamic> registrations, int maxPerTeam,
            string firstStartTime, int intervalMinutes, string classOrder = "")
        {
            // TODO: Extract and refactor from controller
            return new { teams = new List<dynamic>() };
        }

        /// <summary>
        /// Generate club-based start list.
        /// Groups teams by member club.
        /// </summary>
        public dynamic GenerateClubBasedTeams(List<dynamic> registrations, int maxPerTeam,
            string firstStartTime, int intervalMinutes)
        {
            // TODO: Extract and refactor from controller
            return new { teams = new List<dynamic>() };
        }

        /// <summary>
        /// Calculate team timings based on start time and interval.
        /// </summary>
        public List<(int teamNumber, string startTime, string endTime)> CalculateTeamTimings(
            int teamCount, string firstStartTime, int intervalMinutes)
        {
            var timings = new List<(int, string, string)>();

            if (!TimeSpan.TryParse(firstStartTime, out var currentTime))
                return timings;

            for (int i = 1; i <= teamCount; i++)
            {
                var endTime = currentTime.Add(TimeSpan.FromMinutes(intervalMinutes));
                timings.Add((i, FormatTime(currentTime), FormatTime(endTime)));
                currentTime = endTime;
            }

            return timings;
        }

        /// <summary>
        /// Validate team composition.
        /// Ensures no duplicate shooters and proper class balance.
        /// </summary>
        public dynamic ValidateTeamComposition(List<dynamic> team)
        {
            var issues = new List<string>();

            // Check for duplicate shooters
            var shooterIds = new HashSet<int>();
            foreach (var shooter in team)
            {
                if (shooterIds.Contains((int)shooter.memberId))
                {
                    issues.Add($"Duplicate shooter: {shooter.name}");
                }
                shooterIds.Add((int)shooter.memberId);
            }

            return new
            {
                isValid = issues.Count == 0,
                issues = issues
            };
        }

        /// <summary>
        /// Format TimeSpan to HH:MM string.
        /// </summary>
        private string FormatTime(TimeSpan time)
        {
            return $"{time.Hours:D2}:{time.Minutes:D2}";
        }

        /// <summary>
        /// Generate HTML representation of start list.
        /// </summary>
        public string GenerateStartListHtml(dynamic startListData, string competitionName)
        {
            var html = new System.Text.StringBuilder();
            
            html.AppendLine("<div class='start-list-container'>");
            html.AppendLine($"<h2>{competitionName}</h2>");
            html.AppendLine($"<p>Generated: {DateTime.Now:yyyy-MM-dd HH:mm}</p>");
            
            // TODO: Build HTML from startListData structure
            
            html.AppendLine("</div>");
            
            return html.ToString();
        }

        /// <summary>
        /// Export start list to JSON format.
        /// </summary>
        public string ExportStartListJson(dynamic startListData)
        {
            return JsonConvert.SerializeObject(startListData, Formatting.Indented);
        }

        /// <summary>
        /// Sort shooters within a team.
        /// </summary>
        public List<dynamic> SortShootersInTeam(List<dynamic> shooters, string sortBy = "name")
        {
            return sortBy switch
            {
                "class" => shooters.OrderBy(s => (string)s.shootingClass).ToList(),
                "club" => shooters.OrderBy(s => (string)s.club).ToList(),
                "name" or _ => shooters.OrderBy(s => (string)s.name).ToList()
            };
        }
    }
}
