-- Shooting Range Database — Phase 3 schema (activity ledger).
-- See Documentation/SHOOTING_RANGE_DATABASE.md §3.7 + §4.
--
--   RangeActivitySession — one logged shooting session at a range. The annual sum of ShotCount per
--   range is the figure reported against the environmental permit's MaxShotsPerYear; distinct Dates =
--   shooting-days; StartTime = time-of-day distribution (noise compliance).
--
--   Layered provenance (ShotCountSource): pistol.nu can only auto-capture its own activity, so the
--   total is a FLOOR — the steward tops it up with ManualBulk rows for off-platform activity:
--     'Competition'    — exact, from a competition linked to the range
--     'TrainingLog'    — from a member's logged TrainingScores attributed to the range
--     'QrSelfReported' — QR check-in/out, shots entered at checkout
--     'ManualBulk'     — steward enters off-platform activity (guest shooters, other clubs, events)
--     'Estimated'      — explicit estimate
--
-- Run manually in SSMS.

IF OBJECT_ID('dbo.RangeActivitySession', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[RangeActivitySession] (
        [Id]                    INT IDENTITY(1,1) PRIMARY KEY,
        [RangeId]               INT           NOT NULL,
        [RangeSectionId]        INT           NULL,
        [MemberId]              INT           NULL,   -- null for ManualBulk / guest entries
        [ClubId]                INT           NULL,   -- which club's activity (multi-club attribution)
        [Date]                  DATE          NOT NULL,
        [StartTime]             TIME(0)       NULL,
        [EndTime]               TIME(0)       NULL,   -- null = open (checked in, not out)
        [ShotCount]             INT           NOT NULL CONSTRAINT DF_RangeActivity_Shots    DEFAULT 0,
        [ShotCountSource]       NVARCHAR(20)  NOT NULL,    -- see ShotSource* constants
        [ShooterCount]          INT           NOT NULL CONSTRAINT DF_RangeActivity_Shooters DEFAULT 1,
        [LinkedCompetitionId]   INT           NULL,
        [LinkedTrainingScoreId] INT           NULL,
        [EnteredByMemberId]     INT           NULL,
        [Note]                  NVARCHAR(400) NULL,
        [CreatedAt]             DATETIME      NOT NULL CONSTRAINT DF_RangeActivity_Created  DEFAULT GETDATE()
    );
    CREATE INDEX IX_RangeActivity_RangeDate ON [dbo].[RangeActivitySession] (RangeId, [Date]);
    -- Fast open-session lookup for QR check-out (the member's currently-open session at a range).
    CREATE INDEX IX_RangeActivity_Open      ON [dbo].[RangeActivitySession] (RangeId, MemberId, EndTime);
END
GO
