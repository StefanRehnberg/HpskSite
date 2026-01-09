using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    public class AddMaxSeriesCountToTrainingMatches : AsyncMigrationBase
    {
        public AddMaxSeriesCountToTrainingMatches(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            if (!ColumnExists("TrainingMatches", "MaxSeriesCount"))
            {
                Alter.Table("TrainingMatches")
                    .AddColumn("MaxSeriesCount").AsInt32().Nullable();
            }
        }
    }
}
