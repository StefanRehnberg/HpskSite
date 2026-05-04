---
title: Tävlingsserier - Beräkningsstrategier
roles: [club-admin, regional-admin, admin]
features: [series, calculation-strategies, placement-points]
---

# Tävlingsserier — Beräkningsstrategier

En tävlingsserie samlar flera tävlingar och beräknar en totalställning. Administratörer väljer beräkningsstrategi via **"Konfigurera strategi"** i serieredigeringen.

## Individuella strategier

### Individuellt totalsumma

Summerar alla tävlingsresultat. Varje tävling räknas — inget stryks.

**Inställningar:**
- **Placeringspoäng** — tre lägen:
  - **Av (råpoäng)** — använder skyttens faktiska totalpoäng
  - **Dynamisk** — 1:a = antal deltagare i klassen, 2:a = antal-1, osv. ner till 1
  - **Fast poängtabell** — poäng tilldelas efter en vald tabell (t.ex. F1-poäng: 25, 20, 16, 13, 11...)

### Individuellt bästa N

Bara de N bästa tävlingsresultaten räknas per skytt. Övriga visas överstrukna i tabellen.

**Inställningar:**
- **Antal bästa resultat** — hur många tävlingar som räknas (t.ex. 4 av 6)
- **Placeringspoäng** — samma tre lägen som ovan

Urvalet "bästa N" görs efter eventuell placeringspoängskonvertering.

### Individuellt antal segrar

Räknar hur många gånger varje skytt kommit 1:a i sin klass. Flest segrar vinner serien.

En seger delas om flera skyttar har identisk totalpoäng OCH X-antal i samma tävling.

**Inga inställningar.**

### Individuellt fasta poäng

Varje tävling tilldelas poäng enligt en fast tabell baserat på placering.

**Tillgängliga tabeller:**
- F1-poäng: 25, 20, 16, 13, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1
- Topp 9: 12, 10, 8, 6, 5, 4, 3, 2, 1
- Topp 8: 10, 8, 6, 5, 4, 3, 2, 1
- Topp 6: 7, 5, 4, 3, 2, 1
- Topp 5: 5, 4, 3, 2, 1
- Prispall: 3, 2, 1
- Egen tabell (ange egna siffror)

### Individuellt dynamiska poäng

Poängen baseras på antalet deltagare i klassen per tävling. 1:a = antal deltagare, 2:a = antal-1, osv. Rättvisare när deltagarantal varierar mellan tävlingar.

**Inga inställningar.**

## Lagstrategi

### Klubblag bästa X

Lagtävling per klubb. Varje tävling plockas varje klubbs N bästa skyttar och deras poäng summeras till klubbens tävlingsresultat.

**Inställningar:**
- **Max antal skyttar per klubb och tävling** — t.ex. 3 bästa räknas
- **Antal bästa deltävlingar** — 0 = alla räknas, annars stryks sämsta tävlingar
- **Gruppera per klass** — beräkna separat per skytteklass, eller kombinerat
- **Klubbseriepoäng** — tre lägen:
  - **Summa (råpoäng)** — klubbens tävlingspoäng summeras direkt
  - **Dynamisk placering** — klubbar rangordnas per tävling, 1:a = antal klubbar osv.
  - **Fast poängtabell** — klubbar tilldelas poäng enligt en tabell

Producerar **två sektioner** i resultatet: individuell ställning och klubbställning.

## Hur lika resultat hanteras

Vid lika serietotal jämförs i ordning:
1. Högst totalpoäng i serien
2. Flest X-antal totalt
3. **Senaste tävlingen avgör** — tävlingarna gås igenom bakifrån (senaste först), och den som har högst råpoäng i den första tävlingen där de skiljer sig vinner

Vid poängtilldelning med placering: skyttar med identisk totalpoäng OCH X-antal delar placeringen och får genomsnittet av de poäng deras positioner ger (heltalsdivision).

## Hur resultaten visas

Serieställningen visar en tabell per klass med:
- **Placering**, namn, klubb
- En kolumn per tävling (med tävlingens kortnamn)
- **Totalpoäng**

Resultat som inte räknas (strukna av "bästa N") visas med genomstrykning och dämpad text. Tävlingar man inte deltog i visar "-".

Administratörer kan klicka **"Beräkna om"** för att tvinga en omräkning (resultat cachelagras i 5 minuter).

## Om en skytt byter klass mitt i serien

Varje deltävling använder den klass skytten var anmäld i för just den tävlingen. Serieberäkningen läser dessa klasser som de är — den försöker inte slå ihop resultat från olika klasser för samma skytt.

**Det innebär:** om en skytt skjuter omg. 1–2 i C2 och omg. 3 och framåt i C3, så syns hen som **två rader** i serieställningen — en rad i C2-tabellen med poäng i omg. 1–2 (och "-" på resten), och en rad i C3-tabellen med "-" på omg. 1–2 och poäng från omg. 3 och framåt.

Det finns idag ingen automatisk eller manuell funktion för att flytta tidigare resultat till den nya klassen. Det här är ett känt specialfall som bevakas — kontakta administratör om det blir aktuellt i en pågående serie.
