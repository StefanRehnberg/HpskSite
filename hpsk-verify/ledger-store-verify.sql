-- ledger-store-verify.sql — verifikationsliggarens LAGER (P1 + projektdimensionen).
--
-- KÖR:
--   sqlcmd -S localhost\SQLEXPRESS -d Umbraco -E -C -b -W -i hpsk-verify/ledger-store-verify.sql
--
-- ⚠️⚠️ DEN HÄR SVITEN MÄTER DATABASEN, INTE KODEN — och det är hela poängen.
-- Liggarens löfte är att siffrorna inte går att ändra i efterhand. Det löftet bärs av FYRA TRIGGERS,
-- inte av en konvention i C#. En tabell utan sina triggers ser fullständigt frisk ut: den tar emot
-- poster, läser tillbaka dem, ingenting felar. Det enda som är borta är garantin. Ett enhetstest
-- mot tjänsten kan aldrig se det, eftersom tjänsten aldrig försöker göra det förbjudna.
--
-- ⚠️ VARJE PÅSTÅENDE HAR SIN EGEN TRANSAKTION, och det är inte en stilfråga.
-- Triggarna vägrar med THROW, och en THROW inuti en trigger rullar tillbaka transaktionen. Låg
-- fixturen i en yttre transaktion skulle den försvinna vid första vägran och alla följande
-- påståenden mäta ett tomt bord. Varje test bygger därför det lilla det behöver och rullar tillbaka
-- allt — inklusive fixturen. Sviten lämnar NOLL rader efter sig.
--
-- ⚠️ PÅSTÅENDENA MATCHAR PÅ FELNUMMER (50100/50101/50102), aldrig på "det blev ett fel".
-- Ett stavfel i ett kolumnnamn ger också ett fel, och ett test som nöjer sig med "något kastade"
-- blir då grönt av sin egen trasighet. Det är exakt den sortens vakuösa grönt den här filen finns
-- för att inte producera.
--
-- ⚠️ KONTROLLPROVEN ÄR OBLIGATORISKA. Sektion 4 skriver det som SKA gå igenom. Utan dem kan varje
-- "vägrades"-påstående vara grönt för att fixturen är ogiltig och INSERT:en aldrig kom fram.
--
-- ⚠️ QUOTED_IDENTIFIER MÅSTE VARA PÅ. LedgerJournalEntryLine bär ett FILTRERAT index
-- (IX_LedgerJournalEntryLine_Project), och SQL Server vägrar ALL DML mot en sådan tabell när
-- QUOTED_IDENTIFIER är AV — vilket är sqlcmds standard. Utan -b exitar sqlcmd dessutom 0 på
-- T-SQL-fel, så en körning kan se lyckad ut medan ingenting kördes. Kör med -b.
--
-- A/B — KÖR DEN, annars vet du inte att sviten kan bli röd:
--   Sätt @AB = 1 nedan. Sviten stänger då TILLFÄLLIGT av oföränderlighetstriggern, kör om
--   påstående 2.1 och slår på den igen.
--
--   ⚠️⚠️ DISABLE TRIGGER, INTE sp_rename — och skillnaden kostade en körning.
--   Husregeln "döp om en trigger, droppa den aldrig" gäller en NAMNBASERAD schemakontroll: där
--   gör omdöpningen att kontrollen inte hittar triggern. Men ett BETEENDEtest mäter om triggern
--   FYRAR, och en omdöpt trigger sitter kvar på tabellen och fyrar precis som förut. Första A/B:n
--   rapporterade därför "vägrades ändå" om en trigger som aldrig varit borta.
--   ⚠️ ALDRIG DROP + CREATE — triggerkroppen bär svensk text och en återskapning via sqlcmd
--   riskerar mojibake i ett felmeddelande som ska läsas av en människa. DISABLE rör aldrig kroppen.
--
--   ⚠️⚠️ SEKTION 1 OCH 7 KRÄVER `is_disabled = 0`, inte bara att triggern finns. `DISABLE TRIGGER`
--   tar inte bort raden ur sys.triggers, så en närvarokontroll på namn är GRÖN på en liggare vars
--   spärrar allihop är avstängda. Samma hål fanns i LedgerSchemaInspector och är täppt.
--   Faller sektion 7 är liggaren OSKYDDAD just nu — slå på för hand innan du gör något annat:
--       ENABLE TRIGGER TR_LedgerJournalEntry_NoUpdateDelete ON dbo.LedgerJournalEntry;
--
-- FIXTUREN ligger på utställare (0, 999001) — ett id ingen klubb har. Sök på 999001 om något mot
-- förmodan blir kvar.

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

DECLARE @AB bit = 0;              -- 1 = kör A/B (se huvudet)

DECLARE @Issuer int = 999001;
DECLARE @EntryId int, @LineId int, @FyOpen int, @FyEstablished int, @ProjId int;

-- ⚠⚠ TABELLVARIABEL, INTE #temptabell — och det är inte en stilfråga.
-- En #temptabell är TRANSAKTIONELL. Varje påstående som skrivs INNE i sin transaktion före
-- ROLLBACK försvinner då med rollbacken — alltså exakt de POSITIVA påståendena, inklusive
-- kontrollprovet. Första körningen rapporterade "15/15 ALLT GRONT" om en svit där fyra påståenden
-- aldrig registrerades; enda spåret var hål i identitetsnumreringen (12, 13, 15, 17 saknades).
-- En tabellvariabel rörs inte av ROLLBACK. Byt ALDRIG tillbaka till #temp.
DECLARE @r TABLE (Nr int IDENTITY(1,1), Sektion nvarchar(40), Namn nvarchar(170), Ok bit, Detalj nvarchar(400));

-- =================================================================================================
-- 1. SCHEMAT — finns lagret över huvud taget?
-- =================================================================================================
INSERT @r (Sektion, Namn, Ok, Detalj)
SELECT '1 Schema', '16 ledger-tabeller finns',
       CASE WHEN COUNT(*) = 16 THEN 1 ELSE 0 END, CONCAT(COUNT(*), ' av 16')
FROM sys.tables WHERE name LIKE 'Ledger%';

INSERT @r (Sektion, Namn, Ok, Detalj)
SELECT '1 Schema', 'De fyra triggarna finns OCH ar paslagna',
       CASE WHEN COUNT(*) = 4 THEN 1 ELSE 0 END, CONCAT(COUNT(*), ' av 4 paslagna')
FROM sys.triggers
WHERE is_disabled = 0 AND name IN ('TR_LedgerJournalEntry_NoUpdateDelete','TR_LedgerJournalEntryLine_NoUpdateDelete',
               'TR_LedgerJournalEntry_NoWriteInEstablishedYear','TR_LedgerReceipt_NoUpdateDelete');

INSERT @r (Sektion, Namn, Ok, Detalj)
SELECT '1 Schema', 'Konteringsraden bar ProjectId + ProjectName',
       CASE WHEN COL_LENGTH('dbo.LedgerJournalEntryLine','ProjectId') IS NOT NULL
             AND COL_LENGTH('dbo.LedgerJournalEntryLine','ProjectName') IS NOT NULL THEN 1 ELSE 0 END,
       'projektdimensionen';

-- =================================================================================================
-- 2. OFÖRÄNDERLIGHETEN — lagrets bärande löfte
-- =================================================================================================

-- 2.1 UPDATE på en bokförd verifikation
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerJournalEntry (IssuerType, IssuerId, SeriesId, Number, FiscalYearId,
        AccountingDate, EventDate, RegisteredUtc, Description, SourceType, CreatedByMemberId)
    VALUES (0, @Issuer, 1, 1, 0, '2026-01-15', '2026-01-15', SYSUTCDATETIME(), N'ZZV testpost', 'manual', 1);
    SET @EntryId = SCOPE_IDENTITY();
    UPDATE dbo.LedgerJournalEntry SET Description = N'andrad i efterhand' WHERE Id = @EntryId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'UPDATE pa verifikation vagras', 0, 'UPDATE GICK IGENOM - liggaren ar oskyddad');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'UPDATE pa verifikation vagras',
            CASE WHEN ERROR_NUMBER() = 50100 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- 2.2 DELETE på en bokförd verifikation
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerJournalEntry (IssuerType, IssuerId, SeriesId, Number, FiscalYearId,
        AccountingDate, EventDate, RegisteredUtc, Description, SourceType, CreatedByMemberId)
    VALUES (0, @Issuer, 1, 2, 0, '2026-01-15', '2026-01-15', SYSUTCDATETIME(), N'ZZV testpost', 'manual', 1);
    SET @EntryId = SCOPE_IDENTITY();
    DELETE dbo.LedgerJournalEntry WHERE Id = @EntryId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'DELETE pa verifikation vagras', 0, 'DELETE GICK IGENOM - liggaren ar oskyddad');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'DELETE pa verifikation vagras',
            CASE WHEN ERROR_NUMBER() = 50100 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- 2.3 UPDATE på en konteringsrad
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerJournalEntryLine (JournalEntryId, LineNumber, AccountNumber, AccountName, Debit, Credit)
    VALUES (0, 1, 1930, N'Foreningskonto', 300, 0);
    SET @LineId = SCOPE_IDENTITY();
    UPDATE dbo.LedgerJournalEntryLine SET Debit = 999 WHERE Id = @LineId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'UPDATE pa konteringsrad vagras', 0, 'UPDATE GICK IGENOM');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'UPDATE pa konteringsrad vagras',
            CASE WHEN ERROR_NUMBER() = 50101 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- 2.4 DELETE på en konteringsrad
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerJournalEntryLine (JournalEntryId, LineNumber, AccountNumber, AccountName, Debit, Credit)
    VALUES (0, 1, 1930, N'Foreningskonto', 300, 0);
    SET @LineId = SCOPE_IDENTITY();
    DELETE dbo.LedgerJournalEntryLine WHERE Id = @LineId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'DELETE pa konteringsrad vagras', 0, 'DELETE GICK IGENOM');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'DELETE pa konteringsrad vagras',
            CASE WHEN ERROR_NUMBER() = 50101 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- 2.5 ⚠️ PROJEKTMÄRKNINGEN KAN INTE EFTERMONTERAS.
-- Det är hela skälet till att kolumnen måste in FÖRE första bokförda raden. Går den att sätta i
-- efterhand är det påståendet falskt, och då är migreringens varning fel.
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerJournalEntryLine (JournalEntryId, LineNumber, AccountNumber, AccountName, Debit, Credit)
    VALUES (0, 1, 4030, N'Priser och medaljer', 284, 0);
    SET @LineId = SCOPE_IDENTITY();
    UPDATE dbo.LedgerJournalEntryLine SET ProjectId = 1, ProjectName = N'SSM 2025' WHERE Id = @LineId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'Projekt gar INTE att satta i efterhand', 0,
            'UPDATE GICK IGENOM - kolumnen hade kunnat eftermonteras');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'Projekt gar INTE att satta i efterhand',
            CASE WHEN ERROR_NUMBER() = 50101 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- 2.6 + 2.7 Kvittot har en EGEN serie och en EGEN trigger
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerReceipt (IssuerType, IssuerId, SeriesId, Number, PaymentId, Amount)
    VALUES (0, @Issuer, 1, 1, 0, 250);
    SET @EntryId = SCOPE_IDENTITY();
    UPDATE dbo.LedgerReceipt SET Amount = 999 WHERE Id = @EntryId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'UPDATE pa kvitto vagras', 0, 'UPDATE GICK IGENOM');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'UPDATE pa kvitto vagras',
            CASE WHEN ERROR_NUMBER() BETWEEN 50100 AND 50199 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerReceipt (IssuerType, IssuerId, SeriesId, Number, PaymentId, Amount)
    VALUES (0, @Issuer, 1, 2, 0, 250);
    SET @EntryId = SCOPE_IDENTITY();
    DELETE dbo.LedgerReceipt WHERE Id = @EntryId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'DELETE pa kvitto vagras', 0, 'DELETE GICK IGENOM');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('2 Oforanderlighet', 'DELETE pa kvitto vagras',
            CASE WHEN ERROR_NUMBER() BETWEEN 50100 AND 50199 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- =================================================================================================
-- 3. PERIODLÅSNINGEN — ett fastställt år tar inte emot skrivningar (F3, beslutad 2026-09-18)
-- =================================================================================================
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerFiscalYear (IssuerType, IssuerId, Year, StartDate, EndDate, Status)
    VALUES (0, @Issuer, 2025, '2025-01-01', '2025-12-31', 'established');
    SET @FyEstablished = SCOPE_IDENTITY();
    INSERT dbo.LedgerJournalEntry (IssuerType, IssuerId, SeriesId, Number, FiscalYearId,
        AccountingDate, EventDate, RegisteredUtc, Description, SourceType, CreatedByMemberId)
    VALUES (0, @Issuer, 1, 3, @FyEstablished, '2025-06-01', '2025-06-01', SYSUTCDATETIME(), N'ZZV', 'manual', 1);
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('3 Periodlasning', 'INSERT i FASTSTALLT ar vagras', 0, 'INSERT GICK IGENOM - aret var oskyddat');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('3 Periodlasning', 'INSERT i FASTSTALLT ar vagras',
            CASE WHEN ERROR_NUMBER() = 50102 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 90)));
END CATCH

-- ⚠️ `closing` MÅSTE släppa igenom. Det mellanläget är hela skälet Status har TRE tillstånd och
-- inte är en boolean: bokslutsposterna (kundfordran vid årsskiftet, periodiseringar) skrivs i ett
-- år som är stängt för löpande bokföring. Vägrar den här är bokslut omöjligt att göra.
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerFiscalYear (IssuerType, IssuerId, Year, StartDate, EndDate, Status)
    VALUES (0, @Issuer, 2025, '2025-01-01', '2025-12-31', 'closing');
    SET @FyOpen = SCOPE_IDENTITY();
    INSERT dbo.LedgerJournalEntry (IssuerType, IssuerId, SeriesId, Number, FiscalYearId,
        AccountingDate, EventDate, RegisteredUtc, Description, SourceType, CreatedByMemberId)
    VALUES (0, @Issuer, 1, 4, @FyOpen, '2025-12-31', '2025-12-31', SYSUTCDATETIME(), N'ZZV bokslutspost', 'manual', 1);
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('3 Periodlasning', 'INSERT i ar under BOKSLUT (closing) slapps igenom', 1, 'bokslutsposter kan skrivas');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('3 Periodlasning', 'INSERT i ar under BOKSLUT (closing) slapps igenom', 0,
            CONCAT('VAGRADES - fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 80)));
END CATCH

-- =================================================================================================
-- 4. KONTROLLPROV — det som SKA gå igenom måste gå igenom
--
-- ⚠️ Utan den här sektionen kan varje "vagras"-paastaaende ovan vara groent av fel skael: en ogiltig
--    INSERT kastar ocksaa, och daa maeter sviten sin egen trasighet i staellet foer liggarens skydd.
-- =================================================================================================
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerFiscalYear (IssuerType, IssuerId, Year, StartDate, EndDate, Status)
    VALUES (0, @Issuer, 2026, '2026-01-01', '2026-12-31', 'open');
    SET @FyOpen = SCOPE_IDENTITY();
    INSERT dbo.LedgerJournalEntry (IssuerType, IssuerId, SeriesId, Number, FiscalYearId,
        AccountingDate, EventDate, RegisteredUtc, Description, SourceType, CreatedByMemberId)
    VALUES (0, @Issuer, 1, 5, @FyOpen, '2026-05-19', '2026-05-19', SYSUTCDATETIME(), N'ZZV giltig post', 'manual', 1);
    SET @EntryId = SCOPE_IDENTITY();
    INSERT dbo.LedgerJournalEntryLine (JournalEntryId, LineNumber, AccountNumber, AccountName, Debit, Credit, ProjectId, ProjectName)
    VALUES (@EntryId, 1, 1930, N'Foreningskonto', 300, 0, 1, N'SSM 2025');
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('4 Kontrollprov', 'En GILTIG verifikation med projekt gar att skriva', 1,
            CONCAT('verifikation ', @EntryId, ' med projektmarkt rad'));
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('4 Kontrollprov', 'En GILTIG verifikation med projekt gar att skriva', 0,
            CONCAT('FIXTUREN AR TRASIG - fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 80)));
END CATCH

-- =================================================================================================
-- 5. PROJEKTDIMENSIONEN — det schemat garanterar, inte det koden lovar
-- =================================================================================================

-- 5.1 Två projekt med samma namn hos samma förening är en dubblett som delar rapporten i två.
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerProject (IssuerType, IssuerId, Name) VALUES (0, @Issuer, N'SSM 2025');
    INSERT dbo.LedgerProject (IssuerType, IssuerId, Name) VALUES (0, @Issuer, N'SSM 2025');
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Dubblettnamn hos samma forening vagras', 0, 'BADA GICK IGENOM');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Dubblettnamn hos samma forening vagras',
            CASE WHEN ERROR_NUMBER() IN (2601, 2627) THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 80)));
END CATCH

-- 5.2 Samma namn hos EN ANNAN forening maaste gaa igenom - annars aer indexet foer brett och en
--     klubb kan blockera en annans projektnamn.
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerProject (IssuerType, IssuerId, Name) VALUES (0, @Issuer, N'SSM 2025');
    INSERT dbo.LedgerProject (IssuerType, IssuerId, Name) VALUES (1, @Issuer, N'SSM 2025');
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Samma namn hos ANNAN forening slapps igenom', 1, 'indexet ar scopat till utstallaren');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Samma namn hos ANNAN forening slapps igenom', 0,
            CONCAT('VAGRADES - fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 80)));
END CATCH

-- 5.3 Ett projekt som slutar innan det börjar är en felskrivning, inte ett projekt.
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerProject (IssuerType, IssuerId, Name, StartDate, EndDate)
    VALUES (0, @Issuer, N'ZZV bakvant', '2026-05-19', '2026-01-01');
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Slutdatum fore startdatum vagras', 0, 'GICK IGENOM');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Slutdatum fore startdatum vagras',
            CASE WHEN ERROR_NUMBER() = 547 THEN 1 ELSE 0 END,
            CONCAT('fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 80)));
END CATCH

-- 5.4 Ett projekt AR mutabelt - det ar verifikationerna som ar oforanderliga. Gar det inte att
--     dopa om kan en forening aldrig ratta ett felstavat projektnamn.
BEGIN TRY
    BEGIN TRAN;
    INSERT dbo.LedgerProject (IssuerType, IssuerId, Name) VALUES (0, @Issuer, N'ZZV projekt');
    SET @ProjId = SCOPE_IDENTITY();
    UPDATE dbo.LedgerProject SET Name = N'ZZV omdopt' WHERE Id = @ProjId;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Ett projekt GAR att dopa om', 1, 'projektet ar mutabelt, verifikationen ar inte');
    ROLLBACK;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK;
    INSERT @r (Sektion, Namn, Ok, Detalj)
    VALUES ('5 Projekt', 'Ett projekt GAR att dopa om', 0,
            CONCAT('VAGRADES - fel ', ERROR_NUMBER(), ': ', LEFT(ERROR_MESSAGE(), 80)));
END CATCH

-- =================================================================================================
-- 6. A/B — bevisar att sektion 2 kan bli röd. Se huvudet.
-- =================================================================================================
IF @AB = 1
BEGIN
    BEGIN TRY
        DISABLE TRIGGER dbo.TR_LedgerJournalEntry_NoUpdateDelete ON dbo.LedgerJournalEntry;

        BEGIN TRY
            BEGIN TRAN;
            INSERT dbo.LedgerJournalEntry (IssuerType, IssuerId, SeriesId, Number, FiscalYearId,
                AccountingDate, EventDate, RegisteredUtc, Description, SourceType, CreatedByMemberId)
            VALUES (0, @Issuer, 1, 9, 0, '2026-01-15', '2026-01-15', SYSUTCDATETIME(), N'ZZV ab', 'manual', 1);
            SET @EntryId = SCOPE_IDENTITY();
            UPDATE dbo.LedgerJournalEntry SET Description = N'andrad' WHERE Id = @EntryId;
            INSERT @r (Sektion, Namn, Ok, Detalj)
            VALUES ('6 A/B', 'UTAN triggern gar UPDATE igenom (sviten kan bli rod)', 1,
                    'paastaaende 2.1 mater verkligen triggern');
            ROLLBACK;
        END TRY
        BEGIN CATCH
            IF @@TRANCOUNT > 0 ROLLBACK;
            INSERT @r (Sektion, Namn, Ok, Detalj)
            VALUES ('6 A/B', 'UTAN triggern gar UPDATE igenom (sviten kan bli rod)', 0,
                    CONCAT('vagrades anda - fel ', ERROR_NUMBER(), ' - paastaaende 2.1 mater NAGOT ANNAT'));
        END CATCH;

        -- ⚠️ Semikolonet efter END CATCH ar inte kosmetik: ENABLE/DISABLE TRIGGER kraver att
        -- foregaende sats ar terminerad, annars svarar parsern "Incorrect syntax near 'ENABLE'".
        ENABLE TRIGGER dbo.TR_LedgerJournalEntry_NoUpdateDelete ON dbo.LedgerJournalEntry;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK;
        -- ⚠️ Slå ALLTID på igen, även när något sprack mitt i. En avstängd spärr som blir kvar är
        -- värre än ett rött test — liggaren tar då emot ändringar utan att något säger ifrån.
        ENABLE TRIGGER dbo.TR_LedgerJournalEntry_NoUpdateDelete ON dbo.LedgerJournalEntry;
        INSERT @r (Sektion, Namn, Ok, Detalj)
        VALUES ('6 A/B', 'A/B kunde genomforas', 0, LEFT(ERROR_MESSAGE(), 200));
    END CATCH
END

-- =================================================================================================
-- 7. EFTERKONTROLL — liggaren måste vara skyddad när sviten är klar
-- =================================================================================================
INSERT @r (Sektion, Namn, Ok, Detalj)
SELECT '7 Efterkontroll', 'Alla fyra triggars ar PASLAGNA efter koringen',
       CASE WHEN COUNT(*) = 4 THEN 1 ELSE 0 END,
       CASE WHEN COUNT(*) = 4 THEN 'liggaren ar skyddad'
            ELSE 'LIGGAREN AR OSKYDDAD - slaa paa triggern for hand, se huvudet' END
FROM sys.triggers
WHERE is_disabled = 0 AND name IN ('TR_LedgerJournalEntry_NoUpdateDelete','TR_LedgerJournalEntryLine_NoUpdateDelete',
               'TR_LedgerJournalEntry_NoWriteInEstablishedYear','TR_LedgerReceipt_NoUpdateDelete');

INSERT @r (Sektion, Namn, Ok, Detalj)
SELECT '7 Efterkontroll', 'Sviten lamnade inga rader efter sig',
       CASE WHEN (SELECT COUNT(*) FROM dbo.LedgerJournalEntry WHERE IssuerId = @Issuer)
               + (SELECT COUNT(*) FROM dbo.LedgerFiscalYear WHERE IssuerId = @Issuer)
               + (SELECT COUNT(*) FROM dbo.LedgerProject WHERE IssuerId = @Issuer)
               + (SELECT COUNT(*) FROM dbo.LedgerReceipt WHERE IssuerId = @Issuer) = 0 THEN 1 ELSE 0 END,
       'allt rullades tillbaka';

-- =================================================================================================
-- RESULTAT
-- =================================================================================================
SELECT Nr, Sektion, Namn, CASE WHEN Ok = 1 THEN 'OK' ELSE 'FEL' END AS Utfall, Detalj FROM @r ORDER BY Nr;

SELECT CONCAT(SUM(CAST(Ok AS int)), '/', COUNT(*), ' grona') AS Summering,
       CASE WHEN SUM(CAST(Ok AS int)) = COUNT(*) THEN 'ALLT GRONT' ELSE 'ROTT - se listan ovan' END AS Dom
FROM @r;


-- ⚠️⚠️ SISTA RADEN, ALLTID. Svaret ska vara 0. Är det inte 0 ligger en öppen transaktion kvar och
-- håller lås på liggarens tabeller tills sessionen committar eller stängs — en app-recycle biter
-- INTE på det.
SELECT @@TRANCOUNT AS OppnaTransaktioner;
