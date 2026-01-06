# Competition.cshtml Refactoring Guide

## Summary of Changes

Competition.cshtml has been split into manageable files:
- **JavaScript**: Extracted to `/wwwroot/js/competition-registration.js`
- **Modals**: Extracted to 4 partial views in `/Views/Partials/`

## Files Created ✅

1. ✅ `wwwroot/js/competition-registration.js` (1,500+ lines)
2. ✅ `Views/Partials/CompetitionRegistrationModal.cshtml` (271 lines)
3. ✅ `Views/Partials/CompetitionPreferenceModal.cshtml` (39 lines)
4. ✅ `Views/Partials/CompetitionUpdateConfirmModal.cshtml` (30 lines)
5. ✅ `Views/Partials/CompetitionReplaceConfirmModal.cshtml` (35 lines)

## Changes Required in Competition.cshtml

### Step 1: Replace Registration Modal (Lines 493-762)

**Find** (starts at line 493):
```html
<!-- Registration Modal -->
<div class="modal fade" id="registrationModal"...
...
</div>
```

**Replace with**:
```cshtml
@* Registration Modal *@
@await Html.PartialAsync("CompetitionRegistrationModal", new ViewDataDictionary(ViewData)
{
    { "CompetitionId", Model.Id },
    { "CompetitionName", competitionName },
    { "AClasses", aClasses },
    { "BClasses", bClasses },
    { "CClasses", cClasses },
    { "CRegularClasses", cRegularClasses },
    { "CVetClasses", cVetClasses },
    { "CJuniorClasses", cJuniorClasses },
    { "CDamClasses", cDamClasses },
    { "AllowDualCClassRegistration", allowDualCClassRegistration }
})
```

### Step 2: Replace Preference Modal (Lines 764-801)

**Find** (starts at line 764):
```html
<!-- Start Preference and Notes Modal -->
<div class="modal fade" id="preferenceModal"...
...
</div>
```

**Replace with**:
```cshtml
@* Preference Modal *@
@await Html.PartialAsync("CompetitionPreferenceModal")
```

### Step 3: Replace Update Confirmation Modal (Lines 803-831)

**Find** (starts at line 803):
```html
<!-- Update Registration Confirmation Modal -->
<div class="modal fade" id="updateRegistrationConfirmModal"...
...
</div>
```

**Replace with**:
```cshtml
@* Update Confirmation Modal *@
@await Html.PartialAsync("CompetitionUpdateConfirmModal")
```

### Step 4: Replace Replace Confirmation Modal (Lines 833-866)

**Find** (starts at line 833):
```html
<!-- Replace Registration Confirmation Modal -->
<div class="modal fade" id="replaceRegistrationConfirmModal"...
...
</div>
```

**Replace with**:
```cshtml
@* Replace Confirmation Modal *@
@await Html.PartialAsync("CompetitionReplaceConfirmModal")
```

### Step 5: Replace JavaScript Section (Lines 868-2393)

**Find** (starts at line 868):
```html
<script>
// Global competition ID
const COMPETITION_ID = @Model.Id;
...
[entire 1,500+ line JavaScript block]
...
</script>
```

**Replace with**:
```cshtml
@* JavaScript Configuration and Script Reference *@
<script>
// Configuration object for competition-registration.js
window.CompetitionConfig = {
    competitionId: @Model.Id,
    allowDualCClassRegistration: @(allowDualCClassRegistration.ToString().ToLower())
};

// Show success/error messages from server
@if (TempData["Success"] != null)
{
    <text>
    showNotification('@TempData["Success"]', 'success');
    </text>
}

@if (TempData["Error"] != null)
{
    <text>
    showNotification('@TempData["Error"]', 'error');
    </text>
}

@if (TempData["DebugInfo"] != null)
{
    <text>
    console.log('Registration Debug Info: @TempData["DebugInfo"]');
    showNotification('DEBUG: @TempData["DebugInfo"]', 'info');
    </text>
}
</script>

@* Load external JavaScript file *@
<script src="/js/competition-registration.js"></script>
```

## Expected Results

After these changes, Competition.cshtml will be reduced from **2,393 lines** to approximately **700 lines** (70% reduction).

### File Size Comparison:

| File | Before | After |
|------|--------|-------|
| Competition.cshtml | 2,393 lines | ~700 lines |
| competition-registration.js | 0 lines | 1,500 lines (new, cacheable) |
| Modal partials (4 files) | 0 lines | ~375 lines (new, reusable) |

### Benefits:

1. **Maintainability**: Each component is independently editable
2. **Performance**: JavaScript file is cacheable and can be minified
3. **Reusability**: Modal partials can be used in other views
4. **Readability**: Main view is much more manageable
5. **Project Standard**: Establishes pattern for other large views (UserProfile.cshtml, etc.)

## Testing Checklist

After making changes, test:

- [ ] Registration modal opens correctly
- [ ] Class selection and validation works
- [ ] Preference modal opens and saves settings
- [ ] Duplicate registration detection shows badges
- [ ] Update and replace confirmation dialogs work
- [ ] Form submission completes successfully
- [ ] Admin registration on behalf of members works
- [ ] Payment prompt appears after registration
- [ ] Console has no JavaScript errors

## Rollback Plan

If issues occur, the original Competition.cshtml can be restored from git:
```bash
git checkout Views/Competition.cshtml
```

All new files can remain (they won't interfere with the old version).

## Next Steps

After testing Competition.cshtml refactoring, consider applying the same pattern to:
- `Views/UserProfile.cshtml` (~1,000 lines of JS → `dashboard.js`)
- `Views/CompetitionManagement.cshtml` → `competition-admin.js`

---

**Documentation Version:** 2025-11-12
**Refactoring Type:** JavaScript extraction + Modal componentization
**Status:** Ready for implementation
