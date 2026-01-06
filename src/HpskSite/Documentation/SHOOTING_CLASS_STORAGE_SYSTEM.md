# Shooting Class Storage System - Technical Documentation

## Overview

This document describes the shooting class storage and retrieval system for competitions in the HPSK site. It covers the architecture, data format, implementation details, and testing requirements to prevent regressions.

**Last Updated:** 2025-10-30
**Version:** 2.0 (JSON format)

---

## Table of Contents

1. [Data Format Specification](#data-format-specification)
2. [Architecture Overview](#architecture-overview)
3. [Implementation Details](#implementation-details)
4. [Testing Requirements](#testing-requirements)
5. [Migration Guide](#migration-guide)
6. [Troubleshooting](#troubleshooting)

---

## Data Format Specification

### Current Format (v2.0) - JSON Array

**Storage Format:**
```json
["A1","A2","A3","B1","B2","B3","C1","C2","C3"]
```

**Umbraco Property:**
- Property Alias: `shootingClassIds`
- Data Type: Textstring (stores serialized JSON)
- Document Type: `competition`

**Valid Shooting Class IDs:**
- `A1`, `A2`, `A3` - Vapenklass A (levels 1-3)
- `B1`, `B2`, `B3` - Vapenklass B (levels 1-3)
- `C1`, `C2`, `C3` - Vapenklass C (levels 1-3)
- `C_Vet_Y`, `C_Vet_A` - Veteran classes
- `C_Jun` - Junior class
- `C1_Dam`, `C2_Dam`, `C3_Dam` - Women's classes

### Legacy Format (v1.0) - CSV String

**Storage Format:**
```
A1,A2,A3,B1,B2,B3,C1,C2,C3
```

**Status:** Deprecated but still supported for backward compatibility

### Why JSON Format?

1. **Type Safety:** Properly typed as an array, not a string
2. **Standards Compliant:** Uses standard JSON serialization
3. **Umbraco Compatibility:** Works correctly with Umbraco's property system
4. **Extensibility:** Easier to add metadata in the future (e.g., `[{"id":"A1","enabled":true}]`)

---

## Architecture Overview

### Write Operations (Saving Shooting Classes)

```
User Input (CSV from checkboxes)
        ↓
CompetitionAdminController.CreateCompetition
        ↓
Convert CSV → JSON Array
        ↓
_contentService.SetValue("shootingClassIds", jsonString)
        ↓
Umbraco Database (stores JSON string)
```

**Key Files:**
- `Controllers/CompetitionAdminController.cs` (lines 339-357)
- `CompetitionTypes/Precision/Services/PrecisionCompetitionEditService.cs` (lines 331-372)

### Read Operations (Displaying Shooting Classes)

```
Umbraco Database (JSON string)
        ↓
competition.Value("shootingClassIds") → returns string
        ↓
Detect format (JSON vs CSV vs Array)
        ↓
Deserialize to string[]
        ↓
Filter ShootingClasses.All by IDs
        ↓
Display to user
```

**Key Files:**
- `Views/CompetitionsHub.cshtml` (card view: lines 406-441, list view: lines 735-790)
- `Views/CompetitionSeries.cshtml` (lines 155-208)

---

## Implementation Details

### 1. Writing Shooting Classes (Controllers)

#### CompetitionAdminController.CreateCompetition

**Location:** `Controllers/CompetitionAdminController.cs` lines 339-357

**Purpose:** Convert user input to JSON format when creating competitions

**Implementation:**
```csharp
else if (field.Key == "shootingClassIds" && value != null)
{
    // Convert to JSON array string for storage
    if (value is string stringValue && !string.IsNullOrEmpty(stringValue))
    {
        // Split comma-separated values and serialize to JSON array
        var classIds = stringValue.Split(',')
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .ToArray();
        value = System.Text.Json.JsonSerializer.Serialize(classIds);
    }
    else if (value is System.Text.Json.JsonElement jsonElement)
    {
        // Handle JSON array from frontend
        if (jsonElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var classIds = jsonElement.EnumerateArray()
                .Select(e => e.GetString())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            value = System.Text.Json.JsonSerializer.Serialize(classIds);
        }
    }
}
```

**Critical Points:**
- ✅ MUST use `System.Text.Json.JsonSerializer.Serialize()`
- ✅ MUST handle both string (CSV) and JsonElement input
- ❌ DO NOT assign array directly (Umbraco will serialize incorrectly)
- ❌ DO NOT assign CSV string directly

#### PrecisionCompetitionEditService.ConvertFieldValue

**Location:** `CompetitionTypes/Precision/Services/PrecisionCompetitionEditService.cs` lines 331-372

**Purpose:** Convert shooting class IDs when editing competitions

**Implementation:**
```csharp
return fieldName switch
{
    // ... other fields ...
    "shootingClassIds" => ConvertShootingClassIds(value.ToString()),
    _ => value.ToString()
};

private object ConvertShootingClassIds(string value)
{
    if (string.IsNullOrEmpty(value))
        return null;

    // If it's already a JSON array, return as-is
    if (value.TrimStart().StartsWith("["))
        return value;

    // Split CSV and convert to JSON array string
    var classIds = value.Split(',')
        .Select(s => s.Trim())
        .Where(s => !string.IsNullOrEmpty(s))
        .ToArray();

    return System.Text.Json.JsonSerializer.Serialize(classIds);
}
```

**Critical Points:**
- ✅ MUST check if already JSON (starts with `[`)
- ✅ MUST convert CSV to JSON if not already JSON
- ✅ MUST handle null/empty gracefully

### 2. Reading Shooting Classes (Views)

#### Standard Pattern for All Views

**Locations:**
- `Views/CompetitionsHub.cshtml` (lines 406-441, 735-790)
- `Views/CompetitionSeries.cshtml` (lines 155-208)

**Implementation Pattern:**
```csharp
// Multiple shooting classes support - handle string array, JSON array, or CSV
var shootingClassIdsRaw = (object?)null;
string[] classIdArray = Array.Empty<string>();
try
{
    shootingClassIdsRaw = competition.Value("shootingClassIds");
    if (shootingClassIdsRaw is string[] stringArray)
    {
        // Direct array from Umbraco (rare)
        classIdArray = stringArray;
    }
    else if (shootingClassIdsRaw is string stringValue && !string.IsNullOrEmpty(stringValue))
    {
        // Check if JSON array format
        if (stringValue.TrimStart().StartsWith("["))
        {
            classIdArray = System.Text.Json.JsonSerializer.Deserialize<string[]>(stringValue)
                ?? Array.Empty<string>();
        }
        else
        {
            // CSV format (legacy)
            classIdArray = stringValue.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
        }
    }
}
catch (System.Text.Json.JsonException)
{
    // Handle corrupted shooting class data - fallback to empty
    classIdArray = Array.Empty<string>();
}

// Get all shooting classes for this competition
var competitionShootingClasses = new List<dynamic>();
if (classIdArray.Length > 0)
{
    competitionShootingClasses = shootingClasses
        .Where(sc => classIdArray.Contains((string)sc.Id))
        .ToList<dynamic>();
}
```

**Critical Points:**
- ✅ MUST use `string[]` not `string` for final result
- ✅ MUST check if JSON (starts with `[`) before deserializing
- ✅ MUST handle all three formats (string[], JSON, CSV)
- ✅ MUST cast `sc.Id` to `(string)` when using `.Contains()` on dynamic list
- ✅ MUST use try-catch for JSON deserialization
- ❌ DO NOT use `.Split(',')` directly on raw value (won't work with JSON)
- ❌ DO NOT forget to handle empty/null cases

### 3. Migration Endpoint

**Location:** `Controllers/CompetitionAdminController.cs` lines 583-675

**URL:** `/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat`

**Purpose:** Convert existing competitions from CSV to JSON format

**Algorithm:**
1. Get all competition nodes from content tree
2. For each competition:
   - Read `shootingClassIds` value
   - Check if already JSON (starts with `[`)
   - If CSV: split → serialize to JSON → save
   - Track statistics (fixed, already correct, errors)
3. Return summary report

**Response Format:**
```json
{
  "success": true,
  "message": "Migration completed. Fixed: 15, Already correct: 1, Errors: 0",
  "fixedCount": 15,
  "alreadyCorrectCount": 1,
  "errorCount": 0,
  "totalCompetitions": 16,
  "errors": []
}
```

---

## Testing Requirements

### Manual Testing Checklist

#### Test 1: Create New Competition with Shooting Classes
**Steps:**
1. Navigate to Admin → Competitions tab
2. Click "Skapa ny tävling"
3. Fill in required fields
4. Select multiple shooting classes (e.g., C1, C2, A1)
5. Click "Skapa"

**Expected Results:**
- ✅ Competition created successfully
- ✅ Database stores shooting classes as JSON: `["C1","C2","A1"]`
- ✅ Competitions list (card view) shows selected classes
- ✅ Competitions list (list view) shows selected classes in "Klass" column
- ✅ Competition series page shows selected classes

**How to Verify Database:**
```sql
SELECT Id, shootingClassIds FROM [dbo].[umbracoContent]
WHERE nodeId IN (SELECT id FROM umbracoNode WHERE text = 'Your Competition Name')
```

#### Test 2: Edit Existing Competition
**Steps:**
1. Navigate to competitions list
2. Click edit icon on any competition
3. Change shooting classes selection
4. Click "Spara"

**Expected Results:**
- ✅ Changes saved successfully
- ✅ Database updated with new JSON array
- ✅ Views immediately show updated classes (after refresh)

#### Test 3: Migration Endpoint
**Steps:**
1. Create a test competition with CSV format data (manually in DB if needed)
2. Navigate to `/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat`
3. Review response

**Expected Results:**
- ✅ Response shows `success: true`
- ✅ `fixedCount` matches number of CSV-format competitions
- ✅ Database now has JSON format for all competitions
- ✅ No errors reported

#### Test 4: Backward Compatibility with Legacy CSV
**Steps:**
1. Manually set a competition's shootingClassIds to CSV: `"C1,C2,A1"`
2. View competitions list (card and list views)
3. View competition series page

**Expected Results:**
- ✅ All views correctly display "C1, C2, A1"
- ✅ No JavaScript errors in console
- ✅ No server errors

#### Test 5: Edge Cases
**Test 5a: Empty Shooting Classes**
- Create competition with NO shooting classes selected
- Expected: Views show "Inga klasser"

**Test 5b: All Shooting Classes**
- Select all 15+ shooting classes
- Expected: All classes display correctly

**Test 5c: Corrupted JSON**
- Manually set shootingClassIds to invalid JSON: `"[C1,C2"`
- Expected: Views show "Inga klasser" (graceful fallback)

**Test 5d: Special Characters**
- Shooting classes with underscores (C_Vet_Y, C_Jun)
- Expected: Display correctly without escaping issues

---

### Automated Testing Recommendations

#### Unit Tests (Not Currently Implemented)

**Test Class:** `ShootingClassStorageTests.cs`

**Test Cases:**

```csharp
[TestClass]
public class ShootingClassStorageTests
{
    [TestMethod]
    public void ConvertShootingClassIds_CsvString_ReturnsJsonArray()
    {
        // Arrange
        var input = "C1,C2,A1";

        // Act
        var result = ConvertShootingClassIds(input);

        // Assert
        Assert.AreEqual("[\"C1\",\"C2\",\"A1\"]", result);
    }

    [TestMethod]
    public void ConvertShootingClassIds_JsonArray_ReturnsUnchanged()
    {
        // Arrange
        var input = "[\"C1\",\"C2\",\"A1\"]";

        // Act
        var result = ConvertShootingClassIds(input);

        // Assert
        Assert.AreEqual(input, result);
    }

    [TestMethod]
    public void DeserializeShootingClasses_JsonArray_ReturnsStringArray()
    {
        // Arrange
        var input = "[\"C1\",\"C2\",\"A1\"]";

        // Act
        var result = DeserializeShootingClasses(input);

        // Assert
        Assert.AreEqual(3, result.Length);
        Assert.AreEqual("C1", result[0]);
    }

    [TestMethod]
    public void DeserializeShootingClasses_CsvString_ReturnsStringArray()
    {
        // Arrange
        var input = "C1,C2,A1";

        // Act
        var result = DeserializeShootingClasses(input);

        // Assert
        Assert.AreEqual(3, result.Length);
        Assert.AreEqual("C1", result[0]);
    }

    [TestMethod]
    public void DeserializeShootingClasses_EmptyString_ReturnsEmptyArray()
    {
        // Arrange
        var input = "";

        // Act
        var result = DeserializeShootingClasses(input);

        // Assert
        Assert.AreEqual(0, result.Length);
    }

    [TestMethod]
    public void DeserializeShootingClasses_InvalidJson_ReturnsEmptyArray()
    {
        // Arrange
        var input = "[C1,C2"; // Invalid JSON

        // Act
        var result = DeserializeShootingClasses(input);

        // Assert
        Assert.AreEqual(0, result.Length); // Graceful fallback
    }
}
```

#### Integration Tests

**Test Class:** `CompetitionShootingClassesIntegrationTests.cs`

**Test Cases:**

```csharp
[TestClass]
public class CompetitionShootingClassesIntegrationTests
{
    private IContentService _contentService;
    private int _testCompetitionId;

    [TestInitialize]
    public void Setup()
    {
        // Initialize Umbraco services
        // Create test competition
    }

    [TestMethod]
    public void CreateCompetition_WithShootingClasses_StoresAsJson()
    {
        // Arrange
        var shootingClasses = "C1,C2,A1";

        // Act
        var competitionId = CreateCompetition(shootingClasses);
        var competition = _contentService.GetById(competitionId);
        var storedValue = competition.GetValue<string>("shootingClassIds");

        // Assert
        Assert.IsTrue(storedValue.StartsWith("["));
        Assert.IsTrue(storedValue.Contains("\"C1\""));
    }

    [TestMethod]
    public void EditCompetition_UpdatesShootingClasses_StoresAsJson()
    {
        // Arrange
        var competition = _contentService.GetById(_testCompetitionId);

        // Act
        competition.SetValue("shootingClassIds", "B1,B2");
        SaveCompetition(competition);

        // Assert
        var updated = _contentService.GetById(_testCompetitionId);
        var storedValue = updated.GetValue<string>("shootingClassIds");
        Assert.AreEqual("[\"B1\",\"B2\"]", storedValue);
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Delete test competitions
    }
}
```

---

## Migration Guide

### When to Run Migration

Run the migration endpoint in these scenarios:
1. **After upgrading from v1.0 to v2.0** (CSV to JSON)
2. **After detecting "Inga klasser" on old competitions**
3. **Before going to production** (to ensure data consistency)

### How to Run Migration

**Method 1: Direct URL (Recommended)**
```
https://yourdomain.com/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat
```

**Method 2: Via Browser Console**
```javascript
fetch('/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat')
    .then(r => r.json())
    .then(data => console.log(data));
```

**Method 3: Via PowerShell**
```powershell
Invoke-WebRequest -Uri "https://yourdomain.com/umbraco/surface/CompetitionAdmin/FixShootingClassIdsFormat" `
    -UseDefaultCredentials | ConvertFrom-Json
```

### Migration Output Analysis

**Healthy Output:**
```json
{
  "fixedCount": 20,      // All old competitions fixed
  "alreadyCorrectCount": 5,  // Recent competitions already OK
  "errorCount": 0,       // No errors
  "errors": []
}
```

**Problematic Output:**
```json
{
  "fixedCount": 15,
  "alreadyCorrectCount": 5,
  "errorCount": 3,
  "errors": [
    "Competition 2171 (Spring Cup): Failed to save",
    "Competition 2180 (Summer Open): Failed to save"
  ]
}
```

**Actions for Errors:**
1. Check Umbraco logs for detailed error messages
2. Verify competitions can be edited manually
3. Check database permissions
4. Re-run migration after fixing issues

---

## Troubleshooting

### Problem: "Inga klasser" displayed for all competitions

**Symptoms:**
- Competitions list shows "Inga klasser" in card view, list view, or both
- Database has shooting class data

**Diagnosis:**
1. Check database format:
   ```sql
   SELECT TOP 5 shootingClassIds FROM umbracoContent WHERE shootingClassIds IS NOT NULL
   ```
2. Check if data is JSON (`["C1","C2"]`) or CSV (`C1,C2`)

**Solution:**
- If CSV format: Run migration endpoint
- If JSON format: Check view code has deserialization logic (see Implementation Details)

### Problem: New competitions save as CSV instead of JSON

**Symptoms:**
- Database shows `C1,C2,A1` instead of `["C1","C2","A1"]`

**Diagnosis:**
1. Check `CompetitionAdminController.CreateCompetition` lines 339-357
2. Verify `System.Text.Json.JsonSerializer.Serialize()` is being called

**Solution:**
- Verify code matches Implementation Details section
- Check if recent merge/refactor reverted the fix
- Re-apply the JSON serialization code

### Problem: Migration endpoint returns errors

**Symptoms:**
- Migration response has `errorCount > 0`
- Specific competitions fail to update

**Diagnosis:**
1. Check Umbraco logs: `App_Data/Logs/`
2. Try manually editing the failing competition
3. Check if competition is published

**Solution:**
- Unpublish and re-publish competition
- Check database permissions
- Verify content tree structure is intact

### Problem: JavaScript errors in browser console

**Symptoms:**
- Browser console shows errors like "Cannot read property 'Id' of undefined"
- Shooting classes don't display

**Diagnosis:**
1. Open browser DevTools → Console
2. Check for specific error messages
3. Verify page source shows correct data

**Solution:**
- Check if view code has `(string)sc.Id` cast (see Implementation Details)
- Verify `shootingClasses` list is populated
- Check for null reference errors in LINQ queries

---

## Version History

### v2.0 (2025-10-30)
- **Breaking Change:** Switched from CSV to JSON array format
- Added migration endpoint
- Updated all view code to handle both formats
- Fixed dynamic type extension method errors

### v1.0 (Initial)
- CSV format storage
- Basic view rendering

---

## References

- **Shooting Classes Definition:** `Models/ShootingClasses.cs`
- **Competition Document Type:** Umbraco → Settings → Document Types → competition
- **Related Documentation:** `COMPETITION_RESULTS_WORKFLOW.md`

---

## Maintenance Checklist

**When modifying competition-related code:**

- [ ] Does it read or write `shootingClassIds`?
- [ ] If writing: Does it serialize to JSON using `System.Text.Json.JsonSerializer.Serialize()`?
- [ ] If reading: Does it handle JSON, CSV, and string[] formats?
- [ ] Does it include try-catch for JSON deserialization?
- [ ] Does it cast dynamic properties to `(string)` when using LINQ?
- [ ] Have you tested with both JSON and CSV format data?
- [ ] Have you verified all three views (card, list, series page)?
- [ ] Is the change documented in this file?

---

**End of Documentation**
