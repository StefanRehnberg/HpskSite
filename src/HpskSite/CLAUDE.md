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
