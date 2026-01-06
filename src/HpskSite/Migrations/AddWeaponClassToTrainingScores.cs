using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    public class AddWeaponClassToTrainingScores : AsyncMigrationBase
    {
        public AddWeaponClassToTrainingScores(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            if (!ColumnExists("TrainingScores", "WeaponClass"))
            {
                Alter.Table("TrainingScores")
                    .AddColumn("WeaponClass").AsString(10).Nullable();
            }
        }
    }
}
