# Controller Architecture

**Last Updated:** 2025-11-24

## Overview

The admin functionality was refactored from a monolithic AdminController into specialized controllers following Single Responsibility Principle (2025-10-28).

## AdminAuthorizationService

**File:** `Services/AdminAuthorizationService.cs`
**Registration:** Singleton in `AdminServicesComposer.cs`

### Key Methods
- `IsCurrentUserAdminAsync()` - Site admin check
- `IsClubAdminForClub(clubId)` - Club-specific admin check
- `GetManagedClubIds()` - Get clubs user can administer
- `IsCompetitionManager(competitionId)` - Check if user manages specific competition
- `EnsureClubAdminGroup(clubId, clubName)` - Create club admin groups

### Authorization Pattern (2025-11-02)

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

**See Also:** [AUTHORIZATION_SECURITY_AUDIT.md](AUTHORIZATION_SECURITY_AUDIT.md) for complete security fixes

## Specialized Controllers

### MemberAdminController

**File:** `Controllers/MemberAdminController.cs`
**Endpoints:** 8 endpoints for member management

**GET Endpoints:**
- `GetMembers` - Retrieve all members with filtering
- `GetMember(memberId)` - Get single member details
- `GetMemberGroups` - List all member groups
- `GetPendingApprovals` - Members awaiting approval

**POST Endpoints:**
- `SaveMember` - Create/update member
- `DeleteMember` - Delete member
- `SeedRandomMembers` - Test data generation
- `FixUsersWithoutGroups` - Maintenance utility

**Used By:**
- `Views/Partials/UserManagement.cshtml`

### ClubAdminController

**File:** `Controllers/ClubAdminController.cs`
**Endpoints:** 19 endpoints for club management

**CRUD Operations:**
- `GetClubs` - List all clubs
- `GetClub(clubId)` - Get single club
- `SaveClub` - Create/update club
- `DeleteClub` - Delete club
- `CheckClubCanBeDeleted` - Validation check

**Member Management:**
- `GetClubMembers` - All club members
- `GetClubMembersForClubAdmin` - Filtered for club admin
- `GetPendingApprovalsCount` - Count pending members

**Public Endpoints:**
- `GetClubsForRegistration` - Registration dropdown
- `GetClubsPublic` - Public club directory

**Admin Assignment:**
- `AssignClubAdmin` - Grant club admin rights
- `RemoveClubAdmin` - Revoke club admin rights
- `GetClubAdmins` - List club administrators
- `GetAvailableMembersForClubAdmin` - Eligible members

**Validation & Migration:**
- `CleanupInvalidClubReferences` - Data cleanup
- `DebugClubs` - Diagnostic tool
- `InitializeClubs` - Setup utility
- `PreviewClubMigration` - Migration preview
- `MigrateClubReferences` - Execute migration

**Used By:**
- `Views/Partials/ClubManagement.cshtml`
- `Views/ClubsPage.cshtml`
- `Views/ClubAdmin.cshtml`
- `Views/Partials/Register.cshtml`

### RegistrationAdminController

**File:** `Controllers/RegistrationAdminController.cs`
**Endpoints:** 5 endpoints for competition registration management

**GET Endpoints:**
- `GetCompetitionRegistrations(competitionId)` - List registrations
- `GetActiveCompetitions` - Competitions accepting registrations

**POST Endpoints:**
- `UpdateCompetitionRegistration` - Modify registration
- `DeleteCompetitionRegistration` - Remove registration
- `ExportCompetitionRegistrations` - CSV export

**Used By:**
- `Views/Partials/RegistrationManagement.cshtml`
- `Views/Partials/CompetitionRegistrationManagement.cshtml`
- `Views/Partials/CompetitionExportManagement.cshtml`
- `Views/CompetitionManagement.cshtml`

### CompetitionController

**File:** `Controllers/CompetitionController.cs`
**Type:** Mixed public + admin endpoints

**Key Endpoints:**
- `GetCompetitionRegistrations` - View registrations (Site Admin OR Competition Manager OR Club Admin)
- `RegisterForCompetition` - Member registration
- `UnregisterFromCompetition` - Cancel registration

**Authorization:**
- Public: Registration endpoints
- Admin: View registrations (requires appropriate role)

## Benefits of Refactoring

1. **Clear Separation of Concerns** - Each controller has single responsibility
2. **Easier Maintenance** - Changes isolated to relevant controller
3. **Better Testing** - Smaller, focused units
4. **Reduced Duplication** - Shared AuthorizationService
5. **Improved Code Organization** - Logical grouping by domain

## Common Patterns

### Dependency Injection

```csharp
public class MyController : SurfaceController
{
    private readonly AdminAuthorizationService _authorizationService;
    private readonly IMemberService _memberService;
    private readonly IContentService _contentService;

    public MyController(
        IUmbracoContextAccessor umbracoContextAccessor,
        IUmbracoDatabaseFactory databaseFactory,
        ServiceContext services,
        AppCaches appCaches,
        IProfilingLogger profilingLogger,
        IPublishedUrlProvider publishedUrlProvider,
        AdminAuthorizationService authorizationService,
        IMemberService memberService,
        IContentService contentService)
        : base(umbracoContextAccessor, databaseFactory, services, appCaches, profilingLogger, publishedUrlProvider)
    {
        _authorizationService = authorizationService;
        _memberService = memberService;
        _contentService = contentService;
    }
}
```

### Authorization Check Pattern

```csharp
[HttpPost]
[ValidateAntiForgeryToken]
public async Task<IActionResult> MyAdminEndpoint(int id)
{
    // Check authorization
    if (!await _authorizationService.IsCurrentUserAdminAsync())
    {
        return Json(new { success = false, message = "Access denied" });
    }

    // Proceed with operation
    // ...
}
```

### Content Service Operations

```csharp
// Create content
var content = _contentService.Create(name, parentId, documentTypeAlias);
content.SetValue("propertyAlias", value);
_contentService.SaveAndPublish(content);

// Delete content (unpublish first)
_contentService.Unpublish(content);
_contentService.Delete(content);
```

## Related Documentation

- [AUTHORIZATION_SECURITY_AUDIT.md](AUTHORIZATION_SECURITY_AUDIT.md) - Security audit results
- [CLUB_SYSTEM_MIGRATIONS.md](CLUB_SYSTEM_MIGRATIONS.md) - Club system refactoring
- [LOGIN_REGISTRATION_SYSTEM.md](LOGIN_REGISTRATION_SYSTEM.md) - Authentication system
