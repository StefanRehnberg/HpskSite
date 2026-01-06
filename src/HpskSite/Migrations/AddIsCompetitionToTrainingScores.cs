using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to add IsCompetition column to TrainingScores table.
    ///
    /// Purpose: Allow members to store results from external competitions (other regions/countries)
    /// that are not tracked in the main competition system. These will be distinguished from
    /// training sessions and counted separately in statistics.
    ///
    /// This migration adds:
    /// - IsCompetition (BIT) column with default value FALSE
    /// </summary>
    public class AddIsCompetitionToTrainingScores : AsyncMigrationBase
    {
        public AddIsCompetitionToTrainingScores(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            if (!ColumnExists("TrainingScores", "IsCompetition"))
            {
                Alter.Table("TrainingScores")
                    .AddColumn("IsCompetition").AsBoolean().NotNullable().WithDefaultValue(false);
            }

            await Task.CompletedTask;
        }
    }
}
