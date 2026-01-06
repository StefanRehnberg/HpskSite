# Competition Types Architecture Guide

## Overview

This document defines the architecture for managing multiple competition types in the HPSK shooting club system. The goal is to support different competition formats (Precision, Field Shooting, Spring Shooting, etc.) with a scalable, maintainable structure that promotes code reuse without creating type coupling.

## Architecture Principles

### 1. **Independence First**
- Each competition type is self-contained in its own namespace
- Types never directly reference other types
- Coupling is minimized to maximum extent possible
- Developers can implement a new type without understanding others

### 2. **Shared Utilities, Not Shared Logic**
- Only pure, stateless utilities are shared (Common/Utilities)
- No shared business logic between types
- Each type implements its own services completely
- Some code duplication is acceptable for independence

### 3. **Consistent Patterns**
- All types follow the same interface contracts
- Precision serves as the reference implementation example
- New types can learn patterns from Precision without copying code
- Developers familiar with one type can quickly understand another

### 4. **Clear Responsibility**
- Each service has a specific domain
- No service overlaps with another
- Business logic is not mixed with utilities
- Clear separation between models, services, and controllers

## Folder Structure

```
/CompetitionTypes/
│
├── /Common/                              # Shared across all types
│   ├── /Interfaces/
│   │   ├── ICompetitionType.cs
│   │   ├── IRegistrationService.cs
│   │   ├── IStartListService.cs
│   │   ├── IScoringService.cs
│   │   ├── IResultsService.cs
│   │   └── ICompetitionEditService.cs
│   │
│   ├── /Utilities/                       # Pure stateless helpers
│   │   ├── ScoringUtilities.cs           # Shot value, point calculations
│   │   ├── RankingUtilities.cs           # Tie-breaking, sorting
│   │   ├── ValidationUtilities.cs        # Input validation
│   │   ├── ExportUtilities.cs            # CSV, Excel, PDF helpers
│   │   └── DateTimeUtilities.cs          # Date/time formatting
│   │
│   └── /BaseClasses/                     # Optional base implementations
│       └── CompetitionTypeBase.cs        # (Not required, kept simple)
│
├── /Precision/                           # Reference Implementation (Single-Part)
│   ├── /Models/
│   │   ├── PrecisionResultEntry.cs
│   │   ├── PrecisionStartList.cs
│   │   └── PrecisionFinalsStartList.cs
│   │
│   ├── /ViewModels/
│   │   ├── PrecisionShotEntryViewModel.cs
│   │   ├── PrecisionResultsEntryViewModel.cs
│   │   └── PrecisionFinalsQualificationViewModel.cs
│   │
│   ├── /Services/
│   │   ├── PrecisionRegistrationService.cs    → IRegistrationService
│   │   ├── PrecisionStartListService.cs       → IStartListService
│   │   ├── PrecisionScoringService.cs         → IScoringService
│   │   ├── PrecisionResultsService.cs         → IResultsService
│   │   └── PrecisionCompetitionEditService.cs → ICompetitionEditService
│   │
│   ├── /Controllers/
│   │   ├── PrecisionStartListController.cs
│   │   └── PrecisionResultsController.cs
│   │
│   └── /Tests/
│       └── PrecisionScoringTests.cs
│
├── /Duell/                               # Identical to Precision (Alias)
│   └── /Services/
│       ├── DuellScoringService.cs        (extends PrecisionScoringService)
│       ├── DuellResultsService.cs        (extends PrecisionResultsService)
│       └── ... (other services as needed)
│
├── /Milsnabb/                            # Multi-Part Competition (4 parts)
│   ├── /Models/
│   │   ├── MilsnabbResultEntry.cs
│   │   ├── MilsnabbPartResult.cs
│   │   └── MilsnabbStartList.cs
│   │
│   ├── /ViewModels/
│   │   └── (similar to Precision)
│   │
│   ├── /Services/
│   │   ├── MilsnabbRegistrationService.cs
│   │   ├── MilsnabbStartListService.cs
│   │   ├── MilsnabbScoringService.cs
│   │   ├── MilsnabbResultsService.cs     (aggregates 4 parts)
│   │   └── MilsnabbCompetitionEditService.cs
│   │
│   └── /Controllers/
│       └── (similar to Precision)
│
├── /NationellHelmatch/                   # Multi-Part Competition (3 parts)
│   ├── /Models/
│   │   ├── HelmatchResultEntry.cs
│   │   ├── HelmatchPartResult.cs
│   │   └── HelmatchStartList.cs
│   │
│   ├── /Services/
│   │   ├── HelmatchRegistrationService.cs
│   │   ├── HelmatchStartListService.cs
│   │   ├── HelmatchScoringService.cs
│   │   ├── HelmatchResultsService.cs     (aggregates 3 parts)
│   │   └── HelmatchCompetitionEditService.cs
│   │
│   └── /Controllers/
│       └── (similar to Precision)
│
├── /FieldShooting/                       # Completely Different Type
│   ├── /Models/
│   │   └── (Field-specific data models)
│   │
│   ├── /Services/
│   │   ├── FieldShootingRegistrationService.cs
│   │   ├── FieldShootingStartListService.cs
│   │   ├── FieldShootingScoringService.cs
│   │   ├── FieldShootingResultsService.cs
│   │   └── FieldShootingCompetitionEditService.cs
│   │
│   └── /Controllers/
│       └── (Field-specific controllers)
│
└── /Springskytte/                        # Completely Different Type
    ├── /Models/
    │   └── (Spring-specific data models)
    │
    ├── /Services/
    │   ├── SpringStartListService.cs
    │   ├── SpringScoringService.cs
    │   └── ... (other services)
    │
    └── /Controllers/
        └── (Spring-specific controllers)
```

## Namespace Convention

```csharp
// Common interfaces and utilities
namespace HpskSite.CompetitionTypes.Common.Interfaces
namespace HpskSite.CompetitionTypes.Common.Utilities

// Precision type
namespace HpskSite.CompetitionTypes.Precision.Models
namespace HpskSite.CompetitionTypes.Precision.Services
namespace HpskSite.CompetitionTypes.Precision.Controllers
namespace HpskSite.CompetitionTypes.Precision.ViewModels

// Other types follow same pattern
namespace HpskSite.CompetitionTypes.Milsnabb.Services
namespace HpskSite.CompetitionTypes.FieldShooting.Services
// etc.
```

## View Files and Umbraco Template Conventions

### Important Architectural Constraint

Unlike backend code (Controllers, Services, Models) which can be organized in the CompetitionTypes folder structure, **view files MUST follow Umbraco's platform conventions** for document type templates.

### View File Locations

#### Document Type Templates → `/Views/` (Root Only)

Umbraco's routing engine requires document type templates to be placed in the root Views folder with exact naming:

```
/Views/                                    ← Umbraco's required location
├── PrecisionStartList.cshtml             ← Document type: precisionStartList
├── PrecisionResults.cshtml               ← Document type: precisionResults
├── Competition.cshtml                     ← Document type: competition
├── CompetitionSeries.cshtml              ← Document type: competitionSeries
└── Club.cshtml                           ← Document type: club
```

**Naming Rule:** Document type alias `precisionStartList` → template `Views/PrecisionStartList.cshtml` (PascalCase)

**Why This Location:**
1. Umbraco's template resolution looks for exact path matches in `/Views/`
2. No recursive subfolder searching for document type templates
3. This is an ASP.NET Core MVC / Umbraco platform requirement, not a design choice
4. Custom `IViewLocationExpander` would be needed to change this (not implemented)

#### Partial Views → `/Views/Partials/` (Subfolders Allowed)

Partial views CAN be organized in subfolders since they're explicitly referenced:

```
/Views/Partials/
├── Precision/                            ← Type-specific partials
│   ├── StartListTable.cshtml
│   ├── ResultsEntry.cshtml
│   └── FinalsScoreboard.cshtml
├── Competition/                          ← Generic competition partials
│   ├── ManagementDashboard.cshtml
│   └── RegistrationForm.cshtml
├── TrainingScoreEntry.cshtml             ← Shared partials
└── UserManagement.cshtml
```

**Usage:**
```csharp
// Explicit path reference allows subfolder organization
@await Html.PartialAsync("~/Views/Partials/Precision/StartListTable.cshtml", Model)
```

### Impact on Competition Types Architecture

This creates a **hybrid architecture**:

| Component | Location | Reason |
|-----------|----------|--------|
| Controllers | `CompetitionTypes/{Type}/Controllers/` | ✅ Can be nested in namespaces |
| Services | `CompetitionTypes/{Type}/Services/` | ✅ Can be nested in namespaces |
| Models | `CompetitionTypes/{Type}/Models/` | ✅ Can be nested in namespaces |
| ViewModels | `CompetitionTypes/{Type}/ViewModels/` | ✅ Can be nested in namespaces |
| Tests | `CompetitionTypes/{Type}/Tests/` | ✅ Can be nested in namespaces |
| **Document Templates** | `/Views/` **(root only)** | ⚠️ **Umbraco platform constraint** |
| **Partials** | `/Views/Partials/{Type}/` | ✅ Can be organized in subfolders |

### Best Practices for Views

**✅ DO:**
- Keep document type templates thin - just routing/layout
- Delegate rendering logic to type-specific partials
- Organize partials by competition type in subfolders
- Use explicit paths when referencing partials
- Name templates to match document type alias exactly

**❌ DON'T:**
- Create `CompetitionTypes/Precision/Views/` folders (won't be found by Umbraco)
- Try to nest document type templates in subfolders (won't work)
- Put complex rendering logic directly in document type templates
- Assume Umbraco will search recursively for templates

### Example: Adding RapidFire Type

When adding a new competition type, view files go in two places:

**Backend (Fully Isolated):**
```
CompetitionTypes/RapidFire/
├── Controllers/
│   └── RapidFireController.cs           ✅ Nested in type folder
├── Services/
│   └── RapidFireService.cs              ✅ Nested in type folder
└── Models/
    └── RapidFireConfig.cs               ✅ Nested in type folder
```

**Frontend (Mixed Locations):**
```
Views/
├── RapidFireCompetition.cshtml          ⚠️ Must be at root (Umbraco requirement)
└── Partials/
    └── RapidFire/                       ✅ Can be in subfolder
        ├── ShotTimer.cshtml
        └── TargetDisplay.cshtml
```

### Why We Accept This Limitation

1. **Platform Convention:** Umbraco is the foundation - we work with it, not against it
2. **Complexity Trade-off:** Custom `IViewLocationExpander` adds maintenance burden
3. **Clear Separation:** Backend isolation is more important than frontend isolation
4. **Partial Organization:** We still get organized partials per type
5. **Thin Templates:** Document templates are just routing - real logic lives in partials

### Template Structure Pattern

Keep document type templates minimal:

```csharp
@inherits Umbraco.Cms.Web.Common.Views.UmbracoViewPage<PrecisionStartList>
@{
    Layout = "Master.cshtml";
}

<!-- Thin routing template - just delegates to type-specific partial -->
@await Html.PartialAsync("~/Views/Partials/Precision/StartListDisplay.cshtml", Model)
```

All complex rendering logic lives in the type-specific partial, which CAN be organized in the `Partials/Precision/` subfolder.

## Core Service Interfaces

Every competition type must implement these 5 core service interfaces:

### 1. IRegistrationService
**Responsibility**: Manage participant registration for competitions

```csharp
public interface IRegistrationService
{
    Task RegisterShooter(ShooterRegistration registration);
    Task<List<Registration>> GetRegistrations(int competitionId);
    Task UpdateRegistration(int registrationId, ShooterRegistration updatedData);
    Task UnregisterShooter(int registrationId);
}
```

### 2. IStartListService
**Responsibility**: Generate and manage start lists (participant ordering and timing)

```csharp
public interface IStartListService
{
    Task<dynamic> GenerateStartList(int competitionId);
    Task<dynamic> GenerateStartListWithStrategy(int competitionId, string groupingStrategy);
    Task<dynamic> ValidateStartList(int startListId);
    Task<bool> PublishStartList(int startListId);
    Task<dynamic> GetCurrentStartList(int competitionId);
    Task<dynamic> UpdateStartList(int competitionId);
}
```

**Grouping Strategies** (each type can support different ones):
- `"mixed"` - Mix different classes and clubs
- `"byClass"` - Group by shooting class
- `"byClub"` - Group by member club
- `"random"` - Random ordering

### 3. IScoringService
**Responsibility**: Calculate points and validate shot entries

```csharp
public interface IScoringService
{
    decimal CalculateSeriesTotal(List<string> shots);
    int CalculateTens(List<string> shots);
    bool IsValidShotValue(string shotValue);
    // Type-specific scoring methods can be added as public methods
}
```

### 4. IResultsService
**Responsibility**: Generate final results, rankings, and leaderboards

```csharp
public interface IResultsService
{
    Task<List<dynamic>> GenerateCompetitionResults(int competitionId);
    Task<List<dynamic>> GetLiveLeaderboard(int competitionId);
    Task<dynamic> GetParticipantResults(int registrationId);
    Task<byte[]> ExportResults(int competitionId, string format);
    Task<List<dynamic>> CalculateFinalRanking(int competitionId);
}
```

### 5. ICompetitionEditService
**Responsibility**: Admin CRUD operations (create, update, delete competitions)

```csharp
public interface ICompetitionEditService
{
    Task<dynamic> CreateCompetition(CompetitionData data);
    Task<dynamic> UpdateCompetition(int competitionId, CompetitionData data);
    Task<bool> DeleteCompetition(int competitionId);
    Task<dynamic> GetCompetitionData(int competitionId);
}
```

## Shared Utilities (Common/Utilities)

### ScoringUtilities.cs
Pure utility functions for scoring calculations:

```csharp
public static class ScoringUtilities
{
    /// <summary>Shot value to points conversion</summary>
    public static decimal ShotToPoints(string shot)
    {
        // X = 10, 10 = 10, 9-0 = numeric value
        // Can be used by Precision, Duell, Milsnabb, Helmatch
    }

    /// <summary>Validate shot value format</summary>
    public static bool IsValidShotFormat(string shot)
    {
        // 0-10 or X
    }
}
```

### RankingUtilities.cs
Ranking and tie-breaking helpers:

```csharp
public static class RankingUtilities
{
    /// <summary>Apply tie-breaking rules to a ranked list</summary>
    public static List<T> ApplyTieBreaking<T>(
        List<T> items,
        params Func<T, IComparable>[] tieBreakers)
    {
        // Generic implementation that works for any type
    }

    /// <summary>Group by shooting class</summary>
    public static Dictionary<string, List<T>> GroupByClass<T>(List<T> items)
    {
        // where T has shootingClass property
    }
}
```

### ValidationUtilities.cs
Input validation helpers:

```csharp
public static class ValidationUtilities
{
    public static bool IsValidEmail(string email);
    public static bool IsValidPhoneNumber(string phone);
    public static bool IsValidCompetitionDate(DateTime date);
    public static List<string> ValidateRegistration(Registration reg);
}
```

### ExportUtilities.cs
Export format helpers:

```csharp
public static class ExportUtilities
{
    public static byte[] ExportToCsv<T>(List<T> items);
    public static byte[] ExportToExcel<T>(List<T> items);
    public static byte[] ExportToPdf<T>(List<T> items);
    public static string ToCsvLine<T>(T item);
}
```

### DateTimeUtilities.cs
Date/time formatting:

```csharp
public static class DateTimeUtilities
{
    public static string FormatCompetitionDate(DateTime date);
    public static string FormatTime(TimeSpan time);
    public static string FormatSchedule(DateTime start, DateTime end);
}
```

## Implementation Patterns

### Pattern 1: Single-Part Competition (Precision, Duell)

These types have straightforward structure:

```csharp
// One series of shots = one result entry
// Total score = sum of all shots
// Ranking = ordered by total score with tie-breaking

public class PrecisionResultsService : IResultsService
{
    private readonly PrecisionScoringService _scoring;

    public async Task<List<dynamic>> GenerateCompetitionResults(int competitionId)
    {
        // 1. Get all registrations
        // 2. For each registration:
        //    - Get all series results
        //    - Calculate total using _scoring.CalculateSeriesTotal()
        //    - Apply tie-breaking rules
        // 3. Return ranked list
    }
}
```

### Pattern 2: Multi-Part Competition (Milsnabb = 4 parts, Helmatch = 3 parts)

Structure with aggregated results:

```csharp
public class MilsnabbResultEntry
{
    public int RegistrationId { get; set; }
    public List<PartResult> Parts { get; set; }  // 4 parts
    public decimal TotalScore { get; set; }       // Sum of all parts
}

public class MilsnabbResultsService : IResultsService
{
    public async Task<List<dynamic>> GenerateCompetitionResults(int competitionId)
    {
        // 1. Get all registrations
        // 2. For each registration:
        //    a. Get results for each of 4 parts
        //    b. Calculate score for each part using _scoring
        //    c. Create PartResult for each
        //    d. Sum all parts for total score
        // 3. Return ranked list with sub-part breakdown

        var results = new List<dynamic>();
        foreach (var registration in registrations)
        {
            var entry = new MilsnabbResultEntry
            {
                RegistrationId = registration.Id,
                Parts = new List<PartResult>()
            };

            foreach (int partNumber in 1..4)
            {
                var partScores = GetPartScores(registration.Id, partNumber);
                entry.Parts.Add(new PartResult
                {
                    PartNumber = partNumber,
                    Score = _scoring.CalculateTotal(partScores)
                });
            }

            entry.TotalScore = entry.Parts.Sum(p => p.Score);
            results.Add(entry);
        }

        return RankResults(results);
    }
}
```

### Pattern 3: Completely Different Type (FieldShooting, Springskytte)

Implement all services from scratch:

```csharp
// In FieldShooting namespace
public class FieldShootingScoringService : IScoringService
{
    // Completely different scoring rules
    // No inheritance from Precision
    // Uses RankingUtilities and ValidationUtilities from Common

    public decimal CalculateSeriesTotal(List<string> shots)
    {
        // Field shooting has different scoring rules
        // (targets instead of rings, etc.)
    }
}
```

## Handling Code Reuse

### Option A: Duell as Precision Alias (Recommended for identical types)

Since Duell scoring and results are identical to Precision:

```csharp
// In Duell namespace
public class DuellScoringService : Precision.Services.PrecisionScoringService
{
    // Inherits everything from Precision
    // No additional code needed
}

public class DuellResultsService : Precision.Services.PrecisionResultsService
{
    // Inherits everything from Precision
    // Can override specific methods if needed
}

// Only override what's different (if anything)
```

### Option B: Adapted Implementation (For similar but different types)

For Milsnabb and Helmatch that have multi-part structure:

```csharp
// Copy Precision's code as starting point
// Customize for multi-part results aggregation
// Use shared utilities (ScoringUtilities, RankingUtilities)
// But implement complete services independently

// DON'T inherit from Precision services
// This maintains independence
```

### Option C: Shared Utility Usage (For all types)

All services can use pure utilities without coupling:

```csharp
public class MilsnabbScoringService : IScoringService
{
    public decimal CalculateSeriesTotal(List<string> shots)
    {
        var total = 0m;
        foreach (var shot in shots)
        {
            // Use shared utility function
            total += ScoringUtilities.ShotToPoints(shot);
        }
        return total;
    }
}
```

## File Naming Rules

Prefix all files with competition type name:

```
Precision/Services/PrecisionStartListService.cs ✓
Precision/Services/StartListService.cs          ✗ Too generic

Milsnabb/ViewModels/MilsnabbShotEntryViewModel.cs ✓
Milsnabb/ViewModels/ShotEntryViewModel.cs         ✗ Too generic
```

This helps with:
- Finding which files belong to which type
- Preventing accidental name conflicts
- Making type ownership clear at a glance
- Improving code search and IntelliSense readability

## Migration Path

### Phase 1: Extract Shared Utilities
- Move pure utility functions to Common/Utilities
- Ensure no business logic or type-specific code
- Update Precision to use these utilities
- Test Precision still works

### Phase 2: Create Duell as Alias
- Create `/Duell/Services/` folder
- Implement DuellScoringService extending PrecisionScoringService
- Implement other services extending Precision equivalents
- Wire up in CompetitionTypeFactory

### Phase 3: Implement Milsnabb
- Create `/Milsnabb/Services/` folder
- Implement all 5 services independently
- Add multi-part aggregation logic
- Add Milsnabb-specific ViewModels and Controllers

### Phase 4: Implement Helmatch
- Create `/NationellHelmatch/Services/` folder
- Similar to Milsnabb but with 3 parts instead of 4
- Use shared utilities where applicable

### Phase 5: Future Types
- FieldShooting as completely independent implementation
- Springskytte as completely independent implementation
- Each fully self-contained

## Adding a New Competition Type

### Checklist for New Type Implementation

- [ ] Create `/[TypeName]/` folder structure
- [ ] Create Models folder and define type-specific data models
- [ ] Create Services folder and implement all 5 service interfaces
- [ ] Create ViewModels folder for UI data transfer
- [ ] Create Controllers folder for API endpoints
- [ ] Add any type-specific utilities to Common/Utilities if needed
- [ ] Wire up services in dependency injection (Program.cs)
- [ ] Add type to CompetitionTypes.All list in Models/CompetitionType.cs
- [ ] Create unit tests in Tests folder
- [ ] Update documentation with new type details
- [ ] Verify it compiles and no conflicts with other types

### Minimal Example

To implement a new competition type called "CustomType":

1. Create folder: `/CompetitionTypes/CustomType/`
2. Create services implementing these 5 interfaces:
   - CustomTypeRegistrationService : IRegistrationService
   - CustomTypeStartListService : IStartListService
   - CustomTypeScoringService : IScoringService
   - CustomTypeResultsService : IResultsService
   - CustomTypeCompetitionEditService : ICompetitionEditService
3. Create models and viewmodels as needed
4. Create controllers for endpoints
5. Register in DI container
6. Done! Other types are completely unaffected

## Testing Strategy

### Unit Tests per Type
Each type should have tests for:
- Scoring calculations
- Ranking/tie-breaking logic
- Validation rules
- Data transformations

```csharp
// Tests/MilsnabbScoringTests.cs
public class MilsnabbScoringTests
{
    [Test]
    public void CalculateSeriesTotal_WithValidShots_ReturnsCorrectTotal()
    {
        var service = new MilsnabbScoringService();
        var shots = new List<string> { "10", "X", "9" };
        var result = service.CalculateSeriesTotal(shots);
        Assert.AreEqual(29, result);
    }
}
```

### Integration Tests
- Test that all services work together for a complete competition
- Test results generation with realistic data
- Test export functionality

### No Cross-Type Testing
- Each type is tested in isolation
- No test should verify interaction between types
- This maintains independence

## FAQ

**Q: Should we share scoring logic between Precision and Duell?**
A: Yes, via inheritance. DuellScoringService extends PrecisionScoringService. This is appropriate for identical types.

**Q: Should we share start list generation logic between Precision and Milsnabb?**
A: No, implement independently using shared utilities. Even if logic is similar, maintaining independence reduces coupling.

**Q: How do we handle type-specific UI requirements?**
A: Each type has its own ViewModels and Controllers. UI is not shared.

**Q: Can a developer implement Springskytte without understanding Precision?**
A: Yes, they should only need to understand the 5 interface contracts and look at Precision as an example pattern, not copy Precision code.

**Q: Where does utility code belong?**
A: Common/Utilities. But only if it has ZERO type-specific logic.

**Q: How do we add a feature that affects multiple types?**
A: 1) If it's a shared utility, add to Common/Utilities
    2) If it's type-specific logic, implement separately in each type
    3) Don't modify one type's services to support another type's feature

## Related Documents
- COMPETITION_TYPES_ARCHITECTURE.md - Original planning document
- Precision implementation - Reference example for new types
- Interface definitions - What each service must implement
