-- Shooting Range Database — Phase 0 schema (Skjutbanedatabas).
-- See Documentation/SHOOTING_RANGE_DATABASE.md.
--
-- Two-level model driven by the regulation (the skjutbana/facility is the permit-bearing,
-- inspected unit for BOTH the Police and the miljöförvaltning; individual banor/skjutvallar are
-- child configuration, not separately permitted):
--   ShootingRange       — the facility (permit-bearing; permits + activity ledger attach here in later phases)
--   RangeSection        — a bana / "vall" / skjutplats within the facility (config + kulfång detail)
--   ClubRangeLink       — many-to-many: which clubs use/own a range (informational; NOT access)
--   ClubRangeAllocation — each club's day/time slots within the range's allowed window
--   RangeSteward        — ACL: who may see/edit a range's private data (claiming creates the first row)
--
-- Ranges are deliberately decoupled from clubs (a range may be shared by several clubs, owned by
-- one+ of them, or owned by a 3rd party not on pistol.nu). Access is by stewardship, never inherited
-- from club-admin. Site admins always have access.
--
-- Run manually in SSMS against the Umbraco database (project convention — no migration composer).

-- ── ShootingRange (Skjutbana) ──────────────────────────────────────────────
IF OBJECT_ID('dbo.ShootingRange', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ShootingRange] (
        [Id]                   INT IDENTITY(1,1) PRIMARY KEY,
        [Name]                 NVARCHAR(200) NOT NULL,
        [Latitude]             FLOAT         NULL,
        [Longitude]            FLOAT         NULL,
        [Address]              NVARCHAR(200) NULL,
        [Postcode]             NVARCHAR(20)  NULL,
        [City]                 NVARCHAR(100) NULL,
        [Municipality]         NVARCHAR(100) NULL,   -- kommun (key for the FOIA/municipal data track)
        [County]               NVARCHAR(100) NULL,   -- län
        [LocationSensitivity]  NVARCHAR(20)  NOT NULL CONSTRAINT DF_ShootingRange_Sens   DEFAULT 'Members',       -- 'Members' | 'Restricted'
        [HuvudmanType]         NVARCHAR(20)  NULL,   -- 'ClubOnPlatform' | 'ExternalParty' | 'Municipality' | 'Private' | 'Federation'
        [HuvudmanClubId]       INT           NULL,   -- Umbraco club node id when ClubOnPlatform
        [HuvudmanName]         NVARCHAR(200) NULL,   -- free text when owner is off-platform
        [SkjutbanechefName]    NVARCHAR(200) NULL,
        [SkjutbanechefContact] NVARCHAR(200) NULL,
        [Description]          NVARCHAR(MAX) NULL,
        [Status]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_ShootingRange_Status DEFAULT 'UnclaimedSeed',  -- 'Active' | 'Inactive' | 'Decommissioned' | 'UnclaimedSeed'
        [Source]               NVARCHAR(20)  NOT NULL CONSTRAINT DF_ShootingRange_Source DEFAULT 'Manual',         -- 'Osm' | 'Manual' | 'Municipal' | 'Claimed'
        [OsmRef]               NVARCHAR(60)  NULL,   -- back-link to the OSM element id for dedup
        [CreatedByMemberId]    INT           NULL,
        [CreatedAt]            DATETIME      NOT NULL CONSTRAINT DF_ShootingRange_Created DEFAULT GETDATE(),
        [UpdatedAt]            DATETIME      NOT NULL CONSTRAINT DF_ShootingRange_Updated DEFAULT GETDATE()
    );

    CREATE INDEX IX_ShootingRange_Municipality ON [dbo].[ShootingRange] (Municipality);
    CREATE INDEX IX_ShootingRange_Status       ON [dbo].[ShootingRange] (Status);
    -- One seed per OSM element (filtered unique — manual/claimed rows have NULL OsmRef).
    CREATE UNIQUE INDEX UX_ShootingRange_OsmRef ON [dbo].[ShootingRange] (OsmRef) WHERE OsmRef IS NOT NULL;
END
GO

-- ── RangeSection (Bana / "vall" / skjutplats) ──────────────────────────────
IF OBJECT_ID('dbo.RangeSection', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RangeSection] (
        [Id]                     INT IDENTITY(1,1) PRIMARY KEY,
        [RangeId]                INT           NOT NULL,
        [Label]                  NVARCHAR(120) NOT NULL,   -- e.g. "25 m pistol", "300 m gevär", "Vall A"
        [BanaType]               NVARCHAR(60)  NULL,       -- Pistol 25/50 m, Gevär 50-300 m, Viltmål, Lerduva …
        [DistanceMeters]         INT           NULL,
        [DirectionDegrees]       INT           NULL,       -- skjutriktning
        [FiringPoints]           INT           NULL,       -- antal skjutplatser
        [KulfangSpec]            NVARCHAR(MAX) NULL,        -- slope/material/height/width (free text v1)
        [AllowedWeaponsCalibers] NVARCHAR(MAX) NULL,
        [Notes]                  NVARCHAR(MAX) NULL,
        [SortOrder]              INT           NOT NULL CONSTRAINT DF_RangeSection_Sort DEFAULT 0,
        [CreatedAt]              DATETIME      NOT NULL CONSTRAINT DF_RangeSection_Created DEFAULT GETDATE(),
        [UpdatedAt]              DATETIME      NOT NULL CONSTRAINT DF_RangeSection_Updated DEFAULT GETDATE()
    );
    CREATE INDEX IX_RangeSection_RangeId ON [dbo].[RangeSection] (RangeId);
END
GO

-- ── ClubRangeLink (m:n — which clubs use/own a range) ──────────────────────
IF OBJECT_ID('dbo.ClubRangeLink', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ClubRangeLink] (
        [Id]              INT IDENTITY(1,1) PRIMARY KEY,
        [RangeId]         INT          NOT NULL,
        [ClubId]          INT          NOT NULL,    -- Umbraco club node id
        [RelationType]    NVARCHAR(20) NOT NULL CONSTRAINT DF_ClubRangeLink_Rel DEFAULT 'User',  -- 'Owner' | 'PrimaryUser' | 'User' | 'Tenant'
        [AddedByMemberId] INT          NULL,
        [AddedAt]         DATETIME     NOT NULL CONSTRAINT DF_ClubRangeLink_Added DEFAULT GETDATE()
    );
    CREATE UNIQUE INDEX UX_ClubRangeLink ON [dbo].[ClubRangeLink] (RangeId, ClubId);
END
GO

-- ── ClubRangeAllocation (per-club day/time slots within the range envelope) ─
IF OBJECT_ID('dbo.ClubRangeAllocation', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[ClubRangeAllocation] (
        [Id]                INT IDENTITY(1,1) PRIMARY KEY,
        [ClubRangeLinkId]   INT          NOT NULL,    -- the (range, club) pairing this slot belongs to
        [RangeSectionId]    INT          NULL,        -- optional — slot limited to a specific bana/section
        [DayOfWeek]         TINYINT      NOT NULL,     -- ISO-8601: 1=Mon … 7=Sun
        [StartTime]         TIME(0)      NOT NULL,
        [EndTime]           TIME(0)      NOT NULL,
        [ValidFrom]         DATE         NULL,         -- optional seasonal/temporary allocation
        [ValidTo]           DATE         NULL,
        [Note]              NVARCHAR(200) NULL,
        [CreatedByMemberId] INT          NULL,
        [CreatedAt]         DATETIME     NOT NULL CONSTRAINT DF_ClubRangeAllocation_Created DEFAULT GETDATE()
    );
    CREATE INDEX IX_ClubRangeAllocation_Link ON [dbo].[ClubRangeAllocation] (ClubRangeLinkId);
END
GO

-- ── RangeSteward (ACL — who may see/edit a range's private data) ───────────
IF OBJECT_ID('dbo.RangeSteward', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RangeSteward] (
        [Id]                INT IDENTITY(1,1) PRIMARY KEY,
        [RangeId]           INT      NOT NULL,
        [MemberId]          INT      NOT NULL,
        [GrantedByMemberId] INT      NULL,
        [GrantedAt]         DATETIME NOT NULL CONSTRAINT DF_RangeSteward_Granted DEFAULT GETDATE()
    );
    CREATE UNIQUE INDEX UX_RangeSteward ON [dbo].[RangeSteward] (RangeId, MemberId);
    CREATE INDEX IX_RangeSteward_Member ON [dbo].[RangeSteward] (MemberId);
END
GO
