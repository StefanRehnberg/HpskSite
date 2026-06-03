-- Shooting Range Database — Phase 2 schema (compliance dossier).
-- See Documentation/SHOOTING_RANGE_DATABASE.md §3.5/§3.6.
--
--   RangePermit   — police tillstånd + environmental anmälan/tillstånd attached to a ShootingRange.
--                   Holds the shot-cap (MaxShotsPerYear) and the allowed shooting window (AllowedWindows
--                   JSON) — the facility-level legal envelope. Expiry drives renewal reminders.
--   RangeDocument — uploaded compliance files (tillstånd, besiktningsprotokoll, skjutbaneinstruktion,
--                   bullerutredning, markundersökning/bly, skötselplan, försäkring …). The file itself
--                   lives under App_Data/range-documents (RangeDocumentStorage); only the ref is stored.
--
-- Both belong to a range; access is steward/site-admin (the private tier). Run manually in SSMS.

-- ── RangePermit ─────────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.RangePermit', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RangePermit] (
        [Id]               INT IDENTITY(1,1) PRIMARY KEY,
        [RangeId]          INT           NOT NULL,
        [PermitType]       NVARCHAR(30)  NOT NULL,   -- 'PoliceTillstand' | 'EnvAnmalanC' | 'EnvTillstandB' | 'Other'
        [IssuingAuthority] NVARCHAR(200) NULL,        -- e.g. "Polismyndigheten region Syd", "Malmö miljöförvaltning"
        [ReferenceNumber]  NVARCHAR(100) NULL,        -- diarienummer
        [IssuedDate]       DATE          NULL,
        [ExpiryDate]       DATE          NULL,        -- police ~5 yr → drives renewal reminders
        [MaxShotsPerYear]  INT           NULL,        -- environmental cap (the primary limit)
        [AllowedWindows]   NVARCHAR(MAX) NULL,        -- JSON array of { day:1-7, start:"HH:mm", end:"HH:mm" }
        [Conditions]       NVARCHAR(MAX) NULL,        -- other restrictions (free text)
        [Status]           NVARCHAR(20)  NOT NULL CONSTRAINT DF_RangePermit_Status DEFAULT 'Active',  -- 'Active' | 'Expired' | 'PendingRenewal'
        [CreatedAt]        DATETIME      NOT NULL CONSTRAINT DF_RangePermit_Created DEFAULT GETDATE(),
        [UpdatedAt]        DATETIME      NOT NULL CONSTRAINT DF_RangePermit_Updated DEFAULT GETDATE()
    );
    CREATE INDEX IX_RangePermit_RangeId ON [dbo].[RangePermit] (RangeId);
END
GO

-- ── RangeDocument ───────────────────────────────────────────────────────────
IF OBJECT_ID('dbo.RangeDocument', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RangeDocument] (
        [Id]                 INT IDENTITY(1,1) PRIMARY KEY,
        [RangeId]            INT           NOT NULL,
        [DocType]            NVARCHAR(40)  NOT NULL,   -- see RangeConstants Doc* constants
        [Title]              NVARCHAR(200) NOT NULL,
        [FileRef]            NVARCHAR(400) NOT NULL,   -- bare stored filename under App_Data/range-documents
        [IssuedDate]         DATE          NULL,
        [ValidUntil]         DATE          NULL,        -- drives renewal reminders
        [UploadedByMemberId] INT           NULL,
        [UploadedAt]         DATETIME      NOT NULL CONSTRAINT DF_RangeDocument_Uploaded DEFAULT GETDATE()
    );
    CREATE INDEX IX_RangeDocument_RangeId ON [dbo].[RangeDocument] (RangeId);
END
GO
