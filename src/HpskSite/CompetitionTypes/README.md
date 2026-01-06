# Competition Types Structure

This folder contains all competition type implementations following a clean architecture pattern.

## Overview

Each competition type (Precision, Rapid Fire, etc.) has its own isolated folder with complete implementation including models, controllers, services, and views.

## Structure

```
CompetitionTypes/
├── Common/                    # Shared interfaces and base classes
│   ├── Interfaces/            # Common interfaces
│   ├── ViewModels/            # Shared ViewModels (to be added)
│   └── Services/              # Shared service interfaces (to be added)
│
└── Precision/                 # Precision competition type
    ├── Models/                # Precision models
    ├── ViewModels/            # Precision ViewModels
    ├── Controllers/           # Precision controllers
    ├── Services/              # Precision services
    └── Tests/                 # Precision tests
```

**Note:** View files are NOT included in the CompetitionTypes folder structure due to Umbraco conventions. See "View File Organization" section below.

## Adding a New Competition Type

To add a new competition type (e.g., "RapidFire"):

1. **Create folder structure:**
   ```
   CompetitionTypes/RapidFire/
   ├── Models/
   ├── ViewModels/
   ├── Controllers/
   ├── Services/
   └── Tests/
   ```

   **Important:** Do NOT create a Views/ subfolder here. View files must be placed in the root `/Views/` folder following Umbraco's document type conventions (see View File Organization section).

2. **Implement common interfaces:**
   - Implement `ICompetitionType`
   - Implement other interfaces from `Common/Interfaces/`

3. **Create type-specific implementations:**
   - Models for your competition type
   - Services for scoring, results, etc.
   - Controllers for handling requests
   - Views in `/Views/` folder (NOT in CompetitionTypes - see View File Organization)

4. **Follow naming conventions:**
   - Prefix all classes with type name (e.g., `RapidFireController`)
   - Use namespace `HpskSite.CompetitionTypes.RapidFire.*`

## View File Organization

### Umbraco Document Type Template Conventions

**Important:** Unlike Controllers, Models, and Services which can be organized in the CompetitionTypes folder structure, **View files MUST follow Umbraco's conventions** and live in specific locations.

### Where View Files Must Live

#### Document Type Templates → `/Views/` (Root)
Umbraco's routing system requires document type templates to be in the root Views folder:

```
/Views/
├── PrecisionStartList.cshtml       ← Document type template for "precisionStartList"
├── PrecisionResults.cshtml         ← Document type template for "precisionResults"
├── Competition.cshtml              ← Document type template for "competition"
└── CompetitionSeries.cshtml        ← etc.
```

**Naming Convention:** Document type alias `precisionStartList` maps to `Views/PrecisionStartList.cshtml` (PascalCase).

**Why:** Umbraco's template resolution engine looks for an exact match in the `/Views/` folder. It does NOT search subfolders for document type templates.

#### Partial Views → `/Views/Partials/` (Can Use Subfolders)
Partial views can be organized in subfolders since they're referenced explicitly:

```
/Views/Partials/
├── Precision/                      ← Type-specific partials
│   ├── StartListTable.cshtml
│   ├── ResultsEntry.cshtml
│   └── FinalsScoreboard.cshtml
├── Competition/                    ← Generic competition partials
│   ├── ManagementDashboard.cshtml
│   └── RegistrationForm.cshtml
└── TrainingScoreEntry.cshtml       ← Shared partials
```

**Usage in Views:**
```csharp
@await Html.PartialAsync("~/Views/Partials/Precision/StartListTable.cshtml", Model)
```

### Why This Architecture Limitation Exists

1. **Umbraco Platform Constraint:** Umbraco's document type → template mapping is a core platform feature that expects templates in `/Views/`
2. **No Custom View Location Configuration:** The project doesn't implement a custom `IViewLocationExpander`
3. **Standard ASP.NET Core MVC:** Umbraco follows standard MVC view resolution which doesn't recursively search subfolders

### Best Practices

**✅ DO:**
- Place document type templates in `/Views/` root
- Name templates to match document type alias (PascalCase)
- Organize partial views in `/Views/Partials/` subfolders by competition type
- Keep templates thin - delegate rendering to type-specific partials
- Use explicit paths when referencing partials

**❌ DON'T:**
- Create `CompetitionTypes/Precision/Views/` folders (not used by Umbraco)
- Try to organize document type templates in subfolders (won't work)
- Assume Umbraco will find views in nested folders (it won't)

### Example: Adding a RapidFire Competition Type

**Backend Code (CompetitionTypes folder):**
```
CompetitionTypes/RapidFire/
├── Controllers/RapidFireController.cs       ✅ Can be nested
├── Services/RapidFireService.cs             ✅ Can be nested
└── Models/RapidFireConfig.cs                ✅ Can be nested
```

**Frontend Views (Views folder):**
```
Views/
├── RapidFireCompetition.cshtml              ✅ Document type template (root)
└── Partials/
    └── RapidFire/                           ✅ Partials (can be nested)
        ├── ShotTimer.cshtml
        └── TargetDisplay.cshtml
```

### Impact on Architecture

This means the Competition Types architecture is **partially isolated**:
- ✅ **Backend:** Controllers, Services, Models, Tests are fully isolated in CompetitionTypes folders
- ⚠️ **Frontend:** Document type templates must live in `/Views/` root (Umbraco limitation)
- ✅ **Partials:** Can be organized by type in `/Views/Partials/` subfolders

The benefit is that you still get clean separation of backend logic while respecting Umbraco's platform conventions.

## Design Principles

1. **Isolation** - Each type is completely self-contained (backend code)
2. **No Breaking Changes** - Adding new types doesn't affect existing types
3. **Interface-Based** - All types implement common interfaces
4. **Factory Pattern** - Use factories to create type-specific instances
5. **Type Safety** - Strong typing throughout
6. **Umbraco Conventions** - View files follow platform requirements (not negotiable)

## Competition Data Editing Architecture

### Overview
Competition details are edited via a modal-based interface for optimal UX across all devices. The editing system is type-agnostic and works with all competition types.

### UX Pattern: Card-Based Edit Modal

- **Read-only card display** - Competition details shown in clean card format
- **"Redigera" button** - Opens dedicated modal with edit form
- **Form organization** - Fields grouped by sections (Basic Info, Registration Settings, Configuration)
- **Mobile-optimized** - Full-screen modal works seamlessly on all screen sizes
- **Type-aware** - Routes to appropriate competition type for saving

### Architecture

**Base Controller:**
- Location: `HpskSite/Controllers/CompetitionEditController.cs`
- Endpoint: `POST /umbraco/surface/CompetitionEdit/SaveCompetition`
- Handles: Request routing, type detection, response formatting
- Parameters: `competitionId`, `competitionType`, field data

**Type-Specific Services:**
- Location: `HpskSite/CompetitionTypes/[Type]/Services/[Type]CompetitionEditService.cs`
- Responsibility: 
  - Validate type-specific fields
  - Apply type-specific business rules
  - Save to Umbraco using content service
  - Return validation results or success response

**Example: Precision Type**
```
HpskSite.CompetitionTypes.Precision/Services/PrecisionCompetitionEditService.cs
```

### Implementation Steps

1. **Create base controller** to handle cross-cutting concerns
2. **Create type-specific edit services** in each competition type folder
3. **Implement validation** for each type's specific fields
4. **Handle Umbraco saves** through content service API
5. **Return structured responses** for client-side handling

### Adding Edit Support for a New Type

1. Create `[Type]CompetitionEditService.cs` in your type's Services folder
2. Implement field validation and Umbraco save logic
3. Register service in dependency injection if needed
4. Base controller will automatically route to your service

### Data Flow

```
Client Modal Form
    ↓
POST /umbraco/surface/CompetitionEdit/SaveCompetition
    ↓
CompetitionEditController (routes by type)
    ↓
PrecisionCompetitionEditService.SaveCompetition()
    ↓
Validate fields
    ↓
Update Umbraco content via ContentService
    ↓
Return success/error response
    ↓
Client refreshes data and closes modal
```

## Current Implementation Status

- ✅ **Precision** - Fully implemented with refactored controller architecture
  - `PrecisionStartListController` - HTTP request handling
  - `StartListRequestValidator` - Validation logic
  - `UmbracoStartListRepository` - Data retrieval
  - `StartListGenerator` - Team generation algorithms
  - `StartListHtmlRenderer` - HTML rendering
- 🔄 **Common** - Base interfaces created
- 🔄 **Competition Editing** - Base controller and type-agnostic infrastructure
- ⏳ **Other Types** - To be added as needed

## Documentation

- See `/COMPETITION_TYPES_ARCHITECTURE.md` for full architecture
- See `/PRECISION_TYPE_MIGRATION_PLAN.md` for migration details
- See individual type READMEs for type-specific information

## Benefits

1. **Easy to add new competition types** without touching existing code
2. **Clear ownership** - all code for a type lives in one place
3. **Safe refactoring** - changes to one type don't affect others
4. **Better testing** - each type can be tested in isolation
5. **Team collaboration** - different developers can work on different types
6. **Extensible editing** - Add edit support for new types with minimal effort
