using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to add StartDate to TrainingMatches and create TrainingMatchJoinRequests table
    /// </summary>
    public class CreateTrainingMatchJoinRequestsTable : AsyncMigrationBase
    {
        public CreateTrainingMatchJoinRequestsTable(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Add StartDate column to TrainingMatches if it doesn't exist
            if (!ColumnExists("TrainingMatches", "StartDate"))
            {
                Alter.Table("TrainingMatches")
                    .AddColumn("StartDate").AsDateTime().Nullable()
                    .Do();

                // Set default StartDate to CreatedDate for existing matches (they've already started)
                Execute.Sql("UPDATE TrainingMatches SET StartDate = CreatedDate WHERE StartDate IS NULL");
            }

            // Create TrainingMatchJoinRequests table
            if (!TableExists("TrainingMatchJoinRequests"))
            {
                Create.Table("TrainingMatchJoinRequests")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_TrainingMatchJoinRequests").Identity()
                    .WithColumn("TrainingMatchId").AsInt32().NotNullable()
                    .WithColumn("MemberId").AsInt32().NotNullable()
                    .WithColumn("MemberName").AsString(200).Nullable()
                    .WithColumn("MemberProfilePictureUrl").AsString(500).Nullable()
                    .WithColumn("Status").AsString(20).NotNullable().WithDefaultValue("Pending")
                    .WithColumn("RequestDate").AsDateTime().NotNullable()
                    .WithColumn("ResponseDate").AsDateTime().Nullable()
                    .WithColumn("ResponseByMemberId").AsInt32().Nullable()
                    .WithColumn("Notes").AsString(500).Nullable()
                    .Do();

                // Create indexes for performance
                Create.Index("IX_TrainingMatchJoinRequests_TrainingMatchId")
                    .OnTable("TrainingMatchJoinRequests")
                    .OnColumn("TrainingMatchId")
                    .Ascending();

                Create.Index("IX_TrainingMatchJoinRequests_MemberId")
                    .OnTable("TrainingMatchJoinRequests")
                    .OnColumn("MemberId")
                    .Ascending();

                Create.Index("IX_TrainingMatchJoinRequests_Status")
                    .OnTable("TrainingMatchJoinRequests")
                    .OnColumn("Status")
                    .Ascending();

                // Create unique constraint to prevent duplicate requests
                Create.Index("UQ_TrainingMatchJoinRequests_MatchMember")
                    .OnTable("TrainingMatchJoinRequests")
                    .OnColumn("TrainingMatchId").Ascending()
                    .OnColumn("MemberId").Ascending()
                    .WithOptions().Unique();
            }
        }
    }
}
