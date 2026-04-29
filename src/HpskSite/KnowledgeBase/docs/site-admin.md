---
title: Sajtadministratör - Fullständig systemhantering
roles: [admin]
features: [competition-crud, series-management, user-management, invoices, statistics, clubs, regions, riksinstruktorer, certifieringar, visitor-stats]
---

# Sajtadministration

Som sajtadministratör har du tillgång till alla funktioner på pistol.nu, inklusive systemövergripande administration.

## Adminpanelen

Gå till **Admin** i menyn. Panelen har följande flikar:

- **Tävlingar** — skapa, redigera, kopiera, ta bort tävlingar
- **Serier** — hantera tävlingsserier
- **Användare** — medlemshantering
- **Klubbar** — klubbadministration
- **Fakturor** — betalningshantering
- **Kretsar** — regional administration (bara sajtadmin)
- **Riksinstruktörer** — utfärda Riksinstruktörscertifieringar och utnämna till område (bara sajtadmin)
- **Statistik** — systemstatistik (bara sajtadmin)
- **Figurkatalog** — figurhantering för fältskytte

Kretsadmins har också tillgång till adminpanelen men ser bara data från sin region och saknar Kretsar-, Riksinstruktörer- och Statistik-flikarna.

## Tävlingshantering

### Skapa tävling
1. Klicka **"Skapa tävling"** — öppnar tävlingsguiden
2. Fyll i steg för steg:
   - **Grundinfo** — namn, typ, datum, plats, klubb
   - **Skytteklasser** — vilka klasser som är tillgängliga
   - **Anmälan** — öppnings-/stängningsdatum, maxantal deltagare
   - **Avgift** — belopp och Swish-konfiguration (om tillämpligt)
   - **Serier** — antal omgångar
   - **Finaler** — aktivera/avaktivera
   - **Kontaktinfo** — tävlingsledare, e-post, telefon
   - **Tävlingsledare** — tilldela ansvariga medlemmar
3. Klicka **"Skapa"**

### Redigera och kopiera tävlingar
- Öppna en befintlig tävling och klicka **"Redigera"**
- I beskrivningsfältet kan du **ladda upp bilder** via bildikonen i verktygsfältet (JPG, PNG, GIF, WebP, max 5 MB)
- Kopiera en tävling som mall (datum justeras +1 år)

### Inbjudan (PDF) — bara för externa tävlingar
Uppladdning av inbjudnings-PDF finns **enbart för externa tävlingar** (annonser skapade med "Ny annons"). Vanliga pistol.nu-tävlingar har ingen separat PDF-uppladdning. Istället läggs bilder och information direkt i beskrivningsfältet.

**Tips:** Om en arrangör behöver en mer utförlig inbjudningssida kan klubbadmin skapa ett **evenemang** på klubbsidan med bilder, textblock och en anmälningslänk.

## Seriehantering

Serier är en samling tävlingar som räknas ihop (t.ex. Hallandsserien):
1. **Skapa serie** — namn, säsong, beskrivning
2. **Lägg till tävlingar** i serien
3. **Konfigurera poängberäkning** — hur resultat från deltävlingar räknas ihop
4. **Publicera serieställning**

Du kan kopiera serier från föregående år som mall.

## Användarhantering

Under fliken **Användare**:
- **Sök** bland alla medlemmar (namn, e-post)
- **Godkänn/Avslå** väntande registreringar
- **Redigera** medlemsinformation
- **Tilldela roller:**
  - Kretsadmin — via rolldialogen
  - Klubbadmin — via rolldialogen
  - (Skjutledare tilldelas via klubbens Admin-panel, inte härifrån)
- **Lås upp** konton (konton låses automatiskt efter misslyckade inloggningar — det finns ingen manuell låsningsfunktion)
- **Ta bort** medlemmar
- **Exportera** medlemsdata (CSV)
- **Bjud in** nya medlemmar via e-post — mottagaren får en länk för att aktivera sitt konto

Godkännandemail till klubbadmins innehåller en **"Godkänn direkt"**-länk för snabb godkänning utan att logga in.

## Klubbhantering

Under fliken **Klubbar**:
- Se alla klubbar i systemet
- Skapa nya klubbar
- Redigera klubbinformation
- Tilldela klubbadministratörer
- Verifiera och granska klubbdata

## Fakturahantering

Under fliken **Fakturor**:
- Se alla utställda fakturor för tävlingsanmälningar
- Filtrera på betalningsstatus, tävling, klubb, region, datum
- **Markera som betald** när betalning inkommit
- **Makulera** fakturor
- **Skicka om** faktura-e-post med Swish QR-kod
- **Generera ny QR-kod** för en faktura

## Kretshantering

Under fliken **Kretsar** (bara sajtadmin):
- Hantera regionala förbund
- Tilldela kretsadministratörer

## Riksinstruktörer

Under fliken **Riksinstruktörer** (bara sajtadmin):
- Per område (Syd / Väst / Öst / Nord) ser du antalet utnämnda mot målet (2 / 2 / 2 / 3) och en lista över aktuella personer med datum och certifikatnummer.
- Klicka **"Tilldela ny"** för att utfärda en Riksinstruktörscertifiering och utnämna till område i ett steg. Inget "Certifierad av"-fält behövs — sajten registrerar att SPSF utfärdat certifieringen.
- **Återkalla** certifiering eller **avutnämn** från ett område via knapparna i listan.

Bootstrap-flöde för en ny installation: börja här och tilldela första Riksinstruktörerna. Därefter kan de utfärda Kretsinstruktörscertifieringar via kretsens admin, och Kretsinstruktörer kan utfärda Föreningsinstruktör/Vapenkontrollant/Banläggare via klubbarnas admin.

Se separat dokumentation: [Instruktörer och certifieringar](instructors.md).

## Statistik

Under fliken **Statistik** (bara sajtadmin):
- **Antal medlemmar**, nya medlemmar denna månad
- **Aktiva klubbar** och klubbar med senaste aktivitet
- **Tävlingsstatistik** — antal tävlingar i år, fördelning per disciplin
- **Träningsmatcher** — totalt och senaste 30 dagarna
- **Besökarstatistik** — unika besökare per dag (senaste 30 dagar) och per vecka (senaste 53 veckor). Bottar och statiska resurser räknas inte.
- **Diagram:**
  - Nya medlemmar per månad
  - Medlemmar per krets
  - Medlemmar per klubb
  - Registreringstrend
  - Aktiva/inaktiva medlemmar
