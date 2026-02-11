-- Document Archive Tables
-- Run this script in SSMS against the Umbraco database

-- 1. DocumentCategories
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DocumentCategories')
BEGIN
    CREATE TABLE [dbo].[DocumentCategories] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [Name] NVARCHAR(200) NOT NULL,
        [Description] NVARCHAR(1000) NULL,
        [OwnerType] INT NOT NULL,          -- 0=Club, 1=Region
        [OwnerId] INT NOT NULL,
        [SortOrder] INT NOT NULL DEFAULT 0,
        [ShowInQuickLinks] BIT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME NOT NULL,
        [CreatedBy] INT NOT NULL,
        CONSTRAINT [PK_DocumentCategories] PRIMARY KEY CLUSTERED ([Id])
    );

    CREATE NONCLUSTERED INDEX [IX_DocumentCategories_Owner]
        ON [dbo].[DocumentCategories] ([OwnerType], [OwnerId]);

    PRINT 'Created DocumentCategories table';
END
ELSE
    PRINT 'DocumentCategories table already exists';
GO

-- 2. Documents
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'Documents')
BEGIN
    CREATE TABLE [dbo].[Documents] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [Title] NVARCHAR(300) NOT NULL,
        [Description] NVARCHAR(2000) NULL,
        [CategoryId] INT NULL,
        [OwnerType] INT NOT NULL,
        [OwnerId] INT NOT NULL,
        [FileName] NVARCHAR(500) NOT NULL,
        [StoredFileName] NVARCHAR(500) NOT NULL,
        [ContentType] NVARCHAR(200) NOT NULL,
        [FileSize] BIGINT NOT NULL,
        [AccessLevel] INT NOT NULL DEFAULT 0,
        [SortOrder] INT NOT NULL DEFAULT 0,
        [ShowInQuickLinks] BIT NOT NULL DEFAULT 0,
        [DownloadCount] INT NOT NULL DEFAULT 0,
        [CreatedAt] DATETIME NOT NULL,
        [CreatedBy] INT NOT NULL,
        [UpdatedAt] DATETIME NOT NULL,
        CONSTRAINT [PK_Documents] PRIMARY KEY CLUSTERED ([Id]),
        CONSTRAINT [FK_Documents_CategoryId] FOREIGN KEY ([CategoryId])
            REFERENCES [dbo].[DocumentCategories] ([Id])
    );

    CREATE NONCLUSTERED INDEX [IX_Documents_Owner]
        ON [dbo].[Documents] ([OwnerType], [OwnerId]);

    CREATE NONCLUSTERED INDEX [IX_Documents_CategoryId]
        ON [dbo].[Documents] ([CategoryId]);

    CREATE NONCLUSTERED INDEX [IX_Documents_AccessLevel]
        ON [dbo].[Documents] ([AccessLevel]);

    PRINT 'Created Documents table';
END
ELSE
    PRINT 'Documents table already exists';
GO

-- 3. DocumentStorageQuotas
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'DocumentStorageQuotas')
BEGIN
    CREATE TABLE [dbo].[DocumentStorageQuotas] (
        [Id] INT IDENTITY(1,1) NOT NULL,
        [OwnerType] INT NOT NULL,
        [OwnerId] INT NOT NULL,
        [StorageLimitMB] INT NOT NULL,
        CONSTRAINT [PK_DocumentStorageQuotas] PRIMARY KEY CLUSTERED ([Id])
    );

    CREATE UNIQUE NONCLUSTERED INDEX [IX_DocumentStorageQuotas_Owner]
        ON [dbo].[DocumentStorageQuotas] ([OwnerType], [OwnerId]);

    PRINT 'Created DocumentStorageQuotas table';
END
ELSE
    PRINT 'DocumentStorageQuotas table already exists';
GO

PRINT 'Document Archive tables setup complete!';
