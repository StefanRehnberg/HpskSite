using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.ScoreSources
{
    /// <summary>
    /// Per-competition shooter totals plus the column headings that fit the discipline.
    /// Precision counts "Totalt / X"; normalfält counts "Träff / Fig."; poängfält "Poäng / Poängmål".
    /// </summary>
    public class SeriesScoreSet
    {
        public Dictionary<int, List<ShooterCompetitionScore>> ByCompetition { get; set; } = new();

        /// <summary>Heading for the total column (and the per-round cells).</summary>
        public string ScoreLabel { get; set; } = "Totalt";

        /// <summary>Heading for the secondary/tie-break column.</summary>
        public string SecondaryLabel { get; set; } = "X";
    }
}
