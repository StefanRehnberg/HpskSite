-- Field Target Catalog tables + seed data
-- Run manually in SSMS against the Umbraco database
-- 2026-04-02

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FieldTarget')
BEGIN
    CREATE TABLE FieldTarget (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Name NVARCHAR(200) NOT NULL,
        MaxDistanceC INT NULL,
        MaxDistanceB INT NULL,
        MaxDistanceA INT NULL,
        MaxDistanceR INT NULL
    );
END
GO

IF NOT EXISTS (SELECT * FROM sys.tables WHERE name = 'FieldTargetVariant')
BEGIN
    CREATE TABLE FieldTargetVariant (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        TargetId INT NOT NULL,
        FullName NVARCHAR(300) NOT NULL,
        ImageName NVARCHAR(300) NOT NULL,
        Color NVARCHAR(50) NOT NULL DEFAULT '',

        CONSTRAINT FK_FieldTargetVariant_Target
            FOREIGN KEY (TargetId) REFERENCES FieldTarget(Id) ON DELETE CASCADE
    );

    CREATE INDEX IX_FieldTargetVariant_Target ON FieldTargetVariant (TargetId);
END
GO

-- Seed data (only if table is empty)
IF NOT EXISTS (SELECT TOP 1 1 FROM FieldTarget)
BEGIN
    DECLARE @tid INT;

    INSERT INTO FieldTarget (Name) VALUES (N'1/3');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/3 blå', N'1-3 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/3 grön', N'1-3 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/3 gul', N'1-3 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/3 orange', N'1-3 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/3 svart', N'1-3 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/4 Höger');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Höger blå', N'1-4 Hoger bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Höger grön', N'1-4 Hoger gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Höger gul', N'1-4 Hoger gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Höger orange', N'1-4 Hoger orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Höger svart', N'1-4 Hoger svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/4 rak');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 rak blå', N'1-4 rak bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 rak grön', N'1-4 rak gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 rak gul', N'1-4 rak gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 rak orange', N'1-4 rak orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 rak svart', N'1-4 rak svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/4 Vänster');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Vänster blå', N'1-4 Vanster bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Vänster grön', N'1-4 Vanster gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Vänster gul', N'1-4 Vanster gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Vänster orange', N'1-4 Vanster orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/4 Vänster svart', N'1-4 Vanster svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/6 Höger');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Höger blå', N'1-6 Hoger bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Höger grön', N'1-6 Hoger gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Höger gul', N'1-6 Hoger gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Höger orange', N'1-6 Hoger orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Höger svart', N'1-6 Hoger svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/6 rak');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 rak blå', N'1-6 rak bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 rak grön', N'1-6 rak gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 rak gul', N'1-6 rak gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 rak orange', N'1-6 rak orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 rak svart', N'1-6 rak svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/6 Vänster');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Vänster blå', N'1-6 Vanster bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Vänster grön', N'1-6 Vanster gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Vänster gul', N'1-6 Vanster gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Vänster orange', N'1-6 Vanster orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/6 Vänster svart', N'1-6 Vanster svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/7');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/7 blå', N'1-7 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/7 grön', N'1-7 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/7 gul', N'1-7 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/7 orange', N'1-7 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/7 svart', N'1-7 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'1/8');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/8 blå', N'1-8 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/8 grön', N'1-8 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/8 gul', N'1-8 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/8 orange', N'1-8 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'1/8 svart', N'1-8 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'B100');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B100 blå', N'B100 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B100 grön', N'B100 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B100 gul', N'B100 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B100 orange', N'B100 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B100 svart', N'B100 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'B45');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B45 blå', N'B45 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B45 grön', N'B45 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B45 gul', N'B45 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B45 orange', N'B45 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B45 svart', N'B45 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'B65');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B65 blå', N'B65 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B65 grön', N'B65 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B65 gul', N'B65 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B65 orange', N'B65 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'B65 svart', N'B65 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Ballongmål');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ballongmål  Grön 78x45 cm,', N'Ballongmal  Gron 78x45 cm.gif', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ballongmål  Gul, 78x45 cm,', N'Ballongmal  Gul 78x45 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ballongmål  röd 78x45 cm', N'Ballongmal  rod 78x45 cm.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ballongmål  Svart 78x45cm,', N'Ballongmal  Svart 78x45cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Bildäck bakifrån 42x61cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Bildäck bakifrån 42x61cm svart', N'Bildack bakifran 42x61cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Bildäck sida 67x34cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Bildäck sida 67x34 cm svart', N'Bildack sida 67x34 cm svart.gif', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Bunkerspringa 50x30cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Bunkerspringa 50x30cm svart figurpapp', N'Bunkerspringa 50x30cm svart figur.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'C20');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C 20 militärgrön', N'C 20 militargron.jpg', N'militärgrön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C20  svart', N'C20  svart.jpg', N'svart');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C20 blå', N'C20 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C20 grön', N'C20 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C20 gul', N'C20 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C20 orange', N'C20 orange.jpg', N'orange');

    INSERT INTO FieldTarget (Name) VALUES (N'C25');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C 25 militärgrön', N'C 25 militargron.jpg', N'militärgrön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C25 blå', N'C25 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C25 grön', N'C25 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C25 gul', N'C25 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C25 orange', N'C25 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C25 svart', N'C25 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'C15 D');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C15 D grön', N'C15 D gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C15 D gul', N'C15 D gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C15 D orange', N'C15 D orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C15 D svart', N'C15 D svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'C30');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C30 blå', N'C30 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C30 grön', N'C30 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C30 gul', N'C30 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C30 orange', N'C30 orange.jpg', N'orange');

    INSERT INTO FieldTarget (Name) VALUES (N'C35');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C35 blå', N'C35 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C35 grön', N'C35 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C35 gul', N'C35 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C35 orange', N'C35 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C35 svart', N'C35 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'C40');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C40 blå', N'C40 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C40 grön', N'C40 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C40 gul', N'C40 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C40 orange', N'C40 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C40 svart', N'C40 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'C50');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C50 blå', N'C50 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C50 grön', N'C50 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C50 gul', N'C50 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C50 orange', N'C50 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'C50 svart', N'C50 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Canaxa Ovalen 17x28cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Canaxa Ovalen 17x28 cm,', N'Canaxa Ovalen 17x28 cm.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Diamanten');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Diamanten blå, 30 x 21 cm', N'Diamanten bla 30 x 21 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Diamanten grön, 30 x 21 cm', N'Diamanten gron 30 x 21 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Diamanten gul, 30 x 21 cm', N'Diamanten gul 30 x 21 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Diamanten orange, 30 x 21 cm', N'Diamanten orange 30 x 21 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Diamanten svart, 30 x 21 cm', N'Diamanten svart 30 x 21 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Flaskmål');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Flaskmål grönt 39x14 cm,', N'Flaskmal gront 39x14 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Flaskmål gul 39x14 cm,', N'Flaskmal gul 39x14 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Flaskmål röd 39x14 cm,', N'Flaskmal rod 39x14 cm.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Flaskmål svart 39x14 cm,', N'Flaskmal svart 39x14 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Helfigur "mini"');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Helfigur "mini"  grön 10-ringad figurpapp', N'Helfigur _mini_  gron 10-ringad figur.jpg', N'grön');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-Oval 1 20cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 1 20cm grön,', N'H-J A-Oval 1 20cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 1 20cm röd,', N'H-J A-Oval 1 20cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 1 20cm svart,', N'H-J A-Oval 1 20cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-Oval 2 26cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 2 26cm svart,', N'H-J A-Oval 2 26cm svart.jpg', N'svart');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 2 26cm grön,', N'H-J A-Oval 2 26cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 2 26cm röd,', N'H-J A-Oval 2 26cm rod.jpg', N'röd');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-Oval 3 39cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 3 39cm grön,', N'H-J A-Oval 3 39cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 3 39cm röd,', N'H-J A-Oval 3 39cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Oval 3 39cm svart,', N'H-J A-Oval 3 39cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-Rondell 1 22cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Rondell 1 22cm  röd,', N'H-J A-Rondell 1 22cm  rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Rondell 1 22cm grön,', N'H-J A-Rondell 1 22cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Rondell 1 22cm svart,', N'H-J A-Rondell 1 22cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-Rondell 2 33cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Rondell 2 33 cm svart,', N'H-J A-Rondell 2 33 cm svart.jpg', N'svart');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Rondell 2 33cm grön,', N'H-J A-Rondell 2 33cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Rondell 2 33cm röd,', N'H-J A-Rondell 2 33cm rod.jpg', N'röd');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-triangel 1 21cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-triangel 1 21cm grön,', N'H-J A-triangel 1 21cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-triangel 1 21cm röd,', N'H-J A-triangel 1 21cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-triangel 1 21cm svart,', N'H-J A-triangel 1 21cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J A-Triangel 2 29cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Triangel 2 29cm grön,', N'H-J A-Triangel 2 29cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Triangel 2 29cm röd,', N'H-J A-Triangel 2 29cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J A-Triangel 2 29cm svart,', N'H-J A-Triangel 2 29cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Oval 1 29cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Oval 1 29cm grön,', N'H-J B-Oval 1 29cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Oval 1 29cm röd,', N'H-J B-Oval 1 29cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Oval 1 29cm svart,', N'H-J B-Oval 1 29cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Oval 2 44cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Oval 2 44cm grön,', N'H-J B-Oval 2 44cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Oval 2 44cm röd,', N'H-J B-Oval 2 44cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Oval 2 44cm svart,', N'H-J B-Oval 2 44cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Rondell 1 12cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 1 12cm grön,', N'H-J B-Rondell 1 12cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 1 12cm röd,', N'H-J B-Rondell 1 12cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 1 12cm svart,', N'H-J B-Rondell 1 12cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Rondell 2 18cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 2 18cm grön,', N'H-J B-Rondell 2 18cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 2 18cm röd,', N'H-J B-Rondell 2 18cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 2 18cm svart,', N'H-J B-Rondell 2 18cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Rondell 3 21cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 3 21cm grön,', N'H-J B-Rondell 3 21cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 3 21cm röd,', N'H-J B-Rondell 3 21cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Rondell 3 21cm svart,', N'H-J B-Rondell 3 21cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Triangel 1 20cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Triangel 1 20cm grön,', N'H-J B-Triangel 1 20cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Triangel 1 20cm röd,', N'H-J B-Triangel 1 20cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Triangel 1 20cm svart,', N'H-J B-Triangel 1 20cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J B-Triangel 2 26cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Triangel 2 26cm grön,', N'H-J B-Triangel 2 26cm gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Triangel 2 26cm röd,', N'H-J B-Triangel 2 26cm rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J B-Triangel 2 26cm svart,', N'H-J B-Triangel 2 26cm svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J Sexkant 1 32cm hög');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 1 32cm hög grön,', N'H-J Sexkant 1 32cm hog gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 1 32cm hög röd,', N'H-J Sexkant 1 32cm hog rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 1 32cm hög svart,', N'H-J Sexkant 1 32cm hog svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J Sexkant 2 46cm hög');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 2 46cm hög grön,', N'H-J Sexkant 2 46cm hog gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 2 46cm hög röd,', N'H-J Sexkant 2 46cm hog rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 2 46cm hög svart,', N'H-J Sexkant 2 46cm hog svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J Sexkant 3 63cm hög');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 3 63cm hög grön,', N'H-J Sexkant 3 63cm hog gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 3 63cm hög röd,', N'H-J Sexkant 3 63cm hog rod.jpg', N'röd');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Sexkant 3 63cm hög svart,', N'H-J Sexkant 3 63cm hog svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'H-J Snabbmål 3 tavlor');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'H-J Snabbmål 3 tavlor,', N'H-J Snabbmal 3 tavlor.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'KorthållsfigurTunnan');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'KorthållsfigurTunnan,', N'KorthallsfigurTunnan.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/3 / T10');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/3 / T10,', N'Korthallsfigur 1-3 - T10.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/4 höger / T7H');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/4 höger / T7H,', N'Korthallsfigur 1-4 hoger - T7H.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/4 rak / T7');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/4 rak / T7,', N'Korthallsfigur 1-4 rak - T7.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/4 vänster / T7V');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/4 vänster / T7V,', N'Korthallsfigur 1-4 vanster - T7V.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/6 höger / T5H');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/6 höger / T5H,', N'Korthallsfigur 1-6 hoger - T5H.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/6 rak / T5');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/6 rak / T5,', N'Korthallsfigur 1-6 rak - T5.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/6 vänster / T5V');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/6 vänster / T5V,', N'Korthallsfigur 1-6 vanster - T5V.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/7 / B5');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/7 / B5,', N'Korthallsfigur 1-7 - B5.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur 1/8 / T4');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur 1/8 / T4,', N'Korthallsfigur 1-8 - T4.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur B100 / B20');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur B100 / B20,', N'Korthallsfigur B100 - B20.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur B45 / B9');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur B45 / B9,', N'Korthallsfigur B45 - B9.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur B65 / B13');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur B65 / B13,', N'Korthallsfigur B65 - B13.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur C20 / C4');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur C20 / C4,', N'Korthallsfigur C20 - C4.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur C25 / C5');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur C25 / C5,', N'Korthallsfigur C25 - C5.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur C30 / C6');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur C30 / C6,', N'Korthallsfigur C30 - C6.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur C35 / C7');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur C35 / C7,', N'Korthallsfigur C35 - C7.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur C40 / C8');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur C40 / C8,', N'Korthallsfigur C40 - C8.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur C50 / C10');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur C50 / C10,', N'Korthallsfigur C50 - C10.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur L 1 / L9');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur L 1 / L9,', N'Korthallsfigur L 1 - L9.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur L 2 / L14');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur L 2 / L14,', N'Korthallsfigur L 2 - L14.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur L 3 / L20');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur L 3 / L20,', N'Korthallsfigur L 3 - L20.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur S20 / S4V/S4H');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur S20 / S4V/S4H,', N'Korthallsfigur S20 - S4V-S4H.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Korthållsfigur S25 / S5V/S5H');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Korthållsfigur S25 / S5V/S5H,', N'Korthallsfigur S25 - S5V-S5H.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Krita vita 12/fp');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Krita vita 12/fp', N'Krita vita 12-fp.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Kvadraten nr 1');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 1 blå, 17 x 17 cm', N'Kvadraten nr 1 bla 17 x 17 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 1 grön, 17 x 17 cm', N'Kvadraten nr 1 gron 17 x 17 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 1 gul, 17 x 17 cm', N'Kvadraten nr 1 gul 17 x 17 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 1 svart, 17 x 17 cm', N'Kvadraten nr 1 svart 17 x 17 cm.jpg', N'svart');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 1orange, 17 x 17 cm', N'Kvadraten nr 1orange 17 x 17 cm.jpg', N'orange');

    INSERT INTO FieldTarget (Name) VALUES (N'Kvadraten nr 2');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 2 blå, 23 x 23 cm', N'Kvadraten nr 2 bla 23 x 23 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 2 grön, 23 x 23 cm', N'Kvadraten nr 2 gron 23 x 23 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 2 gul, 23 x 23 cm', N'Kvadraten nr 2 gul 23 x 23 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 2 orange, 23 x 23 cm', N'Kvadraten nr 2 orange 23 x 23 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 2 svart, 23 x 23 cm', N'Kvadraten nr 2 svart 23 x 23 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Kvadraten nr 3');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 3 blå, 42 x 42 cm', N'Kvadraten nr 3 bla 42 x 42 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 3 grön,  42 x 42 cm', N'Kvadraten nr 3 gron  42 x 42 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 3 gul, 42 x 42 cm', N'Kvadraten nr 3 gul 42 x 42 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 3 orange, 42 x 42 cm', N'Kvadraten nr 3 orange 42 x 42 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Kvadraten nr 3 svart, 42 x 42 cm', N'Kvadraten nr 3 svart 42 x 42 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'L1');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L1 blå', N'L1 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L1 grön', N'L1 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L1 gul', N'L1 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L1 orange', N'L1 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L1 svart', N'L1 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'L2');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L2 blå', N'L2 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L2 grön', N'L2 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L2 gul', N'L2 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L2 orange', N'L2 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L2 svart', N'L2 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'L3');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L3 blå', N'L3 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L3 grön', N'L3 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L3 gul', N'L3 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L3 orange', N'L3 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'L3 svart', N'L3 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Lim för uppfodring 2 liter');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Lim för uppfodring 2 liter', N'Lim for uppfodring 2 liter.gif', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Lurifaxen 18x27cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Lurifaxen 18x27 cm,', N'Lurifaxen 18x27 cm.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Markeringskritan gel');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Markeringskritan gel, rosa', N'Markeringskritan gel rosa.jpg', N'rosa');

    INSERT INTO FieldTarget (Name) VALUES (N'Metric-tavla nålad 100/fp');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Metric-tavla nålad 100/fp', N'Metric-tavla nalad 100-fp.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Ovalen nr 1');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 1 blå, 10,4 x 20 cm', N'Ovalen nr 1 bla 104 x 20 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 1 gul, 10,4 x 20 cm', N'Ovalen nr 1 gul 104 x 20 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 1 orange, 10,4 x 20 cm', N'Ovalen nr 1 orange 104 x 20 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 1 svart, 10,4 x 20 cm', N'Ovalen nr 1 svart 104 x 20 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Ovalen nr 2');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 2 blå, 15,5 x 30 cm', N'Ovalen nr 2 bla 155 x 30 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 2 grön, 15,5 x 30 cm', N'Ovalen nr 2 gron 155 x 30 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 2 gul, 15,5 x 30 cm', N'Ovalen nr 2 gul 155 x 30 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 2 orange,15,5 x 30 cm', N'Ovalen nr 2 orange155 x 30 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 2 svart, 15,5 x 30 cm', N'Ovalen nr 2 svart 155 x 30 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Ovalen nr 3');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 3 blå, 23,5 x 47 cm', N'Ovalen nr 3 bla 235 x 47 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 3 grön, 23,5 x 47 cm', N'Ovalen nr 3 gron 235 x 47 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 3 gul, 23,5 x 47 cm', N'Ovalen nr 3 gul 235 x 47 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 3 orange, 23,5 x 47 cm', N'Ovalen nr 3 orange 235 x 47 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Ovalen nr 3 svart, 23,5 x 47 cm', N'Ovalen nr 3 svart 235 x 47 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Rapporthund');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Rapporthund', N'Rapporthund.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Rubinen');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Rubinen blå, 37,5 x 50 cm', N'Rubinen bla 375 x 50 cm.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Rubinen grön, 37,5 x 50 cm', N'Rubinen gron 375 x 50 cm.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Rubinen gul, 37,5 x 50 cm', N'Rubinen gul 375 x 50 cm.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Rubinen orange, 37,5 x 50 cm', N'Rubinen orange 375 x 50 cm.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Rubinen svart, 37,5 x 50 cm', N'Rubinen svart 375 x 50 cm.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'S10 D');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S10 D blå', N'S10 D bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S10 D grön', N'S10 D gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S10 D gul', N'S10 D gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S10 D orange', N'S10 D orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S10 D svart', N'S10 D svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'S15 D');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S15 D blå', N'S15 D bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S15 D grön', N'S15 D gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S15 D gul', N'S15 D gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S15 D orange', N'S15 D orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S15 D svart', N'S15 D svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'S20');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S20 blå', N'S20 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S20 grön', N'S20 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S20 gul', N'S20 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S20 orange', N'S20 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S20 svart', N'S20 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'S25');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S25 blå', N'S25 bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S25 grön', N'S25 gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S25 gul', N'S25 gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S25 orange', N'S25 orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'S25 svart', N'S25 svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Spraylim 200 ml');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Spraylim 200 ml', N'Spraylim 200 ml.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Stolpskottet');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Stolpskottet,', N'Stolpskottet.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'SWE-målet 3-delar');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'SWE-målet 3-delar,', N'SWE-malet 3-delar.jpg', N'');

    INSERT INTO FieldTarget (Name) VALUES (N'Tunna D');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunna D blå', N'Tunna D bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunna D grön', N'Tunna D gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunna D gul', N'Tunna D gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunna D orange', N'Tunna D orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunna D svart', N'Tunna D svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Tunnan');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunnan blå', N'Tunnan bla.jpg', N'blå');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunnan grön', N'Tunnan gron.jpg', N'grön');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunnan gul', N'Tunnan gul.jpg', N'gul');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunnan orange', N'Tunnan orange.jpg', N'orange');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tunnan svart', N'Tunnan svart.jpg', N'svart');

    INSERT INTO FieldTarget (Name) VALUES (N'Tvåfärgsmål 1/3');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tvåfärgsmål 1/3 grön', N'Tvafargsmal 1-3 gron.jpg', N'grön');

    INSERT INTO FieldTarget (Name) VALUES (N'Tvåfärgsmål 1/4 Rak');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tvåfärgsmål 1/4 Rak brun', N'Tvafargsmal 1-4 Rak brun.jpg', N'brun');
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tvåfärgsmål 1/4 Rak grön', N'Tvafargsmal 1-4 Rak gron.jpg', N'grön');

    INSERT INTO FieldTarget (Name) VALUES (N'Tvåfärgsmål B 65');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tvåfärgsmål B 65 brun', N'Tvafargsmal B 65 brun.jpg', N'brun');

    INSERT INTO FieldTarget (Name) VALUES (N'Tvåfärgsmål C35');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tvåfärgsmål C35 rödbrun', N'Tvafargsmal C35 rodbrun.jpg', N'rödbrun');

    INSERT INTO FieldTarget (Name) VALUES (N'Tvåfärgsmål Tunnan');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Tvåfärgsmål Tunnan grön', N'Tvafargsmal Tunnan gron.jpg', N'grön');

    INSERT INTO FieldTarget (Name) VALUES (N'Wellpapp, "Pansarwell", 4 mm 99x99cm');
    SET @tid = SCOPE_IDENTITY();
    INSERT INTO FieldTargetVariant (TargetId, FullName, ImageName, Color)
        VALUES (@tid, N'Wellpapp, "Pansarwell", 4 mm 99x99cm', N'Wellpapp _Pansarwell_ 4 mm 99x99cm.jpg', N'');

END
GO