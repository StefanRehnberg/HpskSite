using NPoco;

namespace HpskSite.Models
{
    public enum CertificationType
    {
        Foreningsinstruktor,
        Kretsinstruktor,
        Riksinstruktor,
        Vapenkontrollant,
        Banlaggare
    }

    public static class CertificationTypes
    {
        public const string Foreningsinstruktor = "Foreningsinstruktor";
        public const string Kretsinstruktor = "Kretsinstruktor";
        public const string Riksinstruktor = "Riksinstruktor";
        public const string Vapenkontrollant = "Vapenkontrollant";
        public const string Banlaggare = "Banlaggare";

        /// <summary>
        /// Display label in Swedish (with diacritics). The string identifier above is used
        /// for storage and member-group naming where diacritics are awkward.
        /// </summary>
        public static string DisplayName(string type) => type switch
        {
            Foreningsinstruktor => "Föreningsinstruktör",
            Kretsinstruktor => "Kretsinstruktör",
            Riksinstruktor => "Riksinstruktör",
            Vapenkontrollant => "Vapenkontrollant",
            Banlaggare => "Banläggare",
            _ => type
        };

        /// <summary>
        /// True if the certification by itself grants authority — no separate appointment
        /// step is required. Currently Vapenkontrollant and Banläggare.
        /// </summary>
        public static bool IsAppointmentless(string type) =>
            type == Vapenkontrollant || type == Banlaggare;

        /// <summary>
        /// Member-group name used to flag holders. For the appointment-bearing types this
        /// includes the scope id; for the appointmentless types it's a single global group.
        /// Returns null if no group is applicable (shouldn't happen with current types).
        /// </summary>
        public static string? AppointmentGroup(string type, string? scopeId) => type switch
        {
            Foreningsinstruktor when !string.IsNullOrEmpty(scopeId) => $"Foreningsinstruktor_{scopeId}",
            Kretsinstruktor when !string.IsNullOrEmpty(scopeId) => $"Kretsinstruktor_{scopeId}",
            Riksinstruktor when !string.IsNullOrEmpty(scopeId) => $"Riksinstruktor_{scopeId}",
            Vapenkontrollant => "Vapenkontrollant",
            Banlaggare => "Banlaggare",
            _ => null
        };

        /// <summary>
        /// Prefix used when scanning a member's roles to find appointments of a given type.
        /// </summary>
        public static string AppointmentGroupPrefix(string type) => type switch
        {
            Foreningsinstruktor => "Foreningsinstruktor_",
            Kretsinstruktor => "Kretsinstruktor_",
            Riksinstruktor => "Riksinstruktor_",
            _ => ""
        };
    }

    [TableName("MemberCertifications")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class MemberCertification
    {
        public int Id { get; set; }
        public int MemberId { get; set; }
        public string CertificationType { get; set; } = "";
        public int? CertifiedByMemberId { get; set; }
        public DateTime CertifiedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? CertificateNumber { get; set; }
        public bool IsActive { get; set; }
        public DateTime? RevokedAt { get; set; }
        public int? RevokedByMemberId { get; set; }
        public string? RevokedReason { get; set; }
        public string? Notes { get; set; }
        public DateTime CreatedAt { get; set; }

        [Ignore]
        public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.UtcNow;
    }
}
