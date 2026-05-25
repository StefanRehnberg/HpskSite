-- Add RequestedApproverMemberId to FaltskytteConfiguration.
--
-- The owner picks a specific Banläggare when they ask for approval — the request
-- is targeted (with an email notification), not a broadcast that anyone can pick up.
-- Cleared when status returns to Draft.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-05-25

IF COL_LENGTH('FaltskytteConfiguration', 'RequestedApproverMemberId') IS NULL
BEGIN
    ALTER TABLE [FaltskytteConfiguration] ADD [RequestedApproverMemberId] INT NULL;
    PRINT 'Added RequestedApproverMemberId column';
END
