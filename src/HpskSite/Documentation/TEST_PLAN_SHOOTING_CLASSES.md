# Test Plan: Shooting Class Storage System

## Overview

This test plan ensures the shooting class storage and display system works correctly across all scenarios and prevents regressions from future changes.

**Feature:** Shooting Class Storage for Competitions
**Components:** CompetitionAdminController, PrecisionCompetitionEditService, CompetitionsHub.cshtml, CompetitionSeries.cshtml
**Last Updated:** 2025-10-30

---

## Test Environment Setup

### Prerequisites

1. **Database:**
   - Fresh database OR
   - Database with mix of competitions (some with CSV format, some with JSON format)

2. **Test Data:**
   - At least 3-5 test competitions
   - Mix of shooting classes (single class, multiple classes, all classes, no classes)

3. **User Account:**
   - Administrator account for full access
   - Regular member account for read-only testing

4. **Browsers:**
   - Chrome (latest)
   - Firefox (latest)
   - Edge (latest)

---

## Test Cases

### TC-001: Create Competition with Single Shooting Class

**Priority:** P0 (Critical)
**Type:** Functional

**Preconditions:**
- Logged in as administrator
- On Admin → Competitions tab

**Steps:**
1. Click "Skapa ny tävling"
2. Fill in required fields:
   - Name: "Test Competition - Single Class"
   - Date: Future date
   - Competition Type: "Precisionstävling"
3. Check ONLY "C1" shooting class
4. Click "Skapa"

**Expected Results:**
- ✅ Success message displayed
- ✅ Competition appears in competitions list
- ✅ Database contains: `shootingClassIds = ["C1"]`
- ✅ Card view shows: "C1"
- ✅ List view "Klass" column shows: "C1"
- ✅ Series page shows: "C1"

**Test Data:**
```json
{
  "name": "Test Competition - Single Class",
  "shootingClasses": ["C1"],
  "expectedDatabase": "[\"C1\"]",
  "expectedDisplay": "C1"
}
```

---

### TC-002: Create Competition with Multiple Shooting Classes

**Priority:** P0 (Critical)
**Type:** Functional

**Preconditions:**
- Logged in as administrator
- On Admin → Competitions tab

**Steps:**
1. Click "Skapa ny tävling"
2. Fill in required fields:
   - Name: "Test Competition - Multiple Classes"
   - Date: Future date
   - Competition Type: "Precisionstävling"
3. Check multiple shooting classes: C1, C2, A1, B1
4. Click "Skapa"

**Expected Results:**
- ✅ Success message displayed
- ✅ Database contains: `shootingClassIds = ["C1","C2","A1","B1"]`
- ✅ Card view shows: "C1, C2, A1, B1"
- ✅ List view "Klass" column shows: "C1, C2, A1, B1"
- ✅ Series page shows: "C1, C2, A1, B1"

**SQL Verification:**
```sql
SELECT TOP 1 shootingClassIds
FROM umbracoContent
WHERE nodeId IN (
    SELECT id FROM umbracoNode
    WHERE text = 'Test Competition - Multiple Classes'
)
```

---

### TC-003: Create Competition with NO Shooting Classes

**Priority:** P1 (High)
**Type:** Edge Case

**Preconditions:**
- Logged in as administrator
- On Admin → Competitions tab

**Steps:**
1. Click "Skapa ny tävling"
2. Fill in required fields
3. Do NOT check any shooting classes
4. Click "Skapa"

**Expected Results:**
- ✅ Success message displayed
- ✅ Database contains: `shootingClassIds = NULL` OR `shootingClassIds = []`
- ✅ Card view shows: "Inga klasser"
- ✅ List view "Klass" column shows: "Inga klasser"
- ✅ Series page shows: "Inga klasser"

---

### TC-004: Edit Competition - Change Shooting Classes

**Priority:** P0 (Critical)
**Type:** Functional

**Preconditions:**
- Competition exists with shooting classes: C1, C2
- Logged in as administrator

**Steps:**
1. Navigate to competitions list
2. Click edit icon on test competition
3. Uncheck C1, C2
4. Check A1, A2, A3
5. Click "Spara"
6. Refresh competitions list page

**Expected Results:**
- ✅ Success message displayed
- ✅ Database updated to: `["A1","A2","A3"]`
- ✅ Card view shows: "A1, A2, A3" (not "C1, C2")
- ✅ List view shows: "A1, A2, A3" (not "C1, C2")

---

### TC-005: Edit Competition - Remove All Shooting Classes

**Priority:** P1 (High)
**Type:** Edge Case

**Preconditions:**
- Competition exists with shooting classes: C1, C2
- Logged in as administrator

**Steps:**
1. Click edit on competition
2. Uncheck all shooting classes
3. Click "Spara"
4. Refresh page

**Expected Results:**
- ✅ Success message displayed
- ✅ Views show "Inga klasser"

---

### TC-006: Copy Competition - Shooting Classes Preserved

**Priority:** P1 (High)
**Type:** Functional

**Preconditions:**
- Competition exists with shooting classes: C1, C2, A1

**Steps:**
1. Navigate to competitions list
2. Click copy icon on competition
3. Enter new date
4. Click "Kopiera"
5. View copied competition

**Expected Results:**
- ✅ New competition created
- ✅ Database contains: `["C1","C2","A1"]` (same as original)
- ✅ Views show same shooting classes as original

---

### TC-007: Migration Endpoint - Convert CSV to JSON

**Priority:** P0 (Critical)
**Type:** Data Migration

**Preconditions:**
- Database has competitions with CSV format: `"C1,C2,A1"`
- Logged in as administrator

**Steps:**
1. Navigate to `/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat`
2. Wait for response
3. Refresh competitions list
4. Check database

**Expected Results:**
- ✅ Response shows success: `{"success": true, "fixedCount": X}`
- ✅ Database now has JSON: `["C1","C2","A1"]`
- ✅ `errorCount = 0`
- ✅ Views display shooting classes correctly
- ✅ No "Inga klasser" for migrated competitions

**Response Validation:**
```json
{
  "success": true,
  "fixedCount": <number>,
  "alreadyCorrectCount": <number>,
  "errorCount": 0,
  "errors": []
}
```

---

### TC-008: Backward Compatibility - Display CSV Format

**Priority:** P1 (High)
**Type:** Compatibility

**Preconditions:**
- Manually set competition's shootingClassIds to CSV: `"C1,C2,A1"` in database

**Steps:**
1. Refresh competitions list (card view)
2. Switch to list view
3. Navigate to series page

**Expected Results:**
- ✅ Card view shows: "C1, C2, A1"
- ✅ List view shows: "C1, C2, A1"
- ✅ Series page shows: "C1, C2, A1"
- ✅ No JavaScript errors in console
- ✅ No server errors

**SQL Setup:**
```sql
UPDATE umbracoContent
SET shootingClassIds = 'C1,C2,A1'
WHERE nodeId = <test-competition-id>
```

---

### TC-009: Error Handling - Corrupted JSON

**Priority:** P2 (Medium)
**Type:** Error Handling

**Preconditions:**
- Manually set competition's shootingClassIds to invalid JSON: `"[C1,C2"`

**Steps:**
1. Refresh competitions list
2. Check browser console
3. Check server logs

**Expected Results:**
- ✅ Views show "Inga klasser" (graceful fallback)
- ✅ No application crash
- ✅ No JavaScript errors (or handled gracefully)
- ✅ Other competitions display correctly

**SQL Setup:**
```sql
UPDATE umbracoContent
SET shootingClassIds = '[C1,C2'  -- Invalid JSON
WHERE nodeId = <test-competition-id>
```

---

### TC-010: All Shooting Classes Selected

**Priority:** P2 (Medium)
**Type:** Edge Case

**Preconditions:**
- Logged in as administrator

**Steps:**
1. Create new competition
2. Select ALL 15 shooting classes (A1-A3, B1-B3, C1-C3, C_Vet_Y, C_Vet_A, C_Jun, C1_Dam-C3_Dam)
3. Click "Skapa"
4. View in all three views

**Expected Results:**
- ✅ Success message displayed
- ✅ Database contains JSON array with all 15 classes
- ✅ Card view displays all classes (may wrap to multiple lines)
- ✅ List view displays all classes (may truncate with "...")
- ✅ No performance issues

---

### TC-011: Special Characters in Shooting Class IDs

**Priority:** P2 (Medium)
**Type:** Compatibility

**Preconditions:**
- Competition with shooting classes containing underscores: C_Vet_Y, C_Jun

**Steps:**
1. Create competition with C_Vet_Y and C_Jun
2. View in all views
3. Edit and save again

**Expected Results:**
- ✅ Database stores correctly: `["C_Vet_Y","C_Jun"]`
- ✅ No encoding issues (underscores not escaped)
- ✅ Display shows proper names: "C Vet Y, C Jun"
- ✅ Re-edit preserves values

---

### TC-012: Card View vs List View Consistency

**Priority:** P1 (High)
**Type:** UI Consistency

**Preconditions:**
- Multiple competitions with various shooting classes

**Steps:**
1. View competitions list in card view
2. Note shooting classes for each competition
3. Switch to list view
4. Compare "Klass" column values

**Expected Results:**
- ✅ Card view and list view show IDENTICAL shooting classes for each competition
- ✅ Order may differ but content must match
- ✅ "Inga klasser" shown consistently for competitions without classes

---

### TC-013: Series Page Display

**Priority:** P1 (High)
**Type:** Functional

**Preconditions:**
- Competition series exists with multiple competitions
- Each competition has different shooting classes

**Steps:**
1. Navigate to competition series page
2. Expand competition list
3. Check shooting classes displayed for each competition

**Expected Results:**
- ✅ All competitions show correct shooting classes
- ✅ Matches card view and list view display
- ✅ No "Inga klasser" for competitions with classes

---

### TC-014: Performance - Large Number of Competitions

**Priority:** P2 (Medium)
**Type:** Performance

**Preconditions:**
- Database with 50+ competitions
- All have shooting classes in JSON format

**Steps:**
1. Navigate to /competitions/ page
2. Measure page load time
3. Switch between card and list views
4. Check browser console for errors

**Expected Results:**
- ✅ Page loads in < 3 seconds
- ✅ View switching is instant (< 500ms)
- ✅ No memory leaks
- ✅ No JavaScript errors

---

### TC-015: Concurrent Editing

**Priority:** P3 (Low)
**Type:** Concurrency

**Preconditions:**
- Two admin users logged in
- Same competition open for editing in both browsers

**Steps:**
1. User A edits shooting classes to C1, C2
2. User B edits shooting classes to A1, A2
3. User A saves first
4. User B saves second
5. Check final state

**Expected Results:**
- ✅ Both saves succeed (no error)
- ✅ Final state shows User B's changes (last write wins)
- ✅ Data format is correct JSON
- ✅ No data corruption

---

## Regression Test Suite

**Run this suite after ANY change to:**
- CompetitionAdminController.cs
- PrecisionCompetitionEditService.cs
- CompetitionsHub.cshtml
- CompetitionSeries.cshtml
- Models/ShootingClasses.cs

**Quick Regression Tests (15 minutes):**
1. TC-001: Create competition with single class
2. TC-002: Create competition with multiple classes
3. TC-004: Edit competition shooting classes
4. TC-008: Backward compatibility with CSV
5. TC-012: Card vs list view consistency

**Full Regression Tests (45 minutes):**
- Run ALL test cases (TC-001 through TC-015)

---

## Automated Test Stubs

### Example: Playwright E2E Test

```javascript
// tests/shooting-classes.spec.js
import { test, expect } from '@playwright/test';

test.describe('Shooting Class Storage', () => {

  test('TC-001: Create competition with single shooting class', async ({ page }) => {
    await page.goto('/admin');
    await page.click('text=Competitions');
    await page.click('text=Skapa ny tävling');

    await page.fill('[name="name"]', 'Test Competition - Single Class');
    await page.fill('[name="competitionDate"]', '2026-01-15');
    await page.selectOption('[name="competitionType"]', 'Precisionstävling');

    await page.check('[value="C1"]');

    await page.click('text=Skapa');

    // Wait for success message
    await expect(page.locator('.alert-success')).toBeVisible();

    // Navigate to competitions list
    await page.goto('/competitions/');

    // Check card view
    const cardText = await page.locator('.competition-card').first().textContent();
    expect(cardText).toContain('C1');

    // Check list view
    await page.click('text=List View');
    const listRow = await page.locator('table tr').first().textContent();
    expect(listRow).toContain('C1');
  });

  test('TC-007: Migration endpoint converts CSV to JSON', async ({ request }) => {
    const response = await request.get('/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat');
    const data = await response.json();

    expect(data.success).toBe(true);
    expect(data.errorCount).toBe(0);
    expect(data.fixedCount).toBeGreaterThanOrEqual(0);
  });

});
```

---

## Bug Report Template

If a test fails, use this template to report the bug:

```markdown
## Bug Report: Shooting Class Storage Issue

**Test Case:** TC-XXX
**Priority:** P0/P1/P2/P3
**Status:** Open

### Description
[Brief description of what went wrong]

### Steps to Reproduce
1. [Step 1]
2. [Step 2]
3. [Step 3]

### Expected Result
[What should have happened]

### Actual Result
[What actually happened]

### Screenshots
[Attach screenshots if applicable]

### Database State
```sql
SELECT shootingClassIds FROM umbracoContent WHERE nodeId = XXX
```
Result: [Paste result here]
```

### Browser Console Errors
```
[Paste any console errors]
```

### Server Logs
```
[Paste relevant server log entries]
```

### Environment
- Browser: [Chrome/Firefox/Edge] version [X.X]
- OS: [Windows/Mac/Linux]
- Database: [SQL Server version]
- Umbraco: [Version]

### Related Code Files
- [ ] CompetitionAdminController.cs
- [ ] PrecisionCompetitionEditService.cs
- [ ] CompetitionsHub.cshtml
- [ ] CompetitionSeries.cshtml

### Suggested Fix
[If known, describe potential fix]
```

---

## Test Execution Log Template

```markdown
# Test Execution Log

**Date:** 2025-10-30
**Tester:** [Name]
**Environment:** [Development/Staging/Production]
**Build:** [Git commit hash or version]

## Test Results

| Test Case | Status | Notes |
|-----------|--------|-------|
| TC-001 | ✅ PASS | |
| TC-002 | ✅ PASS | |
| TC-003 | ✅ PASS | |
| TC-004 | ❌ FAIL | See bug report #123 |
| TC-005 | ✅ PASS | |
| TC-006 | ⚠️ SKIP | Copy feature not yet implemented |
| TC-007 | ✅ PASS | Fixed 15 competitions |
| TC-008 | ✅ PASS | |
| TC-009 | ✅ PASS | |
| TC-010 | ✅ PASS | |
| TC-011 | ✅ PASS | |
| TC-012 | ✅ PASS | |
| TC-013 | ✅ PASS | |
| TC-014 | ⚠️ SKIP | Not enough test data |
| TC-015 | ⚠️ SKIP | Requires multiple users |

**Summary:**
- Total: 15 tests
- Passed: 11
- Failed: 1
- Skipped: 3

**Blockers:** None
**Recommendations:** Fix TC-004 before deployment
```

---

## Test Data Management

### Test Competitions

Create these test competitions for comprehensive testing:

```json
[
  {
    "name": "Test - No Classes",
    "shootingClasses": [],
    "expectedDisplay": "Inga klasser"
  },
  {
    "name": "Test - Single Class",
    "shootingClasses": ["C1"],
    "expectedDisplay": "C1"
  },
  {
    "name": "Test - Multiple Classes",
    "shootingClasses": ["C1", "C2", "A1"],
    "expectedDisplay": "C1, C2, A1"
  },
  {
    "name": "Test - All Classes",
    "shootingClasses": ["A1","A2","A3","B1","B2","B3","C1","C2","C3","C_Vet_Y","C_Vet_A","C_Jun","C1_Dam","C2_Dam","C3_Dam"],
    "expectedDisplay": "A1, A2, A3, B1, B2, B3, C1, C2, C3, C Vet Y, C Vet Ä, C Jun, C1 Dam, C2 Dam, C3 Dam"
  },
  {
    "name": "Test - Special Characters",
    "shootingClasses": ["C_Vet_Y", "C_Jun"],
    "expectedDisplay": "C Vet Y, C Jun"
  }
]
```

### Database Cleanup Script

After testing, clean up test data:

```sql
-- Delete test competitions
DELETE FROM umbracoNode WHERE text LIKE 'Test -%' OR text LIKE 'Test Competition%'

-- Verify cleanup
SELECT COUNT(*) FROM umbracoNode WHERE text LIKE 'Test -%'
-- Should return 0
```

---

## Sign-Off Checklist

Before marking this feature as "DONE":

- [ ] All P0 tests pass (TC-001, TC-002, TC-004, TC-007)
- [ ] All P1 tests pass
- [ ] Regression suite passes
- [ ] Migration endpoint tested with real data
- [ ] Documentation reviewed and approved
- [ ] Code reviewed by at least one other developer
- [ ] Performance acceptable (TC-014)
- [ ] Backward compatibility verified (TC-008)
- [ ] No console errors in browser
- [ ] No server errors in logs

**Approved By:** __________________
**Date:** __________________

---

**End of Test Plan**
