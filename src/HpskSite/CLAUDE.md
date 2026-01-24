# Claude Development Notes - HPSK Site

## Project Overview
Umbraco v16.2 project for HPSK shooting club featuring member management, club administration, training system (Skyttetrappan), and competition management.

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
- 5 endpoints for competition registration management
- GET: GetCompetitionRegistrations, GetActiveCompetitions
- POST: UpdateCompetitionRegistration, DeleteCompetitionRegistration, ExportCompetitionRegistrations
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
**Member Groups Pattern**: `ClubAdmin_{ClubId}` (e.g., ClubAdmin_1098)

**Permission Hierarchy:**
1. Site Administrators - Full access to all clubs
2. Club Administrators - Access only to assigned club(s)
3. Regular Users - No admin access

**Key API Methods** (AdminController.cs):
```csharp
GetClubsAsContent()          // Retrieves clubs from content tree
IsClubAdminForClub(clubId)   // Check if user is club admin
GetManagedClubIds()          // Get clubs user can administer
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
9 progressive training levels (🥉 Nybörjartrappa Brons → 🏅 Rekordtrappan), 74 total steps.

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
**Admin:** CompleteStep, ResetProgress, GetMemberProgress?memberId=X

### Implementation Status
✅ Models, API, UI, Admin Interface
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
- Series-by-series score entry
- Match history and leaderboards
- Support for guests (non-registered participants)

**Data Storage:** Database tables `TrainingMatches`, `TrainingMatchParticipants`, `TrainingMatchScores`
**Controller:** `TrainingMatchController.cs`
**UI:** `Views/Partials/TrainingMatchScoreboard.cshtml`

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
- Competition must have `registrationFee` property > 0
- Payment button only shows when both conditions are met

**See Also:**
- [SWISH_PAYMENT_SETUP.md](Documentation/SWISH_PAYMENT_SETUP.md) - Complete setup guide
- [SWISH_PAYMENT_IMPLEMENTATION.md](Documentation/SWISH_PAYMENT_IMPLEMENTATION.md) - Implementation details

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
- **/training-stairs** - Training system interface
- **/login-register** - Login and registration page
- **/user-profile** - User profile with dashboard, training results

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
- **Club Admin Panel (Phase 1)** - Events, Competitions, Settings tabs with proper authorization
- **Training System (Skyttetrappan)** - Backend, UI, admin interface
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
