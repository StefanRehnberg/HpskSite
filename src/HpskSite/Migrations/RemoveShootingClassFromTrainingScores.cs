using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to remove ShootingClass column and index from TrainingScores table.
    ///
    /// Background: ShootingClass was originally added but is not needed. TrainingScores should only
    /// track WeaponClass (A, B, C, R). ShootingClass refers to specific competition classes like
    /// "A3", "C Vet Y" which is separate from weapon classification.
    ///
    /// This migration:
    /// 1. Drops the IX_TrainingScores_ShootingClass index
    /// 2. Drops the ShootingClass column
    /// </summary>
    public class RemoveShootingClassFromTrainingScores : AsyncMigrationBase
    {
        public RemoveShootingClassFromTrainingScores(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Drop index if it exists
            if (IndexExists("IX_TrainingScores_ShootingClass"))
            {
                Delete.Index("IX_TrainingScores_ShootingClass")
                    .OnTable("TrainingScores");
            }

            // Drop column if it exists
            if (ColumnExists("TrainingScores", "ShootingClass"))
            {
                Delete.Column("ShootingClass")
                    .FromTable("TrainingScores");
            }

            await Task.CompletedTask;
        }
    }
}
