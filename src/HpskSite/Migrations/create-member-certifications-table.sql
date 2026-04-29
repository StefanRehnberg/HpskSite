-- MemberCertifications: certification-based roles registered with Svenska Pistolskytteförbundet.
-- The cert is the personal credential and follows the person across club/region moves.
-- The "appointment" that grants authority within a specific scope (Föreningsinstruktör for
-- club X, Kretsinstruktör for region Y, Riksinstruktör for area Z) is held in the existing
-- Umbraco member-group system: Foreningsinstruktor_{clubId}, Kretsinstruktor_{regionCode},
-- Riksinstruktor_{areaCode}, Vapenkontrollant, Banlaggare.
--
-- Run manually in SSMS against the Umbraco database. No Umbraco composer/plan migration
-- (the project's migration plan path has historically been unreliable per CLAUDE.md).

IF OBJECT_ID('dbo.MemberCertifications', 'U') IS NULL
BEGIN
    CREATE TABLE [dbo].[MemberCertifications] (
        [Id]                  INT IDENTITY(1,1) PRIMARY KEY,
        [MemberId]            INT          NOT NULL,
        [CertificationType]   NVARCHAR(50) NOT NULL,
        [CertifiedByMemberId] INT          NULL,
        [CertifiedAt]         DATETIME     NOT NULL,
        [ExpiresAt]           DATETIME     NULL,
        [CertificateNumber]   NVARCHAR(100) NULL,
        [IsActive]            BIT          NOT NULL CONSTRAINT DF_MemberCertifications_IsActive DEFAULT 1,
        [RevokedAt]           DATETIME     NULL,
        [RevokedByMemberId]   INT          NULL,
        [RevokedReason]       NVARCHAR(500) NULL,
        [Notes]               NVARCHAR(MAX) NULL,
        [CreatedAt]           DATETIME     NOT NULL CONSTRAINT DF_MemberCertifications_CreatedAt DEFAULT GETDATE()
    );

    CREATE INDEX IX_MemberCertifications_MemberId
        ON [dbo].[MemberCertifications] (MemberId, IsActive);

    CREATE INDEX IX_MemberCertifications_TypeActive
        ON [dbo].[MemberCertifications] (CertificationType, IsActive);

    CREATE INDEX IX_MemberCertifications_ExpiresAt
        ON [dbo].[MemberCertifications] (ExpiresAt) WHERE ExpiresAt IS NOT NULL;
END
GO
