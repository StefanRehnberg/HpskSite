namespace HpskSite.Shared.Models
{
    /// <summary>
    /// Represents a member's personal best score for a specific series count and shooting class
    /// </summary>
    public class PersonalBest
    {
        /// <summary>
        /// Member ID
        /// </summary>
        public int MemberId { get; set; }

        /// <summary>
        /// Weapon class (A, B, C, R, P)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// Indicates if this is a competition result or training
        /// </summary>
        public bool IsCompetition { get; set; }

        /// <summary>
        /// Number of series (1, 2, 3, etc.)
        /// </summary>
        public int SeriesCount { get; set; }

        /// <summary>
        /// Best total score achieved
        /// </summary>
        public int BestScore { get; set; }

        /// <summary>
        /// X-count for the best score
        /// </summary>
        public int XCount { get; set; }

        /// <summary>
        /// Date when this personal best was achieved
        /// </summary>
        public DateTime AchievedDate { get; set; }

        /// <summary>
        /// Training score entry ID that holds this record
        /// </summary>
        public int TrainingScoreId { get; set; }

        /// <summary>
        /// Previous best score (for showing improvement)
        /// </summary>
        public int? PreviousBest { get; set; }

        /// <summary>
        /// Points improved from previous best
        /// </summary>
        public int Improvement => PreviousBest.HasValue ? BestScore - PreviousBest.Value : 0;

        /// <summary>
        /// Is this a new personal best?
        /// </summary>
        public bool IsNewRecord => PreviousBest.HasValue && Improvement > 0;

        /// <summary>
        /// Get display text for this personal best
        /// </summary>
        public string GetDisplayText()
        {
            var text = $"{BestScore}p";

            if (XCount > 0)
            {
                text += $" ({XCount} X)";
            }

            if (IsNewRecord)
            {
                text += $" +{Improvement}";
            }

            return text;
        }

        /// <summary>
        /// Get description including series count and class
        /// </summary>
        public string GetFullDescription()
        {
            var type = IsCompetition ? "Tavling" : "Traning";
            return $"{WeaponClass}-Vapen ({type}) - {SeriesCount} serier - {GetDisplayText()}";
        }
    }

    /// <summary>
    /// Personal bests grouped by weapon class
    /// </summary>
    public class PersonalBestsByClass
    {
        /// <summary>
        /// Weapon class (A, B, C, R, P)
        /// </summary>
        public string WeaponClass { get; set; } = string.Empty;

        /// <summary>
        /// All personal bests for this class (by series count)
        /// </summary>
        public List<PersonalBest> Bests { get; set; } = new List<PersonalBest>();

        /// <summary>
        /// Get best score for a specific series count
        /// </summary>
        public PersonalBest? GetBestForSeriesCount(int seriesCount)
        {
            return Bests.FirstOrDefault(b => b.SeriesCount == seriesCount);
        }

        /// <summary>
        /// Get overall best score (highest total regardless of series count)
        /// </summary>
        public PersonalBest? GetOverallBest()
        {
            return Bests.OrderByDescending(b => b.BestScore).FirstOrDefault();
        }
    }
}
