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
- Used by: Views/Partials/CompetitionRegistrationManagement.cshtml, Views/Partials/CompetitionExportManagement.cshtml, Views/CompetitionManagement.cshtml

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

### Medlemmar tab — Åtgärder menu + keys register (2026-07-05)
The Medlemmar tab's action buttons (formerly a flat 8-button row) are consolidated into a
single **"Åtgärder"** Bootstrap dropdown on the heading row (grouped: Medlemmar /
Kommunikation & avgifter / Listor / Register / Behörigheter). No permanent
primary button — members self-register, so *Lägg till klubbmedlem* is just a menu item.
The audience is low-computer-literacy club admins, so everything stays visible behind a
*labelled* menu (never a bare hamburger).

Other changes shipped together:
- Removed the top "Klubbadministration – Du kan hantera…" info banner.
- **DPA acceptance:** while unaccepted, the warning gate (`#dpaGate`, `loadDpaGate()`)
  still shows as a prominent banner. Once accepted it no longer renders a banner — the
  status moves into a **"Personuppgiftsbiträdesavtal"** item in the Åtgärder menu that
  opens `#dpaStatusModal` (version/date/who + link to the avtal).
- Removed the *Medlem sedan* field from the **create-member** modal (it belongs to club
  membership data, not member personal data; the edit-member modal keeps it).
- **Club-wide keys/codes register:** Åtgärder → "Nycklar & koder" opens `#clubKeysModal`
  (overview + CRUD over all the club's `MemberAccessKey` rows). New read endpoint
  `MemberAccessKeyController.ListForClub(clubId)` + `MemberAccessKeyService.GetForClub`
  (site/club-admin gated); add/edit/delete reuse the existing `SaveKey`/`DeleteKey`.
  View-only change except the two backend methods → full rebuild to deploy those; the
  `.cshtml` also changed.

### Club admin panel navigation — grouped vertical rail (2026-07-05)
The 13 admin sub-tabs (formerly a horizontal `nav-tabs` bar that wrapped to 2–3 rows) are now
a **grouped vertical rail** (`ClubAdminPanel.cshtml`). Layout is a `.row`: a `col-lg-3` rail
(`.admin-rail`, `nav nav-pills flex-column`, sticky) grouped under four headings — *Kalender &
tävlingar* / *Medlemmar* / *Utmärkelser* / *Klubben* — plus a `col-lg-9` content column holding
the unchanged `#adminTabContent`. **All tab-button ids + `data-bs-target`s are unchanged**, so
the many `shown.bs.tab` lazy-loaders (events/members/marken/settings/…) keep working.
- **Styrelsearbete** is in the rail's *Klubben* group (moved out of the Medlemmar Åtgärder
  menu 2026-07-05) — but as a navigate-away `<a href="/styrelse…">` link (external-arrow icon),
  not a tab. The mobile picker handles it via a URL-valued `<option>`: the sync IIFE does
  `window.location` when the selected value doesn't start with `#`.
- **Responsive:** rail shows at **≥ lg**; below lg (phones + portrait tablets) it's hidden
  (`d-none d-lg-block`) and replaced by a full-width native `<select>` picker
  (`#adminTabMobileSelect`, `d-lg-none`) with the four groups as `<optgroup>`s. A small IIFE at
  the end of the panel wires the select ↔ tabs both ways (change → `bootstrap.Tab…show()`;
  `shown.bs.tab` → sync select value). Chose lg (not md) so data-dense tables get full tablet width.
- Member-facing club page tabs (`ClubNavigation.cshtml`, 8 tabs) were intentionally left as a
  horizontal top nav — top-nav suits a public browse page; the rail signals "admin console".
- View-only change (no rebuild). Rail CSS lives in the panel's `<style>` block (`.admin-rail*`).
  See memory [[member-actions-menu]] for the low-literacy-admin UX rules driving both changes.

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
**Member:** StartTraining, CompleteStep (own next step, levels 4+ only), UncompleteStep (own self-reported step)
**Admin/Trainer/Skjutledare:** CompleteStep (any step), GetMemberProgress?memberId=X
**Site Admin only:** ResetProgress

### Step Approval Authorization
Training step approval (`CompleteStep`) uses a four-tier authorization check:
1. **Site Admin** — can approve any member
2. **Trainer** — can approve members in their active training group (`IsTrainerForMember`)
3. **Skjutledare** — can approve members at their club, even without an active training group (`IsSkjutledareForMember`)
4. **Club Admin** — can approve members at their club (`IsClubAdminForClub`)

The same tiers apply to `GetMemberProgress` when viewing another member's progress.

### Self-service from level 4 up (2026-08-21)

**A shooter may tick their OWN steps from level 4 (Guldmärkesskytt 1) and up. Levels 1-3 stay
functionary-approved.** `TrainingDefinitions.SelfServiceMinLevel` / `IsSelfServiceLevel` is the ONE
place that boundary is expressed — don't re-test `levelId >= 4` inline.

**⚠️ The reason is the märke, not the shooter's rank.** Finishing level 1, 2 or 3 calls
`MarkenLedgerService.SyncTrappaBadgesAsync`, which mints the official Pistolskyttemärke (brons /
silver / guld) into the märkesledger stamped with the approving functionary. Nobody signs off their
own märke, so those steps cannot be self-reported. Levels 4-9 mint nothing and are the shooter's own
bookkeeping. An earlier proposal gated self-service on *holding* the guldmärke — that is circular:
the trappa is what awards it, so the gate would open exactly when the beginner ladder is already
finished. Express the rule as "does this step have an official consequence", never as
"what does this shooter hold".

**Server rules** (`CompleteStep` → `CanSelfReport`): the caller must be the target member, the level
must be self-service, and the step must be the member's **current** position — so the ladder cannot
be skipped to Rekordtrappan. The refusal message explains which rule bit; a beginner is told who
approves their step instead of getting "Access denied".

**Other behaviour differences for a self-reported step:**
- `StepCompletion.SelfReported = true` (absent in older stored JSON → false, which is correct —
  everything recorded before this existed was functionary-approved).
- `InstructorName` stays **null**: nobody signed it off. Don't stamp the shooter's own name there.
- **No approval email.** Mailing the shooter about something they just ticked is noise.
- The badge sync is additionally guarded on `!isSelfService` — belt and braces if the boundary
  ever moves.

**`UncompleteStep`** exists for the mis-click, and is deliberately restricted to levels 4+: undoing a
level 1-3 step would leave an already-minted märke behind with nothing backing it, so correcting those
stays a site-admin `ResetProgress` matter. **Whoever may approve a 4+ step may also undo it** — the
same four tiers, via the shared `ResolveStepAuthorityAsync` helper, so approving and undoing cannot
drift apart. Without that symmetry a trainer's mis-tick could only be cleared by a site admin. A
shooter may undo only their **own self-reported** steps: a functionary's sign-off is not theirs to
withdraw.

Verified 40/40 `hpsk-verify/trappa-selfservice-verify.mjs`. **⚠️ Read its fixture note before editing
it:** it takes the shooter to level 4 with ONE functionary-approved step (4,1), because
`CalculateCurrentPosition` takes the highest completed step and `SyncTrappaBadgesAsync` needs ALL of a
level's steps — so the whole run mints no märke and is fully reversible by a club admin. Going the long
way (18 steps through the beginner ladder) would mint three märken and need a site admin to clean up.
**There is no `admin.claude@pistol.nu` in dev** — a first version of the script "passed" its negative
assertions while every admin call was silently anonymous. The functionary is `builder.claude`
(ClubAdmin_2604, so authorized for Haaplinge GoAss members).

**View** (`TrainingStairs.cshtml`): `SELF_SERVICE_MIN_LEVEL` must match the C# constant.
`canSelfReport()` mirrors `CanSelfReport` — the server is still the authority. The step modal shows
a "Jag har klarat det" button on a self-service step, an **Ångra** button on the newest self-reported
one, and on levels 1-3 an explanation of who approves instead of a dead button.

**⚠️ Fixed while here: `currentMemberProgress.isStepCompleted(...)` was always undefined.** The
payload is plain JSON with no methods, so `isCompleted` evaluated false for **every** step — the
ladder never showed a completed step and the modal's status badge was always wrong. Replaced by the
`myCompletion` / `hasCompletedStep` / `isMyCurrentStep` helpers. Anything reading progress
client-side must go through those.

Adds C# → full rebuild. No migration, no doctype property, no Umbraco node.

### Veteranen som redan har märket börjar på nivå 4 (2026-08-21)

Självservice från nivå 4 löser ingenting för den som **redan** haft guldmärket i tjugo år: positionen
härleds ur avklarade steg, så en nyregistrerad veteran satt fast på Nybörjartrappa Brons steg 1 och
skulle behöva skjuta om hela nybörjartrappan för att komma åt sin egen nivå.
**`TrainingBadgeCreditService` räknar nybörjartrappan som avklarad utifrån en hållen
Pistolskyttemärkes-valör** — märket ÄR beviset. Registreras av klubben som förut under
**Klubbadministration → Märken** (`Marken/AwardBadge`), så ingen ny adminyta behövdes.

**Krediten är HÄRLEDD, aldrig lagrad.** `MemberProgress.SaveToMember` **strippar** varje
`FromBadge`-steg och tjänsten återskapar dem vid läsning. Följderna är hela poängen:
- Ett märke som makuleras, avvisas eller rättas tar sin kredit med sig. Lagrade steg hade lämnat en
  nybörjartrappa stående på ingenting, och nivå 1-3 kan inte ångras (se ovan) — alltså ett läge bara
  en sajtadmin kunde reda ut.
- **Ingen läsning skriver.** Första utsågan sparade medlemmen vid varje sidladdning.
- Valören avgör hur långt: silver krediterar nivå 1-2 (nivå 3 är fortfarande funktionärens), guld
  krediterar 1-3 → position 4/1. **Alla** nivåer upp till valören, inte bara den den mappar mot — den
  som håller guld har passerat brons och silver.

**⚠️ Krediten måste appliceras överallt där en trappa materialiseras**, annars säger sidorna emot
varandra: `GetTrainingOverview` (både rostern och den inloggade), `GetMemberProgress`, `GetLeaderboard`,
och **`CompleteStep`/`UncompleteStep` — positionsgrinden läser `CurrentLevel`/`CurrentStep`**, så utan
krediten står veteranen på 1/1 och kan aldrig markera sitt riktiga steg. Roster och topplista går via
`ApplyManyAsync` + `MarkenLedgerService.GetHighestBaseValorForMembersAsync` — **EN** fråga för hela
listan, chunkad på 1000 (`IN (@0)` tar slut kring 2100 parametrar och gör det tyst).

**⚠️ Ping-pong-skyddet i `SyncTrappaBadgesAsync` är inte teoretiskt — det är bärande på en väg.**
Den hoppar över en nivå vars steg är `FromBadge`. `MarkenController.SyncTrappaForMemberAsync` läser
den LAGRADE JSON:en och kan aldrig se en kredit, men **`TrainingController.CompleteStep` skickar in
den krediterade in-memory-progressen**. Utan guarden: en funktionär godkänner ett nivå-1-steg för en
veteran som håller guld → hela nivån ser klar ut → **brons och silver myntas som officiella märken**,
stämplade "Automatiskt från Skyttetrappan", utan funktionär och utan att något skjutits här.

**`StepCompletion.Source`** ersätter den tidigare `SelfReported`-boolen: tre äkta olika provänser
(`Functionary` / `SelfReported` / `Badge`) hålls inte isär av två bool-flaggor. `SelfReported` och
`FromBadge` finns kvar som härledda properties, så klienten läser dem oförändrat ur payloaden.
**Null Source i äldre lagrad JSON = Functionary**, vilket är rätt.

**⚠️ `AchievedYear` slår `AchievedDate` när de säger emot varandra.** `AwardBadge` stämplar
`AchievedDate = DateTime.Now` även för ett märke från 1998, så datumet är en bokföringsstämpel och
ÅRET är fakta. Att lita på datumet daterade veteranens hela nybörjartrappa till idag.

Verifierat 31/31 `hpsk-verify/trappa-veteran-verify.mjs` (silver → nivå 1-2, guld → 4/1, att inga
märken tillverkas av krediten, och att märket bort tar krediten med sig men lämnar skyttens egna steg).
Hela fixturen är reversibel just därför att krediten är härledd. Regression: 40/40 self-service.
**Ej täckt:** att ett *overifierat* märke inte krediterar — regeln finns (`Status = Verified`), men
`AwardBadge` skapar alltid Verified och ingen endpoint sätter annan status på en valör, så tillståndet
går inte att framkalla via API:et.

Adds C# → full rebuild. No migration, no doctype property, no Umbraco node.

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

### Serieberäkning per gren — ISeriesScoreSource (2026-08-18)

**The six calculation strategies were never the precision-only part.** `IndividualSumAll` /
`BestOf` / `WinsCount` / `FixedPoints` / `DynamicPoints` / `ClubTeamBestOf` only ever read
`ShooterCompetitionScore.TotalScore` and `.XCount` — they never touch shots, series or targets, so
they work for any discipline unchanged. What was precision-only was the **fetch layer**:
`SeriesCalculationService` mapped `competitionType` to a result table with
`_ => "PrecisionResultEntry"` as the fallback. A Fältskytte series therefore queried the wrong
table, got zero rows, built empty standings, and the page **hid the container** — which reads as
"inga resultat än", not as a bug. Reported from Säters Fältserie 2026 in prod, where the strategy
was configured correctly the whole time.

**`CompetitionTypes/Common/SeriesCalculation/ScoreSources/ISeriesScoreSource`** is now the seam.
The service injects `IEnumerable<ISeriesScoreSource>` and picks the first that `Supports` the
series' competition type; a new discipline is one class plus one line in `AdminServicesComposer`.
- `PrecisionFamilySeriesScoreSource` — the old logic, lifted out unchanged. **Keeps the
  empty/unknown-type fallback to Precision**, which legacy series nodes with no `competitionType`
  rely on.
- `FaltskytteSeriesScoreSource` — Fältskytte + MagnumFält (they share `FaltskytteResultEntry`).

**Fältskytte specifics**, all mirroring what `GetFaltskytteResults` already does for one round:
- **Scoring mode is resolved PER ROUND** via `FaltskytteScoringMode.Resolve(config, property)` —
  never the competition's `scoringMode` property alone, which is a stale-able mirror (see the
  Fältkonfigurator section). Normalfält: total = träff, tie-break = figurer. Poängfält: total =
  träff+figurer, tie-break = poängmålssumman.
- **Shoot-off stations (`IsShootOffOnly`) are excluded.** A särskjutning decides a placement inside
  one round; letting it into the series total would pay a shooter twice for a tie.
- **Names and clubs come from the patrol snapshot**, not the member register, so a shooter who
  changed club mid-series is credited to the club they actually shot for in each round. Club
  standings group on an id, so the snapshot's club NAME is resolved through `ClubService`; an
  unresolvable name gets a **stable synthetic negative id** rather than collapsing every unknown
  club into one "0" bucket.
- Patrol-member lookup is chunked (the `IN (@0)` ~2100-parameter cap).
- **Per-round merge configs are deliberately NOT applied.** A class merged in round 3 but not in
  round 5 would split one shooter across two standings rows.
- **A series only carries ONE secondary number**, so normalfält's third SHB tie-break (poängmål)
  can't be represented. Träff → figurer is as far as the series ranks.

**Column headings follow the discipline.** `SeriesResultData` gained `ScoreLabel` /
`SecondaryLabel`; `CompetitionSeries.cshtml` reads them instead of hardcoding "Totalt"/"X", so a
fältserie shows **Träff / Fig.** or **Poäng / Poängmål**. A series mixing both modes takes its
headings from round 1 but still scores each round by that round's own mode.

**An unsupported discipline now says so** (`UnsupportedMessage`) instead of rendering nothing —
a Springskytte series used to be indistinguishable from "no results yet".

**⚠️ `seriesSortOrder` is not a doctype property in dev** — `CreateCompetition` logs
`No PropertyType exists with the supplied alias "seriesSortOrder"` and swallows it. The service
reads it as `GetValue<int?>(...) ?? int.MaxValue`, so rounds fall back to ordering by
`competitionDate`. Check whether prod actually has the property before relying on the manual order.

No migration, no doctype property, no Umbraco node. Adds C# → **full rebuild**. Verified 38/38 via
`hpsk-verify/faltserie-verify.mjs`, which builds a throwaway 3-round Fältskytte series seeded from
competition 5282's real rows, asserts every cell and total against SQL ground truth (plus the
shoot-off exclusion, both scoring modes, the rendered page, and a Hallandsserien-2202 regression),
then deletes the fixture. **Its teardown sweeps by node NAME, not only by the ids it captured** — a
competition that saves but fails to publish never reaches the id list and would then block
`DeleteSeries` forever; `faltserie-cleanup.mjs` does the same sweep for a killed run.

### Tävlingens FÄLT ägs av CompetitionFieldCatalog (2026-08-31)

**Ska du lägga till eller ändra ett fält på en tävling? Läs
[COMPETITION_FIELDS_HOWTO.md](Documentation/COMPETITION_FIELDS_HOWTO.md) först** — den är
checklistan. Kortversionen:

1. Markup i de modaler fältet gäller, med **`id = prefix + name`**
   (`wizard_` / `edit_` / `springEdit_`).
2. En rad i `CompetitionTypes/Common/CompetitionFieldCatalog.cs`.
3. Egenskapen på doctypen `competition` — utan den är `SetValue` en TYST no-op.
4. Bara för belopp eller udda heltal: en rad i `BeloppsFalt` / `HeltalsFalt`.

Ur katalograden följer **ifyllnaden** (`_CompetitionFieldMap.cshtml`), **serverns
sparlista** (`MapFieldNameToAlias`), **typkonverteringen** (`ConvertFieldValue`, via
`FieldControl`) och **testerna**. Ingen av dem behöver en rad till per fält.

**⚠️ Registret finns för att göra TYSTNADEN omöjlig, inte för att spara rader.** Fälten
var handskrivna på fyra till fem ställen och listorna hann glida isär. Följden var inte
dubbelarbete utan tysta fel: Springskyttemodalen tömde tävlingens klubb/krets och
förstörde dess URL; den fick nio flikar men aldrig `hpskRevealField`, så ett ogiltigt
fält på en dold flik gjorde att **Spara såg ut att inte göra någonting**; omfattningens
värden tappade diakriter så springskyttemästerskap inte räknades som mästerskap
(`StringComparison.Ordinal`); och en glömd ifyllnadsrad skrev tillbaka **tomt** vid
nästa sparning. Ingenting sa ifrån i något av fallen.

**Delade partialer — skriv inte en fjärde kopia:** `_CompetitionFormSave` (validering +
fältinsamling) · `_CompetitionFieldMap` (katalogdriven ifyllnad) · `_DateInputHelpers`
· `_ShootingClassPicker` · `_ModalSectionNav`. **Partialerna inkluderar hjälparna
själva**, aldrig värdsidan — samma regel som `_HtmlEscape`.

**⚠️ Säkerhetskontraktet vid varje refaktorering är PAYLOAD-LIKHET.** Sparvägen är en
field bag utan typkontroll (`MapFieldNameToAlias` släpper tyst okända fält), så "samma
payload in = samma beteende" är det enda som håller. Fånga före, jämför efter:
`compedit-baseline-capture.mjs` + `payload-diff.mjs` (täcker både payloaden och det
ifyllda formuläret). **⚠️ Olika fixturer prövar olika VÄRDETILLSTÅND** — baselinens sju
tävlingar har riktiga värden och missade att en default inte tillämpades på 0;
`all-surfaces` tomma fixturer fångade det.

**Markupen är medvetet INTE genererad.** 34 av 43 fält är enkla nog, men nio är
`Slot`/`Radio` och modalerna bär 25 respektive 68 villkorliga visa/dölj-regler. En
generisk renderare hade behövt uttrycka layout, ordning och villkor för tre genuint olika
gränssnitt — alltså ett mallspråk. Drift fångas i stället av kontrakten
(`wizard-catalog-verify`, `compedit-catalog-verify`), som täcker alla tre ytor.

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

**"Redigera tävlingen" dispatchas också på ETT ställe** (2026-08-28) — `window.openAdminEditModal`
låg inbakad i `AdminCompetitionsList.cshtml`, så bara sajtadmin hade den. Kretslistan anropade den
och fick **"Redigeringsfunktion kunde inte hittas. Kontakta administratören."** på varje rad, och
klubbsidan bar en nedbantad KOPIA i `Club.cshtml` som struntade i `competitionType` — en
Springskyttetävling öppnades tyst i den vanliga tävlingsmodalen. Dispatchen (`openAdminEditModal`
+ `openInternalEditModal` / `openSpringskytteEditModal` / `openAdvertEditModal`) bor nu i
**`Views/Partials/_CompetitionEditDispatch.cshtml`**, inkluderad av AdminCompetitionsList,
RegionalPage och Club. Partialen väljer bara modal — **markupen tillhör fortfarande sidan**, så en
yta som inkluderar den måste också ha `CompetitionEditModal` (`#competitionEditModal`),
`SpringskytteEditModal` (`#springEditModal`) och `CompetitionAdvertEditModal`
(`#competitionAdvertEditModal`); klubbsidan saknade den sista och har fått den. Verifierat 31/31
`hpsk-verify/competition-edit-dispatch-verify.mjs` (klickar en rad per modaltyp på alla tre ytorna),
plus 23/23 complist-shared-renderer, 25/25 comptype-filter och 60/60 row-action-menus oförändrade.

**THE COMPETITION LIST IS SHOWN ON THREE SURFACES BUT RENDERED ONCE** (consolidated 2026-08-18) —
`AdminCompetitionsList.cshtml` (site), the Tävlingar sub-tab in `RegionalAdminPanel.cshtml` (krets)
and the Tävlingar tab in `ClubAdminPanel.cshtml` (klubb). All three call the same
`GetCompetitionsList` and now draw every row through **`Views/Partials/_CompetitionListRenderer.cshtml`**
(`hpskRenderCompetitionRow` / `hpskRenderCompetitionRows` / `hpskRenderCompetitionTable`, plus the
status / registration / scope / extern badge helpers). **Include that partial — and
`_CompetitionTypeCatalogue.cshtml` — on any new surface rather than writing a fourth copy.** They
previously hand-rolled row markup, badges and status independently, which is how the discipline-dropdown
bug shipped on all three at once while the club page also kept a stale yellow badge (same trap as
`startlist-dual-renderer`, with three renderers instead of two).

**Each row's actions are ONE Åtgärder menu, not a strip of icon buttons (2026-08-24).** Five
look-alike outline icons were unreadable and the last of them scrolled off on a tablet; the row
menus at the registreringsbord already solved this. Add a row action as an `<li>` inside that menu.
The series lists got the same treatment through **`Views/Partials/_SeriesRowActions.cshtml`**
(`hpskSeriesActionsMenu(series)`), shared by AdminSeriesList / RegionalAdminPanel / ClubAdminPanel —
all three define the same `openSeriesEditModal` / `openSeriesCopyModal` / `openSeriesDeleteModal`
globally, each scoped to its own surface, which is what lets one builder serve all three.
⚠ **`data-bs-popper-config='{"strategy":"fixed"}'` is required** — the tables sit inside
`.table-responsive` (`overflow-x:auto`), which clips an absolutely-positioned menu, and
`data-bs-strategy` is silently ignored by Bootstrap. It only shows on the LAST row.
⚠ **A geometry test must open the OUTER Administration tab first** (`regionAdmin-tab` /
`clubAdmin-tab`): an inner sub-pane carries `.active` while the whole admin pane is
`display:none`, so class-level assertions pass on an invisible page and every rect reads zero.
Verified 60/60 `hpsk-verify/row-action-menus-verify.mjs` (all six lists, incl. the bottom-row
reachability hit-test).

Surface differences are **parameters**, not copies: `{kind, groupBySeries, edit, copy, del, editArgs,
copyArgs, manageUrl}`. Only `kind` changes visible output — a club's own page badges an open
club-hosted comp "Öppen/inbjudan" instead of "Klubb", since "Klubb" says nothing new there. The
per-page callback names (`openAdminEditModal` vs `editRegionCompetition` vs `editClubCompetition`) and
their differing signatures are what `edit`/`editArgs` exist for.
- **Upload buttons are capability-detected**, not configured: they render only where
  `window.openUploadInvitationModal` / `openUploadResultListModal` exist, so a surface gets them by
  including the upload modals. The klubb list previously had neither the buttons nor the Extern badge.
- **Status comes from the server's `status` field on every list.** The site list used to recompute it
  in JS with a *different vocabulary* (Öppen / Kommande / Pågående / Avslutad) than the krets and klubb
  lists showed for the same competition (Utkast / Schemalagd / Aktiv / Avslutad). One vocabulary now:
  **Utkast / Schemalagd / Pågående / Avslutad**. The registration-window information the old site
  wording carried survives as a separate `hpskCompetitionRegistrationBadge` ("Anmälan öppen" /
  "Anmälan öppnar …"), which the krets and klubb lists gain.
- ⚠️ **Reading badges in a test still requires scoping to the name cell** — and now the STATUS cell
  carries `bg-warning-subtle` too (the "Anmälan öppnar" badge), on top of the status colours.
- Verified 23/23 `hpsk-verify/complist-shared-renderer-verify.mjs` (asserts the SAME competition
  renders identical type/date/status/anmälningar cells on all three surfaces, which is the drift the
  consolidation exists to prevent) plus 25/25 `comptype-filter-verify.mjs` unchanged.

**Discipline dropdown (2026-08-18):** all three used to build the Tävlingstyp options from the rows
that happened to be loaded, so a discipline with no competition in the current year/krets/klubb was
simply missing from the filter. They now share **`Views/Partials/_CompetitionTypeCatalogue.cshtml`**
(`hpskLoadCompetitionTypes` / `hpskFillCompetitionTypeSelect` / `hpskCompetitionTypeLabel`), which
fetches the canonical `CompetitionTypes.All` from `GetCompetitionTypes`. Include that partial rather
than writing a fourth copy. The dropdown sends the type **id**; server-side the filter matches through
`CompetitionTypes.GetFuzzy` on both sides because the stored `competitionType` property holds ids
("MagnumFalt") on some competitions and display names on others — a literal compare returns nothing.

**Scope badges** are the same pair everywhere: solid `bg-primary` **"Klubb"** = club-hosted and open
to other clubs; `bg-primary-subtle` + lock **"Endast klubb"** = `isClubOnly`, the club's own members
only. Both are blue on purpose — the old yellow read as a warning, which an internal competition is
not. **Reading these in a test requires scoping to the name cell**: the status column uses
`bg-warning` ("Kommande") and `bg-primary` ("Pågående") too.

**`clubScope` filter** (`all` / `noInternal` / `noClub`) folds club competitions away server-side, so
the list isn't drowned by them. Krets defaults to `noInternal`, which preserves its long-standing
behaviour of hiding klubbinterna — that used to be a hard client-side filter with no way back.

**Anmälningar column:** counted in SQL, not from `comp.Children`. Public registrations are `Save()`d
but never `Publish()`ed, so the published cache never saw them and the column read **0 on every row**.
The rows are cached 60 s under `admin_competitions_list_regcounts` — deliberately under the
`admin_competitions_list_` prefix that `InvalidateCompetitionCaches()` already clears, so there are no
new invalidation call sites. Verified 25/25 via `hpsk-verify/comptype-filter-verify.mjs` (all three
lists; needs a temporary Administrators grant for the site-admin page — see that script's header).

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

### Särskjutning (Shoot-Off) for Championship Medal Positions ✅ (2026-05-19, gate corrected 2026-08-16)
**Rule:** In Championship competitions (`competitionScope` ∈ {`Svenskt Mästerskap`, `Landsdelsmästerskap`, `Kretsmästerskap`, `Klubbmästerskap`}), tied medal positions 1–3 are resolved **only** by a 5-shot shoot-off. **None of the normal tie-breakers apply at medal positions** — not X-count, not series countback. Repeat rounds until separated. Ranks 4+ continue to use X-count + countback as before.

**⚠️ WHEN it applies is part of the rule (corrected 2026-08-16).** For over a year the entire gate was `IsChampionshipScope(scope)` — the code had no notion of *when* in the competition a tie counts. On a 7+3 championship that surfaced the Särskjutning card as soon as two shooters tied after **series 7**, i.e. after the grundomgång and before the final that actually separates them. Reported by Tomelilla PK. `TotalScore` sums whatever series happen to be entered, so a 7-series total looked like a final standing. A shoot-off decides a MEDAL, so it can only follow the round that produces the final standings.

The gate now also requires each tied shooter to have shot **everything they were due**:
- Qualifying series for everyone, plus the finals series for whoever is on the `finalsStartList`.
- Finalists are read by `CompetitionResultsController.GetFinalistMemberIds` — **ignoring `isOfficialFinalsStartList`**, which is reset to false on every generation and only flipped by the organiser's Publicera. Gating on it would hide a perfectly good list.
- **No finals start list yet = "unknown", NOT "nobody".** The organiser is stuck exactly there when the bug bites; treating an empty finalist set as "nobody is a finalist" would judge everyone on the qualifying series and reinstate the original bug unchanged.
- Checked **per shooter**, not per class — shooters cut before the final never shoot those series and must not hold back the finalists' medal.
- DNS/DNF short-circuits it to "finished" (see below), otherwise the gate would wait forever for series that will never arrive.

**Fältskytte is deliberately untouched by this** — it has no finals concept and shoot-offs there follow the whole competition (confirmed with Stefan 2026-08-16). Applying the same condition would have disabled Fältskytte särskjutning entirely.

Verified 25/25 `hpsk-verify/participant-status-verify.mjs` (builds + deletes a throwaway Klubbmästerskap, so no dev data is mutated).

**Scope:** Precision, Duell, Milsnabb, MagnumPrecision, NationellHelmatch (all five share the same code path through `CompetitionResultsController.CalculateFinalResults`). **Fältskytte is also done** — Normal/Poäng/Magnumfält, shipped 2026-05-20, via its own `FaltskytteShootOffEntry` table + per-variation comparers (see "Fältskytte Särskjutning" section below). Springskytte (full re-run) is intentionally not implemented (vanishingly rare; would re-run the whole event).

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

### DNS / DNF for the precision family ✅ (2026-08-16)
**Why:** a missing result row was ambiguous between *still shooting*, *never started* and *withdrew*. Nothing could tell them apart, so anything asking "is this shooter finished?" had to guess. The särskjutning gate above is the first real consumer. Before this the precision family had **nothing** — Springskytte has full DNS/DNF (`SpringskytteResultEntry.Status`), Fältskytte has DNS on `FaltskyttePatrolMember.Status` that only frees a patrol slot and never touches results.

**⚠️ It cannot live on the result-entry rows.** `PrecisionResultEntry.Shots` is `NVARCHAR(50) NOT NULL` and `ValidateResultRequest` demands exactly five valid shots — where `"0"` is valid. A placeholder row is indistinguishable from a genuine zero series. Springskytte's trick of writing an empty row (`Shots="[]"`) is therefore **not portable here**. Hence a separate table.

- **Table `CompetitionParticipantStatus`**, keyed `(CompetitionId, MemberId, ShootingClass)` — the same identity key as `CompetitionShootOffEntry`, so regenerating start lists or merging classes cannot orphan a status. Class is in the key because a multi-class shooter can withdraw from one class and finish another.
- **Nullable `FromSeriesNumber`** = first series NOT shot. One field covers every case: null/1 = never took part, qual+1 = skipped the final, 9 = broke off after 8. Forced to null for DNS, which never started.
- `Services/ParticipantStatusService.cs` + `SetParticipantStatus` / `ClearParticipantStatus` / `GetParticipantStatuses` on `CompetitionResultsController` (same three-tier auth as shoot-off entry). Reads swallow a missing table and return empty, so an un-migrated environment degrades to today's behaviour instead of taking the result list down.
- **A DNF keeps their placement, ranked on their partial sum**, marked "Bruten" (Stefan's call 2026-08-16) — not unranked-at-the-bottom like Springskytte. Before this, a shooter who skipped the final just looked like they had shot badly, publicly. A DNF tied at a medal place was judged too unlikely to design around, so a withdrawn shooter *can* land in a tied group.
- **UI:** the start-list editor's per-shooter kebab menu (with a status badge next to the class badge) and the results-entry shooter card's participation menu. Deliberately **not** on `/station` — that screen is for shots, not administration.

**⚠️ The Id-vs-Name trap.** The table stores the class **Id** (`C1`, `A_opt_1`); `PrecisionShooterResult.ShootingClass` carries the display **Name** (`C 1`, `A Opt 1`). The lookup in `CalculateFinalResults` happens while the raw id is still in scope — probing on the display name silently matches nothing for every class where they differ. Same trap already present in `ChangeShooterClass`, which writes Name where everything else writes Id.

**Operator step:** run `Migrations/create-competition-participant-status-table.sql` in SSMS. Guarded **per object**, not per table — a table-level guard skips index creation forever after a partial failure. **Visual Studio's static T-SQL analysis does not evaluate the guards** and reports "already exists" per CREATE in its Error List; those are design-time messages, not execution errors. The script ends with a verification query (expect 9 columns, 3 indexes). Run in prod 2026-08-16.

Verified 13/13 `hpsk-verify/dnsdnf-ui-verify.mjs` (full round trip: menu → database → badge → cleanup).

### Finals result entry (Precision / MagnumPrecision) ✅ (2026-08-16)
**The finals mode always worked; nothing in the UI could reach it.** The scoring screen switches into finals mode on `?phase=finals`, loads the finals start list and sets the first series to `qualificationSeriesCount + 1`. `SaveResult` never capped below `numberOfSeriesOrStations` either. But the **only** link in the codebase producing `phase=finals` sat inside the Finalsskjutlag block in `CompetitionResultsManagement.cshtml`, which is wrapped in `@if (!isStationPage && !supportsSkjutledareView)` — and `supportsSkjutledareView` is true for exactly Precision and MagnumPrecision. Those disciplines launch their scoring screens from the **Funktionärer** tab instead, which built its list from the qualifying start list only and linked without `phase`. Result: `currentPhase` stayed `'qualification'` and entry stopped at the last qualifying series. Reported by Tomelilla PK, who could not get past series 7 of 10.

Fixed with a **Final section on the Funktionärer tab** (`PrecisionFunktionarerManagement.cshtml`) — where Precision already has its entry points — rather than opening up a block that was deliberately switched off for these disciplines.
- Renders only when `numberOfFinalSeries > 0` (reads `ViewData["NumberOfFinalSeries"]`, set in `CompetitionManagement.cshtml:46`).
- **Tied to the finals START LIST, not the finals series count** — finals mode loads its shooters from that list, so without one there is nothing to enter against. Missing list → an explanation pointing at the Startlistor tab, never a dead button.
- Not gated on `isOfficialFinalsStartList` (same reasoning as the särskjutning gate); an unpublished list warns instead.
- Loaded once plus on tab re-entry, **not** on the 15 s poll — a finals start list is created once, not continuously.

Verified 21/21 `hpsk-verify/finals-entry-verify.mjs`, including that entry really starts on series 4 when the qualifying round is 3, and both branches (with and without a finals start list).

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

**Self-redirect trap — `ERR_TOO_MANY_REDIRECTS` on a competition URL (2026-07-27).** Because the URL is a *function of properties* (`clubId` / `regionalFederation` / `competitionScope` / `competitionDate`) and not of tree position, every edit that changes the shape makes Umbraco's URL tracker store the previous pretty URL in `umbracoRedirectUrl`. **Revert that edit** (e.g. set a club, then clear it) and a stored "old" URL becomes identical to the live one — `ContentFinderByRedirectUrl` then 301s to `_publishedUrlProvider.GetUrl(node)`, which is the URL that was just requested. Infinite loop. Two mitigations are in place, both in `Routing/`:
- `CompetitionContentFinderComposer` puts `CompetitionUrlContentFinder` **before** `ContentFinderByRedirectUrl` (`InsertBefore<,>`, wrapped in try/catch + a startup warning if the core finder isn't in the collection yet), so a URL that resolves to real content can never be hijacked by a stale row.
- `CompetitionUrlProvider` returns **null** when `clubId > 0` but the club won't resolve to a published `club` under a `regionalPage`, instead of silently emitting the region-hosted 3-segment shape. `FindByRegionShape` rejects any comp with `clubId > 0`, so that URL could never round-trip.

**Diagnosing:** the backoffice Info tab saying *"This document is published but its URL cannot be routed"* is the same fault seen from the other side — Umbraco routes the generated URL back through the finder pipeline (`DetectCollisionAsync`) and got no content. "…would collide with content X" means a different thing (duplicate slug). Existing bad rows must still be deleted by hand: Content → Omdirigerings-URL:er, or `DELETE FROM umbracoRedirectUrl WHERE Url = '/competitions/…'` (rows are stored with a leading slash, no trailing slash).

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

**Swish-number validation lives in `Views/Partials/_SwishNumberValidation.cshtml`** (extracted 2026-08-16), included by both `CompetitionWizardModal` and `CompetitionEditModal`, guarded by `window.swishNumberValidationLoaded` so rendering both doesn't redefine it. It used to sit inside the wizard modal on the assumption — stated in a comment there — that the edit modal "renders its swishNumber input alongside this one". That holds on AdminPage, Club and RegionalPage, which include both partials, but **`CompetitionManagement.cshtml` includes ONLY the edit modal** — so on the page organisers actually edit their competition from, every keystroke in the Swish field threw `validateSwishNumberField is not defined` and the Validera button did nothing. **Silently**, because exceptions in inline `oninput`/`onclick` attributes surface nowhere in the UI. Format-only check (Swish exposes no "is this number registered" endpoint) and the copy is deliberate about that. Verified 13/13 `hpsk-verify/swish-validation-verify.mjs`.
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
   UNIQUE INDEX: (CompetitionId, MemberId, ShootingClass, SeriesNumber)
   -- TeamNumber and Position are now INFORMATIONAL only
   ```

   **⚠️ ShootingClass MUST be in that key**, and the schema files were wrong about it until 2026-08-16. A shooter can compete in several weapon classes in the same competition and then legitimately has one result per class for the same series number. `SaveResultToDatabase`'s MERGE matches on all four columns; leave `ShootingClass` out of the index and that MERGE finds no match, tries to INSERT, and trips the unique index. The save then **fails hard and is not retryable** — the shooter can hold results in exactly one weapon class per series number and the other is permanently blocked.

   Prod was corrected by `Migrations/fix-multiclass-results.sql` on 2026-02-20 (verified still in place 2026-08-16: index `UX_PrecisionResultEntry_CompetitionMemberClassSeries`, all four columns). But `create-precision-result-entry-tables.sql` and `_prod-schema-sync-additive.sql` kept creating the OLD three-column index, so **any newly created database reintroduced the bug** — both are fixed now. The sister tables (`DuellResultEntry`, `MilsnabbResultEntry`, `MagnumPrecisionResultEntry`, `NationellHelmatchResultEntry`) were created after the fix and always had the correct shape.

   The failure was hard to diagnose because both the user message and the log **asserted the wrong cause**: "Resultatet sparades redan av en annan funktionär. Försök igen." and `"likely concurrent save by another range master"`. SQL 2627/2601 has at least two causes here — a genuine concurrent save (transient, retry works) and this schema fault (deterministic, retry never works). Both now name both causes, and the log carries class, series and competition so they can be told apart afterwards.

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

### Fakturor page performance ✅ (2026-08-16)
Club Admin → Fakturor (and the site-wide / regional invoice lists) took **~12 s per load and did it again on every filter change, page step and view switch** — exactly what an organiser does when reconciling and consolidating club invoices. The existing controller cache only helped on an *exact* repeat: its key contains `page`, `viewType`, `excludePaid` and the filters, so every new combination was a fresh 12 s.

Two causes, both in `Services/InvoiceAdminService.cs`:
1. **The tree walk.** `GetFlatDescendants` enqueued every node and issued one `GetPagedChildren` **per node** — one query per registration and one per invoice, then discarded them, so cost grew with what grows fastest on this site. The region filter walked the whole tree a **second** time just to find club nodes. Now pruned, collecting all five needed aliases (`competition`, `registrationInvoicesHub`, `competitionTeamRegistration`, `competitionRegistrationsHub`, `club`) in one pass and cached 60 s.
   - Don't descend into `registrationInvoicesHub` — invoices are read per hub by `GetInvoicesFromHub` anyway.
   - Under `competitionRegistrationsHub`, take the `competitionTeamRegistration` children and stop. They ARE direct children of the hub (`CompetitionTeamService.cs:1432`), so this is complete.
   - **⚠️ Add those children straight to the result and do NOT also enqueue them** — doing both adds them twice, and every downstream lookup is keyed on their ids.
   - `DoNotDescend` is a claim that nothing the aggregation collects can appear below a type. Check against the five aliases before extending it.
2. **`GetMemberInvoices` paged the ENTIRE member register** (500 at a time) on every request, including every page step through the results where the answer cannot have changed. Now cached per club.

**Both cache keys sit under the `admin_invoices_` prefix on purpose** — the existing `InvalidateInvoiceCaches()` (`ClearByRegex("^admin_invoices_")`) already drops them on create / mark-paid / cancel, so there are **no new invalidation call sites** to remember. The client-side request-sequence guard against the "Inga fakturor" flicker already existed.

Measured in dev: 12670/12581/12130/11927/12727 ms → **33–40 ms warm**, ~5 s for the first scan after the cache expires.

**⚠️ How to prove a change here didn't drop invoices:** `hpsk-verify/invoice-perf-verify.mjs` fingerprints the invoice set per view. Run A/B against an **identical data state** — stash only the service, rebuild, measure, restore. A first attempt compared snapshots taken either side of other verify scripts and appeared to lose team invoices; that was **mutating test scripts** (consolidated-paid-cascade marks invoices paid, and `excludePaid=true` then hides them), not the pruning. Clean A/B: identical sets in all five views.

### Cashier Workflow & Multi-Class Walk-In ✅ (2026-05-06)

**Overview:** End-to-end registration-desk experience for the cashier on competition day. Walk-in supports multi-class with per-class slot/patrol pickers, mark-as-paid records actual amount and emails a betalningsbekräftelse (the printable kvitto lives on the shooter's Min sida), registrations can be re-pointed to a different shooter, and the start list updates automatically as walk-ins land.

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
- The email on Paid is now a **Betalningsbekräftelse** (confirmation), NOT a kvitto — opt-out checkbox, fires inside `PaymentService.UpdatePaymentStatusAsync` on transition to Paid (also `InvoiceAdminController.MarkAsPaid`). Members without email skipped. The body links to Min sida → Tävlingar where the shooter prints the legal **Kvitto** (`/kvitto/{invoiceId}`). Audit event key stays `ReceiptSent` (display "Betalningsbekräftelse skickad"). See memory `kvitto-vs-betalningsbekraftelse`.

**Invoices are created eagerly (2026-06-11):** every fee-bearing registration/team gets its Pending invoice at *creation* (not lazily on first payment option) via `PaymentService.EnsureRegistrationInvoiceAsync` (wired into public register / late-walk-in / team creation). So a fee'd row shows **Väntande** immediately; "Saknar betalning" was renamed **"Saknar faktura"** (now the error/edge case) and free comps show **"Ingen avgift"** (`No Fee`). The status lookup now matches invoices by the single `registrationId` too, not only legacy `relatedRegistrationIds`. See memory `eager-invoices-and-lag-section`.

**Lag section on the Anmälningar tab (2026-06-11; late team CREATION added 2026-08-11 — see "Efteranmälan av lag/stafett" below):** teams + relay (stafett) are listed in their own collapsible "Lag" card (`RegistrationAdmin/GetCompetitionTeams`), discipline-agnostic, with the same per-row payment actions as individuals (team-aware manage-payment modal; team invoices use memberId `team-{id}`). **Team edit/delete is now authorized** (`CompetitionTeamController.UpdateTeam`/`DeleteTeam` previously had none): site admin / club+regional admin for the team's club / member whose primary club is that club (`CanManageTeamAsync`); other clubs' edit/delete buttons hidden in the team + stafett registration modals.

**Springskytte deferred team/relay rosters (2026-06-11):** Springskytte lag/stafett can be created with a name only + paid, shooters named any time before the event (relaxation gated to Springskytte in the shared `CompetitionTeamService` + modals; a stafett edit modal was added). See memory `springskytte-deferred-team-roster`.

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
- `Created`, `MarkedPaid`, `Cancelled`, `Refunded`, `EmailSent` (Swish QR email), `ReceiptSent` (betalningsbekräftelse email; key unchanged, display "Betalningsbekräftelse skickad"), `Transferred`, `StatusChanged`.

**Multi-invoice top-up model:**
- One registration can have several invoices: original Paid + zero-or-more Paid top-ups + at most one Pending top-up. Paid invoices are never modified — they're the historical record. Top-up math is `delta = newFee - sumPaid`.
- API exposes per-row `paidAmount` and `pendingAmount` aggregates so the summary cards reflect totals correctly across multi-invoice rows. `hasVariance` is true when any paid invoice's `actualPaidAmount` differs from its `totalAmount`.

**Class mutex bucket logic (`getClassMutexBucket` in CompetitionRegistrationManagement.cshtml):**
- Maps a class id to a `weapon:subcategory` bucket. Within-bucket ticks auto-untick siblings; across-bucket ticks are independent.
- Subcategory derivation: `_Jun` → jun, `_Vet_` → vet, contains `Dam` → dam, else open. Springskytte composite ids (containing `-`) get a unique bucket per id so multi-class registration there is unaffected.

### "Tävlar för" — en anmälan kan gälla vilken som helst av skyttens klubbar (2026-08-16)

A member has a `primaryClubId` **plus** a CSV of additional clubs in `memberClubIds`, and has had
for years — but every registration path read only the primary one, so a shooter who competes for
their second club in one discipline could not say so. The only "fix" available to an organiser was
to edit the member's primary club, which changes them everywhere, for every competition.

**`Services/MemberClubService.cs` is the ONE place a member's clubs are resolved.** `GetClubOptions`
(primary first, unresolvable clubs dropped), `IsMemberOfClub`, `ResolveRegistrationClubId` and
`GetRegistrationClubIds(competitionId)` (memberId → the club their registration is filed under).
- **⚠️ `primaryClubId` is a STRING property.** `GetValue<int>("primaryClubId")` does not convert it
  and silently yields **0**. `AddLateRegistration` did exactly that, so **every walk-in registration
  was stored with `clubId=0`** and only looked right because the read paths fall back to the
  member's primary club. Always go through `GetPrimaryClubId`.

**Surfaces.** `representingClubId` on `CompetitionController.RegisterForCompetition` (public
precision modal + Springskytte modal), `ClubId` on `AddLateRegistration` (walk-in) and on
`UpdateCompetitionRegistration` (Redigera anmälan, where the static club field became a dropdown).
Every picker is **hidden below two options** — a dropdown that cannot be changed is noise on a form
almost every shooter fills in.
- **Silent fallback on the REGISTRATION paths, hard refusal on the EDIT path.** On registration the
  value comes from a control that is legitimately absent, so an unusable id falls back to primary.
  In Redigera anmälan the operator has explicitly chosen, so a club the shooter does not belong to
  is refused with a message naming the fix. Same rule, opposite handling — don't unify them.
- **⚠️ Re-registration must not stamp the club unconditionally.** `RegisterForCompetition`'s update
  branch applies the club **only when `representingClubId` was actually sent**. Stamping the
  resolved default there would drag a correctly-filed shooter back to their primary club every time
  they added a class, because the resolver falls back to primary whenever nothing is asked for.

**⚠️ The club is SNAPSHOTTED in three places, and the result list reads the START LIST first**
(`CompetitionResultsController.GetShooterNameAndClub`). Correcting the registration alone leaves the
public start list and result list showing the old club with nothing on screen saying so. Hence
`Services/RegistrationClubPropagationService.cs`, which **rewrites in place and never regenerates**
(a club correction must be safe between series; regenerating reshuffles skjutlag and times):
1. `precisionStartList` / `finalsStartList` `configurationData` — parsed as a **JObject**, because
   that doctype carries at least three different config shapes. Patches `Teams[].Shooters[]`
   (precision/finals) and `Starters[]` (Springskytte), then re-renders the cached HTML blob via
   `StartListHtmlRenderer` or `SpringskytteController.BuildStartListHtml` (made `internal` for this).
2. `FaltskyttePatrolMember.ClubName` — targeted SQL UPDATE.
3. **Stafett lists are deliberately skipped** — their `Club` is the TEAM's club, not the shooter's.
- **⚠️ Direktplacering must NOT be patched.** `DirektplaceringStartListService` writes its
  `precisionStartList` node with its own anonymous config shape and its own bespoke HTML;
  deserializing that into a `StartListConfiguration` and re-rendering would silently replace the
  whole list's markup. DP lists are fully derived from the registrations, so it calls
  `Regenerate` instead.
- Only re-publishes a node that was **already published** — publishing a draft start list as a side
  effect of a club correction would make an unfinished list public.

**⚠️ Springskytte's RESULT list never read the registration at all.** `LoadMemberInfo` resolved the
club straight off the member record, so start-list propagation alone would still have printed the
wrong club. It now takes a `competitionId` and prefers `GetRegistrationClubIds`, falling back to the
primary club for legacy rows and for shooters with no registration.

**Lag/stafett.** `GetRegisteredMembersInClasses` and `GetRegisteredMembersForClub` now bucket by the
**registration's** club (a shooter entered for club Y belongs in Y's lag urval, and used to show up
in X's and be missing from Y's). `GetClubMembers` (the stafett picker) tests `IsMemberOfClub` rather
than primary-only. **NB `GetClubMembers` also requires the `Users` member-group role**, which is a
separate pre-existing filter — a member without it is invisible there regardless.

Verified 76/76 across `hpsk-verify/clubswap-{verify,registration-verify,team-verify}.mjs` (every
mutation reverted, with the revert itself asserted). **Two traps for whoever writes the next test
here:** the precision editor endpoint returns an *empty configuration, not an error*, for a
Springskytte list — read those via `GetSpringskytteStartLists`; and `FaltskytteController` shortens
club names at read time (`ClubNameHelper.Shorten`), so a patrol row reads "Falkenbergs PK" for a
stored "Falkenbergs Pistolklubb".

Adds C# → full rebuild. No migration, no doctype property, no Umbraco node.

### Efteranmälan av lag/stafett vid registreringsbordet (2026-08-11)

**What:** the desk can create lag AND stafettlag after sista anmälningsdag. Individual walk-in
already worked; teams did not — team creation only ever existed on the public competition page,
where the buttons are disabled once registration closes. Nothing server-side enforced the deadline,
so the gap was purely the missing UI. Discipline-agnostic: renders whenever `allowTeams` /
`allowStafett` is set. Verified on Springskytte (6628, 5626) and Precision (2576) via
`hpsk-verify/late-team-{verify,e2e}.mjs`.

- **`lateTeamRegistrationModal`** (`CompetitionRegistrationManagement.cshtml`). One modal, both
  kinds; `lateTeamIsRelay` switches class list (`teamClasses` vs `stafettTeamClasses` — both come
  from `CompetitionTeam/GetTeamsForCompetition`), member source (`GetEligibleMembers` vs
  `GetClubMembersForRelay`) and endpoint (`CreateTeam` vs `CreateStafettTeam`).
- **Payment handoff** reuses `showPaymentOptionsForTeam()` — the Lag card's own chooser — so QR,
  mark-paid and QR-email need no new code. **No QR pre-flight** (unlike the individual walk-in,
  which calls `GeneratePaymentQR` to force invoice creation): `CreateTeamAsync` already creates the
  team invoice eagerly via `EnsureTeamInvoiceCoreAsync`, so the amount is right on first paint.
- **Auth widened** with `HasCompetitionStaffAccessAsync` on `CompetitionTeam.CreateTeam`,
  `CreateStafettTeam` and `GetClubMembersForRelay` — the organiser must be able to register a
  **visiting** club's team. The old rule was own-club-or-club-admin, which locked the krets out of
  every club but its own on a region-hosted SM. Member self-service path is unchanged.
- **Header is now one Åtgärder menu** instead of six buttons (walk-in, lag, stafett, påminnelser,
  bokföringsunderlag, CSV) which wrapped on a desk tablet. Styled as the PAGE-level menu from
  club-admin Medlemmar (`btn-primary`, default size, `shadow`, `min-width:260px`, `dropdown-header`
  groups) — deliberately unlike the small grey outline toggles the table ROWS use, so blue+large
  reads as "acts on the whole list" and small+grey as "acts on this row". No
  `data-bs-popper-config` needed here: the header is not inside `.table-responsive`.

**Two team-QR-email bugs fixed at the same time** (`SwishController.SendTeamQRCodeEmail`):

1. **Relay fee.** It read `teamRegistrationFee` unconditionally, so a 250 kr stafett was mailed a
   300 kr QR — and it refused outright ("Betalning ej konfigurerad") on a competition configuring
   only a stafett fee. Now branches on `team.IsRelay`, as `GenerateTeamPaymentQR` always did. The
   Swish **reference stays `"Lag: {invoiceNumber}"` for both kinds** — on-screen and mailed QR must
   carry the same reference or the payment can't be reconciled. Proof: audit rows for the same team
   went `300.00 "Lag-QR"` → `250.00 "Stafett-QR"`.
2. **Recipient.** It mailed the *logged-in user*, right on the public page (club's lagledare mails
   themselves) and wrong at the desk (organiser mails themselves). Now takes optional
   `targetMemberId`, gated on admin/desk-staff auth, fed by the new
   **`CompetitionTeam/GetTeamContacts(competitionId, teamId)`** (team members that have an email;
   `CanManageTeamAsync`-gated). Deliberately a separate lookup, not a field on the Lag list — that
   list is a hot path and this is only needed when the operator clicks. Deferred Springskytte roster
   → no contacts → chooser degrades to "Mig själv" with an explanation.

### Team QR: amount from the invoice, and never twice for the same money (2026-08-19)

**Both team-QR endpoints priced the QR from the competition's fee property**
(`teamRegistrationFee` / `stafettRegistrationFee`) rather than from the invoice — including on the
branch that REUSES an existing pending invoice. Edit the fee after invoicing and the QR and the
invoice ask for different sums; reproduced at 477 kr QR against a 400 kr invoice. The individual
shooter path never had this: it goes through `EnsureOutstandingInvoiceAsync` and bills what is owed.

**Worse, and newer: a team already covered by a samlingsfaktura could still be charged separately.**
Consolidation leaves the child at `paymentStatus = "Pending"` and only sets `settledByInvoiceId`, and
these two endpoints refused solely on `"Paid"` — so the desk got a live QR, and
`SendTeamQRCodeEmail` actually **sent** one, for money the club was already paying through the
parent. `IsCoveredByOpenConsolidation` had been wired to guard *cancelling* and *re-pricing* such an
invoice (commit `683c97b`); paying it is the third case and was missed. Team invoices are squarely in
scope — they carry no `invoiceKind`, they appear in the club's *outgoing* list, and `683c97b`'s own
persona test consolidates a team invoice with individual ones.

**`SwishController.ResolveTeamQrAmount(invoiceId, feeFallback)` is now the single answer to "how much
should this QR be for", and both endpoints go through it** — the on-screen QR and the mailed QR can
no longer disagree, and the mail's printed amount + audit row use the same number.
- Refuses when `IsCoveredByOpenConsolidation`, when Paid, and when the invoice is makulerad.

**Superseded 2026-08-25 — the rule moved to `ConsolidatedInvoiceService.ResolveQrAmount` and
`ResolveTeamQrAmount` now delegates to it.** Keeping a private copy here is exactly why the three
paths that MAIL a QR never got this fix: `SendPaymentReminders`, `ResendInvoiceEmail` and
`SendTestReminder` all still read `totalAmount` raw and checked nothing. Only the team-specific
WORDING stays in SwishController, switched on `QrAmountResolution.Refusal`. **Never re-derive the
rule at a new QR site — call the resolver.** NB the reachable fault there was not the kreditfaktura
(an individual registration invoice cannot be credited — `CreateCreditNoteAsync` requires a
samlingsfaktura) but the samlingsfaktura: a covered child stays `Pending`, so the reminder mailed a
Swish QR for money the club was already paying. `ResendInvoiceEmail` had **no status check at all**,
so a Paid invoice could be re-mailed with a live QR.
- Otherwise takes `ConsolidatedInvoiceService.GetBalance().AmountDue`. **Not `totalAmount` directly** —
  an issued invoice is never edited, so a kreditfaktura reduces what is owed without touching it, and
  `GetBalance` is where that is derived (read its doc comment before adding another QR site).
- **The fee survives as a FALLBACK, deliberately.** A legacy invoice with no readable amount now logs
  a warning and produces today's QR rather than leaving the registration desk unable to take payment.
- `PaymentService.CoveredByConsolidationPaymentMessage` is a *separate*, deliberately **one-sentence**
  message from `CoveredByConsolidationMessage`: refusing a payment is not refusing a cancel, and
  ⚠️ the public team modals render a failure as the **button label**, so a two-sentence explanation
  breaks the layout there instead of helping.

Verified 23/23 `hpsk-verify/teamqr-consolidation-verify.mjs` (bumps the real fee and asserts the QR
ignores it, consolidates the team invoice and asserts both endpoints refuse, then makulerar and
asserts it is payable again — every mutation reverted and the revert asserted). **A/B'd against the
un-fixed build: 7 of those 23 fail there**, so the script actually discriminates. Regression: 43/43
consolidated-invoice, 27/27 creditnote, 19/19 paid-cascade, late-team e2e green. Adds C# → full
rebuild. No SQL, no doctype property.

### Samlingsfaktura: audit trail, and Skjutledare off the finance surface (2026-08-19)

**Consolidation left almost no trace.** `ConsolidatedInvoiceService.CreateAsync` accepted an
`actingMemberId` and **never used it**, and `PaymentService.CreateStandaloneInvoiceAsync` wrote the
parent's `Created` audit row with `byMemberId`/`byMemberName` hardcoded `null`. The covered CHILDREN
got no row at all. So "who decided Varbergs PK owes 2 400 kr for these seven entries?" was
unanswerable — on an operation that redirects who pays and **locks the children against
cancellation**.

- `CreateStandaloneInvoiceAsync` takes optional `actorMemberId` / `actorMemberName` (optional only so
  older callers compile — **pass them**), and the consolidation now does.
- Two new event types in `InvoicePaymentEventTypes`: **`Consolidated`** and
  **`ConsolidationCancelled`**, logged on each CHILD naming the parent, the payer and the actor.
  Reconciling starts from one shooter's invoice ("why can't I pay this?"), and without a child row
  that invoice silently changes behaviour — still Pending, but no longer payable on its own.
- `CancelUnpaidParent` gained optional actor args and logs the release, so the history doesn't end at
  "Ingår i samlingsfaktura X" forever after the invoice was freed.
- Swedish labels added to the history renderer in `CompetitionRegistrationManagement.cshtml`
  (`labelFor`/`colorFor`). That map falls back to the raw type, so a missed label degrades, not breaks.
- ⚠️ **`GetInvoiceHistory` returns NEWEST FIRST** (`ORDER BY OccurredAt DESC, Id DESC`). The most
  recent row is `[0]`, not the last — a test that used `.pop()` read the oldest and looked wrong.
- Logging is **best-effort and after the money is linked**: a failed audit write must never undo a
  correct consolidation.

**Skjutledare removed from the invoice/finance surface (Stefan's call).** `IsSkjutledareForClub` was
OR-ed into `CanManageCompetitionInvoice` *and* repeated inline in five `InvoiceAdminController`
endpoints, so a range-master of the organising club could mark payments received, makulera invoices,
mail payment reminders and export Bokföringsunderlag. **Removed from all six**, deliberately together:
a partial tightening leaves someone who cannot mark an invoice paid still able to mail reminders
about it. `HasCompetitionStaffAccessAsync` keeps Skjutledare — that is the range, which is the role.

- **No new "invoice permission" was introduced, on purpose.** Anyone who can consolidate can already
  mark a payment received and issue a kreditfaktura, so a separate gate would grant *less* than what
  the same people already hold — pure friction, no security gain, and one more thing a
  low-computer-literacy club admin must know to set, whose failure mode is a silent lockout on
  competition day. Accountability is served by the audit trail above instead.
- **The escape hatch for a genuine sekretariat/kassa person: Bemanning app access, or name them
  tävlingsansvarig.** Both feed `HasCompetitionManagementAccess`, which is checked first — per
  competition, revocable, person-level. `HasCompetitionManagementAccess`'s own doc comment already
  names this persona ("a Sekretariat- eller Kassaansvarig needs the same page without being appointed
  tävlingsledare").
- ⚠️ **This changes NOTHING on a region-hosted competition (an SM).** The Skjutledare branch only ever
  ran inside `clubId > 0`; SM Springskytte 2026 has `clubId` unset and `regionalFederation = Halland`
  (verified in dev), so it was already unreachable there. Don't expect this to affect SM behaviour.

Verified 17/17 `hpsk-verify/skjutledare-invoice-gate-verify.mjs` — grants Skjutledare to a spare
member on a CLUB-hosted comp, asserts all eight finance endpoints refuse AND that the /station pad
still renders, reads and saves, then revokes. **A/B'd: 7 of 8 finance assertions fail on the old
build** (it really did mark an invoice paid and makulera it). Audit trail: 24/24
`consolidation-audit-verify.mjs`. Adds C# → full rebuild. No SQL, no doctype property.

### Borttaget lag makulerar sin avgift (2026-08-20)

**A half-applied fix, found in prod at SM.** `DeleteCompetitionRegistration` has cancelled an
individual's pending invoices for a long time — its own comment explains that an orphan otherwise
keeps showing under "Utestående betalningar" with no shooter to chase. **The team path never got the
same rule**: `DeleteTeamAsync` did `DELETE FROM CompetitionTeam` plus `DeleteTeamRegistrationDoc` and
nothing else. Three test teams deleted during SM preparation left 450 kr of "unpaid" invoices that
inflated the krets's Fakturor page and were offered to the organiser's samlingsfaktura picker.

`CompetitionTeamService.CancelTeamInvoicesAsync` now runs **before** the row is deleted:
- Cancels every **Pending** invoice on `team-{id}`; Paid ones are untouched (the money was collected
  and the books need to see it). **Every** one, not the first — prod has duplicates minted a second
  apart by two code paths, and leaving the second behind recreates the phantom.
- ⚠️ **Refuses the whole deletion when an invoice is covered by an open samlingsfaktura.** The A/B
  showed the old code cheerfully deleting such a team, leaving the club paying a total that includes
  a lag that no longer exists — nothing recalculates a parent. The correction there is to makulera the
  samlingsfaktura or issue a kreditfaktura, and the refusal says so.
- A failure to *reason* about the money also aborts: a silent orphan is the thing being prevented.
- The actor is threaded from the controller so the makulering is attributable — "Laget borttaget"
  with no name is an audit row nobody can act on.

**Existing orphans must still be cleaned by hand** — an unpaid invoice is räkenskapsinformation, so it
is makulerad, never deleted. `hpsk-verify/cancel-orphan-team-invoices.mjs <compId>` lists them
(**dry run by default**, `--apply` to act, `--base` for prod) and skips any covered by a
samlingsfaktura.

Verified 15/15 `hpsk-verify/team-delete-invoice-verify.mjs`. **A/B'd: 8 of 15 fail on the old build**,
including the covered-team deletion succeeding. Regression: lag-gender 20/20, lag-multiteam 15/15,
lag-conflict-dialog 14/14, clubswap-team 13/13, late-team e2e green, organiser-consolidation 31/31.

### Kretsens adminpanel — grupperad vertikal räls (2026-08-20)

Same treatment `ClubAdminPanel` got on 2026-07-05, now on `RegionalAdminPanel`: the horizontal
`nav-tabs` bar wrapped to several rows once the krets had nine sections. Layout is a `.row`: a
`col-lg-auto` rail (`.admin-rail`, `nav nav-pills flex-column`, sticky, fixed 250px) plus a `col-lg`
content column holding the unchanged `#regionAdminTabContent`.

**Same four headings as the club, in the same order, with the same icons** — *Kalender & tävlingar* /
*Medlemmar* / *Utmärkelser* / *Kretsen* (the club's fourth is *Klubben*). An admin who knows one page
knows the other. Below `lg` the rail is replaced by a grouped `<select>`
(`#regionAdminTabMobileSelect`), exactly like the club's.

**Every tab-button id and `data-bs-target` is unchanged**, because the lazy-loaders bind by button id
— a rename there breaks loading silently rather than visibly. Dokument stays the default tab (its
pane still carries `show active`); it simply sits in its group now instead of first in a row.

⚠️ **The picker calls `btn.click()`, NOT `bootstrap.Tab.show()`.** The Tävlingar loader binds to
**click** rather than `shown.bs.tab` (see the comment at its binding — the event did not reach it
reliably in prod). Showing the tab programmatically switches the pane and never loads the
competitions on a phone. Clicking satisfies both binding styles, since Bootstrap's delegated handler
switches the tab either way.

⚠️ **Fixed a duplicate DOM id while in here:** the admin Dokument button was `regionDocuments-tab`,
which `RegionalNavigation.cshtml` **also** uses for the PUBLIC region page's Dokument tab. Two
elements, one id, on the same page — `getElementById` returned the public one. Nothing bound to it
(the loaders use the unique `regionEvents-tab` / `regionSettings-tab` / `regionAdminCompetitions-tab`),
so it had never broken anything, but it is invalid HTML and a trap. The admin one is now
`regionAdminDocuments-tab`; the pane id `#regionDocumentsTab` is unchanged.

**"Skicka mail" moved from the header into the rail** (Stefan). It is an ACTION, not a section, so it
sits below a divider at the bottom and is deliberately **not** a `.nav-link` — Bootstrap's Tab plugin
must never treat it as a tab or try to make it active. In the picker, which has no divider
affordance, it becomes an `Åtgärder` optgroup with the sentinel value `action:email`; the handler runs
it and then **restores the picker to the current section**, since an action is not a destination.

Verified 43/43 `hpsk-verify/region-admin-rail-verify.mjs`. Razor views are runtime-compiled, so
`dotnet build` validates none of this — **loading the page is the compile check**, and that is half
of what the suite is for. It also asserts every old tab id still maps to its old pane, that the
click-bound competitions loader really fires from the picker, and that the mail action leaves the
selected section alone. Regression: region-receivable 19/19, region-hosted-invoices 12/12.

### Samlingsfaktura åt BÅDA hållen på klubbens Fakturor-sida (2026-08-20)

**The asymmetry Stefan spotted:** a club admin could bundle what the club OWES ("att betala" → tick
rows → Betala valda) but not what it is OWED. To bill another club they had to leave their own page
entirely and go to the competition. The receivables view is the more natural home for "send them one
bill"; the competition's Anmälningar tab is for the desk on the day. Both now exist.

**⚠️ The two directions are NOT the same operation with the roles swapped — get this right or the
wrong party is invoiced:**
- **Payer flow** (unchanged): the club bundles its own debts. The parent is created in the
  **organiser's** ledger, addressed to the club clicking. `payerClubId` = me.
- **Receivable flow** (new): the club is the **utställare**. The parent is created in **its own**
  ledger, addressed to the debtor. `payerClubId` = the other club.

**⚠️ BOTH HOST SHAPES — regions arrange competitions too.** A competition is hosted either by a club
(`clubId` set) or by the **krets** itself (`clubId` unset, `regionalFederation` set); an SM is the
latter, so a region carries fordringar exactly like a club. `GetClubReceivableDebtors` therefore takes
`clubId` **or** `region`, and the button exists on both surfaces: the club's Fakturor tab
(`ClubAdminInvoicesList`) and the krets's (`AdminInvoicesList` with `LockedRegionCode`, which is the
page the SM organiser actually looks at). Serving only the club shape is the mistake this codebase has
made four separate times — see `IsRegionHostAdminAsync`.
- Club scope = `GetCompetitionsHostedByClub` (`clubId == x`); region scope =
  `GetCompetitionsHostedByRegion` (region matches **and `clubId <= 0`**). Without that second half the
  krets would sweep in every club-hosted competition in the region, whose invoices belong to those clubs.
- Both go through the **cached** tree scan; walking the tree again is what made the Fakturor page take 12 s.
- A club owing **itself** is skipped on the club shape (own entries, settled internally). A krets is
  not a club, so on the region shape every club is a legitimate debtor.
- The krets tab shows the bar unconditionally — that tab *is* the "egna tävlingar" view. The club page
  must instead follow the att-betala / att-få-betalt-för switch.
- ⚠️ **Region code casing:** pass the node's casing (`"Halland"`). `IsRegionalAdminForRegion` compares
  the member group `RegionalAdmin_{code}` **exactly**, while competition matching goes through
  `NormalizeRegionCode`, which lowercases. A lowercased code finds the right competitions and is then
  refused by auth — which reads as a permission problem rather than a casing one.

**One dialog, two sources.** `Views/Partials/_ConsolidateByClubModal.cshtml` holds the whole
pick-club → review → issue flow; only `fetchGroups` differs (one competition vs a club's
receivables). The competition page's bespoke copy was deleted in favour of it. Both send
`organiserScope: true` — both are the utställare direction.

**Different control on purpose:** paying ticks rows ("which of my debts do I settle together"),
invoicing picks a club ("bill them for everything they owe me"). Same page, two questions.

**A debtor owing across several competitions** gets one samlingsfaktura PER competition, because that
is what the engine produces and each competition has its own payee. The picker says so before you
commit and the success panel links every parent it created.

⚠️ **JS trap that cost a debugging round.** The partial's entry point is an `async function` declared
inside an `if (!window.X) { … }` guard. Annex B's web-compat hoisting that leaks a block-level
`function` to global scope **does not apply to `async` (or generator) declarations** — they stay
block-scoped. The script rendered complete and parsed fine, yet the caller got
`hpskOpenConsolidateModal is not defined`. Both consolidation partials now **export explicitly**
(`window.x = x`) instead of relying on hoisting; do the same in any new guarded partial.

Verified 19/19 `hpsk-verify/club-receivable-consolidation-verify.mjs` and 19/19
`region-receivable-consolidation-verify.mjs`. Both assert the DIRECTION explicitly — the parent
appears in the ISSUING organisation's own list with `memberId = club-{debtor}` — because nothing else
would catch a flip. The region suite also asserts every competition behind the fordran is
region-hosted, and covers the **multi-competition** case: a debtor owing across two of the krets's
competitions gets **two** parents, one per tävling, together summing to the whole fordran. (A first
version of that test read only the first `/faktura/` link and called the total wrong — the engine was
right.) Refactor regression: organiser 31/31, club payer flow 17/17, consolidated-invoice 42/42,
persona-authorization-matrix 21/21.

Two region suites stay red and are **pre-existing**: `region-own-invoices` 11/12 (both queries hit the
`pageSize=50` cap, so `own < wide` cannot hold) and `region-invoices-tab` 9/10 (a **stale test** —
it expects the banner wording *"Visar fakturor för tävlingar i"* while the markup has said
*"…egna tävlingar"* for some time; the banner was not touched here).

### Samlingsfaktura från Anmälningar-fliken — arrangörssidan (2026-08-20)

**The samlingsfaktura was a PULL model and that was the whole problem.** Only the PAYING club could
build one, on its own klubbsida → Fakturor — the page the clubs who most need it have never opened.
Clubs that pay for several lag or several members just want a räkning to Swish/BG. Now the
**tävlingssekreteraren** can issue it from where they already stand: Anmälningar → **Åtgärder →
Betalningar → "Samlingsfaktura – en räkning till en klubb"**.

The motivation sits with the organiser (they want to be paid) rather than the club (which wants to
*not yet* pay and logs in never), and it inverts who must understand the concept: nobody outside the
sekretariat does — the club just receives one bill.

- **`InvoiceAdmin/GetCompetitionPayerClubs(competitionId)`** groups the competition's consolidatable
  invoices by the club that would pay each one. **The club is the REGISTRATION's club, not the
  member's `primaryClubId`** (see "Tävlar för"); team invoices take the TEAM's club. Eligibility is
  `ConsolidatedInvoiceService.Inspect` — never re-implement those rules, that is how the two drift.
- **Clubs with a single invoice are not offered.** The service refuses to mint a second document for
  the same money, so they would only produce a confusing "betalas direkt".
- **Two steps, and the confirm button exists only on step 2** — pick the club, then see exactly what
  will be created. Nothing can be issued from a screen that has not shown what it issues. On success
  the dialog hands over `/faktura/{id}`, which is the artefact the treasurer actually needs (belopp,
  referens, bankgiro-QR).
- **Rendering is shared with the club's Fakturor tab** via `Views/Partials/_ConsolidatePreview.cshtml`
  (`hpskRenderConsolidatePreview` / `hpskConsolidateSuccessHtml`, guarded against double-include).
  Only the rendering — each surface keeps its own selection model, because they select different
  things. Same lesson as the competition list that was rendered three times until 2026-08-18.

**Authorization: no new permission.** `PreviewConsolidation` / `CreateConsolidatedInvoices` accept the
payer side as before, OR an organiser holding the finance right on the competition **every** selected
invoice belongs to (`CanManageCompetitionInvoice` per invoice). That right already allows MarkAsPaid,
CancelInvoice and CreateCreditNote, so this grants nothing the holder did not have — a separate
"fakturabehörighet" would grant *less* than they already hold, and its failure mode is a silent
lockout on competition day. A sekretariat/kassa person who is not a club admin gets in the way that
already exists: **Bemanning app access, or named tävlingsansvarig** (both feed
`HasCompetitionManagementAccess`). Accountability comes from the audit trail, not a gate.
- `AdminAuthorizationService.CanManageCompetitionFinanceAsync(competitionId)` was extracted so the
  per-invoice and per-competition questions cannot drift; `CanManageCompetitionInvoice` now defers to it.

**⚠️ "One club at a time" is scoped to the ORGANISER path via `ConsolidationRequest.OrganiserScope`,
and that scoping was learned the hard way.** Two earlier cuts were wrong:
1. Checking the *caller* — `CanPayForClubAsync` short-circuits for a site admin, so the check silently
   did not apply to exactly the person who can see the whole field. The e2e caught it.
2. Then checking the *selection* for everyone — which broke two legitimate things at once. A club
   paying for a member registered as competing for ANOTHER club is still **one payer**; the
   registration club has nothing to do with who pays. And `consolidated-invoice-verify` previews a
   broad selection to discover what is eligible — Preview writes nothing, so refusing it is the wrong
   answer to "tell me what would happen". That cut took the suite from 42/42 to 0 eligible.

3. Then gating the check on the client's `organiserScope` flag alone — which left a **hole**, and
   `persona-authorization-matrix` caught it: on the legacy call shape an organiser could bill an
   arbitrary club (the test picked one in Östergötland) for invoices that were not theirs. Create was
   refused only by accident, because that fixture's invoice happened to be covered already.

Final shape — `SelectionBelongsToPayerClub` runs when **`!isPayerSide || request.OrganiserScope`**:
- **Not payer side** ⇒ the caller got in as the organiser, so they are not an admin of the paying club
  and must bill the club the invoices really belong to. Intrinsic, so it cannot be skipped by omitting
  a flag.
- **`OrganiserScope`** still matters for the case the first condition misses: a sekreterare who *is*
  also a club admin of the paying club would otherwise fall through to the club page's deliberately
  permissive path.
- The club's own Fakturor page sends neither and is byte-for-byte unchanged (42/42).

What guarantees one bill to one club at the structural level is the single `payerClubId` per action;
this check adds the organiser-specific property on top. The flag is not a security boundary — the
finance right is — and it only ever tightens.

Verified 31/31 `hpsk-verify/organiser-consolidation-verify.mjs` — drives the real menu → modal →
picker → preview → create, then checks server-side that the children are covered, that the audit
trail recorded it, and that a cross-club selection is refused **and writes nothing**. The guard is
tested BEFORE anything is consolidated: afterwards the invoices are ineligible for their own reasons
and a mixed selection would be refused by the wrong rule, passing while proving nothing.
Adds C# → full rebuild. No SQL, no doctype property.

### Escaping i Fältskyttes vyer + kretsgränsen på fälttävlingar (2026-08-25)

**Shooter and club names went straight into `innerHTML` and into three `document.write` print
windows.** The member sets their own name and the organiser opens the print: stored XSS from low
privilege to higher. A plain `<` also broke the printout outright.

**⚠️ The dangerous one was an ONCLICK, not the visible rows.** The "lägg till skytt" button built a
JS string literal out of the name *inside the attribute* and escaped **single quotes only**. A double
quote in a name terminates the attribute; a trailing backslash escapes the literal's own closing
quote. That is code execution in the organiser's browser, not a broken layout. It now passes an
**INDEX** into the fetched list (`faltAddShooterByIndex`), so no user text reaches the attribute —
same shape as `hpskRemoveOrphanRow`. **Never build a JS literal in an attribute from user text; pass
an index.**

**`Views/Partials/_HtmlEscape.cshtml` holds `hpskEsc()`, once.** Three copies of an escaper already
existed (FaltskytteStartListManagement, FaltskytteConfigurationEditor, FaltskytteConfigurationHub)
and the sites that needed one most had none — *a helper living in the same file as the bug it
prevents is not a safeguard.* **Included by the PARTIALS themselves, not by the host pages**, so it
travels with the code that needs it and cannot be forgotten when a partial is added to a new surface.
Guarded against double include.

**⚠️ Four of the sites a grep turns up must NOT be escaped:** one `textContent` assignment and three
`confirm()` dialogs. Escaping there is actively wrong — it would show `&lt;` to the user. A source
check must filter on **HTML context** (`document.write`, `innerHTML`, or a literal opening a tag),
not on "no `confirm(` on this line": a dialog string can be built in a multi-line ternary and passed
to `confirm` two lines later.

**`FaltskytteController.IsAuthorizedForCompetition` let ANY regional admin manage EVERY Fältskytte
competition in the country** — 35 endpoints (patrols, station config, results, shoot-offs) — because
it read `GetManagedRegions()` and returned true on `regions.Any()`, while every other surface asks
about *this* competition's region. Confirmed a bug with Stefan 2026-08-25. It now delegates to
`AdminAuthorizationService.HasCompetitionStaffAccessAsync`, which handles **both host shapes**
(club-hosted, where `IsClubAdminForClub` folds in that club's regional admins; and region-hosted,
`clubId` unset — the SM shape). Writing the host check by hand is what has gone wrong here
repeatedly. Skjutledare stay in deliberately. **`IsAuthorizedForCatalog` keeps `regions.Any()`** — the
figure catalogue legitimately asks "do you administer any krets at all"; that is not a competition.

Verified 22/22 `hpsk-verify/faltskytte-escaping-verify.mjs` (A/B: 11 of 22 fail on baseline) and
**25/25 `faltskytte-auth-region-verify.mjs`** (A/B: 7 of 25).

**Both gaps this section used to declare are now CLOSED (2026-08-25, later the same day), after
Stefan created a dev site-admin account.** What is proven, and what still isn't:
- **The cross-region refusal is proven.** `faltskytte-auth-region-verify` section 4 grants
  `RegionalAdmin_<other krets>` to a plain member as site admin, confirms the role actually sits on
  them, and asserts all four gated endpoints plus `/competitionmanagement` refuse — then revokes and
  asserts the revocation. **A/B: on baseline an Ankeland admin really did read Halland's patrols,
  station config, shoot-off status and station QR.** It also carries a control probe, because a green
  refusal can equally mean the account is simply logged out.
- **The shooter's NAME is proven in the DOM — but only on the precision editor**, by
  `name-escaping-dom-verify.mjs`. See the *En plats på startlistan är per (skytt, klass)* section.
- **⚠️ Still NOT reachable on the Fältskytte surfaces, and this is a property of the data flow, not
  of the test:** `Faltskytte/SearchAvailableShooters` returns the REGISTRATION's snapshot name
  (`r.MemberName`) and `FaltskyttePatrolMember.MemberName` is likewise a snapshot, so renaming a
  member changes nothing on the patrol lists, the print window or the add-shooter list. Getting
  hostile text there means re-registering or regenerating patrols. What IS asserted is the structural
  fix — every row's onclick is `faltAddShooterByIndex(n)`, so no user text reaches the attribute at
  all — plus the patrol label's end-to-end proof through list and print.
- **⚠️ The dev site-admin login is `admin.claude@pistol.nu` / `123456`.** Its `cmsMember.LoginName`
  was `adminclaude` and its password was not the shared dev one, so it could not be logged in with at
  all; both were corrected directly in the dev DB on 2026-08-25 (the original hash is kept in that
  session's scratchpad). Dev only — the account does not exist in prod.

Adds C# → full rebuild.

### Föräldralösa startlisterader + rätt resultattabell (2026-08-25)

**The coverage panel named the orphaned rows and offered no way to fix them** — the same criticism
we levelled at the old krets invoice page. Worse than the usual visible-but-not-actionable, because
the row could not be cleared by hand either: `RemoveShooterFromStartList` refuses a shooter who has
results, and those results are unreachable once the registration for that class is gone. The row was
stuck.

**`RegistrationAdmin/RemoveOrphanStartListRow` is SCOPED TO ONE CLASS.** A shooter can hold a
perfectly good place in C1 while their A1 row is the orphan; clearing the shooter wholesale would
delete a start they are entitled to. Fältskytte scales it to the WEAPON GROUP instead, because a
patrol walks the course once.
- **It re-derives the orphan status server-side before writing.** The client's list can be seconds
  stale and this DELETES: if a registration exists for that class the row is a legitimate start and
  must not be removed by a button meant for clearing leftovers.
- **⚠️ The class-scoped delete must carry ShootingClass in the WHERE.** All three tables (result,
  shoot-off, DNS/DNF status) are keyed on (competition, member, CLASS), so a scoped delete without it
  would wipe a class the shooter IS entered in. The comparison strips whitespace, because the class is
  stored as an ID ("C1") but written as a display NAME ("C 1") by `ChangeShooterClass`.
- The panel also names the other option — add the class to the registration via Redigera anmälan —
  so removal does not read as the only path.

**⚠️ Two queries in `PrecisionStartListController` hardcoded `PrecisionResultEntry`** — the removal
guard (:1175) and `HasResults` (:2166). On **Duell / Milsnabb / MagnumPrecision / NationellHelmatch**,
whose rows live in their own tables, both answered "no results": the guard protected nothing, so a
shooter with results could be removed from the start list and their results orphaned, silently. Both
now go through `CompetitionResultTables`. **No other discipline is affected** — every discipline has
its OWN `HasResults` and each view calls its own, so the shared endpoint is only ever asked about the
precision family. A/B: the shared query answers 0 on baseline where the discipline's own answers 4
(Fältskytte) and 13 (Springskytte).

**⚠️ `DeleteResult` matches on ShootingClass but `ValidateDeleteRequest` does not require it.** A
caller that omits it deletes nothing and used to be told *"Inget resultat hittades att ta bort"* — a
statement about the DATA when the truth was a statement about the REQUEST. That misattribution cost a
debugging round: a row that plainly existed reported itself absent, and the removal guard looked like
it was contradicting the database. The message now names the missing field.

**Fixed 2026-08-25 (same day, own section below):** `AddShooterToStartList` checked MEMBER, not
(member, class). See *En plats på startlistan är per (skytt, klass)*.

Verified 20/20 `hpsk-verify/orphan-row-cleanup-verify.mjs` and 6/6 `result-table-lookup-verify.mjs`.
⚠️ The orphan suite **builds its own orphan and removes exactly that one** — its first version ate
its fixture by cleaning dev of every orphan, and then failed on the next run with "ingen föräldralös
rad i dev". Building the state you measure is the only repeatable shape here.

Adds C# → full rebuild. No SQL, no doctype property.

### En plats på startlistan är per (skytt, klass) — inte per skytt (2026-08-25)

**`AddShooterToStartList` vägrade en medlem som redan stod på listan**, för att dubblettkontrollen
frågade om MEDLEMMEN och inte om (medlem, klass) — trots att generatorerna rutinmässigt placerar
samma skytt i A2, B2 och C2 på samma tävling. **Redigeraren kunde alltså inte göra vad generatorn
gör:** en skytt som efteranmälde sig i en andra klass gick inte att placera alls, och enda vägen att
lägga till den ENA raden var att generera om hela listan — vilket flyttar allas skjutlag och tider.

**Och borttagningen hade samma fel, spegelvänt.** `RemoveShooterFromStartList` tog den FÖRSTA raden
som matchade medlemmen, tvärs över alla skjutlag, och svarade *"Skyttan har tagits bort."* Ombedd att
städa en kvarglömd C1-rad kunde den lika gärna radera en fullt giltig A2-plats. **Det var inte
teoretiskt** — den nya svitens egen städning gjorde precis det på dev och rapporterade lyckat
resultat; `repair-2576-startlist.mjs` finns kvar från den återställningen.

- **Nyckeln är `CoverageKeys.Canonical`, inte en literal jämförelse.** Klassen lagras som ID ("C1")
  men skrivs som visningsNAMN ("C 1") av `ChangeShooterClass`, så en rak strängjämförelse missar
  dubbletten för varje klass där de skiljer sig — och skulle alltså släppa igenom samma start två
  gånger. Samma nyckel som `StartListCoverage` och `StartListCleanup` använder.
- **Utan klass i begäran: neka och NAMNGE fältet.** Att svara "skyttan finns redan" på en begäran som
  bara utelämnade klassen är ett påstående om DATAT när sanningen är ett påstående om BEGÄRAN — exakt
  den felattributionen som redan kostat en felsökningsrunda på `DeleteResult`. Borttagningen nekar på
  samma sätt när medlemmen har flera placeringar, och räknar upp dem. **Den gissar aldrig**, eftersom
  en gissning här förstör en start operatören inte frågade om.
- **⚠️ Resultatgrinden i borttagningen måste ha SAMMA klasstuktur.** Resultatrader är nycklade
  (tävling, medlem, klass, serie), så en medlemsbred räkning lät resultat i A2 blockera borttagningen
  av en kvarglömd C1-rad som skytten inte ens är anmäld i — den föräldralösa raden som då inte gick
  att städa via UI:t alls. Filtret ligger i C# via `Canonical`, inte i SQL: ett handrullat
  `UPPER(REPLACE(...))` är en andra normalisering som är fri att glida från den alla andra ytor använder.
- **Klienten skickar klassen och SKRIVER UT den i dialogen.** "Ta bort NN från startlistan" säger
  inte vilken av skyttens starter som försvinner.

**⚠️ Samma XSS-form som Fältskyttes onclick (`12e0578`) satt i syskonvyn — och den var exploaterad,
inte teoretisk.** `CompetitionStartListManagement.cshtml` byggde tre onclick-attribut som
`'${shooter.name.replace(/'/g, "\\'")}'` (bara enkelfnuttar) och skrev dessutom namnet rakt i
innerHTML. A/B mot den ofixade vyn: `window.__pwned` **kördes** och ett `<img src=x>` skapades i
arrangörens redigerare. Raderna läser nu skytten från **data-attribut** via
`removeShooterFromTeamFor` / `openMoveShooterModalFor` / `openEditWeaponClassModalFor`, så ingen
användartext når ett attribut. Partialen inkluderar `_HtmlEscape` själv, och dess fjärde handrullade
escaper (`escapeHtmlInline`) delegerar nu till `hpskEsc`.

**⚠️ `AddShooterToStartList` stämplade varje tillagd skytt "Okänd klubb".** Den läste
`GetValue<int>("primaryClubId")`, och egenskapen är en STRÄNG — konverteringen sker inte, värdet blir
tyst 0. Syns på den publika startlistan och, eftersom resultatlistan läser startlistan först, i
resultaten. Går nu via `MemberClubService.GetPrimaryClubId`. Samma fälla som gav varje
walk-in-anmälan `clubId=0`.

**Kartan gren → resultattabell har inga kopior kvar.** `CompetitionResultTables` fick
`ForSharedResultEndpoint`, och de två sista handhållna switcharna (`CompetitionResultsController`
och `PrecisionStartListController`, den senare med kommentaren "keep the two in sync") delegerar dit.
Den **löser precisionsfamiljen exakt som förut**, inklusive tom/okänd typ → Precision som äldre noder
förlitar sig på, och **kastar för Fältskytte/MagnumFält**. ⚠️ Att i stället bara peka dem på `For()`
hade varit sämre, inte bättre: DELETE:n och klassbytets UPDATE hade då börjat adressera VERKLIGA
`FaltskytteResultEntry`-rader med ett klass-scopat WHERE. En fälttävling som når hit är ett
anroparfel åt båda hållen — säg det i stället för att gissa; varje anropsplats ligger redan i en
try/catch som rapporterar fel till operatören.

Verifierat 24/24 `hpsk-verify/multiclass-add-shooter-verify.mjs` (**A/B: 9 av 19 faller på baseline**)
och 29/29 `name-escaping-dom-verify.mjs` (**A/B: 7 av 29**, inklusive att koden faktiskt kördes).
⚠️ **Läs namnsvitens huvud innan den ändras** — den bevisar precisionsredigeraren i DOM:en men
Fältskyttes "lägg till skytt" bara STRUKTURELLT: `SearchAvailableShooters` returnerar ANMÄLANS
snapshot-namn, inte medlemsregistret, så ett namnbyte når aldrig den ytan. Regression: escaping 22/22,
auth-region 25/25, orphan-row 20/20, result-table-lookup 6/6, startlist-aware-delete 26/26,
startlist-coverage 27/27, dnsdnf-ui 13/13, row-action-menus 60/60, action-menus-sweep 113/113.

Adds C# → full rebuild. No SQL, no doctype property.

### Publicerad startlista kan stänga självanmälan — och "Lägg till efteranmäld" är borta (2026-08-31)

**Problemet Stefan beskrev:** en skytt dyker upp oanmäld strax före start. Anmäler hen sig själv på
tävlingssidan hamnar anmälan utanför den redan genererade startlistan, och den enda knapp en
funktionär tillförlitligt hittar på Startlistor-fliken är **"Skapa ny startlista"** — som ger alla
andra NYA startnummer.

**Rätt väg fanns redan och är oförändrad:** Anmälningar → Åtgärder → **Anmäl och betala**. Den
skapar anmälan OCH faktura, och lägger skytten sist i valt skjutlag via
`AssignWalkInToStartListTeam` (Fältskytte: patrullväljaren). Ingen annans nummer rörs.

**⚠️ "Lägg till efteranmäld" på Startlistor-fliken är BORTTAGEN — den var en fälla, inte en
genväg.** Den anropade `AddShooterToStartList`, som bara skriver en RAD i startlistan: ingen
anmälan, ingen avgift. Skytten stod på listan, dök upp som "på listan utan anmälan" i
täckningspanelen, och sköt gratis. Att knappen låg först och hette något som lät rätt gjorde den
till den väg funktionären naturligt tog. **Varningstexten ovanför pekade dessutom i klartext på
den** och överlevde en första borttagning av själva knappen — en instruktion till en knapp som inte
fanns; sviten assertar därför på TEXTEN och inte bara på `#addLateShooterBtn`.
**Endpointen är kvar** och används av redigeraren, där den är rätt verktyg: att placera en REDAN
anmäld skytt som saknar plats.

**Grinden: `Services/RegistrationGate/StartListRegistrationGate`.**
- **⚠️ HÄRLEDD, aldrig speglad:** `(arrangörens val) AND (en startlista är publicerad just nu)`,
  utvärderat vid varje läsning. En första utsåga lagrade en enda "anmälan stängd"-boolean som
  vändes vid publicering och nollställdes vid avpublicering — det är en spegel, och speglar i den
  här kodbasen ruttnar så fort en av två skrivare missas (jfr `scoringMode`-driften). Härlett
  betyder att en avpublicering öppnar anmälan igen utan en andra skrivning att komma ihåg, och att
  Springskyttes publicering PER VAPENKLASS inte kan lämna flaggan påstående "öppen" medan en lista
  fortfarande är publik.
- **Publicerad lista** läses ur den PUBLICERADE cachen: `faltskyttePatrolsPublished` (Fältskytte,
  flagga på tävlingen) eller någon `precisionStartList`-barnnod med `isOfficialStartList`
  (precisionsfamiljen OCH Springskytte, som delar doctype). Ett draftvärde betyder att listan inte
  är publik, och en grind som slog till på det hade stängt anmälan för en lista ingen kan se.
- **Fail-open** vid uppslagsfel: en skytt som inte kan anmäla sig är ett supportärende om en trasig
  tävling; en som slinker igenom är en rad täckningspanelen redan flaggar.

**Valet görs i publiceringsdialogen, inte automatiskt.** `Views/Partials/_PublishStartListDialog.cshtml`
är EN dialog delad av alla tre grenarna (den ersatte tre `confirm()` som sa olika saker om samma
handling) och inkluderas av startliste-partialerna SJÄLVA, samma regel som `_HtmlEscape`.
- **Förkryssad vid FÖRSTA publiceringen, speglar tidigare val vid ompublicering.** Annars hade en
  liten redigering + Publicera tyst slagit på en spärr arrangören medvetet stängt av.
- `CloseRegistration` är `bool?` på alla tre publish-DTO:erna. **Null = rör inte inställningen** —
  en äldre klient, och avpubliceringen, får aldrig nollställa valet som sidoeffekt.
- **Springskytte frågar bara när den FÖRSTA listan blir officiell** (den publicerar en per
  vapenklass/dag) — annars hade arrangören fått samma dialog fem gånger.
- Fältskytte skriver valet på den `Save()` metoden ändå gör; Precision/Springskytte går via
  `PersistChoice`, som **rapporterar en misslyckad `Publish()`** i stället för att låta flaggan bli
  kvar på draften medan arrangören tror att anmälan är stängd.

**⚠️ Funktionärer undantas på BÅDA sidor.** Servern (`RegisterForCompetition`, via
`HasCompetitionStaffAccessAsync` som bär både klubb- och kretsvärdad tävling) och vyn
(`canManageCompetition`). Undantogs bara servern hade undantaget varit onåbart från gränssnittet —
arrangören anmäler rutinmässigt någon annan via samma modal (`targetMemberId`). **Vyns predikat
måste förbli en DELMÄNGD av serverns**, annars visas en knapp vars anrop nekas; det är det idag
(samma roller minus Skjutledare). Funktionären får en gul ruta om att skyttarna inte ser knappen.

**Serverkontrollen är inte valfri.** Skytten har ofta tävlingssidan öppen i en flik medan
startlistan publiceras. Den ligger FÖRE all klassvalidering, så en nekad begäran skriver ingenting.
Registreringsbordets egen väg är en annan endpoint och berörs inte.

**Lag/stafett är medvetet UTANFÖR grinden** — de ligger inte i den individuella startlistan och har
sin egen frist.

Verifierat 29/29 `hpsk-verify/startlist-closes-registration-verify.mjs`. **⚠️ Läs svitens huvud innan
den ändras — tre fällor kostade en körning:** sidan tar `?competitionId=`, INTE `?c=` (fel namn ger
200 + tävlingsVÄLJAREN, och varje "finns inte"-påstående blir grönt på en sida som aldrig visade
funktionen); två inloggningar i samma `BrowserContext` delar cookie, så funktionären loggades ut av
skytten och servern svarade "inte behörighet" — vilket såg ut som ett produktfel; och
`GetStartLists` returnerar `status: "Official"`, inte `isOfficial`, så baseline blev `undefined` och
återställningskontrollen jämförde `undefined` med `undefined` — grön och helt utan innebörd. En
släckt Anmäl-knapp betyder dessutom inte att grinden slog till: sista anmälningsdag släcker den
också, och sviten skiljer orsakerna åt. **Steg 4–8 SKIPPAR tills doctype-egenskapen finns** — en
grön körning utan den vore ett påstående som inte kan falla.

Adds C# → full ombyggnad. Ingen SQL. **En doctype-egenskap** (se listan ovan).

### Tävlingar-sidan: hitta en viss lokal tävling (2026-08-26)

Klagomål: "rörigt att hitta en viss lokal tävling". **Volymen var inte huvudorsaken** — mätt i dev
renderades 94 kort men bara **11 syntes**, eftersom statusfiltret redan gömmer avslutade. Tre
strukturella fel gjorde det däremot, och alla tre är åtgärdade:

1. **Sökfältet låg inne i den kollapsade "Mer filter"-panelen** (`offsetParent === null`). Den
   snabbaste vägen till EN viss tävling — skriv namnet — fanns inte på skärmen. Det ligger nu först
   och alltid synligt, med en rensa-knapp. ⚠️ Den gamla inputen är **borttagen**, inte flyttad: två
   element med samma `id` gör det andra dött för `getElementById`.
2. **⚠️ En tävling som ingår i en serie fanns inte på sidan över huvud taget.** `allCompetitions`
   uteslöt allt vars förälder är en `competitionSeries`, så omgången fick inget kort och därmed
   ingen `data-search-text` — **sökning på dess namn gav noll träffar**. Och det är just där lokala
   klubbtävlingar bor (en omgång i Hallandsserien). Varje omgång är nu ett vanligt tävlingskort med
   en **"Del av <serien>"**-länk, och **seriens namn ligger i kortets söktext** så en sökning på
   serien hittar dess omgångar. Seriekorten är borta från sidan — de konkurrerade om samma kortyta
   utan att göra omgångarna hittbara. Serien nås via brickan; `/competitions/{serie}/` är oförändrad.
3. **`isClubOnly`-tävlingar var uteslutna för ALLA**, även för den egna klubbens medlemmar. De visas
   nu för medlemmar i klubben, badgade **"Endast klubb"** så det går att förstå varför andra inte ser
   dem. Medlemskapet resolvas via `MemberClubService.GetAllClubIds` — **`primaryClubId` är en STRÄNG**,
   så `GetValue<int>` ger tyst 0.

**TRE sektioner, i den ordningen (utökat 2026-08-26 efter Stefans önskemål):**
`data-tier` på varje kort — **0 = Din krets** (min egen krets, eller en klubb jag är medlem i),
**1 = Nära dig** (en krets som GRÄNSAR till någon av mina), **2 = Övriga tävlingar i landet**.
Nivån är primär sorteringsnyckel, och rubrikerna renderas ur grupperna.
- **Klubbmedlemskap slår kretsen.** En medlem i en klubb i en annan krets ska hitta sin egen klubbs
  tävling under "Din krets", inte två sektioner ned.
- Rubriken blir **"Dina kretsar"** i plural när medlemmen har klubbar i flera kretsar.
- **"Din krets" namnger MIN krets; "Nära dig" namnger de kretsar som FAKTISKT ligger i sektionen.**
  ⚠️ En första version satte grannLISTAN som undertext, så rubriken sa "Älvsborg · Göteborg ·
  Jönköping + 3 fler" över en sektion som innehöll en enda tävling från Kronoberg — tre kretsnamn som
  inte fanns på skärmen. Undertexten härleds nu ur korten (`data-effective-region`), kapad vid tre
  namn eftersom en rubrik som radbryts slutar vara en rubrik.
- **"Bara nära dig" behåller nivå 0 OCH 1** — "nära dig" utan den egna kretsen vore en konstig fråga.

**`Models/RegionAdjacency.cs` är gränsschemat**, och det är **handskriven geografi** — inget register
levererar det. Kretsarna följer i stort de gamla länen (Skåne delat i Malmöhus/Kristianstad, Kalmar i
Norra/Södra, Västra Götaland i Göteborg-Bohuslän/Älvsborg/Skaraborg/Västgöta-Dal).
- **Kanterna deklareras EN gång som par och speglas i konstruktorn.** Att skriva båda riktningarna
  för hand är dubbelt så mycket data och gör en ensidig gräns möjlig — vilket ger det märkliga
  utfallet att A är nära B men inte B nära A.
- **Gotland får havsgränser** (Kalmar N/S, Östergötland, Stockholm, efter färjelägena). Alternativet
  är att ön aldrig har någon granne, vilket gör hela sektionen meningslös just där.
- **Ankeland har medvetet inga grannar** — påhittad krets för demo/testdata (jfr demoklubben
  Ankeborg), och ska inte dra in riktiga kretsars tävlingar.
- `NeighboursOfAny` **utesluter de egna kretsarna**, så samma tävling aldrig kan hamna i två sektioner.
- Uppslag är skiftlägesokänsligt, eftersom `NormalizeRegionCode` gemenar koden på en del kodvägar.
- **14 test i `RegionAdjacencyTests`** kontrollerar FORMEN — varje kod finns i enumet, varje gräns är
  ömsesidig, ingen dubblett, ingen självgräns, ingen oavsiktlig ö. **Geografin kan de inte
  kontrollera**; ett par som ser fel ut ska rättas i `Borders` och listan är ordnad söder → norr just
  för att gå att läsa mot en karta.

**⚠️ En region-lös serieomgång ÄRVER sina syskons kretsar.** Kretsen härleds ur `clubId` eller
`regionalFederation`, och på en del omgångar är ingen satt. Förut syntes det bara som ett tomt
regionfilter (som tolererar tomt) — men nu AVGÖR kretsen vilken sektion tävlingen hamnar i, och utan
arvet landade en Halland-medlems egna Hallandsserie-omgångar under "Övriga tävlingar i landet",
vilket är precis klagomålet. Mätt: två omgångar flyttade från Övriga till Din krets.

**"Nära dig" är en SORTERING, inte ett standardfilter.** Kort med `data-near-me="1"` (tävling hos en
klubb jag tillhör, eller i en krets någon av mina klubbar sitter i) sorteras först, med två rubriker
som säger var gränsen går. **Ingenting göms** — ett standardfilter som tyst gömmer tävlingar är vad
som genererar "min tävling är borta", och användarbasen är delvis datorovan. Den som vill smalna av
trycker **"Bara nära dig"** (opt-in, och Rensa filter lyfter det).
- ⚠️ **Rubrikerna måste räknas PER CONTAINER.** `filterCompetitions()` plockar upp
  `.competition-card` **globalt**, alltså både kortvyns kort och listvyns rader — samma tävling två
  gånger. Första utsågan visade "14 st" när sju kort syntes, vilket läser som att sidan räknar fel
  på tävlingarna. `renderSectionHeadings` grupperar på `parentNode`.
- ⚠️ **Rubrikerna återskapas EFTER sorteringen.** `sortCards` flyttar korten genom att appenda dem
  på nytt, så en rubrik som redan låg i flödet hamnar på fel plats.
- **Ingen krets eller klubb → ingen sektion och ingen knapp.** Utloggad, eller medlem utan klubb, får
  exakt den gamla platta listan i stället för en tom "Nära dig"-rubrik.
- Kretsnamnet i rubriken går via `Federations.GetDescription` (koden lagras som enum-NAMN "Halland",
  människor känner igen "Hallands Pistolskyttekrets"), med koden som reserv.

**Kartan:** det server-genererade `seriesChildCompetitionsForMapJson`-tillägget är **borttaget**. Det
fanns just för att serieomgångar inte var kort; nu är de det, så tillägget hade räknat varje omgång
**två gånger** — och det lydde inte filtret.

**Rättelse av en tidigare felläsning:** region-filtret släpper INTE region-lösa tävlingar. Rad
`cardRegion === ''` i `filterCompetitions` visar dem när någon krets är valt. 36 % av korten i dev har
tom region (en series region härleds ur barnens `clubId`/`regionalFederation`), men hålet finns inte.

Verifierat 34/34 `hpsk-verify/competitions-hub-findability-verify.mjs` (**A/B: 12 faller mot den
tvåsektionsversion som föregick den, och 12 mot originalet**), 9/9
`competitions-hub-clubonly-verify.mjs` och 14/14 `RegionAdjacencyTests`. Den senare **bygger sin egen fixtur** — dev har noll
`isClubOnly`-tävlingar, så grenen kan inte mätas på befintlig data — och raderar den igen.
⚠️ `CreateCompetition` returnerar id:t under **`data.id`**, inte `competitionId`; en första version
läste fel nyckel, fick 0, och lämnade fixturen kvar medan raderingen svarade "Competition not found".

**Gränsschemat är C# → full ombyggnad krävs.** Ingen SQL, ingen doctype-property, ingen Umbraco-nod.

**Kvarstående UX-observation, inte åtgärdad:** `#clearFilters` ligger själv inne i den kollapsade
"Mer filter"-panelen. Den som smalnat av med den synliga "Bara nära dig"-knappen hittar alltså inte
den synliga vägen tillbaka — knappen själv är vägen, men "Rensa filter" borde ligga på primärraden.

### shootingClassIds lagrades som CSV av tävlingsguiden (2026-08-26)

Konventionen högre upp i den här filen är entydig: `shootingClassIds` MÅSTE lagras som en
**JSON-array** (`["C1","C2"]`), aldrig CSV. Ändå fick varje tävling skapad via **tävlingsguiden**
`C1,C2,C3`. Upptäckt genom att titta i databasen på en Standardpistoltävling.

**⚠️ Orsaken är en typ som aldrig kan matcha.** `fields` deserialiseras till
`Dictionary<string, object>`, så System.Text.Json ger **`JsonElement`** — aldrig `string`.
Konverteringen testade:
```csharp
if (value is string stringValue) { /* CSV → JSON */ }
else if (value is JsonElement el && el.ValueKind == JsonValueKind.Array) { /* array → JSON */ }
```
Guiden skickar klasserna som en CSV-**sträng** (`wizard_shootingClassIds.value = selected.join(',')`),
alltså ett `JsonElement` med `ValueKind == String` — som matchar **ingen** av grenarna. Det råa
elementet lagrades, och dess `ToString()` är CSV:n. `value is string` var död kod från början.

**Tyst i åratal, eftersom varje LÄSARE bär en CSV-fallback.** Ingenting gick fel; det syntes bara i
databasen. Konventionen finns ändå av ett skäl (ett klassnamn kan innehålla komma) och det finns en
migreringsendpoint just för att städa CSV-rader.

`Models/ShootingClassIdsValue.cs` är nu THE one place den konverteringen bor — den låg i **fyra**
kopior, tre i `CompetitionAdminController` (skapa/kopiera/annons, alla med luckan) och en i
`PrecisionCompetitionEditService`. **Redigeringsvägen var korrekt hela tiden**, för den gör
`value.ToString()` innan den konverterar; den delegerar ändå nu, så de inte kan glida isär.
`FromText` skickar igenom en redan JSON-kodad array oförändrad — kontrollen är "börjar med `[`" och
inte en parse, just för att ett dubbelkodat värde inte ska uppstå av att någon sparar en gång till.

**⚠️ Migreringsendpointen `FixShootingClassIdsFormat` hade ALDRIG fungerat.** Den letade hubben med
`_contentService.GetRootContent().FirstOrDefault(c => c.ContentType.Alias == "competitionsHub")` —
men `GetRootContent()` returnerar noder på ROTNIVÅ, alltså bara `Home`, och `competitionsHub` är ett
BARN till Home. Den svarade därför `"Competitions hub not found"` varje gång den anropades, sedan den
skrevs. Nu använder den samma uppslag som `CreateCompetition` och samma normaliserare som skrivvägen.
Körd i dev: **18 rättade, 64 redan korrekta, 0 fel** av 96 tävlingar.

**⚠️ Läs INTE lagringsformen via `CompetitionEdit/GetCompetitionData`** — den normaliserar medvetet
till CSV (`GetShootingClassIdsString`), eftersom redigeringsformulärets dolda fält vill ha CSV. En
kontroll måste gå till databasen, och **värdet ligger i `varcharValue`, inte `textValue`**; en fråga
på bara `textValue` ger NULL och det ser ut som att ingenting sparades.

Verifierat 12/12 `hpsk-verify/shootingclassids-json-verify.mjs` (CSV-sträng, JSON-array-sträng och
riktig JSON-array round-trippar alla till exakt C1, C2, C3 utan dubbelkodning) plus SQL-kontroll av
lagringsformen: alla tre lagrar `["C1","C2","C3"]`.

**KÖR I PROD efter deploy:** `GET /umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat`
(sajtadmin). Den är idempotent och rapporterar antal. Tävlingar i papperskorgen migreras inte —
migreringen går genom tävlingsnavets underträd, vilket är rätt.

### "Redan anmäld i…"-brickan följde inte den valda skytten (2026-08-26)

**Rapporterat på en Standardpistoltävling: efter att en skytt anmälts stod den gula brickan kvar och
det såg ut som att ingen mer gick att anmäla.**

⚠️ **Det var INTE grenspecifikt och inte nytt med de nya tävlingstyperna.**
`wwwroot/js/competition-registration.js` delas av hela precisionsfamiljen och har **noll**
grenberoende — `grep -cE "Standardpistol|Sportpistol|Milsnabb|MagnumPrecision|competitionType"` ger
**0**. Senaste ändringen i filen före fixen var `43e361c` (2026-08-16, "Tävlar för"-arbetet).
Att den nya grenen var där det upptäcktes betyder bara att det var där någon råkade byta skytte
mitt i ett anmälningspass.

**Två av tre vägar släppte inte förra skyttens tillstånd.** `queryExistingRegistrations()` anropades
bara när en NY skytt valdes:
- **Byte av KLUBB** (`handleClubSelection`) nollade `selectedTargetMemberId` men lämnade
  `existingRegistrations`, `weaponClassConflicts`, brickorna OCH **de förkryssade klasserna** kvar.
  Det är den allvarliga halvan: nästa skytt ärvde föregåendes klassval och kunde anmälas i den.
- **Tomma "Välj medlem…"** (tidiga returgrenen i `handleMemberSelection`) lämnade brickan kvar över
  ett tomt formulär.
- Byte av MEDLEM fungerade redan, vilket är precis därför felet var lätt att missa.

`clearExistingRegistrationState()` är nu den enda platsen som släpper tillståndet, och anropas från
båda vägarna. Den utnyttjar att `addExistingRegistrationBadges()` börjar med att ta bort alla
brickor — med tom `existingRegistrations` lägger den inte tillbaka några.

**⚠️ Regressionen som måste finnas med:** att brickan **kommer tillbaka** när den anmälda skytten
väljs igen. Utan det påståendet är "inga brickor" grönt även om funktionen tagits bort helt — och
brickan är det som hindrar en dubbelanmälan.

Verifierat 10/10 `hpsk-verify/registration-badge-reset-verify.mjs`; **A/B: 3 faller på ofixad kod**,
exakt de tre som beskriver felet. Läser bara — anmäler ingen, ändrar ingenting.

**Endast en JS-fil → ingen ombyggnad krävs.**

**⚠️ Rättelse av ett "fynd" som inte var ett fel.** Under felsökningen ovan svarade tävlingens
snygga URL (`/competitions/2026/halland/falkenbergs-pistolklubb/standarden/`) **500** medan trädets
URL gav 200, och det rapporterades som en misstänkt bugg i URL-provideren. **Det var transient.**
Omkontrollerat 6 gånger i rad direkt efteråt: **200 varje gång.** Enda 500:an inträffade i samma
fönster där appen låg i innehållslåskonflikt precis efter en omstart (se
[[umbraco-content-lock-teardown]]).

Lärdomen är värd mer än fyndet: **ett enstaka 500 från en nyss omstartad dev-app är inte ett fynd.**
Mät om innan det rapporteras, särskilt när appen samtidigt visat sig ha låsproblem — annars går tid
åt att leta efter en bugg som inte finns.

*Separat, verkligt och orelaterat:* dev-loggen bär ~220 felrader från Examine,
`FileNotFoundException` på `umbraco/Data/TEMP/ExamineIndexes/MembersIndex/_1bu.si` — ett korrupt
medlemsindex i dev. Åtgärdas genom att bygga om indexet i backoffice; rör inte prod och har inget
med det här arbetet att göra.

### Roller som redigeraren aldrig visade fick inte tas bort av den (2026-08-26)

**En sajtadmin som öppnade medlemsmodalen på en klubbadmin och tryckte Spara tog TYST bort hens
`ClubAdmin_*`-roll.** Tre rimliga beslut som tillsammans blev en bugg: `MemberAdmin/GetMember`
filtrerade bort `ClubAdmin_*` ur `groups`, `GetMemberGroups` filtrerade bort dem ur kryssrutelistan
("to reduce response size and UI clutter") — men `SaveMember` diffade de POSTADE grupperna mot
`GetAllRoles`, alltså mot **alla** roller medlemmen har. Klienten kunde bevisligen inte posta
tillbaka det den aldrig fick. A/B: 2 klubbadmins på klubb 2604 blev 1 vid ett enda Spara, utan
felmeddelande och utan att något på skärmen antydde att grupper ändrades.

**`GetGroupEditorGroupNamesAsync` är nu den ENDA plats som bestämmer vad gruppredigeraren erbjuder,
och `SaveMember` får bara ta bort roller ur den mängden.** Att i stället bevara prefixet `ClubAdmin_`
explicit hade lagat exakt det här fallet och lämnat FORMEN kvar — felet är inte prefixet, utan att en
diff tar bort det som aldrig erbjöds. Filtrera bort något i den metoden och det är skyddat i
sparandet gratis; invarianten är strukturell i stället för en prefixlista att hålla i takt.
`GetMember` läser samma mängd, så förkryssningen och skyddet kan inte glida isär.
- **`rolesToAdd` lämnades ORÖRT, medvetet.** Där finns ingen tyst förlust, och att börja tysta bort
  en icke-erbjuden tilldelning skulle införa precis den sortens tysta bortfall som fixen handlar om.
- **De andra scope-rollerna hade INTE buggen:** `RegionalAdmin_*`, `Skjutledare_*`,
  `Foreningsinstruktor_*`, `Kretsinstruktor_*`, `Riksinstruktor_*` ligger i
  `_memberGroupService.GetAllAsync()`, alltså i kryssrutelistan, och rundgår korrekt. Bevisat på
  vägen: `Riksinstruktor_Syd` överlevde samma sparning som åt `ClubAdmin_2604`. De är ändå skyddade
  nu, eftersom skyddet följer "erbjöds den?" och inte en lista över prefix.

**Väntande på Anmälningar räknades per FAKTURARAD på lagsidan.** Individsidan fick definitionen
"vad ANMÄLAN är skyldig (avgift − betalt)" i `f4d425d`; lagsidan buckettade fortfarande på
fakturastatus, alltså den form som gjorde att rubriken sa 1000 kr medan Lag-listan under visade 600.
`GetCompetitionTeams` returnerar nu `paidAmount` + `outstandingAmount` med samma definition, och
klienten totaliserar båda slagen med en regel. ⚠️ Endpointen läste bara den NYASTE icke-makulerade
fakturan, vilket ger fel skuld åt vilket håll id:na råkar falla när ett lag har en betald och en
kvarglömd väntande. Basen är AVGIFTEN, med det fakturerade beloppet som reserv bara när tävlingen
inte bär någon avgift för lagtypen — annars läser ett gammalt lag vars avgift nollats "0 kr skuld".

**Delade partialer i stället för en fjärde handskriven kopia:** `_ScreenWakeButton.cshtml`
("Håll skärmen vaken") och `_ConnectionBadge.cshtml` (`hpskSetConnection(ok)`). Tre skärmar bar redan
var sin kopia av wake-lock-dansen med egna variabelprefix; de ligger kvar, men partialen är den att
konvergera mot. Två regler som är hela poängen med att de finns:
- **Låset MÅSTE återtas på `visibilitychange`** — OS:et släpper det varje gång fliken göms, så utan
  det ser knappen påslagen ut och gör ingenting efter första flikbytet.
- **Rapportera FETCH-utfallet, aldrig `navigator.onLine`** — en telefon ansluten till banans AP utan
  uppström rapporterar `onLine === true`, vilket är exakt det läge badgen finns för. En avvisad
  sparning (`success:false`) räknas som UPPKOPPLAD; att blanda ihop de två skickar operatören på
  nätverksjakt när det var datat som var fel.
- Partialen visar tillstånd med `.active` + `aria-pressed`, inte genom att byta in en bestämd
  Bootstrap-variant: anroparen väljer variant (den mörka `/live`-tavlan skickar `btn-outline-light`),
  och två varianter på samma knapp låter stilmallens ordning avgöra utseendet.
- ⚠️ **Precisionens `/station`-padda är `CompetitionResultsManagement` med `IsStationPage`**, inte
  `DistributedResultEntry.cshtml` (som är en självrapporteringsmodal). Knappen sitter bara i
  `isStationPage`-grenen. **`SkjutledareView` gör noll serveranrop** — en uppkopplingsbadge där
  skulle rapportera om ingenting.
- **Fynd på vägen:** Fältskyttes omskjutnings-autospar rapporterade ingenting alls vid fel
  (`catch { console.error }`) — raden visade det nya antalet medan servern aldrig hörde om det.

**⚠️ Omnumrering av patruller flyttade inte resultatradernas patrullnummer.**
`FaltskytteResultEntry` bär en KOPIA av `PatrolNumber` och ingenting höll de två i takt, så en
omnumrering lämnade varje redan inmatat resultat pekande på det nummer patrullen HADE.
`FaltskytteStatsController` joinar resultatrader mot patruller på just `PatrolNumber` (`:51-57`), så
flödesstatistiken krediterade varje patrulls sträcktider till en annan patrulls vapengrupp. **Tyst**,
eftersom resultaten i sig förblir riktiga och bara ATTRIBUTIONEN ruttnar. `RenumberAllPatrolsAsync`
returnerar nu mappningen och migrerar raderna i samma operation, tvåfasat av samma skäl som
patrullerna: ett gammalt och ett nytt nummer kan kollidera mitt i vandringen (patrull 3 blir 2 medan
den verkliga 2 inte flyttat än). **Ett läge går inte att lösa** — delade två patruller nummer är
deras resultatrader oskiljbara, för raderna registrerade bara numret; det larmas om i förväg i
stället för att låta migreringen framstå som förlustfri i alla lägen.
Ny läsande `Faltskytte/PreviewRenumberPatrols` namnger vad som ändras (gammalt → nytt, vapengrupp,
etikett, antal resultat), listar dubblettnummer, och använder **samma `ORDER BY`** som själva
omnumreringen — annars kan de två vara oense om vilken patrull som blir vilket nummer. Spåret är en
`LogInformation` med hela mappningen; en ny kolumn skulle behöva en migrering för att säga samma sak.

Övrigt i samma omgång: Fältskyttes Startlistor-flik läser om på `shown.bs.tab` (den renderade en gång
vid sidladdning och var inaktuell i samma sekund anmälningsbordet registrerade någon), skriv-in-rader
i print-CSS på startlista och patrullista, och den föräldralösa `StationInfoCard.cshtml` raderad.

Verifierat 48/48 `hpsk-verify/carryover-batch-verify.mjs`, **A/B: 21 faller på baseline**.
⚠️ **Punkt 1 i den sviten är den enda vars A/B FÖRSTÖR dev** — där tar sparandet verkligen bort
rollen. Två A/B-körningar tömde klubb 2604 på båda sina klubbadmins innan självreparationen fanns,
och enda spåret var `Current roles:`-raderna i apploggen. Ta inte bort självreparationen.
Adds C# → full rebuild. Ingen SQL, ingen doctype-property, ingen Umbraco-nod.

### En skytts klass är EN sträng — `ShootingClasses.ToCanonicalName` (2026-08-25)

**Klubbmästerskapet 2026-08-25 (tävling 3706) listade veteranerna TVÅ gånger** i resultatlistan: en
rad med grundserierna 1–7 och en med finalserierna 8–10, och båda visade samma klass. C1/C2/C3 såg
helt riktiga ut.

**Orsaken är att klassen finns i två former** — `ShootingClass.Id` (`C_Vet_Y`, `C_Vet_A`, `A_opt_1`)
och `ShootingClass.Name` (`C Vet Y`, `C Vet Ä`, `A Opt 1`). **De är IDENTISKA för C1/C2/C3/A1/B2/…
och skiljer sig för varje klass med ändelse**, så en yta som lagrar Id:t ser korrekt ut i all
testning och delar bara veteran-, dam-, junior- och optikklasserna. Resultatrader ska bära NAMNET —
det är vad `GetShootersForResultsEntry` ger kvalinmatningen — men **finalinmatningen läste klassen
rakt ur finalstartlistans JSON, som lagrar Id:t** (`loadFinalsStartList`). Resultatlistan grupperar
på `(MemberId, ShootingClass)`, så de två formerna blev två skyttar som visade samma klass.

**`ShootingClasses.ToCanonicalName` är nu den enda form en resultatrad får lagras i**, och
`NormalizeKey` är dess nyckelvariant. Anropa den på varje klasssträng som går in i eller ut ur en
resultatrad.
- **Skrivvägen kanoniseras i `SaveResult`** — den enda strypningspunkten varje resultatrad passerar,
  alltså det enda ställe som kan garantera att en skytts serier bär samma sträng oavsett vilken
  inmatningsyta som skickade dem. Ett byte loggas.
- **Läsvägen kanoniserar sina GRUPPERINGSNYCKLAR** (`CalculateFinalResults`, `CalculateLeaderboard`,
  `PrecisionFamilySeriesScoreSource`, `PrecisionFinalsQualificationService` × 2), så redan skrivna
  rader slås ihop utan SQL. Läsvägen får aldrig vara den som litar på kolumnen.
- **`ParticipantStatusService.Key` viker ihop båda formerna.** Statustabellen lagrar Id:t medan
  resultatraderna bär namnet, så en DNS/DNF träffade tyst ingenting för exakt de klasser där de
  skiljer sig — den buggen låg och väntade och är dokumenterad som "Id-vs-Name-fällan" i DNS/DNF-avsnittet.
- **Klienten:** `window.getShootingClassName` i `_ShootingClassesBootstrap.cshtml` (speglar C#-metoden)
  och finalinmatningen går via den. **Startlistans JSON fortsätter lagra Id:t** — det är dess
  konvention; konverteringen hör vid övergången till en resultatrad.

`Migrations/normalize-result-shootingclass-to-display-name.sql` städar det lagrade datat (spar-,
raderings- och klassbytesvägarna matchar på exakt sträng). Idempotent, precisionsfamiljen ENDAST —
Springskytte och Fältskytte äger sina tabeller och sina konventioner. ⚠️ Den **rapporterar
kollisioner och rör dem inte**: en rad i Id-form vars namnform redan finns för samma (tävling,
medlem, serie) är samma serie inmatad två gånger och kräver ett mänskligt beslut — det unika indexet
skulle avvisa UPDATE:n ändå.

17 nya test i `ShootingClassesTests` (61/61), inklusive en svep över hela registret så en klass som
läggs till senare inte tyst kan bryta ihopvikningen. Adds C# → full rebuild.

### Live-resultat kräver ingen resultatlista (2026-08-25)

**Rapporterat:** "jag var tvungen att skapa en resultatlista för att Live resultat skulle visas."
Regeln ska vara: är `showLiveResults` på, visas live-resultat **från att det första resultatet är
inmatat**, oavsett om någon `competitionResult`-nod finns.

**Servern klarade redan detta** — `GetResultsList` har en nod-fri gren
(`resultPage == null && showLiveResults`) som räknar rakt ur resultattabellen via
`CalculateFinalResults`. Två helt andra saker stod i vägen:

1. **Länken erbjöds aldrig.** Tävlingssidans två första grenar kräver `hasResultPage`; den nod-fria
   vägen gick via en JS-probe som läste **`data.Success && data.Count > 0`** — PascalCase — medan MVC
   serialiserar de anonyma objekten **camelCase** (verifierat på tråden: `{"success":true,"count":0}`).
   Alltså permanent falsk. **Springskytte var dubbelt trasig:** proben läste
   `data.results.classGroups`, och `GetSpringskytteResults` har ingen `results`-nyckel alls —
   `classGroups` ligger i roten. **Fältskytte var den enda gren där proben fungerade**, vilket är
   varför felet kunde ligga kvar. `liveProbeHasResults` läser nu båda skalen och båda placeringarna.
2. **Proben frågade bara en gång, vid sidladdning.** Öppnar arrangören sidan innan första serien är
   inmatad dyker länken aldrig upp utan omladdning — vilket är precis tvärtemot "från första
   resultatet". Den frågar nu om var 30 s och slutar när länken visats.

Platshållartexten sa dessutom **"Resultat har inte publicerats än"**, vilket beskriver en
resultatlista och inte live-läget. Den lyder nu *"Live-resultat visas så snart det första resultatet
är inmatat"* — ett löfte koden numera håller.

**Och tavlan satt kvar på sin spinner.** Före första resultatet svarar alla tre endpoints
`success:false`, och `fetchResults` gjorde `return` — så `renderAll()` kördes aldrig och
`#rbTableWrap` behöll skelettets spinner, utan text, utan badge, utan tidsstämpel. En tavla castas
till en vägg-TV **innan** skjutningen börjar, så den måste kunna vänta högt och fylla på sig själv.
`rbWait()` renderar "Väntar på det första resultatet…".
⚠️ **`rbWait` skriver bara när `allGroups` är tom.** En senare misslyckad poll får aldrig ersätta
ställningen som står på skärmen — på en vägg-TV ska ett nätverksglapp inte tömma tavlan.
⚠️ **`!d.exists`-grinden i precisionsgrenen var en tystnadsfälla** och pekar nu också på `rbWait`.

**Kvarstår medvetet:** den nod-fria grenen har ingen `mergeConfig` eller `classNameOverrides` — de
bor på noden. Live-tavlan visar alltså råa klasser tills en resultatlista finns, vilket är rätt: en
sammanslagning är arrangörens beslut, inte något som ska gissas fram.

Verifierat 31/31 `hpsk-verify/liveresults-no-resultlist-verify.mjs` (**A/B: 9 av 31 faller på
baseline**, bl.a. att en tävling med 12 inmatade resultat ändå visade "Resultat har inte publicerats
än" och ingen länk) plus 13/13 i den utbrutna probe-läsaren. ⚠️ **Fällor för den som ändrar sviten:**
mocken måste matcha grenens endpoint — 5326 ser ut som ett bra val och är Springskytte, så en
`GetResultsList`-mock applicerades aldrig (använd 5103, Precision); `liveProbeHasResults` är korrekt
scopad i `DOMContentLoaded` och syns **inte** för `page.evaluate` — assertera observerbart beteende;
och läs platshållartexten ur **server-HTML**, för på en tävling som redan har resultat har proben
hunnit byta ut den innan `page.content()` läses, så påståendet faller just när fixen fungerar.

**Endast vyer → ingen ombyggnad krävs**, filerna kan laddas upp direkt. Ingen SQL, ingen
doctype-property.

### Startliste-medveten radering (2026-08-25)

Everywhere except Springskytte, deleting a registration was a bare confirm dialog: the registration
went, the shooter **stayed on the generated start list** with orphaned result rows behind them.
A/B against the un-fixed build shows it plainly — the **public** start list still displayed the
deleted shooter, and the orphan count went from 2 to 3: the delete *created* one.

**On the old code the mess could not even be cleaned up through the UI.**
`RemoveShooterFromStartList` refuses to remove a shooter who has results
(`SELECT COUNT(*) FROM PrecisionResultEntry …`, hardcoded), and the results are unreachable once
the registration is gone. The shooter was stuck on the list.

**`Services/StartListCleanup` is the same seam as `StartListCoverage`** — coverage MAKES the mess
visible, cleanup REMOVES it. One source per discipline, because the start unit differs (skjutlag in
a content node vs patrol in SQL) and so does the result table.

**It runs SERVER-SIDE inside `DeleteCompetitionRegistration`**, not as a second client call the way
Springskytte does it. So it cannot be skipped by a client that never calls back, and there is no
window where the registration is gone but the list still holds the shooter. Best-effort and AFTER
the delete: the registration is already gone, so throwing would report a failed deletion that
actually succeeded and invite a retry.

**Four decisions that are easy to get wrong:**
- **The vacated position is left as a GAP — no renumbering.** Position is a firing point;
  renumbering moves every shooter after them to a different lane, mid-competition, for someone
  else's withdrawal. Springskytte made the same call for start numbers. The suite asserts the other
  rows are byte-identical.
- **⚠️ Direktplacering is REGENERATED, not patched.** DP writes its own anonymous config shape and
  its own bespoke HTML; deserializing that into a `StartListConfiguration` and re-rendering would
  silently replace the whole list's markup. Same rule as `RegistrationClubPropagationService`.
- **Only what was ALREADY published is re-published.** Publishing a draft list as a side effect of a
  deletion would make an unfinished list public.
- **⚠️ The result table is resolved with `TryFor`, not `For`.** `For()` answers
  `PrecisionResultEntry` for anything unknown, which is right for a READ and dangerous for a DELETE
  — a typo in the type would delete from another discipline's table.

`CompetitionTypes/Common/CompetitionResultTables` now holds the type → result-table map, which
existed in **three** copies, one carrying the comment *"keep the two in sync"* (the smell, not the
safeguard). **The two existing call sites are deliberately NOT migrated:** their
`_ => "PrecisionResultEntry"` fallback would change behaviour for Fältskytte on a hot read path no
suite here covers. Migrate them only with a test that pins that path.

**Confirm dialog and response:** the dialog now says WHERE the shooter stands ("Skjutlag 1, plats
7"), whether the list is **PUBLICERAD**, and that the result rows go too
(`RegistrationAdmin/GetRegistrationPlacement`, read-only). The response reports what happened
instead of a bare "Anmälan borttagen", and `warnings` are surfaced separately because they are
things the operator must act on (a stale cached blob, a failed re-publish).

**Springskytte is untouched:** no source is registered, so the service no-ops there and its own
client path still owns it. **Do not add a Springskytte source without removing the client call** —
both would run and the second would report "0 freed", which reads like a failure.

Verified 26/26 `hpsk-verify/startlist-aware-delete-verify.mjs`, which builds its own fixture:
places a shooter, enters a result, deletes, then checks the shooter is gone from the configuration
AND from the public page, that the result row followed, and that everyone else's slot is unchanged.
**A/B: 12 of 26 fail on baseline.** ⚠️ Two fixture traps that cost a round: "unplaced" is per
(member, CLASS), so a shooter can be missing a C3 slot while standing on the list in another class
— `AddShooterToStartList` then refuses with "finns redan i startlistan"; and
`ValidateResultRequest` requires `rangeOfficerId > 0` but answers only "Ogiltig begäran" without
naming the field.

Adds C# → full rebuild. No SQL, no doctype property, no Umbraco node.

### Startlistetäckning — är alla anmälda faktiskt placerade? (2026-08-25)

A shooter could be registered, invoiced and **completely absent from the start list** with nothing
anywhere saying so — the first to notice was the shooter, on the day. Springskytte got the answer
2026-08-05 (a desk run found 43 A-starts with no start time behind a screen that looked finished);
the same silence stood on the whole precision family and on Fältskytte.

**`Services/StartListCoverage` is the seam** — same shape as `ISeriesScoreSource`, because where a
start time LIVES differs per discipline: the precision family keeps skjutlag in a
`precisionStartList` node's `configurationData`, Fältskytte keeps patrols in SQL. A new discipline
is one class plus one line in `AdminServicesComposer`, never a branch in a controller.

**⚠️ The two disciplines key placement DIFFERENTLY, and that is the whole design:**
- Precision family: **(member, CLASS)**. Every registered class gets its own position in a skjutlag,
  so A1 and A_opt_1 are two separate starts.
- Fältskytte/MagnumFält: **(member, WEAPON GROUP)** — a patrol walks the course once, so C1 and C2
  are the same start; the assign path already matches on `LEFT(pm.ShootingClass, 1)`. Keying per
  class here would report a phantom missing start for the second class forever.
`CoverageBuilder.Row.KeyClass` carries the key while `ShootingClass` stays the real class, because
that is what the organiser must read on the row.

**⚠️ `CoverageKeys.Canonical` exists for the Id-vs-Name trap.** Registrations and most writers
store the class **ID** (`C1`, `A_opt_1`); `ChangeShooterClass` writes the display **NAME** (`C 1`,
`A Opt 1`). A literal compare matches nothing for every class where they differ, and the whole list
then reads as unplaced — which looks like a planning failure, not a bug. Verified against `C1_Dam`
and `C_Vet_Y`.

**The MIRROR fault is reported too (`onListWithoutRegistration`)** — rows on the list matching no
registration. Found during verification: 3 of 12 rows on dev competition 2576 sit in a class the
shooter is not entered in (Andy Haard registered in C3, on the list as `C_Vet_Y` **and** `A1`), i.e.
the class change that causes result-row orphaning. **Reporting only the unplaced half makes the
warning untrustworthy:** the organiser looks the shooter up, finds them ON the list, and writes the
alarm off. Both halves have to be on screen for either to mean anything.

**`hasAnyStartList` separates "no list created yet" from "the list forgot people".** Before the first
generation everyone is unplaced, which is where every competition starts — a red alarm there trains
the organiser to ignore the panel. That state renders amber and says so.

**Endpoint is `RegistrationAdmin/GetStartListCoverage`** so it reuses `CanManageCompetitionDeskAsync`,
which carries the club-vs-region host rule. An SM is region-hosted (`clubId` unset) and a
hand-written `clubId` check locks the organising krets out of its own competition — got wrong four
times before. Read-only and safe to poll.

**Springskytte is deliberately untouched:** its own endpoint also covers **stafett teams**, a concept
no other discipline has, and it works. The client picks the endpoint by discipline
(`deskCoverageEndpoint`). Fold it in only if that endpoint is being touched anyway.

**Surfaces:** `Views/Partials/_StartListCoveragePanel.cshtml` on the Startlistor tab (precision +
Fältskytte), plus the desk banner on Anmälningar which now renders for **every** discipline (it was
Springskytte-only). The panel exposes `window.hpskLoadStartListCoverage()`; `loadStartLists()` and
`loadFaltPatrols()` call it, since every generate/edit path already comes back through those.
⚠️ The loader is assigned with `window.x = x`, not left to hoisting — an `async function`
declaration inside a block does **not** leak to global scope (same trap as the consolidation
partials).

Verified 27/27 `hpsk-verify/startlist-coverage-verify.mjs` (read-only). It asserts that the gap
between list rows and `placed` is explained **entirely** by the orphans, so a silent key mismatch
cannot slip through disguised as one. Regression: startpref 26/26, startpref-precision 10/10,
startpass-total 11/11, verify-spring-all PASS. Adds C# → full rebuild. No SQL, no doctype property.

### Anmälningar row Åtgärder menu, shooter info, push column, reference lookup (2026-08-07)

Desk batch on the Anmälningar tab (`CompetitionRegistrationManagement.cshtml`). All five items
below ship together; verified 199/199 via `hpsk-verify/{row-actions-menu,notices-card,payment-reference-lookup,deskbatch-comprehensive}-verify.mjs`.

- **Per-row Åtgärder dropdown** replaces the variable-length icon strip on BOTH the shooter rows and
  the Lag rows (same items, same order). Delete is last, under a divider, in red — strictly safer
  than the old pinned-right trash icon. Icon is `bi-sliders` to match the club-admin Medlemmar menu;
  `text-end` on both the `<th>` and the cell right-aligns it.
  **⚠️ `data-bs-popper-config='{"strategy":"fixed"}'` is required** — `.table-responsive` is
  `overflow-x:auto` and clips the menu on the LAST rows. `data-bs-strategy` is NOT a Bootstrap
  option and is silently ignored; assert `getComputedStyle(menu).position === 'fixed'`, and test the
  bottom row (the top row passes either way).
- **`RegistrationAdmin/GetShooterInfo(competitionId, registrationId)`** — contact card behind
  "Visa skytt". Deliberately narrow: the row payload already carries payment/reminder/class state
  client-side, so this returns only member contact + the two registration fields the list endpoint
  omits (`registeredBy`, `shooterNotes`). Refuses a registrationId belonging to another competition.
  Also returns `clubUrl` / `regionName` / `regionUrl` so the Klubb row links to both pages. The
  krets comes from the TREE (`regionalPage > clubsPage > club`, i.e. `clubNode.Parent?.Parent`), not
  from the `regionalFederation` code — same source the URL provider uses for club-hosted comps.
  Wrapped in try/catch: an unpublished or moved club yields no link and the names render as text.
- **Push-reachability "Notis" column** — `WebPushService.GetMemberIdsWithSubscriptions(ids)` (new,
  ONE query for the whole table) → `hasPushSubscription` on each row in
  `CompetitionController.GetCompetitionRegistrations`. Means "can we push at all" (≥1 subscription),
  NOT a per-competition opt-in. **⚠️ `IN (@0)` caps at ~2100 SQL parameters** — fine at SM scale,
  breaks at thousands of registrations, and fails SILENTLY (antennas go dark). See backlog P3.
- **"Skicka notis…" to one shooter** — NO new backend: reuses
  `ParticipantMessage/SendToParticipants` with the already-existing `Person` scope
  (`ParticipantAudienceResolver.cs:42`), so same auth, audit trail and in-app inbox.
- **Sortable Närvaro / Notis** — both boolean columns sort false-first ascending, so one click groups
  the rows the desk must chase.

### Springskytte: flera starter (omgångar) i EN startlista (2026-08-15)

A list could only have ONE first start time. Now it can hold several — e.g. 30 starters from 13:11
and the rest from 16:11 — sharing one set of classes, one interval, one break rule and **one start
number series**. Springskytte only; no migration, no doctype property.

- **`SpringskytteStartPass { FirstStartTime, Count }` + `config.Passes`.** `Count` null = "resten";
  the LAST pass is always forced open so an efteranmälan lands there without re-typing counts.
  **`Passes` is only persisted when there is genuinely more than one**, so a single-pass list's
  stored JSON is byte-identical to before. `EffectivePasses()` is the only thing anyone should read
  — it synthesises the legacy single pass from `FirstStartTime`.
- **`FirstStartTime` is kept and mirrors pass 1.** It is the list's sort key across the competition
  (renumber ordering at `GetOrderedIndividualStartLists`, admin cards, follow-on numbering); removing
  it would have rippled far wider than this feature.
- **Pass membership is DERIVED from the start time** (`PassIndexFor`), never stored per starter. An
  organiser can move one shooter to another time long after generation; a stored index would drift
  and nothing would notice.
- **⚠ THE TRAP — `BuildTimelineAsync` never reads `FirstStartTime`.** Free slots are derived purely
  from the HOLES between actual start times, so without a cap the wait between two passes becomes
  bookable "paus" slots: measured **144 fake slots** on a 3-hour boundary at 1-min intervals. The
  organiser's rule (Stefan 2026-08-15): a few slots straight after the last shooter of a pass, then
  nothing until the next pass. Implemented as `NextPassStartAfter` + `TrailingSlotsPerPass = 3`.
  A legacy single-pass list has no boundary → returns null → every line behaves exactly as before.
- **The wish decides the pass EXPLICITLY** (`AssignPasses`), it does not fall out of the sort. The
  sort is weapon class first, so on a list covering A and C a plain "first N" cut would put the whole
  A block in pass 1 — including anyone in A who asked to start late. Tidig claims the earliest pass
  with room, sen the latest, everyone else fills the rest in order. **An unhonourable wish is
  reported in `warnings` and alerted**, never silently dropped.
- **Refuses an overlapping split**: a later pass starting before the previous one finished would
  interleave two sequences on one range.
- Each pass **restarts the break counter** — "paus efter 10 starter" is about a run of starters, and
  a two-hour wait is already a break.
- **Live class total** in the settings modal ("61 skyttar i 10 klasser") + a remainder on the last
  pass row that counts down as the counts are typed — that number is what the split is divided out
  of. Summed client-side from the per-class `count` the classes endpoint already returned; no
  backend change. Sized from the CLASS SELECTION, not `card.starters` — a new list has no starters
  yet and the operator may have just changed the classes. **Re-rendering the pass rows on a class
  tick must `springSyncPassRowsFromDom()` first**, or a typed-but-unsynced time is discarded; and a
  count keystroke updates only `#springSet_restLabel`, because a full re-render steals focus.
- Surfaces: settings modal gets a repeatable **Starter** editor (`springModalPasses`,
  `springValidatePasses`); card summary lists both starts; public `/startlista` and the cached HTML
  draw a **`Start N – kl HH:MM`** heading (`.pass-row`) instead of letting the gap logic label the
  boundary "Paus"; `GetSpringskytteStartLists` returns `passes` with live per-pass counts.
- **Nothing else changed.** Numbering, live-tavlan, Mitt schema, startledare and the station screens
  read actual start times and are untouched — verified, not assumed.
- **Cross-discipline safety:** the whole diff is Springskytte files. `precisionStartList` is a SHARED
  doctype, but `MyScheduleService` tells the shapes apart on `Starters` vs `Teams`, which a new field
  cannot flip. Regression-verified against Precision (2576) and Fältskytte (**5312 Banfältet** — the
  builder account is NOT a competition manager for 5282, so that comp only renders Access Denied and
  asserts nothing) plus `/mitt-schema` on all three.
- Verified 27/27 `hpsk-verify/startpass-verify.mjs` (incl. the free-slot cap + the cross-discipline
  regression), 16/16 `startpass-modal-verify.mjs` — both restore the dev list to a single pass —
  and 11/11 `startpass-total-verify.mjs` (read-only, never generates).

### Önskemål om starttid — the wish finally gets a consumer (2026-08-15)

`ShootingClassEntry.StartPreference` has existed for years, was collected on some surfaces and
**consumed by nothing**. Built for SM: the organiser can record a wish and the Springskytte
generator sorts on it. No doctype property, no migration — the field already existed.

- **`Models/StartPreference.cs`** is the ONE place the value is interpreted. **⚠ Six spellings are
  in the wild** because nothing ever read the field: `"Inget"` · `"No Preference"` (the C# default
  on `ShootingClassEntry`, so it sits on every untouched entry) · `""` (the repository's legacy
  single-class fallback) · `"Tidig Start"`/`"Sen Start"` (the pickers) · `"Early"`/`"Late"` (the
  deprecated display switch and the `AddLateRegistration` API example). A plain string compare
  matches none of them reliably. Always `Normalize()` / `Rank()`; unreadable → neutral, so a
  drifted value can never reshuffle a list. `normalizeStartPreference()` in
  `CompetitionRegistrationManagement.cshtml` is the client mirror — keep the two in step.
- **Carried, not dropped:** `UmbracoStartListRepository` used to discard the field when flattening
  registrations, so generators never saw it. `CompetitionRegistration.StartPreference` now carries
  it (per class).
- **Consumed:** `GenerateSpringskytteStartList` sorts `weaponClass → StartPreference.Rank → Id`.
  The wish sorts **within** the weapon class — a Springskytte start is per (member, vapengrupp),
  so an early wish must not lift someone out of their own class block.
- **⚠ Regeneration keeps NUMBERS but recomputes TIMES** (`ApplyNumbersToGeneratedList` is sticky per
  (member, weaponClass)). Change the sort after a list is numbered and the numbers no longer run in
  time order — the organiser must then run **"Numrera om"**. Best applied before the first generation.
- **Set from the row's Åtgärder menu**, via its own narrow endpoint
  **`RegistrationAdmin/SetStartPreference`** — deliberately NOT a field on
  `UpdateCompetitionRegistration`, which recomputes the fee and can create a top-up invoice on save.
  Recording a wish must never be one Spara away from invoicing. Writes `StartPreference` and nothing
  else; `CanManageCompetitionDeskAsync` (carries the club-vs-region host check); refuses a
  registration belonging to another competition and a class the registration lacks. Legacy
  single-class registrations (no `shootingClasses` JSON) get the scalar `startPreference` property
  written instead of being silently migrated to the JSON shape.
- **Modal rows are per WEAPON GROUP on Springskytte**, per class elsewhere; one group row writes the
  same wish to every class in the group, so storage stays per class. An already-slotted shooter is
  told so on the spot — the wish is consumed at generation, so for them it changes nothing until a
  regeneration. Footnote is honest per discipline: other disciplines say their generator does not
  sort on it yet (a wish nobody honours is the bug being fixed).
- Display: the previously **dead** `getPreferenceBadgeClass()` finally has a caller — the badge rides
  alongside its class badge (a separate column could not say which class it belonged to on a
  multi-class row). The existing Startpreferens filter and CSV column already worked.
- **Not done (deliberate):** the shooter-facing picker. `SpringskytteRegistrationModal` hardcodes
  `'Inget'` and the desk walk-in hardcodes `'Inget'`, so for SM no shooter-entered data exists —
  the wishes arrive by mail. Precision-family public registration still collects it via the gear icon.
- Verified 26/26 (`hpsk-verify/startpref-verify.mjs`, Springskytte incl. the generator reorder, with
  restore) + 10/10 (`startpref-precision-verify.mjs`, per-class branch).
  **⚠ Note for that script:** a stored start list can carry manual per-starter time edits, so the
  baseline for a TIME assertion must be taken *after* a no-op regeneration — comparing against the
  pre-regeneration times measures the manual edits, not the wish.

### Bank payment-reference lookup — samlingsfaktura (2026-08-07)

Reported by the SM cashier: an individual reference (`3803-1120-1`) is on the registration row so
the search finds it, but a **samlingsfaktura reference is `{competitionId}-club-{clubId}-{seq}`** and
belongs to a PARENT invoice that is not a registration — the rows under it carry their own child
numbers. The search returned nothing, and there is **no Fakturor tab on the competition page** to
fall back to (the invoice list lives only on ClubAdminPanel / RegionalAdminPanel).

`RegistrationAdmin/LookupPaymentReference(competitionId, reference)` resolves a pasted reference to
one of five outcomes: `consolidated` / `individual` / `othercompetition` (names it + links) /
`trashed` (offers restore) / `notfound`. On `consolidated` the client filters the individuals table
AND the Lag card to what the payment settles, and offers Markera som betald via the existing
`InvoiceAdmin/MarkAsPaid` (which already cascades to children).

**⚠️ A samlingsfaktura may cover individual registrations, LAG, or BOTH — and a team invoice carries
a NON-ZERO `registrationId` that is not a registration node id** (invoice 6770 → 6769). Routing on
`registrationId` alone puts lag into the individuals filter and the banner claims "täcker 7
anmälningar" over an empty table. Split on `memberId` instead: `"team-{id}"` → Lag card, otherwise a
registration. All three shapes exist in dev data (`5326-club-2604-1` mixed, `5631-club-2604-20`
individuals-only, `6628-club-2614-1` lag-only).

**`RestoreDeletedInvoice`** pulls a deleted invoice back under the competition's invoice hub.
Justified because **cancelling never deletes** — `CancelUnpaidParent` only sets
`paymentStatus=Cancelled` and leaves the invoice in place — so anything in the recycle bin was
deleted by hand in the backoffice while the club may still have paid against that reference. Guarded
to trashed invoices whose number starts with the competition id.
**⚠️ `GetPagedDescendants` returns NOTHING against `Constants.System.RecycleBinContent`** — use
`GetPagedChildren`. Silently empty, so the feature just doesn't work.

**⚠️ Invoice numbers are NOT unique across live + deleted.** `GenerateInvoiceNumber` counts existing
invoices, so deleting one frees its number for re-issue — `5312-2353-1` exists both trashed and live
in dev. The lookup therefore searches the hub FIRST and only falls back to the recycle bin, so the
live invoice (the one you would actually settle) always wins. Anything keying off an invoice number
alone must not assume it identifies one node.

**Pending (agreed 2026-08-07):** block deletion of `registrationInvoice` entirely via a
`ContentMovingToRecycleBinNotification` handler — an invoice is räkenskapsinformation under
bokföringslagen and must be makulerad/credited, never deleted. Deferred out of the SM-eve deploy.
NB the `PaymentService.Delete(invoice)` calls are rollback of a FAILED creation, not deletion of a
live invoice — those stay.

### Notiser card shared by the competition page and /mitt-schema (2026-08-07)

Extracted from `Competition.cshtml` into **`Views/Partials/_ParticipantNotices.cshtml`** and
rendered by both surfaces, so they cannot drift. ViewData knobs: `CompetitionId` (required),
`NoticesInitialCount` (default 3), `NoticesHeading`, `NoticesCardClass` (the competition page passes
`"information-sidebar mb-4"` — that styling is defined INSIDE Competition.cshtml and exists nowhere
else, and the `mb-4` is needed because the Information card below sets no top margin).
**Not safe to render twice on one page** — it includes `_WebPushSetup`, which owns fixed element ids.

On `/mitt-schema?c=` it sits BELOW the timeline (the page is called Mitt schema). Long-feed
handling: collapsed to 3 with "Visa alla N", expanded list capped at `60vh` and scrolling inside the
card, unread "Ny" badges + header count via `localStorage['hpsk_pn_seen_<compId>']`, and an unread
Safety/Urgent message below the fold **force-expands** the list.
**⚠️ The unread watermark must be captured ONCE per page load**, not re-read per render — otherwise
the first render marks the visible messages seen and the Ny badges vanish the instant the user taps
"Visa alla".

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
- **registrationInvoice**: add `paymentSentDate` (Date Picker) + `paymentSentBy` (Textstring) properties (both optional). These record a **"payment sent" CLAIM** by the *payer* (the shooter via "Jag har betalat" in the Swish modal, or a club admin via "Betald av klubben" in the club Fakturor tab) — explicitly distinct from the organizer's authoritative *received* state (`paymentStatus=Paid`). A payer can never set received; the organizer never has their received state flipped by a payer. Without these properties the claim silently no-ops (`SetValue` on a missing property) — the button "succeeds" but nothing persists. Added 2026-06-12.
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
- **club**: add `markenRequireOnSiteWitness` True/False property (optional, default false, label "Kräv bevittning på plats för serier"). When ON, a `MarkenSeries` can only be APPROVED by scanning the shooter's live QR code — `SetSeriesStatus` refuses a bare approve from the validation queue. Rejecting is always allowed. Missing property = safe default (off) + `SetMarkenClubSettings` refuses that half of the save with a message naming the property, rather than no-op'ing (`SetValue` on a missing property is silently ignored, so the switch would appear to work and revert on next load). Added 2026-08-28.
- **club**: add `markenSignoffSkjutledare` True/False property (optional, default false, label "Tillåt skjutledare att signera märken"). Powers per-club sign-off authority for Märken (Pistolskyttemärket). OFF (default) → only board members (Styrelse, via `BoardRoles`) + site admins sign off Guldfodringar/märken; ON → Skjutledare of the club may too. Missing property = silent no-op (safe default = board only). Added 2026-05-31. Also run `Migrations/create-marken-tables.sql` in SSMS.
- **competition**: add `rangeId` Integer property (optional, default 0, label "Skjutbana (id)"). Links a competition to a shooting range in the Skjutbanedatabas → the public competition page shows venue + map + Vägbeskrivning (members-only block) and the management page gets a range-picker. Missing property = graceful no-op (picker shows "lägg till egenskapen", public block hidden). Added 2026-06-03. Also run `Migrations/create-range-tables.sql` in SSMS + create the `shootingRangeHub` node (alias `skjutbanor`). See `Documentation/SHOOTING_RANGE_DATABASE.md`.
- **club**: add `orgNumber` Textstring property (optional, label "Organisationsnummer"). Swedish org. number for the club. Surfaced in the club create/edit modal (`ClubManagement.cshtml`) and printed on competition payment receipts (organizer block) so the receipt is valid for friskvårdsbidrag claims. (`address`/`city`/`postalCode` already existed on `club`.) Missing property = silent no-op + blank receipt row. Added 2026-06-11.
- **club**: add `receiptEmail` Textstring property (optional, label "E-post på kvitto"). The email address shown on the printable Kvitto; falls back to `contactEmail` when empty. Surfaced in the club create/edit modal + club Inställningar tab. Added 2026-06-11.
- **regionalPage**: add `orgNumber`, `address`, `city`, `postalCode` Textstring properties (all optional). Org. number + postal address for region-hosted competitions; surfaced in the region edit modal (`RegionalEditModal.cshtml`) and printed on receipts for region-hosted comps (clubId unset → organizer resolved via `regionalFederation`→`regionCode`). Missing properties = silent no-op + blank receipt rows. Added 2026-06-11.
- **regionalPage**: add `receiptEmail` Textstring property (optional, label "E-post på kvitto"). Same role as the club one — shown on the Kvitto for region-hosted comps, falls back to `contactEmail`. Surfaced in the region edit modal + regional Inställningar tab. Added 2026-06-11.
- **competition**: add `closeRegistrationOnStartList` True/False property (optional, default false, label "Stäng självanmälan när startlistan publiceras"). Arrangörens val i publiceringsdialogen. **Default false = deploy ändrar ingenting för en tävling vars startlista redan är publicerad** — det är avsiktligt, inte försiktighet: en default-on hade stängt anmälan på levande tävlingar i deploy-ögonblicket utan att någon fick veta det. Utan egenskapen är `SetValue` en tyst no-op, så publiceringen **vägrar och namnger egenskapen** i stället för att rapportera en sparning som inte hände. Added 2026-08-31.
- **competition**: add `teamResultSeriesCount` Integer property (optional, default 0, label "Antal serier i lagresultat"). How many series count toward a team's total — surfaced next to "Tillåt laganmälan" in the competition wizard + edit modals. 0/empty = auto (defaults to the qualification series count = `numberOfSeriesOrStations − numberOfFinalSeries`), so a 7+3 finals comp counts only the 7 qualifying series without any config. Set a value to override. Read by `CompetitionTeamController.GetTeamResultSeriesCount`. **The team-results-show-0 fix does NOT depend on this property** (the qualification default handles it); the property only adds explicit override. Missing property = silent no-op (auto default used). Added 2026-07-22.

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

### "Inga resultat hittades att ta bort" — på A Opt, aldrig på C1 (2026-08-29)

Vetlanda rapporterade att en skytt som skulle bort ur **A Opt 1** (hans A1-resultat skulle vara kvar)
inte gick att ta bort: *"Kunde inte ta bort resultat: Inga resultat hittades att ta bort."* Det var
efterspelet till den skytt han i somras beskrev som fastlåst i ett skjutlag.

**Id-vs-Namn-fällan igen, i den sista metod som blev kvar på fel sida av den.** En klass har ett Id
(`A_opt_1`) och ett visningsNAMN (`A Opt 1`). De är **identiska för C1/C2/C3** och olika för varje klass
med ändelse — optik, veteran, dam, junior. Sedan 2026-08-25 kanoniserar `SaveResult` nya resultatrader
till NAMNET och varje läsväg grupperar på `ShootingClasses.ToCanonicalName` — men
`DeleteShooterFromClass` löste fortfarande upp till **Id:t** och matchade därför noll rader på precis de
klasser där formerna skiljer sig. Samma knapp hade fungerat på C1 hela tiden, vilket är varför ingen
upptäckt det.

- **⚠️ BÅDA formerna matchas nu, inte den kanoniska.** En riktig databas bär båda:
  `normalize-result-shootingclass-to-display-name.sql` hoppar medvetet över rader vars namnform redan
  finns, så det blandade läget är permanent för dem. Dev har 223 rader `C Vet Y` bredvid 6 `C_Vet_Y`.
- Matchat som två exakta värden, **inte** med ett handrullat `UPPER(REPLACE(...))` — det vore en andra
  normalisering, fri att glida från den alla andra ytor använder.
- **Meddelandet namnger nu vad som söktes.** *"Inga resultat hittades"* ensamt är ett påstående om
  DATAT när sanningen kan vara ett påstående om BEGÄRAN — samma felattribution som redan kostat en
  runda på `DeleteResult`.

⚠️ **Ordningen spelar roll för operatören:** ta bort resultatet i klassen FÖRST, sedan raden i
startlistan. `RemoveShooterFromStartList` vägrar så länge det finns resultat i klassen — och före
2026-08-25 var den kontrollen dessutom medlemsbred, så A1-resultaten blockerade borttagningen ur A Opt.
Det är sannolikt exakt vad han såg i somras.

Verifierat 14/14 `hpsk-verify/delete-class-idname-verify.mjs` (**A/B: 5 av 14 faller**). Sviten bygger
sin egen fixtur på ett kast-medlems-id och asserterar båda riktningarna (rader lagrade som namn raderade
via Id, och tvärtom), det blandade fallet i en operation, och — kärnan i rapporten — att skyttens **A1
står kvar** när A Opt 1 tas bort.

### "Utvalda tävlingar"-väljaren gick inte att skilja tävlingar åt i (2026-08-29)

Rapporterat med skärmbild: fem rader som alla läste *"Kretsmästerska…"* och ingen ledtråd om vilken som
var vilken — *"här finns massa kretstävlingar som jag inte vet vilka de är"*.

**⚠️ Det var inte den publika tävlingslistan.** Skärmbilden visade `FeaturedItemsPicker.cshtml`, modalen
där klubben/kretsen väljer vilka tävlingar som lyfts fram på sin egen sida. En första analys utgick från
`CompetitionsHub` och hade byggt fel sak — **öppna skärmbilden innan du kartlägger en rapport om "en
lista"**.

Fyra fel, och tre av dem var att befintlig data inte visades:
- **Arrangör och serie fanns redan i payloaden** (`clubName`, `regionCode`, `seriesName` från
  `Club/GetAvailableCompetitions`) och renderades inte. Raden var namn + datum + bricka.
- **Namnet var det som klipptes.** Allt låg på EN flexrad med `text-truncate` på namnet, så på en telefon
  offrades namnet först. Att bara lägga till arrangören där hade gett trunkeringen mer att äta. Raden är
  nu tvårads: namn på egen rad, arrangör · serie · datum som liten metarad under.
- **Filtren fanns men scrollade bort.** Dialogen är `modal-dialog-scrollable`, alltså scrollar BODY —
  och filterraden följde med listan uppåt. De var alltså aldrig borta, bara utanför skärmen exakt när de
  behövdes. Nu `position: sticky` (med `bg-body`, inte hårdkodat vitt, så randen följer temat).
- **Kretsen kom ut som enum-kod** (`Jonkoping`). `GetAvailableCompetitions` skickar nu även `regionName`
  från `regionalPage`-noden; `regionCode` ligger kvar orörd eftersom regionfiltret jämför mot den.

Dessutom, efter påpekande: **regionfiltret är förvalt till sidans egen krets** (fjärde argumentet till
`openFeaturedPicker`) — listan är nationell och på väg mot tusentals rader. Förvalet sätts EFTER att
dropdownen fyllts, och bara om kretsen finns bland alternativen; annars hade admin mötts av en tom lista
med ett filter hen inte valt. Raden *"Visar X av Y poster"* finns just för att ett förvalt filter aldrig
ska kunna gömma något tyst.

**Och rubriken sa fel sak.** "Redigera Utvalda Tävlingar" läser som att man ska ändra på tävlingarna;
modalen väljer bara vad som visas. Nu *"Välj vad som visas på sidan"*, med pennikonen utbytt mot en
stjärna på båda ytorna (klubbsidan och kretssidan).

⚠️ Sökrutan lovar numera klubb och serie, så sökningen matchar mot namn + serie + klubb + krets. En
platshållare som lovar mer än koden gör är sin egen bugg.

Verifierat 18/18 `hpsk-verify/featured-picker-verify.mjs` (**A/B: 7 av 12 faller innan baseline kraschar
på den saknade filterraden**). ⚠️ Sviten måste öppna modalen via `window.openFeaturedPicker(...)` — att
kalla bootstraps `show()` för hand ger ett tomt skal, eftersom hämtningen ligger i den funktionen; första
körningen mätte "0 poster" och skyllde på renderingen. Klubbsidan loggar `ckeditor-duplicated-modules`
vid varje besök (befintligt brus, orelaterat) och filtreras bort ur JS-felkontrollen.

⚠️ **Bygg-fälla på vägen:** `Copy-Item` bevarar källfilens tidsstämpel, så en återställd `.cs` såg äldre
ut än föregående byggutdata och MSBuild hoppade över den — appen körde vidare på baseline-binären medan
källan såg rätt ut. Rör filen (`LastWriteTime = Get-Date`) före ombyggnad efter en A/B.

### Träningsloggen växte tomma serier · tangent för TIO · klubbsidan ur avatarmenyn (2026-08-29)

Tre punkter ur klubbadminens andra mail. Endast vyer → **ingen ombyggnad krävs**, ingen SQL.

**Träningsloggen lade till en tom serie på varje tryck.** Rapporterat som *"när man trycker på spara
eller registrera guldserie läggs det till onödigt många serier … efter sista serien tyckte programmet
att jag var på serie 17, med sjukt många blanka serier däremellan"*. `handleEnterSeries()` i
`TrainingScoreEntry.cshtml` slutade i ett villkorslöst `addNewSeries()`. **Två oberoende mekanismer**,
och en fix som bara tar den ena hade lämnat felet kvar:
1. ENTER på en serie man navigerat TILLBAKA till lade ändå till en tom serie **i slutet**. Nu stegar
   den bara framåt till den serie som redan finns; `addNewSeries()` vägrar dessutom skapa en andra tom
   serie när det redan står en sist.
2. **Tangentbordslyssnaren ägde tangenterna även när en dialog låg ovanpå.** Märkes-/QR-modalen öppnas
   ÖVER träningsmodalen medan den senare behåller `.show`, så varje ENTER i QR-dialogen gick till
   `handleEnterSeries()` bakom den — vilket är exakt varför användaren kopplade felet till "registrera
   guldserie". Guarden är `document.querySelector('.modal.show:not(#addTrainingScoreModal)')`.
   ⚠️ Samma guard lades i `TrainingMatchScoreEntry.cshtml`, som har samma konstruktion.

**En tia hade ingen tangent.** Fyra knappsatser mappade 0–9 och `x` men saknade väg till 10 — och en
etta ger 1, så tian gick bara att klicka fram. `+` och `-` ger nu 10, `*` är numpad-tvillingen till
`x`. Båda är åtkomliga med och utan numeriskt tangentbord, vilket var hela poängen (arrangören matar
in resultat på en laptop). Ändrat på **alla fyra**: `CompetitionResultsManagement` (huvudknappsatsen
OCH särskjutningsmodalen), `TrainingScoreEntry`, `TrainingMatchScoreEntry`. ⚠️ De fyra mappningarna är
kopior av varandra — håll dem lika, eller bryt ut dem.

**Klubbsidan låg bakom avatarbilden.** Medlemmens klubbar och kretsar är **flyttade** från
avatarmenyn till en egen toppmeny i `Master.cshtml` (`Min klubb` / `Mina klubbar` när det är fler).
- **En meny, inte en direktlänk, även vid en enda klubb.** En medlem kan tillhöra fem klubbar, och då
  finns ingen enskild "min klubb" att länka till; en rubrik som byter form efter antal är svårare att
  lära sig än en som står still.
- **Flyttade, inte kopierade** — två vägar till samma sida i samma navigering är sin egen förvirring.
- Startsidans snabbknapp **Tävlingar** är ersatt av en knapp till huvudklubben (namngiven), med
  Tävlingar kvar som fallback för utloggade och klubblösa så raden aldrig står med ett hål.
- ⚠️ `clubUrl` resolvas i HomePage-vyns TOPPKODBLOCK. Ett `@@{ }` mitt i markupen där spräcker vyns
  runtime-kompilering med ett meddelandelöst `UmbracoCompilationException` (står redan i filen).

Verifierat 22/22 `hpsk-verify/vetlanda-batch-verify.mjs` (**A/B: 12 av 22 faller**). ⚠️ Sviten öppnar
modalen på riktigt och asserterar att den bär `.show` — tangentbordslyssnaren avbryter annars, och en
svit som bara anropar funktionerna direkt hade varit grön medan tangentbordshalvan var död. Det hände
på första körningen. ⚠️ Knappsatsen på `/competitionmanagement` renderas bara för den som får
administrera tävlingen; läst som vanlig medlem svarar sidan 200 utan knappsats, vilket ser ut som en
saknad funktion.

### En guldserie finns på ETT ställe — tävlingsserier materialiseras (2026-08-28)

Rapporterat av en klubbadmin, som två klagomål som visade sig vara samma fel sett från två sidor:
guldserieligan på klubbsidan saknade tävlingsserier, och guldfodringen räknade en serie **två gånger**
om den både skjutits i en klubbtävling och skickats in för hand.

**Orsaken var två parallella källor.** `MarkenCandidateService` läste kvalificerande precisionsserier
**live ur `PrecisionResultEntry`** *utöver* `MarkenSeries`, utan någon dedup. Ligan läser bara
`MarkenSeries` och kunde därför aldrig se dem. Ingen av ytorna hade fel för sig själv; formen var fel.

**`Services/MarkenCompetitionSeriesSync` materialiserar dem nu in i samma register**, och analysatorn
läser registret ENSAMT. Nya kolumner: `SourceResultId` (vilken `PrecisionResultEntry`-rad serien kom
från), `SourceCompetitionId`, `CountsTowardGuldfodring`.

- **⚠️ RECONCILE, aldrig append.** Varje synk räknar om vad resultaten säger och får registret att
  stämma: lägger till, uppdaterar, och **raderar rader vars källa är borta eller inte längre
  kvalificerar**. Det är det som gör att sena rättelser följer med — resultat rättas dagar efter en
  tävling, och en enkelriktad "insert on save"-hook hade lämnat guldserier stående på poäng som inte
  finns. Den är därmed också idempotent och kan köras på läsning.
- **⚠️ `CountsTowardGuldfodring` skrivs ALDRIG över av en synk.** En funktionär som uteslutit en serie
  som dubblett får inte få sitt beslut ogjort av nästa omräkning.
- **Unikt FILTRERAT index på `SourceResultId`** gör den andra kopian av en resultatrad omöjlig i
  schemat, inte bara osannolik i koden. Filtrerat, eftersom de många handinlagda raderna är NULL och
  ett vanligt unikt index bara tillåter EN NULL.
- **⚠️ Det filtrerade indexet ändrade kraven på varje sqlcmd-skript som rör tabellen.** SQL Server
  vägrar all DML mot en tabell med filtrerat index när `QUOTED_IDENTIFIER` är OFF — vilket är sqlcmds
  standard. Utan `-b` exitar sqlcmd dessutom 0 på T-SQL-fel. Tillsammans gjorde det att en äldre
  verifieringssvits städ-DELETE **gjorde ingenting medan den rapporterade lyckat**. Appen påverkas inte
  (SqlClient sätter ON), men varje skript måste sätta det själv.
- **⚠️ Tröskeln är `min(guldkrav, Elit brons 45)`, inte guldkravet.** Inte varje serie — en
  precisionstävling har 7–10 per skytt och rader som aldrig kan räknas mot något hade begravt registret
  — men baren är den LÄGSTA någon konsument bryr sig om. För vapengrupp C är guldkravet 46 medan Elit
  brons är 45, så att bara läsa guldkravet hade tyst undanhållit varje C-skytts 45:or från deras Elit.
  **`Qualifies` betyder exakt "når guldkravet"** och är därför `false` på en sådan rad: Elit-bevis, men
  utanför guldfodringen. ~186 rader i dev.
- **`ClubId` bär skyttens EGEN klubb** på en materialiserad rad (ingen klubb *validerade* den —
  tävlingsinmatningen är valideringen). Det är vad som gör ligan **medlemsbaserad**, vilket var
  Stefans beslut: den listar klubbens medlemmars serier var de än skjutits. ⚠️ En skytt utan
  `primaryClubId` får `ClubId = 0` och syns då i ingen liga — korrekt, och asserterat, eftersom
  sträng-egenskapsfällan (`GetValue<int>("primaryClubId")` → tyst 0) annars skulle tömma ligan tyst.
- **`SeriesDate` är TÄVLINGENS datum, inte `EnteredAt`** — en funktionär kan mata in raden dagar senare.
- **Medlemslistan hämtas ur RESULTATTABELLEN**, medvetet inte ur en klubbroster eller
  `GetAllActiveMemberIdsAsync`: en skytt vars enda guldserier kommer från tävlingar har inget märke,
  ingen kvalifikation och ingen inskickad serie, så varje roster byggd av märkesdata missar exakt det
  fall detta finns för. Klubbytorna cachar synken 10 min (`marken_compsync_year_<år>`); medlemmens egen
  sida synkar villkorslöst och billigt.

**Dubbletten hanteras i tre steg, och INGET av dem gissar:**
1. **Vid inskicket:** samma skytt, vapengrupp, dag OCH **identisk skottsekvens** → serien sparas men
   med `CountsTowardGuldfodring = false`, så dubbelräkningen aldrig uppstår. Samma poäng men *andra*
   skott ger bara en varning — en skytt som skjuter 48 två gånger i C på en dag är vardag i en
   10-serierstävling, och en automatisk ihopslagning hade UNDERräknat.
2. **I kön och i Detaljer:** varje serie visas med sin källa, så dubbletten syns intill sin tvilling.
3. **Åtgärden:** `SetSeriesCountsToward` (räkna/räkna inte, reversibelt, funkar för båda slagen) och
   `DeleteSeriesAsFunctionary` (bara handinlagda — **att radera en tävlingsserie nekas med förklaring**,
   eftersom nästa synk skapar om den; är resultatet fel är det resultatet som ska rättas).
   Båda gated på `CanSignOffForMemberAsync` (styrelse / skjutledare-om-tillåtet / sajtadmin) — en
   klubbadmin får SE men inte signera, samma delning som valideringskön redan har.

**Elit räknar tävlingsserier** (bekräftat med Stefan 2026-08-28 ur SHB 5.4: *"skjutningarna får göras
under både tränings- och tävlingsskjutning som anordnats enligt förbundets bestämmelser"*). Hela
regeluppsättningen, för referens: guldmärket krävs först och provet får göras tidigast kalenderåret
EFTER att det erövrades; **5 precisionsserier + 5 snabbserier under SAMMA kalenderår**; per serie brons
45 / silver 48 / guld 49 för BÅDA momenten; ett märke per år, i turordning. Allt det var redan
implementerat och är oförändrat — det enda som ändrades är att filtret nedan togs bort.

- **BÅDA halvorna får sitt bränsle från tävlingar.** Stefan bekräftade 2026-08-28 att en Duelltävlings
  serier skjuts mot snabbpistoltavla på 25 m med 3 s/skott, alltså giltigt snabbskyttebevis, så
  `DuellResultEntry` materialiseras som snabbserier (`SeriesType=Speed`, `Target=Snabbpistol_25m`,
  valör ur 49/48/45 — samma stege som `SubmitSeries`). Tröskeln där är Elit brons ensamt; en Duellserie
  har ingen annan konsument.
- **⚠️⚠️ DE TVÅ RESULTATTABELLERNA HAR OBEROENDE IDENTITETSKOLUMNER.** `PrecisionResultEntry.Id = 2377`
  och `DuellResultEntry.Id = 2377` är OLIKA rader med samma heltal — i dev kolliderar id 7 på riktigt.
  Därför bär varje materialiserad rad **`SourceTable`** och det unika indexet är på
  **(SourceTable, SourceResultId)** (`add-sourcetable-to-markenseries.sql`, en följdmigrering eftersom
  den första redan var körd i prod). Nyckling på id:t ensamt hade avvisat en giltig Duellserie och gjort
  synkens svar beroende av vilken tabell som lästes först. **Varje SQL-fråga och varje join mot
  `MarkenSeries` måste scopa på `SourceTable`** — sviten gick själv i den fällan tre gånger: en
  målradsjoin som pekade på fel resultatrad, en skottjämförelse och ett dubblettindexprov.
- **⚠️ Fixad samtidigt, för materialiseringen hade förstärkt den:** guldfodringens **del 2** räknade
  `SeriesType == Speed` brett och svalde därmed snabbpistolserier. SHB 5.1.1.1 pt 2 definierar del 2 som
  3 **tillämpnings**serier mot B 100 eller 1/6 C 30 — snabbpistoltavlan är Elits bevis, inte detta.
  Kodbasen sa redan så i `Marken.SeriesDiscipline`s egen kommentar medan räkningen accepterade båda.
  Mätt på medlem 1078 i dev: 4 tillämpningsserier och 10 snabbpistolserier → gamla koden svarade 14.
  `PendingSpeedCount` är scopad likadant.

**Tre guldserier FÅR komma från en enda tävling** (Stefan 2026-08-28). Dev visar en skytt med sju
kvalificerande 48-serier från samma tävlingsdag, alla räknande. Beteendet är oförändrat sedan före
materialiseringen — det syns bara nu.

**Varför frågan var värd att ställa innan filtret lyftes** (behållet, för nästa gång något liknande
dyker upp): `AnalyzeSeriesProofAsync` filtrerade bort materialiserade
serier (`!s.IsFromCompetition`). Elit brons kräver 45 p/serie där C-guldkravet är 46, så att bara
släppa igenom dem hade börjat dela ut Elitmärken som SIDOEFFEKT av en ändring om guldfodringen och
ligan — och märkestilldelning är enkelriktad, så ett felaktigt "ja" hade inte gått att ångra. Det var
en regelfråga, inte en kodfråga, och den hörde därför hos Stefan.

**⚠️ TRÖSKELVÄRDENA ÄR RÄTT SOM DE ÄR — ändra dem inte utan att läsa `Documentation/shb_kap5.txt`.**
En sammanfattning som cirkulerade 2026-08-28 angav guldkravet till A 34 / B 40 / C 46; källtexten (kap 5,
tabellen under punkt 1) säger **Brons 32/33/34, Silver 38/39/40, Guld 43/45/46** för A/B/C. De avvikande
siffrorna är **C-kolumnen läst nedåt** — ett lätt misstag i den PDF-utvunna layouten, och alla C-värden i
sammanfattningen var riktiga. Att följa dem hade sänkt A-guldkravet från 43 till 34 och retroaktivt
kvalificerat mängder av serier; märkestilldelning är enkelriktad. Koden matchar källan.
- **Åldersavdragen:** 55+ → −1 p/serie (implementerat, matchar källan). 65+ → källan har TVÅ
  bestämmelser: −2 p/serie (5.1.1, under tabellen) och *"Skytt som ett föregående år fyllt 65 år
  erhåller inteckning efter att ha uppfyllt fordringarna för pistolskyttemärket i silver"* (5.1.2.2,
  som handlar just om årtalsmärken). Guldfodringen ÄR årtalsmärkesinteckningen, så koden följer 5.1.2.2
  (silvertabellen) — den mer specifika bestämmelsen, och den mer generösa. Medvetet val, dokumenterat här
  eftersom −2 vore en rimlig läsning av 5.1.1 ensam.

**Operatörssteg:** kör `Migrations/add-source-and-counts-to-markenseries.sql` **(körd i prod
2026-08-28)** och därefter `Migrations/add-sourcetable-to-markenseries.sql` — den senare backfillar
befintliga rader till `PrecisionResultEntry` och byter det unika indexet mot det sammansatta. Båda
idempotenta, egen batch per objekt, sätter `QUOTED_IDENTIFIER ON` själva. Adds C# → full ombyggnad.

Verifierat 59/59 `hpsk-verify/marken-compseries-sync-verify.mjs`, två körningar i rad (**A/B: 11 av 44
faller** på den version som mättes före Elit- och Duelltilläggen, inklusive alla fyra
propageringspåståenden och hela dubblett-halvan). Sviten redigerar verkliga resultatrader — poäng ner
under kravet, 45 p som Elit-bevis, 44 p under båda trösklarna, ändrad poäng, raderad rad — och
återställer varje mutation ur en FULL snapshot, **inklusive en synk efteråt**: att återställa resultatet
räcker inte, serien beskriver den muterade poängen tills något rekonciliererar, och nästa körning hade
då mätt förra körningens rester. ⚠️ Den **konvergerar registret innan den mäter det** av samma skäl.
⚠️ Och den anropar `GetMemberMarkenDetail` som **sajtadmin** — målraden plockas ur verklig dev-data och
kan tillhöra vilken krets som helst, så en klubb-/kretsadmin nekas, ingen synk körs, och
propageringspåståendena faller medan koden är hel.
Regression: marken-witness-date 79/79, resultlist-flatten 38/38, märkes- och kvalifikationstabellerna
oförändrade före/efter (6/6). Dev: 186 materialiserade precisionsserier + 12 Duellserier.

### Resultatlistan kan läsas per klass ELLER per vapengrupp (2026-08-28)

Från samma klubbadminrapport: *"vid mästerskap är det ju en enda lång lista att redovisa … att kunna
växla mellan total resultatlista och en sorterad efter klass vore roligt för deltagare"*. Klargjort
med rapportören att det gäller **klass 1–3, inte vapengrupperna** — och därmed är det väldefinierat,
eftersom klasserna inom en vapengrupp skjuter samma program.

**Det är en VY, inte en sammanslagning.** Placeringar och medaljer räknas oförändrat per klass;
varje rad i det utplattade läget skriver ut sin klass OCH sin officiella placering i klassen
(`C2 · 1`), så listan aldrig kan läsas som ett resultat. En riktig sammanslagning ändrar vem som
vann — det är `ClassMergingService` och en annan funktion.

**Implementerad som en omgruppering av `classGroups` FÖRE renderaren** (`crFlattenByWeaponGroup` i
`Views/CompetitionResult.cshtml`), inte som en andra tabellbyggare. Hela kolumnbredds- och
cellogiken är oberörd och kan därför inte glida från klassvyn. Ren vy-ändring → ingen ombyggnad.

- **⚠️ Vapengruppen resolvas via `window.getWeaponClassCode`, aldrig `charAt(0)`.** `A_opt_2` skulle
  annars foldas in i A, och optiksikte är inte samma tävling som öppet sikte. Partialen
  `_ShootingClassesBootstrap.cshtml` var **inte** inkluderad i resultatvyn och är det nu; den
  resolvar både klass-ID och visningsNAMN, vilket krävs här eftersom resultatrader bär namnet.
- **⚠️ Koden läser SKYTTARNA, inte gruppens rubrik.** En grupp kan vara sammanslagen (`C2+Dam`) eller
  omdöpt av en admin (`C2 Allmänt`) — rubriken resolvar då till ingenting.
- **Lika total OCH lika X delar placering.** Den officiella särskiljningen är en serie-countback som
  servern gör per klass; att räkna fram en egen tvärs över klasser vore en andra rangordningsregel
  fri att säga emot den riktiga, så den utplattade listan avstår från att skilja skyttar den inte
  kan skilja ärligt.
- **⚠️ Pennan för att döpa om klassgruppen är BORTTAGEN i utplattat läge.** "Vapengrupp C" är en
  etikett den här sidan hittat på, och en override sparad på den nyckeln hade skrivit en rad i
  `classNameOverrides` som aldrig mer matchar någon verklig klassgrupp. Radera-knappen bär däremot
  fortfarande skyttens RIKTIGA klass (den kommer från skytten, inte från gruppen).
- **Växlingen erbjuds bara när minst en vapengrupp har mer än en klassgrupp.** En kontroll som inte
  gör något är sämre än ingen kontroll. Verkligt fall i dev: Rynketians sammanslagningar lämnar exakt
  en grupp per vapengrupp, och där finns ingen växling.
- **Särskjutningsnoterna följer med** till den utplattade gruppen — de förklarar hur en MEDALJ
  avgjordes och är sanna oavsett gruppering.
- Valet minns per tävling i `localStorage` (`hpsk_cr_flat_<compId>`), och `crShooterTotals` är nu
  enda platsen skotten parsas (raden räknade själv om samma sak).
- **Springskytte och Fältskytte har egna renderare i samma fil och är medvetet orörda.**

**⚠️ Fälla för den som testar detta: en start är per (skytt, KLASS).** 19 skyttar på tävling 2586 är
anmälda i två klasser och har därför två rader i den utplattade vyn — vilket är rätt. En uppslagning
på bara namnet hittar den andra starten och rapporterar en korrekt rad som fel; det kostade en
felsökningsrunda. Matcha på (namn, klass).

Verifierat 38/38 `hpsk-verify/resultlist-flatten-verify.mjs` (**A/B: baseline avbryter efter 1/3** —
utan växlingen kan inget nedanför köras). Läser bara; rör inget utom besökarens egen
localStorage-preferens, som städas. Fixtur 2586 (7 grupper: 3 i A, 4 i C) + Rynketian som negativt
fall. Regression: marken-witness-date 79/79.

### Märken: bevittning på plats, skjutdatum och kontext i valideringskön (2026-08-28)

Från en klubbadmins rapport: *"jag får ingen info eller förhandsvisning om vad det är … jag kan inte
se vilka datum som avses"*, plus frågan om en serie ens kan skickas in obevittnad. Den kunde det.

**`SubmitSeries` var ingen grind.** Den lade serien `Pending` i kön och returnerade QR-token som ett
*erbjudande* — kön var den asynkrona vägen, så en guldserie kunde skickas in från soffan. Nu finns
klubbinställningen **`markenRequireOnSiteWitness`** (per klubb, av som standard, samma mönster som
`markenSignoffSkjutledare`).
- Grinden sitter på **godkännandet**, inte på inskicket: vid inskickstillfället har ingen ännu
  validerat, så "kräv bevittning" kan bara betyda *approve kräver ett bevis på att någon stod där*.
  `SetSeriesStatus` kräver då en **levande verify-token myntad för exakt den serien**
  (`IsLiveVerifyToken(token, "series:{id}")`), vilket bara skyttens egen skärm kan producera.
  `MarkenVerify.cshtml` skickar med `TOKEN`; köns Godkänn skickar ingen och nekas med en förklaring.
- **Avvisa är alltid tillåtet** — en klubb som kräver bevittning måste ändå kunna rensa kön från
  serier ingen bevittnat, annars låser kön sig.
- **Medvetet per klubb, inte global spärr.** Vid en bana utan täckning betyder ett hårt krav att
  ingen guldserie kan registreras alls; klubben väljer vilken risk den vill bära.
- **Gäller serier, inte `comp`/`stormastar`** — ett egenrapporterat mästerskapsresultat är en
  resultatlista på papper, inte något en funktionär står och tittar på.

**⚠️ Tokens har giltighetstid nu (`ToTimeLimitedDataProtector`, 30 min) — och det öppnade ett hål som
MÅSTE täppas i samma ändring.** `CreateProtector` ensam gav en evig token; när den blir bärande
grind är evigheten fel. Men en utgången token på en klubb som kräver bevittning skulle **stranda
serien permanent**: godkännbar av ingen, raderbar bara efter att först ha avvisats. Därför
**`GetMyVerifyLink(id)`** — myntar en FÄRSK kod för skyttens egen *pending* serie (QR-ikon i "Mina
inskickade serier" → `#mySerieQrModal`, återanvänder `markenRenderQr` så skärmen pollar och stänger
sig själv när någon godkänner). Inför aldrig en utgångstid på något som är enda vägen framåt utan att
samtidigt bygga vägen tillbaka.

**Skjutdatum går att ange** (`SubmitSeriesRequest.SeriesDate`, "yyyy-MM-dd"). Förut `DateTime.Now`
hårdkodat, så en serie skjuten igår registrerades som skjuten idag. `ResolveSeriesDate` bär reglerna:
tomt = idag, inte i framtiden, högst **60 dagar** bakåt (äldre hör till den funktionärsgrindade
klubbliggarimporten). **`Year` följer skjutdatumet, inte inskicksdagen** — en serie skjuten 28
december och inskickad 3 januari tillhör det gamla årets guldfodring. Klientsidan delar EN
implementation (`markenDateInit`/`markenDateReset`/`markenDateValue` i `_MarkenSerieQuickSubmit.cshtml`,
gränserna speglar servern) över alla tre inskicksytorna — Guldserie-modalen, Snabbserie-modalen och
snabbinskicket vid banan. Den degraderar till ett vanligt textfält när flatpickr inte finns på sidan
(`TrainingMatch.cshtml` laddar den inte).

**Kön visar nu det som gör ett beslut möjligt:** datum, skyttens notering, och en förklaring när
klubben kräver bevittning. Förut fick den som klickade Godkänn namn + fem siffror.
- ⚠️ **`seriesDate` skickades redan** från servern och ritades bara aldrig ut — leta efter det
  innan du lägger till ett fält.
- ⚠️ **Ett tävlingsresultat bär `competitionDate`, en serie bär `seriesDate`** — läser man bara den
  ena blir halva kön odaterad. Båda köerna (`renderClubQueueItem` i ClubAdminPanel,
  `renderQueueItem` i UserProfile) har fixen; det är samma dual-renderer-fälla som startlistorna.
- `requiresOnSiteWitness` ligger **per rad i `SerieDto`**, inte per kö: Min sida-kön spänner över
  flera klubbar och bara vissa av dem kan kräva bevittning. `RequireOnSiteWitness` memoiserar per
  request, annars blir det en innehållsuppslagning per rad.

**Rättelse av något jag trodde saknades:** *självvalidering var redan blockerad.* `SelfValidateMsg`
kontrolleras i `SetSeriesStatus`, `SetCompResultStatus`, `SetStormastarStatus` och
`GetSerieForVerify`. Kontrollen sitter i anroparen, inte i `CanValidateSeriesAsync` — läs den innan
du drar slutsatsen att grinden fattas. (Klubbliggarimporten självvaliderar avsiktligt och rör inte
`SetSeriesStatus`.)

**Fortfarande OBYGGT och beslutat:** materialisering av tävlingsserier i `MarkenSeries` (löser både
dubbelräkningen i guldfodringen och att guldserieligan inte ser tävlingsserier — se
`marken-club-admin-report-2026-08-28` i minnet), dubblettvarning vid inskick, och växlingen
samlad/klassvis resultatlista. Foto i snabbinskicket väntar på Android-appen.

**Operatörssteg:** lägg till `club.markenRequireOnSiteWitness` (True/False). Adds C# → full rebuild.
Ingen SQL.

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

**Admin UI (`CompetitionFinalsStartListManagement.cshtml`, Rev 3 2026-07-05):** the card leads with a **3-mode chooser** (`#finalsModeChooser`) + an always-visible **readiness ladder** (`#finalsReadiness`: Resultat registrerade / Finalsstartlista skapad / Publicerad) + a plain-language explainer. The freeze/cut wizard is now just one of three modes:
1. **Fortsätt i samma ordning** (`Mode:"clone"`) — one click; deep-copies the official qualifying start list so finals results-entry uses the SAME skjutlag/order. No freeze, no cut.
2. **Placera om efter resultat** (`Mode:"rerank"`) — one click; flattens every shooter with qualifying results into ONE global list sorted by total (score→X→name), chunked into skjutlag. No freeze, no cut.
3. **Mästerskapsfinal (avancerad)** — the existing 3-step freeze→config→generate wizard inside `#generateFinalsStartListModal`, for championships that cut to a subset. Gate is now explicit (Generera disabled + `#wizardGateMsg` until ≥1 group frozen).

Modes 1+2 hit the new endpoint **`GenerateSimpleFinalsStartList`**; all three share **`PersistFinalsStartListAsync`** (the save/publish/render tail refactored out of `GenerateFinalsStartList`). `BuildRerankFinalsConfigAsync` reads `GetAvailableClassRankingsAsync` (live, needs only entered results — NOT a generated result-list node; merges only matter for mode 3). **Format strings:** clone→`"Final"`, rerank/wizard→`"Championship Finals"`; both renderers (`StartListHtmlRenderer` + `PrecisionFinalsStartList.cshtml`) treat "Final" as finals for labels but show Rang/Kvalresultat columns only when a shooter carries a QualificationScore (`showKval`). `GetFinalsStartList` now returns `TeamFormat`. On success the card refreshes. **The "result list must be generated first" framing was corrected — the real prerequisite is *entered results*.**

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

### Fältskytte Projekt + hub UX + per-station name/reorder (2026-06-15)

**Projekt (Phase 1)** — a lightweight container that groups standalone configs for organisation, shared access, and archiving.
- Tables `FaltskytteProject` (Id, Name, Description, OwnerMemberId, OwnerClubId, Status [Active/Archived], dates) + `FaltskytteProjectMember` (ProjectId, MemberId; PK composite; FK CASCADE). `FaltskytteConfiguration` gains nullable `ProjectId` (FK → FaltskytteProject **ON DELETE SET NULL** — deleting a project orphans configs to standalone, never cascades). Migration `Migrations/create-faltskytte-project-tables.sql` (gitignored; run in SSMS).
- `Services/FaltskytteProjectService.cs` + `Controllers/FaltskytteProjectController.cs` (ListAccessible, Create, Update, Delete, Archive/Unarchive, AddMember/RemoveMember, AssignConfig). Member picker reuses `FaltskytteConfiguration/SearchMembers`.
- **Access rolls up, doesn't compete:** `FaltskytteConfigurationService.CanViewAsync`/`CanEditAsync` now also pass when the config's `ProjectId` is set and the viewer owns/belongs-to that project (private helper `IsProjectMemberOrOwnerAsync`, raw SQL to avoid a circular service dep). `AssignToProjectAsync` requires edit on the config + membership of the target project. `BuildViewAsync` resolves `ProjectName` + `IsInArchivedProject`; the project view carries rollup counts (ConfigCount/ApprovedConfigCount/PendingConfigCount).
- **Phase 2 (deferred):** manager role, a designated responsible Banläggare, and a one-click "approve all" rolling up the existing per-config approvals.

**Hub UX** (`FaltskytteConfigurationHub.cshtml`, full rewrite): compact list/table toggle, sorting (modified/name/stations/owner), sticky toolbar, pinned ⭐ favorites, group-by-project (collapsible sections + "Utan projekt" + name-only header for projects the viewer can't manage), show-archived. View/sort/group/pins persist in localStorage. New create/edit-project modal (member search/add/remove, archive, delete) + assign-config modal; create-config modal gained a project picker.

**Per-station name + reorder** (`_FaltskytteConfiguratorScript.cshtml`):
- `name` added to `createDefaultStation`; editable in advanced-mode card body + simple-mode header; `faltCfgEscapeHtml` helper added.
- **Reorder is per weapon class** with array order = display/sequence and the station NUMBER as stable identity (looked up everywhere by `.station`, and a single-station save serializes the whole blob — so no renumber, nothing downstream breaks). `ensureStations` rewritten to PRESERVE array order (keep in-order ≤count, append missing). `faltCfgMoveStation` (advanced, per-class; linked classes sync via faltCfgMarkDirty) + `faltCfgSimpleMoveStation`/`faltCfgSimpleSetName` (simple, uniform across classes). Scope: editor + printout only — NOT patrol generation / result-entry order (those sort by number).
- **Station name in headers:** added `Name` to `FaltskytteStationConfig` (server model) + `StationName` to `FaltskytteStationView`; `GetStationEntryData` returns it (first non-empty across classes). Shown after "Station X" in: editor cards, printed station card (`faltCfgPrintStation`), result-entry roll-call + entry headers (`fseStationLabel()`), and the public `FaltskytteStationInfoStatic` (QR Förutsättningar; the old `StationInfoCard` partial that also showed it was orphaned and deleted 2026-08-26). Intentionally NOT on Tidur / live results.

Verified end-to-end via Playwright (32/32, 2026-06-15) — see KB `faltskytte-konfigurationer.md` for the user-facing guide.

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

**`MinShotsPerFigure` / `MaxShotsPerFigure` are STATION-WIDE** (mirroring SHB phrasing "min/max träff per figur i en station"). Both fields render as Min träff/fig + Max träff/fig inputs in the configurator's station card. Min defaults to 0 (no requirement), shown in BOTH Normal and Poäng modes. Max defaults to 6 (Poäng's "no cap"), shown in Normal only (Poäng users don't need to set it). Non-default values surface in the QR Förutsättningar page (`FaltskytteStationInfoStatic`) as *"Min/Max träff/figur"*. An earlier commit (`7ee3df9`) added these as **per-figure** controls in error — corrected in `28c63c5`.

**Svårighetsgrad badge:** `round(100 × SHB-min-tid / station.shootingTimeSec)`. 100 % = exactly at SHB minimum; <100 % = generous; >100 % = below SHB minimum (impossible per regelverk but mathematically valid). Plain badge — no threshold colors per Banläggare feedback. Sourced from the same Excel formula HPSK Banläggare have used historically ("Pokalen 2 tidutrakning.xls", VBA dump).

**Tävlingstyp moved into the configurator (2026-05-26):** the Poängberäkning / scoringMode dropdown — formerly per-competition on the wizard + edit modal — is now part of the configuration. Picker lives in `Views/FaltskytteConfigurationEditor.cshtml` next to the Mörker toggle and writes `_scoringMode` into the JSON blob meta keys.
- **Phase 1:** picker added; `_FaltskytteCompetitionPicker.cshtml` propagates `_scoringMode` → competition `scoringMode` doctype property on Anslut + Konvertera, so all 7+ downstream read sites (FaltskytteController, FaltskytteStandardMedalService, FaltskytteShootOffService, FaltskytteStatsService, FaltskytteStationEntry.cshtml, FaltskytteResultsManagement.cshtml, CompetitionResult.cshtml) keep reading the competition property unchanged.
- **Phase 2:** wizard + edit modal dropdowns replaced with a hidden input + read-only `<span id="{prefix}scoringMode_display">` badge ("Normal" / "Poängfält"). `faltUpdateScoringModeDisplay(prefix)` is invoked on modal open and on picker attach to keep the label in sync.
- **⚠️ The competition's `scoringMode` property is a MIRROR that goes stale — never read it alone (2026-08-10).** `faltCfgSetScoringMode` writes only `_scoringMode` into the config blob, and the picker propagates it to the competition **only at Anslut time**. Change the Tävlingstyp in the configurator afterwards (or attach a config predating the propagation) and the two disagree *permanently*, with no UI anywhere showing the conflict. Reported by a club admin whose printed station card omitted Max träff/fig while its own QR code said "max 2" — the card read Poäng from the config and correctly suppressed it, the QR page read Normal from the property and leaked the **legacy `maxShotsPerFigure` default of 2** (see `_FaltskytteConfiguratorScript.cshtml:625`). Same drift silently scores a Poängfält competition as Normalfält in results + standardmedaljer. **Always resolve via `FaltskytteScoringMode.Resolve(config, competition.GetValue<string>("scoringMode"))`** (config wins → property → "Normal"; tolerates an array-shaped `["Poang"]` read). Wired at all 7 server-side sites: `FaltskytteController` 224/602/949/1555, `FaltskytteStatsService:97`, `FaltskyttePrintController:53`, `StationPage.cshtml` `?t=` branch. The JS surfaces read `results.scoringMode` off those payloads, so they inherit it. `FaltskytteConfigParser` now surfaces the key as `FaltskytteCompetitionConfig.ScoringMode` (it used to be stripped with the other `_`-prefixed meta keys). Not fixed: the drift itself — the configurator still can't push a changed Tävlingstyp out to already-attached competitions, so the wizard/edit-modal badge can still show a stale label.

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

**Springskytte: "Nyss startat" band + paged standings (2026-08-09).** Built for a wall TV at SM, where one weapon class is on screen at a time (C + stafett Saturday, A Sunday) and the field is 100+ — so `?tiles=`/`?vaxla=` are the wrong tools here; the asks were *fit more shooters* and *show the start line*.
- **`Springskytte/GetSpringskytteBoardState(competitionId, weaponClass?)`** (new, anonymous like the board itself) reads the **published** start list: `justStarted` = the last **3** starters whose time has passed, `upcoming` = the next 4 due off. `GetSpringskytteResults` cannot answer this — it only returns shooters who already have a result row, so the start line was invisible to the board.
- **`allStarted`** (no upcoming left) makes the board **drop the band entirely**, handing the full screen to the standings — which is also the point where spectators stop watching the line.
- **`justStarted` is deliberately START-based, not "still out on the course".** The first cut listed everyone without a finishing time; a *missing result is indistinguishable from a runner*, so a slow result desk made the band swell toward its cap and crowd out the standings (measured: 43% of a 1080p screen vs 18% now). A fixed 3 cannot do that, and it needs no staleness guard.
- **DNS excluded, DNF kept** — a DNS never took the line; a DNF did start. A DNF'd future starter is also kept out of `upcoming` (not "next off").
- **"Now" is resolved server-side** and echoed as `serverNowSec`; the client ticks the time-since-start clocks locally from that baseline. The board machine is a borrowed kiosk PC and a skewed clock would show the wrong shooters at the line.
- Dated lists for another day are skipped (`ListDate` vs today) — start times are time-of-day only, so Sunday's A list would otherwise read as live mid-Saturday.
- **Resolved 2026-08-12:** a shooter with partial station results but no finishing time used to appear as an unranked `rank 0 / Totaltid -` row. The flat board (below) **excludes them** — see `springFlatSorted`. They are represented by the band while they run. `?grupper=1` still shows them unranked.
- **`pistol.nu` brand centred in the top bar** (`.rb-brand`) — **absolutely positioned**, not flex-centred, so it stays put as the competition title and the controls change width; `.rb-header h1` is capped at 42% and `#rbStatusBadge` gets `margin-left:auto` to keep the middle clear and the controls hard right. Hidden below 992px, where there is no room to centre anything. The verify script asserts the geometry (centre within 2px, title not overlapping), not just presence.
- **Paged, not scrolled.** `springPaginate` chunks a flat row stream (group headers included) and `springPageTick` flips every **12 s**, with a `Sida x/y` line; a page opening mid-class repeats the header with `(forts.)`. A crawl is unreadable and you can never find your own name. **Capacity is measured from the real DOM** (band + thead + row heights) — the band's height changes with how many are out, and the first cut of the maths ignored it and let the table run past the bottom edge, i.e. re-created the truncation paging exists to fix. `liveboard-springskytte-verify.mjs` asserts the last row sits inside the viewport for exactly that reason.
- **Shot dots are deliberately kept** (Stefan: the biathlon-style hit/miss row is what spectators already know how to read). `?compact=1` drops **Bom** instead — it's just a count of the red dots. `?rows=N` overrides the auto-fit.
- **Flat cross-class table is the Springskytte DEFAULT (2026-08-12), `?grupper=1` restores group headers.** `springFlatSorted` replaces the per-class blocks with one list ranked on **Totaltid ascending** plus a narrow **Klass** column carrying `H 35 · 3` (class + rank within class). On an SM C-field the ~10 age/gender headers cost ~10 rows per pass (plus a `(forts.)` repeat when a page opens mid-class); the column costs none. **Only finishers are ranked** — a shooter mid-course has accumulated *less* time and a naive Totaltid sort puts someone still out running at the TOP of the board. Excluded from the table, shown by the band. DNF → bottom, unranked. DNS → never shown. The `#` column is therefore the **overall** position and the gold/silver/bronze tint follows it; because Springskytte places *per class*, class leaders instead get a discreet cue (tinted row + green `border-left` on the first cell). That edge is a **cell border, not a `box-shadow` on the `<tr>`** — `.rb-table` is `border-collapse:collapse`, where a row shadow is not reliably painted. Logic covered by `scratchpad/test-flatsorted.mjs` (extracts the real function, 9 assertions).
- Verified 40/40 via `C:\Repos\hpsk-verify\liveboard-springskytte-verify.mjs` (dev comp 6628). The band is time-relative, so the test data needs re-timing before a run — see the seeder note in that script's header.
- **⚠️ The board used to hardcode `isOfficial=false` for Springskytte — fixed 2026-08-12.** `fetchResults`' Springskytte branch set it literally false and never read the payload, so the badge said **LIVE even on a fully published list** and anything keyed off official (now the results QR) could never fire. It now reads **`officialWeaponClasses`** — NOT `isOfficial`, which isn't in that payload; the yes/no field is `resultsOfficial` and only means "at least one class is public". Because A and C publish independently, `updateStatus` computes official as **every weapon class currently on screen is published**, so C being official can't make a still-preliminary A list claim OFFICIELLT. `isMedals` is still hardcoded false for Springskytte (deliberate — reading it would add a Std column nobody asked for; the payload does carry `isAwardingStandardMedals`).
- **Results-QR panel for the wait before the medal ceremony (2026-08-12).** There is ~an hour between the last shooter and the ceremony, when what spectators want is their own full result on their own phone. A right-hand panel (`#rbQrPanel`, `.rb-qr`) shows a QR to the competition's official `/resultat/` page. **It appears by itself when results go official** — the board is a Chrome kiosk with no keyboard to hand and changing its URL at the venue means killing the supervisor loop over Remote Desktop, so nothing may require an operator action. "Official" is also the guarantee the link resolves, since `PublishResults` is what creates the `competitionResult` node. `?qr=1` forces it on, `?qr=0` off (`rbQrApply`, called from `updateStatus` so it re-evaluates on every 15 s poll; guarded by `qrShown` so the PNG isn't re-fetched each time). **The URL is resolved from the node** (`competition.Children()` → alias `competitionResult`, absolute URL built from `Context.Request`), never hardcoded — the competition URL is a function of scope + host, so a literal path would silently rot into a 404. **Split left/right, not stacked:** the panel costs WIDTH, and the paging capacity maths is measured from the table wrap's HEIGHT, so no rows are lost; a bottom band would silently shrink every page. QR PNG comes from the existing anonymous `Faltskytte/GenerateQrCode?url=` (`pixelsPerModule:10`), upscaled with `image-rendering:pixelated` so modules stay hard-edged and scannable across a hall.
- **Stafett (relay) is intentionally NOT on the board** — its results live in their own store keyed `(Competition, TeamId)` and `renderSpringskytte` only walks the class groups. Confirmed out of scope 2026-08-09.
- Views + one controller method → **full rebuild** to deploy (the `.cshtml` alone is not enough).

### Fältskytte: two QR codes per station + station-layout secrecy (2026-05-27)

**What:** Each Fältskytte station now carries **two** purpose-built QR codes, and station layouts are kept secret (not browsable/enumerable).

- **QR-1 — Förutsättningar (on the station card):** opens a **read-only** view of that station's conditions + a **static per-figure visibility timeline** (green show/hide bands, no clock). **No login.** Served at **`/station?t=<token>`** where the token is an opaque `IDataProtector` payload (`"<compId>:<station>"`, protector purpose `"Faltskytte.StationInfoQr.v1"`) — non-enumerable + non-forgeable, so a shooter can't change a station number to preview others. Rendered **server-side** by the new partial `Views/Partials/FaltskytteStationInfoStatic.cshtml` (typed `FaltskytteStationConfig`; C# port of the Tidur's `tmrFigBands`; bands use inline styles + `InvariantCulture` percentages — avoids the "top-level `<style>` in a partial 500s" trap).
- **QR-2 — Result entry (separate cut-out, placed by the Målgrupper):** opens `/station?c&s`. **Login required.** Shows an **adaptive landing**: a *Stationschef* button if the user has staff access + one button per patrol they're in (labelled `Vapengrupp · Patrull N · HH:mm`) when `faltskytteSelfServiceResults` is on. 0 → "ingen behörighet"; 1 → straight there (`?role=chief` or `?p=<patrolId>`); 2+ → chooser. **Fixes the dual-role case** (functionary who is also a shooter). **No sticky memory — select every scan** (the old `hpsk_faltselfservice_*` localStorage auto-resolve was removed; entering under the wrong class is the failure mode to avoid).

**Secrecy lock-down:** the old `/station` `else` branch rendered the full layout (`StationInfoCard`) to **anyone** with `?c&s`, and `GetStationConfig` was an **unauthenticated** endpoint returning the **whole** competition. Both fixed: the leaky `else` is gone (logged-out → login CTA, logged-in non-participant → "ingen behörighet", never the layout), and **`GetStationConfig` is now gated by `CanReadStationAsync`** (staff or self-service participant). QR-1 renders server-side so it needs no endpoint. `StationInfoCard.cshtml` was left **orphaned** by this and was **deleted 2026-08-26** — it still carried the double-`?` returnUrl that `cb8a91d` fixed in `StationPage.cshtml` (prod IIS 404s it, Kestrel does not, so dev could never catch it), and deleting it is what stops that pattern being copied back out. Residual: a registered participant could still read other stations via the API — acceptable ("hard, not impossible"); tightening it to per-allowed-station is a follow-up.

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

## Mitt schema — personal competition itinerary (2026-07-25)

A member's own competition day in one timeline: what they **shoot** + what they **work** + the day's
**programme**. `Services/Schedule/MyScheduleService.cs` is the single source of truth; every surface
renders what it returns and none of them read start lists themselves.

**Sources it fans out over** (per discipline, because start times live in different places):
- Precision-family / Direktplacering / **Springskytte** → a `precisionStartList` child node's
  `configurationData`. All three share the doctype — the JSON **shape** is the discriminator
  (precision `Teams` / springskytte `Starters` / stafett `Teams` tagged `TeamFormat`).
- Championship finals → `finalsStartList` (same precision shape).
- Fältskytte / MagnumFält → `FaltskyttePatrol` + `FaltskyttePatrolMember` (SQL).
- Working, all disciplines → `StaffAssignment` (+ `StaffPass` for structured shifts).
- Everyone's programme → `CompetitionAgendaItem` (new table).

**Three invariants (don't relax):** (1) only PUBLISHED/official start lists count — otherwise the
member is told "startlistan är inte publicerad än"; (2) a funktionär row says "Station 3" and nothing
more — **station layouts stay secret**, reachable only via the station QR; (3) absolute times are never
invented — `ScheduleItem.StartsAt` is null when the data doesn't pin a moment down, and conflict
detection / reminders / .ics all skip those rather than guess.

**Surfaces:** `/mitt-schema` (+ `?c=<id>`, routed controller, no Umbraco node) · "Ditt schema" card on
`Competition.cshtml` · "Ditt schema" card on `HomePage.cshtml` · `/mitt-schema/kalender.ics?c=` ·
Planering → **Dagsprogram** tab · schedule-quality panel on the Bemanning tab.

**Home-page card vs "Dina funktionärsuppdrag" — split by TIME HORIZON, not role.** The schedule card
only appears for competitions within 7 days (`ScheduleHubService.WindowDays`). Competitions it shows are
suppressed on the funktionär card via `StaffHubSummary.VisibleRows(schedule.ShownCompetitionIds)` —
**except rows still needing a response**, because an unanswered invitation is a to-do and must never be
hidden behind a timeline. That exception is what keeps `NeedsResponseCount` consistent with the screen.

**Conflict detection** is deliberately conservative and never invents a duration: a pair clashes only
when one item has a real end and the other starts inside it, or when two open-ended items start on the
same minute. `Praktiskt` rows are excluded (a two-hour "Anmälan öppen" band would flag every start).
The same overlap check is surfaced to the organiser via `Staffing/GetScheduleQuality` (missing times +
per-person clashes) on the Bemanning tab.

**`StartListTeam.Date` (new, optional "yyyy-MM-dd").** Precision skjutlag only carried `"HH:mm"`, so a
two-day competition's Sunday lag couldn't be ordered after Saturday's. Empty = fall back to the
competition date, but **only on a single-day comp**; on a multi-day comp an undated lag groups under its
freeform `Label` and claims no absolute time (plus a "saknar datum" warning). Generators were left
alone on purpose — `ResolveDay` already resolves single-day comps, so the field is purely a multi-day
override set via the skjutlag edit modal. **DUAL-RENDERER (really triple):** wired in
`StartListHtmlRenderer` (cached blob) + `PrecisionStartList.cshtml` + `PrecisionFinalsStartList.cshtml`.
`UpdateTeamTimes` now sorts by **date then time** before renumbering — time-only sorting interleaved days.

**Reminders** — `ScheduleReminderHostedService` (mirrors `RankingSnapshotHostedService`) sweeps every
5 min, 30 min lead. Opted-in members are filtered FIRST (`WebPushSubscription.ScheduleRemindersEnabled`,
**DEFAULT 0** — participant pushes are opt-in only; toggle in `_WebPushSetup.cshtml`), then itineraries
are rebuilt with `useCache:false`. `ScheduleReminderLog` + its unique index is what prevents duplicate
sends: **claim (insert) then send**, so a crash costs one missed reminder rather than spam.

**Change notifications** — `.ics` is a one-shot snapshot by design, so it depends on the member being
told when times move: `PrecisionStartList/PublishStartList` and `Faltskytte/PublishPatrolList` now fire
a participant notification (first publish vs re-publish wording), gated on the existing
`autoNotifyParticipants` property, best-effort.

### Razor gotchas learned building this (cost hours — don't repeat)
`UmbracoCompilationException` carries **no message and no diagnostics**, so these fail at runtime with
nothing to go on; bisecting is the only way. In `HomePage.cshtml`:
- **A `@{ }` code block placed mid-markup broke the view.** Hoist computed values into the file's TOP
  code block instead.
- **Explicit generic type arguments in a Razor code block** (`new HashSet<int>()`,
  `new List<StaffHubItem>()`) are parsed as HTML tags. Inferred generics (`.Where`/`.ToList`) are fine —
  or move the logic into C# (which is why `StaffHubSummary.VisibleRows` exists).
- **`is { } x` property patterns** in an `@if` condition are also a brace-balancing hazard — use plain
  null checks.
- Local helper functions in a partial's `@{ }` block: same class of problem. `ScheduleItem` therefore
  exposes `IconClass` / `AccentClass` / `KindLabel` / `IsPast` / `ConflictText` / `MinutesUntil` so
  `_MittSchema.cshtml` stays markup-only.

**`Value<DateTime?>` on an unset date property returns `DateTime.MinValue`, not null** (`RealDate()`
guards it). Taking it at face value made every competition look like it ended in year 1 and silently
emptied the cross-competition lookup. The window filter tests **overlap** (`start <= to && end >= from`),
not "start date inside window" — otherwise a comp running 1–31 Aug, or one already under way, drops out.

**Operator steps:** run `Migrations/create-competition-agenda-table.sql`,
`create-schedule-reminder-log-table.sql`, `add-schedulereminders-to-webpushsubscription.sql`. No doctype
properties, no Umbraco nodes. Adds C# → full rebuild. Verified end-to-end 33/33 via
`C:\Repos\hpsk-verify\schema-verify.mjs`. KB: `KnowledgeBase/docs/mitt-schema.md`.

## Bemanning — the grid (roll × dag), open role catalog, person identity (2026-08-14 → 15)

Rebuilt from a real SM springskytte staffing plan (`Bemanningsplan Spring SM 20226.xlsx`, 41 people,
101 assignments, 3 days). Design rationale + what is still open:
`notes/bemanningsrutnat-skiss-2026-08-14.md` (local working notes — see `notes/README.md`; the
folder is gitignored, so the file is not in a fresh clone).

**Verify scripts** (all in `C:\Repos\hpsk-verify`): `staffing-grid-verify.mjs` (41, the regression to
run after any change here) · `grid-fixes-verify.mjs` (19 — width, club abbreviation, delete-any-role,
delete-any-day) · `personpanel-verify.mjs` (15 — the pencil, locked person) · `behorighet-verify.mjs`
(11 — person-level app access) · `epost-verify.mjs` (10) · `deltagare-epost-verify.mjs` (13) ·
`hans-verkligheten.mjs` (the reality walkthrough: sickness, walk-ins, typos, mid-shift dropout).

**Migrations (all four run in prod 2026-08-14):** `create-staff-role-table.sql`,
`create-staff-day-table.sql`, `add-daydate-to-staffassignment.sql`,
`add-originalname-to-staffassignment.sql`. Adds C# → full rebuild. **The 2026-08-15 batch adds no
migration** — every change below is code-only.

### Bemanning IS the grid — there are no sub-tabs
It used to be five (Rutnät / Personer / Roller / Behov & täckning / Värvning) and an organiser opening
Planering could not tell which one was the work. They were **dissolved, not hidden behind a menu** — a
menu of five is the same question with the buttons out of sight. Personer → click a name (a panel);
Roller → deleted; Behov → the number lives in the cell; Värvning → one button. `#bem-roster` markup
survives **hidden** because the add/edit dialog and a dozen helpers bind to its ids; `loadRoster()`
early-returns while it is hidden. Delete the pane and those bindings together, not before.

⚠️ **That early-return silently broke the pencil, and will break anything else that reads `groups`.**
`groups` was filled as a *side effect of rendering* the retired view, so the assignment lookup was
permanently empty and `stfEdit`'s own `if (!a) return` swallowed every click — no error, nothing.
Fixed 2026-08-15 with `ensureAssignments()`, which fetches `GetRoster` on demand; it refetches on
**every** open because nothing refills `groups` after a save while the pane is hidden, so a cache hit
would show pre-edit values. Anything else that reaches into `groups` needs the same treatment.

### The day axis: `StaffDay`, not the competition span
**The days you STAFF are not the days you COMPETE.** The source plan has Friday = iordningställande;
bigger events add banbygge a fortnight earlier and återställning the day after, all with real crew.
So the span (`competitionDate`..`competitionEndDate`) only **seeds** the list on first read
(`StaffDayService.EnsureSeeded`, runs only when empty) and the arrangör owns it after that.
- `Kind` = `Tavlingsdag` | `Forberedelse` | `Efterarbete`. **Not cosmetic:** a Förberedelse day is
  filtered out of participants' Dagsprogram in `MyScheduleService.BuildAgendaItems` (crew still see it).
- Grid columns = `StaffDay` ∪ dates that actually carry crew. The competition date is deliberately NOT
  unioned in — doing that produced a phantom column the organiser could not remove.
- **Dagsprogram reads the same list** (`GetDays`), so the two surfaces cannot disagree about the days.
- ⚠ First open of Bemanning **writes** (the seed). A read that writes.

### `DayDate` — how "heldag på lördag" is expressible
`StartsAt` carries date+time, so an all-day row could only be dated by inventing a clock time, and a
fake 00:00 becomes a real reminder at 23:30 the night before. Resolution order **everywhere**:
`StartsAt.Date → DayDate → linked StaffPass.PassDate`. Wired in the grid, `MyScheduleService`,
`MoveToDay`, `CopyDay` and the day-delete guard. A timed row must never also carry `DayDate`.

### Roles are arrangör-named (`StaffRole` + `RoleCatalogService`)
`SaveAssignment` used to hard-reject anything outside a closed catalog; the source plan used 22
functions of which 5 existed. Worse than a missing word is a word the club already uses for a
*different* job. **`RoleCatalogService` is the ONE place a role is resolved — never call
`FunctionaryRoles` from a surface again** (12 call sites were migrated). Same key = rename/override a
built-in; new key = new role; `IsActive=0` = hidden for this competition. Falls back to the built-ins
if the table is missing, and every caller already falls back to the raw key.

**"Dolda roller" is gone as a CONCEPT (2026-08-15)** — the bar, the Mer-menu entry, `HideRole`,
`SetHidden`, `GetHidden`, `HiddenRoleView` and `HideStaffRoleRequest` were all deleted. It existed
only because a built-in couldn't be deleted, which put our storage problem on screen as two kinds of
delete: Kassa had no trash icon, Kassör did. **`DeleteRole` now removes any row**, built-in included —
`IsActive=0` survives purely as the *mechanism* for a built-in (there is no row to delete), never as
vocabulary. A role comes back by typing its name again.

**Removal never drops crew silently.** `DeleteRole`/`DeleteDay` return `(ok:false, message:null,
inUse:n)` on the first call and write **nothing**; the client asks with the count and calls back with
`DeleteAssignments=true`. Same shape for both — copy it for any future "remove a thing people stand
on". `DeleteDay` also takes a **`DateKey` instead of an id**, because an "ej i planen" column has no
`StaffDay` row and previously could not be removed at all.

### Person identity: one human, many rows
Name/e-mail/phone live on the **assignment**, so one person is often 3–5 rows.
`StaffingService.ApplyPersonIdentity` acts on a **person key** (`m:{id}` / `n:{lowercased name}` — the
same key `CompetitionPeopleService` groups on) and touches every row.
- **Link ≠ replace.** Link = same human, now identified → answers and check-ins stand. Replace =
  someone else does the work → status resets to Planerad, check-ins clear, app access is revoked
  (it was granted to a login).
- **`OriginalName`** keeps the first typed name, set once. A link overwrites the name with the
  register's spelling; without this it was silent AND irreversible — during testing "Lis Erevall"
  became "Andy Haard" on three rows and the original was gone. `UndoPersonIdentity` puts it back.
- **`PersonMatchService`** guesses which member was meant (misspelling / abbreviation / first name
  only) **and** flags the same person typed twice in the same plan — the commonest typo, which the
  member register cannot see. Suggestions only; nothing is ever auto-linked.
- `ReconcileManagersIntoRoster` is now a single `INSERT … WHERE NOT EXISTS`: the page loads roster and
  people in parallel and both called it, so read-then-insert duplicated the tävlingsledare.
- **App access is a PERSON-level grant (`SetPersonAdminAccess`, 2026-08-15).** `HasAdminAccess` is
  stored per assignment but was never *read* that way: `HasRosterAdminAccess` asks whether the member
  has **any** row with it, and `SyncCompetitionManagers` distincts by member. So ticking it on one of
  five rows granted the whole competition, and unticking it there did nothing while another row still
  carried it — a switch that silently failed. It now writes every row of the person, and the
  per-assignment checkbox is gone from the dialog (the input survives hidden so an ordinary save
  round-trips the value unchanged — **no migration, existing data unchanged**). Requires a member id:
  access is granted to a login, so a free-text helper is refused server-side, not just in the UI.
- **Leadership is deduped per person**, not per assignment (`CompetitionPeopleService`). Since
  tävlingsledning is staffed per day, a 3-day SM listed Hans three times on Förberedelser. Star and
  admin-access are OR-ed across the days.

### Club names are abbreviated in Bemanning
`ClubNameHelper.Shorten` ("Varbergs Pistolklubb" → "Varbergs PK") is applied in `StaffingGridService`
**and** `CompetitionPeopleService`, so cell, person panel and printout can't disagree. This is not
cosmetic: the cell is one nowrap line of name + club + time, and an unabbreviated club is what pushed
the column past the wrap (see below).

### The grid's width is decided by the LAYOUT, never by cell content
`#grdTable` is `table-layout: fixed`, and the minimum is computed from the **column count** in JS
(`grdSizeTable`, 230 + n×215 px) rather than a `min-width` per cell. Under the old auto layout a long
club name on a nowrap line set the column's min-content width, the table outgrew `#grdWrap`, and the
last day scrolled out of sight — which reads as "the page got narrower". Names and clubs ellipsis when
tight; **the time never truncates** and is in the tooltip either way. Three days fit a laptop; eight
scroll because they genuinely must.

### E-postlista — two of them, same idiom
Bemanning: **Mer → E-postlista** (client-side, off the `people` projection). Anmälningar:
**Åtgärder → E-postlista** → `RegistrationAdmin/GetParticipantEmails` (a separate endpoint, *not* a
column on `GetCompetitionRegistrations` — that payload is a hot path re-rendered on every filter
change, and this needs a member lookup per person). Both: semicolon-joined + copy, mailto uses
**`?bcc=`**, dedupe on the **address**, and **who is missing is named as prominently as who is
included** — a list that silently omits people reads as complete. Participants include **team
members** (on a relay comp they may never appear as an individual registration); cancelled
registrations are skipped.

⚠️ `RegistrationAdminController.CanManageRegistrationsAsync` now holds the club-vs-region host check
for registration data — `ExportCompetitionRegistrations` was migrated onto it. Written out per
endpoint it has been got wrong repeatedly, always by checking only `clubId` and locking the krets out
of its own SM (memory `competition-host-shape-auth`). Add new registration endpoints onto it.

### Upprop (day-of check-in) is HIDDEN, not deleted (2026-08-15)
The two `_StaffingDayOf` PartialAsync calls in `PrecisionFunktionarerManagement.cshtml` and
`SpringskytteFunktionarerManagement.cshtml` are commented out; partial, service and endpoints are
untouched. **Nothing else reads `StaffAssignment.CheckedInAt`** — verified: written by
`Staffing/SetCheckedIn`, read only by the section that wrote it. It was pulled because it answered
"who got ticked off" and, more decisively, `CheckedInAt` is **one timestamp per assignment**, so a
Friday tick still read as "incheckad" on Sunday. Replacement requirements (per-DAY presence, "var är
X kl 15", "vem bemannar Y kl 15") are in `backlog.md` → *Funktionärer: närvaro och var-är-vem*.

### Coverage and clashes live where the organiser is standing
- Per-role need (`SetCrewNeed`) renders as `bemannat/behov` **in the cell**. The old coverage matrix was
  its own tab and was empty on every real competition — nobody found it.
- **Time gaps**: "ingen 12:00–14:00" when a cell's own shifts leave a hole. Measured only between that
  cell's first and last shift — flagging the rest of the day would invent a requirement nobody stated.
- **Clashes** are computed on the person key (not `MemberId` — 37 of 41 people on a real plan are free
  text, so the old rule checked almost nobody) and require the **same day**; comparing clock times alone
  made Saturday 08–09 collide with Sunday 07:30–09:00. Rendered above the grid, not on a retired tab.
- `SplitAssignment` cuts a timed shift at a clock time and hands the tail to someone else (the mid-shift
  dropout). Refuses on an untimed row — there is no point to split at.

### Still open
`ClubId` on the assignment. Club is the basis for splitting a competition's surplus between the clubs
that staffed it, so it must be settable per row (a person can be a member of several clubs, and an
external helper has no member record at all) — today it is only derived from `primaryClubId`.

## Dubblettsammanslagning av medlemmar (2026-08-25)

Klubbadmin → Medlemmar → Åtgärder → **Hitta dubbletter**. Efter en import finns samma person ofta
två gånger: en gång självregistrerad (annan e-postadress, så varken personnummer eller e-post
matchade) och en gång från klubbens gamla register. Den självregistrerade posten äger INLOGGNINGEN
och historiken; den importerade äger de bra fältvärdena (pistolskyttekort, adress, telefon). Ingen av
dem går att kasta rakt av.

**En sammanslagning är inte en radering, det är en FLYTT — och tabellkartan finns redan.**
`MemberDataPurgeService.SubjectTables` (37 tabeller, med regeln att aktör-/audit-kolumner aldrig
räknas) exponeras nu publikt och `MemberMergeService` går samma karta med UPDATE i stället för
DELETE. **Skriv aldrig en andra tabellista** — en som glidit isär lämnar tyst en persons resultat
kvar på ett konto som är på väg att raderas. En ny tabell läggs till på ETT ställe och båda får den.

- **`FindCandidates(clubId)`** jämför klubbens roster mot sig själv OCH mot **klubblösa** medlemmar —
  den självregistrerade som aldrig valde klubb syns inte i klubbens lista, vilket är precis där
  dubbletten gömmer sig. Andra klubbars medlemmar returneras aldrig (både integritet och för att en
  korsklubbs-merge skulle flytta en främmandes historik). `MembersAreInScope` upprepar kontrollen
  server-side på både Compare och Merge, så handpostade id:n inte kommer förbi.
- **Poäng:** personnummer 100 / pistolskyttekort 95 (IDENTITETSBEVIS — lika betyder samma person);
  därefter krävs att NAMNET också stämmer: +födelsedatum 85, +telefon 80, +adress 70, enbart namn 40.
  "Samma telefon" ensamt är ett hushåll, inte en dubblett. Uppslagen går via hinkar (namn/pnr/
  kort/telefon), inte O(n²).
- **⚠ Namnnormaliseringen får ALDRIG folda diakriter.** å/ä/ö → a/a/o gör Öberg och Oberg till samma
  person, och det är olika människor. Bara gemener + kollapsade blanksteg.
- **Överlevaren föreslås på INLOGGNING, inte på datamängd.** Den posten äger lösenordet medlemmen
  kan, hens push-prenumerationer och hens resultat. Behåll skalet och kasta inloggningen så har du
  låst ute medlemmen från sitt eget konto. Oavgjort → äldsta kontot. Operatören kan byta.
- **Fältvalen:** tomt hos överlevaren = förkryssat (inget att förlora); båda har värden och de
  skiljer sig = gulmarkerad rad och operatörens val. `MergeableFields` utesluter medvetet
  session-/samtyckesskrot (tokens, tutorial-flaggor, last-active, träningsguidens position) — det
  tillhör kontot som skapade det och skulle felrapportera överlevarens egen aktivitet.
- **ClubMembership unionsslås, flyttas inte** — unikt index är (MemberId, ClubId), så en delad klubb
  skulle krocka. Överlevarens rad suger upp den andras tomma kolumner och **tidigaste MemberSince**
  (medlemmens verkliga historik med klubben); klubbar bara förloraren hade flyttas rakt av.
- **⚠ Unika index är den verkliga faran vid flytten.** Båda kontona anmälda till samma tävling, samma
  märke, samma enhet → en bulk-UPDATE kastar 2627 och rullar tillbaka HELA tabellen. Därför:
  försök bulk, fall tillbaka på rad-för-rad, och räkna det som inte gick som **konflikt** i stället
  för att avbryta. Krockande rader följer med den borttagna posten men **namnges i resultatet** och
  innehållet ligger i ögonblicksbilden — inget försvinner osagt.
- **Tävlingsanmälningar är Umbraco-NODER**, inte rader (`competitionRegistration.memberId`), och de
  är sparade opublicerade → `Save()`, aldrig `Publish()`.
- **Ordningen är inte godtycklig:** ögonblicksbilden tas FÖRE något flyttas (den är enda kopian av
  förloraren) och förloraren raderas SIST, efter att allt loggats. En krasch på halva vägen lämnar
  båda vid liv och loggraden borta — kör om — i stället för en raderad medlem vars rader aldrig kom fram.

**`MemberMerge`-tabellen är både revisionsspår och dedupnyckel.** `MemberImportController.LoadMemberIndexes`
har nu en TREDJE pass som indexerar `LoserEmail → SurvivorMemberId`. **Utan den skapar nästa import
från samma gamla register dubbletten igen** — filen bär ju den utrangerade adressen. Det är också
skälet att det är en tabell och inte en ny doctype-property: ingen backoffice-ändring att deploya.
Passet ligger sist så en levande medlem alltid slår en utrangerad adress.

**⚠ NPoco-fälla som kostade en runda:** `Fetch<T>(sql)` genererar `SELECT * FROM <T>` om SQL:en inte
**börjar** med SELECT. Ett `IF OBJECT_ID(...) IS NOT NULL SELECT …` gav
`Invalid object name 'RetiredEmailRow'` — den frågade alltså efter en tabell uppkallad efter POCO:n.
Gör existenskontrollen till ett eget `ExecuteScalar`.

**⚠ Modalen ligger i klubbens adminpanel, som är `display:none` tills Administration-fliken öppnas.**
En modal inuti en dold panel får `.show` men blir aldrig synlig — samma fälla som geometritesterna
går i. Ett test måste klicka `#clubAdmin-tab` först.

Operatörssteg: kör `Migrations/create-member-merge-table.sql`. Ingen doctype-property, ingen
Umbraco-nod. Adds C# → full rebuild. Verifierat 29/29 `hpsk-verify/member-merge-verify.mjs` (bygger
sin egen fixtur, kör hela UI-flödet, kontrollerar att uppgifterna flyttade och att den utrangerade
adressen pekar rätt, och städar bort sig själv i SQL — `MemberAdmin/DeleteMember` kräver sajtadmin
och testkontot är klubbadmin). Regression: member-search-sort 25/25.
KB: `KnowledgeBase/docs/dubbletter.md`.

## Board Work (Styrelsearbete) — dedicated /styrelse page

Senior-friendly board workspace for clubs & regions, built on the existing `BoardRoles` table.
Lives at **`/styrelse`** (routed `StyrelseController`, **no Umbraco node** — grabs the site root like
`SightPictureController`), reached from a **"Styrelse"** link in the user menu (Master.cshtml; shown to
board members via `BoardRoleService.IsOnAnyBoard` OR admins). Scope picker lists only the boards the
member sits on; admins can deep-link `?type=&id=` from the club/region admin panels' "Styrelsearbete"
link. Four tabs: **Möten, Styrelsen, Årshjul, Valberedning**.

**Access gate** (no per-post permissions): site/club/regional admin OR an active board member of
the owner = `CanAccessBoardWork` (full access; in `BoardMeetingController` / `BoardGovernanceController` /
`BoardKallelseController`, and `CanAccessScopeAsync` in `StyrelseController`). **Scoped valberedning access
(2026-06-22):** valberedning members (non-board, see below) get a *looser* gate `CanAccessValberedning`
(full OR active valberedning role) used only by the **nomination** endpoints + the page load + valförslag
print; the UI then shows them **only the Valberedning tab** (`StyrelseScope.ValberedningOnly`, computed in
`StyrelseController.Index`). Protokoll/dagordning prints + årshjul + meetings stay on the strict gate.

- **Roles: board vs övriga förtroendevalda (2026-06-22).** `IsBoardMember` on each `BoardRole` row decides
  who actually sits on the styrelse (seeded into attendance, counted in quorum, gates full board access) vs
  other elected functionaries. **Revisor/Revisorssuppleant + the two valberedning keys default to
  `IsBoardMember=false`** in `BoardRoleDefinitions`. The /styrelse add-form drives a "Sitter i styrelsen"
  checkbox from each role's `data-board` default (restored from the old `BoardRolesManagement.cshtml`
  pattern) instead of the earlier hardcoded `true`. Styrelsen tab renders board members + an "Övriga
  förtroendevalda" section (revisor m.fl.); valberedning roles are excluded there.
- **Meetings** (`BoardMeetingService` / `BoardMeetingController`): create-from-type seeds the dagordning
  (`BoardMeetingTemplates`) + attendees from the board roster (boardOnly → revisor/valb excluded); närvaro +
  beslutsförhet (majority); beslut per agenda item; åtgärder (assignee + due, open ones surface across years);
  attachments (`BoardMeetingAgendaLinks`); justering locks the protokoll. **Edit date** (mDate field,
  onchange→UpdateMeeting) + **delete** (`DeleteMeeting`; trash button in list + "Ta bort möte" in detail)
  are wired in the UI (the endpoints had always existed).
  **Typed agenda items + editable templates (2026-06-23, Phase 1):** agenda items carry an `ItemType` —
  `note` (anteckningar only) / `text` (anteckningar+beslut) / `election` (pick N present persons).
  `Models/BoardAgendaItemCatalog.cs` is the ready-made "Lägg till punkt" dropdown; `BoardMeetingTemplates`
  = ordered catalog keys per meeting type. **Election items replaced the old end-of-meeting justerare
  picker** — `SaveAgendaElection(itemId, ids)` stores `ElectedMemberIds` and mirrors role-mapped elections
  (`val-ordforande`→IsChairman / `val-sekreterare`→IsSecretary / `val-justerare(-2)`→IsAdjuster) to attendee
  flags, which drive the protokoll signatures (multi-justerare → `StyrelsePrintModel.AdjusterNames`, one slot
  each) and the Phase-2 approver set. Clubs/regions edit + save their own agenda per type
  (`BoardMeetingTemplates` table, `BoardMeetingTemplateService` / `BoardMeetingTemplateController`, "Anpassa
  mötesmallar" editor on the Möten tab); `CreateMeeting` seeds saved-or-default. Run
  `add-typed-agenda-items-and-templates.sql`.
- **Digital justering (2026-06-23, Phase 2).** Required signers = ordförande+sekreterare+justerare (the
  attendee role flags the election items set). Status flow **Genomfört → VantarJustering → Justerat**;
  "Skicka för justering" locks edits, each signer approves, last approval → Justerat. **QR sign-off on the
  spot:** `BoardMeeting/GetJusteringQr` (QRCoder PNG of an IDataProtector token) → chromeless
  **`/styrelse/justera?t=…`** (`StyrelseController.Justera` → `Views/StyrelseJustera.cshtml`, login-gated, no
  Umbraco node) → `GetJusteringByToken` + `ApproveProtokollByToken`. **Email fallback:** `SendJusteringEmails`
  mails the link to signers who haven't approved. In-app "Godkänn protokollet" = `ApproveProtokoll`. Admin
  "Återöppna för redigering" = `ReopenJustering` (clears approvals). State: `ApprovedDate`/`ApprovedVia` on
  `BoardMeetingAttendee` + `JusteringRequestedDate` on `BoardMeeting` — run `add-board-justering-approvals.sql`.
  All board members need pistol.nu accounts (no offline override). Verified via `hpsk-verify/verify-justering.mjs`.
- **Roles & terms** (`BoardRoleService`): `ElectedDate`/`TermEndsDate`/`TermYears` on `BoardRoles`;
  "mandat som går ut" view. Valberedning helpers: `IsValberedningOf` / `IsOnAnyValberedning` /
  `GetValberedningMembershipsForMember` (RoleKey ∈ `BoardRoleDefinitions.ValberedningRoleKeys`, NOT gated on IsBoardMember).
- **Årshjul** (`BoardGovernanceService`, `BoardYearWheelItems`): per-year checklist seeded from
  `BoardYearWheelTemplate` (bokslut+årsredovisning / **årsredovisning i MAP (31/1)** / verksamhetsberättelse /
  revision / kallelse / årsmöte / konstituering / budget / medlemsrapportering — NO LOK-stöd item; pistol.nu
  has no LOK-stöd support), target dates, in-place done-toggle, overdue highlight, **per-item inline edit**
  (`svWheelEdit`/`svWheelSave` → existing `UpdateWheelItem`). Template change seeds NEW year-wheels only.
- **Valberedning** (`BoardNominations` + `BoardRoles`): a **committee roster** at the top (RoleKeys
  `Valberedning` / `ValberedningSammankallande`, one sammankallande, non-board — managed by admins) + the
  existing posts-up-for-election + candidate nominations + formal printable förslag.
- **Kallelse** (`BoardKallelseController`): emails the dagordning. Recipients by type — club årsmöte → all
  approved members; club other → board; region → region board. Confirm-before-send w/ count; reuses
  `EmailService` (SMTP ≤250) / `BrevoEmailService` (club `brevoApiKey`); records `KallelseSentDate/By/Count`;
  ticks the årshjul kallelse item; admin oversight copy.
- **Formal prints** (routed on StyrelseController, `Layout=null`): `/styrelse/dagordning/{id}`,
  `/styrelse/protokoll/{id}` (`StyrelseProtokoll.cshtml`), `/styrelse/valforslag?type=&id=&year=`
  (`StyrelseValforslag.cshtml`).

**Deploy:** run `Migrations/add-terms-to-board-roles.sql` + `Migrations/create-board-meeting-tables.sql`
(idempotent; creates meeting/agenda/attendee/action/agenda-link/yearwheel/nomination tables + kallelse
columns) in SSMS, then full rebuild. **For the 2026-06-22 revision also run
`Migrations/fix-revisor-valberedning-not-board-members.sql`** (flips existing Revisor/Valberedning rows to
IsBoardMember=0 + removes them as attendees on non-justerade meetings) — without it, legacy data keeps
counting them in attendance/quorum. **For the typed-agenda/templates revision (2026-06-23) also run
`Migrations/add-typed-agenda-items-and-templates.sql`** (agenda item type cols + `BoardMeetingTemplates`
table) **and `Migrations/add-board-justering-approvals.sql`** (per-signer approval cols + justering status);
both idempotent, existing agenda items default to `text`. **No Umbraco doctype/property/node.** UI JS reads **camelCase** DTO
keys (System.Text.Json camelCases output) and never passes strings through inline onclick/onchange attrs
(use id-only handlers). Spec: `Documentation/BOARD_WORK_PHASE1_TERMS.md`, `_PHASE2_MEETINGS.md`,
`_PHASE3_GOVERNANCE.md`. KB: `KnowledgeBase/docs/styrelsearbete.md`. Marketed on /om-pistol-nu (Årshjul shot).

## Radåtgärder: när en knapprad ska bli en meny (2026-08-24)

**Gränsen: 3 eller fler kontroller synliga på en typisk rad → EN `Åtgärder`-meny.** Det är där
alla konverteringar har landat, och den är nu genomförd överallt (11 radtyper på 8 ytor, se nedan).
Tre skärpningar:
- **Exakt 2 → meny bara om båda är ikoner utan text.** Två textknappar är läsbara; två ikoner i en
  smal kolumn är en gissningslek.
- **Varierar antalet med tillståndet → meny redan vid 2.** En layout som byter form kan operatören
  aldrig lära sig. Det var fakturaradernas egentliga problem: tre olika betydelser bakom två
  nästan identiska bock-ikoner (*en betalares ANMÄLAN* vs *arrangörens MOTTAGNA* vs *ångra*).
- **Destruktivt sist, i rött, med utskriven text** — aldrig en naken soptunna intill andra ikoner.

**Sidnivå (kortrubrik/verktygsrad): 4+ → en primär blå `Åtgärder`-meny med `dropdown-header`-grupper**
(mönstret från Anmälningar och klubbens Medlemmar). Behåll den primära åtgärden synlig utanför menyn.
1–3 *textade* knappar är bra som de är — resultatkortens Uppdatera/Publicera/Skriv ut är kortets
arbetsflöde och ska inte gömmas.

**Flikar: 8+ → grupperad vertikal räls + mobilväljare** (klubb 14, krets 10). 5–7 duger horisontellt
men kontrollera radbrytning vid 1280 px. `/admin` har 9 på en rad och är den sista ytan över gränsen
(ej gjord — rälsen bör då brytas ut till en delad partial i stället för en tredje kopia).

**Undantagen är inte förhandlingsbara** — de här ska INTE bli menyer, oavsett antal:
pagers (Första/Föregående/Nästa/Sista skytt), sifferknappsater (resultatinmatning, särskjutning),
segmenterade väljare (Kortvy/Listvy/Kartvy), stepparna ± i tiduret, siktbildens styrkors,
**flytta upp/ner** (en pil måste sitta kvar vid raden man flyttar), och `tel:`-länken **Ring** i
bemanningen (att ringa en funktionär som inte kommit är enda åtgärden som måste vara ett tryck).
Måttet är dessutom **synliga samtidigt**, inte antal i markup: `TrainingMatchScoreboard` ser ut att
ha fem knappar men visar 1–2 beroende på roll.

**Delade byggare — skriv inte en fjärde kopia:**
`_CompetitionListRenderer` (tävlingsrader, 3 ytor) · **`_SeriesRowActions`** (serier, 3 ytor) ·
**`_InvoiceRowActions`** (fakturor, 2 ytor — konfigobjekt med `qrFn`/`emailFn`/`cancelFn`/`creditFn`
och `markItems`, eftersom ytorna har olika callback-namn).

⚠️ **`data-bs-popper-config='{"strategy":"fixed"}'` på varje toggle.** Tabellerna ligger i
`.table-responsive` vars overflow klipper en absolut positionerad meny; `data-bs-strategy` ignoreras
tyst av Bootstrap. Syns bara på SISTA raden.

⚠️ **Hover-gömda åtgärder måste överleva att pekaren lämnar raden.** `#grdTable .grd-roleacts` är
`visibility:hidden` till hover, och den öppna menyn är ett BARN till den spanen — utan
`:focus-within` / `:has(.show)` försvinner menyn på väg till menyvalet.

**Fällor för den som testar detta:**
- **Öppna den YTTRE fliken först** (`regionAdmin-tab` / `clubAdmin-tab`): en inre underpanel bär
  `.active` medan hela adminpanelen är `display:none`, så klasspåståenden går igenom på en osynlig
  sida och all geometri läser noll.
- **Personlistan i Bemanning ligger i en offcanvas** (`stfOpenPeople()`), och `#stfPeopleBody` finns
  i DOM ändå → en tom platshållarrad läses som "ingen person på tävlingen" på en tävling med 41.
  Personnyckeln i `onclick` är dessutom **URL-kodad**; `stfOpenPerson` vill ha den avkodad.
- **Bemanningsfliken pollar och byter ut sin `<tbody>`**, så en nod som hämtades före klicket kan
  vara borttagen när menyn läses — läs den klickade noden först, och gör om försöket en gång.
- **Styrelsemöten:** ett nyskapat möte kan ha TOM dagordning och ett justerat protokoll är låst —
  båda renderar ingen Bifoga-kontroll. Välj ett möte med punkter som inte är justerat.
- **Förberedelser och Bemanning finns sällan på samma tävling** i dev (5312 har uppgifterna, 6628
  har folket). En tävling för båda halvorna hoppar tyst över den den saknar.
- **En `if (await x.count())`-guard förvandlar en borttagen kontroll till ett grönt hopp.** Så tappade
  `behorighet-verify` två påståenden och rapporterade ändå 9/9. Assertera att kontrollen finns.

Verifierat 113/113 `hpsk-verify/action-menus-sweep-verify.mjs` (alla 8 ytor, inklusive att nedersta
radens meny går att klicka på). Regression: row-action-menus 60/60, region-series-tab 38/38,
complist-shared-renderer 23/23, staffing-grid 41/41, grid-fixes 19/19, personpanel 16/16*,
behorighet 13/13*, epost 10/10, consolidated-invoice 42/42, organiser-consolidation 31/31,
region-receivable 19/19. (*uppdaterade till menyn — samma dialog, ny väg in.)

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
- **Certifications System (2026-04-29)** - SPSF-registered roles (Föreningsinstruktör, Kretsinstruktör, Riksinstruktör, Vapenkontrollant, Banläggare). Personal cert stored in `MemberCertifications` table; appointment via member groups. Hierarchy-aware grant authority. Statistik integration on club + regional + admin tabs. Members-tier panels on Club and RegionalPage. See [Documentation](Documentation/CERTIFICATIONS_SYSTEM.md). **Manual operator steps required:** run `Migrations/create-member-certifications-table.sql` and add `area` Textstring property to `regionalPage` doctype. **2026-07-05:** the club Certifieringar tab (`CertificationManagementPanel.cshtml`, Club scope) shows a note card listing the club's region's Kretsinstruktörer + contact info, telling admins to contact them for Föreningsinstruktör/Vapenkontrollant/Banläggare training. Backed by `CertificationController.KretsinstruktorerForClub(clubId)` (resolves region via `GetRegionForClub` = club `regionalFederation`; contact email/phone gated to club/site/regional admins).
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
- **[SPRINGSKYTTE_STAFF_SCREENS_REUSABLE_PATTERNS.md](Documentation/SPRINGSKYTTE_STAFF_SCREENS_REUSABLE_PATTERNS.md)** - 2026-07 range-role overhaul (per-role/per-class staff screens, field-scoped auto-save, wake-lock, connectivity indicator, deferred offline-queue analysis, move/DNS + timeline free-slots, penalties ledger on every staff screen, unique-per-weapon-class numbering, calculate/publish split, dual-mode wall/operator screen). **Pattern catalogue for porting to other disciplines.**

### Other Documentation
See [Documentation/README.md](Documentation/README.md) for complete documentation index.

---

**Documentation Version:** 2025-11-06 (Production Deployment)
**Umbraco Version:** 16.2
**Build Status:** ✅ Compiles (0 errors)
**Deployment Status:** ✅ Production deployment successful
**Last Updated:** Added production deployment guide and resolved ModelsBuilder issues
