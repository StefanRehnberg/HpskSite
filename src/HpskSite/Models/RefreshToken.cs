using System.ComponentModel.DataAnnotations;
using NPoco;

namespace HpskSite.Migrations
{
    /// <summary>
    /// DTO for the RefreshTokens table — JWT refresh-token storage for the mobile API.
    /// Schema lives in Migrations/create-refresh-tokens-table.sql (run manually in SSMS for
    /// new databases). Kept under the HpskSite.Migrations namespace for backwards
    /// compatibility with existing call sites in AuthApiController.
    /// </summary>
    [TableName("RefreshTokens")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RefreshTokenDto
    {
        /// <summary>Primary key</summary>
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>Member ID this token belongs to</summary>
        [Column("MemberId")]
        public int MemberId { get; set; }

        /// <summary>
        /// Klartexten. ⚠️ Skrivs INTE längre (säkerhetsplanen 2026-11-02, H6) — bara <see cref="TokenHash"/> lagras. Kolumnen
        /// finns kvar tills steg 2-skriptet tömt och tagit bort den; äldre rader har den fortfarande ifylld.
        /// </summary>
        [Column("Token")]
        [MaxLength(500)]
        public string? Token { get; set; }

        /// <summary>SHA-256 av token som 64 gemena hextecken (<c>RefreshTokenHasher</c>). Det som slås upp.</summary>
        [Column("TokenHash")]
        [MaxLength(64)]
        public string? TokenHash { get; set; }

        /// <summary>Alla token som kommer ur samma inloggning delar familj. En återanvänd token återkallar hela familjen.</summary>
        [Column("FamilyId")]
        public Guid? FamilyId { get; set; }

        /// <summary>Hashen av token som ersatte den här (när den roterades).</summary>
        [Column("ReplacedByHash")]
        [MaxLength(64)]
        public string? ReplacedByHash { get; set; }

        /// <summary>När token senast användes för att få en ny (UTC) — grunden för nådefönstret vid dubbelanrop.</summary>
        [Column("LastUsedAt")]
        public DateTime? LastUsedAt { get; set; }

        /// <summary>When the token expires</summary>
        [Column("ExpiresAt")]
        public DateTime ExpiresAt { get; set; }

        /// <summary>When the token was created</summary>
        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>When the token was revoked (null if still valid)</summary>
        [Column("RevokedAt")]
        public DateTime? RevokedAt { get; set; }

        /// <summary>IP address where token was created</summary>
        [Column("CreatedByIp")]
        [MaxLength(50)]
        public string? CreatedByIp { get; set; }

        /// <summary>IP address where token was revoked</summary>
        [Column("RevokedByIp")]
        [MaxLength(50)]
        public string? RevokedByIp { get; set; }

        /// <summary>Token that replaced this one (when refreshed)</summary>
        [Column("ReplacedByToken")]
        [MaxLength(500)]
        public string? ReplacedByToken { get; set; }

        /// <summary>User agent of the device that created this token</summary>
        [Column("UserAgent")]
        [MaxLength(500)]
        public string? UserAgent { get; set; }

        /// <summary>Check if token is expired</summary>
        [Ignore]
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

        /// <summary>Check if token has been revoked</summary>
        [Ignore]
        public bool IsRevoked => RevokedAt != null;

        /// <summary>Check if token is still active (not expired and not revoked)</summary>
        [Ignore]
        public bool IsActive => !IsExpired && !IsRevoked;
    }
}
