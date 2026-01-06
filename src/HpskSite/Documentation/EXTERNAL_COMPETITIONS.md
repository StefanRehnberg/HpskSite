# External/Advertisement Competition System

**Status:** ✅ Implemented (Phase 6 Complete)
**Date:** 2025-11-30
**Architecture Decision:** Single document type with conditionals

---

## Overview

The HPSK competition system supports two types of competitions:

1. **Internal Competitions** - Full-featured competitions with registration, start lists, results, and invoices
2. **External Competitions (Advertisements)** - View-only listings that redirect to external websites/email for registration

Both types use the same `competition` document type, differentiated by the `isExternal` boolean property.

---

## Architecture Decision

### Decision: Single Document Type with Conditionals ⚠️

After evaluating both options (single doc type vs separate document types), we chose to proceed with a **single `competition` document type** with `isExternal` flag-based conditionals.

**Rationale:**
- Already implemented - no migration needed
- Content editors see unified competition list
- Competitions can be converted between types if needed
- Search/filtering works across both types
- Simpler content model (1 doc type vs 2)

**Trade-offs:**
- ~15 conditional checks across 4 files (within acceptable range)
- Property pollution (document type has some unused fields per type)
- Risk of "if/else hell" if more features added

**Threshold for Refactoring:** 20+ conditionals

**Decision Date:** 2025-11-30 (Phase 6)

---

## Document Type Properties

### Shared Properties (Both Types)
- `competitionName`, `description`, `venue`, `competitionDate`, `competitionEndDate`
- `competitionType`, `registrationOpenDate`, `registrationCloseDate`
- `shootingClassIds` (optional for external)
- `numberOfSeriesOrStations`, `numberOfFinalSeries`
- `isActive`

### External-Only Properties
- `isExternal` (True/False) - Marks competition as external/advertisement
- `externalUrl` (Textstring) - General competition info URL
- `externalRegistrationEmail` (Textstring) - Email for registration
- `invitationFile` (Media Picker) - PDF/Word invitation file

### Internal-Only Properties
- `registrationFee`, `swishNumber`, `maxParticipants`
- `competitionDirector`, `contactEmail`, `contactPhone`
- `managerIds`, `clubId`, `isClubOnly`
- `liveResultsEnabled`, and other management properties

---

## Conditional Locations in Codebase

### Phase 6 Implementation (15 conditionals)

#### 1. Views/Partials/CompetitionAdvertModal.cshtml
- **Line 173**: HTML form fields for series/final series (no conditional, always shown)
- **Line 348-351**: JavaScript validation for series count

#### 2. Controllers/CompetitionAdminController.cs
- **Line 654-673**: Series fields handling in CreateAdvertisement endpoint

#### 3. Views/Competition.cshtml (Main refactoring - 8 conditionals)
- **Line 367-417**: Header buttons section
  - Lines 368-385: External competition buttons (external URL + email)
  - Lines 387-416: Internal competition buttons (registration + management)

- **Line 464-468**: Hide max participants and registration fee for external competitions

- **Line 480-483**: Hide live results toggle for external competitions

- **Line 490-552**: Contact Information card (hidden for external)
  - Entire card wrapped in `@if (!isExternal)`

- **Line 613-777**: Information sidebar (hidden for external)
  - Entire sidebar wrapped in `@if (!isExternal)`
  - Lines 720-771: Internal sidebar buttons (already had conditionals)

- **Line 567-609**: Invitation file display (shown for both types)
  - Comment marker added, but no conditional change needed

#### 4. Views/CompetitionsHub.cshtml (from Phases 1-5)
- Badge display for external competitions
- Registration button logic

#### 5. Controllers/CompetitionController.cs (from Phases 1-5)
- RegisterForCompetition validation

#### 6. Controllers/CompetitionResultsController.cs (from Phases 1-5)
- Results entry validation

#### 7. Services/PaymentService.cs (from Phases 1-5)
- Invoice creation validation

---

## Conditional Count Summary

**Total Conditionals: 15** (across 7 files)

- Views: 10 conditionals (3 files)
- Controllers: 3 conditionals (2 files)
- Services: 1 conditional (1 file)
- JavaScript: 1 conditional (1 file)

**Status:** ✅ Within acceptable range (threshold: 20+)

---

## When to Consider Refactoring

Consider refactoring to separate document types if ANY of these conditions occur:

### 1. Conditional Proliferation (PRIMARY TRIGGER)
- ❌ Conditionals exceed 20 checks
- ❌ Conditionals become nested (more than 2 levels deep)
- ❌ Same conditional pattern repeated in many files

### 2. Feature Complexity
- ❌ External competitions gain complex unique features (e.g., external API integration)
- ❌ Different validation rules become too complex to manage in single code path
- ❌ Business logic diverges significantly between types

### 3. Maintenance Pain Points
- ❌ Property pollution becomes problematic for content editors
- ❌ Multiple bugs caused by admins setting wrong properties for external competitions
- ❌ Difficulty understanding code flow due to conditionals

### 4. Performance Issues
- ❌ Query performance degrades due to filtering on `isExternal` flag
- ❌ Index fragmentation on competition content type

---

## Maintenance Guidelines

### Adding New Features

**Step 1: Determine Scope**
- Does the feature apply to internal competitions only, external competitions only, or both?

**Step 2: Add Conditionals**
- If internal-only: Wrap in `@if (!isExternal)` or backend check
- If external-only: Wrap in `@if (isExternal)` or backend check
- If both: No conditional needed

**Step 3: Update Conditional Count**
- Update this document with new conditional location
- Increment conditional count
- Check if threshold (20+) is approaching

**Step 4: Add Comment Markers**
- Use consistent format:
  ```cshtml
  @* ======================================== INTERNAL COMPETITION ONLY: Feature Name ======================================== *@
  ```
- Or:
  ```cshtml
  @* ======================================== EXTERNAL COMPETITION ONLY: Feature Name ======================================== *@
  ```

### Code Organization Best Practices

1. **Group Related Conditionals**
   - Keep external/internal logic in clearly separated sections
   - Use comment markers to delineate boundaries

2. **Backend Validation**
   - Always validate `isExternal` flag in controllers/services
   - Never rely solely on UI hiding for security

3. **Testing**
   - Test both internal and external code paths
   - Test edge cases (missing properties, invalid states)

4. **Documentation**
   - Update this file when adding new conditionals
   - Document any special behavior for external competitions

---

## Migration Path (If Refactoring Needed)

If conditional count exceeds 20 or maintenance becomes problematic:

### Option A: Separate Document Types

**Estimated Effort:** 4-6 hours

**Steps:**
1. Create new `competitionAdvert` document type
2. Migrate content from `competition` where `isExternal = true`
3. Update all controllers to handle both document types
4. Update views to query both types
5. Update search/filtering logic
6. Test extensively

**Benefits:**
- Clean separation of concerns
- No property pollution
- Type-safe properties

**Drawbacks:**
- Cannot convert between types without migration
- Duplicate code for shared functionality
- More complex content model

### Option B: Extract to Services

**Estimated Effort:** 2-3 hours

**Steps:**
1. Create `ExternalCompetitionService`
2. Move external-specific logic to service
3. Reduce view conditionals
4. Keep single document type

**Benefits:**
- Cleaner views
- Better testability
- Keep single content model

**Drawbacks:**
- Still have property pollution
- Conditionals moved but not eliminated

---

## References

### Related Documentation
- [CLAUDE.md](../CLAUDE.md) - Project overview and architecture
- [COMPETITION_CONFIGURATION_GUIDE.md](COMPETITION_CONFIGURATION_GUIDE.md) - Competition setup guide
- [SWISH_PAYMENT_SETUP.md](SWISH_PAYMENT_SETUP.md) - Swish payment system

### Implementation Plan
- [shimmering-churning-muffin.md](../../.claude/plans/shimmering-churning-muffin.md) - Phase 6 implementation plan

### Key Commits
- Phase 1-5: External competition base implementation
- Phase 6 (2025-11-30): Series fields + UI refinements

---

**Last Updated:** 2025-11-30
**Status:** ✅ Phase 6 Complete
**Next Review:** When conditionals approach 18+ (currently: 15)
