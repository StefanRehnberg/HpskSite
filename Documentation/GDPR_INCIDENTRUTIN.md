# Rutin vid personuppgiftsincident

**Organisation:** Hallands Pistolskyttekrets (HPSK), org.nr 849202-2416 · **Tjänst:** Pistol.nu
**Ansvarig:** _[fyll i namn + roll i styrelsen]_ · **Kontakt:** admin@pistol.nu
**Senast uppdaterad:** 2026-06-06 · **Version:** 1.1

> En **personuppgiftsincident** är en säkerhetsbrist som leder till oavsiktlig eller olaglig förstöring,
> förlust, ändring eller obehörigt röjande av/åtkomst till personuppgifter. Exempel: obehörig kommer åt
> konton eller databasen, uppgifter mejlas till fel mottagare, dataförlust hos driftleverantören,
> förlorad/stulen enhet med åtkomst.

## Roller (kom ihåg: HPSK har två hattar)

- **För föreningarnas uppgifter är HPSK personuppgiftsbiträde** → HPSK ska **informera den/de berörda
  föreningarna utan onödigt dröjsmål**. Föreningen (personuppgiftsansvarig) ansvarar för ev. anmälan
  till IMY och för att informera de registrerade.
- **För HPSK:s egna uppgifter är HPSK personuppgiftsansvarig** → HPSK **anmäler själv till IMY inom
  72 timmar** om incidenten sannolikt medför en risk för de registrerade, och informerar vid hög risk
  de registrerade.

## Steg-för-steg

1. **Upptäck & larma (omedelbart).** Den som upptäcker något kontaktar genast ansvarig ovan. Logga
   tidpunkt för upptäckt – 72-timmarsklockan startar när HPSK *fått kännedom*.
2. **Begränsa skadan.** Stäng av drabbade konton, byt lösenord/nycklar, kontakta vid behov Simply.com.
   Bevara loggar och bevis – ändra inte mer än nödvändigt.
3. **Bedöm (snabbt).** Vad har hänt, vilka uppgifter och hur många personer berörs, vilka risker för de
   registrerade? Notera om personnummer eller andra känsliga uppgifter ingår (höjer risken).
4. **Informera rätt part.**
   - Berör det en **förenings** uppgifter → meddela föreningens administratör/kontakt **utan onödigt
     dröjsmål** med den information som finns (vad, omfattning, åtgärder). Mall nedan.
   - Berör det **HPSK:s egna** uppgifter och innebär sannolik risk → **anmäl till IMY inom 72 timmar**
     (imy.se → anmäl personuppgiftsincident). Vid hög risk: informera även de registrerade.
5. **Åtgärda & följ upp.** Rätta grundorsaken, vidta förebyggande åtgärder.
6. **Dokumentera ALLT** i incidentloggen nedan – även incidenter som inte anmäls (dokumentationskrav
   enligt art. 33.5).

> HPSK och de anslutna klubbarna tillhör **Svenska Pistolskytteförbundet (SPSF)**, en frivillig
> försvarsorganisation utanför Riksidrottsförbundet. RF:s incidentrutin gäller därför **inte** – anmälan
> görs direkt till **IMY** enligt ovan. Kontrollera om SPSF har egen vägledning att även följa.

## Incidentlogg

| Datum upptäckt | Beskrivning | Berörda uppgifter / antal | Roll (biträde/ansvarig) | Åtgärder | Informerad förening / IMY (datum) | Status |
|---|---|---|---|---|---|---|
| | | | | | | |

## Mall – meddelande till berörd förening

> Hej,
> Vi vill informera er om en personuppgiftsincident som rör uppgifter ni är personuppgiftsansvariga för
> på Pistol.nu.
> **Vad som hänt:** …
> **När:** upptäcktes [datum/tid].
> **Vilka uppgifter/personer som berörs:** …
> **Åtgärder vi vidtagit:** …
> **Vår bedömning av risk:** …
> Som personuppgiftsansvariga ansvarar ni för att bedöma anmälan till IMY (inom 72 timmar) och att vid
> behov informera era medlemmar. Vi bistår med all information ni behöver.
> Kontakt: admin@pistol.nu

## Rutin vid begäran om radering ("rätten att bli glömd", art. 17)

En medlem kan begära att få sina uppgifter raderade, eller en förening kan instruera om radering. Rätten
är **inte absolut** – uppgifter som måste bevaras enligt lag eller som bevaras på berättigat intresse får
behållas. Gör så här:

1. **Radera medlemmen i Umbraco.** Detta tar bort den centrala identitetsposten – **namn, e-postadress,
   personnummer och samtliga medlemsegenskaper**. Rader i databasen som endast är kopplade via ett
   `MemberId` (t.ex. träningsresultat) blir därmed **anonymiserade** eftersom kopplingen till personen
   är borta.
2. **Behåll det som lagligt ska/får bevaras** (raderas alltså INTE):
   - Betalnings- och bokföringsunderlag – **7 år** enligt bokföringslagen.
   - **Officiella tävlingsresultat** – bevaras på berättigat intresse (resultathistorik, rekord,
     mästartitlar). Innehåller namn, klubb, klass och resultat – **inte personnummer**.
3. **Rensa eventuella fristående namnuppgifter** som *inte* omfattas av en laglig grund ovan – t.ex.
   fritext-/anteckningsfält eller gästnamn i träningsmatcher.
4. **Bekräfta** åtgärden till den som begärt radering (eller till föreningen) **senast inom en månad**
   (art. 12.3), och informera om vad som eventuellt behållits och på vilken grund.
5. **Dokumentera** begäran och åtgärden i loggen nedan.

### Logg över raderingsbegäranden

| Datum begäran | Vem (medlem/förening) | Åtgärd (raderat / behållet + grund) | Bekräftat (datum) | Handläggare |
|---|---|---|---|---|
| | | | | |

---

_Förvaras hos HPSK:s styrelse tillsammans med registerförteckningen._
