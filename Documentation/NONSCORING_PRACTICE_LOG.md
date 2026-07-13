# Non-Scoring Practice Log (Träningslogg utan poäng) — incl. Vittavla

**Status:** Phase 1 BUILT 2026-07-13 (compiles green; migration + e2e verification pending). Phase 2
(group-size trend) is a fast-follow. Backlog item "vittavla" (Inbox, 2026-07-11) generalized.
**Author of design:** Stefan + Claude, 2026-07-13.

**Deploy before use:** run `Migrations/add-practicetype-to-trainingscores.sql` in SSMS, then full
rebuild. Without the column, every `PracticeType IS NULL` filter + the insert throw at runtime.

## 1. Goal

Let a shooter log a **deliberately non-scoring** practice session — recorded and kept
visible, but **excluded from every scoring aggregate** (average, trend, personal bests,
handicap) while **still counted as training volume** ("did I train", aktiv-skytt evidence).

Two driving cases, one concept:

| Flavor | Discipline(s) | What's captured |
|---|---|---|
| **Vittavla** (white-target trigger drill) | Precision family | weapon class + groups (each 5 or n shots) + **optional group size** |
| **Fri övning** (e.g. fältskytte home-target practice) | any discipline incl. Fältskytte | weapon class + total shots + notes, no scoring |

Vittavla is the richest flavor of a general **non-scoring practice log**. Fri övning is the
same shape with the group structure collapsed to one entry of *n* shots and no group size.

The white-target drill always yields 0 points by design; today the only way to log it is a
normal Resultat row, so `TotalScore = 0` pollutes averages/trend/PBs. Fältskytte practice has
**no** self-log path at all today (the only fältskytte self-entry is träff/figur scored).

## 2. Concept / data model

**One orthogonal classifier on the training log — not a new discipline.**
`Discipline` says *which shooting context*; a new `PracticeType` says *"non-scoring practice +
which flavor"*. The stat-exclusion rule is dead simple: **any non-null `PracticeType` → excluded
from scoring, counted as volume.**

### 2.1 Schema

```sql
-- Migrations/add-practicetype-to-trainingscores.sql
ALTER TABLE TrainingScores ADD PracticeType NVARCHAR(30) NULL;
```

- `NULL` = normal scored training (every existing row — zero migration risk, mirrors the
  existing `Discipline IS NULL` backward-compat handling in `UnifiedResultsService`).
- `'Vittavla'` = white-target trigger drill (Precision family).
- `'Fri'` = generic non-scoring practice (any discipline, incl. Fältskytte).
- Extensible: future 0-point drills add a value, no schema change.

No new `ShotCount` column — shot count lives in the `SeriesScores` JSON (below) and is summed in
code. Volume metrics count **rows** (= sessions); shot count is a display/detail figure.

### 2.2 `SeriesScores` JSON shape (Practice rows)

Reuses the existing `List<TrainingSeries>` container. Each element = **one group**.

```jsonc
// Vittavla, 3 groups of 5, two measured:
[ { "entryMethod": "Practice", "shotCount": 5, "groupSizeMm": 32 },
  { "entryMethod": "Practice", "shotCount": 5, "groupSizeMm": 41 },
  { "entryMethod": "Practice", "shotCount": 5 } ]

// Fri övning (fältskytte), 30 shots, no groups:
[ { "entryMethod": "Practice", "shotCount": 30 } ]
```

- **Total shots** (volume) = `Series.Sum(s => s.ShotCount)`.
- `TotalScore = 0`, `XCount = 0` on the row.

### 2.3 Model changes

`HpskSite.Shared/Models/TrainingSeries.cs` — add two optional fields (only meaningful when
`EntryMethod == "Practice"`):

```csharp
[JsonPropertyName("shotCount")]   public int? ShotCount { get; set; }
[JsonPropertyName("groupSizeMm")] public int? GroupSizeMm { get; set; }
```

`TrainingSeries.IsValid()` — add a `"Practice"` case that validates on `ShotCount > 0` only
(Shots null, Total 0). **Required** because the current validators reject 0-point rows:
`ValidateTotalOnly`/`ValidateSeriesTotal` both demand `Total > 0`.

`TrainingScoreEntry` (BOTH copies — `Models/ViewModels/TrainingScoring/` and `HpskSite.Shared/Models/`)
carries `PracticeType` (string?). `IsValid()` already passes (≥1 series, ≤24) once the series
validator accepts Practice; `CalculateTotals()` already leaves `Total = 0` when Shots are null.

## 3. Entry UX — one dedicated modal + button (AS BUILT)

**Build-time decision (2026-07-13): a dedicated modal, NOT a toggle inside each existing modal.**
The scored flows (the shared precision-family `TrainingScoreEntry.cshtml` keypad and the separate
Fältskytte comp modal) are complex; a mode-branch on each risked regressions and still needed two
implementations. A practice entry is discipline-agnostic (weapon class + shots + optional groups +
notes), so one small self-contained modal serves every discipline and leaves the scored paths
untouched.

- New **"Logga övningspass"** button beside "Lägg in Resultat" in the Resultat-tab toolbar
  (`UserProfile.cshtml`). Opens `#practiceLogModal` (`openPracticeLogModal()`), prefilled with the
  discipline the Resultat dropdown is on.
- **Typ av övning** selector, discipline-aware:
  - **Precision** → `Vittavla – femskottsgrupper` (N groups of 5, per-group optional `Gruppstorlek (mm)`),
    `Vittavla – fritt antal skott` (n shots + one optional mm), `Fri övning (utan poäng)` (n shots, no mm).
  - **All other disciplines incl. Fältskytte** → `Fri övning (utan poäng)` only (weapon class + n shots + notes).
- Submits JSON to the dedicated endpoint **`TrainingScoring/RecordPracticeLog`** (§4.5). Delete
  reuses the generic `TrainingScoring/DeleteTrainingScore` (owner-checked; medal-cleanup + handicap
  recalc are safe no-ops for practice rows).
- The Fältskytte comp modal + its self-log endpoints are **untouched** — fältskytte practice rides
  the generic endpoint with `Discipline='Faltskytte'`, `PracticeType='Fri'`.

## 4. Stat touch-points (AS BUILT)

**Chosen strategy: exclude practice rows at the SQL source in every scoring/stat read path**
(`WHERE PracticeType IS NULL`), rather than tagging a `SourceType` and splitting downstream. This
keeps the fragile dashboard-aggregation code untouched — the loaders simply never return practice
rows, so averages/trend/PB/monthly-chart can't see them. Volume + visibility come from separate
dedicated queries (§4.6/§4.7). `UnifiedResultEntry` did **not** need a `PracticeType` field.

### 4.1 Scoring read paths — `AND PracticeType IS NULL` at source

1. `Services/UnifiedResultsService.cs` `GetTrainingScoresResults` (Precision dashboard + `GetMyResults`).
2. `Controllers/TrainingScoringController.cs` `LoadDisciplineResults` (Milsnabb/Duell/NatHelmatch/MagnumPrecision dashboard).
3. `Controllers/MemberController.cs` `GetMemberResultsForDiscipline` (the `GetMemberDashboard` duplicate path).
4. `Controllers/MemberController.cs` `GetMemberResults` scored Query 2 (`disciplineFilter`).
5. `CompetitionTypes/Faltskytte/Services/FaltskytteStatsService.cs` `LoadExternal` (both year/all
   variants) — critical: a practice row's `SeriesScores` is practice groups, NOT a
   `FaltskytteExternalPayload`, so it must never reach the season parse.
6. `Controllers/TrainingScoringController.cs` `GetPersonalBests` (extra `.Where("PracticeType IS NULL")`).

### 4.2 Handicap — CTE fix (the subtle one)

`Services/ShooterStatisticsService.cs` `RecalculateFromHistoryAsync` + its as-of variant rebuild
the handicap baseline from a `TrainingScores` CTE (the "Self-entered training" branch). Added
`AND ts.PracticeType IS NULL` there so a 0-point practice row can't enter the handicap window.
(The incremental `UpdateAfterMatchAsync` path is never called for practice — `RecordPracticeLog`
skips it entirely.)

### 4.3 Left correctly INCLUDED (practice = training volume)

Activity/volume counters intentionally still count practice rows: `HomeHubService` (träningsdagar/
streak + last-trained), `ClubComparisonService` (active members), `ClubStatisticsController` +
`AdminStatisticsController` (activity counts). `RankingSnapshotService` is match-only (practice has
no `TrainingMatchId`) and reads scoring from the now-clean `ShooterStatistics`, so ranking is safe.

### 4.4 Write path

New `TrainingScoringController.RecordPracticeLog([FromBody] PracticeLogRequest)` (+ `PracticeGroupDto`):
validates login/date/weapon class/groups, normalizes `PracticeType` (`Vittavla`|`Fri`), builds a
`List<TrainingSeries>` of `EntryMethod="Practice"` groups, inserts with `TotalScore=0`,
`IsCompetition=false`, `PracticeType` set. Deliberately does **not** call the handicap statistics or
the Standardmedalj ledger. Delete reuses the generic `DeleteTrainingScore`.

**Antiforgery gotcha:** these SurfaceController POST/DELETE calls validate an antiforgery token —
the client fetch MUST send a `RequestVerificationToken` header read from the page's hidden
`__RequestVerificationToken` input, or the request 400s before the action runs (same as every other
`RecordTrainingScore`/`DeleteTrainingScore` call on the page).

### 4.5 Volume + visibility

- `MemberController.BuildPracticeResults(memberId, discipline, year, weaponClass)` returns the
  discipline-scoped practice rows (parsed to `{date, weaponClass, practiceType, shotCount,
  groupCount, groupSizes[], notes}`), included as `practiceResults` in **both** `GetMemberResults`
  responses (Fältskytte branch + main branch).
- **Föreningsintyg / aktiv-skytt evidence** (backlog item): include practice rows in the *träningar*
  tally when that item is built — legitimate training for both families. Positive synergy.

### 4.6 Resultat-tab UI (`UserProfile.cshtml`)

A dedicated **"Övningspass (utan poäng)"** card (`#practiceResultsContent`, `renderPracticeResults`)
below the scored results table, fed by `data.practiceResults`, for every discipline. Each row shows
date · type badge (Vittavla/Fri övning) · weapon class · "N skott [· grupper: a, b mm]" · notes ·
delete. The card count doubles as the visible volume metric; a footer states it doesn't affect
snitt/trend/personbästa. Cleared at the top of `loadResults()` so it can't linger on switch/error.

## 5. Left alone on purpose

- Training match (vittavla/fri övning is solo practice).
- "Spara som Guldserie/Snabbserie" quick-submit (a practice row can't be a märkesserie).
- Standardmedalj sync (keys off medal fields; a practice row has none).

## 6. Unification with the DNF/incomplete-results backlog item

Don't merge the *causes* (vittavla = user-declared at entry; DNF = derived from
`SeriesCount < expected`). Merge the *filter*: one predicate `CountsTowardScoringStats(entry)`
returning false when `PracticeType` is set **or** the result is incomplete, routed through the
same `SourceType`-style exclusion. Build the practice log first (self-contained, low-risk); the
DNF item then reuses the same exclusion plumbing.

## 7. Phasing

- **Phase 1 (core):** `PracticeType` column + `"Practice"` entry method + the two modal toggles +
  the §4 stat touch-points + Resultat-list flagging. Ship logging + correct stat exclusion first.
- **Phase 2 (fast follow, Precision/vittavla only):** a **separate** vittavla view over Practice
  rows with a non-null `GroupSizeMm` — **Gruppstorlek över tid** (lower = better) + **Bästa grupp**
  (smallest measured). Never mixed with score. This is the drill's real feedback loop.

## 8. Deploy

- Run `Migrations/add-practicetype-to-trainingscores.sql` in SSMS.
- Full rebuild (C# model + controller changes). Razor views runtime-compiled → load Min sida
  Resultat once after deploy.
- No new Umbraco doctype/property/node.

## 9. Decisions (resolved 2026-07-13)

1. **Group-size units — store mm, display mm.** Integer millimetres end to end; no unit
   conversion, no decimal-comma parsing trap. Label fields `Gruppstorlek (mm)`.
2. **Phase-2 timing — fast-follow.** Ship Phase 1 (logging + stat exclusion) first, then land the
   Gruppstorlek-över-tid + Bästa grupp view over clean data.
