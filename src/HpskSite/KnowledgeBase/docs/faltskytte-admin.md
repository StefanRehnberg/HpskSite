---
title: Fältskytte - Tävlingshantering
roles: [competition-manager, skjutledare, club-admin, admin]
features: [faltskytte-management, station-config, patrol-generation, result-entry, target-catalog]
---

# Fältskytte — Tävlingshantering

Fältskytte har ett omfattande hanteringsgränssnitt med stationskonfiguration, patrullhantering och mobil resultatinmatning.

## Stationskonfiguration

Konfigurera varje stations skjutparametrar:

### Per station
- **Skjuttid** (sekunder)
- **Utgångsläge** — Stående/Knästående/Sittande/Liggande/Valfri
- **Vapenläge** — 45 grader/Riktning tillåten
- **Stödhand** — Ej stödhand/Stödhand tillåten
- **Max skott per figur** (i Normal-läge)

### Målgrupper
Varje station har en eller flera målgrupper (A, B, C...). Per målgrupp:
- Figurer från figurkatalogen eller direktuppladdade bilder
- Antal mål per figur (t.ex. ballongmål har flera)
- Beteende: Fast, Framsvängande, Bortsvängande
- Tidsinställningar för rörliga mål
- Gruppfoto

### Vapenklasser
- Flikar per vapenklass (C, B, A, R, M)
- **"Kopiera från:"** — kopiera konfiguration från en annan vapenklass med proportionell tidsskalning
- **"Länka..."** — länka två vapenklasser för identisk konfiguration
- **"Avlänka"** — bryt länken och behåll en kopia

### Utskrift
Varje station har en **"Skriv ut"**-knapp som genererar ett stationskort med förutsättningar, figurer, foton och QR-kod.

## Patrullhantering

### Generera patruller
1. Klicka **"Generera patruller"**
2. Välj vilka vapenklasser som ska ingå
3. Ställ in:
   - **Patrullstorlek** (standard 6)
   - **Starttid**
   - **Intervall** (minuter mellan patruller)
   - **Tid mellan vapenklasser** (minuter)
4. Generera — patruller läggs till befintliga (tar inte bort tidigare)

Generera separat för varje vapenklassgrupp med olika tider.

### Manuell hantering
- **"Skapa patrull"** — skapa en enskild patrull med vapengrupp och position
- **Dra och flytta** skyttar mellan patruller
- **Bulkoperationer** — markera flera skyttar med kryssrutor och flytta eller ta bort
- **Redigera starttid** — klicka på pennaikonen på patrullen
- **Sök** efter anmälda skyttar som inte är placerade

### Rullande start
Om aktiverat skapas patruller automatiskt vid anmälan. Knapparna för generering och manuellt skapande döljs.

### Publicera
- **"Publicera"** / **"Avpublicera"** — gör patrullistan synlig/osynlig för allmänheten
- **"Skriv ut"** — skriv ut patrullistan

### Resultatskydd
Om resultat redan finns visas en varning innan ändringar: "OBS! Det finns redan registrerade resultat..."

## Resultatinmatning (mobil)

Inmatning sker station för station, optimerad för mobilen.

### Flöde
1. **Välj station** (auto-vald om du kom via QR-kod)
2. **Välj patrull** — patruller visas som kort med vapengrupp, starttid och antal. Grön bock om alla resultat är sparade
3. **Upprop** — kryssa i vilka skyttar som är närvarande. Se stationens förutsättningar
4. **Resultatinmatning** — navigera mellan skyttar med pilar

### Per skytt
- **Figurknappar** — tryck på stora knappar (0-6) för varje figur och mål
- **Poängräkning** — figurmål med zonpoäng öppnar ett numeriskt tangentbord (0-50)
- **Löpande total** visas högst upp (blir röd om > 6)
- **"SPARA RESULTAT"** — stor grön knapp
- **Omskjutning** — **"Registrera omskjutning"** toggle med spårning över alla stationer
- **Osparade ändringar** — varning vid navigering utan att spara

### Åtkomst via QR-kod
Skanna QR-koden på stationskortet → hamnar direkt på rätt station med resultatformuläret.

**Roller som kan mata in resultat:** Tävlingsledare, skjutledare (för klubbens tävling), klubbadmin, sajtadmin.

## Figurkatalog

Under fliken **"Figurkatalog"** på adminpanelen (eller via stationskonfiguratorn).

### Se figurer
- Sökbar gallerivy med miniatyrbilder
- Klicka för att se stor bild, färgvarianter och avstånd per vapenklass

### Redigera figurer (sajtadmin och kretsadmin)
- Skapa ny figur med namn, avstånd per vapenklass (A, R, B, C i meter) och antal mål
- Lägg till färgvarianter med namn, färg och bild
- Ta bort figurer och varianter

## Resultathantering

### Uppdatera resultat
Klicka **"Uppdatera"** för att beräkna resultatlistan med placeringar och medaljer.

### Klasssammanslagning
Om klasser har < 5 deltagare:
1. Klicka **"Sammanslagning..."**
2. Systemet analyserar och föreslår sammanslagningar
3. Välj vilka som ska slås ihop
4. **"Skapa med sammanslagning"** eller **"Skapa utan sammanslagning"**

### Publicera
- **"Officiell"** — markerar resultaten som slutgiltiga (synliga för alla)
- **"Preliminär"** — återgår till preliminär status
- **"Skriv ut"** — skriv ut resultatlistan

### Deltävling
Om tävlingen har en deltävling visas ett separat resultatkort för deltävlingens resultat.
