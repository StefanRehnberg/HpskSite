using Umbraco.Cms.Infrastructure.Migrations;

namespace HpskSite.Migrations
{
    /// <summary>
    /// Migration to create DocumentCategories, Documents, and DocumentStorageQuotas tables
    /// for the club/region document archive feature.
    /// </summary>
    public class CreateDocumentArchiveTables : AsyncMigrationBase
    {
        public CreateDocumentArchiveTables(IMigrationContext context) : base(context)
        {
        }

        protected override async Task MigrateAsync()
        {
            // Create DocumentCategories table
            if (!TableExists("DocumentCategories"))
            {
                Create.Table("DocumentCategories")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_DocumentCategories").Identity()
                    .WithColumn("Name").AsString(200).NotNullable()
                    .WithColumn("Description").AsString(1000).Nullable()
                    .WithColumn("OwnerType").AsInt32().NotNullable() // 0=Club, 1=Region
                    .WithColumn("OwnerId").AsInt32().NotNullable()
                    .WithColumn("SortOrder").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("ShowInQuickLinks").AsBoolean().NotNullable().WithDefaultValue(false)
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("CreatedBy").AsInt32().NotNullable()
                    .Do();

                Create.Index("IX_DocumentCategories_Owner")
                    .OnTable("DocumentCategories")
                    .OnColumn("OwnerType").Ascending()
                    .OnColumn("OwnerId").Ascending();
            }

            // Create Documents table
            if (!TableExists("Documents"))
            {
                Create.Table("Documents")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_Documents").Identity()
                    .WithColumn("Title").AsString(300).NotNullable()
                    .WithColumn("Description").AsString(2000).Nullable()
                    .WithColumn("CategoryId").AsInt32().Nullable()
                    .WithColumn("OwnerType").AsInt32().NotNullable()
                    .WithColumn("OwnerId").AsInt32().NotNullable()
                    .WithColumn("FileName").AsString(500).NotNullable()
                    .WithColumn("StoredFileName").AsString(500).NotNullable()
                    .WithColumn("ContentType").AsString(200).NotNullable()
                    .WithColumn("FileSize").AsInt64().NotNullable()
                    .WithColumn("AccessLevel").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("SortOrder").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("ShowInQuickLinks").AsBoolean().NotNullable().WithDefaultValue(false)
                    .WithColumn("DownloadCount").AsInt32().NotNullable().WithDefaultValue(0)
                    .WithColumn("CreatedAt").AsDateTime().NotNullable()
                    .WithColumn("CreatedBy").AsInt32().NotNullable()
                    .WithColumn("UpdatedAt").AsDateTime().NotNullable()
                    .Do();

                Create.Index("IX_Documents_Owner")
                    .OnTable("Documents")
                    .OnColumn("OwnerType").Ascending()
                    .OnColumn("OwnerId").Ascending();

                Create.Index("IX_Documents_CategoryId")
                    .OnTable("Documents")
                    .OnColumn("CategoryId").Ascending();

                Create.Index("IX_Documents_AccessLevel")
                    .OnTable("Documents")
                    .OnColumn("AccessLevel").Ascending();

                Create.ForeignKey("FK_Documents_CategoryId")
                    .FromTable("Documents").ForeignColumn("CategoryId")
                    .ToTable("DocumentCategories").PrimaryColumn("Id");
            }

            // Create DocumentStorageQuotas table
            if (!TableExists("DocumentStorageQuotas"))
            {
                Create.Table("DocumentStorageQuotas")
                    .WithColumn("Id").AsInt32().PrimaryKey("PK_DocumentStorageQuotas").Identity()
                    .WithColumn("OwnerType").AsInt32().NotNullable()
                    .WithColumn("OwnerId").AsInt32().NotNullable()
                    .WithColumn("StorageLimitMB").AsInt32().NotNullable()
                    .Do();

                Create.Index("IX_DocumentStorageQuotas_Owner")
                    .OnTable("DocumentStorageQuotas")
                    .OnColumn("OwnerType").Ascending()
                    .OnColumn("OwnerId").Ascending()
                    .WithOptions().Unique();
            }

            await Task.CompletedTask;
        }
    }
}
