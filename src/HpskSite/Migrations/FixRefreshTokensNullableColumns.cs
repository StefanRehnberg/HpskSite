using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to fix nullable columns in RefreshTokens table
    /// </summary>
    public class FixRefreshTokensNullableColumns : MigrationBase
    {
        public FixRefreshTokensNullableColumns(IMigrationContext context)
            : base(context)
        {
        }

        protected override void Migrate()
        {
            if (TableExists("RefreshTokens"))
            {
                // Alter columns to allow NULL values
                Alter.Table("RefreshTokens")
                    .AlterColumn("RevokedAt").AsDateTime().Nullable()
                    .Do();

                Alter.Table("RefreshTokens")
                    .AlterColumn("RevokedByIp").AsString(50).Nullable()
                    .Do();

                Alter.Table("RefreshTokens")
                    .AlterColumn("ReplacedByToken").AsString(500).Nullable()
                    .Do();
            }
        }
    }
}
