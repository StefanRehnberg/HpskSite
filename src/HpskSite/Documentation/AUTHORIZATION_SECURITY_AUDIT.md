# Authorization Security Audit & Fixes (2025-11-02)

This document details the comprehensive security audit and authorization fixes implemented on November 2, 2025.

## Overview

Critical security vulnerabilities were identified and fixed across multiple controllers. The audit revealed flawed authorization patterns that allowed unauthorized access to club and competition management functions.

**Audit Date:** 2025-11-02
**Scope:** 6 areas across 5 controllers
**Result:** 17 authorization checks centralized, multiple security vulnerabilities fixed

---

## Three-Tier Authorization Pattern

Most endpoints now follow a standardized three-tier authorization pattern:

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

**Key Principle:** Access granted if user has ANY applicable role (Site Admin OR Competition Manager OR Club Admin)

---

## Security Vulnerabilities Fixed

### 1. ClubController.cs (7 Methods Fixed)

**Vulnerability:** Any logged-in user could modify ANY club's data (events, news, settings)

**Root Cause:** No authorization checks before modification operations

**Methods Fixed:**
1. `CreateClubEvent` - Create new club events
2. `EditClubEvent` - Update existing club events
3. `DeleteClubEvent` - Remove club events
4. `CreateClubNews` - Create club news items
5. `EditClubNews` - Update club news items
6. `DeleteClubNews` - Remove club news items
7. `UpdateClubInfo` - Update club contact information

**Fix Applied:**
Added `IsClubAdminForClub(clubId)` authorization check to all modification endpoints:

```csharp
// Check authorization - user must be club admin for this club OR site admin
bool isAuthorized = await _authorizationService.IsClubAdminForClub(clubId);
if (!isAuthorized)
{
    return Json(new { success = false, message = "Du har inte behörighet att göra ändringar för denna klubb." });
}
```

**Impact:**
- ✅ Club admins can only modify their assigned clubs
- ✅ Site admins can modify any club
- ✅ Regular users cannot modify any club data

---

### 2. CompetitionController.cs (5 Locations Fixed)

**Vulnerability:** Flawed adminPage fallback pattern that checked if page exists, not if user has access

**Flawed Pattern (BEFORE):**
```csharp
var adminPage = _contentService.GetRootContent()
    .FirstOrDefault(c => c.ContentType.Alias == "adminPage");

if (adminPage == null)
{
    return Json(new { success = false, message = "Access denied" });
}
```

**Problem:** This checked if admin page *exists*, not if *user has admin access*. Any logged-in user could pass this check.

**Fix Applied:**
Replaced with proper `IsCurrentUserAdminAsync()` checks:

```csharp
bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
if (!isSiteAdmin)
{
    return Json(new { success = false, message = "Access denied" });
}
```

**Affected Methods:**
1. `RegisterForCompetition` - Competition registration
2. `UnregisterFromCompetition` - Remove registration
3. `GetCompetitionRegistrations` - View registrations (also added club admin support)
4. Multiple registration workflow methods

**Impact:**
- ✅ Only actual site admins can perform admin-level registration operations
- ✅ Flawed existence check eliminated
- ✅ Proper role-based access control enforced

---

### 3. GetCompetitionRegistrations Authorization (CompetitionController.cs)

**Vulnerability:** Club admins couldn't view registrations for their own club's competitions

**Original Authorization:** Only Site Admin OR Competition Manager

**Fix Applied:**
Added club admin check alongside existing checks:

```csharp
bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();
bool isCompetitionManager = await _authorizationService.IsCompetitionManager(competitionId);

// Check if user is club admin for this competition's club
bool isClubAdmin = false;
var competitionClubId = competition.Value<int>("clubId");
if (competitionClubId > 0)
{
    isClubAdmin = await _authorizationService.IsClubAdminForClub(competitionClubId);
}

if (!isSiteAdmin && !isCompetitionManager && !isClubAdmin)
{
    return Json(new { success = false, message = "Du har inte behörighet att se anmälningar för denna tävling." });
}
```

**Impact:**
- ✅ Club admins can now view registrations for their club's competitions
- ✅ Site admins maintain full access
- ✅ Competition managers maintain access
- ✅ Regular users still blocked

---

### 4. GetCompetitionsList Authorization (CompetitionAdminController.cs)

**Vulnerability:** Only site admins could access, blocking club admins from viewing competitions in ClubAdminPanel

**Original Authorization:** Site admins only

**Fix Applied:**
Added club admin support with server-side filtering:

```csharp
bool isSiteAdmin = await _authorizationService.IsCurrentUserAdminAsync();

List<int> managedClubIds = new List<int>();
if (!isSiteAdmin)
{
    // Check if user is club admin for any club
    managedClubIds = await _authorizationService.GetManagedClubIds();
    if (managedClubIds.Count == 0)
    {
        return Json(new { success = false, message = "Access denied" });
    }
}

// Filter competitions based on role
if (!isSiteAdmin && managedClubIds.Count > 0)
{
    // Club admins only see their clubs' competitions
    competitions = competitions.Where(c => {
        var clubId = c.GetValue<int>("clubId");
        return managedClubIds.Contains(clubId);
    }).ToList();
}
```

**Behavior:**
- **Site admins:** See all competitions
- **Club admins:** See only their managed clubs' competitions (server-side filtered)
- **Regular users:** Access denied

**Impact:**
- ✅ Club admins can now access competition list in ClubAdminPanel
- ✅ Server-side filtering ensures data security
- ✅ No client-side filtering required

---

### 5. View Authorization (Competition.cshtml & CompetitionManagement.cshtml)

**Vulnerability:** Club admins saw "Manage Competition" button on ALL competitions, regardless of ownership

**Original Check:** Only checked if user was site admin or competition manager

**Fix Applied (Competition.cshtml):**
```csharp
bool canManage = false;

if (isAdmin)
{
    canManage = true;
}
else if (Model.Value<int>("clubId") > 0)
{
    var clubId = Model.Value<int>("clubId");
    // Check if user is club admin for this competition's club
    var managedClubIds = await authorizationService.GetManagedClubIds();
    canManage = managedClubIds.Contains(clubId);
}

// Show button only if canManage is true
@if (canManage)
{
    <a href="/competitionmanagement?competitionId=@Model.Id" class="btn btn-primary">
        <i class="bi bi-gear"></i> Hantera Tävling
    </a>
}
```

**Fix Applied (CompetitionManagement.cshtml):**
- Added same club admin check using competition's `clubId` property
- Button only visible if user is Site Admin, Competition Manager, OR Club Admin for that competition's club

**Impact:**
- ✅ Club admins only see manage button for their clubs' competitions
- ✅ Site admins see button for all competitions
- ✅ Competition managers see button for assigned competitions
- ✅ Regular users don't see button

---

### 6. Duplicate Code Elimination

**Issue:** Duplicate `IsCurrentUserAdminAsync()` methods existed in:
- `TrainingController.cs`
- `CompetitionAdminController.cs`

**Fix Applied:**
- Removed duplicate methods (2 instances)
- All controllers now use centralized `AdminAuthorizationService.IsCurrentUserAdminAsync()`
- **Total calls centralized:** 17 authorization checks now use the shared service

**Impact:**
- ✅ Single source of truth for admin checks
- ✅ Easier maintenance
- ✅ Consistent authorization logic
- ✅ Reduced code duplication

---

## Additional UI Fixes

### Admin Panel Button Removal (CompetitionManagement.cshtml)

**Issue:** "Admin" button appeared in competition management navigation, causing confusion (users already in admin context)

**Fix:** Removed admin panel button from CompetitionManagement page

### Master.cshtml Admin Menu Visibility

**Issue:** Admin menu item visible to all logged-in users

**Fix:** Added proper authorization check to only show menu item to site admins

```csharp
@if (isLoggedIn && await authorizationService.IsCurrentUserAdminAsync())
{
    <li class="nav-item">
        <a class="nav-link" href="/admin">
            <i class="bi bi-gear-fill"></i> Administration
        </a>
    </li>
}
```

### AdminPage.cshtml Authorization

**Issue:** Missing authorization check on admin page itself

**Fix:** Added authorization check at page load to redirect non-admins

---

## Security Impact Summary

### Before Audit
❌ Any logged-in user could modify any club's data
❌ Flawed existence checks instead of authorization checks
❌ Club admins couldn't access their own competitions
❌ Club admins saw manage buttons for competitions they don't manage
❌ Duplicate authorization code in multiple controllers
❌ Admin UI elements visible to non-admins

### After Audit
✅ Proper role-based access control across all endpoints
✅ Club admins can only access their assigned clubs
✅ Three-tier authorization pattern (Site Admin > Competition Manager > Club Admin)
✅ Server-side filtering for data security
✅ Centralized authorization service (17 calls)
✅ Consistent authorization checks across entire codebase
✅ Admin UI elements properly hidden from regular users

---

## Testing Checklist

**As Site Admin:**
- [x] Can modify any club's data
- [x] Can view all competitions in admin panel
- [x] Can view registrations for any competition
- [x] Can see manage button on all competitions

**As Club Admin:**
- [x] Can only modify assigned club's data
- [x] Cannot modify other clubs' data
- [x] Can view only assigned club's competitions
- [x] Can view registrations for assigned club's competitions
- [x] Can see manage button only on assigned club's competitions
- [x] Cannot access site admin functions

**As Regular User:**
- [x] Cannot modify any club data
- [x] Cannot access admin panels
- [x] Cannot view competition registrations
- [x] Cannot see manage competition buttons
- [x] Cannot access admin menu items

---

## Files Modified

1. `Controllers/ClubController.cs` - Added authorization to 7 methods
2. `Controllers/CompetitionController.cs` - Fixed flawed adminPage pattern (5 locations), added club admin support
3. `Controllers/CompetitionAdminController.cs` - Added club admin support to GetCompetitionsList, removed duplicate code
4. `Controllers/TrainingController.cs` - Removed duplicate authorization method
5. `Views/Competition.cshtml` - Added club admin check for manage button
6. `Views/CompetitionManagement.cshtml` - Added club admin check, removed admin panel button
7. `Views/Master.cshtml` - Fixed admin menu visibility
8. `Views/AdminPage.cshtml` - Added authorization check

---

## Centralized Authorization Service

All authorization now flows through `Services/AdminAuthorizationService.cs`:

**Key Methods:**
- `IsCurrentUserAdminAsync()` - Site admin check (17 calls)
- `IsClubAdminForClub(clubId)` - Club-specific admin check (15+ calls)
- `GetManagedClubIds()` - Get clubs user can administer (5+ calls)
- `IsCompetitionManager(competitionId)` - Check if user manages specific competition (3 calls)
- `EnsureClubAdminGroup(clubId, clubName)` - Create club admin groups

**Registered:** Singleton in `AdminServicesComposer.cs`

---

## Recommendations

1. ✅ **Always use AdminAuthorizationService** - Don't create duplicate authorization logic
2. ✅ **Use three-tier pattern** - Check Site Admin, then specific role, then Club Admin
3. ✅ **Server-side filtering** - Never trust client-side authorization
4. ✅ **Test with multiple roles** - Verify each role has appropriate access
5. ✅ **Consistent error messages** - Use Swedish "Du har inte behörighet..." messages
6. ⚠️ **Regular security audits** - Review authorization logic when adding new endpoints

---

**Audit Completed:** 2025-11-02
**Status:** ✅ All vulnerabilities fixed
**Build Status:** ✅ 0 errors
**Testing:** ✅ Verified with multiple user roles
