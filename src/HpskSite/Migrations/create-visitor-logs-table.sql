-- VisitorLogs: anonymous page-view log feeding the Statistik tab visitor chart.
-- One row per (anonymous session, path) per ~5 min throttle window.
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.VisitorLogs', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[VisitorLogs] (
        [Id]          BIGINT IDENTITY(1,1) PRIMARY KEY,
        [VisitedAt]   DATETIME      NOT NULL,
        [SessionHash] NVARCHAR(64)  NOT NULL,   -- SHA-256 hex of an opaque session cookie
        [Path]        NVARCHAR(512) NOT NULL
    );

    CREATE INDEX IX_VisitorLogs_VisitedAt
        ON [dbo].[VisitorLogs] (VisitedAt);

    CREATE INDEX IX_VisitorLogs_VisitedAt_SessionHash
        ON [dbo].[VisitorLogs] (VisitedAt, SessionHash);
END
GO
