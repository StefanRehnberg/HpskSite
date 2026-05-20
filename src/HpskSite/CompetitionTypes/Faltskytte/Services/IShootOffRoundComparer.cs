using HpskSite.CompetitionTypes.Faltskytte.Models;

namespace HpskSite.CompetitionTypes.Faltskytte.Services
{
    /// <summary>
    /// Strategy for comparing two shoot-off round entries within a single round.
    /// One implementation per Fältskytte variation. Used by
    /// <see cref="FaltskytteShootOffService"/> to drive both progressive resolution
    /// and public "Sär" column formatting.
    /// </summary>
    public interface IShootOffRoundComparer
    {
        /// <summary>
        /// Compare two shooters' single-round entries. Positive = <paramref name="a"/> beats
        /// <paramref name="b"/>, negative = <paramref name="b"/> wins, zero = still tied for this round.
        /// </summary>
        int Compare(FaltskytteShootOffEntry a, FaltskytteShootOffEntry b);

        /// <summary>
        /// Short display string for one round used in the public "Sär" column and
        /// the admin status card (e.g. "5/4", "10p", "23p").
        /// </summary>
        string FormatRound(FaltskytteShootOffEntry e);
    }
}
