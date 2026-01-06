using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to recreate the RefreshTokens table with correct schema
    /// </summary>
    public class RecreateRefreshTokensTable : MigrationBase
    {
        public RecreateRefreshTokensTable(IMigrationContext context)
            : base(context)
        {
        }

        protected override void Migrate()
        {
            // Drop existing table if it exists
            if (TableExists("RefreshTokens"))
            {
                Delete.Table("RefreshTokens").Do();
            }

            // Create table with correct schema using raw SQL for precise control
            Database.Execute(@"
                CREATE TABLE [RefreshTokens] (
                    [Id] INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    [MemberId] INT NOT NULL,
                    [Token] NVARCHAR(500) NOT NULL,
                    [ExpiresAt] DATETIME NOT NULL,
                    [CreatedAt] DATETIME NOT NULL,
                    [RevokedAt] DATETIME NULL,
                    [CreatedByIp] NVARCHAR(50) NULL,
                    [RevokedByIp] NVARCHAR(50) NULL,
                    [ReplacedByToken] NVARCHAR(500) NULL,
                    [UserAgent] NVARCHAR(500) NULL
                )
            ");

            // Create index on Token for faster lookups
            Database.Execute(@"
                CREATE INDEX [IX_RefreshTokens_Token] ON [RefreshTokens] ([Token])
            ");

            // Create index on MemberId for faster lookups
            Database.Execute(@"
                CREATE INDEX [IX_RefreshTokens_MemberId] ON [RefreshTokens] ([MemberId])
            ");
        }
    }
}
