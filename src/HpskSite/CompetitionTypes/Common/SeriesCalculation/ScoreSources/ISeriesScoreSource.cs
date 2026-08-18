using HpskSite.CompetitionTypes.Common.SeriesCalculation.Models;

namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.ScoreSources
{
    /// <summary>
    /// Turns a set of competitions into the per-shooter totals the series strategies work on.
    ///
    /// The strategies themselves (sum-all, best-of, placement points, club team) are
    /// discipline-agnostic: they only ever read <see cref="ShooterCompetitionScore.TotalScore"/>
    /// and <see cref="ShooterCompetitionScore.XCount"/> (the secondary/tie-break number).
    /// What differs per discipline is where the rows live and how a competition total is
    /// built out of them — that is what a score source encapsulates.
    /// </summary>
    public interface ISeriesScoreSource
    {
        /// <summary>The competition types this source can read. Empty/unknown is handled by the precision source.</summary>
        bool Supports(string competitionType);

        Task<SeriesScoreSet> FetchAsync(IReadOnlyList<int> competitionIds, string competitionType);
    }
}
