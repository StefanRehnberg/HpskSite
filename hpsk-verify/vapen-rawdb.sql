-- vapen-rawdb.sql — DET PÅSTÅENDE SOM FAKTISKT BEVISAR ATT UPPGIFTERNA ÄR KRYPTERADE.
--
-- KÖR (dev):
--   sqlcmd -S localhost\SQLEXPRESS -d Umbraco -E -C -b -W -i hpsk-verify/vapen-rawdb.sql
--
-- Kör den EFTER vapen-verify.mjs, i samma körning. Sviten skapar fixturen `ZZV %` med kända
-- klartextvärden; den här frågan letar efter dem i tabellen.
--
-- ⚠️ VARFÖR DEN INTE LIGGER I .mjs-SVITEN: påståendet handlar om vad som ligger i databasen, inte
--    om vad ett API svarar. Ett API kan mycket väl svara rätt medan kolumnen är oskyddad.
--
-- ⚠️ JÄMFÖRELSEN GÖRS I SQL OCH SVARET ÄR SKALÄRT (1/0). sqlcmd trunkerar VARBINARY(MAX) vid 256
--    tecken, så ett påstående om FRÅNVARO byggt på utskriften kan bli grönt av fel skäl. Ett
--    skalärt svar kan inte trunkeras.
--
-- ⚠️ NÅLARNA BYGGS MED N'' PÅ SERVERSIDAN. Ett dubbelfnutt i ett sqlcmd-argument spränger
--    argumenttolkningen, och nålarna är just den sortens text.
--
-- ⚠️ KONTROLLPROVET ÄR INTE VALFRITT. Utan det kan alla nollor lika gärna betyda att LIKE-uttrycket
--    är fel byggt, att tabellen är tom, eller att fixturen aldrig skapades — alltså ett vakuöst
--    grönt påstående om precis det som prövas.
SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

DECLARE @needles TABLE (n NVARCHAR(100));
INSERT INTO @needles (n) VALUES
    -- Värdena vapen-verify.mjs skriver in:
    (N'Pardini'), (N'SP-1'), (N'.22 LR'), (N'15,2 cm'), (N'P-99871'), (N'AB-12345'),
    -- Och JSON-nyttolastens FÄLTNAMN. De läcker om krypteringen kringgås, även om värdena
    -- skulle råka vara tomma.
    (N'Fabrikat'), (N'Kaliber'), (N'Licensnummer'), (N'Tillverkningsnummer');

SELECT
    n.n AS Needle,
    CASE WHEN EXISTS (
        SELECT 1 FROM Firearm f
        -- Bloben tolkad som text, i BÅDA byteordningarna: UTF-16 (NVARCHAR) och 8-bitars
        -- (VARCHAR). En nål som bara söks i den ena missas om serialiseringen byter form.
        WHERE CAST(f.EncryptedDetails AS NVARCHAR(MAX)) LIKE N'%' + n.n + N'%'
           OR CAST(f.EncryptedDetails AS VARCHAR(MAX))  LIKE  '%' + CAST(n.n AS VARCHAR(100)) + '%'
        -- Och varje klartextkolumn. En uppgift som råkat hamna i Alias är precis lika läckt.
           OR ISNULL(f.Alias, '')         LIKE N'%' + n.n + N'%'
           OR ISNULL(f.WeaponClass, '')   LIKE N'%' + n.n + N'%'
           OR ISNULL(f.Vapentyp, '')      LIKE N'%' + n.n + N'%'
           OR ISNULL(f.AnnanVapentyp, '') LIKE N'%' + n.n + N'%'
           OR ISNULL(f.Status, '')        LIKE N'%' + n.n + N'%'
    ) THEN 1 ELSE 0 END AS FoundInClear
FROM @needles n
ORDER BY n.n;

-- ── Kontrollprov och sammanfattning ──────────────────────────────────────────────────────────
-- ControlProbeFound MÅSTE vara 1: fixturens alias ligger i klar Alias-kolumn, och hittas det inte
-- är frågan ovan inte att lita på.
SELECT
    CASE WHEN EXISTS (SELECT 1 FROM Firearm WHERE Alias LIKE N'ZZV %')
         THEN 1 ELSE 0 END                                                AS ControlProbeFound,
    (SELECT COUNT(*) FROM Firearm WHERE Alias LIKE N'ZZV %')              AS FixtureRows,
    (SELECT COUNT(*) FROM Firearm
      WHERE Alias LIKE N'ZZV %' AND EncryptedDetails IS NOT NULL)         AS FixtureEncrypted,
    (SELECT MIN(DATALENGTH(EncryptedDetails)) FROM Firearm
      WHERE Alias LIKE N'ZZV %' AND EncryptedDetails IS NOT NULL)         AS MinBlobBytes;

-- ── AAD-bindningen: flytta en blob och se att den INTE går att läsa ──────────────────────────
-- Det här är den konkreta attacken: någon med databasåtkomst kopierar A:s uppgifter till B:s rad.
-- Frågan nedan visar bara att bloben ÄR flyttbar i SQL; att den inte går att ÖPPNA måste prövas
-- genom appen (RevealDetails ska svara med felet som namnger "flyttats till ett annat vapen-id").
-- Avkommentera för att sätta upp det läget, kör RevealDetails, och kör sedan --cleanup.
--
-- UPDATE Firearm
--    SET EncryptedDetails = (SELECT EncryptedDetails FROM Firearm WHERE Alias = N'ZZV Innehav')
--  WHERE Alias = N'ZZV Planerat';

-- ── Städning ─────────────────────────────────────────────────────────────────────────────────
-- vapen-verify.mjs GÖMMER sina rader (IsActive=0) via app-vägen — det raderar dem inte, eftersom
-- ett avvecklat vapen aldrig får försvinna i produktion. Fixturen ska däremot bort. Avkommentera:
--
-- DELETE FROM FirearmAccessLog WHERE FirearmId IN (SELECT Id FROM Firearm WHERE Alias LIKE N'ZZV %');
-- DELETE FROM Firearm WHERE Alias LIKE N'ZZV %';
-- SELECT (SELECT COUNT(*) FROM Firearm WHERE Alias LIKE N'ZZV %') AS FixtureLeft,
--        (SELECT COUNT(*) FROM FirearmFederation)                 AS FedTotal,
--        (SELECT COUNT(*) FROM ForeningsintygRequest)             AS RequestTotal,
--        (SELECT COUNT(*) FROM FirearmUsage)                      AS UsageTotal;
GO
