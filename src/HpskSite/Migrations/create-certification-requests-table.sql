-- CertificationRequests: bootstrap path for recording SPSF certifications when the
-- issuing instructor (usually a Kretsinstruktör) is not on pistol.nu and therefore not
-- selectable as grantor in the "Tilldela certifiering" modal.
--
-- A club admin submits a request for a member (candidate). The request carries the
-- candidate's SPSF identity (full name, email, Pistolkortnummer) so an approver can verify
-- the person against the SPSF registry. Either a regional admin for the candidate's region
-- OR a site admin approves; approval is what actually issues the cert (and flips the
-- functional member group), so nothing functional is granted while a request is Pending.
--
-- Run manually in SSMS against the Umbraco database. No Umbraco composer/plan migration
-- (the project's migration plan path has historically been unreliable per CLAUDE.md).

IF OBJECT_ID('dbo.CertificationRequests', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[CertificationRequests] (
        [Id]                  INT IDENTITY(1,1) PRIMARY KEY,
        [CandidateMemberId]   INT           NOT NULL,
        [CertificationType]   NVARCHAR(50)  NOT NULL,
        [ClubId]              INT           NOT NULL,
        [CandidateFullName]   NVARCHAR(200) NOT NULL,
        [CandidateEmail]      NVARCHAR(256) NULL,
        [Pistolkortnummer]    NVARCHAR(50)  NOT NULL,   -- candidate's (the shooter being certified)
        [IssuerName]          NVARCHAR(200) NOT NULL,   -- off-platform issuer (utfärdaren), free text
        [IssuerPistolkortnummer] NVARCHAR(50) NULL,     -- off-platform issuer's; optional (old certs predate them)
        [CertifiedAt]         DATETIME      NOT NULL,   -- the real date the cert was issued
        [ExpiresAt]           DATETIME      NULL,
        [CertificateNumber]   NVARCHAR(100) NULL,       -- actual SPSF certificate number, if known
        [RequestedByMemberId] INT           NOT NULL,
        [RequestedAt]         DATETIME      NOT NULL CONSTRAINT DF_CertificationRequests_RequestedAt DEFAULT GETUTCDATE(),
        [RequestNote]         NVARCHAR(MAX) NULL,
        [Status]              NVARCHAR(20)  NOT NULL CONSTRAINT DF_CertificationRequests_Status DEFAULT 'Pending',
        [ReviewedByMemberId]  INT           NULL,
        [ReviewedAt]          DATETIME      NULL,
        [ReviewNote]          NVARCHAR(500) NULL,
        [CreatedAt]           DATETIME      NOT NULL CONSTRAINT DF_CertificationRequests_CreatedAt DEFAULT GETDATE()
    );

    -- Approver queue: pending requests scoped by club (→ region) for a regional/site admin.
    CREATE INDEX IX_CertificationRequests_StatusClub
        ON [dbo].[CertificationRequests] (Status, ClubId);

    -- "My club's requests" read-only list + duplicate-pending guard.
    CREATE INDEX IX_CertificationRequests_ClubCandidate
        ON [dbo].[CertificationRequests] (ClubId, CandidateMemberId, CertificationType);
END
GO
