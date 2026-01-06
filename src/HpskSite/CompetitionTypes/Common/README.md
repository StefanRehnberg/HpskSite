# Competition Types - Common

This folder contains shared interfaces, base classes, and utilities used by all competition types.

## Structure

- **Interfaces/** - Common interfaces that all competition types implement
- **ViewModels/** - Shared ViewModels and base classes (to be added)
- **Services/** - Shared service interfaces (to be added)

## Purpose

The Common folder provides:
1. **Interfaces** that define contracts for competition types
2. **Base classes** that provide shared functionality
3. **Utilities** that all competition types can use

## Current Interfaces

### ICompetitionType
Base interface that all competition type implementations must implement.

**Properties:**
- `TypeName` - Unique identifier (e.g., "Precision")
- `DisplayName` - User-friendly name
- `Description` - Competition type description
- `IsActive` - Whether the type is available

## Adding New Competition Types

When adding a new competition type:
1. Create interface implementations in this folder
2. Create type-specific implementations in `CompetitionTypes/[YourType]/`
3. Follow the same folder structure as Precision

## See Also
- `/CompetitionTypes/Precision/` - Reference implementation
- `/COMPETITION_TYPES_ARCHITECTURE.md` - Architecture documentation
