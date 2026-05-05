-- RefreshTokens: JWT refresh-token storage for the mobile API.
-- The DTO mapping (Models/RefreshToken.cs → RefreshTokenDto) is the source of truth
-- for the column shapes; this script builds the matching table.
--
-- Replaces RefreshTokensMigrationPlan / RefreshTokensMigrationComposer +
-- CreateRefreshTokensTable.cs / RecreateRefreshTokensTable.cs / FixRefreshTokensNullableColumns.cs.
-- Those three migrations chained as Create → FixNullable → Recreate; this script writes
-- the final state directly.
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.RefreshTokens', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RefreshTokens] (
        [Id]              INT IDENTITY(1,1) NOT NULL CONSTRAINT PK_RefreshTokens PRIMARY KEY,
        [MemberId]        INT           NOT NULL,
        [Token]           NVARCHAR(500) NOT NULL,
        [ExpiresAt]       DATETIME      NOT NULL,
        [CreatedAt]       DATETIME      NOT NULL,
        [RevokedAt]       DATETIME      NULL,
        [CreatedByIp]     NVARCHAR(50)  NULL,
        [RevokedByIp]     NVARCHAR(50)  NULL,
        [ReplacedByToken] NVARCHAR(500) NULL,
        [UserAgent]       NVARCHAR(500) NULL
    );

    CREATE INDEX IX_RefreshTokens_Token    ON [dbo].[RefreshTokens] (Token);
    CREATE INDEX IX_RefreshTokens_MemberId ON [dbo].[RefreshTokens] (MemberId);
END
GO
