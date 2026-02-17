-- TrainingGroups: one row per training group
CREATE TABLE TrainingGroups (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(200) NOT NULL,
    ClubId INT NOT NULL,
    Description NVARCHAR(1000) NULL,
    StartDate DATETIME NOT NULL,
    IsActive BIT NOT NULL DEFAULT 1,
    CreatedDate DATETIME NOT NULL DEFAULT GETDATE(),
    CreatedByMemberId INT NOT NULL
);

CREATE INDEX IX_TrainingGroups_ClubId ON TrainingGroups(ClubId);
CREATE INDEX IX_TrainingGroups_IsActive ON TrainingGroups(IsActive);

-- TrainingGroupMembers: trainers and members in each training group
CREATE TABLE TrainingGroupMembers (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    TrainingGroupId INT NOT NULL,
    MemberId INT NOT NULL,
    Role NVARCHAR(20) NOT NULL DEFAULT 'Member',  -- 'Trainer' or 'Member'
    JoinedDate DATETIME NOT NULL DEFAULT GETDATE(),
    AddedByMemberId INT NULL,
    IsActive BIT NOT NULL DEFAULT 1,

    CONSTRAINT FK_TrainingGroupMembers_TrainingGroups
        FOREIGN KEY (TrainingGroupId) REFERENCES TrainingGroups(Id)
        ON DELETE CASCADE,

    CONSTRAINT UQ_TrainingGroupMembers_GroupMember
        UNIQUE (TrainingGroupId, MemberId)
);

CREATE INDEX IX_TrainingGroupMembers_MemberId ON TrainingGroupMembers(MemberId);
CREATE INDEX IX_TrainingGroupMembers_TrainingGroupId ON TrainingGroupMembers(TrainingGroupId);
CREATE INDEX IX_TrainingGroupMembers_Role ON TrainingGroupMembers(Role);
