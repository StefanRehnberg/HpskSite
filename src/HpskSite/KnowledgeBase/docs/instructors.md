---
title: Instruktörer och certifieringar (Föreningsinstruktör, Kretsinstruktör, Riksinstruktör, Vapenkontrollant, Banläggare)
roles: [member, club-admin, regional-admin, site-admin]
features: [certifications, instructors, foreningsinstruktor, kretsinstruktor, riksinstruktor, vapenkontrollant, banlaggare, certifieringar]
---

# Instruktörer och certifieringar

Sajten hanterar fem certifieringsbaserade roller som är registrerade hos Svenska Pistolskytteförbundet (SPSF):

- **Föreningsinstruktör** — utbildar nybörjare och håller kurser för Pistolskyttekortet. Varje klubb ska ha minst en.
- **Kretsinstruktör** — utbildar Föreningsinstruktörer och signerar Pistolskyttekort. Varje krets bör ha minst två.
- **Riksinstruktör** — utbildar Kretsinstruktörer och är källa till sanning för regelverk. Mål: 2 per område (Syd, Väst, Öst), 3 i Nord.
- **Vapenkontrollant** — kontrollerar att vapen är godkänt för skjutning. Certifieringen följer personen.
- **Banläggare** — designar fältskyttebanor (skjutstationer, tavlor, avstånd, säkerhet). Certifieringen följer personen.

## Certifiering vs. utnämning

En **certifiering** är en personlig kompetens — den utfärdas av rätt instruktörsnivå och följer personen även om hen byter klubb eller krets. För att faktiskt vara klubbens/kretsens **utnämnda** Föreningsinstruktör, Kretsinstruktör eller Riksinstruktör krävs dessutom en utnämning av styrelsen i förening/krets respektive SPSF. För Vapenkontrollant och Banläggare är certifieringen själv tillräcklig — ingen separat utnämning behövs.

En person kan flytta krets och ta med sin Kretsinstruktörscertifiering. Hen utnämns då på nytt av sin nya krets för att få utnämningen där.

## Hitta certifieringar på sajten

- **Mina egna certifieringar:** Min Profil > Profil-fliken > "Mina certifieringar" (visar typ, datum, eventuellt förfallodatum och certifikatnummer)
- **Klubbens instruktörer:** Klubbens sida > Medlemmar-fliken (kräver inloggning). Visar Föreningsinstruktörer, Vapenkontrollanter och Banläggare i klubben.
- **Kretsinstruktörer:** Kretsens sida > Om Kretsen-fliken (kräver inloggning). Visar utnämnda Kretsinstruktörer i kretsen.

Listorna är inte öppna för icke-inloggade besökare.

## Hantera certifieringar och utnämningar

### Klubbadministratör

Klubb-sidans Admin > **Certifieringar**-fliken har tre kort:
- **Föreningsinstruktörer** — klubbens utnämnda lista. Klicka **"Tilldela"** för att lägga till. Du kan också återkalla eller avutnämna.
- **Vapenkontrollanter** — klubbmedlemmar med aktiv Vapenkontrollantscertifiering.
- **Banläggare** — samma fast för Banläggare.

Om du själv är Krets- eller Riksinstruktör kan du utfärda nya certifieringar. Annars visar dropdownen **"Certifierad av"** endast medlemmar med behörighet att utfärda — välj rätt utbildare.

Om du saknar Föreningsinstruktör visas en röd notis i Statistik-fliken: **"Klubben saknar Föreningsinstruktör — krav från SPSF."**

### Kretsadministratör

Kretsens sida > Admin > **Certifieringar**-fliken har:
- **Kretsinstruktörer** högst upp — utnämn och avutnämn för kretsen. Som kretsadmin kan du även **utfärda** Kretsinstruktörscertifieringar utan att välja en specifik utbildare — sajten registrerar att SPSF har utbildat personen och att kretsen utnämner dem.
- **Vapenkontrollanter / Banläggare i kretsen** — read-only översikt.
- **Föreningsinstruktörer per klubb** — längst ned. Lista över alla klubbar i kretsen. Klubbar **utan** utnämnd Föreningsinstruktör visas i **rött** med en varningsikon — då vet du var du behöver hjälpa till med utbildning.

Statistik-fliken visar samma röda lista som en notisruta plus diagrammet "Föreningsinstruktörer per klubb" där klubbar utan instruktörer markeras med röd stapel.

### Sajtadministratör

Admin-sidan > **Riksinstruktörer**-fliken. Per område (Syd / Väst / Öst / Nord) ser du:
- Antalet utnämnda mot målet (2 för Syd/Väst/Öst, 3 för Nord).
- Aktuella personer med datum och certifikatnummer.

Klicka **"Tilldela ny"** för att utfärda en Riksinstruktörscertifiering och utnämna till område i ett steg. Inget "Certifierad av"-fält behövs — sajten registrerar att SPSF utfärdat certifieringen.

## Vem får utfärda vad?

| Certifiering | Får utfärdas av |
|---|---|
| Riksinstruktör | Sajtadmin |
| Kretsinstruktör | Aktiv Riksinstruktör för kandidatens område, eller den utnämnda kretsadminen för kandidatens krets, eller sajtadmin |
| Föreningsinstruktör | Aktiv Krets- eller Riksinstruktör, eller sajtadmin |
| Vapenkontrollant | Aktiv Krets- eller Riksinstruktör, eller sajtadmin |
| Banläggare | Aktiv Krets- eller Riksinstruktör, eller sajtadmin |

Sajtadmin kan alltid utfärda allt. För Riks- och Kretsinstruktörscertifieringar är fältet **"Certifierad av"** dolt — utfärdaren är SPSF, inte en namngiven person på sajten.

## Förfallodatum

En certifiering kan förfalla. Om du anger ett förfallodatum vid utfärdandet räknas certifieringen som inaktiv när datumet passerats. Lämna fältet tomt för en certifiering som aldrig förfaller. Datumväljaren använder svenskt format (ÅÅÅÅ-MM-DD).

## Statistik som hjälper dig hålla koll

- **Klubbens Statistik-flik:** röd notis om Föreningsinstruktör saknas + summering "N F · M V · K B" (Föreningsinstruktörer / Vapenkontrollanter / Banläggare).
- **Kretsens Statistik-flik:** lista över klubbar utan Föreningsinstruktör (SPSF-krav), diagram "Föreningsinstruktörer per klubb" med röda staplar för 0, summering "Krets · Förenings · Vapen · Bana", samt en varning om kretsen har färre än 2 utnämnda Kretsinstruktörer.
- **Sajtens Admin-Statistik:** tävlings- och medlemsstatistik per krets, samt besökarstatistik per dag och per vecka (lades till parallellt med certifieringsmodulen).

## Vanliga frågor

**Min klubb har ingen Föreningsinstruktör — hur löser vi det?**
Kontakta er kretsinstruktör. Hen kan hålla en utbildning för en av era betrodda medlemmar och sedan utfärda certifieringen via klubbens Admin > Certifieringar > Tilldela.

**Jag är ny Kretsinstruktör — vem registrerar min certifiering?**
Den utnämnda kretsadminen i din krets kan registrera certifieringen i kretsens Admin > Certifieringar. Om kretsen ännu inte har en utnämnd Kretsinstruktör kan en Riksinstruktör för ert område också göra det, eller sajtadmin.

**Jag flyttar till en annan krets — behåller jag min certifiering?**
Ja. Certifieringen är personlig och följer dig. Den nya kretsen behöver däremot **utnämna** dig på nytt för att du ska räknas som deras utnämnda Kretsinstruktör.

**Vad händer om en certifiering förfaller?**
Den markeras som inaktiv automatiskt och tas bort från utnämningar. För att fortsätta som t.ex. Föreningsinstruktör behöver du gå en uppdateringskurs och få en ny certifiering registrerad.
