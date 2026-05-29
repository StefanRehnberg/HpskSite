namespace HpskSite.Models
{
    /// <summary>
    /// Domain constants and small rule helpers for Standardmedaljer (Standard medals).
    ///
    /// Rules (SHB, simplified for our purposes):
    ///  * Silver = 2 points, Brons = 1 point.
    ///  * Riksmästarklass (klass 3): per-discipline points from the PREVIOUS year must reach 3.
    ///    (The medal-combo rule — silver+brons / 2×silver / 3×brons — is exactly ">= 3 points".
    ///    The Dam/Junior single-bronze and SM-öppen-klass-bronze exceptions are intentionally
    ///    out of scope; the indicator is a member motivator, not the legal arbiter.)
    ///  * Guldmedalj: points pooled across ALL disciplines, lifetime, 50 points per medal.
    /// </summary>
    public static class StandardMedals
    {
        // ── Medal types (matches the existing TrainingScores CompetitionStdMedal convention) ──
        public const string Silver = "S";
        public const string Brons = "B";

        public const int SilverPoints = 2;
        public const int BronsPoints = 1;

        // ── Thresholds ──
        public const int QualificationThreshold = 3; // points per discipline, previous year, for klass 3
        public const int GoldThreshold = 50;          // pooled points per Guldmedalj

        // ── Sources ──
        public const string SourceOnSite = "OnSite";
        public const string SourceSelfReported = "SelfReported";
        public const string SourceAdminEntered = "AdminEntered";

        // ── Award status ──
        public const string StatusReported = "Reported";
        public const string StatusVerified = "Verified";
        public const string StatusRejected = "Rejected";

        // ── Proof types ──
        public const string ProofFile = "File";
        public const string ProofOnSite = "OnSite";
        public const string ProofAttestation = "Attestation";

        // ── Gold application status ──
        public const string GoldStatusDraft = "Draft";
        public const string GoldStatusApplied = "Applied";
        public const string GoldStatusApproved = "Approved";
        public const string GoldStatusRejected = "Rejected";

        // ── Disciplines ──
        public const string Precision = "Precision";
        public const string MagnumPrecision = "MagnumPrecision";
        public const string Milsnabb = "Milsnabb";
        public const string Faltskytte = "Faltskytte";
        public const string MagnumFalt = "MagnumFalt";
        public const string NationellHelmatch = "NationellHelmatch";
        public const string Duell = "Duell";

        /// <summary>
        /// Disciplines that have a Riksmästarklass (klass 3) and therefore show a qualification
        /// indicator. Magnum disciplines are excluded — they have no class division (SM open to all).
        /// Adjust this list if Nationell helmatch should not show an indicator.
        /// </summary>
        public static readonly string[] QualificationDisciplines =
        {
            Precision, Faltskytte, Milsnabb, NationellHelmatch
        };

        /// <summary>Points awarded for a medal type, or 0 if not a recognized medal.</summary>
        public static int PointsFor(string? medalType) => medalType switch
        {
            Silver => SilverPoints,
            Brons => BronsPoints,
            _ => 0
        };

        public static bool IsMedal(string? medalType) =>
            medalType == Silver || medalType == Brons;

        public static string MedalDisplayName(string? medalType) => medalType switch
        {
            Silver => "Silver",
            Brons => "Brons",
            _ => medalType ?? ""
        };

        public static string DisciplineDisplayName(string? discipline) => discipline switch
        {
            Precision => "Precisionsskjutning",
            MagnumPrecision => "Magnumprecision",
            Milsnabb => "Militär snabbmatch",
            Faltskytte => "Fältskjutning",
            MagnumFalt => "Magnumfält",
            NationellHelmatch => "Nationell helmatch",
            Duell => "Duellskjutning",
            _ => discipline ?? ""
        };
    }
}
