using Umbraco.Cms.Core.Models;

namespace HpskSite.Services.StartListCoverage
{
    /// <summary>
    /// "Is every registered start actually placed somewhere?" — answered per discipline, because
    /// where a start time LIVES differs: the precision family keeps skjutlag in a
    /// `precisionStartList` node's configurationData, Fältskytte keeps patrols in SQL.
    ///
    /// Same seam as ISeriesScoreSource: a new discipline is one class plus one line in
    /// AdminServicesComposer, not a branch in a controller.
    /// </summary>
    public interface IStartListCoverageSource
    {
        bool Supports(string? competitionType);

        Task<StartListCoverageResult> BuildAsync(IContent competition);
    }

    /// <summary>
    /// Shared key rules. Both implementations must use these — a coverage report that keys
    /// placements one way and registrations another reports everyone as unplaced.
    /// </summary>
    public static class CoverageKeys
    {
        /// <summary>
        /// ⚠️ The class Id/Name trap. Registrations and most writers store the class ID ("C1",
        /// "A_opt_1"); ChangeShooterClass writes the display NAME ("C 1", "A Opt 1"). Comparing
        /// the two literally matches nothing for every class where they differ, and the whole
        /// list then reads as unplaced. Strip whitespace and case before comparing.
        /// </summary>
        public static string Canonical(string? shootingClass) =>
            new string((shootingClass ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

        public static string For(int memberId, string? shootingClass) =>
            $"{memberId}|{Canonical(shootingClass)}";

        /// <summary>
        /// Weapon group for grouping only — the first letter is enough for every class id in use
        /// (C1, A_opt_2, R3, M2). Grouping is a display concern; placement is keyed on the class.
        /// </summary>
        public static string WeaponGroupOf(string? shootingClass)
        {
            var c = Canonical(shootingClass);
            return c.Length == 0 ? "?" : c.Substring(0, 1);
        }
    }
}
