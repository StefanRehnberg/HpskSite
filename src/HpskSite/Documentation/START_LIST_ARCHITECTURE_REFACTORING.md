# Start List Architecture Refactoring - TO DO

**Status:** ✅ PHASE 1 COMPLETE - Backend refactoring done
**Priority:** HIGH - Start lists should now work
**Created:** 2025-11-24
**Updated:** 2025-11-24
**Issue:** SqlTransaction error - RESOLVED by removing hub pattern

## Problem Summary

### Current Issues
1. **Missing `await`** - Fixed on line 1275, but transaction error persists
2. **Wrong Architecture** - Using hub pattern instead of single page pattern
3. **Transaction Error** - "SqlTransaction has completed; it is no longer usable"
4. **Corrupted Data** - Old start lists contain "System.Threading.Tasks.Task`1[System.String]" instead of HTML

### Root Cause
The start list system uses a **three-level architecture** (Competition → Hub → Multiple Lists) when it should use a **two-level architecture** (Competition → Single Start List) to match the Results pattern.

## Current vs Target Architecture

### Current (WRONG)
```
Competition
└── Startlistor (competitionStartListsHub)
    ├── Startlista 2024-01-15 09:00 (precisionStartList)
    ├── Startlista 2024-01-15 14:00 (precisionStartList)
    └── Startlista 2024-01-16 10:00 (precisionStartList)
```

**Problems:**
- Unnecessary hub adds complexity
- Multiple start lists confuse users (which one is official?)
- Transaction errors when creating/deleting hub content
- Code must traverse two levels

### Target (CORRECT - Match Results Pattern)
```
Competition
├── Resultat (competitionResult) ← Single page, already working
└── Startlista (precisionStartList) ← Single page, needs refactoring
```

**Benefits:**
- Matches Results pattern exactly
- ONE authoritative start list
- Simpler code (no hub logic)
- No transaction issues with hub
- Clear UX

## Refactoring Plan

### Phase 1: Backend Code Changes

#### File: `UmbracoStartListRepository.cs`

**1. DELETE Method:** `GetOrCreateStartListsHub()` (lines 347-408)
- No longer needed - start list is direct child

**2. MODIFY Method:** `GetStartListsForCompetition()` (lines 165-225)

**Current Logic:**
```csharp
// Get hub
var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
var hub = children.FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");

// Get all start lists under hub
var startLists = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
    .Where(c => c.ContentType.Alias == "precisionStartList")
    .ToList();
```

**New Logic:**
```csharp
// Get THE ONE start list directly from competition
var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
var startList = children.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

if (startList == null) return new List<IContent>();
return new List<IContent> { startList }; // Single item
```

#### File: `PrecisionStartListController.cs`

**1. MODIFY Method:** `GenerateStartList()` (lines 1286-1310)

**Current Logic:**
```csharp
// Find or create start lists hub
var startListsHub = _repository.GetOrCreateStartListsHub(competition);
if (startListsHub == null) { /* error */ }

// Create content node for the start list
var startListName = $"Startlista {DateTime.Now:yyyy-MM-dd HH:mm}";
var startList = _contentService.Create(startListName, startListsHub.Id, "precisionStartList");
```

**New Logic:**
```csharp
// Check if start list already exists (direct child of competition)
var existingStartList = _contentService.GetPagedChildren(competition.Id, 0, 20, out _)
    .FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

IContent startList;
if (existingStartList != null)
{
    // UPDATE existing start list
    startList = existingStartList;
    startList.Name = "Startlista";
    _logger.LogInformation("Updating existing start list {StartListId}", startList.Id);
}
else
{
    // CREATE new start list as direct child of competition
    startList = _contentService.Create("Startlista", competition.Id, "precisionStartList");
    _logger.LogInformation("Creating new start list for competition {CompetitionId}", competition.Id);
}

// Set properties (same as before)
startList.SetValue("competitionId", request.CompetitionId);
startList.SetValue("teamFormat", request.TeamFormat);
startList.SetValue("generatedDate", DateTime.Now);
startList.SetValue("generatedBy", generatedBy);
startList.SetValue("notes", request.Notes ?? "");
startList.SetValue("isOfficialStartList", false);
startList.SetValue("configurationData", JsonConvert.SerializeObject(startListData));
startList.SetValue("startListContent", htmlContent);

// Save and publish (same as before)
_contentService.Save(startList);
_contentService.Publish(startList, new[] { "*" }, -1);
```

**2. MODIFY Method:** `GetOfficialStartList()` (lines 359-395)

**Current Logic:**
```csharp
var allStartLists = _repository.GetStartListsForCompetition(competitionId);
var validStartLists = allStartLists.Where(sl => /* filter corrupted */).ToList();
var currentStartList = validStartLists.OrderByDescending(sl => sl.GetValue<DateTime>("generatedDate")).First();
```

**New Logic:**
```csharp
// Get THE ONE start list directly
var competition = _contentService.GetById(competitionId);
if (competition == null)
{
    return Json(new { Success = false, Message = "Tävling hittades inte." });
}

var children = _contentService.GetPagedChildren(competition.Id, 0, 20, out _);
var startList = children.FirstOrDefault(c => c.ContentType.Alias == "precisionStartList");

if (startList == null)
{
    return Json(new { Success = false, Message = "Ingen startlista finns för denna tävling." });
}

// Return UI-friendly format (same as before)
var response = new
{
    Success = true,
    StartList = new
    {
        Id = startList.Id,
        GeneratedDate = startList.GetValue<DateTime>("generatedDate"),
        TeamFormatDisplay = _renderer.GetTeamFormatDisplay(startList.GetValue<string>("teamFormat") ?? ""),
        TeamCount = _repository.GetTeamCountFromContent(startList),
        TotalShooters = _repository.GetTotalShootersFromContent(startList),
        IsOfficial = startList.GetValue<bool>("isOfficialStartList"),
        Url = GetStartListDisplayUrl(startList, competitionId)
    }
};

return Json(response);
```

**3. DELETE/SIMPLIFY Method:** `GetStartLists()` (lines 313-347)
- May no longer be needed (GetOfficialStartList replaces it)
- Or update to return single item

**4. UPDATE Finals Methods** (if applicable)
- Review finals start list generation
- Ensure works with direct child pattern

### Phase 2: Frontend Code (Already Mostly Done!)

The UI refactoring is already complete:
- ✅ Shows ONE start list card (not table)
- ✅ Status badge (PRELIMINÄR/OFFICIELL)
- ✅ Publish/Unpublish buttons
- ✅ Iframe display

**Only change needed:**
- Update "Generate New Start List" button text to "Update Start List" when one exists

### Phase 3: Document Types (Umbraco Backoffice)

**1. Update Competition Document Type**
- Settings → Document Types → `competition`
- Structure tab → Allowed Child Content Types
- **ADD:** `precisionStartList` (if not already allowed)
- **REMOVE:** `competitionStartListsHub`

**2. Delete Hub Document Type**
- Settings → Document Types
- Find `competitionStartListsHub`
- **DELETE** (after code refactoring and migration)

### Phase 4: Data Migration (Existing Competitions)

**Goal:** Move existing start lists from hub to direct child

**Migration Script:**
```csharp
public void MigrateStartListsToDirectChildren()
{
    var competitions = _contentService.GetByContentType("competition");

    foreach (var competition in competitions)
    {
        try
        {
            // Find hub
            var hub = _contentService.GetPagedChildren(competition.Id, 0, 100, out _)
                .FirstOrDefault(c => c.ContentType.Alias == "competitionStartListsHub");

            if (hub == null) continue;

            // Find official start list (or most recent)
            var startLists = _contentService.GetPagedChildren(hub.Id, 0, int.MaxValue, out _)
                .Where(c => c.ContentType.Alias == "precisionStartList")
                .ToList();

            if (!startLists.Any()) continue;

            // Get official start list (or most recent non-corrupted)
            var targetStartList = startLists
                .Where(sl => {
                    var content = sl.GetValue<string>("startListContent");
                    return !string.IsNullOrEmpty(content) && !content.Contains("System.Threading.Tasks.Task");
                })
                .FirstOrDefault(sl => sl.GetValue<bool>("isOfficialStartList"))
                ?? startLists
                    .OrderByDescending(sl => sl.GetValue<DateTime>("generatedDate"))
                    .FirstOrDefault();

            if (targetStartList == null) continue;

            // Move start list to competition (change parent)
            _contentService.Move(targetStartList, competition.Id);
            targetStartList.Name = "Startlista";
            _contentService.Save(targetStartList);
            _contentService.Publish(targetStartList, new[] { "*" }, -1);

            _logger.LogInformation("Migrated start list {StartListId} for competition {CompetitionId}",
                targetStartList.Id, competition.Id);

            // Delete hub and remaining start lists
            _contentService.Delete(hub);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error migrating start list for competition {CompetitionId}", competition.Id);
        }
    }
}
```

**Run migration:**
- Create controller endpoint: `/umbraco/surface/PrecisionStartList/MigrateStartLists`
- Test in dev environment first
- Backup database before production migration

## Implementation Order

1. ✅ **Backend refactoring** (Phase 1) - DONE 2025-11-24
   - Modified GetStartListsForCompetition() for direct child lookup
   - Deleted GetOrCreateStartListsHub() method
   - Modified GenerateStartList() to create/update single start list
   - Modified UnpublishAllStartListsForCompetition() for new pattern
   - Modified GenerateFinalsStartList() for new pattern
   - Modified GetFinalsStartList() for new pattern
   - Backward compatibility maintained for existing hub-based lists
2. ⏳ **Test locally** - verify generation works
3. ⏳ **Frontend polish** (Phase 2) - button text updates
4. ⏳ **Document type changes** (Phase 3) - Umbraco backoffice
5. ⏳ **Data migration** (Phase 4) - move existing start lists
6. ⏳ **Production deployment** - deploy with migration

## Files to Modify

### Backend
- `CompetitionTypes/Precision/Controllers/UmbracoStartListRepository.cs`
  - DELETE: GetOrCreateStartListsHub()
  - MODIFY: GetStartListsForCompetition()

- `CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs`
  - MODIFY: GenerateStartList() (lines 1286-1310)
  - MODIFY: GetOfficialStartList() (lines 359-395)
  - SIMPLIFY: GetStartLists() (lines 313-347)

### Frontend
- `Views/Partials/CompetitionStartListManagement.cshtml`
  - Update "Generate New Start List" button logic

### Migration
- Create: `Migrations/StartListArchitectureMigration.cs`
  - One-time migration script

## Testing Checklist

After implementation:
- [ ] Generate new start list for competition (no existing list)
- [ ] Update existing start list (regenerate)
- [ ] Mark start list as official
- [ ] Unmark start list (back to preliminary)
- [ ] View start list page (renders correctly)
- [ ] Late registration → regenerate → verify shooter added
- [ ] Results entry → verify start list data accessible
- [ ] No SqlTransaction errors
- [ ] No corrupted HTML content

## Estimated Effort

- **Phase 1 (Backend):** 2-3 hours
- **Phase 2 (Frontend):** 30 minutes
- **Phase 3 (Document Types):** 30 minutes
- **Phase 4 (Migration):** 2 hours
- **Testing:** 2 hours

**Total:** ~7-8 hours

## Benefits

1. **Consistency** - Matches Results pattern exactly
2. **Simplicity** - No hub logic, fewer document types
3. **Performance** - One less content query
4. **Reliability** - No transaction errors from hub operations
5. **UX** - Clear "one authoritative start list" model

## Known Issues to Fix

1. **SqlTransaction Error** - Will be resolved by removing hub creation
2. **Corrupted HTML** - Fixed by adding `await` on line 1275
3. **Multiple Start Lists Confusion** - Resolved by single start list model

## References

- Results system pattern: `Controllers/CompetitionResultsController.cs` (lines 1306-1408)
- Current start list code: `CompetitionTypes/Precision/Controllers/`
- UI refactoring: `Views/Partials/CompetitionStartListManagement.cshtml`

---

**Next Steps:** Implement Phase 1 backend refactoring, test locally, then proceed with phases 2-4.
