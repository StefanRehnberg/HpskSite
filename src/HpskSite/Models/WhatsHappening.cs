namespace HpskSite.Models
{
    /// <summary>
    /// The three content sources aggregated into the "Det här händer" home/region feed.
    /// </summary>
    public enum FeedSource
    {
        Competition,
        ClubEvent,
        TrainingMatch
    }

    /// <summary>
    /// Rolling time buckets the feed groups items into (Now = ongoing träningsmatcher only).
    /// </summary>
    public enum FeedWindow
    {
        Now,
        ThisWeek,
        NextWeek,
        ThisMonth
    }

    /// <summary>
    /// A single read-only feed row. Carries BOTH the full identity (title/club/venue/url) and the
    /// masked metadata (source/type + region). The view decides which to render based on auth state:
    /// anonymous visitors see masked items as "{type} i {region}" only — full identity is never
    /// emitted into the anonymous DOM. See <see cref="Masked"/>.
    /// </summary>
    public class FeedItem
    {
        public FeedSource Source { get; set; }
        public FeedWindow Window { get; set; }
        public DateTime Start { get; set; }
        public bool ShowTime { get; set; }
        public bool IsOngoing { get; set; }

        /// <summary>Region (krets) code = club.regionalFederation / regionalPage.regionCode. "" when unresolved.</summary>
        public string RegionCode { get; set; } = "";
        public string RegionName { get; set; } = "";

        /// <summary>True = identity must be hidden from anonymous visitors (clubOnly comps + all club events).</summary>
        public bool Masked { get; set; }

        public string SourceLabel { get; set; } = "";       // "Tävling" / "Klubbhändelse" / "Träningsmatch"
        public string TypeLabel { get; set; } = "";         // discipline or eventType (full)
        public string MaskedTypeLabel { get; set; } = "";   // neutralised label used in the masked public title

        public string Title { get; set; } = "";
        public string ClubName { get; set; } = "";
        public string Venue { get; set; } = "";
        public string Url { get; set; } = "";
        public int ParticipantCount { get; set; }

        /// <summary>Window key used by the client JS ("now"/"week"/"next"/"month").</summary>
        public string WindowKey => Window switch
        {
            FeedWindow.Now => "now",
            FeedWindow.ThisWeek => "week",
            FeedWindow.NextWeek => "next",
            _ => "month"
        };
    }

    /// <summary>A krets for the dropdown filter + the slimmed region-nav strip below the feed.</summary>
    public class WhatsHappeningRegion
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int ClubCount { get; set; }
        public string Url { get; set; } = "";
    }

    /// <summary>Cached, login-independent aggregate. Masking + region filtering happen at render time.</summary>
    public class WhatsHappeningData
    {
        public List<FeedItem> Items { get; set; } = new();
        public List<WhatsHappeningRegion> Regions { get; set; } = new();
        public Dictionary<string, int> OngoingMatchCountByRegion { get; set; } = new();
        public int OngoingMatchTotal { get; set; }
        public DateTime GeneratedAt { get; set; }
    }

    /// <summary>Model passed to the partial. LockedRegionCode null = national (home page).</summary>
    public class WhatsHappeningScope
    {
        public string? LockedRegionCode { get; set; }
        public string? LockedRegionName { get; set; }

        /// <summary>National only: preselect this krets in the dropdown without locking (member's own krets on the hub).</summary>
        public string? DefaultRegionCode { get; set; }

        /// <summary>Whether to render the slimmed "Kretsar" nav strip in the footer (off on the landing/hub where other UI handles it).</summary>
        public bool ShowRegionNav { get; set; } = true;
    }
}
