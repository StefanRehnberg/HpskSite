-- Add approval workflow columns to FaltskytteConfiguration.
--
-- Approval is granted by a member who holds the active Banläggare cert.
-- States ('Draft' default for legacy rows):
--   Draft              — being built, fully editable by owner + collaborators
--   PendingApproval    — owner has requested Banläggare review
--   Approved           — locked for configuration-data edits; metadata still editable
--
-- ApprovedByMemberId + ApprovedDate are populated only in Approved state.
-- A successful Unapprove transition (back to Draft) clears them.
--
-- Run manually in SSMS against the Umbraco database.
-- Date: 2026-05-25

IF COL_LENGTH('FaltskytteConfiguration', 'ApprovalStatus') IS NULL
BEGIN
    ALTER TABLE [FaltskytteConfiguration] ADD [ApprovalStatus] NVARCHAR(20) NULL;
    PRINT 'Added ApprovalStatus column';
END

IF COL_LENGTH('FaltskytteConfiguration', 'ApprovedByMemberId') IS NULL
BEGIN
    ALTER TABLE [FaltskytteConfiguration] ADD [ApprovedByMemberId] INT NULL;
    PRINT 'Added ApprovedByMemberId column';
END

IF COL_LENGTH('FaltskytteConfiguration', 'ApprovedDate') IS NULL
BEGIN
    ALTER TABLE [FaltskytteConfiguration] ADD [ApprovedDate] DATETIME NULL;
    PRINT 'Added ApprovedDate column';
END

-- Lookup index for the "Väntar på godkännande" Banläggare queue view.
IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('FaltskytteConfiguration') AND name = 'IX_FaltskytteConfiguration_ApprovalStatus'
)
BEGIN
    CREATE NONCLUSTERED INDEX [IX_FaltskytteConfiguration_ApprovalStatus]
    ON [FaltskytteConfiguration] ([ApprovalStatus]) WHERE [ApprovalStatus] IS NOT NULL;
    PRINT 'Added IX_FaltskytteConfiguration_ApprovalStatus';
END
