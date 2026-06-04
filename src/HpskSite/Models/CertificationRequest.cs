using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// A club admin's request to record an SPSF certification for a member whose issuing
    /// instructor (usually a Kretsinstruktör) is not on pistol.nu and therefore cannot be
    /// selected as grantor in the normal "Tilldela certifiering" flow. The request carries
    /// the candidate's SPSF identity so an approver (regional admin for the candidate's
    /// region, or a site admin) can verify them against the SPSF registry. Approval is what
    /// issues the actual <see cref="MemberCertification"/> and flips the functional member
    /// group — so a Pending request grants nothing.
    /// </summary>
    [TableName("CertificationRequests")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class CertificationRequest
    {
        public int Id { get; set; }
        public int CandidateMemberId { get; set; }
        public string CertificationType { get; set; } = "";

        /// <summary>The club the request was submitted from; resolves the candidate's region
        /// for approver routing and (for Föreningsinstruktör) the appointment scope.</summary>
        public int ClubId { get; set; }

        // ── SPSF identity captured at submit time so the approver can verify the candidate ──
        public string CandidateFullName { get; set; } = "";
        public string? CandidateEmail { get; set; }

        /// <summary>The candidate's (shooter's) Pistolkortnummer.</summary>
        public string Pistolkortnummer { get; set; } = "";

        // ── Off-platform issuer (utfärdaren) — the free-text replacement for the on-platform
        // "Certifierad av" dropdown, so the approver can verify the issuer was authorized. ──
        public string IssuerName { get; set; } = "";
        /// <summary>Optional — old certs predate Pistolkort numbers.</summary>
        public string? IssuerPistolkortnummer { get; set; }

        // ── Real certificate attributes, preserved on the request so approval issues the cert
        // with the actual issue date (not the approval date). ──
        public DateTime CertifiedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string? CertificateNumber { get; set; }

        public int RequestedByMemberId { get; set; }
        public DateTime RequestedAt { get; set; }
        public string? RequestNote { get; set; }

        /// <summary>Pending | Approved | Rejected.</summary>
        public string Status { get; set; } = CertificationRequestStatus.Pending;
        public int? ReviewedByMemberId { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public string? ReviewNote { get; set; }

        public DateTime CreatedAt { get; set; }
    }

    public static class CertificationRequestStatus
    {
        public const string Pending = "Pending";
        public const string Approved = "Approved";
        public const string Rejected = "Rejected";
    }
}
