# Claude Development Notes - pistol.nu

## Project Overview
Umbraco v16.2 project for pistol.nu (formerly HPSK) featuring member management, club administration, training system (Skyttetrappan), and competition management.

## Knowledge Base Maintenance
When making changes to **user-facing features** (views, controllers that affect UI/workflows, button labels, new features, removed features), check if the knowledge base at `src/HpskSite/KnowledgeBase/docs/` needs to be updated. The knowledge base is used by an AI chat assistant on the site to help users. Each doc has a `roles` frontmatter tag — update the role list if access control changes.

### AI-chattens sökning (2026-10-04)
Chatten skickar inte längre hela kunskapsbasen (~460 000 tecken, ~€0,18 per fråga mot
mistral-medium, 45+ s) utan de stycken som rör frågan. `KnowledgeChunker` delar varje dokument vid
`##`/`###` (582 stycken, median 560 tecken); `KnowledgeIndexService` gör dem till vektorer via
Mistrals embeddings-API (`mistral-embed`, härlett ur `AiChat:Endpoint`) och väljer de bästa inom
`RetrievalMaxChunks` (10) / `RetrievalMaxChars` (16 000), rollfiltrerat. Modellen får dessutom en
ämneslista över alla dokument.
- **Ändra kunskapsbasen som vanligt.** Vektorcachen nycklas på styckets TEXT, så bara ändrade stycken
  räknas om (efter kunskapsbasens 30-minuterscache). Cachen är filen
  `App_Data/AiChatIndex/embeddings-v1.json` — härledd, ~1 öre att bygga om, därför ingen tabell.
  `KnowledgeIndexWarmupHostedService` bygger indexet 90 s efter start.
- **Ett fel stoppar aldrig chatten:** sökningen returnerar null och frågan får hela kunskapsbasen.
  Det loggas som Warning. Claude/Gemini/Azure får alltid hela kunskapsbasen.
- Chattloggen har en `K:`-rad före `Q:` med de stycken frågan fick (eller `[hela kunskapsbasen]`).
  Läs den när ett svar är dåligt: hittades rätt stycke alls?
- Test: `KnowledgeRetrievalTests` (14).

## ⚖️ LAG: id-reglerna (beslutad av Stefan 2026-10-13 — gäller ALLTID, inga undantag)

Umbraco ger varje nod (sidor, tävlingar, medlemmar, dokumenttyper) ett nummer ur EN räknare, så ett
nod-id pekar alltid på exakt en sak. Våra egna tabeller har en räknare var: samma nummer kan vara en
anmälan, en händelse och en nod samtidigt. En global räknare för våra tabeller löser det INTE —
Umbraco fortsätter räkna (varje ny medlem är en nod) och krockar till slut ändå. Alla id-problem hittills
har haft en av två orsaker: ett id slogs upp på fel ställe, eller samma sort fanns på två ställen under
en flytt. Därför gäller tre regler, för varje sort, nu och framåt:

1. **Ett id följs alltid av sin sort.** Varje kolumn, adress, parameter, token, cachenyckel, ICS-UID,
   QR-kod och JSON-fält som bär ett id säger också vilken sort det är: ett kolumnnamn som bara kan
   betyda en sort (`CompetitionId`), ett sortfält bredvid (`SourceType`/`SourceId`, `OccasionKind`),
   ett prefix (`training:12`, `lag-34`) eller en adress som bara tar en sort (`/competitions/{id}/`).
   Ett nummer utan sort som tolkas som "standardsorten" är en bugg.
2. **Varje sort slås upp på ett enda ställe — sin store/tjänst.** Ett id som kommer utifrån (adress,
   formulär, JSON, kolumn) slås aldrig upp direkt med `Umbraco.Content(id)` / `GetById(id)` om inte
   sorten faktiskt lever som noder, och då alltid med aliaskontroll. Storen avgör var sorten finns
   (noder eller tabell) — anroparen vet det aldrig.
3. **Vid flytt från noder till tabell behåller raderna sina nod-id:n** (`IDENTITY_INSERT`), och
   tabellens räknare sätts över det högsta id:t sorten NÅGONSIN haft eller som någon pekar på
   (`IdentityFloor` med alla `IdSources`, papperskorgen inräknad), går aldrig bakåt och nollställs aldrig
   (`RESEED 0` är förbjudet). Efter flytten finns sorten bara i tabellen.

**Följder:**
- Inga negativa id:n, inga startvärden "långt upp", ingen global räknare. Positiva id:n överallt, så
  `id > 0` betyder "finns".
- Ny tabell som tar över noder → `IdentityFloor` + `IdSources` + en store + ett spärrtest mot nya noder.
- Ny referenskolumn till en sort → lägg den i sortens lista i **`Services/Hosting/TableIdSources.cs`**.
- ⚠️ **Flyttverktygen är BORTTAGNA (2026-10-20)** — alla sex lagringarna står på tabell i prod och noderna är
  raderade. `RegistrationMigration`, `ClubEventMigration`, `StartListMigration`, `CompetitionResultMigration`,
  `CompetitionMigration` (controllers + tjänster + planerare), nodraderingen, fakturaarkivets flytt och kortet
  i Underhåll finns inte längre; avsnitten längre ned som beskriver dem är historik. `…MigrationService.IdSources`
  i texten nedan = `TableIdSources`. Nodlagringarna och växlarna finns kvar.
- Kända avvikelser som ska bort (2026-10-13): ~~start- och resultatlistornas negativa id:n~~ (bytta
  på branchen `skalplan-fas-c` 2026-10-13); `featuredItems` (ingen läser den längre sedan C3 F3 — doctype-
  egenskapen kan tas bort i backoffice); gamla fakturanoders
  `registrationId` (försvinner med fakturaarkivet); närvaro-QR där ett nummer utan prefix tolkas som
  händelse (får prefix).

## Core Architecture Principles

### Data Storage Best Practices
- **NEVER use file system storage** for application data
- **Document Types** = Content pages AND club data entities (club, competition, etc.)
- **Member Types** = Only for actual members (hpskMember)
- **Content Service** = Manage clubs, competitions, events via IContentService
- **Member Service** = Manage members via IMemberService

### Umbraco Services Used
```csharp
IMemberService        // Member CRUD operations
IMemberGroupService   // Member group/role management
IMemberManager        // Current member authentication
IContentService       // Club and competition management
```

## Controller Architecture
→ **Hela avsnittet (69 rader): `Documentation/claude/01-controller-architecture.md`** — läs det innan du arbetar i det här området.

## Club System Architecture
→ **Hela avsnittet (170 rader): `Documentation/claude/02-club-system-architecture.md`** — läs det innan du arbetar i det här området.

## Member System
→ **Hela avsnittet (30 rader): `Documentation/claude/03-member-system.md`** — läs det innan du arbetar i det här området.

## Training System (Skyttetrappan)
→ **Hela avsnittet (182 rader): `Documentation/claude/04-training-system-skyttetrappan.md`** — läs det innan du arbetar i det här området.

## Training Scoring System
→ **Hela avsnittet (49 rader): `Documentation/claude/05-training-scoring-system.md`** — läs det innan du arbetar i det här området.

## Training Match System
→ **Hela avsnittet (70 rader): `Documentation/claude/06-training-match-system.md`** — läs det innan du arbetar i det här området.

## Competition System
→ **Hela avsnittet (2796 rader): `Documentation/claude/07-competition-system.md`** — läs det innan du arbetar i det här området.

## UI Implementation
→ **Hela avsnittet (144 rader): `Documentation/claude/08-ui-implementation.md`** — läs det innan du arbetar i det här området.

## Required Umbraco Backoffice Setup
→ **Hela avsnittet (2105 rader): `Documentation/claude/09-required-umbraco-backoffice-setup.md`** — läs det innan du arbetar i det här området.

## Utgiftssidan (P6) — utlägg, leverantörsfakturor och attest (2026-09-23)
Den sista stora luckan innan påståendet *"vi är ert bokföringsprogram"* håller. Den som lägger ut
pengar för klubben hade ingen väg alls tillbaka, och `LedgerApproval` fanns som **tabell i prod
sedan P1 — tom, utan en enda läsare eller skrivare**.
→ **Hela avsnittet (140 rader): `Documentation/claude/10-utgiftssidan-p6-utlagg-leverantorsfakturor-och-attest-2026-0.md`** — läs det innan du arbetar i det här området.

## Anläggningsregistret (P9) — vad föreningen äger, och årets avskrivning (2026-09-23)
Bokslutets sista frågetecken. Steget "Avskrivningar" stod som **Unknown** med texten
*"anläggningsregister finns inte ännu"*; nu finns registret och steget kan svara.
→ **Hela avsnittet (130 rader): `Documentation/claude/11-anlaggningsregistret-p9-vad-foreningen-ager-och-arets-avskri.md`** — läs det innan du arbetar i det här området.

## Behörigheterna — kassören skriver, den valda revisorn läser (2026-09-24)
Efter Michael Henriksson: *"Kassören borde vara den enda som kan komma in och göra allt i
bokföringen."* Förut skrev **klubbadmin** (`ClubAdmin_{id}` — oftast en annan person än
kassören) och via `IsClubAdminForClub` även varje krets- och sajtadministratör.
→ **Hela avsnittet (58 rader): `Documentation/claude/12-behorigheterna-kassoren-skriver-den-valda-revisorn-laser-202.md`** — läs det innan du arbetar i det här området.

## Momsen — registreringen, satsen per konto, och grinden (2026-09-24)
Efter Michael Henriksson (Åmåls PK). `LedgerIssuerSettings.IsVatRegistered` fanns men **gick inte
att sätta från någon yta** — raden skapades med 0, så en momsregistrerad förening fick
*"Föreningen är inte momsregistrerad"* på varje kvitto. Samtidigt räknade bokföringen moms ur
kontots `DefaultVatRate` **utan att fråga om registreringen**.
→ **Hela avsnittet (55 rader): `Documentation/claude/13-momsen-registreringen-satsen-per-konto-och-grinden-2026-09-2.md`** — läs det innan du arbetar i det här området.

## Bankavstämningen — riktning, borttagning och massbokföring (2026-09-25)
Efter Michael Henriksson (Åmåls PK). `Services/Ledger/LedgerBankImportService` +
`Models/Ledger/BankStatementFormat`, ytan är Avstämning i `Views/Ekonomi.cshtml`.
→ **Hela avsnittet (43 rader): `Documentation/claude/14-bankavstamningen-riktning-borttagning-och-massbokforing-2026.md`** — läs det innan du arbetar i det här området.

## Projektdimensionen — tävlingar och evenemang blir projekt (2026-09-23)
Kvar-listans post 1. `LedgerProject` och `ProjectId` på konteringsraden fanns sedan 2026-09-18, men
**inget skapade ett projekt**: ingen endpoint, ingen väljare i Bokför, och `EnsureForSource` hade noll
anropare. En klubb som ville se vad SSM-25 kostade kunde alltså inte märka något.
→ **Hela avsnittet (123 rader): `Documentation/claude/15-projektdimensionen-tavlingar-och-evenemang-blir-projekt-2026.md`** — läs det innan du arbetar i det här området.

## Kretsavgiften — samma motor som medlemsavgiften (2026-09-23)
Kvar-listans post 2 (P5:s andra halva). Kretsen kunde inte ta betalt av sina klubbar:
`LedgerSourceType.RegionFee` och rollen `revenue-region-fee` fanns, men ingenting skapade en sådan post.
→ **Hela avsnittet (206 rader): `Documentation/claude/16-kretsavgiften-samma-motor-som-medlemsavgiften-2026-09-23.md`** — läs det innan du arbetar i det här området.

## Noter i bokslutet, och räkenskapsårets datum (2026-09-29)
Två rapporter från Michael Henriksson (Åmåls PK).
→ **Hela avsnittet (33 rader): `Documentation/claude/17-noter-i-bokslutet-och-rakenskapsarets-datum-2026-09-29.md`** — läs det innan du arbetar i det här området.

## Verifikationer: kontofilter, huvudbok och kontolänkar (2026-09-30)
Efter Michael Henriksson: *"se vilka konton de bokförts på, filtrera och söka — framför allt för
revisorerna"*. Servern hade redan kontofiltret (`List(accountNumber)`) och huvudboken
(`GetAccountLedger` / `LedgerJournalService.AccountLedger`, med ingående saldo ur ALLT före perioden)
— men ingen yta använde dem.
→ **Hela avsnittet (18 rader): `Documentation/claude/18-verifikationer-kontofilter-huvudbok-och-kontolankar-2026-09.md`** — läs det innan du arbetar i det här området.

## Bokför: överföring mellan egna konton, och kontolistorna följer kontoplanen (2026-09-29)
Två rapporter från Lis Erevall.
→ **Hela avsnittet (27 rader): `Documentation/claude/19-bokfor-overforing-mellan-egna-konton-och-kontolistorna-folje.md`** — läs det innan du arbetar i det här området.

## Ekonomigenomgången 2026-09-24 — liggarfel som bara syns över ÅR
Hittat genom att gå igenom ett kassörsår som tre personer (lekmannakassör, ekonom med eget
program, revisor). Findings och kvarstående förslag: `notes/ekonomi-genomgang-2026-09-24.md`.
→ **Hela avsnittet (1359 rader): `Documentation/claude/20-ekonomigenomgangen-2026-09-24-liggarfel-som-bara-syns-over-a.md`** — läs det innan du arbetar i det här området.

## Banans kalender — träningar och händelser på en skjutbana (2026-10-11)
Efter Michaels önskemål: händelser och träningar kan knytas till en skjutbana (och en bana/vall), som
tävlingar redan kunde, och banan får en kalender där klubbarna som delar den ser varandras tider.
Kalendern är delen **Kalender** på banans egen sida (`/skjutbana/{id}#vy=kalender`, se nästa avsnitt);
den gamla adressen `/skjutbana/kalender?r=<rangeId>` leder dit (`RangeCalendarController.Index`). Data: **`RangeCalendarService`** (EN form, `RangeCalendarItem`) över fem källor —
→ **Hela avsnittet (162 rader): `Documentation/claude/21-banans-kalender-traningar-och-handelser-pa-en-skjutbana-2026.md`** — läs det innan du arbetar i det här området.

## Mitt schema — personal competition itinerary (2026-07-25)
A member's own competition day in one timeline: what they **shoot** + what they **work** + the day's
**programme**. `Services/Schedule/MyScheduleService.cs` is the single source of truth; every surface
renders what it returns and none of them read start lists themselves.
→ **Hela avsnittet (81 rader): `Documentation/claude/22-mitt-schema-personal-competition-itinerary-2026-07-25.md`** — läs det innan du arbetar i det här området.

## Bemanning — the grid (roll × dag), open role catalog, person identity (2026-08-14 → 15)
Rebuilt from a real SM springskytte staffing plan (`Bemanningsplan Spring SM 20226.xlsx`, 41 people,
101 assignments, 3 days). Design rationale + what is still open:
`notes/bemanningsrutnat-skiss-2026-08-14.md` (local working notes — see `notes/README.md`; the
folder is gitignored, so the file is not in a fresh clone).
→ **Hela avsnittet (153 rader): `Documentation/claude/23-bemanning-the-grid-roll-dag-open-role-catalog-person-identit.md`** — läs det innan du arbetar i det här området.

## Aktivitetssammanställning per medlem och år (2026-09-01)
Mellanlagret mellan närvarologgningen och Föreningsintyget: **ETT svar per (medlem, år)** över de tre
källorna — träningslogg, tävlingsdeltagande, evenemangsnärvaro. Utan det är närvarologgen bara rader i
en tabell. `Services/MemberActivitySummaryService.cs` + `Models/MemberActivitySummary.cs`.
→ **Hela avsnittet (319 rader): `Documentation/claude/24-aktivitetssammanstallning-per-medlem-och-ar-2026-09-01.md`** — läs det innan du arbetar i det här området.

## Föreningsintyg — utfärdande och utskrift, fas 1 (2026-09-01)
Kedjans punkt 4. Polisens blankett **PM 551.24 Ver. 2019-01-18/11** som data, ett utfärdandeflöde och
en utskriftsvy. Bygger på aktivitetssammanställningen (punkt 3) och lägger ingen andra läsväg över den.
→ **Hela avsnittet (1041 rader): `Documentation/claude/25-foreningsintyg-utfardande-och-utskrift-fas-1-2026-09-01.md`** — läs det innan du arbetar i det här området.

## Krypterat vapeninnehav — registret, behörigheten och kryptot (2026-09-02)
Kedjans punkt 5. **Läs det här avsnittet innan du rör något under `Services/Firearms/`.**
→ **Hela avsnittet (495 rader): `Documentation/claude/26-krypterat-vapeninnehav-registret-behorigheten-och-kryptot-20.md`** — läs det innan du arbetar i det här området.

## Lånevapensbokning (2026-09-02)
Kedjans punkt 6. **Ett rent lager ovanpå punkt 5** — ingen ny vapenmodell, ingen ny behörighet.
Bokar `Firearm` med `ScopeKind='Club'` och `IsLoanable=1`.
→ **Hela avsnittet (125 rader): `Documentation/claude/27-lanevapensbokning-2026-09-02.md`** — läs det innan du arbetar i det här området.

## Lånevapen v2: praktisk hantering (2026-09-02)
Punkt 6 gjorde det möjligt att boka. v2 handlar om **vad som faktiskt händer i valvet**, och hela
ytan vilar på en enda insikt:
→ **Hela avsnittet (437 rader): `Documentation/claude/28-lanevapen-v2-praktisk-hantering-2026-09-02.md`** — läs det innan du arbetar i det här området.

## Dubblettsammanslagning av medlemmar (2026-08-25)
Klubbadmin → Medlemmar → Åtgärder → **Hitta dubbletter**. Efter en import finns samma person ofta
två gånger: en gång självregistrerad (annan e-postadress, så varken personnummer eller e-post
matchade) och en gång från klubbens gamla register. Den självregistrerade posten äger INLOGGNINGEN
och historiken; den importerade äger de bra fältvärdena (pistolskyttekort, adress, telefon). Ingen av
→ **Hela avsnittet (68 rader): `Documentation/claude/29-dubblettsammanslagning-av-medlemmar-2026-08-25.md`** — läs det innan du arbetar i det här området.

## Board Work (Styrelsearbete) — dedicated /styrelse page
→ **Hela avsnittet (224 rader): `Documentation/claude/30-board-work-styrelsearbete-dedicated-styrelse-page.md`** — läs det innan du arbetar i det här området.

## ⚠️⚠️ VARJE MEJL MÅSTE VÄLJA VART ETT SVAR TAR VÄGEN (2026-09-09)
Rapporterat: en medlem som ombetts komplettera sin föreningsintygsförfrågan svarade på mejlet så
fort hen fixat uppgifterna — men mejlet kom från `admin@pistol.nu`, så svaret nådde sajtägaren och
inte klubben, och klubben satt kvar med *"Väntar på medlemmen"*.
→ **Hela avsnittet (179 rader): `Documentation/claude/31-varje-mejl-maste-valja-vart-ett-svar-tar-vagen-2026-09-09.md`** — läs det innan du arbetar i det här området.

## Radåtgärder: när en knapprad ska bli en meny (2026-08-24)
**Gränsen: 3 eller fler kontroller synliga på en typisk rad → EN `Åtgärder`-meny.** Det är där
alla konverteringar har landat, och den är nu genomförd överallt (11 radtyper på 8 ytor, se nedan).
Tre skärpningar:
- **Exakt 2 → meny bara om båda är ikoner utan text.** Två textknappar är läsbara; två ikoner i en
→ **Hela avsnittet (64 rader): `Documentation/claude/32-radatgarder-nar-en-knapprad-ska-bli-en-meny-2026-08-24.md`** — läs det innan du arbetar i det här området.

## ⚠️⚠️ Sajtstrukturen — anmälningar och händelser i egna tabeller (branch `sajtstruktur`, 2026-10-09)
Plan: `notes/sajtstruktur-plan-2026-10-09.md`. **Byggt på branchen, INTE deployat.** Umbracos globala
skrivlås (-333) tål inte 50 000–75 000 nya noder om året, så det användarna skapar i mängd flyttas
ur innehållsträdet. Umbraco behåller redaktionellt innehåll (klubbar, kretsar, sidor, tävlingarna
tills fas 4).
→ **Hela avsnittet (114 rader): `Documentation/claude/33-sajtstrukturen-anmalningar-och-handelser-i-egna-tabeller-bra.md`** — läs det innan du arbetar i det här området.

## Common Patterns
→ **Hela avsnittet (640 rader): `Documentation/claude/34-common-patterns.md`** — läs det innan du arbetar i det här området.

## ⚠️⚠️ Umbraco 17 på .NET 10 (fas E, branch `umbraco-17`, 2026-10-17)

Umbraco.Cms 17.7.1, net10.0. `HpskSite.Shared` bär både net9.0 och net10.0, eftersom mobilappen kör .NET 9.
Läs det här innan du rör datum, NPoco eller adresser.

- **⚠️⚠️ DATUMEN — ALL TID VISAS I SVENSK TID (Stefan 2026-10-17).** Prod-servern kör i svensk tid
  (hälsosidans "at" bär +02:00), så `DateTime.Now` = svensk tid. Regeln:
  - **Lagrad tid är svensk tid** — utom kolumnerna i **`StoredTimeBasis.UtcColumns`** ("Tabell.Kolumn"), som är UTC.
    Ny kolumn som skrivs med `UtcNow`: lägg den i registret (`StoredTimeBasisTests` fäller bygget för en `…Utc`-kolumn
    som saknas). Blanda aldrig `Now` och `UtcNow` i samma kolumn.
  - **`StoredTimeMapper`** (först bland NPoco-mapparna) märker vid läsning: registrerad kolumn → Kind Utc, övriga
    kolumner i våra `[TableName]`-tabeller och `PropertyDataDto.DateValue` → Unspecified. JSON får alltså `Z` bara på
    riktig UTC, och webbläsaren visar båda rätt. (16 märkte ALLT Utc → en svensk 07:22 visades 09:22.)
  - **En UTC-tid som visas på servern** eller läses med rå SQL / en DTO utan `[TableName]` (Kind okänd):
    **`SwedishTime.FromUtc(...)`** — aldrig `.ToLocalTime()`, som inte flyttar ett Unspecified-värde.
  - **Umbracos systemdatum är UTC i 17** (`CreateDate`, `UpdateDate`, `LastLoginDate`, `umbracoNode` …;
    `MigrateSystemDatesToUtc`). Läs dem genom **`UmbracoTime.Local(...)`** (API) eller **`UmbracoTime.FromDbUtc(...)`** (SQL).
  - **Inkommande JSON:** `SwedishDateTimeJsonConverter` räknar om en tid med `Z`/offset till svensk tid; en tid utan
    zon lämnas. **Webbläsaren:** `wwwroot/js/swedish-dates.js` — `hpskLocalDate()` / `hpskLocalDateTime()` i stället
    för `toISOString()` (som ger UTC och därmed dagen före vid midnatt).
  - **`PropertyDateKindNormalizer`** tar bort Kind från datumvärden innan ett dokument eller en medlem sparas.
  - Datareparation: `Migrations/fix-series-dates-utc-shift.sql` (seriedatum lagrade kvällen före).
- **NPoco 6:** de asynkrona metoderna tar `object[] args` utan `params`. **`NPocoAsyncParamsExtensions`**
  (global using i `GlobalUsings.cs`) tar anrop i den gamla formen. De synkrona metoderna är oförändrade.
- **`GetAtRoot()`/`GetByRoute()`** är borta ur den publicerade cachen. **`PublishedCacheNavigationExtensions`**
  är en brygga med samma namn — använd den inte i ny kod (C3-planen K4: `ISiteNodes`, controllers i S7).
- **`CompetitionUrlProvider`** har `Alias` och `GetPreviewUrlAsync`, som `IUrlProvider` kräver i 17.
- **Körtidskompileringen av vyerna** ligger i paketet `Umbraco.Cms.DevelopmentMode.Backoffice`. Utan det
  startar inte dev (ModelsBuilder `InMemoryAuto`). ⚠️ Mätt: en vy som laddas upp till det PUBLICERADE bygget
  slår inte igenom förrän appen startar om — varken på 16 eller 17 med prods inställningar.
- **⚠️ Adresserna:** 17 routar och lämnar ut `UrlSegment` ur `umbracoDocumentUrl`. Dev-databasens tabell var
  gammal (adressdelar med `&` och `:`), prods var rensad. Deployen tvingar fram en ombyggnad
  (`UPDATE umbracoKeyValue SET value = 'ombyggnad-fas-e' WHERE [key] = 'UmbracoUrlGeneration'` före första
  starten), så tabellen följer dagens regler. Bygger om ~8 000 adresser på några sekunder.
- **Säkerhetshål:** Pkcs 10.0.13 och MessagePack 3.1.12 låsta. ImageSharp 3.1.12 kvar (rättningen finns bara i 4.x).
  TIFF-kodaren är avstängd: **`Services/Media/SafeImageConfiguration`** (bara JPEG/PNG/GIF/WebP/BMP) används av
  Umbracos bildmiddleware (PostConfigure), måttläsningen (DI) och `ImageResizeService`. Ny kod som avkodar en
  uppladdad bild: `Image.LoadAsync(new DecoderOptions { Configuration = SafeImageConfiguration.Instance }, ström)`
  — formatet känns igen på innehållet, så filändelsen skyddar inte. Test: `SafeImageConfigurationTests`.
- Verifiering: kopian `Umbraco_U17` (dev-databasen uppgraderad), `$SP/u17run.sh`, och skyddsnätet 16 mot 17 på
  den frysta kopian (`$SP/s0capture-u17.sh`, `$SP/e-all.sh`). Sviter som läser databasen följer `HPSK_DB`.

## ⚠️⚠️ Lås och räknare ligger i databasen — `DatabaseLock`, `RateCounterService` (skalplanen D1, 2026-10-18)

**Skriv aldrig `lock`/`SemaphoreSlim`/`IMemoryCache` där korrektheten hänger på att bara en åt gången gör
kontroll-och-skrivning, eller på att en räknare är densamma överallt.** Ett lås i processen skyddar bara
samma process — med två instanser, eller en omstart med överlapp, säljs samma plats två gånger.
- **`DatabaseLock`** (singleton, `Services/Hosting/`): `sp_getapplock` på en EGEN anslutning, ägd av
  sessionen. `TryAcquire(name, timeout)` / `TryAcquireAsync` → `Lease` (Dispose släpper) eller null =
  upptaget. Utan SQL Server, eller vid databasfel, faller den tillbaka på ett lås i processen (samma skydd
  som förut, loggat). Namnen är ett kontrakt mellan instanserna: `dp-capacity:{tävling}`,
  `invoice-hub:{tävling}`, `registration-invoice:{tävling}:{anmälan}`, `ranking-snapshot`.
- **Egenbokningens platser:** `DirektplaceringStartListService.TryLockCapacity(Async)` — tävlingssidans
  anmälan, efteranmälan och Redigera anmälan tar SAMMA lås runt kontroll + sparning. ⚠️ Diskens två vägar
  hade inget lås alls före D1; A/B mot det gamla bygget: **5 anmälningar i ett skjutlag med 3 platser**,
  tre omgångar av tre (`hpsk-verify/d1-capacity-race-verify.mjs`, 19/19 efter).
- **Rankningen:** de publika ingångarna i `RankingSnapshotService` tar `ranking-snapshot` (väntar högst 5
  min, annars `BusyMessage`); de inre `…CoreAsync` anropas när låset redan är taget.
- **`RateCounterService`** (`dbo.RateCounter`, egen anslutning): `Increment(key, window)` ger räknaren efter
  anropet, atomärt; `TryClaimOnce` = en gång per fönster (claim före utskick). Ekonomienkätens spärr per IP
  och låsningsmejlet. Saknas tabellen räknas i minnet och det loggas en gång.
- Kvar i processen MED FLIT (bara prestanda eller loggstrypning): besöks- och aktivitetsstämplarnas
  strypning, `OpsSignals`, `CompetitionChangeRecorder`s varningsstrypning, `CompetitionPaymentModelService`s
  cache (beslutet är oföränderligt), `NodeCompetitionResultStore` (bara nodläget),
  `TrainingMigrationService.Gate` (UPDLOCK i transaktionen är spärren), AI-indexets byggare.
- Test: `DatabaseLockTests` — körs mot SQL Server när `HPSK_TEST_SQL` är satt (annars hoppas de över med
  namn). A/B: med båda tjänsterna tvingade till minnet faller 4. ⚠️ "Två instanser får inte samma lås"
  kan inte skilja lägena åt — det är `APPLOCK_TEST` från en annan anslutning som bevisar databasen.
- Migrering: `create-rate-counter-table.sql` (D4 i körordningen), FÖRE deploy.

## ⚠️⚠️ Bakgrundstjänster ärver `IsolatedBackgroundService` — ALDRIG `BackgroundService` (2026-09-24)

**Ska du bygga en ny bakgrundstjänst?** Ärv `HpskSite.Services.Hosting.IsolatedBackgroundService`
och skriv arbetet i `ExecuteIsolatedAsync`. Registrera den som vanligt med `AddHostedService<T>()`.
`ExecuteAsync` är **förseglad** i basklassen, och `BackgroundServiceIsolationTests` faller för varje
hostad tjänst i sajten som inte ärver den.

**Varför.** Värden startar alla bakgrundstjänster från samma uppstartskontext, och Umbracos ambienta
scope är en `AsyncLocal` som flyter med execution context. En vanlig `BackgroundService` delar därför
ambient scope-stack med alla andra. Mätt i dev 2026-09-24: fyra startkontroller med samma fördröjning
(1 min) kapplöpte, två av dem (`MailReplySchemaGuard` och `FirearmKeyGuard`) öppnade scope samtidigt
och delade anslutning (*"The connection's current state is connecting"*), disponeringen kastade
*"not the Ambient Scope"*, och **en rotscope med öppen transaktion blev kvar**. Rankingen (2 min) och
bildstädningen (5 min) skrev sedan in i den: 70 X-lås på `RankingSnapshot`, S-lås på `umbracoLock`,
och varje inloggning hängde för att `MemberService.Save` aldrig fick sitt skrivlås. Kapplöpningen var
slumpvis — den syntes i dev-loggarna nästan varje dag — och därför "hjälpte" en omstart ibland.

Basklassen kör arbetet i `Task.Run` under `ExecutionContext.SuppressFlow()`, så varje tjänst börjar
i en tom kontext med egen scope-stack. **Isoleringen gäller MELLAN tjänster, inte inuti en:** startar
en tjänst själv parallellt arbete gäller samma regel som överallt annars —
`using (ExecutionContext.SuppressFlow()) { _ = Task.Run(...); }`.

**Följdfixen:** `MailReplyService.TableExists` och `FirearmVaultService.GetInventory` (hette
`TryGetInventory`) svalde tidigare undantaget och svarade "saknas", så kapplöpningen loggades som
de FALSKA larmen *"SVARSLAGRET ÄR TRASIGT"* (Fatal) och *"FirearmKeyVault finns inte"*. De kastar nu,
och startkontrollerna loggar att kontrollen inte kunde genomföras. **En kontroll får aldrig översätta
"kunde inte fråga" till ett svar.**

Test: `BackgroundServiceIsolationTests` (5). A/B: basklassen utan `SuppressFlow` → mekanismprovet
rött; en tjänst som ärver `BackgroundService` → arkitekturregeln röd och namnger tjänsten; en tjänst
som åsidosätter `ExecuteAsync` → kompileringsfel.

## ⚠️⚠️ Resultatlistan går genom `ICompetitionResultStore` (skalplanen C1, branch `skalplan-fas-c`, 2026-10-12)

**Läs eller skriv aldrig `competitionResult`-noden direkt** — varken via content service, publicerad
cache, `Children()` eller SQL mot `umbracoPropertyData`. Gå genom `Services/Results/ICompetitionResultStore`.
`ResultStoreGuardTests` fäller bygget på aliaset eller listans egenskaper (`resultData`,
`honoraryAwardConfig` …) utanför lagringen; ett motiverat undantag får `resultstore-ok: <skäl>` på raden.

- **Posten** är `Models/Results/CompetitionResultRecord` — en per tävling; deltävlingen bär sina egna
  `Sub…`-fält på samma post. Fältskyttets huvudlistas publiceringsflagga och fält-/springskyttets
  huvudsammanslagning ligger fortfarande på TÄVLINGEN (`faltskytteResultsOfficial`, `mergeConfig`).
- **Skriv med `Update(competitionId, r => …, createIfMissing)`**, aldrig en hel post: den läser färskt,
  kör ändringen och skriver bara det som ändrades (noden) eller under `UPDLOCK, HOLDLOCK` (tabellen).
  Läs-ändra-skriv av t.ex. klassnamnen görs INNE i lambdan. `Ensure` skapar en tom Preliminär lista.
- **`GetMany`** för listor och sidor (en uppslagning, publicerade cachen i nodläget). `Get` läser
  senast sparade läget (content service i nodläget).
- **`CanStore(field)`** ersätter `HasProperty` — en äldre doctype kan sakna ett fält; tabellen bär alla.
  Ett ändrat fält som inte går att lagra kastar `CompetitionResultFieldMissingException`.
- **`/resultat/`-adressen byggs ur tävlingens adress** överallt (`{tävling}/resultat/`), aldrig ur nodens.
- **Växeln** `Results:Store` (`node`/`table`) och frysningen `Results:WriteFreeze` i `SiteSetting`,
  som anmälningarna. `SwitchingCompetitionResultStore` väljer per anrop; frysningen ger
  `CompetitionResultWriteFrozenException`.
- **Tabellen** `CompetitionResultList` (`create-competition-result-list-table.sql`), en rad per tävling,
  migrerade rader bär nodens id. Flytten: `CompetitionResultMigrationService` / `CompetitionResultMigrationController`
  och kortet **Resultatlistor** i Underhåll på `/admin-page`. `CompetitionResultNodeGuard` vägrar nya
  noder när tabellen gäller.
- **Utan nod:** `CompetitionUrlContentFinder.TryServeResultPage` (snygga adresser) och
  `CompetitionResultTreeUrlFinder` (trädadresser) visar TÄVLINGEN med vyn `CompetitionResult` när lagringen
  har en lista. Vyn tar därför `Model` som antingen resultatnoden eller tävlingen.
- ⚠️ **Dev-databasen står på `table` sedan 2026-10-20, som prod, och noderna är raderade — växla aldrig tillbaka till `node`.** (`hpsk-verify/resultstore-mode.mjs` är historik.)
  Sviter: `resultstore-migration-verify.mjs` (hela fönstret, 30), `resultstore-writepaths-verify.mjs`
  (23, lägesmedveten) — och resultatsviterna är gröna i båda lägena.

## ⚠️⚠️ Tabeller som tar över noder: id:n enligt LAGEN (skalplanen fas C)

**Se ⚖️ LAG: id-reglerna överst.** `CompetitionResultList` och `CompetitionStartList` har `IDENTITY(1,1)`.
Flyttade rader bär nodens id; identiteten sätts över varje id sorten någonsin haft eller som någon pekar på
(`IdentityFloor` med `StartListMigrationService.IdSources` / `CompetitionResultMigrationService.IdSources`)
— efter flytten, efter tömningen och vid varje start (`IdentityFloorHostedService`).
- **Historik:** 2026-10-12 fick tabellerna först ett startvärde på 100 000 000, sedan negativa id:n
  (`IDENTITY(-1,-1)`, `NegativeIdentity`, `TableIdSignGuardTests`). Båda togs bort 2026-10-13 när Stefan
  beslutade lagen: krocken med en nod av en annan sort är ofarlig när varje sort slås upp på ett ställe.
  Tabellskripten bygger om en tom tabell i den gamla formen; startkontrollen räknar den som saknad.
- Påminnelseloggens nycklar (`lag-{id}-…`, `final-…`, `spring-…`, `stafett-…`) och finalernas
  `QualificationStartListId` är källor — ett återanvänt id hade fått en påminnelse att se skickad ut.

## ⚠️⚠️ Startlistorna går genom `IStartListStore` (skalplanen C2, branch `skalplan-fas-c`, 2026-10-12)

**Läs eller skriv aldrig en `precisionStartList`-, `finalsStartList`- eller `competitionStartListsHub`-nod
direkt** — varken via content service, publicerad cache, `Children()` eller SQL mot `umbracoPropertyData`.
Gå genom `Services/StartLists/IStartListStore`. `StartListStoreGuardTests` fäller bygget på aliasen och
listans egenskaper (`configurationData`, `isOfficialStartList` …) utanför lagringen; ett motiverat
undantag får `startliststore-ok: <skäl>` på raden.

- **Posten** är `Models/StartLists/StartListRecord` — en per lista, `Kind` = `Qualifying` (precision,
  egenbokning, springskyttets namngivna listor och stafetten) eller `Finals` (en per vapengrupp).
  `ConfigurationData` har fyra JSON-former (se posten). **Id:t är nodens id och bevaras** — klienten,
  `&fsl=`, påminnelseloggen, kalenderfilernas UID och finalens `QualificationStartListId` bär det.
- **Skriv med `Update(id, r => …, publish)`** (läser färskt, skriver bara ändrade fält; tabellen under
  `UPDLOCK, HOLDLOCK`). `Create(competitionId, kind, name, init)`. `StartListPublish` styr bara nodens
  publicering — tabellen har ingen, och det publika läget avgörs av `IsOfficial`, som alla publika läsare
  frågar efter. `ListPublished` = nodernas publicerade cache, tabellens `List`.
- **Adressen byggs ur tävlingens** + `UrlSegment` (`StartListRecord.PublicUrl`), aldrig ur en nod.
  Utan nod (hubbens listor nu, tabellen sedan) visar `CompetitionUrlContentFinder.TryServeStartListPage`
  och `CompetitionResultTreeUrlFinder` tävlingen med mallen `PrecisionStartList`/`PrecisionFinalsStartList`
  och lägger listan i `Context.Items[StartListItemKey]`.
- **Tabellen** `CompetitionStartList` (`create-competition-start-list-table.sql`). Migrerade rader bär
  nodens id; nya rader fortsätter över varje refererat startliste-id — se *Tabeller som tar över noder:
  id:n enligt LAGEN* ovan. Namnet är unikt per tävling på SEGMENTET
  (`"Namn (1)"`, som syskonnoder). Hubbens listor flyttas med `LegacyHub = 1`.
- **Växeln** `StartLists:Store` (`node`/`table`) och frysningen `StartLists:WriteFreeze` i `SiteSetting`.
  `SwitchingStartListStore` väljer per anrop; frysningen ger `StartListWriteFrozenException`.
  Flytten: `StartListMigrationService` / `StartListMigrationController`, kortet **Startlistor** i
  Underhåll på `/admin-page`. **Utkastet flyttas, inte det publicerade** — torrkörningen räknar listor
  där de skiljer sig. `StartListNodeGuard` vägrar nya noder när tabellen gäller.
- ⚠️ **`teamFormat` är en FlexibleDropdown** som kastar på ett rent strängvärde — `NodeStartListStore.
  FromPublished` läser då det råa källvärdet. Fångades av sviterna, inte av enhetstesten: tävlingssidan
  gav 500 på varje tävling med en springskyttelista.
- **Frysningen svarar med sitt meddelande** genom `Services/Hosting/WriteFrozenExceptionFilter` (alla fyra
  lagringarnas frysundantag), i stället för ett 500-svar från endpoints utan egen felhantering.
- **En permanent raderad tävling** tar med sig sina rader i `CompetitionStartList` och
  `CompetitionResultList` (`CompetitionTableRowsCleanupHandler`) — startlistorna bär namn och klubbar.
  Papperskorgen rör den inte.
- ⚠️ **Dev-databasen står på `table` sedan 2026-10-20, som prod, och noderna är raderade — växla aldrig tillbaka till `node`.** (`hpsk-verify/startliststore-mode.mjs` är historik.)
  Svit: `startliststore-migration-verify.mjs` (hela fönstret) — och startlistesviterna ska vara gröna i
  båda lägena.

## DataProtection-nycklarna ligger i databasen (2026-10-12, skalplanen D2)
`dbo.DataProtectionKey` via `Services/Hosting/SqlDataProtectionKeyRepository` (inkopplad i `Program.cs`).
Nycklarna signerar inloggningar, antiforgery och de utskrivna/mejlade länkarna (stations-QR,
betal-, svars- och justeringslänkar, ICS) — i `App_Data` dör de vid en serverflytt och kan inte delas
mellan instanser. Egen SqlClient-anslutning, aldrig Umbracos scope. Är tabellen tom kopieras filerna i
→ **Hela avsnittet (11 rader): `Documentation/claude/35-dataprotection-nycklarna-ligger-i-databasen-2026-10-12-skalp.md`** — läs det innan du arbetar i det här området.

## ⚠️⚠️ Filerna går genom lagringen — `IFileStore` och `MediaFiles` (skalplanen D3, 2026-10-19)

**Skriv aldrig en fil med `File`/`FileStream`/`Directory`, och bygg aldrig en sökväg till `App_Data` eller
`wwwroot`.** En sådan fil finns bara på en server, dör vid en serverflytt och syns inte för en andra
instans. `FileStorageGuardTests` fäller bygget (undantag: `filestore-ok: <skäl>` på raden).
- **Sajtens egna filer = `Services/Storage/IFileStore`**, ett namn per område i `FileStoreAreas`
  (privata: medaljbevis, bandokument, förberedelsedokument, liggarens underlag, kursmaterialet,
  AI-indexets cache, dokumentarkivet; publika: `field-targets`, `faltskytte`, `faltkonfig`, `uploads`).
  Disk (standard): `App_Data/{område}` respektive `wwwroot/images/{område}` — exakt som förut.
  Blobb (`FileStorage:Provider = AzureBlob`, `ConnectionString`, `ContainerName`): `app-data/{område}/…`
  och `images/{område}/…` i en PRIVAT behållare; `PublicImageMiddleware` serverar `/images/{område}/…`,
  så adresserna i databasen ändras aldrig. Namnregeln (`FileStoreNames`) prövas i lagringen.
  Nytt område = en konstant (publikt: lägg den också i `PublicAreas`) + flyttens lista följer med.
- **Filer under `/media/…` = `Services/Storage/MediaFiles`** (Umbracos mediafilsystem). Med
  `Umbraco:Storage:AzureBlob:Media:ConnectionString` (paketet Umbraco.StorageProviders.AzureBlob 17.1)
  ligger mediabiblioteket i blobblagringen och Umbraco serverar `/media/…` därifrån. Uppladdningar som
  blir medier går genom `ImageMediaService.SaveAsync` / `SaveMediaAsync` (en regel för mapp, namn, skrivväg).
  ⚠️ Paketet skapar inte behållaren — `FileStorageStartupService` gör det vid start och larmar Critical
  när lagringen inte svarar.
- **Läsning är en ström** (`OpenReadAsync`), aldrig en sökväg: `File(stream, typ, enableRangeProcessing: true)`.
- **AI-chattens logg ligger i `dbo.AiChatLog`** (`AiChatLogService`); de gamla månadsfilerna förs in en
  gång av `AiChatLogImportHostedService` och döps om till `imported-…`. Saknas tabellen skrivs filen som förut.
- **Flytten** (efter att blobblagringen slagits på): `/admin-page` → Underhåll → **Fillagring**
  (`FileStorageMigrationService`). Kopierar, raderar aldrig, hoppar över det som redan finns med samma
  storlek, i omgångar om 45 s tills inget återstår. ⚠️ En privat fil som inte flyttats syns inte — kör
  flytten direkt efter bytet.
- Rättat på vägen: arkivdokument med å/ä/ö i filnamnet gav 500 (rått filnamn i Content-Disposition);
  figurkatalogens bilduppladdning saknade behörighetskontroll.
- Test: `FileStoreTests` (samma kontrakt mot disk och — med `HPSK_TEST_BLOB=UseDevelopmentStorage=true`
  och Azurite — mot blobb), `FileStorageGuardTests`. Sviter: `hpsk-verify/d3-filestore-verify.mjs`
  (`HPSK_MODE=disk|blob`, kontrollerar var filen hamnade) och `d3-filestore-migrate-verify.mjs`.
  Azurite lokalt: `npx -p azurite azurite-blob --location <dir> --blobPort 10000 --loose`.
- **D4 (flera servrar)** — se nästa avsnitt.

## ⚠️⚠️ Flera instanser — `ScaleOut` (skalplanen D4, 2026-10-19)

**Utan sektionen `ScaleOut` och utan `ConnectionStrings:redis` beter sig sajten exakt som förut.** Allt nedan
slås på med inställningar den dag sajten ska köra på mer än en server (kräver flytt från Simply).
- **Schemalagt arbete bara på publiceraren.** `ScaleOut:ServerRole` = `SchedulingPublisher` | `Subscriber`
  sätter Umbracos serverroll uttryckligen (`ConfiguredServerRoleAccessor`). Varje bakgrundstjänst som
  SKICKAR eller SKRIVER på ett schema frågar `_scopeFactory.MayRunScheduled(...)` (`ScheduledWorkGate`) i
  början av varje varv: påminnelserna (schema, vapenlicens, kretsgranskning), rankningen, målbildsstädningen,
  adressernas startsvep. **Ny sådan tjänst: lägg in frågan.** Schemavakter, uppvärmning och tömning av
  minnesköer frågar inte — de gäller den egna instansen.
- **Länkar utåt = `Request.PublicBase()`** (`PublicUrls`), aldrig `$"{Request.Scheme}://{Request.Host}"` —
  bakom en lastbalanserare är det den interna adressen. `ScaleOut:PublicBaseUrl` ger den riktiga; tomt =
  ur förfrågan som förut. `ScaleOutGuardTests` fäller bygget (undantag `scaleout-ok:`).
- **Cacher som töms lokalt meddelar de andra instanserna** genom `CacheSignals.Notify(namn)` → raden i
  `dbo.CacheSignal`; `CacheSignalPollingService` läser var tredje sekund och kör tömningen i
  `CacheSignals.Invalidators`. Gäller fakturalistan, "Det här händer", sajtinställningarna (frysningarna!),
  kretskalendern, händelseadresserna och seriernas resultat. Tävlingarna har sin egen versionsrad
  (`CompetitionCacheVersion`). **Ny cache med lokal tömning: namn + tömning i registret + Notify.**
  Tömningen i registret får aldrig själv anropa Notify (slinga). Bara aktiv med `ScaleOut:ServerRole`.
  Cacher utan tömning (korta TTL på 30 s–5 min) är medvetet kvar — de blir inte fel, bara lite sena.
- **Redis** (`ConnectionStrings:redis`): andra nivån i cachen (`IDistributedCache`, som Umbracos
  `HybridCache` använder — cacherna överlever en omstart) och SignalR:s koppling mellan instanserna.
  `abortConnect=false`: sajten svarar när Redis är nere (prövat).
- **Vidarebefordrade huvuden** (`ScaleOut:ForwardedHeaders`, med betrodda proxyer/nät) — av som standard.
- **Umbracos egna inställningar per instans** (bara konfiguration, se körordningen D7):
  `Umbraco:CMS:Global:MainDomLock = SqlMainDomLock` + unik `MainDomKeyDiscriminator`,
  `Hosting:LocalTempStorageLocation = EnvironmentTemp`, `Examine:LuceneDirectoryFactory =
  SyncedTempFileSystemDirectoryFactory`, och `Unattended:UpgradeUnattended = false` på prenumeranterna.
- **Kända begränsningar:** träningsmatchens åskådarlista (statiska listor i `TrainingMatchHub`) visar bara
  den egna instansens åskådare; larmmejlen (`CriticalAlertService`) skickas per instans.
- Verifierat: två instanser mot `Umbraco_Scale` + Garnet (Redis-kompatibel, `dotnet tool install
  garnet-server`) — `hpsk-verify/d4-scaleout-verify.mjs` 14/14 (startas med två lokala instanser, se
  svitens huvud); A/B utan signaltabellen → B ser inte frysningen. Test: `ScaleOutGuardTests`.

## ⚠️⚠️ Skalreglerna — medlemmar (skalplanen B1–B4, 2026-10-12)

Plan och mätningar: `notes/skalplan-2026-10-12.md`. Mätt på en kopia med 20 000 medlemmar: ytor som
läste hela registret tog 3,6–4,1 s, och en rollfråga per medlem gav **21 s** för att lista en klubbs
administratörer. Efter B1–B3: 0,05–0,4 s.

- **Klubbens medlemmar = `ClubRosterService`** (`Active`, `ActiveMemberIds`, `LoadActiveMembers`) —
  ALDRIG `_memberService.GetAll(0, int.MaxValue)` + filter på `primaryClubId`/`memberClubIds`. Källan är
  `ClubMembership` med status annan än `Utträdd`/`Avliden`. `LoadActiveMembers` hämtar fullständiga
  `IMember` i bitar om 1 000 för ytor som behöver många egenskaper.
- **`ClubMembershipSyncHandler`** håller tabellen i takt vid VARJE medlemssparning: raden skapas för
  varje klubb i egenskaperna (Saved), en klubb som tas bort ger `Utträdd` + dagens datum och en klubb
  som läggs tillbaka gör den aktiv igen (Saving, i sparningens transaktion — Stefans beslut 2026-10-12).
  ⚠️ Aliaset heter **`memberClubIDs`** i databasen; jämför alias utan skiftläge i C#.
- **Hela registret i en lista/väljare = `MemberDirectoryService.All()`** (en SQL-fråga, sju egenskaper,
  inga `IMember`). Sajtadmins medlemslista filtrerar/sorterar/delar upp i sidor på den; "kan bli admin"
  utan klubbfilter söker på servern (`search`, högst `take` = 50, svaret bär `totalCount`/`truncated`).
  ⚠️ NPoco kan inte mappa direkt till en klass med beräknade egenskaper ("CLR detected an invalid
  program") — hämta till en ren radklass och mappa själv.
- **Roller i listor = `MemberRoleIndex.Load(db)`** (en fråga för alla) eller `GetMembersInRole(grupp)` —
  aldrig `GetAllRoles(m.Id)` i en loop. För EN medlem duger `GetAllRoles`.
- **Senast aktiv = `MemberActivityService`** (`GetAll`/`Get`, tabellen `MemberActivity`) — skrivs aldrig
  som medlemsegenskap. Middlewaren läser medlemskakan själv (`IdentityConstants.ApplicationScheme`; den
  ligger före autentiseringen i pipelinen, så `context.User` är tom där).
- **⚠️⚠️ Det som skrivs ofta skrivs aldrig på medlemmen (skalplanen B5, 2026-10-19).** En medlemssparning
  tar Umbracos skrivlås för medlemmar och indexerar om i Examine. Därför:
  - **Engångskoder** (lösenordsåterställning, inloggning efter godkännande, inbjudan) =
    `MemberTokenService` (`Issue`/`Get`/`Check`/`Clear`, tabellen `MemberToken`). Bara SHA-256 av koden
    lagras och jämförelsen sker i konstant tid. En felaktig kod lämnar den giltiga orörd; en utgången tas bort.
  - **Sedda filmer** = `TutorialWatchService` (`MemberTutorialWatch`, en rad per medlem och film).
  - **Skyttetrappans framsteg** = `Training.TrainingProgressStore` (`MemberTrainingProgress`, en rad per
    medlem) — ENDA läs- och skrivvägen. `LoadRows`/`All` för listor och statistik. Märkeskrediterade steg
    lagras aldrig (som förut). `MemberProgress.FromMember`/`SaveToMember` finns inte längre.
  - De gamla egenskaperna ligger kvar men läses inte. Ny tabell som bär ett medlems-id: lägg den i
    `MemberDataPurgeService.SubjectTables`; en `…Utc`-kolumn i `StoredTimeBasis.UtcColumns`.
  - Startkontroll: `MemberTablesSchemaGuardHostedService`. Migreringar: körordningen D5.
- **En sökruta = `MemberDirectoryService.Search(fråga, antal, clubId?, approvedOnly, searchNames)`** —
  LIKE i SQL, sorterat på namn, klubbfiltret via `ClubMembership`. Tom fråga = de första `antal`.
- **Få egenskaper över många medlemmar = `MemberPropertyReader.Read(alias[], ids?, approvedOnly, unapprovedOnly)`**
  (statistik, exporter, personmatchning). `IdsWithValue(alias)` ger dem som har ett värde (t.ex. de som
  börjat trappan); behövs då hela `IMember`: `GetAllMembers(ids)` i bitar om 1 000.
- **En krets medlemmar = `ClubRosterService.ActiveInClubs(klubbar)`** (en rad per medlem och klubb,
  `IsPrimary` för huvudklubben). Klubblösa: `ClublessMemberIds()`.
- **`ScaleGuardTests`** fäller bygget på rollfråga per medlem i en lista, fast tak (`GetAll(0, 5000, …)`),
  och på VARJE `MemberService.GetAll(` och `GetAllMembers()` utan urval — taken är 0 sedan 2026-10-19
  (skalplanen B: 39 + 3 ställen flyttade). Mätt på 20 000-kopian: 3–4 s → 0,02–0,3 s per yta;
  certifieringens "certifierad av" 21 s → 0,04 s. Svaren jämförda mot det gamla bygget
  (`hpsk-verify/scale-b-getall-capture.mjs`, `scale-b-import-dryrun.mjs`): identiska utom sökrutorna,
  som nu visar de första träffarna i namnordning.
- **⚠️ Ytorna litar nu på att `ClubMembership` stämmer med klubbegenskaperna.** Kontroll:
  `Migrations/check-prod-clubmembership-drift-2026-10-19.sql` (båda talen 0). Kopian `Umbraco_Scale` var
  inte i takt (111 rader saknades) — backfillen kördes där 2026-10-19.
- Mätverktyg: `ScaleSeedController` (vägrar allt utom databasen `Umbraco_Scale`, startprofilen
  "Umbraco.Web.UI (Scale)"), `hpsk-verify/scale-measure.mjs`, `scale-verify-b.mjs`, `scale-verify-sync.mjs`.
- Migreringar: `create-member-activity-table.sql` (K2, FÖRE deploy) och
  `backfill-clubmembership-from-club-properties.sql` med `@Apply = 1` (K3, efter deploy).

## ⚠️⚠️ Fristående tävlingsplanering, P1 (branch `tavlingsplanering`, 2026-10-21)

Plan: `notes/tavlingsplanering-fristaende-plan-2026-10-21.md` (v3 + beslut 9.2 ändrat) och expertgruppens
`…-utvardering-v3.md`. Stefans beslut 2026-10-21: var och en ställer in sina notiser, **förval ett mejl om
dagen**; svara och rösta ur mejlet **utan inloggning**; inbjudan per e-post till den som inte är skytt;
**"Meddelanden"** och **"Deltar"**; **alla i planeringen** skapar och bokar möten; allt i P1 på en gång.

- **Ytorna:** Tävling → **Tävlingsplanering** (`/tavlingsplanering`, `TavlingsplaneringHub.cshtml`, "Mina
  planeringar" + Ny planering). En planering = `/tavlingsplanering/{id}` (`Planering.cshtml` +
  `wwwroot/js/planering.js`, Ekonomins skal — rälsen grupperad sedan 2026-10-25, se *Rälsen grupperad*; Mina notiser är
  Inställningar → Dina notiser, `#vy=`). Mejlens knappar → `/tavlingsplanering/svar?t=…&a=svara|rosta|oppna|notiser`
  (samma sida i **länkläge**, varje anrop bär `t`). Kalender: `/tavlingsplanering/kalender.ics?t=`.
  Startsidan: kortet *Planeringar* (`_PlanningCard`, bara när något väntar). Mitt schema: *Planeringarnas
  möten* (`_PlanningMeetingsSchedule`, egen sektion — Mitt schema är i övrigt per tävling).
  `/tavlingsplanering?c=` (tävlingens förberedelser och bemanning) är oförändrad.
- **API:** `PlanningController` (surface). **Två sätt att vara inloggad:** sessionen, eller `t` = signerad
  mejllänk (`PlanningLinkService`, purpose `Planning.Mail.v1`, 60 dagar; kalenderns `Planning.Ics.v1` är
  evig men prövar deltagandet vid varje hämtning). **Byt aldrig en purpose-sträng.** Den som inte får läsa
  får "finns inte", aldrig "nekad". Pollningen (`Version`) svarar 200 `{ok:false}`.
- **⚠️⚠️ NYCKELN ÄR DELTAGAREN (`PlanningMember.Id`), aldrig medlemmen.** Meddelanden, omnämnanden,
  läsmärket, röster och notisloggen pekar på deltagaren; en **gäst** har `MemberId = NULL` och bara
  `GuestEmail`. Därför bär bara `PlanningMember.MemberId` en medlem — den ligger i
  `MemberDataPurgeService.AssignmentColumns` (raden står kvar som "Borttagen medlem", meddelandena behåller
  författaren) och följer med en sammanslagning.
- **⚠️⚠️ Planeringsgruppen är sin egen enhet** (Stefan 2026-10-23): den som är inbjuden är med, ingen annan.
  Ingen förening, styrelse eller roll ger läsrätt — `PlanningCaller` har bara `Participant` och `ViaLink`
  (`PlanningRulesTests` fäller bygget om ett fält läggs till). Kolumnerna `OwnerType/OwnerId/Visibility` är
  borttagna. Ingen underförstådd rätt för sajtadmin (som vapenregistret) — hen öppnar som läsare (support) och kan
  **gå med som support** (`Planning/JoinAsSupport`, Stefan 2026-11-11: vanlig deltagarrad, Sammankallande, märkt
  "(support)", mejl av, loggad; lämnar med Lämna). Varje id prövas mot planeringen.
- **Nivåerna = `PlanningAccessLevel`** (lagrade strängar byts aldrig): `Follower` *Följer* < `Participant`
  *Deltar* < `Editor` *Kan ändra* < `Owner` **Sammankallande**. Förklaringarna (`Help`) är EN text, skickas i
  `Get.accessLevels` och visas vid varje val av nivå. Följer läser allt men skriver inte, röstar inte och är
  **inte kallad till mötena** (`PlanningAccess.IsCalledToMeetings`: inga mötesmejl, räknas inte i
  "har inte svarat"/`VotesOwed`, inte i Mitt schema). Bara Sammankallande ger Sammankallande; den sista kan
  inte tas bort eller sänkas, och sidan varnar när det bara finns en. **En mejllänk ändrar aldrig planeringen.**
- **Företräder** (klubb, krets eller egen text) anges vid inbjudan (`AddMember`: inget angivet = hens egen klubb,
  `representsNothing=1` = inget; `InviteByEmail`: krets eller text) och ändras med `UpdateParticipant`; klubb och
  krets prövas (`RepresentsError`). Kretsnamn = `regionName`. Deltagarlistan = adminlistans form: en rad per
  person, nivån som färgad badge (`levelBadge`, samma färger i Nivåerna), sök + filter på nivå, gäster och
  Företräder (så syns om alla kretsar har någon med). På telefon ligger badgen under namnet.
- **Kontaktuppgifter** (radens Åtgärder, Stefan 2026-10-23): `ContactInfo` — bara den som är med (alla nivåer,
  även gäst via mejllänk), bara för en aktiv deltagare i SAMMA planering; medlem = kontots e-post + `phoneNumber`,
  gäst = inbjudningsadressen. Hämtas på begäran — `Get` och `SearchMembers` lämnar aldrig ut adresser.
- *Dina notiser* (Inställningar) länkar till `/user-profile-page#notiser` (Profil-fliken, nedrullad till Notiser-kortet).
- **Byt ut** (`ReplaceParticipant` → `PlanningService.ReplaceParticipant`, en transaktion): den nya (medlem
  eller gäst) får samma nivå och Företräder, den gamla lämnar, meddelandena står kvar, flödet får EN rad
  (`member-replaced`, "X ersatte Y (företräder Z)"). Bara Sammankallande, aldrig sig själv.
- **Planeringens slag frågas inte** — valet ändrade ingenting; nya rader får `Planeringsgrupp`.
- **Bjud in med e-post vägrar en medlems adress** (`isMember`, Stefan 2026-10-22): medlemmen bjuds in på
  vanligt sätt och deltar med sitt konto — aldrig som gäst, och aldrig tyst omvandlad.
- **⚠️⚠️ Bjud in = guide, och ingen länk ger åtkomst förrän någon godkänt** (Stefan 2026-10-23). Steg 1
  medlem eller e-post. **Medlem:** Företräder fylls med hens faktiska klubbar och deras kretsar
  (`RepresentOptions`), nivå väljs, hen är med direkt. **E-post:** bara namn/adress/rader → en
  `PlanningRequest` (`Kind=Invite`, `Status=Sent`) och ett mejl med **`Planning.Invite.v1`** (`inv|requestId|
  planningId`, tidsbegränsad) till `/tavlingsplanering/inbjudan?t=` (`PlaneringInbjudan.cshtml`, ingen
  inloggning). Länken ger **bara rätten att svara**: ja + föreslagen Företräder → `Pending`, nej → `Declined`.
  **Bara `ApproveRequest` skapar `PlanningMember`** — då väljs nivå och Företräder (förslaget kan ändras) och
  gästen får det vanliga mejlet med `Planning.Mail.v1`.
- **Be om att få vara med** (`RequestAccess`, `Kind=Request`): bara på planeringar där Sammankallande slagit
  på **"Visa planeringen för alla medlemmar"** (`Planning.ListedForMembers`, förval AV). Listningen visar
  namn, ungefär när/var och Sammankallande — inget innehåll. Sammankallande får mejl (`kind=request`);
  förfrågningarna ligger överst under Deltagare ("Väntar på dig"), räknas i menyn och i listan.
- **Vem godkänner:** Sammankallande (`CanGrant`), eller **sajtadmin som support** (`ResolveApproverAsync`,
  `Support=true`, loggas med `LogWarning`, ger ingen läsrätt — filtret *Support* i listan). Kan ändra kan
  bjuda in men inte godkänna; den som bjöd in ser bara sina obesvarade (`Sent`) och kan dra tillbaka dem.
- **Inbjudningstak `PlanningInviteRules.MaxPerDay = 30` per planering och dygn** (nya medlemmar som någon annan
  lagt till + e-postinbjudningar, rullande 24 h; `UPDLOCK` på planeringen). Alla medlemmar får skapa planeringar.
- **Mejlet "Du är nu med" har länken "Lämna planeringen"** (`Svar?a=lamna` → Deltagare + bekräftelse).
- **Listsidan `/tavlingsplanering` = adminlistans form** (Träningar): Åtgärder → Ny planering, sök, filter
  *Mina / Kan be om att vara med / Arkiverade / Support* (sajtadmin), radens Åtgärder (Öppna, Lämna, Be om
  att få vara med, Dra tillbaka, Hantera förfrågningar). Öppen för alla inloggade.
- **Notiserna = `PlanningNotifier` + `PlanningNotifyRules`** (matrisen står i klassens kommentar). Varje
  utskick **registreras i `PlanningNotifyLog` FÖRE** utskicket (`INSERT … WHERE NOT EXISTS` + unikt index),
  `Succeeded` = FAKTISKT skickat. Svepet `PlanningNotificationHostedService` (var 5:e min, `MayRunScheduled`,
  av med `SiteSetting Planning:Notifications = off`): kvällsmejlet från kl. 18 (en per deltagare och dag,
  `LastDigestMessageId`), timpushen (villkorad UPDATE på `LastPushAt`), mötespåminnelser dagen innan (mejl)
  och en timme före (push) med mötets starttid i nyckeln (ett flyttat möte påminns igen), påminnelse om
  svarsfristen. Sajtadmin: `Planning/RunSweepNow?asOf=` (prov utan att vänta till kvällen). Direkta notiser
  (inbjudan, omnämnande, mötesförfrågan, bokat, flyttat, inställt) skickas av controllern EFTER skrivningen.
  Pushens text bär aldrig meddelandets innehåll. Sajtadmin får gästens länk i svaret (`supportLink`).
- **Kalenderfilen = `PlanningIcsBuilder`:** `UID planning-meeting-{id}@pistol.nu`, `SEQUENCE` (höjs vid
  ombokning och inställt), `TZID=Europe/Stockholm`, `METHOD:PUBLISH`, `URL` escapas inte. Möteslänken:
  bara `https://` (`PlanningService.IsSafeUrl`).
- **⚠️ `@@ROWCOUNT` i en NPoco-sats blir parametern `@ROWCOUNT`** ("@@" är ett escapat "@"). Skriv
  `IF EXISTS … UPDATE … ELSE INSERT`.
- All tid är svensk tid (`…At`, `DateTime.Now`) — inga `…Utc`-kolumner.
- **Möten, uppgifter och roller (Stefan 2026-10-23, skissen ThReUw38FM6kAi7R7JnU1T).** ⚠️ Mötet är en
  INFORMELL avstämning — inga faser, ingen kallelse, närvaro eller justering (det är styrelsens möten).
  - **Möteslistan** = styrelsemötenas form (När · Möte · Läge · Uppgifter · Åtgärder, "Visa tidigare
    möten"). En rad öppnar **mötessidan** `#vy=mote&id=N`: *Att prata om* (`PlanningMeetingTopic`, alla
    som deltar lägger till), *Hur går det med uppgifterna?* (kommer med av sig självt:
    `PlanningTaskRules.ForMeeting` = öppna + de som blev utförda PÅ mötet), *Anteckningar och vem gör vad*
    (anteckningarna sparas vid blur — `flushNotes()` före varje knapp, annars äter omritningen texten).
  - **Uppgifter = egna tabeller `PlanningTask` + `PlanningTaskEvent`** (historik, varje skrivning
    `UPDLOCK` + händelse + `Bump`), INTE tävlingens `WorkItem` (CompetitionId NOT NULL, tävlingens lägen) —
    P2 flyttar förberedelserna HIT. Lägen (`PlanningTaskStatus`): Ej påbörjad → Pågår → Behöver hjälp →
    Klar att pröva → **Utförd**. Den som har uppgiften (även Följer) sätter de fyra första och skriver
    hur det går (`ReportTask`, även via mejllänk). **Utförd/Fortsätter/Ångra (`DecideTask`) = mötet:** alla
    som deltar, men bara på ett **pågående möte** (`MeetingIsCurrent`: bokat, från 12 h före start till
    14 dagar efter slut) — mellan mötena bara den sammankallande (felet förklarar varför). Fortsätter:
    Klar att pröva → Pågår (+ ev. nytt datum). Ny ansvarig får mejl (`TaskAssignedAsync`, aldrig sig
    själv, respekterar Av); mötespåminnelsen dagen innan bär mottagarens öppna uppgifter. Gästlänk
    `a=uppgifter` → fliken Uppgifter med filtret "Mina".
  - **Roller = `PlanningRole`** (flera per person, `SetRoles` ersätter mängden; borttagna får `RemovedAt`
    = historik). ⚠️ **Roller ger INGA rättigheter** — det gör bara nivån. Förslag ur bemanningens
    `FunctionaryRoles` (alla tre grenar) + planeringens egna; `RoleKey` = katalognyckeln, annars null.
    Bara den sammankallande ger roller. "Under tävlingen"-kolumnen kommer med bemanningen (P2b/P4).
  - Regler: `PlanningAccess.CanCreateTasks/CanEditTask/CanReportOnTask/CanDecideTask/CanAddTopic/
    CanRemoveTopic/CanManageRoles`; rena funktioner i `PlanningTaskRules` (`PlanningTaskRulesTests`, 17).
    Ändpunkter i `PlanningController.Tasks.cs` (`Tasks`, `TaskHistory`, `CreateTask`, `UpdateTask`,
    `ReportTask`, `DecideTask`, `RemoveTask`, `AddTopic`, `RemoveTopic`, `SetRoles`).
- **⚠️⚠️ P2 (Stefan 2026-10-23): förberedelserna BOR I PLANERINGEN** — plan
  `notes/tavlingsplanering-p2-plan-2026-10-23.md`. Förberedelserna var beta, så ingen försiktig övergång:
  EN uppgiftsmodell (`PlanningTask`), och den gamla listan (`_WorkBreakdown.cshtml`, ~25 ändpunkter i
  `StaffingController`, `StaffingTemplateService`) är BORTTAGEN. De gamla tabellerna står kvar orörda.
  - **Tävlingens sida** `/tavlingsplanering?c=` → fliken Förberedelser = kortet `_CompetitionPlanningCard`
    (`Planning/CompetitionState`, läser bara): finns planeringen → läget + *Öppna förberedelserna*; annars
    *Starta tävlingens planering* (`StartForCompetition` → `PlanningService.CreateForCompetition`, som i SAMMA
    transaktion skapar planeringen, kopplingen (`PlanningLink`) och FLYTTAR IN tävlingens WorkArea/WorkItem/
    WorkLink/WorkItemComment — idempotent via `ImportedFrom…Id`) eller *Koppla en egen planering*
    (`LinkCompetition`: Sammankallande OCH `CanEditCompetitionAsync`). En planering per tävling, en tävling per
    planering (tills delarna finns, P3).
  - **Tillgång via kopplingen** (`PlanningController.ApplyCompetitionAccessAsync`): den som har
    `HasCompetitionStaffAccessAsync` på den kopplade tävlingen får en rad `ViaCompetition = 1`, Kan ändra,
    notisläge *Bara när någon nämner mig*. Prövas om vid varje inloggat anrop — tappad behörighet = raden tas
    bort. ⚠️ Mejllänken kan inte pröva om (gäller högst 60 dagar). Ändrar den sammankallande nivån blir
    personen en vanlig deltagare. Koppla loss tar bort alla `ViaCompetition`-rader.
  - **`Planning.DoneAtMeetingsOnly`**: PÅ för en namngiven planering, AV för tävlingens egen — då bockar den
    ansvarige och Kan ändra av direkt (`PlanningAccess.CanDecideTask(c, atMeeting, planning, task)`; Utförd i
    `ReportTask` går till `DecideTask`). Den sammankallande ändrar under Inställningar.
  - **Uppgiften** fick `AreaId` (`PlanningArea`, områden med ansvarig), `AssigneeName` (fritt namn),
    `DueDaysBefore` ("N dagar före" — `DueDate` materialiseras ur ankaret = tävlingens dag, annars planeringens
    första dag, och räknas om när uppgifterna läses: `RealignRelativeDueDates`), `ScopeType/ScopeKey`
    (Station N länkar till Fältkonfiguratorn), `EstimatedCost`, `SortOrder`, `SourceKind/SourceKey`.
    Kommentarer = `PlanningTaskEvent` Kind `Comment`. Dokument och filer = `PlanningDocument` (filen i
    `PrepDocumentStorage`, `DownloadDocument` grindas av planeringen). Beroenden togs INTE med (0 använda).
  - En uppgift till en medlem som inte är med → läggs till som Deltar (`EnsureParticipantForMember`, inom
    inbjudningstaket). `SearchMembers` öppen för den som skapar uppgifter.
  - **Fylla i:** standardmallen (`PrepTemplates` i `WorkBreakdownService.cs`, `SeedTemplate`, gren ur tävlingen
    eller vald), kretsens checklista (`KretsChecklist`/`ApplyKretsChecklist` → `AddChecklistTasks` +
    `KretsPlanningService.MarkHandled`; `RegionChecklistApplied` står kvar per tävling), en uppgift per station
    (`SeedStations`, kopplad fälttävling), inklistring ur kalkylark (`PasteTasks`, förhandsvisning först, ett
    oläsligt datum stoppar allt — `PlanningTaskRules.ParsePaste`). CSV: `ExportTasks`. Materielberäkningen
    och fältets materiellista nås från Uppgifters Åtgärder.
  - **Påminnelser:** *Påminn försenade* (`RemindOverdue`, ett mejl per person och dag, kind `task-overdue`)
    och svepet tre dagar före sista dagen (`DueRemindersAsync`, kind `task-due`, ett mejl per person, varje
    uppgift registrerad med sista dagen i nyckeln).
  - **`WorkBreakdownService` är en LÄSADAPTER** över tävlingens planering i den gamla formen — läses av
    bemanningens personlista (`CompetitionPeopleService`) och utskriftsbladet. Den skriver ingenting.
  - Test: `PlanningPrepRulesTests`. Svit: `hpsk-verify/tavlingsplanering-p2-verify.mjs` 86/86 (fixturer 2576
    och 5312; avbryter om de redan har en planering; A/B utan `DoneAtMeetingsOnly`-grenen och utan att
    tappad behörighet tar bort raden → 4 röda).
- **Dokumentarkivet (2026-10-23, Stefans önskemål):** rälsposten **Dokument** (`#vy=dokument`). Ett dokument
  (fil eller länk) bor i arkivet med en valfri **kategori** (`PlanningDocumentCategory`) och kan **bifogas** på
  flera ställen genom `PlanningDocumentRef` (TargetKind `Message|Meeting|Topic|Task|Area` + TargetId — sorten
  följer alltid id:t). `PlanningDocument.AreaId/TaskId` är BORTTAGNA; P2:s import skriver bifogningar.
  - **Inga behörighetsnivåer per dokument** (Stefan): alla som är med läser allt, även Följer och mejllänken.
    Lägga till/bifoga = Deltar och uppåt; ändra/ta bort dokument = den som lade till eller Kan ändra;
    kategorier = Kan ändra; ta bort en bifogning = den som bifogade eller Kan ändra. Till ett MEDDELANDE
    bifogar bara författaren (`Post` tar `documents`, eller i efterhand ur meddelandets meny).
  - Målet prövas mot planeringen i `TargetExists` (raderat mål vägras). En bifogning till ett borttaget
    dokument visas som *Borttaget dokument* (`removedRefs`); att ta bort en bifogning rör aldrig dokumentet.
  - **Utrymme per planering:** `DocumentArchive:DefaultPlanningStorageLimitMB` (300 i appsettings, 200 som
    förval i koden); summan av `FileSize`. Filerna i `PrepDocuments` (25 MB per fil, som förut).
  - Klienten läser allt ur EN endpoint, `Documents` (kategorier, dokument med sina bifogningar och målens
    rubriker, lagring), som pollas med resten. `Tasks` bär inte längre `documents`.
  - Svit: `hpsk-verify/tavlingsplanering-dokument-verify.mjs` 62/62 (A/B: målet prövat utan planeringen +
    vem som helst får ta bort en bifogning → 6 röda).
- **P2b: bemanningen hör till PLANERINGEN (2026-10-23)** — även innan planeringen kopplats till en tävling.
  - **Ägaren = `StaffOwner(PlanningId, CompetitionId?)`** (`Models/Staffing/StaffOwner.cs`). Tre lägen:
    kopplad planering (båda), planering utan tävling (bara planeringen), tävling utan planering (bara
    tävlingen — de äldre raderna, PlanningId NULL). De nio Staff-tabellerna har `PlanningId` och en nullbar
    `CompetitionId` (`alter-staffing-planning-p2b.sql`); `StaffRole` har ägartypen `Planning`.
  - ⚠️ **Varje fråga på en Staff-tabell per ägare går genom `StaffOwner.Where(i, alias)`** — "planeringens
    rader, eller tävlingens som ännu saknar planering". Varje insättning sätter BÅDA id:na
    (`PlanningIdOrNull`, `CompetitionIdOrNull`). En fråga direkt på `CompetitionId` är fortfarande RÄTT för
    det som gäller tävlingen (Mitt schema, appbehörigheten, raderingsspärren): en kopplad planerings rader
    bär tävlingens id, en losskopplad planerings rader har det inte.
  - **`StaffOwnerResolver`** (scoped) är den ENDA platsen som tar fram ägaren (`ForCompetition`,
    `ForPlanning`, `OfRow`) och vad ägaren vet om sig själv: gren (tävlingens, annars `Planning.DisciplineKey`),
    namn, dagar (`Span`: tävlingens datum, annars planeringens mål i tid).
  - **Kopplingen flyttar bemanningen** (`StaffingPlanningLink`, i PlanningService transaktion): `Adopt` tar in
    tävlingens äldre rader (unika krockar — samma dag, behov, anmälan, källa, roll — planeringens rad vinner),
    `Attach` sätter tävlingens id, `Detach` nollar det. Efteråt kör `PlanningController.AfterStaffingLinkChanged`
    speglingarna (`StaffingService.SyncCompetitionMirrors`: tävlingsansvariga + stationschefer) och lägger till
    tävlingens dagar (`StaffDayService.AddCompetitionDays`). Losskopplad = ingen appbehörighet till tävlingen.
  - **Behörighet** (`StaffingController.OwnerAsync`/`RowOwnerAsync`): med `?planningId=` — Kan ändra i
    planeringen (`PlanningAccess.CanEditStaffing`) ELLER tillgång till den kopplade tävlingen; utan —
    tävlingens behörighet, som förut. **Id-baserade anrop prövas mot RADENS egen ägare**, aldrig mot ett id i
    anropet. Appbehörighet (`HasAdminAccess`) kan bara ges när planeringen är kopplad.
  - ⚠️⚠️ **OMGJORT 2026-10-24 (omgång 2, steg 2): bemanningen är en DEL i planeringen, `#vy=bemanning`** —
    `wwwroot/js/planering-bemanning.js` (`window.PlaneringBemanning.render(api)`/`leave()`) + `wwwroot/css/planering-bemanning.css`,
    laddade av `Planering.cshtml` FÖRE `planering.js`. Sidan `PlaneringBemanning.cshtml` är BORTTAGEN;
    `/tavlingsplanering/{id}/bemanning` (och `?c=…&vy=bemanning`, `StaffOwnerResolver.ManageUrl`, `planning.staffingUrl`)
    leder till `/tavlingsplanering/{id}#vy=bemanning`. Stefan: "lösryckt … knappen Mer … modalerna låsta mot vänsterkanten".
    - **Tre delar via växeln** (`&del=` i adressen): **Rutnät** (rad = roll, kolumn = dag; rutans snabbinmatning
      `#plbEditor` står kvar öppen för nästa namn), **Personer** (sök, filter, *Gruppera per förening* = det gamla
      "Per förening"; namnet öppnar personen i sidan, `&person=<nyckel>`, ← Alla personer + bakåtknappen), **Hitta
      funktionärer** (tre kort). Planeringens form: blå Åtgärder per del, grå `rowMenu` på roller, dagar, personer,
      uppdrag och "något annat"; `formModal`/`plConfirm`. **Inga offcanvas, inga prompt/confirm/alert** (sviten mäter det).
    - Skalet ger modulen ett API (`STAFF_API` i planering.js: `req`, `token`, `setHead`, `setActions`, `rowMenu`,
      `formModal`, `plConfirm`, `showMsg`, `failMsg`, `datePicker`, `S()`, `isActive()`). Modulen pollar sig själv
      (15 s, aldrig med dialog/meny/rutan öppen eller fokus i ett fält); planeringens pollning ritar inte om delen.
    - ⚠️⚠️ **`SaveAssignment` skriver ALLA fält** (även e-post, telefon, plats, ansvarig, behörighet) — uppdragets
      dialog hämtar raden ur `GetRoster` och skickar med det den inte visar. Dialogen visar bara person, roll, dag,
      tid, läge och anteckning; skiftet (`PassId`) behålls bara om varken dag eller tid ändrats. Den gamla dialogen
      tömde e-posten vid varje ändring. **Skift & behov** (StaffPass-ytan) finns inte längre i ytan; endpoints och
      tabeller står kvar — kör `Migrations/check-prod-staffpass-2026-10-24.sql` i prod innan något tas bort.
    - ⚠️ **`SaveRole` skriver alla rollens flaggor** — namnbytet skickar med dem ur `GetRoles`; och
      `RoleCatalogService.SaveRole` rör inte längre `SortOrder` när anropet saknar en (0): ett namnbyte flyttade
      förut raden högst upp (A/B: 1 röd).
    - ⚠️ En öppen radmeny i en låst cell hamnade under nästa rads låsta cell — `td/th:has(.dropdown-menu.show)`
      får högre `z-index` (sviten fann det).
    - Svit `hpsk-verify/tavlingsplanering-bemanningsvy-verify.mjs` 65/65 (A/B: e-posten utan att skickas med → 1 röd;
      namnbytet som nollställer ordningen → 1 röd). bemanning-, hitta-, en- och p3-sviterna uppdaterade till delen.
  - (Historik:) Sidan `/tavlingsplanering/{id}/bemanning` (`PlaneringBemanning.cshtml`) visade samma `_StaffingRoster`/
    `_StaffingGrid` som tävlingens planeringssida; ett fetch-tillägg lade `planningId` på varje anrop. Inställningar
    har Gren (bara utan tävling). **Dagsprogram och materiel stannar på tävlingen.**
  - **Självanmälan:** `/bemanna?p={planering}` (`StaffOwner.SignupPath`); `/bemanna?c=` fungerar som förut och
    leder till tävlingens planering när den har en. Mina uppdrag grupperar per ägare (`MyCompetitionGroup.PlanningId`).
  - Svit: `hpsk-verify/tavlingsplanering-bemanning-verify.mjs` 49/49 (A/B: radägarens prövning släppt +
    losskopplingen nollar inte tävlingen → 11 röda). ⚠️ Inte builder.claude som "utan behörighet" — hen är
    kretsadmin i Halland och kommer åt Ankeborgs tävlingar.
- **P2c: behovet per roll och dag, "Hitta funktionärer" och anmälan som bygger på behoven (2026-10-23)**.
  - **`StaffCrewNeed.DayDate`**: NULL = rollens antal för alla dagar, ett datum = antalet just den dagen (0 =
    behövs inte). Reglerna i **`CrewNeedRules`** (ren, `CrewNeedRulesTests`): `ForDay` (dagens rad, annars
    alla dagars), `Default`, `Plan` (lagrar bara dagar som avviker), `EffectiveFor`. `SetRoleNeeds` skriver hela
    rollens plan; `SaveCrewNeeds`/`GetCrewNeeds` gäller bara alla-dagar-raderna. Rutnätet: `GridRow.NeededByDay`
    + `NeedFor(kolumn)`, dialogen `#grdNeedModal` (snabbval per dagens art). Kopplingen (`StaffingPlanningLink`)
    tar med `DayDate` i nyckeln, null-säkert.
  - **`StaffHelpService.NeedDays(owner)`** = planens dagar (eller ägarens datum, utan att skriva) × roller med
    behov, med `Missing` = behov − bemannade (ej avböjda; dag via starttid → dag → passets dag). Det är vad
    anmälningssidan och panelens steg 2 visar. `HelpSlotChoice` bär `Date` + `RoleKey` (SlotId 0; tom roll =
    "var det behövs"); `SaveMySignup` godtar en dag bara när dagen har behov och rollen behövs den dagen.
  - **`Planning.SignupRoleChoice`** (förval 1): får medlemmen välja roll? `StaffOwnerResolver.SignupRoleChoice`
    (tävling utan planering: ja). Sätts i panelen (`Staffing/SetSignupRoleChoice`, sträng "1"/"0").
  - **Panelen "Hitta funktionärer"** (`#bemVarvningPanel`, hette Värva funktionärer): 1. vilka som får anmäla sig
    — klubbar och kretsar i en sökbar lista (`Staffing/GetSourceScopeOptions` = `StaffingSignupService.ScopeOptions`;
    Ankeland utesluten; arrangörens klubb och krets som förslag bara när tävlingen finns; `AddScope` vägrar
    okänd klubb/krets; kretsen visas med förbundsnamnet), 2. behoven (`Staffing/GetSignupOverview`) + rollvalet +
    "Något annat ni behöver hjälp med" (de gamla hjälppassen), 3. förfrågan, kopiera länken, visa som medlem.
    Ordet "pass" står inte där.
  - **`/bemanna`** är opt-in, grupperad per dag: roller med "N fattas av M" + *Var det behövs* (rollval på) eller
    *Jag kan hjälpa till den här dagen* (av), och "Något annat" under sin dag. Kryssrutorna bär index, aldrig text.
  - Migreringar `alter-staffcrewneed-per-day.sql` och `add-signuprolechoice-to-planning.sql` (P2c/P2d), FÖRE deploy.
  - Svit: `hpsk-verify/tavlingsplanering-hitta-verify.mjs` 47/47 (A/B: dagvalens prövning släppt → 1 röd).
- **EN planering, inte två (Stefan 2026-10-23: "de ska bli en")**. `/tavlingsplanering?c=` (tävlingens knapp
  Planering) **omdirigerar** till tävlingens planering när den finns och man har tillgång till tävlingen
  (`?vy=bemanning|dagsprogram` → planeringens delar); annars visar sidan bara startkortet
  (`_CompetitionPlanningCard`: Starta tävlingens planering / koppla en egen). Flikarna Bemanning och Dagsprogram
  på tävlingens sida finns inte längre. **Dagsprogrammet** är en del i planeringen när den är kopplad:
  `/tavlingsplanering/{id}/dagsprogram` (`PlaneringDagsprogram.cshtml`, samma `_CompetitionAgenda`), rälsens
  "Dagsprogram" bara med `planning.agendaUrl` (= kopplad). Agendaanropen bär `planningId` och prövas av
  `StaffingController.CanManageAgendaAsync`: tävlingens behörighet, eller Kan ändra i planeringen som är kopplad
  till JUST den tävlingen. Planeringens rad om tävlingen länkar till tävlingshanteringen (inte tillbaka till
  `?c=`, som nu är planeringen själv). Svit: `hpsk-verify/tavlingsplanering-en-verify.mjs` 26/26 (A/B utan
  omdirigeringen → 3 röda); p2-sviten uppdaterad (86/86).
- **Småsakerna efter sammanslagningen (2026-10-23):** materielberäkningen (`GetMaterielEstimate`) går genom
  `CanManageAgendaAsync` (kopplingen); utskriften `/tavlingsplanering/blad?p=` tar alla vyer även utan tävling
  (`PlaneringBladModel.PlanningId`, `AgendaAvailable`; Översikt → Åtgärder → *Skriv ut hela planen*); deltagarlistan
  visar **Under tävlingen** (`StaffingService.DutiesByMember`, `participants[].duties`). Meddelanden: skrivrutan
  överst, trådarna efter senaste händelsen, 15 åt gången (`data-pl-older`), svaret skrivs under tråden; inte
  längre sticky. `.pl-card.p-0 { overflow: hidden }` så tabellkort följer de rundade hörnen. Rälsens Bemanning/
  Dagsprogram utan ny-flik-ikon (öppnas i samma flik). Svit `tavlingsplanering-en-verify.mjs` 37/37 (A/B 6 röda).
- **Bemanning och Dagsprogram i planeringens ram (2026-10-23, 3ab1bb9):** `PlaneringBemanning`/`PlaneringDagsprogram`
  renderar `.pl-page .pl-shell` med rälsen (`Partials/_PlaneringShellStyles.cshtml`); `planering.js` med
  `CFG.railOnly` ritar bara rälsen, länkarna går till `/tavlingsplanering/{id}#vy=…`.
- **P3 steg A, milstolpar och Skicka till förbundet (2026-10-23):** plan `notes/tavlingsplanering-p3-arbetsplan-2026-10-23.md`.
  `PlanningTask.IsMilestone`/`SendToForbundet`/`SentToForbundetAt`/`SentToForbundetBy` (migrering
  `add-milestones-to-planning-task.sql`, körordning P3a). SM-mallarna (`WorkBreakdownService`) får området
  **Förbundet** först: stomprogrammet (30 juni året före, SHB C.3.5.3.1) räknas ur tävlingens dag — ⚠️ ingen
  ansökan 1 okt för SM (C.3.5.3.2 gäller nationella/landsdelstävlingar; Stefan 2026-10-24 "SHB har säkert rätt");
  förslaget till inbjudan till förbundet för godkännande (C.3.5.2.1, skickas, 4 veckor före NP-manusstoppet) och
  "Publicera inbjudan" bara efter godkännandet;
  (`PrepItem.DueFromAnchor`), inbjudan i tid till tidningen (datum när redaktören svarat om manusstoppet) och för
  fält underlaget 12 veckor före. Förbundets adress = sajtinställningen `Planning:ForbundetEmail` (sajtadmin under
  Inställningar, `SetForbundetEmail`). `SendToForbundet` bifogar högst 10 dokument / 15 MB ur arkivet
  (länkdokument i texten), svarsadress = avsändaren, och bockar av milstolpen **bara när mejlet gick iväg**
  (`MarkSentToForbundet` + systemmeddelande). Uppgifter: filtret Milstolpar, flaggan på raden; Översikt: kortet
  Milstolpar. Svit `hpsk-verify/tavlingsplanering-p3-verify.mjs` 25/25 (A/B: mallens flagga + uppdateringens
  "behåll" fäller 6).
- **P3 steg B, förslag och beslut (2026-10-23):** `PlanningDecision` + `PlanningDecisionResponse` (migrering
  `create-planning-decisions.sql`, körordning P3b), `PlanningService.Decisions.cs`, `PlanningController.Decisions.cs`,
  regler i `PlanningDecisionRules` + `PlanningAccess.CanProposeDecision/CanRespondToDecision/CanEditDecision/CanDecideDecision`.
  Rälsposten **Förslag och beslut** (`#vy=beslut`). Deltar och uppåt lägger fram och svarar *Vi kan / Vi kan inte*
  + kommentar (en rad per deltagare; via mejllänken också). ⚠️ Svaren raderas inte när förslaget ändras — de
  märks "svarade före ändringen" (`AnsweredBeforeEdit`). Kan ändra registrerar beslutet (arbetsgruppens eller en
  namngiven styrelses; kan ångras) och skickar förslaget till **styrelsen i en klubb eller krets som någon i
  planeringen företräder** (`BoardTargets`, fri text räknas inte): `BoardWorkService.CreateIssue` med källan
  `BoardIssueSources.Planning` ("Planering", `SourceRefId` = förslaget), underlaget bär bakgrunden + svaren,
  `BoardWorkNotifier.IssueSubmittedAsync` aviserar sekreterare/ordförande; avsändaren (inloggad medlem) får
  styrelsens besked. ⚠️ Styrelsens läge lagras inte på förslaget — det läses ur ärendekön (`GetIssuePlacements`):
  väntar / på dagordningen / behandlat (+ styrelsens beslut) / avvisat. Medan ärendet ligger hos styrelsen får
  förslaget inte ändras, dras tillbaka eller skickas igen (`IsAtBoard`). Varje händelse ger en systemrad.
  Svit `tavlingsplanering-p3-verify.mjs` 63/63 (A/B: målföreningens prövning + dubbelutskicket → 5 röda).
  - ⚠️⚠️ **OMGJORT 2026-10-24 (Stefans genomgång) — läs det här, inte stycket ovan, om svar och styrelser:**
    - **Beslutsfattaren väljs när förslaget läggs fram** (`PlanningDecision.DeciderKind`): **Group** (de som deltar
      svarar **Ja/Nej**, `Yes`/`No`; Kan ändra registrerar gruppens beslut) ELLER **Board** (styrelsen i en klubb/krets
      som någon företräder; målet sparas vid skapandet i `BoardOwnerType/Id`). **Aldrig båda:** ett styrelseförslag
      tar inga svar (`RespondRefusal`), väntar inte på någon (`Waiting(d, …)`), räknas inte i rälsens siffra, och
      gruppen registrerar inte dess beslut. Bara Kan ändra (inloggad) lägger fram ett styrelseförslag.
    - **Bilagor**: `PlanningDocTarget.Decision` (CHECK-regeln vidgad). `SendDecisionToBoard(decisionId)` KOPIERAR
      filerna till dokumentarkivets lagring (`BoardIssueAttachments`, högst 10; länkar som `Url`) — styrelsen är inte
      med i planeringen. Dialogen Nytt förslag väljer bilagor via `pickDocuments({onPick})`; ⚠️ `onPick` som öppnar
      nästa dialog i samma fönster svarar `false`, annars stängs det.
    - **Styrelsen kan besluta utan möte** (`BoardWork/DecideIssue`, den som placerar: sekreterare/ordförande/admin):
      utfall Bifall/Avslag, text och vilka ledamöter som stod bakom (bara styrelsens). LAGRAS på ärendet
      (`BoardIssues.Direct*`) och vinner över placeringen (`StateOf(…, directlyDecided)` → `DecidedDirect`).
      **Anmäls på nästa olåsta möte** som underpunkt till *Beslut fattade mellan möten* (`ReportDirectDecisions`,
      punkten skapas före Övriga frågor), och när ett nytt möte skapas om inget fanns. Den som anmälde får beskedet
      direkt (`DirectDecisionAsync`); `DecisionNotifiedDate` sätts så att justeringen inte skickar ett andra.
      Ångras med `UndoIssueDecision` så länge anmälan inte står i ett låst protokoll.
    - **Avsändaren** i styrelsens kö är planeringens namn (`IssueView.SourceName`, "SM 2028 (skickat av …)").
    - Planeringens kort visar styrelsens läge: inte skickat / hos styrelsen / beslutat utan möte / behandlat; ett
      styrelseförslag blir Avgjort när styrelsen beslutat (härlett, lagras inte).
    - Migreringar: `create-planning-decisions.sql` (P3b, rättar dev) och `add-direct-decision-and-attachments-to-board-issues.sql`
      (P3f, FÖRE deploy — NPoco skriver kolumnerna vid varje uppdatering av ett ärende). Svit p3 169/169 (A/B: läget,
      svarsspärren och kopian av bilagorna → 8 röda). ⚠️ Sviten gömmer klubb 2604:s riktiga årsmöte en stund för att pröva
      "inget kommande möte" och ställer tillbaka det i `finally`; varje `waitForResponse` har `.catch` — ett ofångat
      avvisat löfte dödar node innan `finally` hinner köras.
- **P3 steg C, kopiera planeringen och lämna över (2026-10-23):** `PlanningService.Copy.cs` / `PlanningController.Copy.cs`
  (`CopyPlanning`, `HandOver`), regler i `PlanningCopyRules`. Bara den sammankallande. EN transaktion (yttre scope;
  `Create` och `AddParticipant` ansluter). Följer med: delar, områden (ansvarig bara som fritt namn), uppgifter och
  milstolpar (från början, utan ansvarig deltagare), bemanningens roller (planeringens + tävlingens för nycklar utan
  egen), dagar och behov. Inte: personer, meddelanden, möten, dokument, förslag, uppdrag. `CopiedFromPlanningId`.
  ⚠️ Datumen: bemanningens dagar flyttas lika många DAGAR som första dagen (`Shift`, veckodagen behålls); en uppgifts
  fasta datum flyttas i HELA ÅR när kopian ligger ±7 dagar från ett helt antal år senare (`ShiftDeadline` — förbundets
  1 oktober förblir 1 oktober), annars i dagar. Utan första dag (källa eller kopia) följer fasta datum och dagar inte
  med, och svaret räknar dem (`datesDropped`, `daysDropped`). Mottagaren (jag eller en medlem) blir sammankallande och
  får inbjudningsmejlet; jag är med som Kan ändra om jag vill. `HandOver`: en annan deltagare med konto blir
  sammankallande och jag Kan ändra, i ett steg. Ytor: Inställningar → Åtgärder → Kopiera planeringen…; Deltagare →
  radens Lämna över ledningen till … Svit p3 92/92 (A/B: årsflytten + sänkningen vid överlämning → 5 röda).
- **P3 steg D, delar som organisation (2026-10-23):** `PlanningPart` fick beskrivning, gren, ansvarig (deltagare eller
  fritt namn) och värd (klubb ELLER krets ELLER text; bara klubbar/kretsar som någon i planeringen företräder) —
  migrering `add-planning-parts-organisation.sql` (körordning P3c), som också lägger `PartId` på `PlanningTask` och
  `PlanningMessage`. ⚠️ `Kind` styrs av `CK_PlanningPart_Kind`: `Root`, `Gren`, `Ansvar` (`PlanningPartKind`) — grunddelen
  är planeringen själv och visas aldrig som del. `PlanningService.Parts.cs` / `PlanningController.Parts.cs` (`SavePart`,
  `RemovePart`, `SetMessagePart`, `PartHostOptions`); `Get` bär `parts`. Uppgifter (`partId`: utelämnat = behåll,
  0 = hela planeringen) och meddelanden (svaret ärver trådens del) märks och filtreras; en borttagen del lämnar dem kvar
  för hela planeringen. Rälsposten **Delar** (synlig när det finns delar eller man kan ändra). Kopian tar med delarna
  (ansvarig bara som fritt namn) och uppgifternas del. ⚠️ Bemanning och koppling per del är INTE byggt (P4) — de gäller
  hela planeringen. Svit p3 115/115 (A/B: delens planeringsprövning + borttagningens lösgöring → 2 röda).
- **P3 steg E, "Ställer upp för" (beslut 9.6, 2026-10-23):** `StaffAssignment.RepresentsClubId`/`RepresentsRegionId`
  (migrering `add-represents-to-staff-assignment.sql`, körordning P3d). Gäller PERSONEN: `StaffingService.SetPersonRepresents`
  skriver alla hens rader (samma skäl som behörigheten); båda null = förvalet, medlemmens huvudklubb (härlett, lagras
  inte). `CompetitionPeopleService.FinishRepresents` ger `RepresentsType/Id/Name/IsDefault`, valen (medlemmens klubbar +
  deras kretsar ur trädet; för en utan konto de föreningar andra i bemanningen har) och `ByOrganisation` (personer och
  uppdrag per förening, i `GetPeople`). `Staffing/SetPersonRepresents` godtar bara ett av personens val. Ytor:
  personpanelen (rullgardinen "Ställer upp för") och Mer → **Per förening…** (stänger en öppen sidopanel först).
  Underlag när ett överskott delas — själva fördelningen (fråga 15) är inte byggd. Svit p3 129/129 (A/B: valprövningen
  + skrivningen på alla rader → 3 röda).
- **P3 steg F, ansökan ur planeringen (beslut 9.7, 2026-10-23):** planeringen gör ett UTKAST i klubbens vanliga
  tävlingsansökningar (`CompetitionApplication.PlanningId`, migrering `add-planningid-to-competition-application.sql`,
  körordning P3e) — ingen andra blankett; själva ansökan skickas in under klubbens tävlingar, med kretsens regler.
  `SaveApplication` med `planningId` (bara vid en NY ansökan): den som skickar måste kunna ändra planeringen, planeringen
  får inte vara kopplad, och bara en ansökan i taget (`PlanningApplicationLink.ForPlanning` = senaste icke-återkallade).
  Klubbarna som erbjuds = de som någon i planeringen företräder OCH som den som frågar administrerar (`ApplicationJsonAsync`,
  inte alla klubbar en sajtadmin når). ⚠️ **Kopplingen görs på servern** i `CompetitionAdminController.LinkApplicationIfAny`:
  när tävlingen skapas ur ansökan kopplas den också till planeringen (`PlanningApplicationLink.LinkCompetition` =
  `LinkToCompetition` + speglarna + tävlingsdagarna); går det inte svarar skapandet med en `applicationNote`. Ytor: kortet
  på Översikt (`#plAppCard`: frågan, eller ansökans läge + länk till `klubbens url#clubAdmin/competitions`) och Åtgärder →
  *Sök om tävlingen hos kretsen…*. Svit p3 144/144 (A/B: planeringsspärren + kopplingen → 4 röda).
  - ⚠️⚠️ **OMGJORT 2026-10-24 (omgång 2, steg 3): rälsposten Tävlingen, `#vy=tavling`** (`viewCompetition` i
    planering.js). Kortet på Översikt, den korta ansökningsdialogen och Inställningar → Tävlingen är BORTA. Översikt har
    bara en rad under Nästa steg (`#plCompNext`, `competitionNextStep()`) som leder hit. Vyn: *Vägen till tävlingen* i
    fyra steg (`#plCompSteps`: ansökan · kretsen säger ja · tävlingen skapas · planeringen kopplas; "Ingen ansökan
    behövdes" när tävlingen kopplades utan ansökan), det som gäller nu med knappen för steget, kortet Tävlingen när
    planeringen är kopplad (`#plCompCard`), ansökan som rader (`#plAppCard`) och historiken (`#plAppHistory`).
    Ansökan läses med `CompetitionApplication/GetApplication?asClub=true` — `asClub` ger klubbens knappar även till den
    som också är krets (sajtadmin). **Läsrätt för planeringens deltagare** (`PlanningReader`: aktiv rad i
    `a.PlanningId`) i `GetApplication` och `PrintApplication`; ändra/skicka in/svara/skapa kräver fortfarande klubbens
    rätt. **Klubbens fullständiga dialog** (`_CompetitionApplications` + `CompetitionWizardModal` tas med i
    Planering.cshtml utom i länkläget): `hpskAppOpenForm` tar nu `planningId`, `prefill`, `onSaved`;
    `hpskAppCreateCompetition(id, onSaved)`. **Guiden**: `window.onCompetitionSaved(data)` får svaret och returnerar
    `true` när anroparen själv berättar — då ingen `alert()`; `hpskWizardFromPlanning(p)` förifyller en
    föreningstävling (banderollen återanvänder id `wizard_applicationBanner` så att `resetWizard` tar bort den).
    Föreningstävling direkt = guiden + `Planning/LinkCompetition` med guidens `data.data.id`. **Nytt**:
    `CompetitionApplication/DeleteApplication` (bara `ClubCanDelete`: Utkast, aldrig inskickad, ingen tävling — annars
    återkallas den), `PrintApplication` (Views/CompetitionApplicationPrint.cshtml), `Planning/LinkableCompetitions`
    (klubbarna någon företräder, kan redigeras, utan planering, **närmast planeringens datum först** — sorterat från i dag
    låg 2028 utanför de 30), DTO:n har `canDelete`, `planningId`, `planningName` (klubbens lista: "Från planeringen …").
    Svit `tavlingsplanering-tavling-verify.mjs` 57/57 (A/B: utan asClub → 2 röda; PlanningReader nekar → 3 röda;
    ClubCanDelete utan statusvillkoret → 4 röda). ⚠️ Guidens åtta steg körs inte i sviten (fältpåsen + återanropet).
- ⚠️⚠️ **ERSATT 2026-10-25 av "Områden och delplaneringar, Expertgruppens modell" nedan** — områdets beskrivning,
  värd, `AreaHostOptions`, `#stAreas` och "＋ Nytt område…" finns inte längre. Historik:
  **OMGJORT 2026-10-24 (omgång 2, steg 4): Områden ersätter Delar.** Steg D ovan gäller inte längre för
  uppgifter/meddelanden: migrering `merge-planning-parts-into-areas.sql` (körordning efter P3c) slår ihop delarna
  (utom `Root`) med `PlanningArea` (samma namn = samma område), flyttar `PartId` → `AreaId` och **tar bort
  `PlanningTask.PartId` och `PlanningMessage.PartId`**. `PlanningArea` fick `Description`, `HostClubId`/`HostRegionId`/
  `HostText`; `PlanningMessage.AreaId` och `PlanningDocumentCategory.AreaId` är nya. `PlanningPart` finns kvar BARA för
  grunddelen (kopplingens ankare) — `SavePart`/`RemovePart`/`SetMessagePart`/`PartHostOptions` och `Get.parts` är borta;
  nu `SaveArea(..., description, hostType, hostId, hostText, details="1")` (utan `details` behålls de),
  `AreaHostOptions`, `SetMessageArea` (hela tråden; författaren eller den som kan ändra), `Post(..., areaId)` (svaret
  ärver trådens), `SaveDocumentCategory(..., areaId)` (null = behåll, 0 = inget). `Get`/`Tasks` bär `areas`
  (`AreasJson`: ansvarig, värd, öppna/alla uppgifter). `DeleteArea` lösgör uppgifter, meddelanden OCH kategorier.
  Rälsposten Delar är borta; områdena hanteras i Inställningar-kortet `#stAreas`. **"＋ Nytt område…"** i varje
  områdeslista (uppgift, inklistring, meddelande, kategori) är en rad i samma dialog (`wireNewArea`) — aldrig en dialog
  i dialogen. **Grenen** väljs när planeringen skapas (`Planning/Create` tar `discipline`, prövas mot
  `CompetitionTypes`) och ändras på raden `#plbDisc` i Bemanningens rutnät (`SetDiscipline`); Gren-kortet i
  Inställningar är borta. Ingen gren per område. Svit p3 180/180 (A/B: områdesprövningen i `Post` → 1 röd;
  `wireNewArea` som inte sparar → 2 röda).
- ⚠️⚠️ **ERSATT 2026-10-25 — `SplitArea`/`SplitPreview`/`CanSplitArea`/`openSplit`/`IsParentConvener` finns inte
  längre; se nästa punkt.** Historik: **Omgång 2 steg 5 (2026-10-24): dela ut ett område till en egen planering.** `Planning.ParentPlanningId` (migrering
  `add-parent-to-planning.sql`, körordning P3h) — EN nivå: `PlanningAccess.CanSplitArea` = sammankallande, inloggad, och
  planeringen är inte själv en delplanering. `PlanningService.Split.cs`: `SplitPreview` (skriver inget; förslag till
  deltagare, dokument och sammankallande) och `SplitArea` (EN transaktion): ny planering via `Create` med den valda som
  Owner (gäst vägras), gren + `DoneAtMeetingsOnly`/`SignupRoleChoice`/målen följer; valda deltagare KOPIERAS (Owner →
  Editor, bara den valda leder); områdets uppgifter (+ `PlanningTaskEvent`) och trådar (+ svaren + omnämnanden) flyttar med
  deltagar-id:n omskrivna — den som nämns i det som flyttar men inte valdes följer med som BORTTAGEN rad (namnen står kvar;
  en medlem = en rad, `UX_PlanningMember_Member`). Mötes-id:n på uppgifter nollas (mötena stannar). Valda dokument flyttar
  (kategori med samma namn skapas); en bifogning mellan något som flyttar och något som stannar släpps (`RemovedAt`,
  räknas i svaret). Området mjukraderas, kategoriernas `AreaId` nollas, systemrader i båda (`planning-split`,
  `planning-split-child`). **Läsaren:** `PlanningCaller.ParentReader` (det enda undantaget i `PlanningRulesTests`) —
  `ResolveAsync(..., allowParentReader: true)` bara i de läsande anropen (Get, Version, Messages, Meetings, Decisions,
  Documents, DownloadDocument, Tasks, TaskHistory); `IsParentConvener` = aktiv Owner i huvudplaneringen. Läsaren är ingen
  deltagare: skriver inget, får inga notiser, inget läst-läge (`unreadCount` 0), ingen Bemanning/Mina notiser/Inställningar.
  `Get` bär `planning.parent` (url bara för huvudplaneringens deltagare), `planning.subPlannings` (läget: sammankallande,
  öppna/alla/försenade uppgifter, nästa milstolpe och möte; url för huvudplaneringens sammankallande och delplaneringens
  deltagare) och `me.isParentReader`, `me.canSplit`. Ytor: radmenyn i `#stAreas` (*Dela ut till en egen planering…*,
  `openSplit`), kortet `#plSubPlannings` på Översikt, raden "Del av …" och kortet `#plParentReader`. Svit
  `tavlingsplanering-dela-verify.mjs` 57/57 (A/B: läsaren utan Owner-villkoret + bifogningarna inte släppta → 3 röda).
- ⭐ **Områden och delplaneringar, Expertgruppens modell (Stefan 2026-10-25: "Kör på expertgruppens rekommendation")** —
  utvärderingen `notes/expertgrupp-omraden-delplaneringar-2026-10-25.md` (B1–B12). TVÅ skilda begrepp:
  - **Område = ansvarsområde:** `PlanningAreaRow` har bara namn + ansvarig (deltagare eller fritt namn). Bor under
    Uppgifter (områdesrubriken + Åtgärder → *Nytt område…*, `openArea`) och skapas bara där och av mallen. `SaveArea`
    vägrar `description`/`stations`/värd med ett meddelande. **B5:** områdets ansvarige (Deltar+, inloggad —
    `PlanningAccess.LeadsArea`) får ändra, ta och sätta läge på områdets uppgifter (`CanEditTask`/`CanReportOnTask` med
    `areaLeadPid`, controllerns `Lead(AreaLeads(id), task)`), men beslutar INTE Utförd; `Tasks` bär `myAreas`; vid
    NeedsHelp/ReadyForReview av någon annan mejlas den ansvarige (`PlanningNotifier.TaskStatusToAreaLeadAsync`,
    `task-needshelp`/`task-ready`).
  - **Delplanering skapas direkt:** Översikt → Åtgärder / kortet Delplaneringar → *Ny delplanering…* (`openNewSub` →
    `POST CreateSub`: namn, gren, sammankallande, sköts av, dagar, `areas`). `PlanningAccess.CanCreateSub` = sammankallande
    i en planering som inte själv är en delplanering. Valda områden flyttar i SAMMA transaktion (`_scopes.CreateScope()` +
    `outer.Complete()` — ett fel ångrar hela delplaneringen; sviten prövar det med ett okänt område).
  - **Flytta åt båda hållen (B6):** `MovePreview` (targetId 0 = förhandsvisning utan mål) och `MoveAreas`
    (`PlanningService.Family.cs`, `MoveAreasCore`). FK `(PlanningId, AreaId)` → flytten skapar en NY områdesrad i målet
    (eller slår ihop med samma namn) och mjukraderar den gamla. Deltagare via `FindTwin` (medlem, eller gästens e-post);
    "arbetare" (områdets ansvarige + öppna uppgifters ansvariga) blir aktiva med sin nivå (Owner → Editor), övriga Följer —
    utom den som läser genom huvudplaneringen (B3), som får en VILANDE rad. Kategorierna följer området; ett okategoriserat
    dokument följer när alla dess bifogningar flyttar; korsande bifogningar släpps. Systemrader `areas-moved-out`/`-in`,
    `planning-sub-made`/`-created`. Den som flyttar måste vara Owner i målet (`EnsureParentOwner` först).
  - **B4:** huvudplaneringens sammankallande är Owner i varje delplanering — `EnsureParentOwner` (lat, i `ResolveAsync`)
    och vid `CreateSub`. Delplaneringskortets sammankallande-lista utelämnar huvudplaneringens.
  - **B3:** `PlanningFamily.ReadsSubViaParentSql` = aktiv Owner/Editor i huvudplaneringen och INTE `ViaCompetition`
    (ersätter `IsParentConvener`). Delplaneringen läser huvudplaneringens fattade beslut (`planning.parentDecisions`,
    kortet `#plParentDecisions`).
  - **B11:** `Planning.HostClubId/HostRegionId/HostText` (sköts av) — `SetSubHost`, kortet `#stSubHost` i delplaneringens
    Inställningar; `HostOptions` (CanEditPlanning; också stationernas ansvariga).
  - **B7:** `CopyPlanning(includeSubs)` kopierar delplaneringarna med områden och uppgifter (`CopyOne` rekursivt).
  - **Familjen:** `PlanningFamily` (statisk) + `Planning.FamilyRootId AS COALESCE(ParentPlanningId, Id) PERSISTED`
    (`IX_Planning_Family`) — alla familjeuppslag (stationer, överskott, bemanning, Fältkonfig) går därigenom.
  - **`PlanningPart` är BORTA** (tabellen, `PlanningLink.PlanningPartId/AreaId`, `PlanningArea.Description/Host*/Stations`).
  - **B12, mallens ord:** *Ansökan & tillstånd* (`PrepTemplates.AdminArea`; juryn bara nationell/ldm/sm), *Inbjudan &
    anmälan*, *Sekretariat & resultat*, nya *Prisutdelning*, *Säkerhet & sjukvård*, *Funktionärer*, och för krets och
    större *Ekonomi* + *Parkering*; fältets *Banläggning* + *Bygga banan* (`PrepTemplates.BuildCourseArea`, där
    `SeedStationTasks` lägger "Bygg station N"). Test `PrepTemplatesTests.Mallens_ord_…`.
  - **Migreringar:** EN konsoliderad P1 (`create-planning-tables.sql`, slutformen; ersätter P2d/P3a/P3b/P3c/P3g/P3h/P3i/P3k
    — inget av det kördes någonsin i prod) + den bantade P3j. Dev konsoliderad med
    `consolidate-planning-final-form-dev-2026-10-25.sql`.
  - Svit `tavlingsplanering-dela-verify.mjs` omskriven (skapa, flytta åt båda hållen, B3/B4/B5 med A/B, ytan, telefon).
  - **Resten av expertgruppens förslag (2026-10-25 kväll):**
    - **N8, mallen och grenarna:** `PrepArea` bär `Discipline` + `BaseName` för grenens egna områden (`ForMany`).
      `PlanningService.PlanTemplate`/`SeedTemplate` (`TemplateSubs`) lägger dem i huvudplaneringens delplanering med den
      grenen — bara om det finns EXAKT EN sådan och den som fyller i är Owner/Editor där — med sitt vanliga namn, delens
      första dag som ankare och hens rad där som skapare; systemrad `template-from-parent`. Annars stannar de med
      "– Fält". Förhandsvisningen grupperar per (planering, område) och bär `subName`; svaret `inSubs`. `SeedTemplate`
      har ett eget scope utan autoComplete (skriver i flera planeringar).
    - **N5:** `TaskGone` i controllern (alla "Uppgiften finns inte") säger vart en flyttad uppgift tagit vägen
      (`PlanningService.TaskMovedTo`, `movedTo` i svaret). Det finns inga länkar till en enskild uppgift.
    - **N7:** kortet Delplaneringar står före Tävlingarna/veckoprogrammet på Översikt — ovanför vikningen på dator; på
      telefon direkt efter *Nästa steg* (det som väntar på en står först, medvetet), och rälsen fälls ihop (nedan).
    - **B3 veckoprogrammet** fanns redan: `FamilyJson` ger delplaneringen familjens `week` (testat i sviten).
    - **Veckans tävlingar** (`ShootIntentService.Targets`): en delplanering är en tävling bara med en gren eller en
      kopplad tävling — en del utan gren ("Servering") är arbete (Stefan 2026-10-25, efter ett tomt kort). Kortet
      `#plFamilyComps` visas bara med en tävling (rubrik Tävlingen/Tävlingarna) eller ett daterat veckoprogram
      (rubrik Veckoprogrammet) — aldrig tomt.
    - **Notiserna (Stefan: "de ska följa relevant inställning"):** `PlanningNotifyRules.GetsDirectedMail` (= samma
      regel som att nämnas) styr uppgiftsmejlen (ny uppgift, samlad kö, försenade, områdets ansvarige); möten har kvar
      `GetsMeetingMail`. `PlanningNotifyMode.Help` säger vad som mejlas direkt.
    - **Prodkollen** `Migrations/check-prod-planning-family-after-deploy-2026-10-25.sql` (P5 i körordningen).
    - Sviten fick avsnitt H: N1 (ingångarna räknas per vy), N3 (rättighetsmatrisen ur EN tabell, A/B med B som
      sammankallande → exakt en cell), N5, N7 (kortets överkant på dator och telefon), N8 (med A/B) och B3.
  - **Bemanningen över familjen, steg 1a (Expertgruppen 2026-10-25, `notes/tavlingsplanering-bemanning-familjen-plan-2026-10-25-utvardering.md`):**
    - **B7:** `StaffingPlanningLink.RecomputeFamily` låser familjen (rotens rad, UPDLOCK/HOLDLOCK) och NOLLAR `HasAdminAccess`
      på rader vars tävling byts — behörigheten följde förut tyst med vid Koppla loss / Bemannar för. Returnerar
      `AccessRevoked` per planering; `PlanningService.NoteRevokedAccess` skriver systemraden `staff-access-revoked` + loggar.
      Controllern speglar om ALLA familjens tävlingar efteråt (`SyncFamilyMirrors`).
    - `LinkToCompetition`, `Unlink`, `SetStaffsFor`, `Delete` (och `CreateForCompetition`) skriver i flera steg → scope
      UTAN autoComplete + `s.Complete()` sist (autoComplete committar även vid undantag).
    - `UX_PlanningLink_Competition` (en tävling per planering, filtrerat) i P1 (regenererad, bevisad på tom databas).
    - `ReconcileManagersIntoRoster` bara för planeringen med EGEN koppling till tävlingen (en del som ärver fick annars
      tävlingsledarna, den som laddades först).
    - K8: `GetCompetitionIdsForMember` frågan nr 2 filtrerar `CompetitionId IS NOT NULL` (ett planeringsuppdrag utan
      tävling fick `Fetch<int>` att kasta, sväljt → medlemmens fönster tomt).
    - **B14:** en delplanering flyttar ALDRIG till en annan familj — `ParentPlanningId` sätts bara i `CreateSub` och
      `CopyOne`. Bygg ingen väg som ändrar den.
  - **Paraplyet och Överskottet (Stefan 2026-10-25 kväll):** en huvudplanering med delplaneringar (`isUmbrella`:
    `planning.family` finns och ingen `parent`) har ingen *Tävlingen* i rälsen och ingen tävlingsrad i Nästa steg —
    tävlingarna ligger i delarna — utom när den själv har en tävling/ansökan (`hasOwnCompetition`). **Överskottet är
    BORTTAGET** ur planeringen (vyn, `PlanningController.Surplus`, `PlanningService.Surplus`, `PlanningSurplus`-modellen
    och -tabellen, delen `overskott`; P1 = 24 tabeller/25 FK, bevisad på en tom databas). Allt äldre nedan om
    Överskottet / `PlanningSurplusRules` / B10 är historik. Ekonomidelen kan ta det senare.
  - **Rälsen grupperad (Stefan 2026-10-25):** Översikt · *Samarbete* (Meddelanden, Möten, Förslag och beslut, Deltagare) ·
    *Arbetet* (Uppgifter, Dokument) · *Tävlingen* (Tävlingen, Stationerna, Bemanning, Dagsprogram) · Inställningar.
    `VIEWS[].group`; `renderRail` ritar `.pl-railgrp`/`.pl-railh`/`.pl-railitems` (telefon: en rad per grupp, rubriken i
    egen kolumn). **Mina notiser är borta som plats** — *Dina notiser* står överst i Inställningar (`notifyHtml`/
    `bindNotify`), som nu syns för alla som är med (`isParticipant || canEdit`); den som inte kan ändra ser bara
    notiserna. `#vy=notiser` (mejlens länk) är ett alias (`VIEW_ALIAS`).
    **Telefon (< 992 px, N7):** rälsen tog hela första skärmen (~550 px) — den fälls ihop bakom knappen
    `[data-rail-toggle]` "Meny · {platsen}" med summan av väntande siffror; utfälld (`.pl-rail.open`) en rad per grupp.
    `railHere`: ett platsbyte fäller ihop menyn, pollningens omritning (samma plats) gör det inte. ⚠️ Sviter i telefon-
    bredd väntar på rälsens länkar med `{ state: 'attached' }` — de är dolda tills menyn fälls ut.
- **P4 (2026-10-24), plan `notes/tavlingsplanering-p4-arbetsplan-2026-10-24.md`:**
  - **SM-annonsen** (fråga 17): `Models/Planning/NpIssues.cs` uppskattar Nationellt Pistolskyttes manusstopp (nr 1 fjärde
    måndagen i februari, nr 2 första i maj, nr 3 andra i augusti, nr 4 första i november; utgivning +42 dagar).
    `ForCompetition(datum)` = senaste nummer som kommer ut minst 14 dagar före tävlingen — SM-veckan blir nr 2, ett SM
    senare på året nr 3. Mallens milstolpe "SM-annonsen till Nationellt Pistolskytte nr X/ÅR" (`PrepItem.TitleFromAnchor`),
    ändras som vilken uppgift som helst. Test `NpIssuesTests`.
  - **Gruppens namn** (`Planning.GroupName`): "X beslutar", "beslutat av X" (lagras i `DecidedBoardName`). Controllern tar
    `groupNameSet="1"` — en tom sträng binds som null, och null = behåll.
  - **Stationerna** (fråga 13a): `PlanningArea.Stations` ("3–6, 9", `PlanningStations.Parse/Format`); en station kan bara
    höra till ETT område i hela familjen (huvud- + delplaneringar, `StationConflict` i `SaveArea`). Kortet Stationerna på
    Översikt (`PlanningController.Stations.cs` → `GET Stations`, läsaren får läsa) visar ansvarig förening, stationschef
    ur bemanningen och öppna uppgifter, och varnar för luckor. Delning tar med områdets stationer.
  - **Fältkonfigurationen** (fråga 13b): `POST GiveChiefsConfigAccess` gör stationscheferna till medarbetare i tävlingens
    `FaltskytteConfiguration` (`_attachedConfigId`) — kräver `CanEditPlanning` OCH `CanEditAsync` på konfigurationen.
  - **Delplaneringens bemanning** följer huvudplaneringens tävling tills den kopplas till en egen — se omgång 3
    (`PlanningFamilyRules.StaffingCompetition`, `StaffingPlanningLink.RecomputeFamily`).
  - **Överskottet** (fråga 15, "ingen regel, flexibelt"): `PlanningSurplusRules.Distribute` (rena funktioner, test
    `PlanningSurplusRulesTests`) — fasta poster först, resten efter personer/uppdrag/dagar/lika/egna andelar, största resten
    i hela kronor. Föreningarna och talen ur bemanningen (*Ställer upp för*) i hela familjen; föreningar kan läggas till för
    hand (`text:`-nyckel). `GET Surplus`, `POST CalculateSurplus` (räknar, sparar inget), `POST SaveSurplus` — alla
    `CanEditPlanning`. Sparas i `PlanningSurplus` (en rad per planering). Kortet under Tävlingen, med utskrift. Inget bokförs.
  - **Migrering `add-planning-p4.sql` (körordning P3i) FÖRE deployen** — NPoco skriver `GroupName`/`Stations` vid varje
    sparning. Körd i dev, EJ i prod.
  - Svit `tavlingsplanering-p4-verify.mjs` 59/59 (A/B: utan stationskonflikten + utan arvet vid koppling → 4 röda).
- **Omgång 3 (2026-10-24), efter persona-testet och Expertgruppen** — plan
  `notes/tavlingsplanering-omgang3-arbetsplan-2026-10-24.md`, fynden `notes/planeringstest/expertgrupp-planeringstest-2026-10-24.md`.
  - **Säkerheten (K1, K2):** en tillagd medlem räknas som INBJUDEN tills hen varit inne (`PlanningContactRules.IsPendingInvite`:
    `LastSeenAt` null, inte via tävlingen, inte tillagd av sig själv) — `ContactInfo` svarar `pending` utan e-post och telefon;
    tillagd igen av någon annan = inbjuden igen. Tak `MaxNewPlanningsPerDay` = 10 per medlem och dygn (sajtadmin undantagen).
    **Mejllänken bär aldrig Kan ändra** (`PlanningAccess.EditorSignedIn`): via länken ändras/tas bort bara egna uppgifter,
    möten ställs inte in, och på ett möte beslutas bara den egna uppgiften (`DecidesAtMeeting`).
  - **⚠️ En uppgift går bara till någon som är med** (Stefan): `TaskInputAsync` vägrar `assigneeMemberId`, en inaktiv deltagare
    och ett nytt fritt namn (ett gammalt fritt namn på en befintlig uppgift får stå kvar). Inklistring: ett okänt namn blir
    ingen ansvarig. Formuläret erbjuder bara deltagarna ("bjud in hen först").
  - **Överlämningen (K3):** `ApplyPersonIdentity` med `IsReplacement` till ett fritt namn nollar MemberId, e-post, telefon,
    Företräder, incheckning och appåtkomst och sätter en ny **`StaffAssignment.InviteNonce`** — `StaffingInviteToken` v2
    (`"id|nonce"`) bär nonce:n och `StillValid` vägrar en länk som gick till den förra personen. **Byt ut** i planeringen
    (`ReplaceParticipant`) flyttar öppna uppgifter och områdesansvar och kopierar aktiva roller (`MoveWork`); `alsoIn` byter
    också i familjens planeringar där den som frågar är sammankallande (`ReplaceFamily` visar var).
  - **⚠️⚠️ Familjen är veckan** (Stefan: en tävling per delplanering): `PlanningFamilyRules.StaffingCompetition` — egen
    koppling → **Bemannar för** (`Planning.StaffsForCompetitionId`, om tävlingen är familjens) → huvudplaneringens → ingen.
    `StaffingPlanningLink.RecomputeFamily` är ENDA vägen som sätter `CompetitionId` på familjens bemanningsrader (koppling,
    avkoppling, Bemannar för, borttagning). Tävlingens läsningar ser familjens rader (`StaffOwner.IncludeFamily` + `Filter`);
    skrivningar använder fortfarande `Where`. **De fyra `*_Comp`-indexen gäller bara tävlingens egna rader**
    (`WHERE PlanningId IS NULL AND CompetitionId IS NOT NULL`) — förut föll kopplingen när en delplanering hade samma dag
    (K5). `Get` bär `planning.family` (kort *Tävlingarna* på Översikt, väljaren Bemannar för under Tävlingen).
  - **Kan ändra** i huvudplaneringen läser delplaneringarna (nu `PlanningFamily.ReadsSubViaParentSql`, se Expertgruppens
    modell ovan; vid flytt gäller arbetar-regeln där). **En planering med delplaneringar går inte att ta bort**;
    borttagningen tar bort `PlanningLink`, släpper bemanningens `CompetitionId` och speglar om tävlingarna.
  - **Kopplingen (K6):** `LinkableCompetitions` väljer kommande tävlingar vars VÄRD är en klubb eller **krets** som någon i
    planeringen företräder (kretsens nod-id → `regionCode`; en kretsvärdad tävling har `ClubId` 0 — SM!), med seriens värd
    för en omgång utan egen, eller där den som frågar är tävlingsansvarig. Redan kopplade i EN fråga
    (`PlanningService.LinkedCompetitionIds`), behörigheten (`CanEditCompetitionAsync`) bara för de 60 närmaste.
  - **UX (K8):** `wwwroot/js/planering-dates.js` (`hpskPlDatePicker`, `hpskPlParseDate`) läser "16 maj 2027", "16/5",
    "16/5 2027", ISO — annars blir fältet RÖTT och texten står kvar (flatpickr tömde den; `isTrusted` skiljer användarens
    inmatning från flatpickrs egna händelser). Flatpickr låst till 4.6.13. Servern vägrar ett ifyllt datum den inte kan
    läsa (`UnreadableDate`) — förut blev det tyst inget datum. Mötesdialogen: *Fråga vilka tider som passar* / *Tiden är
    bestämd* (en rad, ingen svarsfrist); rader utan dag hoppas över. Nästa steg: väntande godkännanden först, *Dina
    uppgifter* = filtret Mina, tävlingssteget bara för sammankallande. **Bemanning syns för den som deltar** (vem som sköter
    den och mina uppdrag, `viewStaffingInfo`). Nivåtexterna säger vad Kan ändra/Deltar gör. Mallens gren förvald efter
    planeringens. **Inklistrade uppgifter mejlas** — ETT mejl per ansvarig (`PlanningNotifier.TasksAssignedAsync`).
    **Att nämna någon mejlas direkt även med förvalet Ett mejl om dagen** (`PlanningNotifyRules.MailOnMention`).
    Relämejlet (Hitta funktionärer → klubbarnas ansvariga) bär den som frågar (avsändarnamn, Reply-To), vad som fattas
    per dag och `PublicBase` (`StaffRequestService.RequestContext`). Bemanningen på telefon: en dag i taget (dagväljare,
    tävlingsdagen först), namnet på egen rad, "saknar konto och e-post" som en gul rad.
  - **Mallarna (K10):** storlekar klubb · krets · nationell · ldm · sm (`PrepTemplates.Sizes`, `ApplyLevel` efter SHB
    C.3.5.2–C.4.2.1: kretstävling söks hos kretsstyrelsen, nationell/LDM via kretsen före 1 oktober året före, inbjudan
    för kännedom resp. godkännande, fältets underlag till kretsstyrelsen resp. förbundet, jury och vapenkontroll),
    Milsnabb, och flera grenar på en gång (`ForMany`: gemensamma områden en gång, grenens med grennamn). Ett befintligt
    område får de uppgifter som saknas (titel, även borttagna räknas) i stället för att hoppas över. Test `PrepTemplatesTests`.
  - **Fältkonfiguratorn (K9, 3.3):**
    - **Versionen** = `ModifiedDate.Ticks` (`FaltskytteConfigurationView.Version`). `UpdateWithVersionAsync` vägrar en ÄNDRAD
      stationsuppsättning när `ExpectedVersion` skiljer ("ändrats av någon annan … ladda om"); svaret bär den nya versionen,
      som editorn sparar (`window._faltCfgVersion`). En oförändrad sparning prövas inte.
    - **Avståndstaket på servern** (`CourseReviewChecklist` → `TooFar`): en målgrupp över SHB:s maxavstånd vägras och namnges.
      Klienten klämmer reglaget till taket.
    - **Skicka tillbaka med kommentar** (`ReturnForChanges`, `ReturnAsync`): den Banläggare som ombetts (eller sajtadmin), bara
      från Väntar. Utkast igen; `ReturnComment/ReturnedByMemberId/ReturnedDate` visas i utkastbannern tills någon begär
      godkännande igen. Ägaren mejlas (`SendFaltkonfigDecisionAsync`, även vid godkännande). Avbryt begäran har egen text.
    - **⚠️ En delkonfiguration per OMRÅDE** (`CreateAreaConfig`): en egen `FaltskytteConfiguration` för områdets stationer,
      kopierad ur tävlingens anslutna konfiguration (annars tävlingens inline-konfiguration, annars tom). **Konfiguratorns
      stationer numreras 1..N och numret ändras aldrig** (det är stationens identitet); banans nummer står i meta-nyckeln
      **`_courseStations`** (`AreaConfigBlob.Build`, test `AreaConfigBlobTests`) och visas som "banans station X" i rubriker
      och utskrift. `FirstStationNumber` = områdets första station. Kopplingen är `PlanningLink` med `TargetKind = faltconfig`
      och `AreaId` (en per område, `LinkAreaConfig`); ⚠️ varje fråga på tävlingskopplingen filtrerar `TargetKind = competition`.
      En borttagen konfiguration tar bort sin länk. Områdets stationschefer blir medarbetare; `GiveChiefsConfigAccess` tar
      `areaId`. Stationskortet på Översikt listar områdena.
  - **⚠️⚠️ Banan ägs av planeringen (Stefan 2026-10-24: "planering ska kunna äga banans konfiguration innan tävlingen
    finns … alla konfigurationer läggs sedan ihop till tävlingen")** — plan `notes/tavlingsplanering-banan-2026-10-24.md`.
    - **Banan** = `PlanningLink` `TargetKind = faltconfig`, `AreaId NULL`, på FAMILJENS ROT (`PlanningService.CourseConfigId`,
      `LinkCourseConfig`). Ingen migrering. `PlanningCourseService.CreateCourseAsync` är idempotent (finns banan → samma id),
      kopierar tävlingens konfiguration om en tävling är kopplad, namn "{rot} – banan". **Delarna** = områdenas
      delkonfigurationer (`_courseStations`); `CreateAreaConfig` kopierar nu ur BANAN först.
    - **`CourseAssembly`** (rena funktioner, test `CourseAssemblyTests`): `Assemble` lägger delarna i banan per vapenklass
      och numrerar om; en station som ingen del har behålls ur banan. Meta: `_stationCount`, `_assembledFrom`
      [{configId, name, checksum, stations}], `_assembledAt`. **`Checksum`** (innehållet utan `_attachedConfigId`/
      `_assembledAt`) avgör ny/ändrad/aktuell och att en oförändrad ihopsättning inte sparas. Ihopsättning bara med
      knappen (`POST AssembleCourse`, Stefans val) — konfiguratorn visar "ändras här skrivs över".
    - **Godkännande: varje del, sedan helheten** (Stefans val): `RequestApprovalAsync` vägras via
      `CourseAssembly.ApprovalBlocker` tills varje del är Godkänd och oförändrad sedan ihopsättningen.
    - **Behörighet:** `FaltskytteConfigurationService.PlanningAccessAsync` — deltagare i familjen läser banan och delarna,
      Kan ändra i länkens planering eller roten ändrar. ⚠️ SQL:en BÖRJAR med SELECT (härledd tabell) — en CTE blev
      `SELECT * FROM PlanningAccessRow` hos NPoco och tystades i catch.
    - **Till tävlingen:** `AttachToCompetitionAsync` kopierar banan (inte en länk) till tävlingens `_attachedConfigId` +
      `NumberOfSeriesOrStations`/`ScoringMode` — vid `LinkCompetition` och ansökningslänken bara om tävlingen saknar
      konfiguration (`onlyIfEmpty`), annars med knappen (kortet visar `stale` när banan ändrats efter).
    - **Uppgifterna:** `SeedStationTasks` kräver ingen tävling, lägger varje stationsuppgift i området som äger stationen
      och milstolpen "Banläggaren godkänner stationerna"; uppgiften länkar till stationen i delen (`#station=N`,
      konfiguratorn fokuserar den) annars i helheten.
    - **⚠️ ORDET (Stefan): användaren ser "Stationerna", aldrig "banan"** — koden heter fortfarande Course*/course
      (internt), men varje text i planeringen, konfiguratorns planeringsrutor och serverns meddelanden säger
      stationerna / helheten / tävlingens station N. "Banläggare" (certifikatet) står kvar.
  - **⚠️⚠️ Rälsens plats Stationerna (`#vy=stationer`, Stefan 2026-10-24: "rörigt och en kvarleva … gör om")** — skissen
    https://claude.ai/artifact/SYoxkZnwWrSTkQkVUTqfoU. ERSÄTTER Stationerna-kortet på Översikt (nu en rad,
    `#plStationsSummary`) och fältet Stationer i områdesdialogen (bara en rad "väljs under Stationerna"; `SaveArea` med
    `stations` finns kvar i API:t, null = rör inte). Syns för fältplaneringar och när ett område har stationer
    (`stationsWanted`), inte via mejllänken.
    - **Sidan:** Läget i fyra steg (Fördela · Bygg · Sätt ihop och godkänn · Tävlingen, `.pl-steps`), **Föreningarna**
      (= familjens områden med stationer, `PlanningService.StationAreas`; radmeny Välj stationer / Skapa-Öppna delen /
      Ge stationscheferna tillgång / Ändra / Ta bort; delplaneringens rader ändras där) och **Stationerna** (en rad per
      station: förening, byggs i, stationschef, uppgifter; radmeny Öppna i konfiguratorn / Ge till en förening /
      Lägg till en uppgift — `openTaskEdit(null, area, { scopeType, scopeKey })`). Utan stationer i planeringen: kortet
      Skapa stationerna i stället för Läget, tabellerna syns ändå (en kopplad tävlings stationer).
    - **Rutnätet** (`stationGridHtml`): en ruta per station; en station en annan förening i SAMMA planering har "flyttas
      hit"; en som hör till en annan planering i familjen är låst.
    - **`POST AssignStations(areaId, stations)`** → `PlanningService.AssignStations` (UPDLOCK på familjens områden):
      området får EXAKT listan, andra områden i planeringen släpper dem, en station i en annan familjeplanering vägras
      ("Ändra den där"), över tävlingens antal vägras. Ändrade områdens delar följer med:
      `PlanningCourseService.SyncPartsAsync` → **`AreaConfigBlob.Resync`** (behåll delens arbete för kvarvarande
      stationer, ny station ur helheten, numrera om 1..N; tester i `CourseAssemblyTests`) +
      `FaltskytteConfigurationService.ReplaceForPlanningAsync` (ingen behörighetsprövning — planeringen har prövat;
      en godkänd/väntande del blir UTKAST igen och svaret `reopened` säger vilka). Delens namn följer stationerna.
    - **`POST SetStationCount`** → `PlanningCourseService.SetStationCountAsync`: `_stationCount` i helheten; färre vägras
      om en förening har en station över, en sådan station är byggd, eller tävlingen har fler.
    - `GET Stations` bär `areas` (med `part` inkl. `state`), `stationCount` och per station `built` (i helheten ELLER i
      en del, `CourseAssembly.BuiltStations`) och `url` (`StationUrlsAsync`).
    - Svit `tavlingsplanering-stationer-verify.mjs` (ersätter banan-sviten); p4 och o3 läser vyn.
  - **⚠️⚠️ Stationerna omgjorda (Stefan 2026-10-25: "vilken gäller?" och "det är absolut inte så alltid [förening]")**:
    - **EN plats:** `PlanningService.StationOwnerId` — huvudplaneringen om den är fält; annars planeringen själv om den
      är fält; annars familjens enda fältplanering (SM-veckans Fält-del); annars huvudplaneringen. `CourseConfigId`
      läser länken där, med fallback på en länk någon annanstans i familjen (betan). `Get` bär `planning.stationsOwner`
      {here, id, name, isSub, url}; rälsen visar Stationerna bara där (`stationsWanted`), annars Översikt-raden
      "sköts i delplaneringen X". `WholeStationsElsewhere` = ägaren är en annan.
    - **Odelat är utgångsläget:** Läget tre steg Utforma · Godkänn · Tävlingen; inga föreningar, inga gula varningar
      (stationschef saknas = "—"). **Uppdelning** = Åtgärder → *Dela upp stationerna på flera…* (`openStationArea`):
      vem = någon i planeringen (`responsibleId`, områdets namn = personens), en klubb/krets (värd) eller ett namn.
      Först när ett område har stationer syns *Vem gör vilka stationer* (`#plStAreas`), steget Fördela och Sätt ihop.
      `data-st-step` = stegets namn (Fördela/Utforma/Godkänn/Sätt ihop och godkänn/Tävlingen).
  - **⚠️⚠️ EN KONFIGURATION, EN ANSVARIG PER STATION (Stefan 2026-10-25 — ERSÄTTER delarna, sammansättningen och
    "Vem gör vilka stationer" ovan):** "Stationschef" är den som leder stationen på tävlingsdagen; i planeringen heter
    det **Ansvarig** (person, klubb eller krets). Stationerna fanns i två konfigurationer (delen och helheten) — nu
    finns bara planeringens EN konfiguration. Inga delar skapas längre, och ytan har ingen Sätt ihop.
    - **⚠️⚠️ Ansvaret har en EGEN TABELL, `PlanningStationResponsible`** (Stefan 2026-10-25: "varför skapas ett område?")
      — EN rad per station (unikt planering + station) på stationernas planering (`StationOwnerId`): `Kind`
      Person/Club/Region/Name, `ParticipantId` (personen, eller KONTAKTEN för klubb/krets/namn — den som får ändra i
      konfiguratorn), `HostId`, `Name` (visningsnamnet när ansvaret sattes), `Key` (grupperar en ansvarigs stationer).
      **Ett område bär inga stationer:** `PlanningArea.Stations` läses och skrivs inte längre; `SaveArea` med stationer
      vägras. `POST SetStationResponsible(stations, who = p:{pid}|h:{0|1}:{id}|text, name, contactId, release)` (release =
      stationer samma ansvarige släpper) och `POST ClearStationResponsible(stations)` — bara på stationernas planering.
      ⚠️ Taket på stationsnumret gäller bara när antalet är KÄNT (konfigurationen eller en kopplad fälttävling) — annars
      räknade `StationCount` ur de redan tilldelade och vägrade nästa station. `MoveWork` (byt ut en deltagare) flyttar
      raderna; `PlanningPurgeService` tar dem. Borttaget: `AssignStations`, `StationAreas`, `StationConflict`,
      `CreateAreaConfig`, `AssembleCourse`, `SyncPartsAsync` och utdelningens kopia av ett områdes stationer.
      *En uppgift per station* lägger uppgifterna i *Bygga banan* (`PrepTemplates.BuildCourseArea`) med den ansvarige som ansvarig för uppgiften.
      Bemanningens stationsrader (`FamilyAreaStations`) = 1..konfigurationens antal ∪ stationer med en ansvarig.
      Migrering `create-planning-station-responsible.sql` (P3k, efter P3j). Kortet **Ansvariga** (`#plStAreas`,
      rader `[data-st-resp=nyckel]`: Ansvarig · Stationer · Kontakt; Välj stationer… / Ta bort som ansvarig) och
      dialogen *Ge stationer en ansvarig* (`openResponsible`, `#saWho`, `#saResp`, `#stGrid`). Stationsraden: Station ·
      Ansvarig · Utformad · Godkänd · Uppgifter (`responsible`, `responsibleKey`). Läget: Utforma · Godkänn · Tävlingen.
    - **Rätten per station:** `FaltskytteConfigurationService.StationRightsAsync` (partial `.Stations.cs`) — `All` =
      får ändra konfigurationen; annars stationerna där medlemmen är ansvarig eller kontakt
      (`PlanningStationResponsible.ParticipantId` → `PlanningMember.MemberId`). `Get` bär `planningStations` {all, mine, responsible, checksums,
      approvals, stationCount, canJudge}; konfiguratorn (`faltCfgPlanningDecorate`) visar Ansvarig, godkännandet och
      "Bara läsning", och stänger av fälten på andras stationer.
    - **⚠️⚠️ Sparas STATION FÖR STATION:** `POST UpdateStations {Id, Stations, JsonBlob, Expected}` slår ihop bara de
      stationerna (`StationBlob.Merge`), vägrar en station man inte är ansvarig för ("Du är inte ansvarig för station
      N — X är det.") och en station som ändrats sedan fliken läste den (kontrollsumman per station). Skrivningen är
      **compare-and-swap** (`SwapBlobAsync`: `UPDATE … WHERE CAST(JsonBlob AS varbinary(max)) = gamla`, varbinary
      eftersom sorteringen inte skiljer på stora och små bokstäver), högst fem försök. Utan den försvann en stations
      ändring när någon annan sparade en annan station i samma ögonblick (sviten hittade det). Konfiguratorns
      `faltCfgSaveToServer(json, stations)` går dit när `window._faltCfgPlanning` finns.
    - **Godkännandet station för station, sedan helheten:** blobbens `_stationApproval` {n: {s,by,at,c,sum}}
      (`StationBlob`; Pending/Approved/Returned). `POST StationApproval {ConfigId, Station, Action, Comment}`:
      request = den ansvarige; approve/return = banläggare som kan ändra konfigurationen (eller sajtadmin); return
      kräver en kommentar. En station vars innehåll ändrats är ett utkast igen (`StationBlob.Current` jämför `sum`).
      Helheten kan begäras/godkännas först när alla är godkända (`StationBlob.WholeBlocker` i `PartsBlockerAsync`).
      En hel sparning (den gamla vägen, för den som kan ändra allt) behåller SERVERNS godkännanden och öppnar bara
      ändrade stationer (`KeepServerApprovals`). Samma CAS-skrivning.
    - **Projektet:** stationernas konfiguration ligger i ett **FaltskytteProject med planeringens namn** (Stefan),
      kopplat via `PlanningLink` TargetKind **`faltproject`** (`PlanningCourseService.EnsureProjectAsync`, anropas vid
      skapandet och av `GET Stations` för en äldre konfiguration). Projektet syns för planeringens deltagare
      (`FaltskytteProjectService.ListAccessibleAsync`). Unikt index `UX_PlanningLink_Project` (ett projekt per
      planering); går kopplingen inte att skriva tas projektet bort igen. ⚠️ CHECK-villkoret `CK_PlanningLink_Kind`
      måste tillåta `faltproject` (P3j) — annars skapades ett tomt projekt vid VARJE sidladdning.
    - Svit `tavlingsplanering-stationer-verify.mjs` 50/50 (A/B utan ansvarsregeln, serverns godkännanden och
      helhetsspärren → 13 röda; rätten utan den ansvarige → sviten faller). Test `StationBlobTests`. Felen i
      konfiguratorn visas i en rad under planeringsrutan (`faltCfgPlanningError`), aldrig `alert()`.
  - **Fyll i från en mall i två steg (Stefan 2026-10-25):** steg 1 = gren + sorts tävling (`Visa uppgifterna`), steg 2
    = mallens uppgifter med en ruta var (`GET PreviewTemplate` → `PlanningService.PlanTemplate`, nycklar
    "områdesindex:uppgiftsindex"); `SeedTemplate(items)` lägger bara in de valda (utelämnat = alla). EN plan för båda, så
    listan och det som läggs in kan inte skilja sig. Ett område skapas bara när minst en av dess uppgifter väljs;
    befintliga uppgifter står gråa. Tillbaka behåller valen. Provas i o3 (avsnitt 5).
  - **Listsidan: delplaneringarna under sin huvudplanering (Stefan 2026-10-25):** `MyPlannings` bär `parentId` och
    `readOnly` per planering, plus `parents` (huvudplaneringar man bara är med i via en delplanering — en grå RUBRIK,
    ingen länk, eftersom man inte får läsa den) och läsbara delplaneringar för den som är sammankallande eller Kan ändra i
    huvudplaneringen (`PlanningService.HubFamilies`, samma regel som `IsParentConvener`; nivån "Läser …", bara Öppna).
    Utfällt alltid; fler än fem delplaneringar får en Dölj-knapp. En sökträff i en delplanering tar med huvudplaneringen
    som sammanhang (räknas inte i "Visar x av y"). Svit `tavlingsplanering-lista-verify.mjs` 15/15 (A/B gamla vyn → röd).
  - **Planeringstestet körning 2 (2026-10-25, `notes/planeringstest/`, granskning `expertgrupp-resultat-k2-2026-10-25.md`):**
    - ⚠️ `SaveArea` med `details=1` utan `stations` = RÖR INTE (förut `?? ""` som tömde föreningens stationer).
    - ⚠️ `UpdateWithVersionAsync` ger ingen ny version till en INAKTUELL flik (`ExpectedVersion` ≠ nuvarande) även när
      stationerna inte ändras — annars tog fliken versionen och skrev över en flyttad station. Editorns uppgiftssparning
      skickar `expectedVersion`.
    - `ApproveAsync` prövar `PartsBlockerAsync` (helheten kunde godkännas förbi delarna).
    - **Grenen:** frågan står direkt efter namnet i *Ny planering*; `stationsWanted` = områden med stationer, fältgren,
      eller INGEN gren och Kan ändra; `SeedTemplate` sätter planeringens gren när den saknas och mallen har EN gren.
    - **Läget:** "Utforma … utformade" — `CourseAssembly.BuiltStations` kräver innehåll (namn, målgrupp med bild/beskrivning,
      figur med mål/bild/tavla — samma som konfiguratorns `faltCfgStationHasContent`).
    - **Kom igång** står kvar för den sammankallande tills inbjudan, mötet och första meddelandet finns (bockas av);
      Nästa steg tom = "Inget att svara på just nu"; Översikt ritas om när `#plFormModal` stängs.
    - **Startsidans kort** (`HomeCards`): också en egen öppen uppgift (`MyPlanning.OpenTasks`), länken till Översikt;
      `Unread` räknar bara `Kind='Message'`.
    - **Bemanningen:** `StaffHelpService.NeedDays` summerar behov per station (`FamilyAreaStations`); avböjda räknas inte i
      rutnätets `Filled`/`FilledByDay`/`PassCover` men står kvar i rutan.
    - `Decisions.canPropose` är falskt via länken; `LinkableCompetitions` tar med kretsar den som frågar administrerar
      (inte sajtadmin); gästlänken vid godkännande bär `PublicBase`.
    - Svit `tavlingsplanering-k2-verify.mjs` (grenen, Kom igång, startsidans kort). Kvar och väntar på Stefan: avisering
      av kommentar/Klar att pröva/förslag, den drabbade vid flytt, delens mottagare, skift, rollkatalogen enligt SHB.
  - **⚠️⚠️ Bemanning per station och pass — TIDEN ÄR SANNINGEN (3.2):**
    - **`StaffTimeCoverage.Peak`** (rena funktioner) är ENDA räkningen av "hur många är på plats": flest SAMTIDIGT, inte
      antalet uppdrag (tio markörer i två skift om fem = fem). Utan tid = hela dagen; ett uppdrag i ett pass har passets
      tider; kant i kant är en överlämning. Rutnätet (`GridRow.FilledByDay`, `PassCover`) och täckningen
      (`StaffPassService.BuildCoverage` — ett uppdrag utan pass täcker passet när tiderna överlappar) läser den.
      `ParseRange` går genom `ClockTime` ("8" = klockan 8, inte 8 dygn).
    - **Stationsroller** (`DefaultScopeType = Station`) får en rad per station, även tom: en kopplad fälttävlings
      stationer, annars områdenas stationer i hela familjen (`StaffPassService.FamilyAreaStations`). Rutans "lägg till" ger
      uppdraget stationen. Rader sorteras numeriskt (2 före 10).
    - **`StaffCrewNeed.ScopeKey` / `PassId`** (null = varje station / hela dagen; en passrad bär passets dag).
      `CrewNeedRules.For` väljer det mest bestämda: station+pass → station → pass → dagen. ⚠️ `ForDay`/`EffectiveFor`
      läser bara raderna utan station och pass. En stationsrad läser "per station" när något sådant gäller stationen,
      annars rollens antal (som förut). `SetRoleNeeds` tar `scopeKind`, `scopeKey` och `passes` (bara de som avviker
      från sin dag lagras, `PlanPasses`); passen prövas mot ägarens egna. De unika indexen och `StaffingPlanningLink.Unique`
      tar med båda kolumnerna; planeringskopian tar med stationens behov men inte passens.
    - Rutan: "FM 2/3 · EM 3/3" när dagen har flera pass med tider, annars `fyllda/behov`, och "N uppdrag, högst M
      samtidigt" när de skiljer. Behovsdialogen: "Antalet gäller: Varje station / Bara station N" och ett antal per pass.
  - **Krockar och tävlingsavsikter (steg 9, Stefan: "de som anmäler sig som funktionärer meddelar vilka tävlingar de
    tänker anmäla sig till … långt innan anmälningstiden öppnar"):**
    - **`PlanningShootIntent`** (RootPlanningId, TargetPlanningId, MemberId ELLER DisplayName, Classes, Source Self/Organiser).
      ⚠️ Tävlingen är familjens PLANERING (en delplanering = en tävling), så avsikten står kvar när planeringen kopplas.
      **`ShootIntentService.Targets`** = veckans tävlingar: varje kopplad planering, varje delplanering och en ensam
      planering — en okopplad huvudplanering med delplaneringar är veckan själv, ingen tävling. `Save` skriver om en
      persons avsikter (personnyckeln `m:{id}`/`n:{namn}` som bemanningen) och vägrar en tävling utanför veckan.
      I GDPR-rensningens karta (`MemberDataPurgeService`).
    - **Ytor:** `/bemanna` — kortet "Tänker du själv tävla?" (`for-competition` bär `shootTargets`/`myShootIntents`,
      `help-signup` tar `shootIntents`; null = rör inte). Personens panel i Bemanning — "Tävlar själv" med *Ändra…*
      (`Planning/ShootIntents` + `SaveShootIntents`; den som bemannar för vem som helst, en deltagare bara för sig).
      Den som inte bemannar ser bara antalen. Kortet **Tävlingarna** på Översikt: antalet som tänker tävla per
      tävling och **Veckoprogrammet** dag för dag (`family.week`).
    - **`FamilyClashRules`** (rena funktioner) + `ShootIntentService.Clashes`: över hela familjen — **uppdrag** mot uppdrag i
      en ANNAN planering (inom en planering räknar rutnätet själv), **start** (uppdrag under egen start ur
      `MyScheduleService`, bara medlemmar med uppdrag under tävlingens dagar; utan sluttid antas 2 h) och **avsikt**
      (uppdrag en dag personen tänker tävla — en varning som tystnar när personen har en start i tävlingen).
      Rutnätet visar dem som "Krockar i veckan" (`GridResponse.FamilyClashes`, satt i `StaffingController.GetGrid`;
      ett fel fäller inte rutnätet).
    - Index `IX_StaffAssignment_MemberId` (steg 10) ligger i samma migrering.
  - **Gäster, volym och rensning (steg 10):**
    - **Uppgiftsmejlen samlas** (`PlanningNotifier.TaskAssignedOutcomeAsync`): dagens FÖRSTA uppgift till en person mejlas
      direkt; fler samma dag köas som `PlanningNotifyLog` Kind `task-queued` (Succeeded 0) och svepet (`TaskBatchesAsync`)
      skickar ETT mejl när kön stått still i `TaskBatchQuietMinutes` (10) minuter — spärren är en `task-batch`-rad nycklad
      på köns sista rad, och köraderna märks förbrukade. Uppgifter som flyttats till någon annan tas inte med. Svaret
      bär `mailed`/`mailQueued`; inklistringens `TasksAssignedAsync` går direkt som förut.
    - **Gästens dokument** (`PlanningAccess.DocumentsViaLinkOk`): via mejllänken arbetar bara en GÄST (utan konto) med
      dokument — lägger till, och tar bort/ändrar/kopplar loss sina EGNA. En medlem med konto loggar in. Dokumentendpointsen
      tar `t`.
    - **Ny länk** (`/tavlingsplanering/ny-lank`, formuläret på sidan för en länk som inte fungerar): mejlar en ny länk till
      gästrader med just den adressen (`RenewGuestLinksAsync`, loggas som Kind `relink`). Svaret är ALLTID detsamma (sidan
      avslöjar aldrig om en adress finns) och samma adress får fråga en gång i kvarten (`RateCounterService`).
    - **Progressivt avslöjande:** `Planning.Parts` (`PlanningParts`: `beslut`, `overskott`) och `Planning.RolesChosen`.
      *Förslag och beslut* (rälsen) och *Överskottet* (Tävlingen) syns när de har innehåll eller slagits på under Översiktens
      Åtgärder (`SetPart`; `planning.parts` i `Get`). **Välj roller:** en planering med `RolesChosen = 0` visar i rutnätet
      bara roller med folk, behov eller egna (`StaffingController.ApplyRoleChoice`) och frågar vilka som behövs;
      `ChooseRoles` tar fram de valda inbyggda och döljer resten med samma IsActive=0-markör som *Ta bort roll* — en roll
      med folk står kvar och namnges. Åtgärder → *Välj roller…* finns alltid. Migreringen satte båda på befintliga
      planeringar (ingen ändring för dem); en kopia får förlagans.
    - **Rensningen** (`PlanningPurgeService`, dygnssvep `PlanningPurgeHostedService`, sajtadmin `Planning/RunPurgeNow`): en
      borttagen planering tas bort för gott efter 90 dagar — tabellerna i `PlanningIdTables` (testet
      `PlanningStep10Tests.Purge_lists_every_table_that_carries_a_PlanningId` faller för en ny tabell med `PlanningId` som
      inte står där), möten, roller, avsikter; tävlingsansökan kopplas loss, inte raderas. Filer tas bort bara när ingen
      annan rad pekar på dem. En borttagen huvudplanering med en kvarvarande delplanering rensas inte.
    - `MemberPropertyReader` i stället för `GetById` per person (rutnätets klubbar, personlistan;
      `MemberPropertyRow.AllClubIds`). Gallringen nollar `StaffAssignment.Note`.
  - **Migrering `add-planning-omgang3.sql` (körordning P3j) FÖRE deployen.** Körd i dev, EJ i prod.
  - Svit `tavlingsplanering-o3-verify.mjs` 154/154 (A/B steg 9: avsiktsregeln bort + planeringsvillkoret vänt → 2 röda);
    steg 7: utan omnumreringen och utan länkstädningen → 2 röda; steg 8: antal uppdrag i stället för samtidigt + utan
    stationsrader → 5 röda. Test `PlanningFamilyRulesTests`, `StaffingInviteTokenTests`, `PlanningTaskRulesTests`,
    `AreaConfigBlobTests`, `StaffTimeCoverageTests`, `FamilyClashRulesTests`.
- **Migrering `Migrations/create-planning-tables.sql` FÖRE deployen** (21 tabeller med dokumentarkivet och
  `Planning.DisciplineKey`; dev: även `alter-planning-p2-tasks.sql`), **och `alter-staffing-planning-p2b.sql`**
  (bemanningens `PlanningId`) (17 tabeller inkl. `PlanningRequest`,
  `Planning.ListedForMembers` och de fyra ovan; för en DB som redan har de 13: `alter-planning-tasks-topics-roles.sql`
  — NPoco skriver alla kolumner, så de MÅSTE finnas; filtrerade index ⇒
  `QUOTED_IDENTIFIER ON`, sista raden `SELECT @@TRANCOUNT;`). Körd i dev 2026-10-21/23, **EJ i prod**.
  Startkontroll: `PlanningSchemaGuardHostedService`. `PlanningLink` skapas tom (P2).
- **Ej byggt i P1:** dokument (fråga 7), delar och milstolpar (P3), förberedelsernas och bemanningens
  flytt (P2/P2b), kopiera planeringen (P3), SignalR. Dev kan inte skicka mejl — loggraderna står på 0.
- Sviter (`HPSK_BASE=https://localhost:44390`): `hpsk-verify/tavlingsplanering-p1-verify.mjs` 142/142 (A/B:
  röstspärren, olistad förfrågan, inbjudningstaket och godkännarvakten fäller var sin) och
  `tavlingsplanering-uppgifter-verify.mjs` 66/66 (A/B: beslut utan pågående möte + Utförd via rapport fäller 5).
  Fildeploy av KB `tavlingsplanering-fristaende.md`.

## Migrations (DISABLED)
The `/Migrations` folder contains disabled database schemas for direct competition result storage. The system now uses Umbraco Document Types and Content Service instead. Migrations can be safely ignored unless reverting to database-backed storage.

## Common Pitfalls to Avoid
1. ❌ Don't use file system for persistent data
2. ❌ Don't bypass Umbraco's content management patterns
3. ❌ Don't create custom database tables for content
4. ❌ Don't use IMemberService for club lookups (use ClubService)
5. ✅ Always use dependency injection for Umbraco services
6. ✅ Remember `SaveAndPublish()` for content to be visible on frontend
7. ✅ Always use ClubService for club lookups
8. ❌ Don't derive a background service from `BackgroundService` — use `IsolatedBackgroundService` (see the section above)

## Deployment

**CRITICAL:** Always reference `Documentation/PRODUCTION_DEPLOYMENT_GUIDE.md` - **NEVER give deployment advice without reading it first**.

- Full deployment process is documented with exact commands
- Self-contained build required (Simply.com doesn't support .NET 9)
- **MUST remove wwwroot/media/ before upload** to prevent data loss
- Command: `dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"`

## Implementation Status
→ **Hela avsnittet (34 rader): `Documentation/claude/36-implementation-status.md`** — läs det innan du arbetar i det här området.

## Build & Testing
```bash
dotnet build                    # Compile project
dotnet test                     # Run tests (if available)
```

**Admin Access Requirements:**
- Member must be in "Administrators" group
- Access via /admin URL when logged in

## Production Deployment

For detailed deployment instructions, see **[PRODUCTION_DEPLOYMENT_GUIDE.md](Documentation/PRODUCTION_DEPLOYMENT_GUIDE.md)**

**Incremental Deployment (Views/CSS only - 1 minute):**
- View changes: Upload .cshtml files directly (no build needed)
- CSS/JS changes: Upload files directly (no build needed)

**Full Deployment (Recommended - 10 minutes):**
```bash
dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"
Copy-Item 'appsettings.Production.json' -Destination 'C:\temp\publish\' -Force
New-Item -ItemType Directory -Path 'C:\temp\publish\wwwroot\media' -Force
# Upload ALL files from C:\temp\publish\
```

**💡 When in doubt, do a full deployment!**

**Configuration:**
- Self-contained deployment required (win-x86 runtime)
- ModelsBuilder mode: `Nothing` (no strongly-typed models)
- Views must use `@inherits UmbracoViewPage` (dynamic models)

---

## Additional Documentation

For detailed implementation information, see the following documents in the `Documentation/` folder:

### System Architecture & Migrations
- **[PRODUCTION_DEPLOYMENT_GUIDE.md](Documentation/PRODUCTION_DEPLOYMENT_GUIDE.md)** - Complete deployment guide (2025-11-06)
- **[CLUB_SYSTEM_MIGRATIONS.md](Documentation/CLUB_SYSTEM_MIGRATIONS.md)** - Club system migration details (2025-10-30/31)
- **[LOGIN_REGISTRATION_SYSTEM.md](Documentation/LOGIN_REGISTRATION_SYSTEM.md)** - Complete login/registration documentation
- **[TRAINING_SCORING_SYSTEM.md](Documentation/TRAINING_SCORING_SYSTEM.md)** - Training scoring system documentation
- **[AUTHORIZATION_SECURITY_AUDIT.md](Documentation/AUTHORIZATION_SECURITY_AUDIT.md)** - Security audit & fixes (2025-11-02)

### Competition System
- **[SHOOTING_CLASS_STORAGE_SYSTEM.md](Documentation/SHOOTING_CLASS_STORAGE_SYSTEM.md)** - Shooting class storage technical spec
- **[COMPETITION_CONFIGURATION_GUIDE.md](Documentation/COMPETITION_CONFIGURATION_GUIDE.md)** - Competition configuration guide
- **[COMPETITION_RESULTS_WORKFLOW.md](Documentation/COMPETITION_RESULTS_WORKFLOW.md)** - Results entry workflow
- **[SPRINGSKYTTE_STAFF_SCREENS_REUSABLE_PATTERNS.md](Documentation/SPRINGSKYTTE_STAFF_SCREENS_REUSABLE_PATTERNS.md)** - 2026-07 range-role overhaul (per-role/per-class staff screens, field-scoped auto-save, wake-lock, connectivity indicator, deferred offline-queue analysis, move/DNS + timeline free-slots, penalties ledger on every staff screen, unique-per-weapon-class numbering, calculate/publish split, dual-mode wall/operator screen). **Pattern catalogue for porting to other disciplines.**

### Other Documentation
See [Documentation/README.md](Documentation/README.md) for complete documentation index.

---

**Documentation Version:** 2025-11-06 (Production Deployment)
**Umbraco Version:** 16.2
**Build Status:** ✅ Compiles (0 errors)
**Deployment Status:** ✅ Production deployment successful
**Last Updated:** Added production deployment guide and resolved ModelsBuilder issues

## Tävlingsavgifterna i liggaren — fakturamodellens ombyggnad (P3/P4, 2026-09-24)
Kvar-listans post 4. Design: `notes/fakturamodellen-design-2026-09-24.md` (v2, godkänd av Stefan —
**v1 underkändes**, den skrevs utan att dagens fakturering var läst). Stefans uppdrag: *"Allt under
Admin / Fakturor för klubbar och Kretsar ska in under Ekonomin."*
→ **Hela avsnittet (124 rader): `Documentation/claude/37-tavlingsavgifterna-i-liggaren-fakturamodellens-ombyggnad-p3.md`** — läs det innan du arbetar i det här området.

## SIE-importen — en förening flyttar hit mitt i året (P10.2, 2026-09-24)
Kvar-listans post 3. Behovet (F11, revisorn): *"en klubb som byter till oss mitt i ett år behöver få
in sina ingående balanser och gärna tidigare verifikationer"* — och *"SIE-filer är standard i
branschen"*. Utan import kan en klubb bara ansluta vid ett årsskifte.
→ **Hela avsnittet (42 rader): `Documentation/claude/38-sie-importen-en-forening-flyttar-hit-mitt-i-aret-p10-2-2026.md`** — läs det innan du arbetar i det här området.

## Ekonomistatistiken på /admin-page → Statistik (2026-09-24)
Sektion M, laddad från en EGEN endpoint (`AdminStatistics/GetEkonomiStats`, partial-filen
`AdminStatisticsController.Ekonomi.cs`) så att liggarfrågorna aldrig håller tillbaka resten av sidan.
Helt härledd, ingen lagring. Plus en `Ekonomi`-hink (`/ekonomi%`, inloggningskrävd) i besöksstatistiken.
→ **Hela avsnittet (25 rader): `Documentation/claude/39-ekonomistatistiken-pa-admin-page-statistik-2026-09-24.md`** — läs det innan du arbetar i det här området.

## Kretsavgiften: starter och tävlingar i kretsens serier (2026-09-24)
Hallands kretsfaktura (2512 till Varbergs PK) har tre rader: *Årsavgift kretsen, Medlemmar 130 à 10 kr*,
*Lagavgift kretsen, Tävlingar 11 à 20 kr* och *Startavgifter, Starter 140 à 20 kr* — de två sista över
Hallandsserien i precision och fält, räknade för hand klubb för klubb. Nu väljer kretsen tävlingarna och
pistol.nu räknar.
→ **Hela avsnittet (56 rader): `Documentation/claude/40-kretsavgiften-starter-och-tavlingar-i-kretsens-serier-2026-0.md`** — läs det innan du arbetar i det här området.
