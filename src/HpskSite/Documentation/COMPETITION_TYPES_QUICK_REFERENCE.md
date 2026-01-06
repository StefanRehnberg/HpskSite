# Competition Types Architecture - Quick Reference

## TL;DR - The Recommendation

**Use unique namespaces for all types + shared utilities.**

```
✅ Each type in separate namespace (HpskSite.CompetitionTypes.Precision, .Milsnabb, etc.)
✅ Each type implements 5 core services independently (Registration, StartList, Scoring, Results, Edit)
✅ Share only pure utility functions (no business logic)
✅ Use inheritance only for Duell (identical to Precision)
⚠️ Some code duplication is acceptable for independence
```

## The 5 Core Services Every Type Must Implement

```csharp
namespace HpskSite.CompetitionTypes.[TypeName]
{
    public class [Type]RegistrationService : IRegistrationService { }
    public class [Type]StartListService : IStartListService { }
    public class [Type]ScoringService : IScoringService { }
    public class [Type]ResultsService : IResultsService { }
    public class [Type]CompetitionEditService : ICompetitionEditService { }
}
```

## Shared Utilities (No Business Logic, Pure Functions)

Located in `CompetitionTypes/Common/Utilities/`:

| Utility | Purpose | Used By |
|---------|---------|---------|
| **ScoringUtilities** | Convert shots to points (X=10, etc.) | All types |
| **RankingUtilities** | Ranking and tie-breaking logic | All types |
| **ValidationUtilities** | Input validation rules | All types |
| **ExportUtilities** | CSV, HTML, PDF export | All types |
| **DateTimeUtilities** | Date formatting | All types |

## Type Implementation Patterns

### Single-Part (Precision, Duell)
- One series of shots = one result entry
- Total = sum of all shots
- Straightforward implementation

### Multi-Part (Milsnabb=4 parts, Helmatch=3 parts)
- Multiple series grouped by "part"
- Each part has separate score
- Total = sum of all part scores
- Display shows breakdown by part

### Completely Different (FieldShooting, Springskytte)
- Own start list algorithm
- Own scoring rules
- Own results format
- Implement from scratch using utilities

## Folder Structure

```
CompetitionTypes/
├── Common/
│   ├── Interfaces/                (5 core interfaces)
│   └── Utilities/                 (Shared utilities)
├── Precision/                     (Reference implementation)
│   ├── Services/ (5 services)
│   ├── Models/
│   ├── ViewModels/
│   └── Controllers/
├── Duell/                         (Extends Precision)
│   └── Services/ (extend Precision's)
├── Milsnabb/                      (Independent, 4-part)
│   ├── Services/ (5 independent)
│   └── Models/ (MilsnabbPartResult, etc.)
├── NationellHelmatch/             (Independent, 3-part)
│   └── (Similar to Milsnabb)
├── FieldShooting/                 (Independent, different)
│   └── Services/ (5 independent)
└── Springskytte/                  (Independent, different)
    └── Services/ (5 independent)
```

## Namespace Convention

```csharp
HpskSite.CompetitionTypes.Common.Interfaces
HpskSite.CompetitionTypes.Common.Utilities

HpskSite.CompetitionTypes.Precision.Services
HpskSite.CompetitionTypes.Precision.Models
HpskSite.CompetitionTypes.Precision.ViewModels
HpskSite.CompetitionTypes.Precision.Controllers

// Other types follow same pattern
HpskSite.CompetitionTypes.Milsnabb.Services
HpskSite.CompetitionTypes.FieldShooting.Services
```

## When to Use Inheritance (Only Duell)

```csharp
// Duell is identical to Precision, so use inheritance
public class DuellScoringService : PrecisionScoringService { }
public class DuellResultsService : PrecisionResultsService { }
```

**When to use inheritance:**
- Type is completely identical to another type
- Changes to original should affect derivative
- Not recommended for anything else

## When to Use Shared Utilities (All Types)

```csharp
// Good - All types use this
public decimal CalculateSeriesTotal(List<string> shots)
{
    return ScoringUtilities.CalculateTotal(shots);  // Utility function
}

// Bad - Don't do this
public decimal CalculateSeriesTotal(List<string> shots)
{
    return _precisionService.CalculateSeries(shots); // Service coupling!
}
```

## When to Implement Independently (All Types)

```csharp
// Each type implements this independently
public class MilsnabbScoringService : IScoringService
{
    // Milsnabb-specific implementation
    // Doesn't inherit from Precision
    // Doesn't call Precision service
}

public class FieldShootingScoringService : IScoringService
{
    // Completely different rules
    // No connection to Precision
}
```

## Implementation Priority

### Phase 1: Utilities (Weeks 1-2)
- Extract ScoringUtilities
- Extract RankingUtilities
- Extract ValidationUtilities, ExportUtilities, DateTimeUtilities
- Unit test utilities
- Refactor Precision to use utilities
- Verify nothing broke

### Phase 2: Duell (1 day)
- Create services extending Precision

### Phase 3: Milsnabb (1-2 weeks)
- Create all 5 services independently
- Handle 4-part aggregation
- Test with real data

### Phase 4: Helmatch (3-4 days)
- Similar to Milsnabb, 3 parts
- Faster because pattern established

### Phase 5: Future (Later)
- FieldShooting (1-2 weeks)
- Springskytte (1-2 weeks)

## File Naming Rules

```
✅ PrecisionScoringService.cs       (Prefix with type)
✅ MilsnabbResultsService.cs
✅ FieldShootingStartListService.cs

❌ ScoringService.cs                (Too generic, unclear ownership)
❌ ResultsService.cs
❌ StartListService.cs
```

## Testing Strategy

```
CompetitionTypes/
├── Precision/
│   └── Tests/
│       └── PrecisionScoringTests.cs          (Type-specific tests)
├── Common/
│   └── Tests/
│       ├── ScoringUtilitiesTests.cs          (Utility tests)
│       └── RankingUtilitiesTests.cs
└── Milsnabb/
    └── Tests/
        └── MilsnabbResultsTests.cs           (Type-specific tests)
```

## Key Principles

1. **Independence First** - Even duplicated code is OK for independence
2. **Utilities Don't Couple** - Pure functions can be shared safely
3. **One Namespace = One Type** - Types don't reference each other
4. **Consistent Patterns** - All types implement same 5 interfaces
5. **Precision as Reference** - Look at Precision to understand the pattern

## Common Mistakes to Avoid

❌ **Inheritance chains**
```csharp
// DON'T: MilsnabbResultsService extends PrecisionResultsService
// DO: Implement independently
```

❌ **Service-to-Service calls**
```csharp
// DON'T: Call another type's service
_precisionService.CalculateTotal(shots);

// DO: Use utilities
ScoringUtilities.CalculateTotal(shots);
```

❌ **Shared business logic**
```csharp
// DON'T: Common base class with scoring logic
public abstract class CompetitionBase { virtual CalculateScore() }

// DO: Utility functions
ScoringUtilities.CalculateTotal(shots)
```

❌ **Mixing models**
```csharp
// DON'T: Share models between types
PrecisionResultEntry usedByBothTypes;

// DO: Type-specific models
MilsnabbResultEntry
FieldShootingResultEntry
```

## Questions? See Full Documentation

- **Detailed Architecture**: `COMPETITION_TYPES_ARCHITECTURE_GUIDE.md`
- **Implementation Steps**: `COMPETITION_TYPES_IMPLEMENTATION_PLAN.md`
- **Decision Rationale**: `ARCHITECTURE_DECISION_SUMMARY.md`
- **Original Plan**: `COMPETITION_TYPES_ARCHITECTURE.md`

## Quick Links

**Create a new competition type:**
1. Read: COMPETITION_TYPES_ARCHITECTURE_GUIDE.md → "Adding a New Competition Type" section
2. Follow: COMPETITION_TYPES_IMPLEMENTATION_PLAN.md for exact steps

**Understand design decisions:**
1. Read: ARCHITECTURE_DECISION_SUMMARY.md → Full context and rationale

**See examples:**
1. Look at: `/CompetitionTypes/Precision/` (Reference implementation)
2. Or: `/CompetitionTypes/Milsnabb/` (Multi-part example)
