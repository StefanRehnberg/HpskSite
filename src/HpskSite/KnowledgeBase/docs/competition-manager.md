---
title: Tävlingsledare - Hantera resultat och startlistor
roles: [competition-manager]
features: [result-entry, start-lists, competition-management, class-merging, registration-management]
---

# Tävlingsledare

Som tävlingsledare har du ansvar för att hantera en specifik tävling — från startlistor till resultatinmatning och publicering.

## Hur du blir tävlingsledare

En administratör eller klubbadmin tilldelar dig som tävlingsledare på en specifik tävling.

## Hantera tävlingen

Öppna tävlingen och klicka **"Administrera tävling"**. Här har du tillgång till flera flikar beroende på tävlingstyp.

### Redigera tävlingsinfo och lägga till bilder

Klicka **"Redigera"** för att öppna redigeringsformuläret. I **beskrivningsfältet** finns en rik texteditor där du kan:
- Formatera text (rubriker, fetstil, kursiv, listor, länkar)
- **Ladda upp bilder** — klicka på bildikonen i verktygsfältet och välj en bildfil från din dator (JPG, PNG, GIF, WebP, max 5 MB)

**Tips för inbjudningar:** Om du vill visa en inbjudningsbild (t.ex. en inskannad traditionell inbjudan), ladda upp den direkt i beskrivningsfältet. Det finns **ingen separat PDF-uppladdning** för vanliga tävlingar — den funktionen finns bara för externa tävlingar (annonser).

**Alternativ för detaljerade inbjudningar:** Om du behöver en mer utförlig inbjudningssida med flera bilder, utökad information och en "Anmäl dig här"-länk, kan din klubbadmin skapa ett **evenemang** på klubbsidan. Evenemanget får en egen landningssida med plats för bilder, textblock och anmälningslänk till tävlingen.

### Anmälningar

- Se alla anmälda deltagare med namn, klubb och klass
- Se betalningsstatus och **markera betalningar som betalda**
- Hantera anmälningslistan

### Anmälningsavgifter

Vid redigering av tävlingen finns flera avgiftsfält:

- **Anmälningsavgift (individuell)** — grundavgift per anmäld klass
- **Junioravgift** — valfri. Om satt (> 0) används den för juniorklasser (IDn som innehåller `_Jun`, samt Springskytte-åldersklasser `jun`, `15`, `18`) i stället för grundavgiften. 0 kr = samma som grundavgiften
- **Lagavgift** / **Stafettavgift** — flat avgift per lag, används bara för lag-/stafettanmälningar
- **Anmälningsavgift för Deltävling** — tillägg för skyttar som kryssar i deltävlingen. Välj mellan:
  - **Per anmäld klass** — tillägget läggs till varje anmäld klass (t.ex. 2 klasser × 30 kr = 60 kr extra)
  - **En gång per anmälan** — tillägget tas ut en gång oavsett antal klasser (t.ex. engångsavgift till arrangören)

Avgifterna listas automatiskt under **Anmälningsdetaljer** på tävlingssidan så skyttar ser exakt vad som gäller. Betalningsdialogen visar också en separat rad om deltävlingsavgift är inkluderad.

### Startlistor

Generera och publicera startlistor:
1. Klicka **"Generera startlista"**
2. Konfigurera inställningar:
   - **Lagformat** (om lagtävling) — mixade, separerade, kombinerade
   - **Max antal skyttar per skjutlag**
   - **Första starttid** och **startintervall** (i minuter)
   - **Vapengruppernas startordning** (valfritt)
3. Förhandsgranska startlistan
4. Klicka **"Publicera startlista"** för att göra den synlig för deltagarna

Du kan **omnumrera** och **regenerera** startlistan om det behövs, t.ex. efter sena anmälningar.

### Resultatinmatning

Beroende på tävlingstyp:

**Precision:**
- Skriv in poäng serie för serie för varje deltagare
- X-träffar registreras separat
- Finaler hanteras separat om de är aktiverade

**Fältskytte:**
- Resultat matas in station för station
- Varje station har egna positioner och mål

**Springskytte:**
- Tidsbaserade resultat
- Klass C: träff/miss per station
- Klass A: zonpoäng 0-3

### Klasssammanslagning

Om en skytteklass har färre än 5 deltagare kan du slå ihop klasser:
1. Systemet visar vilka klasser som har för få deltagare
2. Välj vilka klasser som ska slås ihop
3. Den sammanslagna klassen får ett kombinerat namn (t.ex. "A+B")

### Publicera resultat

1. Kontrollera att alla resultat är inmatade
2. Klicka **"Publicera resultat"**
3. Resultaten blir synliga för alla besökare på tävlingssidan
