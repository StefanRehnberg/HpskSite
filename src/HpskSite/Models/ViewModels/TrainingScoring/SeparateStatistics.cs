namespace HpskSite.Models.ViewModels.TrainingScoring
{
    /// <summary>
    /// Training-specific statistics (from TrainingScores where IsCompetition=false)
    /// </summary>
    public class TrainingStatistics
    {
        /// <summary>
        /// Total number of training sessions
        /// </summary>
        public int TotalSessions { get; set; }

        /// <summary>
        /// Overall average score across all training
        /// </summary>
        public double OverallAverage { get; set; }

        /// <summary>
        /// Recent average (last 30 days)
        /// </summary>
        public double RecentAverage { get; set; }

        /// <summary>
        /// Recent average breakdown by weapon class
        /// </summary>
        public List<ClassAverage> RecentAverageByClass { get; set; } = new List<ClassAverage>();

        /// <summary>
        /// Personal bests by weapon class (highest average per series count)
        /// </summary>
        public List<PersonalBest> PersonalBests { get; set; } = new List<PersonalBest>();
    }

    /// <summary>
    /// Competition-specific statistics (from TrainingScores where IsCompetition=true + Official Competition Results)
    /// </summary>
    public class CompetitionStatistics
    {
        /// <summary>
        /// Total number of competitions participated in
        /// </summary>
        public int TotalCompetitions { get; set; }

        /// <summary>
        /// Overall average score across all competitions
        /// </summary>
        public double OverallAverage { get; set; }

        /// <summary>
        /// Recent average (last 30 days)
        /// </summary>
        public double RecentAverage { get; set; }

        /// <summary>
        /// Recent average breakdown by weapon class
        /// </summary>
        public List<ClassAverage> RecentAverageByClass { get; set; } = new List<ClassAverage>();

        /// <summary>
        /// Personal bests by weapon class (highest score from competitions)
        /// </summary>
        public List<PersonalBest> PersonalBests { get; set; } = new List<PersonalBest>();
    }

    /// <summary>
    /// Average score for a specific weapon class
    /// </summary>
    public class ClassAverage
    {
        /// <summary>
        /// Weapon class (A, B, C, R, P)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// Average score for this class
        /// </summary>
        public double Average { get; set; }
    }
}
