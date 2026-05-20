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
- **competitionResult**: add `subCompetitionIsOfficial` True/False property (optional, default false, label "Deltävling publicerad som officiell"). Powers the independent Deltävling publish toggle. Without it, the Publicera button on the Deltävling section returns an error and the second public "Visa resultat" button cannot appear. Added 2026-05-17.
- **competitionResult**: add `subCompetitionMergeConfig` Textarea property (optional, label "Deltävling – sammanslagningskonfiguration (JSON)"). Stores the Deltävling's own class-merge config — separate from the main `mergeConfig` so the subset analyses its own <5-shooter classes. Without it, sub-comp Sammanslagning silently no-ops. Added 2026-05-17.
- **competitionResult**: add `classNameOverrides` Textarea property (optional, label "Anpassade klassnamn (JSON-dict)"). Stores admin-edited display names for class groups — JSON dict mapping auto-generated combined name (e.g. "C2+Dam+Vet") to custom name (e.g. "C2 Allmänt"). Empty value = no overrides. Without this property, the pen-icon rename feature on the result page silently no-ops. Added 2026-05-19.
- **competitionResult**: add `subCompetitionClassNameOverrides` Textarea property (optional, label "Deltävling – anpassade klassnamn (JSON-dict)"). Same shape as `classNameOverrides` but applied only to the Deltävling result list (`?sub=true`). Without it, sub-comp class-name overrides silently no-op. Added 2026-05-19.
- **competitionResult**: add `faltskytteShootOffConfig` Textarea property (optional, label "Fältskytte – särskjutnings-station (JSON)"). Stores a single station config (with per-weapon-class variants) used for Fältskytte/MagnumFält Särskjutning. Without it, the "Konfigurera särskjutnings-station" save silently no-ops. Added 2026-05-20.
- **competitionResult**: add `subCompetitionFaltskytteShootOffConfig` Textarea property (optional, label "Deltävling – Fältskytte särskjutnings-station (JSON)"). Same as above for the Deltävling pool. Added 2026-05-20.

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
