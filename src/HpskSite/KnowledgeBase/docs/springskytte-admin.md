---
title: Springskytte - Tävlingshantering
roles: [competition-manager, club-admin, admin]
features: [springskytte-management, result-entry, start-list-generation]
---

# Springskytte — Tävlingshantering

Springskytte har ett eget hanteringsgränssnitt som skiljer sig från Precision och andra discipliner.

## Redigera tävling

Springskytte-tävlingar har unika inställningar:
- **Vapengrupper** — A (Tavla, ring 1-4) och/eller C (Fällmål)
- **Ålders-/könsklasser** — kryssa i vilka klasser som ska vara tillgängliga (Junior/Senior/Veteran med kön och åldersgrupp). Knapparna **"Välj alla"** / **"Avmarkera alla"** förenklar
- **Stationer/Serier** — Klass C är alltid 6 stationer. Klass A har konfigurerbar 1-6 serier
- **Tävlingsomfattning** — Svenskt Mästerskap, Landsdelsmästerskap, Kretsmästerskap eller Klubbmästerskap (påverkar medaljberäkning)
- **Deltävling** — valfritt namn för en deltävling (t.ex. "Svenska Polismästerskapet 2026")

## Startlistor

Springskytte stödjer **flera startlistor** per tävling.

### Skapa startlista
1. Klicka **"+ Ny startlista"**
2. Namnge listan (t.ex. "Vapengrupp A")
3. Kryssa i vilka klasser som ingår (klasser som redan tillhör en annan lista är överkryssade)
4. Ställ in:
   - **Första starttid**
   - **Intervall** (MM:SS mellan starter)
   - **Paus efter antal** (t.ex. var 10:e skytt)
   - **Pauslängd** (MM:SS)
5. Klicka **"Generera"**

Startlistan visas som en tabell med starttid, namn, klubb, vapenklass och klass. Pauser visas som gula rader.

### Numrera startnummer

Knappen **"Numrera startnummer"** tilldelar löpande nummer per vapengrupp över alla genererade listor. **"Återställ"** nollställer till listlokal numrering.

### Resultatskydd

Om resultat redan finns visas en varning innan startlistan ändras: "OBS! Det finns redan registrerade resultat..."

## Resultatinmatning

Två flikar beroende på vapengrupp:

### Vapengrupp A — Sekventiell navigering

Navigera mellan skyttar med pilar. Per skytt:
- **Tider:** Starttid (från startlista), Måltid (ange klockslag vid mål), Löptid (beräknas automatiskt)
- **Skjutresultat:** Per mål, ange antal skott i varje zon: Ring 1, Ring 2, Ring 3, Ring 4, Bom
- Skjutpoäng beräknas automatiskt
- **Knappar:** "Spara", "Hoppa över", "Rensa resultaten"
- Automatiskt hopp till nästa skytt efter sparande

### Vapengrupp C — Banregistrering (mobil)

Optimerad för användning på banan med mobilen:
- Ange **startnummer** och klicka **"Hämta"** för att ladda skytten
- **6 stationer × 5 skott** visas som stora knappar (52×52px)
- Tryck för att toggla: **"-"** (ej satt) → **"T"** (träff, grön) → **"B"** (miss, röd)
- Varje station har en egen **spara-knapp** — efter sparande låses stationens knappar
- **Straffmultiplikator:** "×1 Normal" eller "×2 Märkestagning"
- **Tider:** Starttid (automatisk), Löptid, Måltid (med **"Nu"**-knapp för att fånga aktuell tid)
- Full bredd **"Spara resultat"**-knapp längst ner
- **"Lås skärm"** förhindrar oavsiktlig navigering

Senaste 5 sparade resultat visas för snabb kontroll.

### Dataverifiering

Varje sparning verifieras genom att systemet läser tillbaka det sparade resultatet och jämför med det inskickade. Vid avvikelse visas: "VARNING: Verifieringsfel — sparade skott skiljer sig från inskickade."

## Beräkna slutresultat

Klicka **"Uppdatera"** för att beräkna och publicera officiella resultat. Systemet:
1. Hämtar alla resultat
2. Beräknar medaljer (silver: topp 1/9, brons: topp 1/3 av klassen)
3. Sorterar med tiebreaker-regler
4. Publicerar resultaten
