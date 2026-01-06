# Precision Competition Type - Migration Plan

## Overview
This document identifies all files that need to be moved into the new CompetitionTypes structure for the Precision competition type. This is a comprehensive list for planning purposes only - **DO NOT IMPLEMENT YET**.

---

## Files to Move/Refactor

### 1. Models (7 files)

#### Core Models
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Models/CompetitionType.cs` | `CompetitionTypes/Common/CompetitionTypeBase.cs` | **REFACTOR** | Base class for all competition types. Needs to become abstract base |
| `Models/PrecisionStartList.cs` | `CompetitionTypes/Precision/Models/PrecisionStartList.cs` | **MOVE & RENAME** | Rename to match convention |
| `Models/FinalsStartList.cs` | `CompetitionTypes/Precision/Models/PrecisionFinalsStartList.cs` | **MOVE & RENAME** | Finals are Precision-specific currently |

#### Result Models (Precision-specific)
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Models/CompetitionResult.cs` | **ANALYZE FIRST** | **TBD** | Need to check if this is Precision-specific or generic |
| ~~`Models/CompetitionResultEntry.cs`~~ | **REMOVED** | **DELETED** | Replaced by PrecisionResultEntry table |

### 2. ViewModels (6 files)

#### Precision-Specific ViewModels
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Models/ViewModels/Competition/PrecisionResultsEntryViewModel.cs` | `CompetitionTypes/Precision/ViewModels/PrecisionResultsEntryViewModel.cs` | **MOVE** | Clear Precision naming |
| `Models/ViewModels/Competition/PrecisionSeries.cs` | `CompetitionTypes/Precision/ViewModels/PrecisionSeries.cs` | **MOVE** | Clear Precision naming |
| `Models/ViewModels/Competition/PrecisionTotal.cs` | `CompetitionTypes/Precision/ViewModels/PrecisionTotal.cs` | **MOVE** | Clear Precision naming |
| `Models/ViewModels/Competition/ShotEntryViewModel.cs` | `CompetitionTypes/Precision/ViewModels/PrecisionShotEntryViewModel.cs` | **MOVE & RENAME** | Rename to show Precision ownership |
| `Models/ViewModels/Competition/FinalsQualificationViewModel.cs` | `CompetitionTypes/Precision/ViewModels/PrecisionFinalsQualificationViewModel.cs` | **MOVE & RENAME** | Finals are Precision-specific |
| `Models/ViewModels/Competition/MixedTeamsGenerator.cs` | `CompetitionTypes/Precision/ViewModels/PrecisionMixedTeamsGenerator.cs` | **MOVE & RENAME** | Start list generation logic |

#### Generic ViewModels (May Need Interfaces)
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Models/ViewModels/Competition/StartListGenerationRequest.cs` | `CompetitionTypes/Common/ViewModels/IStartListGenerationRequest.cs` | **CREATE INTERFACE** | Create interface, keep Precision implementation |
| `Models/ViewModels/Competition/CompetitionRegistration.cs` | **ANALYZE FIRST** | **TBD** | Check if generic or needs Precision version |
| `Models/ViewModels/Competition/CompetitionOverviewViewModel.cs` | **ANALYZE FIRST** | **TBD** | Likely generic |
| `Models/ViewModels/Competition/CompetitionManagementViewModel.cs` | **ANALYZE FIRST** | **TBD** | Likely generic |

### 3. Controllers (3 files)

#### Precision-Specific Controllers
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Controllers/PrecisionResultsController.cs` | `CompetitionTypes/Precision/Controllers/PrecisionResultsController.cs` | **MOVE** | Clear Precision naming |
| `Controllers/StartListController.cs` | `CompetitionTypes/Precision/Controllers/PrecisionStartListController.cs` | **MOVE & RENAME** | Currently handles only Precision start lists |

#### Generic Controllers (Need Abstraction)
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Controllers/CompetitionController.cs` | **KEEP + REFACTOR** | **REFACTOR** | Needs to delegate to type-specific controllers |
| `Controllers/CompetitionResultsController.cs` | **ANALYZE FIRST** | **TBD** | Check if generic or needs Precision version |

### 4. Services (1 file)

#### Precision-Specific Services
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Services/FinalsQualificationService.cs` | `CompetitionTypes/Precision/Services/PrecisionFinalsQualificationService.cs` | **MOVE & RENAME** | Finals logic is Precision-specific |

### 5. Views (Multiple files)

#### Precision-Specific Views
| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Views/PrecisionStartList.cshtml` | `CompetitionTypes/Precision/Views/PrecisionStartList.cshtml` | **MOVE** | Clear Precision naming |
| `Views/Partials/Competition/PrecisionResultsEntry.cshtml` | `CompetitionTypes/Precision/Views/Partials/PrecisionResultsEntry.cshtml` | **MOVE** | Clear Precision naming |
| `Views/Partials/Competition/ShotEntry.cshtml` | `CompetitionTypes/Precision/Views/Partials/PrecisionShotEntry.cshtml` | **MOVE & RENAME** | Rename to show Precision ownership |

#### Generic Views (May Need Type-Specific Versions)
| Current Location | Action | Notes |
|-----------------|--------|-------|
| `Views/Competition.cshtml` | **ANALYZE** | May need type-specific rendering sections |
| `Views/CompetitionResult.cshtml` | **ANALYZE** | May need type-specific rendering |
| `Views/CompetitionManagement.cshtml` | **ANALYZE** | May need type-specific sections |
| `Views/CompetitionRegistration.cshtml` | **ANALYZE** | May need type-specific fields |

### 6. Tests (1 file)

| Current Location | New Location | Action | Notes |
|-----------------|--------------|--------|-------|
| `Tests/PrecisionScoringTests.cs` | `CompetitionTypes/Precision/Tests/PrecisionScoringTests.cs` | **MOVE** | Clear Precision naming |

---

## Files That Need Analysis

These files need to be examined to determine if they are:
- Generic (stay in root)
- Precision-specific (move to Precision folder)
- Need both generic interface and Precision implementation

### High Priority Analysis Needed
1. `Models/CompetitionResult.cs` - Check scoring logic
2. ~~`Models/CompetitionResultEntry.cs`~~ - **REMOVED** (replaced by PrecisionResultEntry)
3. `Models/ViewModels/Competition/CompetitionRegistration.cs` - Check fields
4. `Controllers/CompetitionResultsController.cs` - Check logic

---

## New Files to Create

### Common Interfaces (in CompetitionTypes/Common/)
1. `ICompetitionType.cs` - Base interface
2. `IRegistrationService.cs` - Registration handling
3. `IScoringService.cs` - Score calculation
4. `IResultsService.cs` - Results generation
5. `IStartListService.cs` - Start list generation
6. `CompetitionTypeFactory.cs` - Factory for creating type instances

### Precision Implementations (in CompetitionTypes/Precision/)
1. `PrecisionCompetitionType.cs` - Main type implementation
2. `Services/PrecisionRegistrationService.cs` - Registration logic
3. `Services/PrecisionScoringService.cs` - Scoring logic (extract from controllers)
4. `Services/PrecisionResultsService.cs` - Results logic
5. `Services/PrecisionStartListService.cs` - Start list logic (extract from controller)

---

## Migration Strategy

### Phase 1: Preparation
1. Create new folder structure (CompetitionTypes/Common and CompetitionTypes/Precision)
2. Create common interfaces
3. Document all dependencies

### Phase 2: Analyze & Document
1. Review each "ANALYZE FIRST" file
2. Document which files are truly generic vs Precision-specific
3. Update this migration plan with findings

### Phase 3: Create Base Infrastructure
1. Create CompetitionTypeBase abstract class
2. Create common interfaces
3. Create factory pattern implementation
4. Test with empty implementations

### Phase 4: Move Files (One at a Time)
1. Start with Models (no dependencies)
2. Then ViewModels
3. Then Services
4. Then Controllers
5. Finally Views
6. **Test after each file move**

### Phase 5: Extract & Refactor
1. Extract scoring logic from controllers to services
2. Extract start list generation to service
3. Implement all interfaces for Precision type
4. Test thoroughly

### Phase 6: Update References
1. Update all namespaces
2. Update all using statements
3. Update dependency injection registrations
4. Test entire application

### Phase 7: Verification
1. Run all existing tests
2. Manual testing of all Precision features
3. Verify no regressions

---

## Critical Rules During Migration

1. **ONE FILE AT A TIME** - Never move multiple files in one step
2. **TEST AFTER EVERY CHANGE** - Ensure Precision still works
3. **KEEP OLD CODE UNTIL NEW WORKS** - Don't delete until replacement is tested
4. **MAINTAIN BACKWARDS COMPATIBILITY** - Keep URLs and APIs working
5. **DOCUMENT EVERY DECISION** - Update this plan as we learn

---

## Namespace Convention

### Current
```csharp
HpskSite.Models
HpskSite.Models.ViewModels.Competition
HpskSite.Controllers
HpskSite.Services
```

### New Structure
```csharp
// Common
HpskSite.CompetitionTypes.Common
HpskSite.CompetitionTypes.Common.Interfaces
HpskSite.CompetitionTypes.Common.ViewModels

// Precision
HpskSite.CompetitionTypes.Precision
HpskSite.CompetitionTypes.Precision.Models
HpskSite.CompetitionTypes.Precision.ViewModels
HpskSite.CompetitionTypes.Precision.Controllers
HpskSite.CompetitionTypes.Precision.Services
HpskSite.CompetitionTypes.Precision.Views
HpskSite.CompetitionTypes.Precision.Tests
```

---

## Summary

### Total Files Identified
- **7** Model files (Precision-specific or need analysis)
- **6** ViewModel files (Precision-specific)
- **3** Controller files (Precision-specific or need refactoring)
- **1** Service file (Precision-specific)
- **3** View files (Precision-specific)
- **1** Test file (Precision-specific)
- **4+** Additional files need analysis

### New Files to Create
- **6** Common interface/base files
- **5+** Precision service implementations

### Estimated Migration Effort
- **Phase 1-2**: 2-4 hours (setup and analysis)
- **Phase 3**: 3-5 hours (base infrastructure)
- **Phase 4-5**: 8-12 hours (move and refactor files)
- **Phase 6-7**: 3-5 hours (update references and test)
- **Total**: 16-26 hours of careful, methodical work

---

## Next Steps

1. Review this plan
2. Discuss any concerns or modifications
3. Analyze the "ANALYZE FIRST" files
4. Begin Phase 1 when ready

**Remember: This is just the plan. Do not implement anything yet!**
