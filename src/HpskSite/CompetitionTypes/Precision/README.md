# Precision Competition Type

This folder contains all code specific to the Precision competition type.

## Structure

```
Precision/
├── Models/              - Precision-specific models (PrecisionStartList, etc.)
├── ViewModels/          - Precision-specific ViewModels
├── Controllers/         - Precision controllers (refactored with focused responsibilities)
│   ├── PrecisionStartListController.cs
│   ├── StartListRequestValidator.cs  - Validation logic
│   ├── UmbracoStartListRepository.cs  - Data retrieval from Umbraco
│   ├── StartListGenerator.cs          - Team generation algorithms
│   └── StartListHtmlRenderer.cshtml       - HTML rendering
├── Services/            - Precision-specific services (scoring, results, etc.)
└── Tests/               - Precision-specific tests
```

**Note:** View files for Precision document types are in `/Views/` (root), NOT in this folder. See the main CompetitionTypes README for details on view file organization and Umbraco conventions. Partial views can be organized in `/Views/Partials/Precision/`.

## What is Precision Competition?

Precision shooting competitions where:
- Shooters fire a set number of **series** (typically 3)
- Each series contains a set number of **shots** (typically 5 or 10)
- Each shot is scored from **0 to 10** (with 10 being "X" for inner ten)
- Results are calculated based on **total score** and **X-count** for tie-breaking

## Key Features

### Scoring System
- Decimal scoring (0.0 to 10.0 points per shot)
- Inner tens ("X") tracked separately for tie-breaking
- Series-based competition structure

### Start Lists
- Multiple team formats (mixed, separated by class, etc.)
- Configurable start times and intervals
- Class-based grouping options

### Registration
- Class-level registration (A1, B2, C3, etc.)
- Multiple class registration support
- Start time preferences

### Results
- Series-by-series tracking
- Real-time leaderboards
- Finals qualification calculation

## Controllers Architecture

### PrecisionStartListController
The main surface controller that handles HTTP requests. Now delegates to focused helper classes:

**Responsibilities:**
- HTTP request handling
- Response formatting
- Orchestration of helper classes

### StartListRequestValidator
Handles all validation logic:
- Competition ID validation
- Start list ID validation
- Generation request validation
- Permission checks (admin/manager roles)

### UmbracoStartListRepository
Data retrieval layer for Umbraco content:
- Fetching registrations from content tree
- Querying existing start lists
- Hub creation and retrieval
- Extracting team/shooter counts
- Member and club name resolution
- Qualification results retrieval

### StartListGenerator
Pure business logic for team generation:
- `GenerateStartListData()` - Main orchestrator
- `GenerateMixedTeams()` - Mixed weapon class teams
- `GenerateSeparatedTeamsWithClassOrder()` - One class per team
- `GenerateABCombinedTeamsWithClassOrder()` - A+B together, C separate
- `GenerateBCCombinedTeamsWithClassOrder()` - B+C together, A separate
- Sorting and deduplication logic

No dependencies on Umbraco services or HTTP—purely data transformation.

### StartListHtmlRenderer
HTML generation and rendering:
- `GenerateStartListHtml()` - Builds table content
- `BuildHtmlWrapper()` - Full HTML document with Bootstrap styling
- `BuildUserHighlightingScript()` - User/club highlighting JavaScript
- Helper methods for display formatting

## Benefits of This Architecture

1. **Single Responsibility** - Each class has one clear purpose
2. **Testability** - Logic classes have no external dependencies, easy to test
3. **Maintainability** - Easy to locate and modify specific functionality
4. **Reusability** - Helper classes can be used independently
5. **Scalability** - Easy to add new team generation algorithms or rendering options

## Related Files in Root

Some generic files in the root still support Precision:
- `Models/CompetitionResult.cs` - Stores Precision results as JSON
- `Views/Competition.cshtml` - Generic view with Precision sections

## See Also
- `/PRECISION_TYPE_MIGRATION_PLAN.md` - Migration documentation
- `/COMPETITION_TYPES_ARCHITECTURE.md` - Overall architecture
