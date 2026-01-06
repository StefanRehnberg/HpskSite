using System.ComponentModel.DataAnnotations;
using NPoco;
using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to create the RefreshTokens table for JWT authentication
    /// </summary>
    public class CreateRefreshTokensTable : MigrationBase
    {
        public CreateRefreshTokensTable(IMigrationContext context)
            : base(context)
        {
        }

        protected override void Migrate()
        {
            if (!TableExists("RefreshTokens"))
            {
                Create.Table<RefreshTokenDto>().Do();
            }
        }
    }

    /// <summary>
    /// DTO for RefreshTokens table
    /// </summary>
    [TableName("RefreshTokens")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RefreshTokenDto
    {
        /// <summary>
        /// Primary key
        /// </summary>
        [Column("Id")]
        public int Id { get; set; }

        /// <summary>
        /// Member ID this token belongs to
        /// </summary>
        [Column("MemberId")]
        public int MemberId { get; set; }

        /// <summary>
        /// The refresh token value
        /// </summary>
        [Column("Token")]
        [MaxLength(500)]
        public string Token { get; set; } = string.Empty;

        /// <summary>
        /// When the token expires
        /// </summary>
        [Column("ExpiresAt")]
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// When the token was created
        /// </summary>
        [Column("CreatedAt")]
        public DateTime CreatedAt { get; set; }

        /// <summary>
        /// When the token was revoked (null if still valid)
        /// </summary>
        [Column("RevokedAt")]
        public DateTime? RevokedAt { get; set; }

        /// <summary>
        /// IP address where token was created
        /// </summary>
        [Column("CreatedByIp")]
        [MaxLength(50)]
        public string? CreatedByIp { get; set; }

        /// <summary>
        /// IP address where token was revoked
        /// </summary>
        [Column("RevokedByIp")]
        [MaxLength(50)]
        public string? RevokedByIp { get; set; }

        /// <summary>
        /// Token that replaced this one (when refreshed)
        /// </summary>
        [Column("ReplacedByToken")]
        [MaxLength(500)]
        public string? ReplacedByToken { get; set; }

        /// <summary>
        /// User agent of the device that created this token
        /// </summary>
        [Column("UserAgent")]
        [MaxLength(500)]
        public string? UserAgent { get; set; }

        /// <summary>
        /// Check if token is expired
        /// </summary>
        [Ignore]
        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;

        /// <summary>
        /// Check if token has been revoked
        /// </summary>
        [Ignore]
        public bool IsRevoked => RevokedAt != null;

        /// <summary>
        /// Check if token is still active (not expired and not revoked)
        /// </summary>
        [Ignore]
        public bool IsActive => !IsExpired && !IsRevoked;
    }
}
