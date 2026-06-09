using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// String constants for the Shooting Range Database (Skjutbanedatabas).
    /// See Documentation/SHOOTING_RANGE_DATABASE.md. Stored as strings (project convention —
    /// mirrors Marken/Standardmedalj enums-as-constants) so the DB stays readable.
    /// </summary>
    public static class RangeConstants
    {
        // ShootingRange.Status
        public const string StatusActive = "Active";
        public const string StatusInactive = "Inactive";
        public const string StatusDecommissioned = "Decommissioned";
        public const string StatusUnclaimedSeed = "UnclaimedSeed";

        // ShootingRange.Source
        public const string SourceOsm = "Osm";
        public const string SourceManual = "Manual";
        public const string SourceMunicipal = "Municipal";
        public const string SourceClaimed = "Claimed";

        // ShootingRange.LocationSensitivity
        public const string SensMembers = "Members";       // visible to any logged-in member
        public const string SensRestricted = "Restricted"; // coords hidden (military/discreet)

        // ShootingRange.HuvudmanType
        public const string HuvudmanClub = "ClubOnPlatform";
        public const string HuvudmanExternal = "ExternalParty";
        public const string HuvudmanMunicipality = "Municipality";
        public const string HuvudmanPrivate = "Private";
        public const string HuvudmanFederation = "Federation";

        // ClubRangeLink.RelationType
        public const string RelationOwner = "Owner";
        public const string RelationPrimaryUser = "PrimaryUser";
        public const string RelationUser = "User";
        public const string RelationTenant = "Tenant";

        // RangePermit.PermitType
        public const string PermitPolice = "PoliceTillstand";
        public const string PermitEnvC = "EnvAnmalanC";
        public const string PermitEnvB = "EnvTillstandB";
        public const string PermitOther = "Other";

        // RangePermit.Status
        public const string PermitStatusActive = "Active";
        public const string PermitStatusExpired = "Expired";
        public const string PermitStatusPendingRenewal = "PendingRenewal";

        // RangeDocument.DocType
        public const string DocPoliceTillstand = "PoliceTillstand";
        public const string DocBesiktningsprotokoll = "Besiktningsprotokoll";
        public const string DocSkjutbaneinstruktion = "Skjutbaneinstruktion";
        public const string DocEnvDecision = "EnvDecision";
        public const string DocBullerutredning = "Bullerutredning";
        public const string DocMarkundersokning = "Markundersokning"; // bly/lead
        public const string DocSkotselplan = "Skotselplan";
        public const string DocInsurance = "Insurance";
        public const string DocOther = "Other";

        /// <summary>Days within which a permit/document expiry counts as "snart" (renewal reminder).</summary>
        public const int RenewalWarningDays = 90;

        // RangeActivitySession.ShotCountSource
        public const string ShotSourceCompetition = "Competition";
        public const string ShotSourceTraining = "TrainingLog";
        public const string ShotSourceQr = "QrSelfReported";
        public const string ShotSourceManual = "ManualBulk";
        public const string ShotSourceEstimated = "Estimated";
    }

    /// <summary>
    /// A shooting range facility (Skjutbana) — the permit-bearing, inspected unit for BOTH the Police
    /// (one tillstånd + besiktning per facility) and the miljöförvaltning (one anmälan per verksamhet).
    /// Permits and the activity ledger attach to THIS entity in later phases; individual banor/vallar
    /// live on <see cref="RangeSection"/>.
    /// </summary>
    [TableName("ShootingRange")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ShootingRange
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";

        public double? Latitude { get; set; }
        public double? Longitude { get; set; }

        public string? Address { get; set; }
        public string? Postcode { get; set; }
        public string? City { get; set; }
        /// <summary>Kommun — key for the municipal/FOIA data track and filtering.</summary>
        public string? Municipality { get; set; }
        public string? County { get; set; }

        /// <summary>'Members' (default) | 'Restricted' (hide coords for military/discreet ranges).</summary>
        public string LocationSensitivity { get; set; } = RangeConstants.SensMembers;

        /// <summary>See <see cref="RangeConstants"/> Huvudman* constants.</summary>
        public string? HuvudmanType { get; set; }
        /// <summary>Umbraco club node id when the owner is a club on the platform.</summary>
        public int? HuvudmanClubId { get; set; }
        /// <summary>Free text when the owner is off-platform.</summary>
        public string? HuvudmanName { get; set; }

        public string? SkjutbanechefName { get; set; }
        public string? SkjutbanechefContact { get; set; }

        public string? Description { get; set; }

        /// <summary>
        /// Default shot count written when a forgotten check-in is auto-closed at end of day.
        /// Configured on the range modal's Aktivitet tab. Null/0 → write 0 shots on auto-close.
        /// </summary>
        public int? DefaultShotCount { get; set; }

        /// <summary>'Active' | 'Inactive' | 'Decommissioned' | 'UnclaimedSeed'.</summary>
        public string Status { get; set; } = RangeConstants.StatusUnclaimedSeed;
        /// <summary>'Osm' | 'Manual' | 'Municipal' | 'Claimed'.</summary>
        public string Source { get; set; } = RangeConstants.SourceManual;
        /// <summary>Back-link to the OSM element id (e.g. "relation/3861478") for dedup.</summary>
        public string? OsmRef { get; set; }

        public int? CreatedByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A bana / "skjutvall" / skjutplats within a <see cref="ShootingRange"/>. Child configuration +
    /// kulfång (bullet-catcher) detail. NOT separately permitted — described inside the facility's one
    /// permit; the kulfång is the safety object the police besiktning scrutinizes per bana.
    /// </summary>
    [TableName("RangeSection")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RangeSection
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        public string Label { get; set; } = "";
        public string? BanaType { get; set; }
        public int? DistanceMeters { get; set; }
        public int? DirectionDegrees { get; set; }
        public int? FiringPoints { get; set; }
        public string? KulfangSpec { get; set; }
        public string? AllowedWeaponsCalibers { get; set; }
        public string? Notes { get; set; }
        public int SortOrder { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Many-to-many link: a club uses (and/or owns) a range. Informational only — access to a range's
    /// private data is governed by <see cref="RangeSteward"/>, never by this link.
    /// </summary>
    [TableName("ClubRangeLink")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubRangeLink
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        public int ClubId { get; set; }
        /// <summary>'Owner' | 'PrimaryUser' | 'User' | 'Tenant'.</summary>
        public string RelationType { get; set; } = RangeConstants.RelationUser;
        public int? AddedByMemberId { get; set; }
        public DateTime AddedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A club's day/time slot at a range, sitting inside the range's allowed window (permit/instruktion).
    /// One row per (day, window) — e.g. Club A on Mon+Tue 09–21 = two rows; a split Saturday is
    /// Club A {Sat 09–12} + Club B {Sat 12–15}. Optionally scoped to a single <see cref="RangeSection"/>.
    /// </summary>
    [TableName("ClubRangeAllocation")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class ClubRangeAllocation
    {
        public int Id { get; set; }
        public int ClubRangeLinkId { get; set; }
        public int? RangeSectionId { get; set; }
        /// <summary>ISO-8601: 1=Mon … 7=Sun.</summary>
        public byte DayOfWeek { get; set; }
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public DateTime? ValidFrom { get; set; }
        public DateTime? ValidTo { get; set; }
        public string? Note { get; set; }
        public int? CreatedByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Access-control row: a member who may see/edit a range's private data. Claiming a range creates
    /// the first steward; a steward may add co-stewards. Stewardship ≠ ownership ≠ club-admin.
    /// Site admins always have access regardless of these rows.
    /// </summary>
    [TableName("RangeSteward")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RangeSteward
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        public int MemberId { get; set; }
        public int? GrantedByMemberId { get; set; }
        public DateTime GrantedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// A permit attached to a range — police tillstånd or an environmental anmälan/tillstånd. Holds the
    /// facility-level shot cap (<see cref="MaxShotsPerYear"/>) and the allowed shooting window
    /// (<see cref="AllowedWindows"/>, JSON). Expiry feeds renewal reminders. Phase 2.
    /// </summary>
    [TableName("RangePermit")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RangePermit
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        /// <summary>See <see cref="RangeConstants"/> Permit* type constants.</summary>
        public string PermitType { get; set; } = RangeConstants.PermitPolice;
        public string? IssuingAuthority { get; set; }
        public string? ReferenceNumber { get; set; }
        public DateTime? IssuedDate { get; set; }
        public DateTime? ExpiryDate { get; set; }
        public int? MaxShotsPerYear { get; set; }
        /// <summary>JSON array of { day:1-7, start:"HH:mm", end:"HH:mm" } — the legal shooting window.</summary>
        public string? AllowedWindows { get; set; }
        public string? Conditions { get; set; }
        public string Status { get; set; } = RangeConstants.PermitStatusActive;
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// An uploaded compliance file for a range (tillstånd, besiktningsprotokoll, skjutbaneinstruktion,
    /// bullerutredning, markundersökning/bly, skötselplan, försäkring …). The file lives under
    /// App_Data/range-documents; only <see cref="FileRef"/> (the bare stored filename) is in the DB.
    /// </summary>
    [TableName("RangeDocument")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RangeDocument
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        /// <summary>See <see cref="RangeConstants"/> Doc* constants.</summary>
        public string DocType { get; set; } = RangeConstants.DocOther;
        public string Title { get; set; } = "";
        public string FileRef { get; set; } = "";
        public DateTime? IssuedDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public int? UploadedByMemberId { get; set; }
        public DateTime UploadedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// One logged shooting session at a range. The annual sum of <see cref="ShotCount"/> per range is
    /// reported against the environmental permit's MaxShotsPerYear; distinct <see cref="Date"/>s are
    /// shooting-days; <see cref="StartTime"/> gives the time-of-day distribution. Provenance is tagged
    /// via <see cref="ShotCountSource"/> (see <see cref="RangeConstants"/> ShotSource* constants).
    /// A QR check-in creates a row with EndTime null (open); check-out sets EndTime + ShotCount.
    /// </summary>
    [TableName("RangeActivitySession")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RangeActivitySession
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        public int? RangeSectionId { get; set; }
        public int? MemberId { get; set; }
        public int? ClubId { get; set; }
        public DateTime Date { get; set; }
        public TimeSpan? StartTime { get; set; }
        public TimeSpan? EndTime { get; set; }
        public int ShotCount { get; set; }
        public string ShotCountSource { get; set; } = RangeConstants.ShotSourceManual;
        public int ShooterCount { get; set; } = 1;
        public int? LinkedCompetitionId { get; set; }
        public int? LinkedTrainingScoreId { get; set; }
        public int? EnteredByMemberId { get; set; }
        public string? Note { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }
}
