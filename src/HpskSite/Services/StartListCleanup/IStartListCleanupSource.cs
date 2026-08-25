using Umbraco.Cms.Core.Models;

namespace HpskSite.Services.StartListCleanup
{
    /// <summary>Where a shooter currently stands, for the confirm dialog.</summary>
    public sealed class StartListPlacement
    {
        public string ListName { get; init; } = "";
        public string Where { get; init; } = "";        // "Skjutlag 3, plats 7" / "Patrull 4"
        public string StartTime { get; init; } = "";
        public string ShootingClass { get; init; } = "";
        public bool IsPublished { get; init; }
    }

    /// <summary>What the cleanup actually did, so the operator can be told rather than guess.</summary>
    public sealed class CleanupOutcome
    {
        public bool Supported { get; init; } = true;
        public int SlotsFreed { get; init; }
        public int ResultRowsDeleted { get; init; }
        public int PublishedListsUpdated { get; init; }
        public bool Regenerated { get; init; }         // direktplacering: rebuilt rather than patched
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Deleting a registration used to leave the shooter ON the generated start list with orphaned
    /// result rows behind them — a bare confirm dialog and nothing else. Springskytte got the fix
    /// 2026-08-05; every other discipline kept the silent mess.
    ///
    /// Per discipline because the start unit differs (skjutlag in a content node vs patrol in SQL),
    /// and so does the result table. Same seam as IStartListCoverageSource — coverage MAKES the mess
    /// visible, this CLEANS it.
    /// </summary>
    public interface IStartListCleanupSource
    {
        bool Supports(string? competitionType);

        /// <summary>Read-only: where does this member currently stand? For the warning before deleting.</summary>
        Task<List<StartListPlacement>> DescribePlacementsAsync(IContent competition, int memberId);

        /// <summary>
        /// Remove the member from every start unit and drop their result rows.
        /// Called AFTER the registration is gone, so it must be safe with no registration present.
        ///
        /// <paramref name="onlyShootingClass"/> narrows it to ONE class, which is what the orphan-row
        /// action needs: a shooter can legitimately hold a place in C1 while their A1 row is the
        /// orphan, and clearing both would delete a start the shooter is entitled to. Null = all.
        /// </summary>
        Task<CleanupOutcome> CleanupAsync(IContent competition, int memberId, string? onlyShootingClass = null);
    }
}
