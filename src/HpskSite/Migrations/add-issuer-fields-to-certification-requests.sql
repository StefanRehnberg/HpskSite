-- Adds the off-platform issuer + real cert-attribute columns to an EXISTING
-- CertificationRequests table — for environments where create-certification-requests-table.sql
-- was run before these columns were added (2026-06-04). Fresh installs get them from the
-- create script directly and do NOT need this. Idempotent — safe to re-run. Run in SSMS.
--
-- The three NOT NULL columns are added with a temporary DEFAULT so the ALTER succeeds even if
-- rows already exist; the defaults are then dropped so the schema matches a fresh create (the
-- app always supplies these values on insert).

IF COL_LENGTH('dbo.CertificationRequests', 'IssuerName') IS NULL
    ALTER TABLE dbo.CertificationRequests
        ADD [IssuerName] NVARCHAR(200) NOT NULL CONSTRAINT DF_CertReq_IssuerName DEFAULT '';
GO
-- IssuerPistolkortnummer is OPTIONAL (old certs predate Pistolkort numbers).
IF COL_LENGTH('dbo.CertificationRequests', 'IssuerPistolkortnummer') IS NULL
    ALTER TABLE dbo.CertificationRequests ADD [IssuerPistolkortnummer] NVARCHAR(50) NULL;
GO
-- If an earlier version of this script created it NOT NULL, relax it to NULL.
IF OBJECT_ID('DF_CertReq_IssuerPistol', 'D') IS NOT NULL
    ALTER TABLE dbo.CertificationRequests DROP CONSTRAINT DF_CertReq_IssuerPistol;
GO
IF EXISTS (SELECT 1 FROM sys.columns
           WHERE object_id = OBJECT_ID('dbo.CertificationRequests')
             AND name = 'IssuerPistolkortnummer' AND is_nullable = 0)
    ALTER TABLE dbo.CertificationRequests ALTER COLUMN [IssuerPistolkortnummer] NVARCHAR(50) NULL;
GO
IF COL_LENGTH('dbo.CertificationRequests', 'CertifiedAt') IS NULL
    ALTER TABLE dbo.CertificationRequests
        ADD [CertifiedAt] DATETIME NOT NULL CONSTRAINT DF_CertReq_CertifiedAt DEFAULT GETUTCDATE();
GO
IF COL_LENGTH('dbo.CertificationRequests', 'ExpiresAt') IS NULL
    ALTER TABLE dbo.CertificationRequests ADD [ExpiresAt] DATETIME NULL;
GO
IF COL_LENGTH('dbo.CertificationRequests', 'CertificateNumber') IS NULL
    ALTER TABLE dbo.CertificationRequests ADD [CertificateNumber] NVARCHAR(100) NULL;
GO

-- Drop the temporary defaults (no-op if already dropped / never created).
IF OBJECT_ID('DF_CertReq_IssuerName', 'D') IS NOT NULL
    ALTER TABLE dbo.CertificationRequests DROP CONSTRAINT DF_CertReq_IssuerName;
IF OBJECT_ID('DF_CertReq_CertifiedAt', 'D') IS NOT NULL
    ALTER TABLE dbo.CertificationRequests DROP CONSTRAINT DF_CertReq_CertifiedAt;
GO
