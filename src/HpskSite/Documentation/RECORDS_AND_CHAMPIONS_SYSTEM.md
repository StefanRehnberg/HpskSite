# Klubb-/Kretsrekord och Mästartitlar

Complete documentation for the records (rekord) and champions (mästartitlar) system.

**Last updated:** 2026-04-30

---

## Overview

Two related, manually-curated features for celebrating shooting achievements at club and region level:

- **Records** (`CompetitionRecords`) — the best score ever achieved in a given scope, discipline, record type and class. One "current" record per key, with a full history chain when previous records have been beaten.
- **Champions** (`CompetitionChampions`) — annual klubb-/kretsmästerskap titles. One row per (Year, scope, discipline, type, class). The "reigning" champion is the highest-Year row per key.

Both apply only to **Precisionsskjutning, Magnumprecision, Militär snabbmatch**. Both are entered manually by admins (auto-detection from competition results was prototyped but abandoned — many clubs don't run results through pistol.nu yet). Both are visible to all logged-in members; entry is gated by club admin / regional admin / site admin per the standard authorization pattern.

---

## 1. Records

### Data model

Table `CompetitionRecords` (`Migrations/create-competition-records-table.sql`):

| Column | Notes |
|---|---|
| `Id` | PK |
| `Level` | `Club` / `Region` |
| `ScopeId` | clubId (string) for Club / regionCode for Region |
| `Discipline` | `Precision`, `MagnumPrecision`, `Milsnabb` |
| `RecordType` | `Individual`, `Team` |
| `ClassCode` | from `RecordClassRegistry` (see §3) |
| `TotalScore` | int, validated against `50 × seriesCount` |
| `SeriesCount` | audit; auto-set from `RecordClassRegistry.GetSeriesCount` |
| `RecordDate` | date of the competition |
| `CompetitionName` | free text — no FK to internal competition nodes |
| `HolderMemberId` | nullable — record may belong to non-member |
| `HolderName` | always populated (so name remains stable if member is deleted) |
| `TeamName`, `TeamMembersJson` | for team records |
| `IsCurrent` | bit — exactly one current row per `(Level, ScopeId, Discipline, RecordType, ClassCode)` |
| `ReplacedByRecordId` | chains the history when a record is beaten |
| `EnteredByMemberId`, `EnteredAt`, `Notes` | audit |

On Create: insert with `IsCurrent=1`, flip the previous current row to `IsCurrent=0` and set `ReplacedByRecordId` to the new id, all in a transaction.

On Delete: if deleting the current row, find the prior `ReplacedByRecordId == thisId` row and re-promote it to `IsCurrent=1`. Otherwise repair the chain by detaching links pointing at the deleted row.

### Files
- `Models/CompetitionRecord.cs` — POCO + enums (`RecordLevels`, `RecordDisciplines`, `RecordTypes`)
- `Services/CompetitionRecordsService.cs` — single writer
- `Controllers/CompetitionRecordsController.cs` — endpoints (list, history, create, update, delete + member autocomplete pool)

### UI
- **Tab on Club page**: `Views/Partials/RecordsHallOfFame.cshtml` (rendered from `Club.cshtml` "Rekord" tab)
- **Tab on Region page**: same partial, scoped to Region
- **Snabblänk on club home and region home** quick-links column → deep-links to the Rekord tab (logged-in only)
- **Profile card**: `Views/Partials/MemberRecordsCard.cshtml` on `UserProfile.cshtml` — shows records held by the current member
- **Member detail modal**: line in `ClubMembersDirectory.cshtml` showing "Innehar X klubbrekord och Y kretsrekord" with badges per record

The Hall of Fame partial has a hero strip (best individual record per discipline), Individuell/Lag pill switch, and per-class table grids. Click a row → history popover. Admins see "Lägg till rekord" + per-row delete buttons.

### Endpoints (`Controllers/CompetitionRecordsController.cs`)

Login required for all reads, scope-admin for writes.

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET ListForClub(clubId)` | logged in | current records for the club |
| `GET ListForRegion(regionCode)` | logged in | current records for the region |
| `GET ListForMember(memberId)` | logged in | records held by a member |
| `GET History(level, scopeId, discipline, recordType, classCode)` | logged in | full history chain |
| `POST Create` | scope admin | grant new record (flips previous current) |
| `POST UpdateMeta` | scope admin | edit score/date/competition/notes |
| `POST Delete` | scope admin | re-promotes prior holder if exists |

---

## 2. Champions

### Data model

Table `CompetitionChampions` (`Migrations/create-competition-champions-table.sql`):

| Column | Notes |
|---|---|
| `Id` | PK |
| `Level` | `Club` / `Region` |
| `ScopeId` | clubId (string) / regionCode |
| `Year` | int — championship year (e.g. 2026) |
| `Discipline` | as above |
| `ChampionType` | `Individual`, `Team` |
| `ClassCode` | from `RecordClassRegistry` |
| `TotalScore` | int |
| `CompetitionName`, `CompetitionDate` | free text + optional date |
| `HolderMemberId`, `HolderName`, `TeamName`, `TeamMembersJson` | as for records |
| `Notes`, `EnteredByMemberId`, `EnteredAt` | audit |

Unique business rule: at most one row per `(Level, ScopeId, Year, Discipline, ChampionType, ClassCode)` (enforced via `UX_CompetitionChampions_Key` unique index). Friendly duplicate-error returned by the service.

"Reigning" = highest-Year row per `(Level, ScopeId, Discipline, ChampionType, ClassCode)`. There's no `IsCurrent` flag — derivation is cheap because the table is small.

### Files
- `Models/CompetitionChampion.cs` — POCO
- `Services/CompetitionChampionsService.cs` — read (reigning, history, by-member, all-for-scope) + write (Create, Delete)
- `Controllers/CompetitionRecordsController.cs` — adds champion endpoints alongside records (they're conceptually adjacent)

### UI
- **Read-only display** on:
  - Club home page (Snabblänkar right-column area, logged-in only)
  - Club Members tab (above the directory)
  - Region home page (right column, logged-in only)
- **Admin management**: dedicated tab in `ClubAdminPanel.cshtml` and `RegionalAdminPanel.cshtml` — separate from member-facing surfaces. Admin tab shows full history (all years).
- **Member detail modal**: line in `ClubMembersDirectory.cshtml` showing "Innehar N klubbmästartitlar och M kretsmästartitlar" with KM/KrM badges per title.

The display partial `Views/Partials/ReigningChampionsPanel.cshtml` accepts two flags via ViewData:
- `ChampionsCanEdit` — admin actions visible
- `ChampionsShowAllYears` — switches from "reigning per class" view to "all years per class" history view

Admin entry uses `Views/Partials/ChampionEntryModal.cshtml`. Same member autocomplete pattern as the record entry modal.

### Endpoints

| Endpoint | Auth | Purpose |
|---|---|---|
| `GET ChampionsForClub(clubId, includeHistory?)` | logged in | reigning OR all-years for the club |
| `GET ChampionsForRegion(regionCode, includeHistory?)` | logged in | same for region |
| `GET ChampionsForMember(memberId)` | logged in | titles held by a member |
| `GET ChampionHistory(...)` | logged in | full history for one scope/class |
| `POST ChampionCreate` | scope admin | add (rejects duplicates with friendly message) |
| `POST ChampionDelete` | scope admin | remove |

---

## 3. Class registry

Shared between records and champions: `Models/RecordClassRegistry.cs`.

| Discipline | Series (Ind/Team) | Max (Ind/Team) | Individual classes | Team classes |
|---|---|---|---|---|
| Precision | 10 / 7 | 500 / 350 | A, B, C, C_Dam, C_Jun, C_VetY, C_VetA | A, B, C, C_Dam, C_Jun, C_Vet |
| MagnumPrecision | 6 / 6 | 300 / 300 | M1–M7 | M1–M7 |
| Milsnabb | 12 / 12 | 600 / 600 | A, B, C, C_Dam, C_Jun, C_VetY, C_VetA, R | A, B, C, C_Dam, C_Jun, C_Vet, R |

`GetMaxScore = 50 × GetSeriesCount`. `GetClassDisplayName` returns the human label (e.g. `C_VetY` → "C Vet Y").

---

## 4. Authority

Same model as the certifications module:

| Action | Required role |
|---|---|
| View records or champions | Logged in |
| Create/edit/delete Club records or champions | Site admin OR club admin for the club |
| Create/edit/delete Region records or champions | Site admin OR regional admin for the region |

Reuses `AdminAuthorizationService.IsClubAdminForClub(clubId)`, `IsRegionalAdminForRegion(regionCode)`, `IsCurrentUserAdminAsync()`.

Note: the **add buttons are only on the admin tab** (Club admin → Mästare, Region admin → Mästare). The member-facing display panels are read-only even for admins, to keep member surfaces uncluttered.

---

## 5. Member autocomplete

Both entry modals (`RecordEntryModal.cshtml`, `ChampionEntryModal.cshtml`) share the same autocomplete pattern:

- Pool source:
  - For Club scope → `/umbraco/surface/ClubAdmin/GetClubMembers?clubId=<id>`
  - For Region scope → `/umbraco/surface/CompetitionRecords/GetRegionMembers?regionCode=<code>`
- Each suggestion shows: name on first line, `<club> · Pistolkortnr <N>` on second line.
- Match is on name, club name, OR Pistolkortnr — typing `1234` or `Halland` filters the list.
- Picking a suggestion sets `holderMemberId`. Free typing leaves it null. Names persist in `HolderName` even if the linked member is later deleted.

---

## 6. Operator deployment

Two manual SQL migrations — run both in SSMS against the Umbraco DB before deploy:

```
Migrations/create-competition-records-table.sql
Migrations/create-competition-champions-table.sql
```

Both scripts are idempotent (check `OBJECT_ID(...) IS NULL` before creating). After deploy:

1. Records: navigate to Club/Region → Rekord tab → "Lägg till rekord" (admin only)
2. Champions: navigate to Club admin → Mästare tab (or Region admin → Mästare) → "Lägg till mästare"
3. Backfill historic champions per year — admins can add as far back as desired

No CompositionRoot or Umbraco backoffice changes required (no doctype properties, no member groups).

---

## 7. File index

### Added
- `Migrations/create-competition-records-table.sql`
- `Migrations/create-competition-champions-table.sql`
- `Models/CompetitionRecord.cs`
- `Models/CompetitionChampion.cs`
- `Models/RecordClassRegistry.cs`
- `Services/CompetitionRecordsService.cs`
- `Services/CompetitionChampionsService.cs`
- `Controllers/CompetitionRecordsController.cs`
- `Views/Partials/RecordsHallOfFame.cshtml`
- `Views/Partials/RecordEntryModal.cshtml`
- `Views/Partials/MemberRecordsCard.cshtml`
- `Views/Partials/ReigningChampionsPanel.cshtml`
- `Views/Partials/ChampionEntryModal.cshtml`

### Edited (representative — the full surface area)
- `Composers/AdminServicesComposer.cs` — registered the two services
- `Views/Partials/ClubNavigation.cshtml` + `Views/Club.cshtml` — Rekord tab, Snabblänk, Mästare tab in admin panel
- `Views/Partials/RegionalNavigation.cshtml` + `Views/RegionalPage.cshtml` — Rekord tab, Mästare tab in admin panel
- `Views/Partials/_RegionPublicContent.cshtml` — fixed broken Kretsrekord Snabblänk + reigning champions read-only panel
- `Views/Partials/ClubAdminPanel.cshtml` + `Views/Partials/RegionalAdminPanel.cshtml` — Mästare admin tabs
- `Views/Partials/ClubMembersDirectory.cshtml` — reigning champions panel (read-only) + records/champion summary in member detail modal
- `Views/UserProfile.cshtml` — `MemberRecordsCard` on Profil tab

---

## 8. Future work

- **Auto-detect from competition results**: explored, parked. The infrastructure to read internal competition results per class winner exists (`CompetitionResultsController.CalculateFinalResults`); a future flag could let clubs that DO run results through pistol.nu opt in and auto-fill championship entries when a competition with `competitionScope == "Klubbmästerskap"` is finalized.
- **Backfill UX**: a CSV-import endpoint for historic champions would help clubs migrate decades of data faster than the row-by-row modal.
- **Public visibility**: currently logged-in only. Could be relaxed to anonymous if clubs want their records/champions visible to non-members.
