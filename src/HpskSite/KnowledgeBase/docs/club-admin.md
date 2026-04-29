---
title: Klubbadministratör - Hantera din klubb
roles: [club-admin]
features: [member-management, events, competitions, series, club-settings, training-groups, skjutledare-assignment, invoices, certifieringar, klubbstatistik]
---

# Klubbadministration

Som klubbadministratör har du full kontroll över din klubbs sida och medlemmar.

## Komma åt administrationspanelen

Gå till din klubbs sida och klicka på fliken **"Admin"** (visas bara för klubbadmins och sajtadmins).

## Godkänna nya medlemmar

När någon registrerar sig och väljer din klubb:
1. Du får ett e-postmeddelande om att en ny medlem väntar på godkännande (med en snabblänk för direkt godkännande)
2. Gå till **Admin** > **Medlemmar**
3. Se listan med väntande registreringar
4. Klicka **"Godkänn"** eller **"Avslå"** för varje person
5. Medlemmen får ett e-postmeddelande med beskedet och kan sedan logga in

## Hantera medlemmar

Under **Admin** > **Medlemmar**:
- Se alla klubbmedlemmar
- Sök bland medlemmar
- Exportera medlemslista (CSV)

### Tilldela skjutledare

Klicka på **"Skjutledare"**-knappen i Medlemmar-fliken för att öppna skjutledarhanteringen:
- Se nuvarande skjutledare
- Lägg till nya skjutledare från en dropdown
- Ta bort skjutledare

## Hantera evenemang

Under **Admin** > **Händelser**:
1. Klicka **"Ny Händelse"**
2. Fyll i:
   - **Namn**
   - **Typ** — Träning, Städning, Möte, Socialt, Nyhet, Annat
   - **Datum och tid**
   - **Beskrivning**
   - **Plats**
   - **Kontaktperson**
3. Klicka **"Spara"**

**Obs:** Tävlingar skapas separat under **Tävlingar**-fliken, inte som vanliga evenemang.

Du kan även redigera och ta bort befintliga evenemang.

### Evenemangssidor

Evenemang kan utökas till fullständiga landningssidor med:
- **Huvudbild** (hero image)
- **Innehållsblock** — text, bilder och HTML-sektioner som du bygger fritt
- **Snabblänkar** — knappar som leder till t.ex. anmälningslänkar
- **Anmälnings-URL** — extern länk för anmälan
- **Avgift, utrustning, målgrupp** — ytterligare information

Använd redigera-knappen på evenemanget för att bygga ut sidan.

**Tips för tävlingsinbjudningar:** Om du vill skapa en detaljerad inbjudningssida med bilder för en tävling, skapa ett evenemang och lägg till en "Anmäl dig här"-länk till tävlingen. Tävlingens beskrivningsfält stöder också bilduppladdning direkt, men evenemangssidor ger mer utrymme och flexibilitet.

## Hantera tävlingar

Under **Admin** > **Tävlingar**:
- **Skapa ny tävling** för din klubb
- Redigera tävlingsdetaljer
- Kopiera en tävling (datum justeras automatiskt +1 år)
- Ta bort tävlingar (om inga anmälningar finns)
- Tilldela tävlingsledare

## Hantera serier

Under **Admin** > **Serier**:
- Skapa tävlingsserier för din klubb
- Redigera och hantera serier
- Kopiera serier från föregående år

## Fakturor

Under **Admin** > **Fakturor**:
- Se betalningsfakturor för klubbens tävlingar
- Filtrera på tävling, betalningsstatus
- Markera fakturor som betalda

## Redigera klubbinformation

Under **Admin** > **Inställningar**:
- **Klubbnamn** och **beskrivning**
- **Kontaktperson**, e-post, telefon
- **Adress**, postnummer, stad
- **Hemsida**
- **Logotyp** och **bannerbild**

## Hantera träningsgrupper

Via **Skyttetrappan** > fliken **"Administration"** eller via klubbens Admin-panel:
1. Klicka **"Skapa ny grupp"**
2. Namnge gruppen
3. Lägg till medlemmar och tränare
4. Aktivera gruppen

Du kan även:
- Redigera gruppens namn och inställningar
- Lägga till/ta bort medlemmar och tränare
- Avaktivera grupper som inte längre är aktiva
- Skicka välkomstmail till nya medlemmar

## Hantera dokument

Ladda upp och hantera dokument i klubbens dokumentarkiv. Medlemmarna ser dessa under fliken **"Dokument"** på klubbsidan.

## Certifieringar — instruktörer och kontrollanter

Under **Admin** > **Certifieringar** hanteras klubbens SPSF-registrerade roller:

- **Föreningsinstruktörer** — utnämn medlemmar till klubbens Föreningsinstruktörer (kräver att de redan har eller får en certifiering). SPSF kräver att klubben har minst en.
- **Vapenkontrollanter** — medlemmar med aktiv Vapenkontrollantcertifiering.
- **Banläggare** — medlemmar med aktiv Banläggarcertifiering (för fältskytte).

Om du själv är Krets- eller Riksinstruktör kan du utfärda nya certifieringar direkt. Annars väljer du i dropdownen **"Certifierad av"** vem som har utbildat personen — endast medlemmar med behörighet visas. Se separat dokumentation: [Instruktörer och certifieringar](instructors.md).

Datumväljarna i tilldelningsdialogen använder svenskt format (ÅÅÅÅ-MM-DD). Lämna förfallodatum tomt för certifieringar som inte förfaller.

## Statistik

Under **Admin** > **Statistik** ser du klubbens hälsotillstånd:

- **Sammanfattningskort** — antal medlemmar, aktiva senaste 30 dagar (med jämförelse mot snittklubb i samma storlek), kommande events, tävlingar i år.
- **Notiser ("nudges")** — pekar ut konkreta åtgärder: väntande godkännanden, inaktiva medlemmar (90 d), saknade events i år, saknad Skjutledare, saknad Föreningsinstruktör (SPSF-krav).
- **Diagram** — nya medlemmar per månad, medlemmar per Skyttetrappan-nivå, träningsmatcher per månad, träningsresultat per disciplin.
- **Tävlingar** — tävlingar per disciplin (donut) och de fem största tävlingarna (sorterat på antal anmälda).
- **Klubbaktiviteter** — events i år vs. föregående år, events per typ, events per månad.
- **Mest aktiva skyttar (30d)** — topp 5 medlemmar baserat på registrerade träningsresultat senaste 30 dagar. Bra anledning att skicka uppmuntran.

Klicka **"Uppdatera"** för att hämta färska siffror om något ändrats nyligen.
