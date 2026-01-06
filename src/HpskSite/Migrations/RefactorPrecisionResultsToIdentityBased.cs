using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to refactor PrecisionResultEntry to identity-based system
    ///
    /// BREAKING CHANGE: This migration drops and recreates the table.
    /// All existing results data will be lost.
    ///
    /// Changes:
    /// - Results now stored by (CompetitionId, MemberId, SeriesNumber) instead of (CompetitionId, TeamNumber, Position, SeriesNumber)
    /// - TeamNumber and Position become informational fields only
    /// - Enables late registrations without data loss
    /// - Start lists can be regenerated without invalidating results
    /// </summary>
    public class RefactorPrecisionResultsToIdentityBased : AsyncMigrationBase
    {
        public RefactorPrecisionResultsToIdentityBased(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Drop existing table if it exists (beta - data can be scraped)
            if (TableExists("PrecisionResultEntry"))
            {
                Delete.Table("PrecisionResultEntry").Do();
            }

            // Create PrecisionResultEntry table with new identity-based structure
            Create.Table("PrecisionResultEntry")
                .WithColumn("Id").AsInt32().PrimaryKey("PK_PrecisionResultEntry").Identity()
                .WithColumn("CompetitionId").AsInt32().NotNullable()
                .WithColumn("SeriesNumber").AsInt32().NotNullable()
                .WithColumn("MemberId").AsInt32().NotNullable() // IDENTITY FIELD - Primary lookup
                .WithColumn("TeamNumber").AsInt32().NotNullable() // INFORMATIONAL - Position at time of entry
                .WithColumn("Position").AsInt32().NotNullable() // INFORMATIONAL - Position at time of entry
                .WithColumn("ShootingClass").AsString(50).NotNullable()
                .WithColumn("Shots").AsString(50).NotNullable() // JSON: ["X","10","9","8","7"]
                .WithColumn("EnteredBy").AsInt32().NotNullable() // Range officer MemberId
                .WithColumn("EnteredAt").AsDateTime().NotNullable()
                .WithColumn("LastModified").AsDateTime().NotNullable()
                .Do();

            // Create UNIQUE index on (CompetitionId, MemberId, SeriesNumber)
            // This enforces: ONE result per shooter per series, regardless of position
            Create.Index("UX_PrecisionResultEntry_CompetitionMemberSeries")
                .OnTable("PrecisionResultEntry")
                .OnColumn("CompetitionId").Ascending()
                .OnColumn("MemberId").Ascending()
                .OnColumn("SeriesNumber").Ascending()
                .WithOptions()
                .Unique()
                .Do();

            // Create index for competition lookups (frequently used)
            Create.Index("IX_PrecisionResultEntry_CompetitionId")
                .OnTable("PrecisionResultEntry")
                .OnColumn("CompetitionId")
                .Ascending()
                .WithOptions()
                .NonClustered()
                .Do();

            // Create index for member lookups (used for personal results)
            Create.Index("IX_PrecisionResultEntry_MemberId")
                .OnTable("PrecisionResultEntry")
                .OnColumn("MemberId")
                .Ascending()
                .WithOptions()
                .NonClustered()
                .Do();

            // Create index for shooting class (used for results grouping)
            Create.Index("IX_PrecisionResultEntry_ShootingClass")
                .OnTable("PrecisionResultEntry")
                .OnColumn("ShootingClass")
                .Ascending()
                .WithOptions()
                .NonClustered()
                .Do();

            // Also recreate PrecisionResultEntrySession table (session locking for result entry)
            if (TableExists("PrecisionResultEntrySession"))
            {
                Delete.Table("PrecisionResultEntrySession").Do();
            }

            Create.Table("PrecisionResultEntrySession")
                .WithColumn("Id").AsInt32().PrimaryKey("PK_PrecisionResultEntrySession").Identity()
                .WithColumn("CompetitionId").AsInt32().NotNullable()
                .WithColumn("Position").AsInt32().NotNullable()
                .WithColumn("SeriesNumber").AsInt32().NotNullable()
                .WithColumn("RangeOfficerId").AsInt32().NotNullable()
                .WithColumn("SessionStart").AsDateTime().NotNullable()
                .WithColumn("LastActivity").AsDateTime().NotNullable()
                .WithColumn("IsActive").AsBoolean().NotNullable().WithDefaultValue(true)
                .Do();

            // Index for session lookups
            Create.Index("IX_PrecisionResultEntrySession_Competition")
                .OnTable("PrecisionResultEntrySession")
                .OnColumn("CompetitionId").Ascending()
                .OnColumn("Position").Ascending()
                .OnColumn("SeriesNumber").Ascending()
                .WithOptions()
                .NonClustered()
                .Do();
        }
    }
}
