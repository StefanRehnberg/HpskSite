# Club System Migrations

This document details the migrations that transitioned the club system from Member Type storage to Document Type storage, including all related data integrity fixes.

## Overview

The club system underwent two major migrations in October 2025:
1. **Club Lookup Service Migration (2025-10-30)** - Centralized club lookups via ClubService
2. **Competition Registration Club References (2025-10-31)** - Migrated from string to numeric clubId

---

## 1. Club Lookup Service Migration ✅ COMPLETE (2025-10-30)

### Problem

After clubs were migrated from Member Type to Document Type, old code still used `IMemberService.GetById(clubId)` to look up clubs. This failed silently, returning null or wrong data, causing display errors like "Club 1098" or "Ingen klubb" instead of actual club names.

### Solution

Created **ClubService** (`Services/ClubService.cs`) as a centralized service for all club lookups.

**Service Registration:**
- Registered as singleton in `ClubServiceComposer.cs`
- Uses `IContentService` to access club Document Type nodes

**Key Methods:**
```csharp
GetClubNameById(int clubId)  // Returns club name or null
GetClubById(int clubId)      // Returns ClubInfo object or null
```

### Correct Pattern

✅ **CORRECT - Use ClubService:**
```csharp
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

❌ **WRONG - Don't use IMemberService for clubs:**
```csharp
var club = _memberService.GetById(clubId);  // Returns null or wrong data!
var clubName = club?.Name;  // Will be null or incorrect
```

### Fixed Files (2025-10-30)

1. `Controllers/TrainingController.cs`
2. `Controllers/CompetitionController.cs`
3. `CompetitionTypes/Precision/Controllers/UmbracoStartListRepository.cs`
4. `CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs`
5. `CompetitionTypes/Precision/Controllers/StartListHtmlRenderer.cs`

### Why This Matters

- Clubs are stored as **Document Type nodes**, not members
- Old code referencing `_memberService.GetById()` fails
- Results in "Club 1098" or "Ingen klubb" display errors
- ClubService ensures consistent, correct club lookups across entire codebase

### Testing

Verify club names display correctly in:
- Training page participant lists
- Competition registration lists
- Start list generation
- Results entry screens
- CSV exports

---

## 2. Competition Registration Club References ✅ COMPLETE (2025-10-31)

### Problem

Competition registrations stored club references as string property `memberClub`, causing display issues and data integrity problems. Values like "Club 1098" or "Ingen klubb" appeared instead of actual club names.

### Solution

Migrated from string-based `memberClub` to numeric `clubId` property with proper ClubService lookups.

**Document Type Change:**
```
competitionRegistration document type:
- REMOVED: memberClub (Textstring)
- ADDED: clubId (Numeric)
```

### Key Benefits

✅ Type safety - prevents invalid club references
✅ Centralized lookups via ClubService
✅ Consistent club name display across all pages
✅ Future-proof data structure
✅ Backward compatibility during migration

### Migration Strategy

1. **Added clubId property** to competitionRegistration document type in Umbraco backoffice
2. **Updated registration creation** - New registrations store numeric clubId
3. **Updated all read operations** - Read clubId with fallback to legacy memberClub
4. **Created migration endpoint** - Batched processing to convert existing data
5. **Ran migration** - Successfully migrated all registrations
6. **Removed legacy property** - Deleted memberClub from document type

### Files Modified

1. **Controllers/CompetitionController.cs**
   - Line 149-154: Parse primaryClubId from member
   - Line 236-239: Store numeric clubId in registration
   - Line 931-960: Read clubId with multi-level fallback logic

2. **Controllers/RegistrationAdminController.cs**
   - Line 30, 41-42: Injected ClubService
   - Line 65-81: GetCompetitionRegistrations uses clubId
   - Line 253-269: ExportCompetitionRegistrations uses clubId

3. **CompetitionTypes/Precision/Controllers/UmbracoStartListRepository.cs**
   - Line 30, 49-50: Injected ClubService
   - Line 65-94: Start list generation uses clubId with fallback

4. **CompetitionTypes/Precision/Controllers/PrecisionResultsController.cs**
   - Line 30, 45, 54: Injected ClubService
   - Line 185-209: Results entry uses clubId with fallback

5. **Controllers/CompetitionAdminController.cs**
   - Line 1236-1332: MigrateRegistrationClubIds endpoint

### Fallback Logic Pattern

Used during migration to support both old and new data formats:

```csharp
// 1. Try new clubId property (preferred)
var clubId = registration.GetValue<int>("clubId");
if (clubId > 0)
{
    clubName = _clubService.GetClubNameById(clubId) ?? $"Club {clubId}";
}
else
{
    // 2. Fallback to legacy memberClub if numeric
    var memberClubStr = registration.GetValue<string>("memberClub");
    if (!string.IsNullOrEmpty(memberClubStr) && int.TryParse(memberClubStr, out var legacyId))
    {
        clubName = _clubService.GetClubNameById(legacyId) ?? $"Club {legacyId}";
    }
    else if (!string.IsNullOrEmpty(memberClubStr))
    {
        // 3. Use memberClub string directly (old format)
        clubName = memberClubStr;
    }
    else if (memberId > 0)
    {
        // 4. Last resort: get from member's primaryClubId
        var member = _memberService.GetById(memberId);
        var primaryClubIdStr = member?.GetValue<string>("primaryClubId");
        if (!string.IsNullOrEmpty(primaryClubIdStr) && int.TryParse(primaryClubIdStr, out var primaryClubId))
        {
            clubName = _clubService.GetClubNameById(primaryClubId) ?? $"Club {primaryClubId}";
        }
    }
}
```

### Migration Endpoint

```
GET /umbraco/surface/CompetitionAdmin/MigrateRegistrationClubIds
    ?confirm=true&batchSize=50

- Preview mode: confirm=false (default) - Shows migration status
- Migration mode: confirm=true - Processes next batch
- Batched processing: batchSize=50 (default) - Prevents timeout
- Run repeatedly until isComplete=true
```

### Migration Process

1. Added clubId property in Umbraco (Numeric type)
2. Called preview: `/MigrateRegistrationClubIds` → Status shown
3. Called with confirm: `/MigrateRegistrationClubIds?confirm=true&batchSize=50`
4. Repeated until all registrations migrated
5. Verified club names display correctly
6. Deleted memberClub property from document type

### Testing Locations

- `/training-page` → "Deltagare" tab → Klubb column
- `/competitionmanagement?competitionId=X` → "Anmälningar" tab → Klubb column
- Registration admin panels
- CSV exports
- Start list generation
- Results entry

### Result

All club names now display correctly as actual club names (e.g., "Helsingborgs Pistolskytteklubb") instead of "Club 1098" or "Ingen klubb".

---

## Key Takeaways

1. **Always use ClubService** for club lookups - never `IMemberService`
2. **Clubs are Document Type nodes** - stored in content tree, not as members
3. **Numeric clubId** is the standard - no string-based club references
4. **Fallback patterns** allow graceful migration from old to new data formats
5. **Test thoroughly** after migrations - verify display in all UI locations

---

**Last Updated:** 2025-10-31
**Status:** Both migrations complete and tested
