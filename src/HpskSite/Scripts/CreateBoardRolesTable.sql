-- BoardRoles table for club and region organizational positions
-- OwnerType: 0=Club, 1=Region (matches DocumentOwnerType)
-- RoleKey: predefined key (e.g. 'Ordforande') or 'Custom' for free-text roles
-- IsBoardMember: controls visibility in Styrelsen tab (true=board, false=other responsibility)

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'BoardRoles')
BEGIN
    CREATE TABLE BoardRoles (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        OwnerType INT NOT NULL,              -- 0=Club, 1=Region
        OwnerId INT NOT NULL,                -- Umbraco content node ID
        MemberId INT NOT NULL,               -- Umbraco member ID
        RoleKey NVARCHAR(50) NOT NULL,       -- predefined key or 'Custom'
        CustomTitle NVARCHAR(100) NULL,      -- free text when RoleKey='Custom'
        IsBoardMember BIT NOT NULL DEFAULT 1,-- true=shows in Styrelsen tab
        SortOrder INT NOT NULL DEFAULT 0,
        AssignedDate DATETIME NOT NULL DEFAULT GETDATE(),
        AssignedByMemberId INT NULL,
        IsActive BIT NOT NULL DEFAULT 1,
        CONSTRAINT UQ_BoardRoles_OwnerMemberRole
            UNIQUE (OwnerType, OwnerId, MemberId, RoleKey, CustomTitle)
    );

    CREATE INDEX IX_BoardRoles_Owner ON BoardRoles(OwnerType, OwnerId);
    CREATE INDEX IX_BoardRoles_MemberId ON BoardRoles(MemberId);

    PRINT 'BoardRoles table created successfully.';
END
ELSE
BEGIN
    PRINT 'BoardRoles table already exists.';
END
