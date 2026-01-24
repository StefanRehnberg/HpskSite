using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to add team support to training matches
    /// - Creates TrainingMatchTeams table
    /// - Adds IsTeamMatch and MaxShootersPerTeam columns to TrainingMatches
    /// - Adds TeamId column to TrainingMatchParticipants
    /// </summary>
    public class AddTeamSupportToTrainingMatches : AsyncMigrationBase
    {
        public AddTeamSupportToTrainingMatches(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Step 1: Add columns to TrainingMatches table
            if (!ColumnExists("TrainingMatches", "IsTeamMatch"))
            {
                Alter.Table("TrainingMatches")
                    .AddColumn("IsTeamMatch").AsBoolean().NotNullable().WithDefaultValue(false);
            }

            if (!ColumnExists("TrainingMatches", "MaxShootersPerTeam"))
            {
                Alter.Table("TrainingMatches")
                    .AddColumn("MaxShootersPerTeam").AsInt32().Nullable();
            }

            // Step 2: Create TrainingMatchTeams table
            if (!TableExists("TrainingMatchTeams"))
            {
                Create.Table("TrainingMatchTeams")
                    .WithColumn("Id").AsInt32().PrimaryKey().Identity()
                    .WithColumn("TrainingMatchId").AsInt32().NotNullable()
                    .WithColumn("TeamNumber").AsInt32().NotNullable()
                    .WithColumn("TeamName").AsString(100).NotNullable()
                    .WithColumn("ClubId").AsInt32().Nullable()
                    .WithColumn("DisplayOrder").AsInt32().NotNullable().WithDefaultValue(0);

                // Add foreign key to TrainingMatches
                Create.ForeignKey("FK_TrainingMatchTeams_TrainingMatches")
                    .FromTable("TrainingMatchTeams").ForeignColumn("TrainingMatchId")
                    .ToTable("TrainingMatches").PrimaryColumn("Id")
                    .OnDelete(System.Data.Rule.Cascade);

                // Add unique constraint for TrainingMatchId + TeamNumber
                Create.Index("IX_TrainingMatchTeams_MatchTeam")
                    .OnTable("TrainingMatchTeams")
                    .OnColumn("TrainingMatchId").Ascending()
                    .OnColumn("TeamNumber").Ascending()
                    .WithOptions().Unique();
            }

            // Step 3: Add TeamId column to TrainingMatchParticipants
            if (!ColumnExists("TrainingMatchParticipants", "TeamId"))
            {
                Alter.Table("TrainingMatchParticipants")
                    .AddColumn("TeamId").AsInt32().Nullable();

                // Add foreign key to TrainingMatchTeams
                Create.ForeignKey("FK_TrainingMatchParticipants_Teams")
                    .FromTable("TrainingMatchParticipants").ForeignColumn("TeamId")
                    .ToTable("TrainingMatchTeams").PrimaryColumn("Id")
                    .OnDelete(System.Data.Rule.None); // Don't cascade - team deletion handled separately
            }

            await Task.CompletedTask;
        }
    }
}
