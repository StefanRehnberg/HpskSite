-- Fältskytte (Field Shooting) tables
-- Run manually in SSMS against the Umbraco database
-- 2026-04-01

-- Result entry: one row per shooter per station
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FaltskytteResultEntry')
BEGIN
    CREATE TABLE FaltskytteResultEntry (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        CompetitionId INT NOT NULL,
        StationNumber INT NOT NULL,
        MemberId INT NOT NULL,
        PatrolNumber INT NOT NULL,
        ShootingClass NVARCHAR(20) NOT NULL,
        Hits INT NOT NULL DEFAULT 0,
        Figures INT NOT NULL DEFAULT 0,
        HitDistribution NVARCHAR(100) NULL,
        TiebreakerScore INT NULL,
        Reshoots INT NOT NULL DEFAULT 0,
        EnteredBy INT NOT NULL,
        EnteredAt DATETIME2 NOT NULL DEFAULT GETUTCDATE(),
        LastModified DATETIME2 NOT NULL DEFAULT GETUTCDATE(),

        CONSTRAINT UQ_Faltskytte_Result UNIQUE (CompetitionId, StationNumber, MemberId)
    );

    CREATE INDEX IX_FaltskytteResult_Competition ON FaltskytteResultEntry (CompetitionId);
    CREATE INDEX IX_FaltskytteResult_Member ON FaltskytteResultEntry (CompetitionId, MemberId);
END
GO

-- Patrol definitions
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FaltskyttePatrol')
BEGIN
    CREATE TABLE FaltskyttePatrol (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        CompetitionId INT NOT NULL,
        PatrolNumber INT NOT NULL,
        StartTime DATETIME2 NULL,
        WeaponGroup NVARCHAR(50) NULL,
        CurrentStation INT NULL,

        CONSTRAINT UQ_Faltskytte_Patrol UNIQUE (CompetitionId, PatrolNumber)
    );

    CREATE INDEX IX_FaltskyttePatrol_Competition ON FaltskyttePatrol (CompetitionId);
END
GO

-- Patrol members
IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FaltskyttePatrolMember')
BEGIN
    CREATE TABLE FaltskyttePatrolMember (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        PatrolId INT NOT NULL,
        MemberId INT NOT NULL,
        Position INT NOT NULL,
        ShootingClass NVARCHAR(20) NOT NULL,
        MemberName NVARCHAR(200) NOT NULL,
        ClubName NVARCHAR(200) NOT NULL,

        CONSTRAINT FK_FaltskyttePatrolMember_Patrol
            FOREIGN KEY (PatrolId) REFERENCES FaltskyttePatrol(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_FaltskyttePatrolMember_Patrol ON FaltskyttePatrolMember (PatrolId);
END
GO
