-- InvoicePaymentEvents: append-only audit log of state transitions on
-- registrationInvoice content nodes. Captures who did what, when, with which
-- payment method / amount / reference, so the per-invoice "notes" field stops
-- being the only audit trail (it gets overwritten on each status update).
--
-- Used by:
--   - InvoiceAuditService (single writer + reader)
--   - "Visa historik" modal on the per-competition Anmälningar tab
--   - "Skicka påminnelser" bulk action (one EmailSent event per recipient)
--   - Future Bokföringsunderlag (transaction list)
--
-- Run manually in SSMS against the Umbraco database.

IF OBJECT_ID('dbo.InvoicePaymentEvents', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[InvoicePaymentEvents] (
        [Id]                INT IDENTITY(1,1) PRIMARY KEY,
        [InvoiceId]         INT           NOT NULL,    -- Umbraco content node id of the registrationInvoice
        [CompetitionId]     INT           NOT NULL,    -- Denormalised for fast competition-wide queries
        [EventType]         NVARCHAR(40)  NOT NULL,    -- Created | MarkedPaid | Cancelled | Refunded | EmailSent | StatusChanged
        [OccurredAt]        DATETIME      NOT NULL CONSTRAINT DF_InvoicePaymentEvents_OccurredAt DEFAULT GETUTCDATE(),
        [ByMemberId]        INT           NULL,        -- Null for system-triggered events
        [ByMemberName]      NVARCHAR(200) NULL,        -- Denormalised so history rows render even after a member is deleted
        [PaymentMethod]     NVARCHAR(40)  NULL,        -- Swish | Kontant | Bankgiro | Annat (only on payment-related events)
        [Amount]            DECIMAL(18,2) NULL,        -- Recorded amount at the time of the event
        [Reference]         NVARCHAR(100) NULL,        -- Transaction id, invoice number, or other lookup key
        [Notes]             NVARCHAR(MAX) NULL         -- Free-text context (e.g. "Bulk reminder — 3 dagar före tävling")
    );

    -- Most common query: "show me the history of this single invoice"
    CREATE INDEX IX_InvoicePaymentEvents_Invoice
        ON [dbo].[InvoicePaymentEvents] (InvoiceId, OccurredAt DESC);

    -- Competition-wide queries (Bokföringsunderlag, "how many reminders went out")
    CREATE INDEX IX_InvoicePaymentEvents_Competition
        ON [dbo].[InvoicePaymentEvents] (CompetitionId, OccurredAt DESC);
END
GO
