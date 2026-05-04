---
title: Kretsadministratör - Regional administration
roles: [regional-admin]
features: [regional-overview, cross-club-management, member-approval, club-admin-assignment, certifieringar, kretsstatistik, kretsrekord, kretsmästare]
---

# Kretsadministration

Som kretsadministratör har du översikt och administrationsrättigheter för alla klubbar i din region (krets).

## Adminpanelen

Du har tillgång till den centrala **adminpanelen** (samma som sajtadmin) med flikar för Tävlingar, Serier, Användare, Klubbar, Fakturor och Figurkatalog — men begränsat till klubbar och tävlingar i din krets.

## Behörigheter

Du har samma rättigheter som en klubbadmin, men för **alla klubbar i din krets**. Det innebär att du kan:
- **Godkänna nya medlemmar** för alla klubbar i kretsen — särskilt viktigt om en klubb saknar klubbadmin
- **Tilldela klubbadministratörer** — utse klubbadmins för klubbar i din krets
- Se medlemmar i alla klubbar i kretsen
- Hantera tävlingar och serier i regionen
- Hantera fakturor och betalningar
- Hjälpa till med saker som klubbadmins normalt sköter

## Regionssida

Gå till din regions sida för att se:
- **Alla klubbar** i kretsen
- **Tävlingar** som arrangeras i regionen
- **Evenemang** på regional nivå

### Redigera regionssidan

Du kan redigera din regionssidas innehåll:
- **Välkomsttitel** och **välkomsttext**
- **Om-text** — beskrivning av kretsen
- **Kontaktinformation**
- **Bannerbild** och **logotyp**

### Hantera regionala evenemang

Du kan skapa, redigera och ta bort evenemang på regionsnivå. Samma funktionalitet som klubbevenemang — inklusive fullständiga landningssidor med innehållsblock, bilder och snabblänkar.

### Utvalda tävlingar

Du kan välja vilka tävlingar och serier som ska lyftas fram på din regionssida via en väljarmodal.

## När är detta användbart?

- När en klubb saknar klubbadmin och nya medlemmar behöver godkännas
- När en klubb behöver en ny klubbadmin tilldelad
- När tävlingar involverar flera klubbar i samma region
- Som backup när en klubbadmin inte är tillgänglig

## Certifieringar — instruktörer i kretsen

På din regions sida > Admin > **Certifieringar**-fliken hanteras kretsens SPSF-registrerade roller:

- **Kretsinstruktörer** högst upp — utnämn och avutnämn de som är certifierade och ska representera kretsen. Du kan även **utfärda** Kretsinstruktörscertifieringar — sajten registrerar att SPSF utbildat personen och att kretsen utnämner dem. Inget separat "Certifierad av" behövs.
- **Vapenkontrollanter / Banläggare i kretsen** — översikt över aktiva certifieringar bland kretsens medlemmar (read-only).
- **Föreningsinstruktörer per klubb** längst ned — visar **alla** klubbar i kretsen. Klubbar utan utnämnd Föreningsinstruktör listas i **rött** så du ser var det behövs en utbildning.

Se separat dokumentation: [Instruktörer och certifieringar](instructors.md).

## Kretsrekord

På regionssidan finns fliken **"Rekord"** (synlig för inloggade medlemmar). Som kretsadmin ser du knappen **"Lägg till rekord"** och kan ta bort enskilda rekord.

- Samma datamodell som klubbrekord: Precisionsskjutning, Magnumprecision, Militär snabbmatch — individuellt och lag.
- Skytt väljs från en autocomplete som omfattar **alla medlemmar i kretsens klubbar** (namn, klubb och Pistolkortnr filtrerar listan). Fritextsnamn fungerar också för icke-medlemmar.
- Slår ett nytt rekord det förra flippas det förra automatiskt till historik. Tas det aktuella rekordet bort befordras föregående post.

Se separat dokumentation: [Rekord och mästartitlar](records-and-champions.md).

## Kretsmästare (mästartitlar)

På regionssidan > Admin > **Mästare**-fliken registrerar du årets kretsmästare:

- En post per (år, disciplin, klass, individuellt/lag). Manuell inmatning — sajten räknar inte ut vinnare.
- Senaste året visas som "regerande kretsmästare" i höger kolumn på regionens hem-flik. Admin-fliken visar hela historiken (alla år grupperade per klass).
- Du kan **backfilla** äldre årtal en post i taget. Skytten kan väljas från medlemspoolen i kretsen eller anges som fritext.
- Tävlingsnamn och datum är frivilliga.
- Kretsmästartitlar listas i medlemsdetaljmodalen på respektive klubbs medlemslista (KrM-badge), tillsammans med rekord och klubbmästartitlar.

## Statistik

På din regions sida > Admin > **Statistik**-fliken får du översikten över hela kretsen:

- **Sammanfattningskort** — antal klubbar, medlemmar, nya medlemmar senaste 30 dagar, tävlingar i år.
- **Medlemmar per klubb** (horisontellt stapeldiagram) — vilka klubbar är störst och minst.
- **Klubbar som behöver hjälp** — listor över:
  - Klubbar utan Föreningsinstruktör (SPSF-krav, röd)
  - Klubbar utan publicerat event senaste 90 dagar
  - Klubbar utan klubbadmin
  - Klubbar utan Skjutledare
- **Föreningsinstruktörer per klubb** (horisontellt stapeldiagram) — klubbar utan instruktörer markeras med röd stapel.
- **Nya medlemmar per månad** (12 månader) och **tävlingar per klubb (i år)**.
- **Träningsmatcher per klubb (30 d)** — vilka klubbar är aktiva.
- **Topp 5 tillväxt (12 mån, %)** och **Mest aktiva klubbar** — för att lyfta fram positiva exempel snarare än rankning.
- **Notiser** — bl.a. varning om kretsen har färre än 2 utnämnda Kretsinstruktörer.

Klicka **"Uppdatera"** för att hämta färska siffror.
