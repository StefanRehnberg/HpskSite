namespace HpskSite.Services.StartListCoverage
{
    /// <summary>
    /// One registered start that has nowhere to start from. "Start" means (member, shooting class):
    /// a shooter entered in both C1 and A1 needs a slot in each, and missing one of them is exactly
    /// the state this exists to surface.
    /// </summary>
    public sealed class UnplacedStart
    {
        public int MemberId { get; init; }
        public string Name { get; init; } = "";
        public string Club { get; init; } = "";
        public string ShootingClass { get; init; } = "";
    }

    /// <summary>Per weapon group, so the organiser can see WHICH list was never generated.</summary>
    public sealed class CoverageGroup
    {
        public string WeaponClass { get; init; } = "";
        public int Total { get; init; }
        public int Placed { get; init; }
        public List<UnplacedStart> Missing { get; init; } = new();
    }

    public sealed class StartListCoverageResult
    {
        /// <summary>False when the discipline has no coverage reader — never rendered as "allt är placerat".</summary>
        public bool Supported { get; init; } = true;

        /// <summary>What the unit of placement is called here ("skjutlag" / "patrull"), for the copy.</summary>
        public string UnitLabel { get; init; } = "startlista";

        public int Total { get; init; }
        public int Placed { get; init; }
        public int Missing => Math.Max(0, Total - Placed);

        public List<CoverageGroup> ByWeapon { get; init; } = new();

        /// <summary>
        /// The MIRROR fault: rows on the start list that match no registration — a shooter placed
        /// under a class they are not entered in, usually because the registration's class was
        /// changed after the list was generated (see the class-change orphaning issue).
        ///
        /// Reporting only the unplaced half is what let these hide: the organiser sees "Andy saknar
        /// starttid i C3", goes to the list, finds Andy on it (as A1), and concludes the warning is
        /// wrong. Both halves have to be on screen for either to make sense.
        /// </summary>
        public List<UnplacedStart> OnListWithoutRegistration { get; init; } = new();

        /// <summary>
        /// No start list at all is a DIFFERENT state from "some starts are unplaced" — before the
        /// first generation everyone is unplaced, which is normal and must not read as an error.
        /// </summary>
        public bool HasAnyStartList { get; init; }
    }
}
