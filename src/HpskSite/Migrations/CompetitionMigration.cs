using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    public class CompetitionMigration : AsyncMigrationBase
    {
        public CompetitionMigration(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Create CompetitionRegistrations table
            if (!TableExists("CompetitionRegistrations"))
            {
                Create.Table("CompetitionRegistrations")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_CompetitionRegistrations").Identity()
                    .WithColumn("MemberId").AsInt32().NotNullable()
                    .WithColumn("CompetitionId").AsInt32().NotNullable()
                    .WithColumn("MemberClass").AsString(50).NotNullable()
                    .WithColumn("RegistrationDate").AsDateTime().NotNullable()
                    .WithColumn("StartNumber").AsInt32().Nullable()
                    .WithColumn("Notes").AsString(1000).Nullable()
                    .WithColumn("IsActive").AsBoolean().NotNullable().WithDefaultValue(true)
                    .Do();

                // Add foreign key constraints
                Create.ForeignKey("FK_CompetitionRegistrations_Member")
                    .FromTable("CompetitionRegistrations").ForeignColumn("MemberId")
                    .ToTable("cmsMember").PrimaryColumn("nodeId")
                    .Do();

                Create.ForeignKey("FK_CompetitionRegistrations_Competition")
                    .FromTable("CompetitionRegistrations").ForeignColumn("CompetitionId")
                    .ToTable("umbracoNode").PrimaryColumn("id")
                    .Do();
            }

            // Create indexes for CompetitionRegistrations table
            if (TableExists("CompetitionRegistrations"))
            {
                Create.Index("IX_CompetitionRegistrations_MemberId")
                    .OnTable("CompetitionRegistrations")
                    .OnColumn("MemberId");

                Create.Index("IX_CompetitionRegistrations_CompetitionId")
                    .OnTable("CompetitionRegistrations")
                    .OnColumn("CompetitionId");
            }

            // Create CompetitionSeries table
            if (!TableExists("CompetitionSeries"))
            {
                Create.Table("CompetitionSeries")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_CompetitionSeries").Identity()
                    .WithColumn("RegistrationId").AsInt32().NotNullable()
                    .WithColumn("SeriesNumber").AsInt32().NotNullable() // 1, 2, 3, etc.
                    .WithColumn("SeriesType").AsString(50).NotNullable() // "Precision", "Standard", etc.
                    .WithColumn("CompletedDate").AsDateTime().Nullable()
                    .WithColumn("IsCompleted").AsBoolean().NotNullable().WithDefaultValue(false)
                    .WithColumn("Notes").AsString(500).Nullable()
                    .Do();

                // Add foreign key constraint
                Create.ForeignKey("FK_CompetitionSeries_Registration")
                    .FromTable("CompetitionSeries").ForeignColumn("RegistrationId")
                    .ToTable("CompetitionRegistrations").PrimaryColumn("Id")
                    .Do();

            }

            // Create indexes for CompetitionSeries table
            if (TableExists("CompetitionSeries"))
            {
                Create.Index("IX_CompetitionSeries_RegistrationId")
                    .OnTable("CompetitionSeries")
                    .OnColumn("RegistrationId");

                Create.Index("IX_CompetitionSeries_SeriesNumber")
                    .OnTable("CompetitionSeries")
                    .OnColumn("SeriesNumber");
            }

            // Create CompetitionResults table (for individual shots)
            if (!TableExists("CompetitionResults"))
            {
                Create.Table("CompetitionResults")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_CompetitionResults").Identity()
                    .WithColumn("SeriesId").AsInt32().NotNullable()
                    .WithColumn("ShotNumber").AsInt32().NotNullable() // 1-10 for each series
                    .WithColumn("ShotValue").AsString(10).NotNullable() // "1", "2", ..., "10", "X"
                    .WithColumn("ShotPoints").AsDecimal(4, 1).NotNullable() // 1.0, 2.0, ..., 10.0, 10.9
                    .WithColumn("Ring").AsString(10).Nullable() // Inner/Outer ring designation
                    .WithColumn("EnteredBy").AsInt32().Nullable() // Member ID who entered the shot
                    .WithColumn("EnteredDate").AsDateTime().NotNullable()
                    .WithColumn("ModifiedBy").AsInt32().Nullable()
                    .WithColumn("ModifiedDate").AsDateTime().Nullable()
                    .Do();

                // Add foreign key constraints
                Create.ForeignKey("FK_CompetitionResults_Series")
                    .FromTable("CompetitionResults").ForeignColumn("SeriesId")
                    .ToTable("CompetitionSeries").PrimaryColumn("Id")
                    .Do();

                Create.ForeignKey("FK_CompetitionResults_EnteredBy")
                    .FromTable("CompetitionResults").ForeignColumn("EnteredBy")
                    .ToTable("cmsMember").PrimaryColumn("nodeId")
                    .Do();

                Create.ForeignKey("FK_CompetitionResults_ModifiedBy")
                    .FromTable("CompetitionResults").ForeignColumn("ModifiedBy")
                    .ToTable("cmsMember").PrimaryColumn("nodeId")
                    .Do();

            }

            // Create indexes for CompetitionResults table
            if (TableExists("CompetitionResults"))
            {
                Create.Index("IX_CompetitionResults_SeriesId")
                    .OnTable("CompetitionResults")
                    .OnColumn("SeriesId");

                Create.Index("IX_CompetitionResults_ShotNumber")
                    .OnTable("CompetitionResults")
                    .OnColumn("ShotNumber");
            }

            // Create CompetitionTotals table (for series and overall totals)
            if (!TableExists("CompetitionTotals"))
            {
                Create.Table("CompetitionTotals")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_CompetitionTotals").Identity()
                    .WithColumn("RegistrationId").AsInt32().NotNullable()
                    .WithColumn("SeriesId").AsInt32().Nullable() // NULL for overall total
                    .WithColumn("TotalType").AsString(20).NotNullable() // "Series", "Overall"
                    .WithColumn("TotalPoints").AsDecimal(6, 1).NotNullable() // Sum of all shots
                    .WithColumn("InnerTens").AsInt32().NotNullable().WithDefaultValue(0) // Count of X values
                    .WithColumn("Tens").AsInt32().NotNullable().WithDefaultValue(0) // Count of 10 values (including X)
                    .WithColumn("MaxPossible").AsDecimal(6, 1).NotNullable() // Maximum possible points for this total
                    .WithColumn("CalculatedDate").AsDateTime().NotNullable()
                    .WithColumn("IsOfficial").AsBoolean().NotNullable().WithDefaultValue(false)
                    .Do();

                // Add foreign key constraints
                Create.ForeignKey("FK_CompetitionTotals_Registration")
                    .FromTable("CompetitionTotals").ForeignColumn("RegistrationId")
                    .ToTable("CompetitionRegistrations").PrimaryColumn("Id")
                    .Do();

                Create.ForeignKey("FK_CompetitionTotals_Series")
                    .FromTable("CompetitionTotals").ForeignColumn("SeriesId")
                    .ToTable("CompetitionSeries").PrimaryColumn("Id")
                    .Do();

            }

            // Create indexes for CompetitionTotals table
            if (TableExists("CompetitionTotals"))
            {
                Create.Index("IX_CompetitionTotals_RegistrationId")
                    .OnTable("CompetitionTotals")
                    .OnColumn("RegistrationId");

                Create.Index("IX_CompetitionTotals_TotalType")
                    .OnTable("CompetitionTotals")
                    .OnColumn("TotalType");
            }
        }
    }
}