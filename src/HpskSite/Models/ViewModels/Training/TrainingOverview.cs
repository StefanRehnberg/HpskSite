namespace HpskSite.Models.ViewModels.Training
{
    /// <summary>
    /// Overview data for the training system leaderboard and statistics
    /// </summary>
    public class TrainingOverview
    {
        public List<MemberProgress> MemberProgress { get; set; } = new List<MemberProgress>();
        public List<TrainingLevel> AllLevels { get; set; } = new List<TrainingLevel>();
        public MemberProgress? CurrentMemberProgress { get; set; }
        public TrainingStatistics Statistics { get; set; } = new TrainingStatistics();

        /// <summary>
        /// Get members grouped by current level
        /// </summary>
        public Dictionary<int, List<MemberProgress>> GetMembersByLevel()
        {
            return MemberProgress
                .Where(p => p.IsActive)
                .GroupBy(p => p.CurrentLevel)
                .ToDictionary(g => g.Key, g => g.ToList());
        }

        /// <summary>
        /// Get top performers (highest level + step combination)
        /// </summary>
        public List<MemberProgress> GetTopPerformers(int count = 10)
        {
            return MemberProgress
                .Where(p => p.IsActive)
                .OrderByDescending(p => p.CurrentLevel)
                .ThenByDescending(p => p.CurrentStep)
                .ThenByDescending(p => p.LastActivityDate)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Get recently active members
        /// </summary>
        public List<MemberProgress> GetRecentlyActive(int days = 30, int count = 10)
        {
            var cutoffDate = DateTime.Now.AddDays(-days);
            return MemberProgress
                .Where(p => p.IsActive && p.LastActivityDate >= cutoffDate)
                .OrderByDescending(p => p.LastActivityDate)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Get members who have completed specific levels
        /// </summary>
        public List<MemberProgress> GetLevelCompletions(int levelId)
        {
            return MemberProgress
                .Where(p => p.IsActive && p.CurrentLevel > levelId)
                .OrderBy(p => p.CompletedSteps.Where(c => c.LevelId == levelId).Max(c => c.CompletedDate))
                .ToList();
        }
    }

    /// <summary>
    /// Training system statistics
    /// </summary>
    public class TrainingStatistics
    {
        public int TotalActiveMembers { get; set; }
        public int TotalStepsCompleted { get; set; }
        public Dictionary<int, int> MembersPerLevel { get; set; } = new Dictionary<int, int>();
        public DateTime? MostRecentActivity { get; set; }
        public List<LevelStatistics> LevelStats { get; set; } = new List<LevelStatistics>();

        public static TrainingStatistics Calculate(List<MemberProgress> memberProgress)
        {
            var stats = new TrainingStatistics();
            var activeMembers = memberProgress.Where(p => p.IsActive).ToList();

            stats.TotalActiveMembers = activeMembers.Count;
            stats.TotalStepsCompleted = activeMembers.Sum(m => m.CompletedSteps.Count);
            stats.MostRecentActivity = activeMembers.Max(m => m.LastActivityDate);

            // Calculate members per level
            stats.MembersPerLevel = activeMembers
                .GroupBy(m => m.CurrentLevel)
                .ToDictionary(g => g.Key, g => g.Count());

            // Calculate level statistics
            var allLevels = TrainingDefinitions.GetAllLevels();
            foreach (var level in allLevels)
            {
                var levelStat = new LevelStatistics
                {
                    LevelId = level.LevelId,
                    LevelName = level.Name,
                    TotalSteps = level.Steps.Count,
                    MembersAtLevel = stats.MembersPerLevel.GetValueOrDefault(level.LevelId, 0),
                    MembersCompleted = activeMembers.Count(m => m.CurrentLevel > level.LevelId),
                    StepsCompleted = activeMembers.Sum(m => m.CompletedSteps.Count(c => c.LevelId == level.LevelId))
                };

                levelStat.CompletionRate = levelStat.TotalSteps > 0 ?
                    (double)levelStat.StepsCompleted / (levelStat.TotalSteps * stats.TotalActiveMembers) * 100 : 0;

                stats.LevelStats.Add(levelStat);
            }

            return stats;
        }
    }

    /// <summary>
    /// Statistics for a specific training level
    /// </summary>
    public class LevelStatistics
    {
        public int LevelId { get; set; }
        public string LevelName { get; set; } = string.Empty;
        public int TotalSteps { get; set; }
        public int MembersAtLevel { get; set; }
        public int MembersCompleted { get; set; }
        public int StepsCompleted { get; set; }
        public double CompletionRate { get; set; }
    }
}