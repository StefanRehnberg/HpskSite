# Competition Types Architecture Plan

## Overview
This document outlines the architecture for supporting multiple competition types in the Umbraco v16.2 site. The goal is to safely expand beyond the current Precision competition type without breaking existing functionality.

## Architecture Pattern
Use a **Strategy Pattern with Factory** - each competition type gets its own isolated implementation with shared interfaces.

## Folder Structure
```
/CompetitionTypes/
  /Common/                          # Shared interfaces and base classes
    ICompetitionType.cs
    CompetitionTypeBase.cs
    IRegistrationService.cs
    IScoringService.cs
    IResultsService.cs
    CompetitionTypeFactory.cs

  /Precision/                       # Existing Precision implementation
    Models/
      PrecisionCompetitionModel.cs
      PrecisionResultModel.cs
    ViewModels/
      PrecisionRegistrationViewModel.cs
      PrecisionStartListViewModel.cs
      PrecisionResultsViewModel.cs
    Services/
      PrecisionRegistrationService.cs
      PrecisionScoringService.cs
      PrecisionResultsService.cs
    Controllers/
      PrecisionCompetitionController.cs

  /[FutureType]/                    # Future competition types follow same pattern
    Models/
    ViewModels/
    Services/
    Controllers/
```

**Important Note on Views:**
View files (document type templates) do NOT live in the CompetitionTypes folder structure. Due to Umbraco's platform conventions, document type templates must be placed in `/Views/` (root folder). Partial views can be organized in `/Views/Partials/{Type}/` subfolders. See "Umbraco Conventions" section below for details.

## Namespace Convention
```csharp
[ProjectName].CompetitionTypes.Common
[ProjectName].CompetitionTypes.Precision
[ProjectName].CompetitionTypes.[TypeName]
```

## File Naming Rules
- **Prefix all files** with competition type name: `PrecisionRegistrationService.cs`, `PrecisionScoring.cs`
- Helps identify ownership when viewing files in search or IntelliSense
- Each type's files live in their own folder for complete isolation

## Interface Examples
```csharp
namespace [ProjectName].CompetitionTypes.Common
{
    public interface ICompetitionType
    {
        string TypeName { get; }
        string DisplayName { get; }
    }
    
    public interface IRegistrationService
    {
        Task RegisterShooter(ShooterRegistration registration);
        Task<List<Registration>> GetRegistrations(int competitionId);
    }
    
    public interface IScoringService
    {
        Task<Score> CalculateScore(ScoreInput input);
        Task<bool> ValidateScore(ScoreInput input);
    }
    
    public interface IResultsService
    {
        Task<ResultsList> GenerateResults(int competitionId);
        Task<StartList> GenerateStartList(int competitionId);
    }
}
```

## Implementation Example
```csharp
namespace [ProjectName].CompetitionTypes.Precision
{
    public class PrecisionRegistrationService : IRegistrationService
    {
        // Precision-specific registration logic
    }
    
    public class PrecisionScoringService : IScoringService
    {
        // Precision-specific scoring logic
    }
}
```

## Key Benefits
1. **Complete isolation** - Each competition type is self-contained
2. **No breaking changes** - Adding new types won't affect Precision
3. **Clear ownership** - Easy to identify which code belongs to which type
4. **Testability** - Each type can be tested independently
5. **Maintainability** - Changes to one type don't ripple to others
6. **Scalability** - Easy to add new competition types

## Migration Strategy
1. Create folder structure and common interfaces
2. Move existing Precision code into new structure (one piece at a time)
3. Update references and test thoroughly
4. Once Precision is stable in new structure, add new competition types

## Umbraco Conventions

### View File Organization

**Important Constraint:** Umbraco's platform conventions dictate where view files must be placed.

#### Document Type Templates
Unlike backend code which can be organized in the CompetitionTypes folder structure, **document type templates MUST be placed in `/Views/` (root folder)**:

```
/Views/
├── PrecisionStartList.cshtml      ← Document type: precisionStartList
├── PrecisionResults.cshtml        ← Document type: precisionResults
├── Competition.cshtml             ← Document type: competition
└── CompetitionSeries.cshtml       ← etc.
```

**Why:** Umbraco's template resolution engine looks for exact matches in `/Views/` and does NOT recursively search subfolders.

#### Partial Views
Partial views CAN be organized in subfolders since they're explicitly referenced:

```
/Views/Partials/
├── Precision/                     ← Type-specific partials
│   ├── StartListTable.cshtml
│   └── ResultsEntry.cshtml
└── Competition/                   ← Generic partials
    └── ManagementDashboard.cshtml
```

### Architecture Impact

This creates a **hybrid isolation** model:
- ✅ **Backend:** Controllers, Services, Models, Tests = fully isolated in CompetitionTypes folders
- ⚠️ **Frontend:** Document type templates = must live in `/Views/` root (Umbraco platform requirement)
- ✅ **Partials:** Can be organized by type in `/Views/Partials/` subfolders

**Best Practice:** Keep document type templates thin (just routing/layout), delegate rendering to type-specific partials in organized subfolders.

For comprehensive details, see `CompetitionTypes/README.md` and `COMPETITION_TYPES_ARCHITECTURE_GUIDE.md`.

## Notes
- This is a planning document - **DO NOT implement yet**
- All existing Precision functionality must continue working during migration
- Test thoroughly after each migration step
- View files follow Umbraco conventions, not CompetitionTypes folder structure
