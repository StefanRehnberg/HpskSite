using Umbraco.Cms.Core.Models.PublishedContent;
using Umbraco.Cms.Core.Models;
using Umbraco.Extensions;
using HpskSite.CompetitionTypes.Precision.ViewModels;

namespace HpskSite.CompetitionTypes.Precision.Models
{
    /// <summary>
    /// Precision Finals Start List document type for championship competitions.
    /// Contains the qualified finalists organized by championship class with proper start order.
    /// </summary>
    public class PrecisionFinalsStartList : HpskSite.Models.BasePage
    {
        public PrecisionFinalsStartList(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
            : base(content, publishedValueFallback)
        {
        }

        // Basic properties
        public int CompetitionId => this.Value<int>("competitionId", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        public int QualificationStartListId => this.Value<int>("qualificationStartListId", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        public DateTime GeneratedDate => this.Value<DateTime>("generatedDate", fallback: Fallback.ToDefaultValue, defaultValue: DateTime.Now);
        public string GeneratedBy => this.Value<string>("generatedBy") ?? "";
        public bool IsOfficialFinalsStartList => this.Value<bool>("isOfficialFinalsStartList", fallback: Fallback.ToDefaultValue, defaultValue: false);
        
        // Configuration data stored as JSON
        public string ConfigurationData => this.Value<string>("configurationData") ?? "";
        
        // Team format (e.g., "Championship Finals")
        public string TeamFormat => this.Value<string>("teamFormat") ?? "Championship Finals";
        
        // Number of qualified finalists
        public int TotalFinalists => this.Value<int>("totalFinalists", fallback: Fallback.ToDefaultValue, defaultValue: 0);
        
        // Max shooters per team
        public int MaxShootersPerTeam => this.Value<int>("maxShootersPerTeam", fallback: Fallback.ToDefaultValue, defaultValue: 20);

        // Get parent competition
        public IPublishedContent? Competition
        {
            get
            {
                var parent = this.Parent();
                // Finals start list should be under a "Start Lists Hub" which is under Competition
                if (parent?.ContentType.Alias == "competitionStartListsHub")
                {
                    return parent.Parent();
                }
                // Or could be directly under competition
                if (parent?.ContentType.Alias == "competition")
                {
                    return parent;
                }
                return null;
            }
        }

        // Display helpers
        public string GetStatusDisplay()
        {
            return IsOfficialFinalsStartList ? "Officiell Finalstartlista" : "Preliminär Finalstartlista";
        }

        public string GetStatusBadgeClass()
        {
            return IsOfficialFinalsStartList ? "badge bg-success" : "badge bg-warning text-dark";
        }
    }

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
    }
}
