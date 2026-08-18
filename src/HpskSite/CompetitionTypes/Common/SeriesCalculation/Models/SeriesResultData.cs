namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Models
{
    public class SeriesResultData
    {
        public string StrategyId { get; set; } = "";
        public string StrategyName { get; set; } = "";
        public DateTime CalculatedAt { get; set; }
        public List<SeriesCompetitionInfo> Competitions { get; set; } = new();
        public List<SeriesResultSection> Sections { get; set; } = new();

        /// <summary>Heading for the total column — "Totalt" (precision), "Träff" or "Poäng" (fält).</summary>
        public string ScoreLabel { get; set; } = "Totalt";

        /// <summary>Heading for the secondary/tie-break column — "X", "Fig." or "Poängmål".</summary>
        public string SecondaryLabel { get; set; } = "X";

        /// <summary>
        /// Set when the series' discipline has no score source, so the page can say so instead of
        /// silently rendering nothing. Null on a normal calculation.
        /// </summary>
        public string? UnsupportedMessage { get; set; }
    }

    public class SeriesResultSection
    {
        public string SectionType { get; set; } = "Individual"; // "Individual" or "Club"
        public string Title { get; set; } = "";
        public List<SeriesClassStandings> ClassStandings { get; set; } = new();
    }

    public class SeriesClassStandings
    {
        public string ClassName { get; set; } = "";
        public List<SeriesStandingRow> Rows { get; set; } = new();
    }

    public class SeriesStandingRow
    {
        public int Rank { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public int EntityId { get; set; }
        public int TotalSeriesScore { get; set; }
        public int TotalXCount { get; set; }
        public List<SeriesCompetitionCell> CompetitionScores { get; set; } = new();

        /// <summary>
        /// For club rows: the highest individual shooter score across all competitions.
        /// Used for tie-breaking when two clubs have the same total series score.
        /// Null for individual rows.
        /// </summary>
        public int? BestIndividualScore { get; set; }
    }

    public class SeriesCompetitionCell
    {
        public int CompetitionId { get; set; }
        public int? Score { get; set; }
        public int? XCount { get; set; }
        public int? Points { get; set; }
        public bool Counting { get; set; } = true;
    }
}
