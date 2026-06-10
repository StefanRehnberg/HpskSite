# Claude Development Notes - pistol.nu

## Project Overview
Umbraco v16.2 project for pistol.nu (formerly HPSK) featuring member management, club administration, training system (Skyttetrappan), and competition management.

## Knowledge Base Maintenance
When making changes to **user-facing features** (views, controllers that affect UI/workflows, button labels, new features, removed features), check if the knowledge base at `src/HpskSite/KnowledgeBase/docs/` needs to be updated. The knowledge base is used by an AI chat assistant on the site to help users. Each doc has a `roles` frontmatter tag — update the role list if access control changes.

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

### Admin Controllers (Refactored 2025-10-28)
The admin functionality has been refactored from a monolithic AdminController into specialized controllers following Single Responsibility Principle:

**AdminAuthorizationService** (`Services/AdminAuthorizationService.cs`)
- Centralized authorization logic for all admin controllers
- Registered as singleton in `AdminServicesComposer.cs`
- Key methods:
  - `IsCurrentUserAdminAsync()` - Site admin check
  - `IsClubAdminForClub(clubId)` - Club-specific admin check
  - `GetManagedClubIds()` - Get clubs user can administer
  - `IsCompetitionManager(competitionId)` - Check if user manages specific competition
  - `EnsureClubAdminGroup(clubId, clubName)` - Create club admin groups

**Authorization Pattern (2025-11-02):**
Most endpoints follow a three-tier authorization pattern:
```csharp
// Check Site Admin first (has access to everything)
bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

// Check Competition Manager (for competition-specific endpoints)
bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);

// Check Club Admin (for club-scoped access)
bool isClubAdmin = false;
var competitionClubId = competition.Value<int>("clubId");
if (competitionClubId > 0)
{
    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
}

// Grant access if ANY role applies
if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin)
{
    return Json(new { success = false, message = "Access denied" });
}
```

**See Also:** [Authorization Security Audit Documentation](Documentation/AUTHORIZATION_SECURITY_AUDIT.md) for complete security fixes (2025-11-02)

**MemberAdminController** (`Controllers/MemberAdminController.cs`)
- 8 endpoints for member management
- GET: GetMembers, GetMember, GetMemberGroups, GetPendingApprovals
- POST: SaveMember, DeleteMember, SeedRandomMembers, FixUsersWithoutGroups
- Used by: Views/Partials/UserManagement.cshtml

**ClubAdminController** (`Controllers/ClubAdminController.cs`)
- 19 endpoints for club management
- CRUD: GetClubs, GetClub, SaveClub, DeleteClub, CheckClubCanBeDeleted
- Members: GetClubMembers, GetClubMembersForClubAdmin, GetPendingApprovalsCount
- Public: GetClubsForRegistration, GetClubsPublic
- Admin Assignment: AssignClubAdmin, RemoveClubAdmin, GetClubAdmins, GetAvailableMembersForClubAdmin
- Validation: CleanupInvalidClubReferences
- Migration: DebugClubs, InitializeClubs, PreviewClubMigration, MigrateClubReferences
- Used by: Views/Partials/ClubManagement.cshtml, Views/ClubsPage.cshtml, Views/ClubAdmin.cshtml, Views/Partials/Register.cshtml

**RegistrationAdminController** (`Controllers/RegistrationAdminController.cs`)
- ~10 endpoints for competition registration management (cashier desk + admin)
- GET: GetCompetitionRegistrations, GetActiveCompetitions, GetWalkInStartListTeams
- POST: UpdateCompetitionRegistration, DeleteCompetitionRegistration, ExportCompetitionRegistrations, AddLateRegistration, TransferRegistration, SetCheckedIn, AssignWalkInToStartListTeam
- Used by: Views/Partials/RegistrationManagement.cshtml, Views/Partials/CompetitionRegistrationManagement.cshtml, Views/Partials/CompetitionExportManagement.cshtml, Views/CompetitionManagement.cshtml

**Benefits of Refactoring:**
- Clear separation of concerns
- Easier maintenance and testing
- Improved code organization
- Reduced code duplication via shared AuthorizationService

## Club System Architecture

### Implementation (Document Type Based)
Clubs are stored as **Document Type nodes** under clubsPage:

**Structure:**
```
Home
└── Clubs (clubsPage)
    ├── Club 1 (club)
    ├── Club 2 (club)
    └── Club 3 (club)
```

**Club Document Type Properties:**
- clubName, description, aboutClub
- contactPerson, contactEmail, contactPhone, webSite
- address, city, postalCode
- logo, bannerImage (Media Pickers)
- **IsActive**: Determined by Published status

**Club Events:**
- Child nodes with `clubSimpleEvent` document type
- Properties:
  - eventName, eventDate, description, venue
  - eventType (Dropdown): "Tävling", "Träning", "Städning", "Möte", "Socialt", "Annat"
  - contactPerson, contactEmail, contactPhone
  - isActive (Boolean)

### Club Admin System
**Member Groups Patterns**:
- `ClubAdmin_{ClubId}` (e.g., ClubAdmin_1098) — Club administrator
- `Skjutledare_{ClubId}` (e.g., Skjutledare_1098) — Range Master (Skjutledare)
- `RegionalAdmin_{RegionCode}` (e.g., RegionalAdmin_Stockholm) — Regional administrator
- `Foreningsinstruktor_{ClubId}` — Appointed Föreningsinstruktör for the club (cert-backed; see [CERTIFICATIONS_SYSTEM.md](Documentation/CERTIFICATIONS_SYSTEM.md))
- `Kretsinstruktor_{RegionCode}` — Appointed Kretsinstruktör for the region (cert-backed)
- `Riksinstruktor_{AreaCode}` — Appointed Riksinstruktör for an area (`Syd`/`Vast`/`Ost`/`Nord`)
- `Vapenkontrollant`, `Banlaggare` — Global groups, auto-managed by `CertificationService` when the personal cert is granted/revoked

**Permission Hierarchy:**
1. Site Administrators - Full access to all clubs
2. Regional Administrators - Full access to all clubs in their region
3. Club Administrators - Access only to assigned club(s)
4. Skjutledare (Range Master) - Approve training steps + manage competitions for their club
5. Trainer (Training Group) - Approve training steps for their training group members only
6. Regular Users - No admin access

**Certified roles (separate from appointed roles)**: Föreningsinstruktör, Kretsinstruktör, Riksinstruktör, Vapenkontrollant, Banläggare are SPSF-registered certifications. The cert is a personal credential stored in the `MemberCertifications` table; the appointment to a specific scope is held in the member groups above. See [CERTIFICATIONS_SYSTEM.md](Documentation/CERTIFICATIONS_SYSTEM.md) for the full architecture, authority hierarchy, and operator setup steps.

**Key API Methods** (AdminAuthorizationService.cs):
```csharp
IsCurrentUserAdminAsync()          // Site admin check
IsClubAdminForClub(clubId)         // Club-specific admin check (includes regional admin)
IsSkjutledareForClub(clubId)       // Check if Skjutledare for specific club
IsSkjutledareForMember(memberId)   // Check if Skjutledare for member's club
GetManagedClubIds()                // Get clubs user can administer
GetSkjutledareClubIds()            // Get clubs where user is Skjutledare
EnsureSkjutledareGroup(clubId)     // Create Skjutledare group if missing
```

### Club Admin Panel ✅ COMPLETE (Phase 1)
**Location:** Club.cshtml → Admin tab (visible to club admins + site admins)

**Features:**
- Tabbed interface (Events, Competitions, Settings)
- CRUD operations for club events via modals
- Club information editing
- Permission-based access control

**API Endpoints** (ClubController.cs):
All endpoints require club admin authorization (user must be club admin for the specific club OR site admin):
- POST CreateClubEvent - Create new club events
- POST EditClubEvent - Update existing club events
- POST DeleteClubEvent - Remove club events
- POST CreateClubNews - Create club news items
- POST EditClubNews - Update club news items
- POST DeleteClubNews - Remove club news items
- POST UpdateClubInfo - Update club contact information

**Authorization:** Each endpoint checks `await _authorizationService.IsClubAdminForClub(clubId)` before allowing modifications.

**Files:**
- Views/Partials/ClubAdminPanel.cshtml
- Club event management modals

### Club Lookup Service ✅ (2025-10-30)

**CRITICAL: Never use `IMemberService` to look up clubs!**

Clubs are stored as **Document Type nodes**, not as members. Using `IMemberService.GetById(clubId)` will fail silently and return null or wrong data.

**ClubService** (`Services/ClubService.cs`)
- Centralized service for club lookups
- Registered as singleton in `ClubServiceComposer.cs`
- Methods:
  - `GetClubNameById(int clubId)` - Returns club name or null
  - `GetClubById(int clubId)` - Returns ClubInfo object or null

**Correct Pattern:**
```csharp
// ✅ CORRECT - Use ClubService
public class MyController : SurfaceController
{
    private readonly ClubService _clubService;

    public MyController(..., ClubService clubService)
    {
        _clubService = clubService;
    }

    private string GetClubName(int clubId)
    {
        return _clubService.GetClubNameById(clubId) ?? "Unknown Club";
    }
}
```

**Wrong Pattern (DO NOT USE):**
```csharp
// ❌ WRONG - Don't use IMemberService for clubs
var club = _memberService.GetById(clubId);  // Returns null or wrong data!
var clubName = club?.Name;  // Will be null or incorrect
```

**See Also:** [Club System Migrations Documentation](Documentation/CLUB_SYSTEM_MIGRATIONS.md) for complete migration history

## Member System

### Member Type: hpskMember
**Custom Properties:**
- firstName, lastName
- primaryClubId (int) - Links to club content node ID
- memberClubIds (CSV) - Additional club memberships
- Training properties (see Training System section)

**Key Implementation Details:**
- All new members auto-assigned to "Users" group
- Filter queries to exclude club member types (obsolete pattern, now just regular members)
- Admin check: `IsCurrentUserAdminAsync()` checks for "Administrators" role

### Login & Registration System ✅ COMPLETE (2025-11-02)

**Overview:** Comprehensive member authentication and registration system with email notifications, approval workflow, and enhanced user experience.

**Key Features:**
- Smart redirect after login (to previous page)
- Enhanced error messages (pending approval, invalid credentials, etc.)
- Member registration with club selection
- Email notification service (5 templates)
- Missing club request feature
- Unified approval system (2025-11-03)

**Location:** `/login-register` page with tabbed interface

**See Also:** [Login & Registration System Documentation](Documentation/LOGIN_REGISTRATION_SYSTEM.md) for complete implementation details

## Training System (Skyttetrappan)

### Overview
9 progressive training levels, 74 total steps. Training progress is stored on member properties (not on training groups), so progress persists even when training groups are closed.

### Member Properties Required
Add to hpskMember type in backoffice:
- currentTrainingLevel (Numeric)
- currentTrainingStep (Numeric)
- completedTrainingSteps (Textarea for JSON)
- trainingStartDate, lastTrainingActivity (Date Pickers)
- trainingNotes (Textarea)

### API Endpoints (TrainingController.cs)
**Public:** GetTrainingOverview, GetLeaderboard, GetMemberProgress
**Member:** StartTraining
**Admin/Trainer/Skjutledare:** CompleteStep, GetMemberProgress?memberId=X
**Site Admin only:** ResetProgress

### Step Approval Authorization
Training step approval (`CompleteStep`) uses a four-tier authorization check:
1. **Site Admin** — can approve any member
2. **Trainer** — can approve members in their active training group (`IsTrainerForMember`)
3. **Skjutledare** — can approve members at their club, even without an active training group (`IsSkjutledareForMember`)
4. **Club Admin** — can approve members at their club (`IsClubAdminForClub`)

The same tiers apply to `GetMemberProgress` when viewing another member's progress.

### Training Groups System

**Database Tables:** `TrainingGroups`, `TrainingGroupMembers` (see `Scripts/CreateTrainingGroupTables.sql`)

**Service:** `TrainingGroupService.cs` — CRUD for training groups, member/trainer management, authorization checks

**Controller:** `TrainingGroupController.cs` — API endpoints for training group management

**Key Concepts:**
- Training groups belong to a club (`ClubId`) and have an `IsActive` flag
- Members in a group have a `Role` of either `"Member"` or `"Trainer"`
- Trainers can approve steps for members in their active group
- When a group is deactivated (closed), `IsTrainerForMember` returns false, but Skjutledare and Club Admins can still approve steps
- Progress is stored on member properties, not on the group — closing a group preserves all progress

**Authorization for Training Group Management (`CanManageTrainingGroup`):**
1. Site Admin
2. Club Admin for the group's club
3. Skjutledare for the group's club
4. Trainer in the group

**UI Locations:**
- `/skyttetrappan/` — "Min Traningsgrupp" tab (members/trainers), "Administration" tab (admins/skjutledare)
- Club Admin Panel — "Traningsgrupper" tab for club-scoped management

**Features:**
- Create/edit/deactivate training groups
- Add/remove members and trainers
- Per-member step-by-step approval view (Visa framsteg)
- Group email messaging (trainer to group members)
- Opt-in welcome email when adding members
- Email notification on step approval

### Implementation Status
✅ Models, API, UI, Admin Interface
✅ Training Groups (database, service, controller, UI)
✅ Skjutledare integration
⏳ Member properties setup in Umbraco backoffice

## Training Scoring System

### Overview
Self-service training log system where members record individual training sessions with detailed shot-by-shot data. Completely separate from Skyttetrappan (structured curriculum). Used for personal progress tracking and improvement analysis.

**Key Features:**
- Self-service entry (no admin approval required)
- Shot-by-shot tracking with automatic calculations
- Personal best tracking (training vs competition)
- Dashboard with Chart.js visualizations
- Unified results from multiple sources (training, competitions)

**Data Storage:** Database table `TrainingScores`
**Controller:** `TrainingScoringController.cs`
**UI:** Integrated into UserProfile.cshtml with 3 tabs (Dashboard, Profil, Träningsresultat)

**Database Schema:**
- MemberId, TrainingDate, WeaponClass (A, B, C, R, P)
- IsCompetition (bool) - Tracks external competition results
- SeriesScores (JSON), TotalScore, XCount, Notes

**Key Models:**
- `TrainingSeries.cs` - Single series (5 shots)
- `TrainingScoreEntry.cs` - Complete training session
- `PersonalBest.cs` - Personal best tracking

**API Endpoints:**
- POST RecordTrainingScore - Add new training score
- GET GetMyTrainingScores - Get member's scores with pagination
- GET GetPersonalBests - Get personal bests by weapon class
- GET GetDashboardStatistics - Comprehensive statistics for dashboard
- PUT UpdateTrainingScore - Edit existing score
- DELETE DeleteTrainingScore - Delete score

**Dashboard Features (Redesigned 2025-10-31):**
- Year filter dropdown
- 3 quick stats cards (Activity Summary, Current Form, Personal Bests)
- Progress Over Time chart (Chart.js line chart, individual data points)
- Weapon Class Performance chart (Chart.js bar chart, aggregated averages)
- Quick actions (register score, view all results)

**Unified Results System:**
Aggregates results from 3 sources:
1. TrainingScores table (self-entered training)
2. PrecisionResultEntry table (competition entries)
3. Competition Result Documents (future - not yet implemented)

**See Also:** [Training Scoring System Documentation](Documentation/TRAINING_SCORING_SYSTEM.md) for complete implementation details

## Training Match System

### Overview
Real-time multiplayer training matches where members compete together with optional handicap system. Uses SignalR for live updates.

**Key Features:**
- Real-time scoreboard with SignalR
- Handicap system for fair competition across skill levels
- **Team-based competitions** (added 2026-01-24)
- Series-by-series score entry
- Match history and leaderboards
- Support for guests (non-registered participants)

**Data Storage:** Database tables `TrainingMatches`, `TrainingMatchParticipants`, `TrainingMatchScores`, `TrainingMatchTeams`
**Controller:** `TrainingMatchController.cs`
**UI:** `Views/Partials/TrainingMatchScoreboard.cshtml`

### Team Support (Added 2026-01-24)

Team matches allow shooters to compete in teams with combined scores.

**Database Schema:**
- `TrainingMatchTeams` - Team definitions (Id, TeamName, ClubId, TeamNumber)
- `TrainingMatches.IsTeamMatch` - Boolean flag
- `TrainingMatches.MaxShootersPerTeam` - Team size limit
- `TrainingMatchParticipants.TeamId` - Team assignment

**Team Types:**
- **Open Team Match**: Anyone can join; teams created dynamically
- **Closed Team Match**: Pre-defined teams; requires join approval

**Team Score Calculation:**
```csharp
TeamScore = participants
    .Where(p => p.TeamId == teamId)
    .Sum(p => p.AdjustedTotalScore);
```

**SignalR Events:**
- `TeamScoreUpdated` - Broadcasts when team scores change

**See Also:** [Training Match Team System Documentation](Documentation/TRAINING_MATCH_TEAM_SYSTEM.md) for complete details

### Handicap Calculation (Updated 2026-01-24)

**Per-Series Capping Rule:**
```
For each series: AdjustedSeries = clamp(RawScore + HandicapPerSeries, 0, 50)
FinalScore = Sum of all AdjustedSeries
```

**Key Points:**
- Handicap applied per series, not to total
- Each series clamped between 0-50
- Positive handicap capped at 50 (can't exceed perfect score)
- Negative handicap (elite shooters) clamped at 0
- Uses standard rounding (away from zero)

**Calculation Code:**
- **C# Server:** `ResultCalculator.CalculateAdjustedTotal<T>()` in `HpskSite.Shared/Services/ResultCalculator.cs`
- **JavaScript Client:** `calculateAdjustedTotalWithCap()` in `TrainingMatchScoreboard.cshtml`
- **API Leaderboard:** Inline calculation in `TrainingMatchController.cs`

**Example:**
- Scores: 49, 46, 48 with handicap +3.0
- Per-series: 49+3=52→50, 46+3=49, 48+3=51→50
- Final: 50 + 49 + 50 = 149 (not 143 + 9 = 152)

**See Also:** [Training Match Handicap System Documentation](Documentation/TRAINING_MATCH_HANDICAP_SYSTEM.md) for complete details

## Competition System

### Document Types
1. **competitionsHub** - Main listing page (/competitions)
2. **competitionSeries** - Series grouping (e.g., "2024 Season")
3. **competition** - Individual competition
4. **competitionType** - Competition formats
5. **registrationInvoicesHub** - Container for competition payment invoices (child of competition)
6. **registrationInvoice** - Individual payment invoice (child of registrationInvoicesHub)

### Content Hierarchy
```
Home
└── Competitions (competitionsHub)
    ├── 2024 Series (competitionSeries)
    │   ├── Spring Championship (competition)
    │   └── Summer Cup (competition)
    └── 2023 Series (archived)
```

### Competition Properties
- **isClubOnly** (Boolean) - If true, competition only visible to specific club
- **clubId** (Integer) - Links competition to specific club (for club-only competitions)
- **shootingClassIds** (Textstring) - JSON array of shooting class IDs (see Shooting Class Storage below)

### Shooting Class Storage System ✅ COMPLETE (2025-10-30)

**⚠️ CRITICAL:** Shooting classes MUST be stored as JSON arrays, not CSV strings

**Data Format:**
- **Correct:** `["C1","C2","A1"]` (JSON array string)
- **Wrong:** `C1,C2,A1` (CSV string - deprecated)

**Key Pattern (MUST USE):**
```csharp
// WRITING: Always serialize to JSON
var classIds = value.Split(',').Select(s => s.Trim()).ToArray();
value = System.Text.Json.JsonSerializer.Serialize(classIds);
competition.SetValue("shootingClassIds", value);

// READING: Always deserialize JSON with fallback to CSV
string[] classIdArray;
if (stringValue.TrimStart().StartsWith("[")) {
    classIdArray = JsonSerializer.Deserialize<string[]>(stringValue);
} else {
    classIdArray = stringValue.Split(',').Select(s => s.Trim()).ToArray();
}
```

**Documentation:**
- Technical Spec: `Documentation/SHOOTING_CLASS_STORAGE_SYSTEM.md`
- Test Plan: `Documentation/TEST_PLAN_SHOOTING_CLASSES.md`

### Series Admin System ✅ COMPLETE
**Location:** Admin Page → Series tab

**Features:**
- Create new series with name, descriptions, dates, menu visibility
- Edit series using **CKEditor 5** (open-source, no API key required)
- Copy series with user-specified dates (auto +1 year date advancement)
- Delete series (blocked if competitions exist)
- Rich text descriptions with HTML preservation

**API Endpoints** (CompetitionAdminController.cs):
- GET GetSeriesList
- POST CreateSeries, EditSeries, CopySeriesWithCompetitions, DeleteSeries

**Files:**
- Views/Partials/AdminSeriesList.cshtml
- Views/Partials/SeriesEditModal.cshtml (uses CKEditor 5)
- Views/Partials/SeriesCopyModal.cshtml
- Views/Partials/SeriesDeleteConfirmModal.cshtml

**Note on Rich Text Editor:**
- Migrated from TinyMCE (requires API key since 2024) to **CKEditor 5** (open-source)
- Preserves HTML content with data attributes
- Integration with Umbraco RTE storage format

### Competition Admin System ✅ COMPLETE
**Location:** Admin Page → Competitions tab (default)

**Features:**
- Create new competitions (CompetitionCreateModal.cshtml)
- Copy competitions with +1 year date advancement
- Delete competitions (blocked if registrations exist)
- Status auto-detection: Draft/Scheduled/Active/Completed

**API Endpoints** (CompetitionAdminController.cs):
- GET GetCompetitionsList - Returns all competitions (site admins) or filtered by managed clubs (club admins)
- POST CreateCompetition, CopyCompetition, DeleteCompetition - Require appropriate authorization

### Class Merging System ✅ COMPLETE (2026-03-31)
**Location:** CompetitionResultsManagement.cshtml → "Uppdatera" button → merge modal

**Overview:** When generating result lists, classes with < 5 participants can be merged with compatible classes per Swedish shooting sport rules. A modal shows merge suggestions; admin accepts/rejects each.

**Key Files:**
- `Services/ClassMergingService.cs` — rules engine, analysis, combined class naming
- `Controllers/CompetitionResultsController.cs` — `AnalyzeClassMerges` GET endpoint, merge logic in `CalculateFinalResults`
- `Views/Partials/CompetitionResultsManagement.cshtml` — modal UI + JS two-step flow

**Rules:** Class 1 never merges. A/B class 2+3 can merge. A_Opt, A_M, A_P, A_G follow the same level-2/3 rule within their own subgroup — never with each other and never with the open A class. C/L Dam→open class, Vet Ä→Vet Y→open (cascading), Jun→admin choice. R2+R3 for Milsnabb only. Never across weapon groups. MagnumPrecision/Springskytte excluded.

**Implementation:** Merging at GroupBy level (shooter's ShootingClass untouched). Merge config persisted as `mergeConfig` property on `competitionResult` document type (Textarea). Re-applied on preliminary result reload.

**Umbraco Setup:** `competitionResult` document type needs `mergeConfig` property (Textarea).

### Sub-competition (Deltävling) Result Lists ✅ COMPLETE (2026-05-17)
**Overview:** When a competition has `subCompetitionName` set, the admin Resultat tab renders a second result-list card for the Deltävling subset (shooters with `isSubCompetition=true` on their registration). The Deltävling list publishes independently from the main list and gets its own public **Visa resultat** button on the competition page.

**Key behaviour:**
- **Independent publish state**: `subCompetitionIsOfficial` on the `competitionResult` node, separate from main `isOfficial`.
- **Independent merge config**: `subCompetitionMergeConfig` (separate analysis over only the Deltävling subset's class counts).
- **Live recompute**: Deltävling results are always recomputed from the DB (no frozen snapshot). Main keeps the snapshot-on-publish behaviour.
- **Public URL**: `/resultat/?sub=true` renders the Deltävling subset. `Competition.cshtml` shows a second **Visa [subCompetitionName] resultat** button only when `subCompetitionIsOfficial=true`.
- **Admin embed**: Fältskytte's admin Deltävling card iframes `/resultat/?sub=true` so it looks identical to the main card.

**Key files:**
- `CompetitionTypes/Faltskytte/Controllers/FaltskytteController.cs` — `GetFaltskytteResults?subCompetitionOnly=true`, `AnalyzeFaltskytteMerges?subCompetitionOnly=true`, `SaveMergeConfig + PublishResults` with `IsSubCompetition` flag.
- `Controllers/CompetitionResultsController.cs` — `GetResultsList`, `AnalyzeClassMerges`, `CreateResultsList`, `ToggleResultsOfficial` all accept `subCompetitionOnly` / `IsSubCompetition`.
- `CompetitionTypes/Springskytte/Controllers/SpringskytteController.cs` — `GetSpringskytteResults?subCompetitionOnly=true`, `CalculateSpringskytteSubFinalResults` (publishes the Deltävling).
- `Views/CompetitionResult.cshtml` — branches on `?sub=true` query param; title + heading reflect `subCompetitionName`.
- `Views/Competition.cshtml` — second public button, gated on `hasResultPage && subCompetitionName != "" && subCompetitionIsOfficial`.

**Umbraco operator setup**: add two properties to `competitionResult` doctype — see "Document Type Properties — Required Additions" section. Without them, sub-comp publish + sub-comp merge silently fail.

### Särskjutning (Shoot-Off) for Championship Medal Positions ✅ (2026-05-19)
**Rule:** In Championship competitions (`competitionScope` ∈ {`Svenskt Mästerskap`, `Landsdelsmästerskap`, `Kretsmästerskap`, `Klubbmästerskap`}), tied medal positions 1–3 are resolved **only** by a 5-shot shoot-off. **None of the normal tie-breakers apply at medal positions** — not X-count, not series countback. Repeat rounds until separated. Ranks 4+ continue to use X-count + countback as before.

**Scope (this iteration):** Precision, Duell, Milsnabb, MagnumPrecision, NationellHelmatch (all five share the same code path through `CompetitionResultsController.CalculateFinalResults`). Fältskytte (station re-shoot semantics) and Springskytte (full re-run) are TODO.

**Implementation:**
- Single SQL table `CompetitionShootOffEntry` keyed `(CompetitionId, MemberId, ShootingClass, Round, SeriesNumber)` — identity-based so start-list / class regeneration can't orphan entries.
- `Services/ShootOffService.cs` — DB reads/writes plus static `DetectTiedMedalGroups()` and `ApplyShootOffOverride()`.
- `CompetitionTypes/Common/Utilities/CompetitionScopeHelper.IsChampionshipScope()` — the four-mästerskap recognizer. Other places using SM+Landsdel only (`FaltskytteStatsService.cs:151–152`, `FaltskytteController.cs:816–817`) are pre-existing bugs and should adopt this helper.
- `CalculateFinalResults` runs the existing sort, then on championship comps re-orders each tied medal-tier slice using shoot-off entries. Emits `classGroup.tiedMedalGroups[]` (admin payload) and `classGroup.shootOffNotes[]` (public footnote text).
- Admin UI: auto-loading "Särskjutning" card in `Views/Partials/CompetitionResultsManagement.cshtml`, after the Deltävling section. Hidden when no tied medal groups exist. Sub-comp mirror uses the same endpoint with `subCompetitionOnly=true`.
- Public UI: `Views/CompetitionResult.cshtml` JS appends `<span class="badge bg-info">SS: 49</span>` to the total cell when `shooter.shootOffScore != null`. Per-class footnote line under each table for resolved medal tiers.
- Endpoints: `GET GetShootOffStatus`, `POST SaveShootOffEntry`, `POST DeleteShootOffEntry` — all three-tier auth via the new `CanManageCompetitionResults` helper.

**Tests:** `HpskSite.Tests/Services/ShootOffServiceTests.cs` — 16 tests (scope helper, tie detection, single/multi-round resolution, rank-4 ignored, X-count divergence still tied, triple-tie).

**Manual operator steps:** Run `Migrations/create-competition-shootoffs-table.sql` in SSMS. No new doctype properties — all state lives in SQL.

### Standard Medals — A-family pooling rule ✅ (2026-05-17)
**Rule:** When standard medals are calculated, shooters in AM, AP, AG, and the open A class are pooled into a single "A family" ranking — percentage quotas (top 1/9 silver, top 1/3 bronze) are computed across the combined pool. Fixed-score thresholds (267/277 for 6 series, etc.) apply identically to every A-family subgroup. **A_Opt is NOT in the pool** — it's a parallel weapon group with its own ranking.

**Result-list display vs medal grouping are decoupled**: shooters appear in their original display class (AM2 stays AM2) but their medal eligibility is computed against the pooled A-family.

**Implementation:** `GroupByWeaponGroup` in each medal service folds A_M/A_P/A_G into the "A" bucket. Fixed-score tables include explicit `("A_M", n)`, `("A_P", n)`, `("A_G", n)` entries matching A's numbers. Applies to:
- `CompetitionTypes/Precision/Services/StandardMedalCalculationService.cs`
- `CompetitionTypes/Milsnabb/Services/MilsnabbStandardMedalService.cs`
- `CompetitionTypes/Faltskytte/Services/FaltskytteStandardMedalService.cs`
- `CompetitionTypes/NationellHelmatch/Services/NationellHelmatchStandardMedalService.cs`

Medal calculation is gated on `competition.isAwardingStandardMedals && !competition.isClubOnly` (BR-PS.1.3). When OFF, the medal service short-circuits and views drop the Std column from result tables (Fältskytte/Springskytte/Precision public renderers respect this flag).

Tests: `HpskSite.Tests/Services/StandardMedalCalculationServiceTests.AFamilyPooling.cs` covers pooling, fixed-score parity, and A_Opt isolation.

### AM/AP/AG Weapon Subgroups ✅ (2026-05-17)
**Optional weapon subgroups offered by some competitions** for dividing the A weapon group by pistol type:
- **AM**: Militära pistoler av äldre modell (m/07, m/40, P08)
- **AP**: Pistoler av fickmodell (Walther PP, PPK)
- **AG**: Moderna tjänstepistoler med fasta riktmedel (Glock 17, 19)

Each is a separate `WeaponClass` enum value (`A_M`, `A_P`, `A_G`) with 3 competence levels (AM1/AM2/AM3 etc.) following the same 1–3 ladder as A. Class IDs: `A_m_1`, `A_m_2`, …, `A_g_3`. Display names: `AM1`, `AP2`, etc.

**Scope**: Precision, Fältskytte, Milsnabb, NationellHelmatch. (Not MagnumPrecision, Duell, Springskytte.)

**Shooter declares competence (1/2/3) ONCE on `precisionShooterClass`** — same property covers A, A_Opt, AM, AP, AG. Handicap settings apply uniformly across the A-family.

**Per-competition opt-in**: the wizard/edit modal class checkboxes include the 9 new entries; competitions opt in by ticking them. Existing competitions are unaffected.

### Competition URLs & Routing ✅ (2026-05-22)

**Custom URL shapes** for competitions, replacing the default tree-derived `/competitions/{series-year}/{comp}/`. Rendered by `Routing/CompetitionUrlProvider.cs`, resolved back by `Routing/CompetitionUrlContentFinder.cs`, both registered in `Routing/CompetitionContentFinderComposer.cs`.

**Shapes** (priority order — first non-null wins inside `BuildCompetitionUrl`):
1. **SM** (`competitionScope == "Svenskt Mästerskap"`): `/competitions/{year}/sm/[{series}/]{comp}/`
2. **Landsdel** (`competitionScope == "Landsdelsmästerskap"`, with resolvable `regionalPage.area`): `/competitions/{year}/{ssm|vsm|osm|nsm}/[{series}/]{comp}/`
3. **Club-hosted** (`clubId > 0`): `/competitions/{year}/{region}/{club}/[{series}/]{comp}/` where region is the club's `regionalPage.UrlSegment`
4. **Region-hosted** (`regionalFederation` set, no club): `/competitions/{year}/{region}/[{series}/]{comp}/` where region is the `regionalPage.UrlSegment` matched by `regionCode`
5. Otherwise `null` → Umbraco default fallback

**Child nodes** (`precisionStartList`, `competitionResult`, `finalsStartList`) inherit the parent competition URL + own segment.

**Defensive scope read**: `ReadScopeValue` uses untyped `Value("competitionScope")` to avoid `FlexibleDropdownPropertyValueConverter` throwing on plain-string scope values stored from older codepaths. Same pattern as `Models/Competition.cs:31-62` `shootingClassIds`. **Don't use `Value<string>("competitionScope")`** — it crashes on legacy data.

**"At-least-one host" guard**: enforced in four places to keep competitions out of the null-URL state:
- `Views/Partials/CompetitionWizardModal.cshtml` — `submitWizard()` alerts + jumps back to Step 1
- `Views/Partials/CompetitionEditModal.cshtml` — `saveCompetition()` shows inline `#editFormErrors`
- `Controllers/CompetitionAdminController.cs` — `CreateCompetition` returns `{success:false,message:...}`
- `Controllers/CompetitionEditController.cs` — `SaveCompetition` (uses `ReadFieldOrContentAsInt/String` so a partial-update client can't bypass via field omission)

**`isClubOnly` requires a club**: the "Endast för vald klubb" checkbox auto-unchecks + disables when no club is selected (visibility filter `clubId == myClub` would hide the comp everywhere with `clubId=0`). `syncWizardClubOnlyAvailability` / `syncEditClubOnlyAvailability` run on every club/region change and once after the dropdowns finish loading. Cascades into the standard-medals BR-PS.1.3 interlock.

**Composer order pitfall**: `builder.UrlProviders().InsertBefore<DefaultUrlProvider, CompetitionUrlProvider>()` throws at startup — `DefaultUrlProvider` isn't in the collection yet at composer time. Use `Insert<CompetitionUrlProvider>()` (defaults to index 0 — runs first).

**Pretty URLs cover all three host states** (club / krets / national). The wizard's club-mismatch banner ("Du måste gå till den allmänna administrationssidan…") is now stale — RegionalAdminPanel got a Tävlingar sub-tab in 2026-05-28 mirroring the ClubAdminPanel one. Regional admins manage every competition in their region (both region-hosted via `regionalFederation` and club-hosted via `clubId` whose club lives in the region). The wizard / edit / advert-edit / springskytte-edit / delete-confirm / upload-file modals are included on `RegionalPage.cshtml` under `isRegionalAdmin`. `CompetitionAdminController.CopyCompetition` and `DeleteCompetition` now accept regional admins via the new `IsRegionalAdminForCompetition` helper; `CreateCompetition` already did via `GetManagedRegions()`. `GetCompetitionsList` already filtered comps to the regional admin's managed regions automatically.

### CompetitionController (Public + Admin endpoints)
**Location:** `Controllers/CompetitionController.cs`

**Key Endpoints:**
- GET GetCompetitionRegistrations - View registrations (Site Admin OR Competition Manager OR Club Admin)
- POST RegisterForCompetition - Register member for competition
- POST UnregisterFromCompetition - Remove registration

### Swish Payment System ✅ (2025-01-12)

**Overview:** QR code-based payment system for competition registrations using Swedish Swish mobile payments.

**Document Types:**

1. **registrationInvoicesHub** (Container - child of competition)
   - Purpose: Organizes all invoices for a competition
   - Properties: None (acts as container only)
   - Allowed Children: registrationInvoice

2. **registrationInvoice** (Individual invoice - child of registrationInvoicesHub)
   - Purpose: Tracks payment for member's competition registration(s)
   - Properties:
     - **competitionId** (Textstring) - Competition ID
     - **memberId** (Textstring) - Member ID
     - **memberName** (Textstring) - Member name for display
     - **totalAmount** (Decimal) - Total payment amount (e.g., 150.00)
     - **paymentMethod** (Textstring) - Payment method (default: "Swish")
     - **paymentStatus** (Textstring) - Status: "Pending", "Paid", "Failed", "Cancelled", "Refunded"
     - **paymentDate** (Date Picker) - Date when payment was completed
     - **transactionId** (Textstring) - Swish transaction ID
     - **invoiceNumber** (Textstring) - Unique invoice number (format: competitionId-memberId-sequence)
     - **relatedRegistrationIds** (Textarea) - JSON array of registration IDs (e.g., "[1234,1235]")
     - **createdDate** (Date Picker) - When invoice was created
     - **notes** (Textarea) - Admin notes about payment
     - **isActive** (True/False) - Whether invoice is active

**Key Components:**
- **SwishController** - Generates QR codes and manages payment initiation
- **PaymentService** - Creates invoices, tracks payment status, auto-creates invoice hub
- **SwishQrCodeGenerator** - Creates Swish-compatible QR codes

**Payment Flow:**
1. User registers for competition
2. User clicks "Betala med Swish" button in success modal
3. System auto-creates `registrationInvoicesHub` if it doesn't exist
4. System creates invoice with unique number under hub
5. QR code generated with Swish number + amount + invoice reference
6. User scans QR code → Swish app opens with pre-filled payment
7. Club admin verifies payment and marks as "Paid" in registration management

**Invoice Number Format:** `{competitionId}-{memberId}-{sequence}` (e.g., "1067-2043-1")

**Configuration:**
- Competition must have `swishNumber` property configured (10 digits starting with 0)
- At least one of `registrationFee`, `juniorRegistrationFee`, `subCompetitionFee` must be > 0
- Payment button only shows when these conditions are met

**Fee Types (all Textstring on `competition` doctype):**
- `registrationFee` — base fee, charged per selected class
- `juniorRegistrationFee` — optional. Replaces base fee per class for junior classes (IDs containing `_Jun`, or Springskytte age-class `jun`/`15`/`18`). 0 = fall back to base fee
- `subCompetitionFee` — optional extra for shooters who opt into the deltävling at registration
- `subCompetitionFeeMode` — `"perClass"` (default) or `"perRegistration"` — controls whether the deltävling fee multiplies by class count or is a flat one-off
- `teamRegistrationFee` / `stafettRegistrationFee` — flat per-team fees (unchanged; not affected by junior/deltävling modifiers)

**Fee Calculation — single source of truth:**
All fee math flows through `Services/RegistrationFeeCalculator.cs`. Never duplicate the branching inline in controllers. Call sites: `CompetitionController.RegisterForCompetition` (new + old fee for invoice cancellation), `SwishController.GeneratePaymentQR`, `SwishController.SendQRCodeEmail`. The Swish endpoint also returns `includesSubCompetition`, `subCompetitionName`, and `subCompetitionFeeTotal` so payment dialogs render a "Inkluderar X kr i deltävlingsavgift" breakdown.

**Frontend flag flow:** `competition-registration.js` submits `isSubCompetition=true|false` on every standard registration (the flag was previously only sent by the direktplacering path — easy to miss when adding registration surfaces).

**See Also:**
- [SWISH_PAYMENT_SETUP.md](Documentation/SWISH_PAYMENT_SETUP.md) - Complete setup guide
- [SWISH_PAYMENT_IMPLEMENTATION.md](Documentation/SWISH_PAYMENT_IMPLEMENTATION.md) - Implementation details
- [PAYMENT_INVOICE_SYSTEM.md](Documentation/PAYMENT_INVOICE_SYSTEM.md) - Fee calculation details

### Late Registration & Identity-Based Results ✅ (2025-11-23)

**Overview:** Competition results system refactored to support late registrations without data loss. Results are now stored by MemberId instead of position, allowing start lists to be regenerated without invalidating existing scores.

**The Problem:**
- **Before:** Results stored by `(CompetitionId, TeamNumber, Position, SeriesNumber)`
- **Issue:** Regenerating start list shuffled positions → all results became orphaned
- **Impact:** Late registrations impossible after results entry started

**The Solution: Identity-Based Results**
- **Now:** Results stored by `(CompetitionId, MemberId, SeriesNumber)`
- **Benefit:** Results follow the shooter, not their position
- **Impact:** Start lists can be regenerated safely, late registrations work seamlessly

**Key Changes:**

1. **Database Schema** (`PrecisionResultEntry` table):
   ```sql
   UNIQUE CONSTRAINT: (CompetitionId, MemberId, SeriesNumber)
   -- TeamNumber and Position are now INFORMATIONAL only
   ```

2. **Results Controller** (`CompetitionResultsController.cs`):
   - `SaveResultToDatabase`: Queries by MemberId instead of position
   - `DeleteResultFromDatabase`: Looks up MemberId, deletes by identity
   - Existing results preserved when start list regenerates

3. **Late Registration Endpoint** (`RegistrationAdminController.cs`):
   - **POST** `AddLateRegistration` - Creates registration after results entry has started
   - Validates member/competition, checks duplicates
   - Marks as "Admin (Late Registration)" for audit trail
   - Returns success with note about regenerating start list

**API Usage:**
```csharp
POST /umbraco/surface/RegistrationAdmin/AddLateRegistration
{
    "competitionId": 1067,
    "memberId": 2043,
    "shootingClass": "A1",
    "startPreference": "Early",  // Optional
    "notes": "Late registration due to traffic delay"
}
```

**Workflow:**
1. Admin creates late registration via API
2. Start list is regenerated (includes new shooter)
3. **All existing results are preserved** (tied to MemberId)
4. Results entry continues normally for all shooters

**Benefits:**
- ✅ Late registrations without data loss
- ✅ Start list regeneration safety
- ✅ More robust data integrity
- ✅ Results follow shooters through position changes

**Migration:** Database migration `precision-results-identity-based-v1` drops and recreates `PrecisionResultEntry` table with new schema (beta - existing data can be scraped).

**See Also:** [Late Registration Workflow Documentation](Documentation/LATE_REGISTRATION_WORKFLOW.md) for complete implementation details

### Cashier Workflow & Multi-Class Walk-In ✅ (2026-05-06)

**Overview:** End-to-end registration-desk experience for the cashier on competition day. Walk-in supports multi-class with per-class slot/patrol pickers, mark-as-paid records actual amount and emails a receipt, registrations can be re-pointed to a different shooter, and the start list updates automatically as walk-ins land.

**Manual operator steps (one-time, in Umbraco backoffice):**
- `registrationInvoice` doctype: add property `actualPaidAmount` (Decimal, optional, label "Faktiskt belopp"). Without it, the variance feature silently no-ops — billed and actual stay equal.
- `competitionRegistration` doctype: add property `isCheckedIn` (True/False, optional, default false, label "Incheckad"). Without it, the at-the-desk attendance toggle silently no-ops.

**Walk-in registration (`Anmäl och betala` modal):**
- Multi-class via checkbox list. Mutex buckets enforce one-class-per-subcategory like the public registration form's radio groups (C1 ↔ C2 ↔ C3, C_Vet_Y ↔ C_Vet_A, A_opt_1 ↔ A_opt_2, …). Different weapon groups stay independent so A1 + C1 + R1 is a legitimate three-class walk-in.
- Per-class slot pickers when direktplacering is enabled. Each ticked class gets its own dropdown listing teams + remaining capacity. Full slots disabled with `(FULLT)`.
- Patrol picker for Fältskytte / MagnumFält rolling-start. "Nästa lediga", "Skapa ny patrull", or specific patrol — applied per weapon group on submit, so A1 + B1 lands on a patrol per group.
- Non-DP slot picker (precision/MagnumPrecision/Milsnabb): when an existing `precisionStartList` exists for the competition, the modal probes `RegistrationAdmin/GetWalkInStartListTeams` and surfaces a dropdown. Picked team gets one row per registered class via `RegistrationAdmin/AssignWalkInToStartListTeam`.
- Backend: `LateRegistrationRequest.Classes` is the multi-class shape (list of `{Class, StartPreference?, TeamNumber?}`). Single-class fields kept for legacy callers. Capacity validation refuses over-booking before writing JSON.

**Mark-as-paid (`Markera som betald` modal):**
- Operator records actual amount (defaults to invoice total). Variance triggers an "Avvikande belopp" badge on the row and a "Faktiskt" column in Bokföringsunderlag.
- Receipt email is opt-out — checkbox defaults to checked. Receipt now fires inside `PaymentService.UpdatePaymentStatusAsync` whenever status transitions to Paid (also covers `InvoiceAdminController.MarkAsPaid`). Members without an email are silently skipped (no audit row, no error).
- Audit log records `MarkedPaid` and (separately) `ReceiptSent` events.

**Edit Registration (`Redigera anmälan` modal):**
- Multi-class checkbox list with the same mutex bucket logic as walk-in.
- For DP comps: per-class slot dropdowns. Existing classes pre-fill from the registration's stored `teamNumber`; newly-ticked classes start empty for explicit pick.
- The shared "Startpreferens" dropdown is gone — per-class StartPreference is preserved server-side via `existingByClass` lookup keyed on class id (case-insensitive trim).
- Fee re-compute follows the multi-invoice top-up model (`delta = newFee - sumPaid`; patches existing Pending or creates a new top-up Pending invoice; Paid invoices are never modified).
- Capacity check on edit excludes this registration's own existing assignments so a no-op save doesn't trip the guard.

**Transfer registration (`Överför till annan skytt`):**
- New endpoint `RegistrationAdmin/TransferRegistration(registrationId, toMemberId)` re-points the registration AND every linked invoice to the new member, then logs a `Transferred` audit row. Refuses if the target already has their own registration for the competition.

**Närvaro / Check-in toggle:**
- Bootstrap form-switch column on the Anmälningar table. Optimistic — flips local state immediately, reverts on server failure.
- New filter dropdown "Filtrera på närvaro": Alla / Incheckad / Ej incheckad.
- Endpoint `RegistrationAdmin/SetCheckedIn(registrationId, checkedIn)` with the standard four-tier auth.

**Auto-regen of start lists:**
- DP path: `DirektplaceringStartListService.Regenerate(competitionId, ...)` is called from both `AddLateRegistration` (when any teamNumber is present on the new registration) and `UpdateCompetitionRegistration` (when team assignments are involved). The service was extracted from `CompetitionController` so RegistrationAdmin doesn't duplicate the renderer.
- Non-DP path: walk-in's `AssignWalkInToStartListTeam` modifies `configurationData` and re-renders HTML via the existing `StartListHtmlRenderer`.
- Cache: invalidates `dp_availability_<competitionId>` runtime cache key so the next `/Competition/GetTeamAvailability` fetch sees the new state.

**Audit trail event types** (`Models/InvoicePaymentEvent.cs`):
- `Created`, `MarkedPaid`, `Cancelled`, `Refunded`, `EmailSent` (Swish QR email), `ReceiptSent` (payment receipt email), `Transferred`, `StatusChanged`.

**Multi-invoice top-up model:**
- One registration can have several invoices: original Paid + zero-or-more Paid top-ups + at most one Pending top-up. Paid invoices are never modified — they're the historical record. Top-up math is `delta = newFee - sumPaid`.
- API exposes per-row `paidAmount` and `pendingAmount` aggregates so the summary cards reflect totals correctly across multi-invoice rows. `hasVariance` is true when any paid invoice's `actualPaidAmount` differs from its `totalAmount`.

**Class mutex bucket logic (`getClassMutexBucket` in CompetitionRegistrationManagement.cshtml):**
- Maps a class id to a `weapon:subcategory` bucket. Within-bucket ticks auto-untick siblings; across-bucket ticks are independent.
- Subcategory derivation: `_Jun` → jun, `_Vet_` → vet, contains `Dam` → dam, else open. Springskytte composite ids (containing `-`) get a unique bucket per id so multi-class registration there is unaffected.

**See Also:** [Cashier Workflow Knowledge Base Doc](KnowledgeBase/docs/anmalningar-registreringsbord.md) for the user-facing version that the AI chat assistant uses.

## UI Implementation

### Navigation & Header
- **Logo**: `~/images/HpskLogo.jpg` - White header with clickable logo
- **User Menu**: Avatar with initials, dropdown (My Profile, Administration, Logout)
- **Admin Detection**: Checks for adminPage content type existence
- **Site Title/Subtitle**: Editable via Home page properties (siteTitle, siteSubtitle)
- **Bug Report Button**: "Rapportera Fel" - Opens modal for bug reporting with image upload

### Key Pages
- **/admin** - Admin dashboard with tabs (Competitions, Clubs, Users, Training)
- **/clubs** - Club directory for club admins
- **/skyttetrappan** - Training system interface
- **/login-register** - Login and registration page
- **/user-profile-page** - User profile with dashboard, training results

### Date & Time Pickers ✅ (Standardized 2025-11-21)

**CRITICAL: Always use Flatpickr for date/time inputs** - Never use HTML5 native date/time inputs (`<input type="date">`, `<input type="datetime-local">`, `<input type="time">`).

**Why Flatpickr?**
- Consistent Swedish localization (sv-SE) across all browsers
- Better UX with calendar popup
- Standardized date format (YYYY-MM-DD / HH:mm)
- Works identically on all platforms

**Standard Implementation Pattern:**

1. **Add CDN Links** (once per page/partial):
```html
<!-- Flatpickr Date/Time Picker -->
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/flatpickr/dist/flatpickr.min.css">
<script src="https://cdn.jsdelivr.net/npm/flatpickr"></script>
<script src="https://cdn.jsdelivr.net/npm/flatpickr/dist/l10n/sv.js"></script>
```

2. **Date Picker** (e.g., "Medlem sedan"):
```html
<!-- HTML -->
<input type="text" class="form-control" id="memberSince" name="memberSince">

<!-- JavaScript -->
<script>
flatpickr('#memberSince', {
    locale: 'sv',
    dateFormat: 'Y-m-d'
});
</script>
```

3. **DateTime Picker** (e.g., "Event Date"):
```html
<!-- HTML -->
<input type="text" class="form-control" id="eventDate" name="eventDate">

<!-- JavaScript -->
<script>
flatpickr('#eventDate', {
    locale: 'sv',
    enableTime: true,
    time_24hr: true,
    dateFormat: 'Y-m-d H:i'
});
</script>
```

4. **Time-Only Picker** (e.g., "Start Time"):
```html
<!-- HTML -->
<input type="text" class="form-control" id="startTime" name="startTime">

<!-- JavaScript -->
<script>
flatpickr('#startTime', {
    locale: 'sv',
    enableTime: true,
    noCalendar: true,
    dateFormat: 'H:i',
    time_24hr: true
});
</script>
```

**Common Options:**
- `maxDate: 'today'` - Prevent future dates
- `minDate: 'today'` - Prevent past dates
- `defaultDate: 'today'` - Set initial value to today
- `defaultHour: 9, defaultMinute: 0` - Set default time

**Standardized Files (2025-11-21):**
- ✅ ClubAdminPanel.cshtml - Event date (datetime), Member since (date)
- ✅ UserManagement.cshtml - Member since (date)
- ✅ TrainingScoreEntry.cshtml - Training date (date with maxDate: today)
- ✅ CompetitionStartListManagement.cshtml - First start time (time-only)

**Date Display Formatting (Server-Side):**
Always use Swedish culture for date display in views:
```csharp
@using System.Globalization;

// Full date: "måndag, 5 oktober 2025"
@someDate.ToString("dddd, d MMMM yyyy", CultureInfo.GetCultureInfo("sv-SE"))

// Short date: "5 okt 2025"
@someDate.ToString("d MMM yyyy", CultureInfo.GetCultureInfo("sv-SE"))
```

**Date Display Formatting (Client-Side):**
```javascript
// Swedish date string
const dateStr = date.toLocaleDateString('sv-SE', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit'
});
```

### Form Styling Guidelines

**Placeholder Text:**
Always use subtle placeholder text to avoid cluttered forms:
```css
/* Subtle placeholder text - use in all forms */
.form-control::placeholder {
    color: var(--bs-secondary-color);
    opacity: 0.5;
}
```

**Helper/Hint Text:**
Keep form helper text subtle:
```css
.form-text {
    opacity: 0.7;
    font-size: 0.8rem;
}
```

**Dark Mode Compatibility:**
- Always use `var(--bs-secondary-color)` for muted text colors
- Use `var(--bs-tertiary-bg)` for subtle backgrounds
- Use `var(--bs-border-color)` for borders
- Never use hardcoded colors like `#6c757d` or `#f8f9fa`

## Required Umbraco Backoffice Setup

### Document Types to Create
1. **adminPage** - Admin dashboard
2. **userProfile** - User profile page
3. **clubsPage** - Club listing hub
4. **club** - Individual club (with clubSimpleEvent as allowed child)
5. **clubSimpleEvent** - Club events
6. **trainingStairs** - Training system page
7. **competitionsHub** - Competition listing page
8. **competitionSeason** - Optional season grouping
9. **competition** - Individual competition
10. **competitionType** - Competition format
11. **registrationInvoicesHub** - Payment invoice container (child of competition)
12. **registrationInvoice** - Individual payment invoice (see Swish Payment System section for properties)
13. **faltskytteConfigurationHub** - Hub for standalone Fältskytte configurations (no properties; allow under Home). Published as URL alias `faltkonfig`. Without this content node the editor route returns 500.
14. **resultBoard** - Standalone live results board (no properties; allow under Home; default template `ResultBoard`). Published as URL alias `live` → `/live?c=<competitionId>`. Without this content node the board triggers on the competition page dead-link (404).

### Content Pages to Create
Create content nodes using above document types and publish them under Home page.

### Member Groups to Create
Navigate to **Members → Member Groups**:
- Administrators (for site admins)
- Users (default group for all members)
- PendingApproval (for members awaiting approval)
- ClubAdmin_XXXX groups (created automatically by system for each club)
- Skjutledare_XXXX groups (created automatically when assigning Skjutledare via club admin panel)
- RegionalAdmin_XXXX groups (created automatically for regional admins)
- Foreningsinstruktor_XXXX, Kretsinstruktor_XXXX, Riksinstruktor_XXXX (created automatically by `CertificationService` on first appointment per scope)
- Vapenkontrollant, Banlaggare (single global groups, created automatically on first cert grant)

### Document Type Properties — Required Additions
- **regionalPage**: add `area` Textstring property (dropdown values: `Syd`, `Vast`, `Ost`, `Nord`). Required by the Certifications system to scope Riksinstruktör authority. Backfill on every existing region node.
- **registrationInvoice**: add `actualPaidAmount` Decimal property (optional, label "Faktiskt belopp"). Cashier flow records what was actually collected when it differs from the billed total. Without this property, the variance feature silently no-ops (billed = actual). Added 2026-05-06.
- **competitionRegistration**: add `isCheckedIn` True/False property (optional, default false, label "Incheckad"). Powers the at-the-desk attendance toggle on the Anmälningar table. Without this property, the toggle silently no-ops (`IContent.SetValue` on a missing property is a no-op). Added 2026-05-06.
- **competition**: add `faltskytteSelfServiceResults` True/False property (optional, default false, label "Tillåt självservice (skyttar fyller i resultat)"). When ON, logged-in shooters in a patrol can enter results for that patrol on `/station?c=X&s=N`; staff retains full edit. Without this property the wizard checkbox silently no-ops. Added 2026-05-09. Also requires running `Migrations/add-currentstation-to-faltskyttepatrol.sql` in SSMS to add the per-patrol cursor column.
- **competition**: add `faltskytteStationManagers` Textarea property (optional, label "Stationschefer (JSON)"). Stores per-station chief assignments — JSON keyed by station number → `{ memberId?, name, phone }` — set on the **Stationer** tab. Without it, `SetValue` is a silent no-op and station chiefs won't persist. Added 2026-05-27.
- **competitionResult**: add `subCompetitionIsOfficial` True/False property (optional, default false, label "Deltävling publicerad som officiell"). Powers the independent Deltävling publish toggle. Without it, the Publicera button on the Deltävling section returns an error and the second public "Visa resultat" button cannot appear. Added 2026-05-17.
- **competitionResult**: add `subCompetitionMergeConfig` Textarea property (optional, label "Deltävling – sammanslagningskonfiguration (JSON)"). Stores the Deltävling's own class-merge config — separate from the main `mergeConfig` so the subset analyses its own <5-shooter classes. Without it, sub-comp Sammanslagning silently no-ops. Added 2026-05-17.
- **competitionResult**: add `classNameOverrides` Textarea property (optional, label "Anpassade klassnamn (JSON-dict)"). Stores admin-edited display names for class groups — JSON dict mapping auto-generated combined name (e.g. "C2+Dam+Vet") to custom name (e.g. "C2 Allmänt"). Empty value = no overrides. Without this property, the pen-icon rename feature on the result page silently no-ops. Added 2026-05-19.
- **competitionResult**: add `subCompetitionClassNameOverrides` Textarea property (optional, label "Deltävling – anpassade klassnamn (JSON-dict)"). Same shape as `classNameOverrides` but applied only to the Deltävling result list (`?sub=true`). Without it, sub-comp class-name overrides silently no-op. Added 2026-05-19.
- **competitionResult**: add `faltskytteShootOffConfig` Textarea property (optional, label "Fältskytte – särskjutnings-station (JSON)"). Stores a single station config (with per-weapon-class variants) used for Fältskytte/MagnumFält Särskjutning. Without it, the "Konfigurera särskjutnings-station" save silently no-ops. Added 2026-05-20.
- **competitionResult**: add `subCompetitionFaltskytteShootOffConfig` Textarea property (optional, label "Deltävling – Fältskytte särskjutnings-station (JSON)"). Same as above for the Deltävling pool. Added 2026-05-20.
- **precisionStartList**: add `qualifyingResultsSnapshot` Textarea property (optional, label "Kvalresultat — frusna klasser (JSON)"). Stores the per-championship-class snapshot dict — each frozen class carries its own `FrozenAt`, `FrozenBy`, `ChecksumAtFreeze`, and ranked `QualifiedShooters` list inside the same blob. Without this property, freezing silently no-ops. Added 2026-05-21.
- **finalsStartList**: add `perClassConfigData` Textarea property (optional, label "Per-klass-konfiguration (JSON)"). Stores the admin's per-championship-class skjutlag-assignment + cut overrides so they survive regeneration. Added 2026-05-21.
- **finalsStartList**: add `startListContent` Textarea property (optional, label "Cachad HTML"). Mirrors the property on `precisionStartList`. Added 2026-05-21.
- **competition**: add `resultListFile` Media Picker property (optional, label "Resultatlista"). Mirrors `invitationFile` — stores a PDF/Word result list uploaded for external competitions. Without it, the "Ladda upp resultatlista" button silently no-ops and the public "Resultatlista" card never renders. Added 2026-05-28.
- **club**: add `markenSignoffSkjutledare` True/False property (optional, default false, label "Tillåt skjutledare att signera märken"). Powers per-club sign-off authority for Märken (Pistolskyttemärket). OFF (default) → only board members (Styrelse, via `BoardRoles`) + site admins sign off Guldfodringar/märken; ON → Skjutledare of the club may too. Missing property = silent no-op (safe default = board only). Added 2026-05-31. Also run `Migrations/create-marken-tables.sql` in SSMS.
- **competition**: add `rangeId` Integer property (optional, default 0, label "Skjutbana (id)"). Links a competition to a shooting range in the Skjutbanedatabas → the public competition page shows venue + map + Vägbeskrivning (members-only block) and the management page gets a range-picker. Missing property = graceful no-op (picker shows "lägg till egenskapen", public block hidden). Added 2026-06-03. Also run `Migrations/create-range-tables.sql` in SSMS + create the `shootingRangeHub` node (alias `skjutbanor`). See `Documentation/SHOOTING_RANGE_DATABASE.md`.

### Märken (Pistolskyttemärket) ✅ Phase 1 (2026-05-31)
**What:** Marksmanship proficiency badges (SHB kap 5), distinct from competition Standardmedaljer. Phase 1 = **Pistolskyttemärket**: base valörer (Brons/Silver/Guld, Guld carries a national registration number), the yearly **Guldfodringar** (two-part upholding), and the derived **årtalsmärke ladder** (17 named steps). Closes the backlog item "create/verify/report yearly Guldfodringar; permit type of members to verify". Full spec: `Documentation/MARKEN_SYSTEM.md`.

**Validated evidence model (revised 2026-05-31 — NOT TrainingScores):** self-entered training is NOT a valid basis. A member submits validated **series** via two big buttons on the tab:
- **Guldserie** (`SeriesType=Precision`) — a 5-shot series entered shot-by-shot (precision keypad, mirrors `TrainingScoreEntry`).
- **Snabbserie** (`SeriesType=Speed`) — a tillämpningsserie declared by target (`B100_50m`/`C30_25m`) + claimed valör (no shot-by-shot; hits-in-time pass/fail).

Both land **Pending** in the chosen club's **validation queue** and are verified by a functionary in-app or via **QR**. One generalized table `MarkenSeries` holds both (future higher badges reuse it via `ClaimedLevel`).

**Candidate engine** (`Services/MarkenCandidateService.cs`, read-only): Part 1 = ≥3 qualifying Guld series this year = **Verified Guldserier + qualifying precision series from hosted pistol.nu competitions** (read live from `PrecisionResultEntry`, competition year/name resolved via `IUmbracoContextAccessor`, series total ≥ age-adjusted Guld threshold). Part 2 = **3 Verified Guld Snabbserier** OR a held Standardmedalj i fält (SHB 5.1.1.1 pt 2 = 3 tillämpningsserier). Age parsed from `personNumber` (−1/serie at 56+, Silver-krav at 66+ per SHB 5.1.2.2).

**QR verify flow:** the entry modal shows a QR (`/marken/verifiera?t=<IDataProtector token>`, reuses `Faltskytte/GenerateQrCode` for the PNG). A board member/Skjutledare scans → `Views/MarkenVerify.cshtml` (chromeless, routed via `MarkenVerifyController` — NO Umbraco node) → loads detail via `GetSerieForVerify` + Godkänn/Avvisa. **Multi-club edge case:** the entry modal has a club picker (member's primary + additional clubs); the chosen `ClubId` scopes both the queue and the QR-verify authority. Photos optional, stored via `StandardMedalProofStorage` (App_Data), streamed by `GetSeriePhoto`.

**Sign-off / validation authority (per-club, club-scoped):** site admins always; **board members** (Styrelse, via `BoardRoleService.GetBoardMembers`/`GetClubIdsWhereBoardMember`) of the relevant club always; **Skjutledare** only when that club's `markenSignoffSkjutledare` is on. Series validation scopes to the series' chosen `ClubId`; Guldfodring sign-off scopes to the member's primary club. Logic in `MarkenController.CanSignOffForClubAsync` / `GetMarkenSignoffScopeAsync`. Viewing the secretary tab uses the broader club-admin gate.

**Data model:** SQL tables `MemberBadge` (awarded ledger, holds Guld `UniqueNumber`) + `MemberBadgeQualification` (yearly two-part Guldfodring; årtalsmärke level = pure fn of fulfilled+verified count) + `MarkenSeries` (validated Guldserie/Snabbserie submissions + queue). `Services/MarkenLedgerService.cs` (badge/qual/series CRUD + `GetArtalsmarkeStatusAsync`). Constants/ladder/age-parser/targets in `Models/Marken.cs`. Services scoped in `AdminServicesComposer`.

**Flow (revised — TWO steps, no manual yearly sign-off):** (1) shooter submits a Guldserie/Snabbserie; (2) a functionary validates each series (queue or QR). The year's Guldfodring **auto-completes** when Part 1 (3 validated qualifying Guld series) + Part 2 (3 validated Guld Snabbserier OR a held fält-standardmedalj) are both met — `MarkenController.RecomputeYearlyQualificationAsync` runs after every series Verify/Reject and lazily on read, upserting/downgrading the `MemberBadgeQualification` row (which the årtalsmärke ladder counts). There is **no** "Signera guldfodring" button (removed `SignOffGuldfodring`/`SignOffQualification`/`RejectQualification`).

**One labelled validation queue, two surfaces:** "Serier att validera" with full context (shooter, shots, total-vs-krav, photo) + text **Godkänn/Avvisa** buttons — on the **club admin Märken tab** (`GetClubPendingSeries?clubId=`, club admins view; buttons active only if `canValidate`) AND on the **Min sida Medaljer & Märken tab** for functionaries who aren't club admins (`GetPendingSeries`, scoped to their board/Skjutledare clubs). QR (`/marken/verifiera`) is the fast on-the-spot path to the same Verify/Reject.

**UI:** dedicated **"Medaljer & Märken" tab** in `UserProfile.cshtml` (`loadMarkenTab`) — two entry buttons + keypad/snabbserie modals (optional camera photo + QR-after-save), "Mina inskickade serier", the functionary queue, the Pistolskyttemärket card (read-only Guldfodring status), and the Standardmedaljer card. Club admin "Märken" tab in `ClubAdminPanel.cshtml` — validation queue + members-with-activity table (year filter + MAP CSV) + per-member detail (read-only Guldfodring status; manual **award B/S/G + Guld number** stay). Neutral printable `Märkesutskrift`. **No firearms-licence framing** (user dropped it).

**Skyttetrappan → valör link (2026-05-31):** completing all steps of Nybörjartrappa Brons/Silver/Guld (Skyttetrappan levels 1/2/3) auto-materializes the matching Pistolskyttemärket base valör (Source=`Skyttetrappan`, Verified, stamped with the approving functionary + real completion date from `completedTrainingSteps`). Real-time hook in `TrainingController.CompleteStep` (captures approver id); idempotent lazy backfill on read in `MarkenController.BuildMemberPayloadAsync` → `MarkenLedgerService.SyncTrappaBadgesAsync` (historical completions materialize on first card/detail view, approver from the stored InstructorName). Members can still be awarded valörer manually in the secretary tab; trappa-sourced ones show a "Skyttetrappan" badge there.

**Operator steps:** run `Migrations/create-marken-tables.sql` AND `Migrations/create-marken-series-table.sql` in SSMS; add the `club.markenSignoffSkjutledare` property. No Umbraco node needed for `/marken/verifiera` (routed controller). Full rebuild (C#).

**Phase-1 limits (intentional):** R weapon group → C thresholds (verify w/ SPSF). Base valörer come from Skyttetrappan/manual award (a Guldserie feeds the yearly Guldfodring, not the base grundmärke).

### Märken Phase 2 ✅ (2026-06-01) — discipline + series-proof families
Generalized, data-driven family framework (`Models/MarkenFamilies.cs` — all thresholds/ladders/prereqs from SHB kap 5, Fält verified from `Documentation/FaltskytteMarketTables.png`). Two patterns:
- **Competition-achievement** (Precision/Fält/Milsnabb/Nationell helmatch): earned at 3 comps/year (2 for NatHelmatch) meeting point/hit thresholds. `Services/MarkenCompetitionService.cs` harvests the member's **hosted** results live per discipline (precision-shape = sum series totals; Fält = sum per-station hits; comp year/name via `IUmbracoContextAccessor`) + merges **verified self-reported** external results (`MarkenCompetitionResult` table + `create-marken-competition-result-table.sql`). Auto-awards the earned valör (`MemberBadge`, Source=Auto) + family årtalsmärke years (first guld-year earns; later = ånyo). Progression is lenient (highest supported level), documented. **Scope rule (SHB):** Fält/Precision/Milsnabb only count hosted comps at **krets level or above** (`competitionScope` ∈ Kretsmästerskap/Landsdelsmästerskap/Svenskt Mästerskap — read untyped, FlexibleDropdown); club comps + scope "Ingen" must be self-reported (functionary confirms level). NatHelmatch (`RequiresKretsScope=false`) counts any level + träning. Known limit: a krets-level *open* comp on pistol.nu marked "Ingen" won't auto-count — self-report covers it (krets+ comps are often not on pistol.nu anyway).
- **Series-proof** (Luftpistol +5-series, Elit 5 precision + 5 snabb; needs guldmärke): reuse `MarkenSeries` via `SubmitProofSeries` (total → highest valör); `RecomputeSeriesProofFamiliesAsync` auto-awards. Luftpistol årtalsmärke = 1 step/year, others 3.

**Unified validation:** the queue + QR verify now handle both evidence kinds — `GetPendingSeries`/`GetClubPendingSeries` return mixed `items` with a `kind`; `VerifyEvidence`/`RejectEvidence {kind,id}`; QR token `"series:id"`/`"comp:id"`. `GetArtalsmarkeStatusAsync` + `MarkenFamilies.Artalsmarke` are family-aware. Member tab shows all families (`renderFamilies`) with entry modals for comp results + series-proof. **Deferred to Phase 3:** Mästar (5.2), Stormästar (5.3), Springskytte (5.6).

**Deploy (Phase 2):** full rebuild + run `Migrations/create-marken-competition-result-table.sql` in SSMS (the only new table; auto-award writes the existing MemberBadge/Qualification tables; series-proof reuses MarkenSeries). **Compiles green; not yet load-tested — verify the hosted-result harvest against real comp data first (weapon-group parse, series/station counts).**

### Märken Phase 3 (partial) ✅ (2026-06-01) — Mästarmärket (Route 1) + Stormästarmärket
Bespoke (not MarkenFamilies patterns). Springskytte still deferred.
- **Mästarmärket (5.2), Route 1 only** (`Marken.FamilyMastar`): year-count → valör. A **qualifying year** = standardmedalj i SILVER in BOTH fält and precision the same year — auto-derived from `StandardMedalLedgerService.GetAwardsForMemberAsync` (MedalType=Silver, Discipline `Faltskytte`+`Precision`). Brons/Silver/Guld at 3/6/9 qualifying years; guld ★/★★/★★★ at 14/19/24. **Route 2 (kompetensprov) is NOT auto-evaluated** — surfaced as a note (`Marken.MastarRoute2Note`, "se SHB kap 5.2"). Qualifying years stored as `MemberBadgeQualification(family=Mastar)`; a **functionary can add/remove historical years** in the secretary per-member detail (`SetMastarQualifyingYear` / `GetMemberMastar`) — for medals earned before the system. `RecomputeMastarAsync` runs lazily on read; lenient/add-only (no auto-downgrade). Calculators (`MastarLevel`/`MastarGuldStars`/`MastarLevelDisplay`/`MastarYearsToNext`) in `Models/Marken.cs`. **No new table** (reuses MemberBadge/MemberBadgeQualification + reads the StandardMedal ledger).
- **Stormästarmärket (5.3)** (`Marken.FamilyStormastar`): career **inteckningspoäng**; 30 p → eligible (then the club nominates to SPSF with a meritförteckning — manual, no auto-award). `Marken.StormastarPoints(scope, participants, place)` bakes in **Tabell 2** (1972→): `_stormastarTable` keyed Krets/Landsdel/Svenskt × 6 deltagar-band; each string's leftmost digit = points for place 1. Assumptions: KM 201+ carried from 151–200 (SHB left blank); **pre-1972 Tabell 1 + Rikstävling points NOT modelled** (don't apply to current shooters). New table `MarkenStormastarEntry` + `Migrations/create-marken-stormastar-table.sql` + `Services/MarkenStormastarService.cs` (graceful try/catch reads; registered in `AdminServicesComposer`). Member self-enters each comp (scope/deltagare/placering + optional gren/namn/foto) via `#stormastarModal` with a **live point preview** (JS mirror `SM_TABLE`/`smPoints`); lands Pending; a functionary validates. Only Verified rows sum toward 30.
- **Unified queue/QR extended to `kind="stormastar"`**: `GetPendingSeries`/`GetClubPendingSeries` concat `MarkenStormastarService.GetPendingAsync`; `VerifyEvidence`/`RejectEvidence` switch on kind; QR token `"stormastar:id"` in `GetSerieForVerify`; `markenEvidenceDesc`/`markenEvidenceDescAdmin` + queue photo (`GetStormastarPhoto`) handle it; live-update via `GetMyStormastarStatus`.
- **UI**: two new sections on the Min sida "Medaljer & Märken" tab (`renderMastar` → `#markenMastarContent`, `renderStormastar` → `#markenStormastarContent`, fed by extra `mastar`/`stormastar` keys on `GetMyMarken`). Badge images expected at `/images/marken/Mastar.png` + `Stormastar.png` (graceful onerror-hide via `markenBadgeImg`).
- **Deploy (Phase 3):** full rebuild + run `Migrations/create-marken-stormastar-table.sql` in SSMS. Compiles green; **not yet load-tested** (Razor views runtime-compiled — load the tab once after deploy).

### Märken backlog entry — historical Guldserier/Snabbserier from a paper ledger (2026-06-04)
Club admins migrate a hand-written ledger of past series in bulk on the club **Märken** tab. Functionary-only card **"Historiska serier från klubbliggare"** (gated on `CanSignOffForClubAsync` — board / Skjutledare-if-enabled / site admin; hidden for plain club admins, wired off `loadClubMarkenQueue`'s `canValidate`). An add-rows grid: per row member (from `ClubAdmin/GetClubMembers`, defaults to the previous row's member), type (Guldserie=`Precision` / Snabbserie=`Speed`), date, weapon group, and score (precision/snabbpistol) or target+valör (tillämpning). "Spara alla" → `POST Marken/AddBacklogSeries` (`AddBacklogSeriesRequest { ClubId, Entries[] }`).
- Rows insert **directly `Verified`** (the entering functionary is the validator — no queue), `Shots="[]"` (total only), `Notes="Historisk inmatning från klubbliggare"`, `EnteredByMemberId`=`ValidatedByMemberId`=acting functionary. Precision score validated against the age-adjusted `Marken.PrecisionThreshold`; sub-threshold saved but `Qualifies=false`. Speed mirrors `SubmitSeries` (snabbpistol scored 0–50 → valör; tillämpning = valör pass/fail).
- Per-row server validation (member ∈ club via `MemberBelongsToClub`, date not future, valid weapon group, 0–50); bad rows skipped + reported, good rows still save.
- After insert: `RecomputeYearlyQualificationAsync` per affected (member, year) + `RecomputeSeriesProofFamiliesAsync` per member, so Guldfodringar/årtalsmärken complete from the migrated data.
- **No schema change** — reuses `MarkenSeries`. Self-validation is intentionally allowed here (one-time migration of an official ledger). Deploy: rebuild + restart; no migration. Files: `Controllers/MarkenController.cs` (`AddBacklogSeries` + DTOs), `Views/Partials/ClubAdminPanel.cshtml` (card + grid JS).
- **Series now visible in the club summary + a year-report export (2026-06-04):** `GetClubMarkenSummary` previously listed only `MemberBadge`/`MemberBadgeQualification` holders (`GetAllActiveMemberIdsAsync`), so members with verified series but no completed Guldfodring only showed in the Guldserie-ligan. It now ALSO includes members with verified series at the club for the year (via `GetVerifiedSeriesForClubAsync`) and carries `qualifyingSeries`/`speedSeries` counts → the "Årets status" cell shows progress ("2/3 guldserier · 1/3 snabb"). `ExportClubMarken` was reworked from an all-holders snapshot into a **year-achievements report**. Reminder: Guldfodring Part 2 = **3** Guld snabbserier (`GuldfodringSpeedSeriesRequired`) or a held fält-standardmedalj — not one.
- **Multi-family extension (2026-06-04):** the admin Märken view was Pistolskytte-only. Now: (1) **`GetMemberMarkenDetail`** also returns `families` (`BuildFamilySummariesAsync` — Elit/Fält/Precision/Milsnabb/NatHelmatch/Luftpistol), `mastar`, `stormastar` (recomputes comp + series-proof families first, mirroring `GetMyMarken`); the Detaljer modal renders an **"Andra märken"** section (`renderMarkenFamiliesBlock` in ClubAdminPanel.cshtml) — Mästarmärket already had its own block. (2) **`ExportClubMarken`** is now a **multi-family** year report: one row per (member, family) that earned a badge/valör or (Pistolskytte) fulfilled a Guldfodring that year, with a `Familj` column. Uses `GetBadgesForMemberAsync(mid, null)` (all families) + a `FamilyLabel` switch (Pistolskytte/Mästar/Stormästar hardcoded, rest via `MarkenFamilies.DisplayName`). The **summary table stays Pistolskytte-focused** (keystone); other families surface in Detaljer + export only. NB: validation of all families already worked via the shared queue; backlog/verified series feed Elit via `RecomputeSeriesProofFamiliesAsync` (Elit snabb needs Snabbpistoltavla series + a held Guldmärke).
- **Elit progress display (2026-06-04):** Elit (`SeriesThreshold {45,48,49}`, `SeriesRequired 5`, `RequiresSpeedSeriesToo`, prereq Guld) earns when `min(precision≥thr, snabb≥thr) ≥ 5` **in one year**. The old status showed that single `min` (e.g. "0/5") which hid that precision was accumulating — confusing when a member has precision Guldserier but no snabbpistol series yet. `BuildFamilySummariesAsync` now shows **both halves** for `RequiresSpeedSeriesToo` families: "För brons (≥45 p/serie): precision 3/5 · snabb 0/5 (snabbpistoltavla), i år." Precision Guldserier DO count toward Elit (read by discipline via `GetAllVerifiedSeriesAsync`), but Elit needs 5 of EACH half + the Guld grundmärke.
- **Strict valör progression (SHB 5.4.2 et al., 2026-06-04):** the award engine used to grant the **highest** qualified valör in one go (a documented Phase-2 simplification). SHB requires **one valör per year, sequential** — "Endast ett märke kan under året erövras … och märke av högre grad endast av den som förut innehar märke av närmast lägre grad" (appears in 5 chapter sections: Elit + the discipline/Luftpistol families). Now enforced via `Marken.ApplyValorProgression(perYear→(year,qualifiedOrdinal))` → `(held, heldYear, guldYears)`, walking years chronologically, stepping one grade per qualifying year. Wired into BOTH `MarkenCompetitionService.AnalyzeAsync` (added `EarnedYear`) and `MarkenController.AnalyzeSeriesProofAsync` (tuple gained `EarnedYear`); recompute methods stamp the badge with `EarnedYear`. So a member who shoots all-Guld series in one year earns **Brons** that year (Guld takes ≥3 qualifying years). **Forward-only by design:** `EnsureBadgeAsync` is insert-the-missing-level-only (never downgrades), so existing leniently-awarded Guld badges in prod are preserved — strict progression only governs new/future awards. **Testing caveat:** a member already auto-awarded Guld under the old rule keeps Guld (and recompute just adds a Brons row dated their series year) — verify the strict behavior with a FRESH member, not a previously-awarded one.
- **Elit timing gate + editable Guldmärke year (2026-06-04):** now enforced — SHB 5.4.2 "Prov för elitmärke får avläggas första gången året efter det guldmärket erövrats." `AnalyzeSeriesProofAsync` for Elit requires a held Pistolskyttemärket Guld (no Guld → no Elit, also fixes prereq-not-enforced-on-award) and only counts series with `Year >= guldBadge.AchievedYear + 1`. Because that depends on an accurate Guld year, the **Guldmärke year is now captured at award and editable afterward**: `AwardBadge` already accepted `Year` (JS now sends it, prompting at Guld award, default current year); the Detaljer Guld row gained an **År** input next to the Nr input; `SetBadgeUniqueNumber` (+ `UniqueNumberRequest.Year`) now also sets `AchievedYear`. The Elit family summary shows a note ("Elitprov får avläggas först {guldYear+1} …") when the member holds Guld but the viewed year isn't past it. Luftpistol's brons prereq stays advisory (only Elit's gate is hard, since the timing rule needs the Guld year).
- **Remove an auto-awarded family badge (2026-06-04):** awarded badges are persisted `MemberBadge` rows; the engine is add-only, so deleting the source series does NOT retract a badge (it just stops re-deriving). New `POST Marken/DeleteFamilyBadges {memberId, family}` (auth `CanSignOffForMemberAsync`) deletes all of a member's `MemberBadge` + `MemberBadgeQualification` rows for a family; wired to a **"Ta bort" trash button** on each row in the Detaljer "Andra märken" section (`deleteMarkenFamily` JS, functionary-only). Caveat surfaced in the confirm dialog: derived families re-materialize on next read if the underlying evidence still qualifies — remove the series/results first for a lasting removal. (Prior to this there was no UI for the long-existing `DeleteBadge` endpoint — removal required editing the `MemberBadge` table directly.)

### Märken: "Spara som Guldserie/Snabbserie" from Resultat-entry + Training match (2026-06-10)
A shooter can turn a single just-shot **5-shot series** into a Märken submission from two more places besides the "Jag har skjutit en Guldserie" button on Min sida: the manual **Resultat-entry** modal (`TrainingScoreEntry.cshtml`) and a **Training match** (`TrainingMatchScoreEntry.cshtml`). Per-series, shot-by-shot only — NOT offered in serie-total / total-only entry (no per-shot data).
- **Discipline → series routing (the crux):** only two disciplines map to a per-series prov. **Precision → Guldserie** (`SeriesType=Precision`, 5 shots) → feeds Pistolskyttemärkets guldfodring part 1 + Elit precision. **Duell → Snabbserie** (`SeriesType=Speed`, `Target=Snabbpistol_25m`, scored 0–50) → feeds **Elitmärket** (needs Guldmärke), *not* the guldfodring (Duell on snabbpistoltavla is the Elit speed series, not a tillämpningsserie). Milsnabb/MagnumPrecision/NatHelmatch/Springskytte map to no per-series prov → button hidden. Weapon group (A/B/C/R) derived from the shooter's class (manual: `#shootingClass`; match: `currentMatch.weaponClass`); button hidden for M/L/unknown.
- **No new server endpoint for submit** — reuses `Marken/SubmitSeries` (already takes SeriesType/WeaponGroup/Shots/Target/Total/ClubId, computes the age-adjusted threshold + `Qualifies`, lands Pending, returns a QR `verifyUrl`). Flow: save the score normally, then open a QR/queue validation modal — identical to the existing button.
- **Shared partial `Views/Partials/_MarkenSerieQuickSubmit.cshtml`** (mqs*-prefixed, self-contained — club picker via `GetMyClubsForSeries`, personnummer capture, `SubmitSeries`, QR + `GetMySerieStatus` poll). Included on BOTH `UserProfile.cshtml` and `TrainingMatch.cshtml`. The manual modal's per-series button commits the series into the normal entry (`handleEnterSeries`) AND submits to Märken; the match button runs `matchSaveScore()` then opens the submit modal.
- **Personnummer capture (age → reduced Guld krav).** Age is sourced from the existing `personNumber` member property (canonical — same field used at registration, profile edit, Springskytte age classes). When a shooter submits a *precision* Guldserie and `GetBirthYear` returns 0 (no personnummer on file), the modal asks for the personnummer once and saves it to `personNumber` via `Marken/SetMyPersonNumber` (gap-fill only — never overwrites a valid existing one; also `GetMyBirthYearStatus`), so the −1/serie-from-the-year-after-55 and silverkrav-from-the-year-after-65 concessions (SHB 5.1.1.1 + 5.1.2.2 — **55/65, not 60/70**; `Marken.PrecisionThreshold` already correct, unchanged) apply and elderly shooters aren't under-credited. Threshold is a soft flag (series still saves + can be validated), but the auto-guldfodring keys off `Qualifies`, so accurate age matters. **No new property / no operator step** — reuses `personNumber`. Adds C# → full rebuild; views runtime-compiled → load UserProfile + TrainingMatch once after deploy.

### Finals Start List for Precision Competitions (2026-05-21)

**Freeze granularity = result-list group, NOT championship class.** A "group" is whatever the result list shows — a sub-class like "A1" if no merge, or a combined name like "C2+Dam" if the admin merged C2 Dam into C2 via `competitionResult.mergeConfig`. The merge lookup is read by `PrecisionQualifyingResultsService.GetMergeLookup` and applied inside `PrecisionFinalsQualificationService.BuildFullClassRankings` so finals always match the result list.

This means the **result list must be generated first** (click "Uppdatera" on the Resultat tab). Without it, the finals tab shows an empty state explaining what to do.

**Skjutlag-assignment model:** each group has a `SkjutlagNumber` + `OrderInSkjutlag`. Multiple groups can share the same skjutlag — they appear as **contiguous position blocks** ordered by `OrderInSkjutlag`. Each group's leader (rank 1) is always at the start of its block; positions within the block follow the group's leaderboard. There is **no score-based re-ranking across groups**.

Example: "C" shares skjutlag 1 with "C Vet Y". C (order 0) fills positions 1–10; C Vet Y (order 1) fills positions 11–17. C rank 1 at pos 1, C Vet Y rank 1 at pos 11.

**Per-group freeze (not global):** admin clicks "Frys" per group as that group's qualifying completes. Each frozen group stores its own SHA checksum over (`MemberId`, `SeriesNumber`, `Shots`) tuples scoped to the **members** in that group at freeze time. Re-computing detects post-freeze edits — admin can refreeze any individual group without disturbing the others. Snapshot also keeps "orphan" groups (frozen but no longer present in the result list, e.g. after a merge-config change) so the admin can see/unfreeze them.

**Workflow gate:** finals start list cannot be generated until at least one group is frozen.

**`FinalsClassConfig`:** `{ Skip, SkjutlagNumber, OrderInSkjutlag, FinalistCountOverride?, IncludeAllShooters }`. Defaults are filled in by the JS on first render (each frozen group gets its own incrementing skjutlag number).

**Cut rule:** still the SHB default — top 1/6 (rounded up, min 10) with tie-extension at the cutoff score. Overridable per class via `FinalistCountOverride` or fully via `IncludeAllShooters`.

**Shared model trick (avoids duplicate editor endpoints):** `StartListShooter` and `StartListTeam` got optional nullable fields — `QualificationRank`, `QualificationScore`, `QualificationXCount`, `ChampionshipClass`, `ChampionshipClasses` — so all existing editor endpoints (`MoveShooterToTeam`, `AddShooterToStartList`, `UpdateTeamTimes`, `CreateNewTeam`, `DeleteTeam`, `RemoveShooterFromStartList`) work transparently for both qualifying and finals lists. The Step 4 editor in the wizard calls these same endpoints with the finals start list id. The renderer detects `Settings.Format == "Championship Finals"` and emits extra Rang/Kvalresultat columns.

**Dual-renderer trap (same warning as the regular start list):** the public finals page is rendered by `Views/PrecisionFinalsStartList.cshtml` (reads `configurationData` JSON directly via `dynamic`); the cached `startListContent` blob comes from `StartListHtmlRenderer.GenerateStartListHtml` (with `isFinals = format == "Championship Finals"`). Any visible-field addition must be wired in BOTH places.

**Admin UI (`CompetitionFinalsStartListManagement.cshtml`, Rev 2 2026-05-22):** mirrors the standard Startlista layout — a "Skapa ny finalsstartlista" button at the top, an "Official finals start list card" with status badge / Publicera / Avpublicera / Redigera / Öppna i ny flik / iframe of the published page, and a "no list yet" empty state. The 3-step **generation wizard lives entirely inside a Bootstrap modal** `#generateFinalsStartListModal`: Steg 1 (per-group freeze), Steg 2 (per-group skjutlag config + live preview), Steg 3 (start time / interval / max + Generera). On Generera success the modal hides itself and the card refreshes.

**Editing reuses the shared `#startListEditorModal` (from `CompetitionStartListManagement.cshtml`)** — the finals card's Redigera button calls `window.openStartListEditor(finalsId)`. This works because the editor is parameterized on `window.currentStartListId` and the 9 editor endpoints (MoveShooterToTeam, AddShooterToStartList, RemoveShooterFromStartList, UpdateTeamTimes, CreateNewTeam, DeleteTeam, BulkMoveShooters, UpdateShooterWeaponClass, SearchAvailableShooters) operate generically on any start-list node. The only doctype-specific concern — the "Officiell" banner inside the editor — is handled by branching on `startList.ContentType.Alias == "finalsStartList"` inside `GetStartListForEditing` to read `isOfficialFinalsStartList` for finals lists. A `hidden.bs.modal` listener on the editor refreshes the finals card iframe when the editor was opened for the finals list.

The legacy `#finalsStartListSection` markup + `checkFinalsEligibility` / `displayQualificationSummary` / `showExistingFinalsList` / legacy `generateFinalsStartList` JS block in `CompetitionStartListManagement.cshtml` was removed in this rev (it had been dormant since the new partial replaced it).

**Endpoints:**
- `FreezeClassResults` / `UnfreezeClassResults` — per-class freeze
- `GetQualifyingSnapshot` — per-class state (has results, frozen?, frozenAt, frozenBy, shooter count, staleness flag)
- `GetFinalsConfig` / `SaveFinalsConfig` / `PreviewFinalsConfig` — per-class config + dry-run preview
- `GenerateFinalsStartList` — workflow-gated on at least one frozen class
- `GetFinalsStartList` / `PublishFinalsStartList` — read + publish toggle

**Key files:**
- `CompetitionTypes/Precision/Services/PrecisionFinalsQualificationService.cs` (extended with `BuildFullClassRankings`, public `CalculateQualificationCutoff`, static `ChampionshipClasses`)
- `CompetitionTypes/Precision/Services/PrecisionQualifyingResultsService.cs` (new) — per-class freeze/unfreeze/staleness
- `CompetitionTypes/Precision/Services/PrecisionFinalsStartListBuilder.cs` (new) — skjutlag-assignment + block-of-positions builder
- `CompetitionTypes/Precision/Models/PrecisionFinalsStartList.cs` — POCOs `QualifyingResultsSnapshot` (dict), `ClassResultsSnapshot`, `FinalsClassConfig`, `FinalsStartListSettings`
- `CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs` — wizard endpoints + rewritten `GenerateFinalsStartList`
- `Views/Partials/CompetitionFinalsStartListManagement.cshtml` — 4-step wizard with inline editor
- `Views/PrecisionFinalsStartList.cshtml` — public `/finalsstartlista/` page
- `Views/CompetitionManagement.cshtml` — partial wired in, gated on `numberOfFinalSeries > 0`
- `Views/Competition.cshtml` — public "Visa finalsstartlista" button gated on `isOfficialFinalsStartList`

### Skjutlag / Patrull Label (2026-05-20)
**What:** Freeform per-skjutlag/patrol label admins can type to disambiguate multi-day competitions (e.g. "Lördag fm", "Söndag 14 juni", "Final"). Replaces a backlog item that originally asked for a structured day-of-week + date field.

**Scope:** Precision-family (Precision/Duell/Milsnabb/MagnumPrecision/NationellHelmatch) + Fältskytte/MagnumFält. Springskytte excluded (no per-team entity). Direktplacering already had `DirektplaceringTeam.Label` wired end-to-end — no change there.

**Persistence:**
- Precision-family: new `Label` field on `StartListTeam` inside the `configurationData` JSON blob on the `precisionStartList` doctype. Backward-compatible (Newtonsoft deserializes missing → `""`).
- Fältskytte: new `Label NVARCHAR(200) NULL` column on the `FaltskyttePatrol` SQL table. **Manual operator step:** run `Migrations/add-label-to-faltskytte-patrol.sql` in SSMS.

**Dual-renderer trap (don't repeat this mistake):** The Precision team header is rendered in **two** places. Adding any new `StartListTeam` field shown to users must wire both:
- `CompetitionTypes/Precision/Controllers/StartListHtmlRenderer.cs:55` — produces the cached `startListContent` blob (admin preview, print, email). A comment is now in place flagging this.
- `Views/PrecisionStartList.cshtml:253` — the public `/startlista/` Razor page, reads `configurationData` JSON directly via `dynamic` (so missing-property access needs a try/catch fallback to `""`).

**JS dual-casing pattern:** `GetStartListForEditing` returns config with inconsistent casing — same precedent as `shooter.club || shooter.Club` at line 1691. Read team fields as `team.label || team.Label`. Used at the card header, modal lookup, and 3 move/add-shooter dropdowns in `CompetitionStartListManagement.cshtml`.

**Copy unification:** the editor modal was the only surface still using "Lag N" — all references in `CompetitionStartListManagement.cshtml` and one in `CompetitionResultsManagement.cshtml:1678` updated to "Skjutlag N". Edit-modal title changed from "Redigera Lagtider" to "Redigera skjutlag"; per-team button icon changed from clock to pencil-square for edit discoverability.

**Fältskytte UX:** the time-edit pencil now opens a two-step prompt (time then label) via `faltEditPatrol(patrolId, time, label)`. Old `faltEditPatrolTime(...)` kept as a backward-compat shim. Label suffix shown in: admin patrol cards, station entry roll-call/entry screens, public competition page modal, print view, walk-in slot picker, `StationPage` multi-patrol picker.

### Fältskytte Särskjutning (2026-05-20)
**Scope:** Fältskytte (Normal mode + Poäng mode) and Magnum Fält. Same championship gate as the precision-family Särskjutning: `CompetitionScopeHelper.IsChampionshipScope` + tied score at medal places 1–3.

**Round semantics differ per variation:**
- **Normal** — round is one or more stations; intra-round tiebreak Hits → Figures → Poängmål-total. Display: `5/4`.
- **Poäng** — same round shape; intra-round tiebreak Points (Hits+Figures) → Poängmål-total. Display: `10p`.
- **Magnum Fält** — single specially-configured station with all figures as poängmål, only 1 hit per figure counted (max ≈ figures × max-per-figure). Intra-round tiebreak: sum of poängmål-scores. Display: `23p`.

**Architecture:**
- Single SQL table `FaltskytteShootOffEntry` (one row per shooter per round) — `Hits`/`Figures`/`HitDistribution` nullable for Magnum; `PoangmalScores`/`TiebreakerScore` carry the score for all three variations.
- Pluggable `IShootOffRoundComparer` strategy with three implementations (`NormalRoundComparer`, `PoangRoundComparer`, `MagnumRoundComparer`). Factory: `FaltskytteShootOffService.ComparerFor(competitionType, scoringMode)`.
- `FaltskytteShootOffService` mirrors the precision `ShootOffService` API: `GetEntriesForCompetitionAsync`, `SaveEntryAsync`, `DeleteEntryAsync`, `DetectTiedMedalGroups`, `ApplyShootOffOverride`, `ComputeProgressiveStatus`.
- Wired into `FaltskytteController.GetFaltskytteResults` after the existing tiebreaker sort — only fires when `IsChampionshipScope` is true. Standard-medal C-split (SM/LDM-only per SHB FR-204) stays on the narrower `isSmOrLdm` predicate.
- **`FaltskytteController` merge lookup migrated to `ClassMergingService.BuildMergeGroupLookup`** so multi-source merges into one target collapse into a single combined group (same fix as Precision had for competition 2173).

**Endpoints:** `GetFaltskytteShootOffStatus`, `GetFaltskytteShootOffConfig`, `SaveFaltskytteShootOffConfig`, `SaveFaltskytteShootOffEntry`, `DeleteFaltskytteShootOffEntry` — all in `FaltskytteController`, three-tier auth.

**Admin UI** in `Views/Partials/FaltskytteResultsManagement.cshtml`: a "Särskjutning" card appears after the Deltävling section when the API returns tied medal groups. Includes a station-config editor (copy from existing station or empty mall, JSON textarea) and per-class tied-group rows with "Resultat..." buttons that open an entry modal whose form switches between hits+figures+poängmål (Normal/Poäng) and per-figure point boxes (Magnum).

**Public UI** in `Views/CompetitionResult.cshtml` `loadFaltskytteResults`: new `Sär` column rendered only for classes with shoot-off rounds; per-class footnote lines summarise how each medal was decided. The auto-generated combined class name is overrideable via the existing `classNameOverrides` doctype property (admin pen icon — same mechanism as Precision).

**Manual operator steps:** Run `Migrations/create-faltskytte-shootoff-entry-table.sql` in SSMS and add the two new `competitionResult` Textarea properties listed above.

### Fältskytte Standalone Configurations (2026-05-24 → 25)

**What:** Station configurations decoupled from individual competitions. Built at `/faltkonfig` (Tävling nav → Fältkonfig), reused by N competitions via a saved-config picker. Replaces the legacy in-competition `FaltskytteStationConfigModal`.

**Data model:**
- `FaltskytteConfiguration` table — Id, Name, Description, OwnerMemberId, OwnerClubId, Visibility (`Private`/`Club`/`Region`/`Public`), SecretUntil, JsonBlob, CreatedDate, ModifiedDate.
- `FaltskytteConfigurationCollaborator` table — composite PK `(ConfigId, MemberId)`, CASCADE on Config.
- The competition's existing `stationConfig` property is unchanged — Anslut copies JsonBlob into it. A `_attachedConfigId` meta key inside the blob lets the picker restore selection on next edit-modal open. Other meta keys: `_linkedGroups`, `_mode`, `_morker`, `_attachedConfigId`.

**Authorization** (`FaltskytteConfigurationService`): owner / collaborator / site-admin tiers + a SecretUntil gate that overrides Visibility while in force (only owner + collaborators see it). Visibility tiers: Private (owner only), Club (club admins + Skjutledare in OwnerClubId), Region (regional admins in OwnerClubId's region via clubNode.regionalFederation), Public (any authenticated member).

**Surfaces:**
- `Views/FaltskytteConfigurationHub.cshtml` — listing at `/faltkonfig` (Umbraco doctype `faltskytteConfigurationHub`). Login gate, Skapa ny modal, filter tabs (Alla / Mina / Delade med mig / Publika), card grid with Redigera / Duplicera / Ta bort.
- `Views/FaltskytteConfigurationEditor.cshtml` + `Controllers/FaltskytteConfigurationEditorController.cs` — editor at `/faltkonfig/{id}/redigera`. MVC route (Surface Controllers can't host parameterized URLs). Controller looks up the `/faltkonfig` hub node and passes it as the IPublishedContent Model so Master.cshtml's `Model.Root()` / `Url()` calls don't NRE. Returns 500 with a setup hint when the hub node is missing.
- `Views/Partials/_FaltskytteCompetitionPicker.cshtml` — the saved-config picker. Mounted in `CompetitionWizardModal` (prefix `wizard_`) and `CompetitionEditModal` (prefix `edit_`) via shared partial. JS guarded by `window.faltPickerScriptLoaded` so dual-include doesn't double-define functions. Attaches by POST-fetching the chosen config + writing `JsonBlob` into the existing `{prefix}faltStationConfigJson` hidden field; wizard's `{prefix}numberOfSeries` auto-syncs to station count when present.

**Refactored partials shared between editor + legacy surfaces:**
- `_FaltskytteConfiguratorScript.cshtml` — the entire JS body (extracted from the old modal, ~1900 lines).
- `_FaltskytteConfiguratorSuggestionModal.cshtml` — föreslaget-skjuttid breakdown modal.
- The old `FaltskytteStationConfigModal.cshtml` partial is deleted. The legacy `openFaltStationConfigurator()` / `openEditFaltStationConfigurator()` and the wizard's `faltCfgObserver` MutationObserver are also gone.

**Standalone-editor mode in the configurator script:** `window._faltCfgEditorMode = 'configuration'` makes `faltCfgIsStandaloneEditor()` true; `faltCfgSaveToServer` then POSTs to `FaltskytteConfiguration/Update` (`{id, jsonBlob}`) instead of the legacy per-competition save endpoint. `faltCfgIsEditMode()` short-circuits true in this mode. Hidden-field writes are guarded so they no-op when the wizard's hidden field is absent.

**Import station from another config** (in the editor): dropdown of accessible configs → pick source station → pick destination station number → choose Kopiera or Länka. Link writes `_linkedFromConfigId` + `_linkedFromStationNumber` + `_linkedFromChecksum` on each weapon-class station. Checksum is a stable Cyrb53 hash over the canonical station JSON minus the `_linkedFrom*` keys, so chain-of-links doesn't accumulate stored state.

**Linked-station reload UX** (in the editor): a `renderActiveTab` wrapper triggers `fkLinkPostRender()` after every render. Source configs are fetched lazily once + cached. Each station card gets a banner: synkad (gray), diverged (yellow "Källan har ändrats" + Ladda om + Avlänka), or unavailable (gray). Ladda om copies fresh source data into the dest's station + recomputes the checksum.

**Endpoints** (`FaltskytteConfigurationController`):
- `GET ListAccessible` / `GET Get` / `GET GetStationsForImport` / `GET SearchMembers` (collaborator picker, any logged-in user)
- `POST Create` / `POST Update` / `POST Delete` / `POST Duplicate`
- `POST AddCollaborator` / `POST RemoveCollaborator`

`SecretUntil` in the DTOs is **`string?`** (not `DateTime?`) so System.Text.Json doesn't reject Flatpickr's `"Y-m-d H:i"` format. Service parses defensively via `ParseSecretUntil` accepting both ISO and Flatpickr shapes.

**Manual operator steps:**
- Run `Migrations/create-faltskytte-configuration-tables.sql` in SSMS.
- Create doctype `faltskytteConfigurationHub` (no properties, allowed under Home).
- Publish a content node of that type under Home, URL alias `faltkonfig`.

**Backward compat:** existing competitions keep their inline `stationConfig` JSON. The picker shows a **Konvertera till sparad konfiguration** link when it sees inline data without `_attachedConfigId`. Click → prompts for a name → POSTs `Create` with the inline JSON as starting blob → embeds `_attachedConfigId` in the live hidden field. Operator still needs to Save the competition to persist the link.

### Fältkonfigurator Approval Workflow (2026-05-25)

**What:** Banläggare-gated approval lifecycle for saved configurations. Owners pick a specific Banläggare to ask, that person gets an email + deep-link, and only they can approve. Approved configs are locked from JsonBlob edits (metadata stays editable).

**State machine:**
```
Draft  ──RequestApproval(picksApprover)──▶  PendingApproval  ──Approve──▶  Approved
   ▲                                                              │
   └─────────────────────────────  Unapprove  ────────────────────┘
```

**DB columns on FaltskytteConfiguration:**
- `ApprovalStatus NVARCHAR(20) NULL` — `Draft` / `PendingApproval` / `Approved`. Null treated as Draft for legacy rows.
- `RequestedApproverMemberId INT NULL` — who the owner asked. Populated while PendingApproval; cleared on Approve + Unapprove.
- `ApprovedByMemberId INT NULL` + `ApprovedDate DATETIME NULL` — who actually approved. Populated only when Approved.

Two SQL migrations: `add-approval-to-faltskytte-configuration.sql` (status + approved-by columns) and `add-requestedapprover-to-faltskytte-configuration.sql` (directed-request column). Run both on prod.

**Authorization:** `ApproveAsync` permits site admin OR (viewer is owner AND has Banläggare cert) OR (viewer is the RequestedApproverMemberId AND has Banläggare cert). Anyone else gets `403`-equivalent with a friendly message.

**Edit gate:** `UpdateAsync` rejects JsonBlob changes when ApprovalStatus = Approved. Idempotent JsonBlob saves (same content, normalized via `JToken.DeepEquals`) pass through to support "Spara allt" no-op flows. Metadata fields (Name / Description / Visibility / SecretUntil / Collaborators) are always editable.

**Email:** `EmailService.SendFaltkonfigApprovalRequestAsync` sends an HTML mail to the picked Banläggare with the deep-link `/faltkonfig/{id}/redigera`. Best-effort — request persists even if SMTP fails.

**Endpoints** (`FaltskytteConfigurationController`):
- `POST RequestApproval` `{configId, requestedApproverMemberId}` — owner picks who to ask.
- `POST Approve` `{configId}` — gated per ApproveAsync rules.
- `POST Unapprove` `{configId}` — owner / collaborator / Banläggare / admin.
- `GET GetBanlaggareCandidates` — every active Banläggare cert holder (name + primary-club name), sv-SE alphabetical.

**UI:**
- Editor: approval banner above the configurator card with status-aware actions (Begär godkännande modal opens a Banläggare picker; PendingApproval banner names the awaitee + Godkänn button only renders to the requested approver; Approved banner names the approver + Begär ändring / Återkalla actions). `#fkEditorConfigurator.fk-locked` CSS dims + disables pointer events when Approved.
- Listing: per-card approval badge (Godkänd / Väntar); new "Väntar på godkännande" filter chip visible only to Banläggare (via `viewerCanApprove` in the ListAccessible response).
- Competition picker dropdown sorts Approved-first with `✓` prefix; the attached-config status line shows the badge.

**View-model derived fields** (`FaltskytteConfigurationView`): `RequestedApproverMemberId/Name`, `ApprovedByMemberId/Name`, `ApprovedDate`, `ApprovalStatus`, `CanApprove` (viewer holds Banläggare cert), `IsLocked` (Approved), `IsRequestedApprover` (viewer == RequestedApproverMemberId).

**Banläggare cert check** uses the existing `CertificationService.HasActiveCertAsync(memberId, CertificationTypes.Banlaggare)` — no new role plumbing.

### Stationschef Tidur (2026-05-26)

**What:** Audio-driven shooting-time clock on the station entry page (`FaltskytteStationEntry.cshtml`), sitting between Upprop and "Starta resultatinmatning" on the roll-call screen. Reads the patrol's weapon-class `shootingTimeSec` from the loaded station config and runs a 10 s upprop → ELD → skjuttid → Eld upphör auto-sequence with per-figure visibility timelines. Four extra manual command buttons (Ladda / Alla klara pre-fire, Patron ur / Visitation post-fire) round out the full Fältskytte cycle.

**Audio playback (NOT Web Speech API):** Initial implementation used `speechSynthesis` but the cease-fire elongation sounded robotic on every device and Chrome-on-Windows couldn't see the Microsoft OneCore voices. Replaced with 8 pre-recorded Swedish MP3 clips generated via OpenAI's `gpt-4o-mini-tts` (instruction-controllable, military shout style):
- `10-sek-kvar.mp3`, `fardiga.mp3`, `eld.mp3`, `eld-upphor.mp3` — fired automatically by the timer
- `ladda.mp3`, `alla-klara.mp3`, `patron-ur-proppa-vapen.mp3`, `visitation.mp3` — surfaced as manual buttons
All live in `/wwwroot/sounds/kommandon/`. ~243 KB total. `<Audio>` elements preloaded at script-load time. `tmrPlay(key)` plays a clip; if the browser rejects `.play()` (audio not yet unlocked by a user gesture), an `tmrAudioBlocked` banner prompts the operator to tap the 🔊 Test-röst button once. Subsequent plays during the auto-sequence (fired from inside the `requestAnimationFrame` loop via `setTimeout`-equivalent flags) work unblocked after the first gesture.

**Sequence (anchored on `performance.now()` + scheduled via `requestAnimationFrame`, no chained setTimeouts):**
- T-10 s on tap of Starta: plays `10-sek-kvar.mp3` (this is also the user-gesture audio unlock)
- T-3 s: plays `fardiga.mp3`
- T+0: plays `eld.mp3`, shooting bar starts moving
- T+(shoot − 3): plays `eld-upphor.mp3` (~3 s long, lands near T+shoot)
- T+shoot: bar full, display switches to "Eld upphör" in red, Återställ button shown

**Layout (two button rows + center auto-sequence):**
```
[Header: Tidur + 🔊 Test röst + ⟲ Återställ]
[Pre-fire row:  Ladda  |  Alla klara]
[STARTA SKJUTSEKVENS — full-width prominent button]
[Countdown · Skjuttid bar · figure rows]
[Post-fire row:  Patron ur, proppa vapen  |  Visitation]
```
No state machine — every command button is tappable any time so the chief can repeat Alla klara on "nej", or skip ahead.

**Per-figure timeline:** each Framsvängande / Bortsvängande figure gets its own row with the configured behavior + effective times displayed ("fram 8 s, syns 8 s"). Visible windows render as green bands; the now-line scans across, and on a visibility transition the row flashes yellow + a FRAM / UT state badge flips. Fast figures show one full-width green band. Defensive camelCase/PascalCase reads via `tmrPick`, and missing `showTimeSec` / `hideAfterSec` fall back to the configurator's UI defaults (8 / 8) so figures don't render as empty stripes when the user never touched the input field. The main shooting-time bar sits in the same flex row structure as figure rows (140 px label + flex-grow bar + 64 px spacer) for vertical alignment.

**Status line:** shows the read shooting time and weapon class — `"Skjuttid 16 s (vapenklass A). Tryck Starta…"` — which surfaces config drift if the read value doesn't match expectations.

**Lifecycle:** `fseTimerCancel()` runs when leaving the roll-call screen (in `fseStartEntry` and `fseBackFromRollCall`) so the timer never bleeds into result entry or back to the patrol picker. Cancel also pauses + rewinds any in-flight audio (e.g. mid-cease-fire). Screen Wake Lock acquired on Starta (re-acquired on `visibilitychange`).

**Test röst** plays `eld-upphor.mp3` (the longest + most distinctive clip) — verifies playback works AND gives the operator a feel for the cease-fire on this device.

### Skjutledare-vy for Precision / MagnumPrecision (2026-05-26)

**What:** Dedicated range-officer page at `/skjutledare?c=<compId>&l=<lagNum>` for the precision-family disciplines where a Skjutledare commands the firing line instead of marking results. Separate from the staff-facing `/station?c=...&s=...` page on purpose — those have different mental models. Reached via a small yellow 📢 icon button rendered next to each Lag entry on the Resultat tab in `CompetitionManagement`, only shown when `competitionType` is Precision or MagnumPrecision.

**Routing:** Umbraco content node with template `SkjutledareView`. Setup: create doctype `skjutledareView` (no properties, template `SkjutledareView`, allowed under Home) and publish a content node with URL alias `skjutledare`. Same pattern as `/station` and `/competitionmanagement`.

**Auth gates:** mirror `/station`'s `canEnterResults` branch — site admins, competition managers, club admins of the hosting club, and Skjutledare cert holders for the hosting club. Comp-type guard refuses to render for non-Precision/MagnumPrecision with a friendly "Inte tillgängligt" message.

**Auto-sequence (per serie):**
- T-60 s on tap of STARTA SERIE N: plays `ladda.mp3` (skjutledare voices "Serie X" verbally *before* the tap; not part of the auto sequence)
- T-3 s: `fardiga.mp3`
- T+0: `eld.mp3`
- T+(shoot − 3): `eld-upphor.mp3`
- T+shoot: bar full, "Eld upphör" displayed

**Editable shoot-time:** Tap on the big countdown display → modal opens with minute and second steppers (60 s steps for minutes, 5 s for seconds, range 0:05–15:00). Saved per-comp in `localStorage` under `hpsk_skj_shoot_sec_<compId>`. Pencil icon affords the edit, hidden during running.

**Vidare (skip-ahead) button** sits in STARTA's position while the sequence runs (btn-warning yellow). Two skip targets depending on phase:
- During pre-fire (tSec < −3 s): re-anchors `skjStartMs` so next tick fires Färdiga immediately (Eld follows naturally 3 s later)
- During firing (0 ≤ tSec < shoot−3): re-anchors so next tick fires Eld upphör immediately
- Otherwise hidden (between Färdiga–Eld and during cease-fire window — nothing useful to skip to)

**Multi-skjutledare per lag:** a single skjutlag can span multiple ranges (e.g. positions 1–25 in Hall 1, 26–50 in Hall 2) — both Skjutledare open the same URL on their own devices. Timers run independently per device. No server sync.

**Föregående/Nästa serie:** paired buttons at the bottom of the card. Föregående disabled at Serie 1, Nästa disabled at the last serie, both disabled during the running sequence. Either action calls `skjTimerReset()` so the next serie starts from a clean display.

**Manual command buttons (post-fire):** Patron ur, proppa vapen + Visitation — same MP3 files as the Fältskytte Tidur (`/wwwroot/sounds/kommandon/`). The pre-fire manual Ladda/Alla klara buttons of Fältskytte are deliberately omitted here — the Skjutledare voices "Serie X" themselves before tapping STARTA, and Alla klara isn't part of standard Precision range commands.

### Fältkonfigurator: figure timings no longer auto-scaled to shootingTimeSec (2026-05-26)

**What changed:** Figure timing fields (`delayBeforeShowSec` / `showTimeSec` / `hideAfterSec` / `reappearSec`) used to be auto-rescaled across weapon classes by `ratio = destClassTime / srcClassTime` inside `faltCfgSyncShape` (Simple-mode shape-mirror) and `faltCfgUpdateStation` (Simple-mode shootingTimeSec change). That silently rewrote operator-entered values — a "fram efter 8 s" on a 14 s class became "fram efter 10 s" on an 18 s class, and the scaling was only visible after switching to Advanced mode. Both call sites are now removed; the helper `faltCfgScaleFigureTimings` is deleted. Existing snapshots aren't auto-fixed.

**What's kept:** the explicit Advanced-mode **"Kopiera från … Δsek"** copy button still applies proportional scaling — that one's user-initiated and labeled, so it's not a surprise. Re-attaching a config to a competition still copies the full blob (the picker is unchanged).

### Fältskytte SHB Shoot-Time Suggestion + Svårighetsgrad (2026-05-24 → 26)

**What:** The configurator surfaces a SHB-derived suggested skjuttid per station per weapon class, a live breakdown modal, an "Använd" button that copies it to the Skjuttid field, and a **Svårighetsgrad %** badge that scores the chosen Skjuttid against the SHB minimum.

**Formula (depends on Tävlingstyp / `_scoringMode`):**
- 6 skott per station (SHB convention) — the cap is the same in both modes.
- Per-shot time = `D / maxD(SizeGroup, weaponGroup) × maxTime(weaponGroup)`. maxTime: A&R 2.0 s, B 1.75 s, C 1.5 s.
- **Stödhand-easing (SHB D.10.6.1):** when `supportHand === 'Stödhand tillåten'`, look up `maxD` for SizeGroup − 1 (clamped at 1) instead of the figure's actual group. Lower per-shot-time → lower SHB-min → lower svårighetsgrad %. Surfaced in the breakdown as e.g. *"storleksgrupp 5 (stödhand → grupp 4)"*. Replaced the earlier wording in the modal that incorrectly stated stödhand didn't affect the calc.
- **Poangfält (revised 2026-05-26 per Banläggare D.10.3 feedback):** fastest-legal allocation. Each figure gets `max(1, station.minShotsPerFigure × targetsPerFigure)` shots as floor. Remaining shots fill the easiest (lowest per-shot time) figure first, then next-easiest, honoring per-figure max caps. Sum → base. For a 6-figure station collapses to "sum of all per-shot times"; for a 1-figure station = "6 × per-shot" (worst case = best case).
- **Normal:** per-figure greedy 6-shot allocation with min-floor + slowest-first. Each figure gets `station.minShotsPerFigure × targets` as floor, then sort by perShot desc and fill remaining slots up to `targetsPerFigure × maxShotsPerFigure` per figure.
- Tillägg (both modes): +2 s when `weaponStartPosition === '45 grader'`; +2 s × (n_målgrupper − 1) for omriktning; ×1.30 multiplier when Mörkerfältskjutning toggle is on.

**`MinShotsPerFigure` / `MaxShotsPerFigure` are STATION-WIDE** (mirroring SHB phrasing "min/max träff per figur i en station"). Both fields render as Min träff/fig + Max träff/fig inputs in the configurator's station card. Min defaults to 0 (no requirement), shown in BOTH Normal and Poäng modes. Max defaults to 6 (Poäng's "no cap"), shown in Normal only (Poäng users don't need to set it). Non-default values surface in `StationInfoCard` Förutsättningar as *"Min/Max träff/figur"*. An earlier commit (`7ee3df9`) added these as **per-figure** controls in error — corrected in `28c63c5`.

**Svårighetsgrad badge:** `round(100 × SHB-min-tid / station.shootingTimeSec)`. 100 % = exactly at SHB minimum; <100 % = generous; >100 % = below SHB minimum (impossible per regelverk but mathematically valid). Plain badge — no threshold colors per Banläggare feedback. Sourced from the same Excel formula HPSK Banläggare have used historically ("Pokalen 2 tidutrakning.xls", VBA dump).

**Tävlingstyp moved into the configurator (2026-05-26):** the Poängberäkning / scoringMode dropdown — formerly per-competition on the wizard + edit modal — is now part of the configuration. Picker lives in `Views/FaltskytteConfigurationEditor.cshtml` next to the Mörker toggle and writes `_scoringMode` into the JSON blob meta keys.
- **Phase 1:** picker added; `_FaltskytteCompetitionPicker.cshtml` propagates `_scoringMode` → competition `scoringMode` doctype property on Anslut + Konvertera, so all 7+ downstream read sites (FaltskytteController, FaltskytteStandardMedalService, FaltskytteShootOffService, FaltskytteStatsService, FaltskytteStationEntry.cshtml, FaltskytteResultsManagement.cshtml, CompetitionResult.cshtml) keep reading the competition property unchanged.
- **Phase 2:** wizard + edit modal dropdowns replaced with a hidden input + read-only `<span id="{prefix}scoringMode_display">` badge ("Normal" / "Poängfält"). `faltUpdateScoringModeDisplay(prefix)` is invoked on modal open and on picker attach to keep the label in sync.

**Suggestion-details modal:** `_FaltskytteConfiguratorSuggestionModal.cshtml` is `modal-lg` with `table-layout:fixed` + colgroup. In Normal mode the breakdown renders the per-figure greedy allocation as a small italic sub-row spanning both columns (under the per-målgrupp sec row) so allocator detail doesn't push the modal sideways. Appended footer rows: Föreslaget, Vald skjuttid, Svårighetsgrad.

**SHB tables baked into JS** (`SHB_MAX_DISTANCES` const in `_FaltskytteConfiguratorScript.cshtml`): per SizeGroup (1–14) → `{AR, B, C}` max distance, sourced from SHB 2026 pp. 100-122. SizeGroup 15 is the "Ej grupperad" bucket (no SHB row, returns null → no bound).

**FieldTarget table changes:**
- New `SizeGroup INT NOT NULL DEFAULT 15` column (`Migrations/add-sizegroup-to-fieldtarget.sql` + `update-fieldtarget-sizegroup-default.sql` which bumped the default from 0 → 15).
- Dropped the per-figure `MaxDistance{A,R,B,C}` columns (`Migrations/drop-fieldtarget-maxdistance-columns.sql`) — SizeGroup now drives every max-distance lookup; the columns were duplicated SHB table values that nothing read.

**Distance slider** (per målgrupp): bounds derived from `min(figure.SizeGroup → maxDistance for active weapon class)` / 5 m floor. Lives in advanced mode's per-class tab; in simple mode there's a small weapon-class picker above the målgrupper since distance can differ per class. Per-class distance survives the simple-mode shape sync via the saved-distances preserve step in `faltCfgSyncShape`.

**Mörker toggle** at the top of the configurator. Stored as `_morker` meta key on the JSON blob. Toggling shows a confirmation dialog if any station has configured figures — confirming triggers `faltCfgSaveAll()` so the flag persists; otherwise it stays in memory until the next station save.

**Figurkatalog (`FaltskytteTargetPickerModal.cshtml`):**
- Grid groups by SizeGroup with section headers showing the SHB max-distance hint.
- Chip-row filter above the grid (1–14 + 15/Ej grupperad). Selection persists in `sessionStorage`.
- Distance badge under each card removed (the section header carries it).
- Modal goes fullscreen on viewports below `md` (768 px) via `modal-fullscreen-md-down`; inner d-flex stacks vertically below md so the picker is usable on a phone or pad in the field.

**Controller clamps:** `FaltskytteController.CreateTarget` / `UpdateTarget` clamp SizeGroup to 1–15. The legacy `UpdateTargetDistances` endpoint was deleted.

### Live Result Board (Resultattavla) — Fältskytte support, weapon-class multi-select & Växla, standalone /live URL, tiled multi-view (2026-05-27)

**What:** A dark spectator/TV board served at its own public URL **`/live?c=<competitionId>`** (chromeless page `Views/ResultBoard.cshtml`, `Layout=null`). The board logic is `rbBoardScript`, which lives in **`Views/Partials/_ResultBoardScript.cshtml`** (its single home — do not duplicate). It polls a results endpoint every 15 s (`REFRESH=15000`) and re-renders the full standings each time (full snapshot, no diff). Visibility-aware: polling pauses when the tab is hidden, resumes with an immediate fetch. (It was originally an in-memory `window.open('','_blank')` + `document.write` popup that showed `about:blank`; moved to a real URL 2026-05-27 so it's bookmarkable / castable / reloadable on a wall-TV browser.)

**Discipline dispatch inside `rbBoardScript`:**
- `fetchResults()` picks the endpoint by `compTypeId`: Springskytte → `/Springskytte/GetSpringskytteResults`; **Fältskytte/MagnumFalt → `/Faltskytte/GetFaltskytteResults`** (reads `d.results.classGroups / stationCount / scoringMode / isOfficial / isAwardingStandardMedals`; does NOT gate on `d.exists`, which only the Precision payload has); else Precision-family → `/CompetitionResults/GetResultsList`.
- `renderAll()` dispatches: `renderSpringskytte` / `renderFaltskytte` / the default flat Precision list.
- **`renderFaltskytte`** groups every shooter into its **weapon class** (`getWC` → first char: C/B/A/R/L/M) and renders **one combined leaderboard per weapon group**, ranked across all sub-classes by the server tiebreaker keys (Normal: hits→figures→poängmål; Poäng: points→poängmål). The shooter's real sub-class stays in the Klass column. **Deliberate:** the top row is the best raw score in the group, NOT the official per-class placement; medals (Std) come from the server's medal grouping so they can sit below row 1. Station columns carry `rb-series-col` so they auto-hide on narrow screens.

**Why Fältskytte was broken before:** the board only branched `isSpringskytte ? Spring : CompetitionResults`. A Fältskytte comp hit the Precision endpoint (empty `PrecisionResultEntry`), bailed on `!d.exists`, and `renderAll` parsed per-series `shots` JSON that Fältskytte has no concept of. The *button* was already wired for Fältskytte (published case at `Competition.cshtml`'s results section is type-agnostic; pre-publish DB-check already fetched `GetFaltskytteResults`) — only the rendering was missing.

**Weapon-class filter is a multi-select + Växla (all disciplines):**
- The old single `<select id="rbWcSelect">` was replaced by a **checkbox per weapon group** (`#rbWcFilter`, built from `configuredWCs`) + a **Växla** toggle (`#rbCycleToggle`) + a current-class label (`#rbCycleNow`). State moved from `selectedWC` (string) to `selectedWCs` (object map) plus `cycleMode`/`cycleIdx`/`cycleTimer` (`CYCLE_MS=12000`).
- **Växla off** → selected classes stacked. **Växla on** → rotates one weapon class at a time every ~12 s via `cycleTick`, skipping empty classes (`orderedSelectedPresent`). `getFiltered()` returns groups for `currentDisplayWcs()` (all selected, or just the current one when cycling).
- Rotation timer pauses/resumes alongside the poll in the `visibilitychange` handler.
- **Persistence:** `localStorage` per comp — `hpsk_rb_wcs_<compId>` (selected classes JSON) and `hpsk_rb_cycle_<compId>` (`'1'`/`'0'`). `populateWcFilter` only rebuilds the checkbox DOM when the class list (`data-sig`) changes, so user ticks survive each 15 s poll.

**Badge:** `updateStatus()` shows OFFICIELLT (orange) when `isOfficial`, else LIVE (green, pulsing). For Fältskytte `isOfficial` = competition `faltskytteResultsOfficial`. "LIVE" means preliminary, not "in progress".

**No backend changes** — reuses `GetFaltskytteResults` / `GetSpringskytteResults` / `GetResultsList` (recompute live each call, incl. merges/medals/Särskjutning).

**Standalone page (`/live`):** `Views/ResultBoard.cshtml` reads `?c=<id>`, looks up the competition via `Umbraco.Content(id)` (require `ContentType.Alias == "competition"`), reproduces the board params server-side (competitionName, competitionType, numberOfSeriesOrStations/numberOfFinalSeries, and `configuredWCs` parsed from `shootingClassIds` via `HpskSite.Models.ShootingClasses.GetById().Weapon`), then calls `rbBoardScript(...)`. It is **public** (a TV/Chromecast can't log in — unlike the staff-gated `/skjutledare` & `/station`) but only renders when `showLiveResults && !isExternal`; otherwise a minimal "Live-resultat är inte tillgängligt" page. The 3 triggers on the competition page are now `<a href="/live?c=@Model.Id" target="_blank" rel="noopener">` anchors (no `window.open`, so no popup-blocker and bookmarkable; F5 reloads and restores filter/Växla from localStorage). `@Model.Id` interpolates in all 3 contexts incl. the JS-injected one inside the `<text>` block. Views deploy without a rebuild.

**Tiled multi-view + castable URL params (`?tiles=`, `?wc=`, `?vaxla=`):** the board has a layout selector (`#rbLayoutSel`: Lista / 1–4 rutor) next to Växla. State: `layoutMode` ∈ {`list`,`grid`} + `tileCount` (1–4). In grid mode `renderGrid` builds a CSS-grid dashboard of **weapon-group panels** (one tile = one weapon group, combined-ranked); the **largest group by participant count is the hero**. Tile layouts: 1=full, 2=top/bottom, 3=hero-left-full-height + right split top/bottom (explicit `nth-child` placement), 4=2×2. If more selected groups than tiles, **Växla rotates the overflow** through the tiles (`cycleIdx` offset). Tile head (`Vapengrupp X (N)`) is shown for Precision/Springskytte; Fältskytte tiles self-head via `renderFaltskytte`. The flat-Precision renderer was extracted from `renderAll` into **`renderInto(wr,groups)`** so it can target any container (the whole wrap in list mode, a tile body in grid mode); `renderAll` is now the orchestrator (`list` → `renderInto(#rbTableWrap, getFiltered())`, `grid` → `renderGrid`). **URL params drive everything for casting:** `?wc=C,A` (filter — applies to list too), `?tiles=1|2|3|4` (grid), `?vaxla=1`. On load, URL params **override** localStorage (deterministic cast links); any on-screen control change writes back to the URL via `history.replaceState` (`syncUrl`) so the address bar is always copy-ready. Layout persisted in `localStorage` `hpsk_rb_layout_<compId>`.

**Operator setup (Umbraco backoffice, per environment — same shape as /skjutledare, /station):** create template `ResultBoard`, doctype `resultBoard` (no properties, allowed under Home, default template `ResultBoard`), and publish a `resultBoard` content node with URL alias **`live`**. Without the node, `/live?c=…` 404s and all 3 triggers dead-link. No SQL, no doctype properties.

### Fältskytte: two QR codes per station + station-layout secrecy (2026-05-27)

**What:** Each Fältskytte station now carries **two** purpose-built QR codes, and station layouts are kept secret (not browsable/enumerable).

- **QR-1 — Förutsättningar (on the station card):** opens a **read-only** view of that station's conditions + a **static per-figure visibility timeline** (green show/hide bands, no clock). **No login.** Served at **`/station?t=<token>`** where the token is an opaque `IDataProtector` payload (`"<compId>:<station>"`, protector purpose `"Faltskytte.StationInfoQr.v1"`) — non-enumerable + non-forgeable, so a shooter can't change a station number to preview others. Rendered **server-side** by the new partial `Views/Partials/FaltskytteStationInfoStatic.cshtml` (typed `FaltskytteStationConfig`; C# port of the Tidur's `tmrFigBands`; bands use inline styles + `InvariantCulture` percentages — avoids the "top-level `<style>` in a partial 500s" trap).
- **QR-2 — Result entry (separate cut-out, placed by the Målgrupper):** opens `/station?c&s`. **Login required.** Shows an **adaptive landing**: a *Stationschef* button if the user has staff access + one button per patrol they're in (labelled `Vapengrupp · Patrull N · HH:mm`) when `faltskytteSelfServiceResults` is on. 0 → "ingen behörighet"; 1 → straight there (`?role=chief` or `?p=<patrolId>`); 2+ → chooser. **Fixes the dual-role case** (functionary who is also a shooter). **No sticky memory — select every scan** (the old `hpsk_faltselfservice_*` localStorage auto-resolve was removed; entering under the wrong class is the failure mode to avoid).

**Secrecy lock-down:** the old `/station` `else` branch rendered the full layout (`StationInfoCard`) to **anyone** with `?c&s`, and `GetStationConfig` was an **unauthenticated** endpoint returning the **whole** competition. Both fixed: the leaky `else` is gone (logged-out → login CTA, logged-in non-participant → "ingen behörighet", never the layout), and **`GetStationConfig` is now gated by `CanReadStationAsync`** (staff or self-service participant). QR-1 renders server-side so it needs no endpoint. `StationInfoCard.cshtml` is now **orphaned** (delete in a follow-up). Residual: a registered participant could still read other stations via the API — acceptable ("hard, not impossible"); tightening it to per-allowed-station is a follow-up.

**Key code:**
- `CompetitionTypes/Faltskytte/Controllers/FaltskytteController.cs` — injected `IDataProtectionProvider`; `GetStationConfig` now `async` + `CanReadStationAsync` gate; new `GetStationInfoQr(competitionId, stationNumber)` (staff-gated: mints token, builds absolute `/station?t=…` URL via `Request.Scheme/Host`, returns the QR PNG in one call so the print stays synchronous); extracted `QrPng` helper shared with `GenerateQrCode`.
- `Views/StationPage.cshtml` — injects `IDataProtectionProvider`; first body branch handles `?t=` (decode in try/catch → "Ogiltig länk"); the entry path is now a destinations model (`hasChief` + `memberPatrols`, dropped the old `!canEnterResults` gate so staff also get patrol options); `?role=chief` / `?p=` resolution; chooser/login/no-access branches replace the picker + leaky `else`.
- `Views/Partials/FaltskytteStationInfoStatic.cshtml` (NEW).
- `Views/Partials/_FaltskytteConfiguratorScript.cshtml` — `faltCfgPrintStation` prints QR-1 (`GetStationInfoQr`) on the card + QR-2 (`GenerateQrCode?url=/station?c&s`) in a dashed `page-break-before` cut-out.
- Reuses: `FaltskytteSelfServiceQueries` (patrol resolution, cursor advance/lock — unchanged), `IDataProtector` (configured in `Program.cs:54-56`, keys persisted so printed tokens survive recycles).

**Deploy:** adds C# → **full publish/rebuild required** (not views-only). No new Umbraco node/doctype/property; no SQL.

### Fältskytte: Stationer tab, Patrullista page, flow statistics (2026-05-27)

**"Stationer" tab** — `Views/CompetitionManagement.cshtml`, gated `is "Faltskytte" or "MagnumFalt"`, **position 3 (before Resultat)** → partial `Views/Partials/FaltskytteStationerManagement.cshtml`. One **card per station** with figure thumbnails (instant visual association), an assignable **station chief** (member picker w/ phone autofill + free-text fallback), live **last-patrol + time** (off `EnteredAt`, so corrections don't reorder) and **completion** (entries / total patrol-member rows), per-station + "all" **station-card printing**, and an **"Öppna konfigurator"** link (to the attached `_attachedConfigId`). Polls `GetStationOverview` every 15 s (visibility-aware). Endpoints in `FaltskytteController` (staff-gated via `IsAuthorizedForCompetition`): `GetStationOverview`, `SaveStationManagers` (writes the `faltskytteStationManagers` property), `GetMemberContact` (phone autofill).

**Station-card print** — routed `Controllers/FaltskyttePrintController.cs` (`/faltskytte/stationskort/{competitionId}[?station=N]`, staff-gated) → `Views/FaltskyttePrintStationCards.cshtml`; server-renders `FaltskytteStationInfoStatic` per station + QR-1 (`GetStationInfoQr`) + QR-2 (`GenerateQrCode`). The print passes **`CompactNoTargets=true`** → Förutsättningar only; figures + timeline are revealed only by scanning QR-1. This is where the QR codes finally print (the `/faltkonfig` editor has no competitionId).

**Patrullista / send-off page** — routed `Controllers/FaltskyttePatrolListController.cs` (`/patrullista/{competitionId}`) → `Views/FaltskyttePatrolList.cshtml`. Flat patrol-number-ordered list, chromeless, **polls `GetPatrolListState` every 10 s**. Public read-only **wall screen** (clubhouse) + a **send-off mode for logged-in staff** (`CanSendOff`): starters tick patrols off with "Skicka iväg" / "Ångra", which stamps `FaltskyttePatrol.DepartedAt` via `SetPatrolDeparted` (staff-gated, targeted UPDATE). **"NÄST PÅ TUR"** = lowest patrol number with `DepartedAt` null; departed dimmed; "senaste utgång + min sedan" rhythm aid; auto-scrolls the next card into view. Endpoints on `FaltskytteController`: `GetPatrolListState` (public), `SetPatrolDeparted` (staff). **New `DepartedAt DATETIME NULL` column on `FaltskyttePatrol`** — run `Migrations/add-departedat-to-faltskyttepatrol.sql` in SSMS **before deploying** (every FaltskyttePatrol query selects the column once the model property exists). Gated on `faltskyttePatrolsPublished`; no login for the wall screen (the old clock-based "next" was dropped — schedules slip, so it's manual tick-off).

**Flow statistics** — routed `Controllers/FaltskytteStatsController.cs` (`/faltskytte/statistik/{competitionId}`, staff-gated) → `Views/FaltskytteStats.cshtml`, linked from the Stationer tab. Per-(patrol,station) completion = max `EnteredAt`; per-patrol **leg time** (gap from the previous station, time-ordered) → avg per station = **bottleneck**. Charts (Chart.js 4.4.0, same CDN as the dashboard): bottleneck bar, patrol-flow scatter (time × station, by weapon group), throughput bar; + summary cards + per-station table. Phase-2 flow viz on already-collected data.

**All four pages are routed MVC `Controller`s — no Umbraco node/doctype needed** (pattern from `FaltskytteConfigurationEditorController`; chromeless `Layout = null` + a typed `@model`, except the Stationer tab which is a partial in CompetitionManagement). Build is green on these but Razor views are **runtime-compiled** — `dotnet build` does NOT validate them (a named-tuple bug once shipped that way; now `int[]`), so load each page once after deploy. Adds C# → full rebuild.

## Common Patterns

### Model Usage
**✅ CORRECT** - Use auto-generated models:
```csharp
@inherits UmbracoViewPage<ContentModels.AdminPage>
```

**❌ WRONG** - Don't create custom page models for simple pages:
```csharp
public class AdminPage : BasePage { } // Only for complex business logic
```

### Security Checks
```csharp
await IsCurrentUserAdminAsync()           // Site admin check
await IsClubAdminForClub(clubId)          // Club-specific admin check
var managedClubs = await GetManagedClubIds() // Get user's clubs
```

### Content Operations
```csharp
// Create content
var content = _contentService.Create(name, parentId, documentTypeAlias);
content.SetValue("propertyAlias", value);
_contentService.SaveAndPublish(content);

// Delete content (unpublish first)
_contentService.Unpublish(content);
_contentService.Delete(content);
```

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

## Deployment

**CRITICAL:** Always reference `Documentation/PRODUCTION_DEPLOYMENT_GUIDE.md` - **NEVER give deployment advice without reading it first**.

- Full deployment process is documented with exact commands
- Self-contained build required (Simply.com doesn't support .NET 9)
- **MUST remove wwwroot/media/ before upload** to prevent data loss
- Command: `dotnet publish HpskSite.csproj -c Release -r win-x86 --self-contained -o "C:/temp/publish"`

## Implementation Status

### Completed ✅
- **Fältkonfigurator Approval Workflow (2026-05-25)** - Banläggare-gated approval lifecycle (Draft → PendingApproval → Approved). Owners pick a specific Banläggare via dropdown; email notification sent with deep-link; only that Banläggare can approve. Approved configs lock JsonBlob (metadata stays editable). Listing + competition picker show Godkänd / Väntar badges; "Väntar på godkännande" filter visible to Banläggare only. See "Fältkonfigurator Approval Workflow" section.
- **Fältskytte Standalone Configurations + SHB Shoot-Time Suggestion + Svårighetsgrad (2026-05-24 → 26)** - New `/faltkonfig` listing + `/faltkonfig/{id}/redigera` editor for fristående station configurations with visibility tiers, collaborators, SecretUntil sekretessgrind, link-or-copy station import + linked-station reload UX. Replaces the legacy in-competition `FaltskytteStationConfigModal`. Adds a saved-config picker on the wizard / edit modal + a Konvertera-button for legacy inline configs. SHB-derived suggested skjuttid per station/class (6 skott × per-shot floor + tillägg, mörker ×1.30; **Normal** mode uses per-figure greedy 6-shot allocation, **Poangfält** uses worst-case 6 × max-per-shot), Svårighetsgrad % badge per station (100 % = SHB minimum), per-målgrupp distance slider, Mörkerfältskjutning toggle, Figurkatalog grouped + filtered by SizeGroup, mobile-responsive picker modal. **Tävlingstyp** dropdown moved from the competition wizard/editor into the configurator (source of truth); wizard + edit modal show a read-only badge. See "Fältskytte Standalone Configurations" + "Fältskytte SHB Shoot-Time Suggestion + Svårighetsgrad" sections.
- **Competition URLs & Routing (2026-05-22)** - Custom URL provider + content finder for club-hosted, region-hosted, SM, and Landsdel competition URL shapes (see "Competition URLs & Routing" section above). Includes at-least-one-host guard across wizard/edit modal + their backends, and isClubOnly auto-disable when no club is selected.
- **Controller Refactoring (2025-10-28)** - AdminController split into specialized controllers with AdminAuthorizationService
- **Authorization Security Fixes (2025-11-02)** - Comprehensive security audit and fixes across 6 areas (see [Documentation](Documentation/AUTHORIZATION_SECURITY_AUDIT.md))
- **Login & Registration System (2025-11-02)** - Complete overhaul with email notifications, smart redirects, approval workflow (see [Documentation](Documentation/LOGIN_REGISTRATION_SYSTEM.md))
- **Club System (2025-10-30/31)** - Document Type based with migrations to ClubService and numeric clubId (see [Documentation](Documentation/CLUB_SYSTEM_MIGRATIONS.md))
- **Club Admin Panel** - Events, Competitions, Members, Training Groups, Settings tabs with proper authorization
- **Training System (Skyttetrappan)** - Backend, UI, admin interface, training groups, step approval workflow
- **Training Groups System (2026-02)** - Database tables, service, controller, UI in both TrainingStairs and ClubAdminPanel. Includes group lifecycle (create/edit/deactivate), member/trainer management, step-by-step approval, group email messaging
- **Skjutledare (Range Master) Role (2026-02)** - New club-level trust role. Can approve training steps and manage competitions for their club. Member group pattern `Skjutledare_{ClubId}`. Managed via club admin panel Members tab
- **Certifications System (2026-04-29)** - SPSF-registered roles (Föreningsinstruktör, Kretsinstruktör, Riksinstruktör, Vapenkontrollant, Banläggare). Personal cert stored in `MemberCertifications` table; appointment via member groups. Hierarchy-aware grant authority. Statistik integration on club + regional + admin tabs. Members-tier panels on Club and RegionalPage. See [Documentation](Documentation/CERTIFICATIONS_SYSTEM.md). **Manual operator steps required:** run `Migrations/create-member-certifications-table.sql` and add `area` Textstring property to `regionalPage` doctype.
- **Records and Champions (2026-04-30)** - Klubb-/kretsrekord (`CompetitionRecords` table with IsCurrent + history chain, current best per scope/class) and klubb-/kretsmästartitlar (`CompetitionChampions` table, per-year, manual). Both for Precision/MagnumPrecision/Milsnabb. New `Rekord` tab on Club and Region pages, new `Mästare` tab in Club + Region admin panels. Read-only panels embedded in member directory and region home. See [Documentation](Documentation/RECORDS_AND_CHAMPIONS_SYSTEM.md). **Manual operator steps required:** run `Migrations/create-competition-records-table.sql` and `Migrations/create-competition-champions-table.sql`.
- **Club & Region Tävlingar tabs (2026-05-11)** - Simplified per-scope competition lists. Club page tab shows the club's `isClubOnly` comps filtered by year. Region page tab combines region-hosted comps (clubId unset, regionalFederation matches) and non-`isClubOnly` invitation comps from clubs in the region. Past comps with an official `competitionResult` child node link straight to `/competitions/.../resultat/`; everything else (and all `isExternal` comps) link to the competition page. Required Fältskytte fix: `FaltskytteController.PublishResults` now also creates the `competitionResult` page node so `/resultat/` exists for Fältskytte too (Springskytte already did this). Legacy Fältskytte comps need to be re-published once to get the page.
- **Training Scoring System (2025-10-31)** - Complete with dashboard, Chart.js visualizations, unified results (see [Documentation](Documentation/TRAINING_SCORING_SYSTEM.md))
- **Competition Series System** - Full CRUD with CKEditor 5
- **Competition Admin System** - Full CRUD operations with role-based access
- **Bug Report Feature (2025-11-02)** - Site-wide bug reporting with image upload
- CKEditor 5 integration (migrated from TinyMCE)
- User authentication and role-based access control
- Logo and navigation implementation
- Responsive UI with Bootstrap

### Pending ⏳
- Training member properties setup in Umbraco backoffice
- Competition registration enhancements
- Competition results system testing
- Finals system testing
- Club calendar integration (Phase 2)
- Member personal pages & statistics

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

### Other Documentation
See [Documentation/README.md](Documentation/README.md) for complete documentation index.

---

**Documentation Version:** 2025-11-06 (Production Deployment)
**Umbraco Version:** 16.2
**Build Status:** ✅ Compiles (0 errors)
**Deployment Status:** ✅ Production deployment successful
**Last Updated:** Added production deployment guide and resolved ModelsBuilder issues
