using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using HpskSite.CompetitionTypes.Precision.ViewModels;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    /// <summary>
    /// Frozen snapshot of qualifying results — per championship class. Admin freezes
    /// each class individually as that class's qualifying round completes. Stored as a
    /// dictionary keyed by championship class name on the qualifying precisionStartList
    /// node so finals generation can read whichever classes are ready.
    /// </summary>
    public class QualifyingResultsSnapshot
    {
        public int CompetitionId { get; set; }

        // Per-championship-class frozen rankings. Only classes the admin has explicitly
        // frozen appear here. Unfrozen classes are silently skipped during finals
        // generation.
        public Dictionary<string, ClassResultsSnapshot> ClassSnapshots { get; set; } = new();
    }

    /// <summary>
    /// One frozen class within a QualifyingResultsSnapshot. Carries the ranked shooter
    /// list (all participants, no cut applied — generator applies cut per class config)
    /// plus enough metadata to detect staleness if results edits land after freeze.
    /// </summary>
    public class ClassResultsSnapshot
    {
        public string ChampionshipClass { get; set; } = "";
        public DateTime FrozenAt { get; set; }
        public string FrozenBy { get; set; } = "";

        // SHA over (MemberId, SeriesNumber, Shots) for shooters in this championship
        // class only, at freeze time. Re-compute + compare detects post-freeze edits.
        public string ChecksumAtFreeze { get; set; } = "";

        // All participants in this championship class, ranked by score (then X-count).
        public List<QualifiedShooter> QualifiedShooters { get; set; } = new();
    }

    /// <summary>
    /// Per-championship-class config controlling whether a class participates in finals,
    /// which skjutlag it sits in (multiple classes can share a skjutlag — they stack as
    /// contiguous position blocks ordered by OrderInSkjutlag), and optional cut overrides.
    /// </summary>
    public class FinalsClassConfig
    {
        // true → skip this class entirely (no final).
        public bool Skip { get; set; }

        // Which skjutlag this class participates in (1, 2, 3, ...). Defaults to one
        // skjutlag per class. Multiple classes with the same SkjutlagNumber share a
        // skjutlag — they appear as contiguous position blocks ordered by OrderInSkjutlag.
        public int SkjutlagNumber { get; set; } = 1;

        // Position-block order within a shared skjutlag. Lower = earlier positions.
        // E.g., C (order 0) at positions 1-10, C Vet Y (order 1) at positions 11-17.
        public int OrderInSkjutlag { get; set; }

        // null = use default 1/6+min10 cutoff.
        public int? FinalistCountOverride { get; set; }

        // Skip the cut entirely — useful for small comps where everyone advances.
        public bool IncludeAllShooters { get; set; }
    }

    /// <summary>
    /// Settings used by the generator when building the finals StartListConfiguration —
    /// mirrors the regular StartListSettings shape, kept separate so we can evolve
    /// finals-specific knobs (e.g. per-bucket start times) without touching the
    /// qualifying generator.
    /// </summary>
    public class FinalsStartListSettings
    {
        public string FirstStartTime { get; set; } = "10:00";
        public string StartInterval { get; set; } = "1:45";
        public int MaxShootersPerTeam { get; set; } = 20;

        /// <summary>
        /// Vilken vapengrupp den här finalstartlistan gäller ("C", "A", "B" …).
        /// En lista per vapengrupp, eftersom en vapengrupps final är EN skjutsession med
        /// eget datum, egen starttid och egen publicering — se
        /// <see cref="Common.ChampionshipCategory.WeaponGroupFor"/>.
        /// Tom sträng = en äldre lista som täcker hela tävlingen.
        /// </summary>
        public string WeaponGroup { get; set; } = "";

        /// <summary>
        /// Vilken dag finalen skjuts, "yyyy-MM-dd". Tom = tävlingens eget datum.
        /// Stämplas på varje genererat skjutlag (<see cref="StartListTeam.Date"/>), som är
        /// den plats /mitt-schema och kalenderexporten redan läser — utan datum kan
        /// söndagens finalskjutlag inte ordnas efter lördagens.
        /// </summary>
        public string Date { get; set; } = "";
    }
}
