using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to create TrainingScores table for member training session logging
    /// </summary>
    public class CreateTrainingScoresTable : AsyncMigrationBase
    {
        public CreateTrainingScoresTable(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Create TrainingScores table
            if (!TableExists("TrainingScores"))
            {
                Create.Table("TrainingScores")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_TrainingScores").Identity()
                    .WithColumn("MemberId").AsInt32().NotNullable()
                    .WithColumn("TrainingDate").AsDateTime().NotNullable()
                    .WithColumn("ShootingClass").AsString(50).NotNullable()
                    .WithColumn("SeriesScores").AsString(int.MaxValue).NotNullable() // JSON array of series
                    .WithColumn("TotalScore").AsInt32().NotNullable()
                    .WithColumn("XCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("Notes").AsString(1000).Nullable()
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("UpdatedAt").AsDateTime().NotNullable()
                    .Do();

                // Create indexes
                Create.Index("IX_TrainingScores_MemberId")
                    .OnTable("TrainingScores")
                    .OnColumn("MemberId")
                    .Ascending();

                Create.Index("IX_TrainingScores_TrainingDate")
                    .OnTable("TrainingScores")
                    .OnColumn("TrainingDate")
                    .Ascending();

                Create.Index("IX_TrainingScores_ShootingClass")
                    .OnTable("TrainingScores")
                    .OnColumn("ShootingClass")
                    .Ascending();
            }
        }
    }
}
