# Test Plan: Standard Medal Calculation System

**Status:** POSTPONED - Awaiting manual testing and bug fixes
**Created:** 2025-11-14
**Priority:** High (implement after manual testing confirms system works correctly)

## Overview

This document outlines the comprehensive test plan for the Standard Medal Award calculation system for precision shooting competitions. The tests will be implemented using xUnit in a separate `HpskSite.Tests` project.

## Current Investigation Summary

### Test Project Status
- **No separate test project exists** (HpskSite.Tests.csproj needs to be created)
- Existing tests in `CompetitionTypes/Precision/Tests/PrecisionScoringTests.cs` use manual Console.WriteLine pattern
- Main project explicitly excludes Tests folder from compilation
- **No formal testing framework** installed (no xUnit, NUnit, or MSTest packages)

### Code Under Test

**Primary:**
- `CompetitionTypes/Precision/Services/StandardMedalCalculationService.cs` (301 lines)
  - Public methods: `CalculateStandardMedals()`, `ShouldSplitGroupC()`
  - Private methods tested through public interface (grouping, percentage, fixed score, best-of logic)

**Secondary:**
- `Controllers/CompetitionResultsController.cs` (integration point)
  - `CalculateFinalResults()` method (lines 1497-1606)
  - Medal calculation integration (lines 1574-1597)

### Critical Issue Found ⚠️

**Score Table Discrepancy:**
The fixed score values in `StandardMedalCalculationService.cs` (lines 210-228) **DO NOT MATCH** the documentation in `Standard Medal Award (Precision Shooting).md` (lines 44-49).

**Code Values:**
```
6 Series:  A(258B/270S), B(240B/258S), C(210B/234S)
7 Series:  A(301B/315S), B(280B/301S), C(245B/273S)
10 Series: A(430B/450S), B(400B/430S), C(350B/390S)
```

**Documentation Values:**
```
6 Series:  A(267B/277S), B(273B/282S), C(276B/283S)
7 Series:  A(312B/323S), B(319B/329S), C(322B/330S)
10 Series: A(445B/461S), B(455B/470S), C(460B/471S)
```

**Action Required:** Verify with SSF rules which values are correct before implementing tests.

---

## Test Implementation Plan

### Phase 1: Project Setup

#### 1.1 Create Test Project
```bash
# Create new xUnit test project
dotnet new xunit -n HpskSite.Tests -o HpskSite.Tests

# Add project reference
dotnet add HpskSite.Tests reference HpskSite
```

#### 1.2 Install NuGet Packages
```xml
<PackageReference Include="xunit" Version="2.4.2" />
<PackageReference Include="xunit.runner.visualstudio" Version="2.4.5" />
<PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.6.0" />
<PackageReference Include="FluentAssertions" Version="6.12.0" />
```

#### 1.3 Update Solution File
Add test project to `HpskSite.sln`

#### 1.4 Create Test Infrastructure Files
- `TestDataBuilders/ShooterResultBuilder.cs`
- `TestDataBuilders/StandardMedalConfigBuilder.cs`
- `TestData/CompetitionScenarios.cs`

---

### Phase 2: Test Coverage (Complete Coverage - ~78 Tests)

#### 2.1 Grouping Tests (~15 tests)
**File:** `Services/StandardMedalCalculationServiceTests.Grouping.cs`

**Weapon Group Extraction:**
- ✅ "A1" → Group A
- ✅ "A2 Dam" → Group A
- ✅ "B1" → Group B
- ✅ "B2 Vet Y" → Group B
- ✅ "C1" → Group C
- ✅ "C2 Dam" → Group C
- ✅ "Unknown" → Group C (default)
- ✅ Empty string → Group C (default)
- ✅ Null → Group C (default)

**Classification Extraction:**
- ✅ "C1 Dam" → "Dam"
- ✅ "B2 Jun" → "Jun"
- ✅ "A1 Vet Y" → "Vet Y"
- ✅ "C3 Vet Ä" → "Vet Ä"
- ✅ "A1" → null (open class)
- ✅ "C1 VETY" (case insensitive) → "Vet Y"
- ✅ "B2 VETÄ" (no space) → "Vet Ä"

**Competition Scope Split Logic:**
- ✅ "Svenskt Mästerskap" → ShouldSplitGroupC = true
- ✅ "Landsdelsmästerskap" → ShouldSplitGroupC = true
- ✅ "Kretsmästerskap" → ShouldSplitGroupC = false
- ✅ "Klubbmästerskap" → ShouldSplitGroupC = false
- ✅ Empty/null → ShouldSplitGroupC = false

**Group Formation:**
- ✅ Normal competition: A, B, C groups only
- ✅ SM/Landsdel: A, B, C-Dam, C-Jun, C-Vet Y, C-Vet Ä, C-Öppen

---

#### 2.2 Percentage Method Tests (~20 tests)
**File:** `Services/StandardMedalCalculationServiceTests.PercentageMethod.cs`

**Quota Calculations (Round DOWN):**
- ✅ 9 shooters → Silver quota = 1 (9/9 = 1)
- ✅ 27 shooters → Silver quota = 3 (27/9 = 3)
- ✅ 28 shooters → Silver quota = 3 (28/9 = 3.111 → 3)
- ✅ 30 shooters → Bronze quota = 10 (30/3 = 10)
- ✅ 31 shooters → Bronze quota = 10 (31/3 = 10.333 → 10)

**Award Logic:**
- ✅ Top 1/9 get Silver
- ✅ Top 1/3 get Bronze (if not already Silver)
- ✅ Silver overrides Bronze

**Tie Handling:**
- ✅ Last qualifying shooter: score 450, X=15
- ✅ Next shooter: score 450, X=15 → Also gets medal (tied)
- ✅ Next shooter: score 449, X=15 → No medal (not tied on score)
- ✅ Next shooter: score 450, X=14 → No medal (not tied on X-count)
- ✅ Multiple ties extending beyond quota
- ✅ All shooters tied (all get medals)

**Edge Cases:**
- ✅ 1 shooter → Silver quota = 0, Bronze quota = 0 (no medals)
- ✅ 2 shooters → Silver quota = 0, Bronze quota = 0 (no medals)
- ✅ 8 shooters → Silver quota = 0, Bronze quota = 2
- ✅ Sorting: Score DESC, then X-count DESC

---

#### 2.3 Fixed Score Method Tests (~15 tests)
**File:** `Services/StandardMedalCalculationServiceTests.FixedScore.cs`

**⚠️ Note:** Tests will use **current code values** until discrepancy is resolved.

**Score Table Tests (6 Series):**
- ✅ Group A, score 270 → Silver
- ✅ Group A, score 269 → Bronze
- ✅ Group A, score 258 → Bronze
- ✅ Group A, score 257 → None
- ✅ Group B, score 258 → Silver
- ✅ Group B, score 240 → Bronze
- ✅ Group C, score 234 → Silver
- ✅ Group C, score 210 → Bronze

**Score Table Tests (7 Series):**
- ✅ Group A, score 315 → Silver
- ✅ Group B, score 301 → Silver
- ✅ Group C, score 273 → Silver

**Score Table Tests (10 Series):**
- ✅ Group A, score 450 → Silver
- ✅ Group B, score 430 → Silver
- ✅ Group C, score 390 → Silver

**Edge Cases:**
- ✅ Unknown series count (11) → null (no medals)
- ✅ Exact threshold scores

---

#### 2.4 Best-of Logic Tests (~10 tests)
**File:** `Services/StandardMedalCalculationServiceTests.BestOfLogic.cs`

**Mixed Method Results:**
- ✅ Method A: Bronze, Method B: None → Bronze
- ✅ Method A: None, Method B: Bronze → Bronze
- ✅ Method A: Bronze, Method B: Bronze → Bronze
- ✅ Method A: Silver, Method B: Bronze → Silver
- ✅ Method A: Bronze, Method B: Silver → Silver
- ✅ Method A: Silver, Method B: Silver → Silver
- ✅ Method A: None, Method B: None → None

**Never Downgrade:**
- ✅ Already has Silver → Cannot be downgraded to Bronze
- ✅ Already has Bronze → Can be upgraded to Silver

---

#### 2.5 Integration Tests (~10 tests)
**File:** `Services/StandardMedalCalculationServiceTests.Integration.cs`

**Realistic Competition Scenarios:**

1. **Small Club Competition (8 shooters, 6 series):**
   - Group A: 3 shooters (quota: 0S, 1B)
   - Group B: 3 shooters (quota: 0S, 1B)
   - Group C: 2 shooters (quota: 0S, 0B)
   - Test: Fixed score method may award more medals than percentage

2. **Regional Championship (30 shooters, 7 series):**
   - Mixed groups: 10xA, 12xB, 8xC
   - Test: Percentage method likely awards more medals
   - Test: Ties at cutoff boundaries

3. **SM Championship (45 shooters, 7 series, Group C split):**
   - C groups: 5xDam, 6xJun, 4xVet Y, 3xVet Ä, 7xÖppen
   - Test: Each C subgroup calculated separately
   - Test: Different medal counts per subgroup

4. **Edge Case: 10+ Series (12 series):**
   - Test: Fixed score calculation fails (not in table)
   - Test: Only percentage method applies

5. **Finals Competition (6 qual + 3 finals = 9 total):**
   - Test: Only qualification series (6) count for medals
   - Test: Finals excluded from medal calculation

---

#### 2.6 Error Handling Tests (~8 tests)
**File:** `Services/StandardMedalCalculationServiceTests.ErrorHandling.cs`

- ✅ Null shooter list → No medals
- ✅ Empty shooter list → No medals
- ✅ Series count < 6 → No medals (BR-PS.1.2 rule)
- ✅ Series count = 6 → Valid
- ✅ Null config → No medals
- ✅ Shooter with no results → Score = 0, X = 0
- ✅ Invalid shooting class → Defaults to Group C

---

### Phase 3: Test Data Builders

#### ShooterResultBuilder.cs
Fluent API for creating test shooters with realistic data.

**Example Usage:**
```csharp
var shooter = new ShooterResultBuilder()
    .WithMemberId(1)
    .WithName("Test Shooter")
    .WithClub("Test Club")
    .WithShootingClass("B3")
    .WithSeries(score: 45, xCount: 3)
    .WithSeries(score: 47, xCount: 5)
    .Build();
```

**Methods:**
- `WithMemberId(int id)`
- `WithName(string name)`
- `WithClub(string club)`
- `WithShootingClass(string shootingClass)`
- `WithSeries(int score, int xCount)` - Adds a series result
- `WithSeriesFromShots(string[] shots)` - Adds series from shot array
- `Build()` - Returns PrecisionShooterResult

---

#### StandardMedalConfigBuilder.cs
Fluent API for creating test configurations.

**Example Usage:**
```csharp
var config = new StandardMedalConfigBuilder()
    .WithSeriesCount(6)
    .WithCompetitionScope("Svenskt Mästerskap")
    .Build();
```

**Methods:**
- `WithSeriesCount(int count)`
- `WithCompetitionScope(string scope)`
- `WithSplitGroupC(bool split)`
- `Build()` - Returns StandardMedalConfig

---

#### CompetitionScenarios.cs
Predefined realistic test data sets for integration tests.

**Methods:**
- `SmallClubCompetition()` - 8 shooters, 3 groups, 6 series
- `RegionalChampionship()` - 30 shooters, mixed groups, 7 series
- `SwedishChampionship()` - 45 shooters, C-class split, 7 series
- `FinalsCompetition()` - 12 shooters, 6 qual + 3 finals
- `LargeDataset()` - 100+ shooters for performance testing

---

## Project Structure

```
HpskSite.Tests/
├── HpskSite.Tests.csproj
├── Services/
│   ├── StandardMedalCalculationServiceTests.Grouping.cs          (~15 tests)
│   ├── StandardMedalCalculationServiceTests.PercentageMethod.cs  (~20 tests)
│   ├── StandardMedalCalculationServiceTests.FixedScore.cs        (~15 tests)
│   ├── StandardMedalCalculationServiceTests.BestOfLogic.cs       (~10 tests)
│   ├── StandardMedalCalculationServiceTests.Integration.cs       (~10 tests)
│   └── StandardMedalCalculationServiceTests.ErrorHandling.cs     (~8 tests)
├── TestDataBuilders/
│   ├── ShooterResultBuilder.cs
│   └── StandardMedalConfigBuilder.cs
└── TestData/
    └── CompetitionScenarios.cs
```

**Total Estimated Tests:** ~78 tests

---

## Implementation Steps (When Ready)

### Step 1: Resolve Score Table Discrepancy
1. Verify correct values with SSF official rules
2. Update either code or documentation to match
3. Document which values are authoritative

### Step 2: Create Test Project
```bash
cd C:\Repos\HpskSite
dotnet new xunit -n HpskSite.Tests -o HpskSite.Tests
dotnet sln HpskSite.sln add HpskSite.Tests/HpskSite.Tests.csproj
cd HpskSite.Tests
dotnet add reference ../HpskSite.csproj
dotnet add package FluentAssertions
```

### Step 3: Create Test Infrastructure
1. Create `TestDataBuilders/ShooterResultBuilder.cs`
2. Create `TestDataBuilders/StandardMedalConfigBuilder.cs`
3. Create `TestData/CompetitionScenarios.cs`

### Step 4: Implement Tests (Priority Order)
1. **Grouping Tests** - Foundation for all other tests
2. **Percentage Method Tests** - Core medal logic
3. **Fixed Score Method Tests** - Score table validation
4. **Best-of Logic Tests** - Integration between methods
5. **Error Handling Tests** - Edge cases and validation
6. **Integration Tests** - Realistic scenarios

### Step 5: Run Tests
```bash
cd HpskSite.Tests
dotnet test
```

### Step 6: Continuous Testing
Add to CI/CD pipeline (future enhancement)

---

## Test Naming Conventions

**Format:** `MethodName_Scenario_ExpectedResult`

**Examples:**
- `ExtractWeaponGroup_WithA1Class_ReturnsGroupA`
- `CalculateStandardMedals_With9Shooters_Awards1Silver`
- `ApplyPercentageMedals_WithTieAtCutoff_AwardsMedalToTiedShooters`
- `GetFixedScoreMedal_WithScore270InGroupA6Series_ReturnsSilver`
- `ShouldSplitGroupC_WithSwedishChampionship_ReturnsTrue`

---

## Testing Best Practices

### Arrange-Act-Assert Pattern
```csharp
[Fact]
public void CalculateStandardMedals_With9Shooters_Awards1Silver()
{
    // Arrange
    var shooters = new List<PrecisionShooterResult>
    {
        new ShooterResultBuilder().WithShootingClass("A1").WithTotalScore(450).Build(),
        new ShooterResultBuilder().WithShootingClass("A2").WithTotalScore(440).Build(),
        // ... 7 more shooters
    };
    var config = new StandardMedalConfigBuilder().WithSeriesCount(6).Build();
    var service = new StandardMedalCalculationService();

    // Act
    service.CalculateStandardMedals(shooters, config);

    // Assert
    shooters.Count(s => s.StandardMedal == "S").Should().Be(1);
    shooters[0].StandardMedal.Should().Be("S");
}
```

### Use FluentAssertions
```csharp
// Instead of:
Assert.Equal("S", shooter.StandardMedal);

// Use:
shooter.StandardMedal.Should().Be("S");
shooters.Should().HaveCount(9);
shooters.Count(s => s.StandardMedal == "B").Should().Be(3);
```

### Parameterized Tests with Theory
```csharp
[Theory]
[InlineData("A1", "A")]
[InlineData("B2", "B")]
[InlineData("C3", "C")]
[InlineData("Unknown", "C")]
[InlineData("", "C")]
public void ExtractWeaponGroup_WithVariousClasses_ReturnsExpectedGroup(
    string shootingClass, string expectedGroup)
{
    // Test implementation
}
```

---

## Known Issues & Considerations

### Issue 1: Score Table Discrepancy ⚠️
**Status:** UNRESOLVED
**Impact:** Fixed score tests may fail if using wrong values
**Action:** User will manually verify and fix before testing

### Issue 2: Finals Series Handling
**Current Implementation:** Uses `qualificationSeriesCount` (excludes finals)
**To Verify:** Confirm this matches SSF rules for medal calculation

### Issue 3: Group C Splitting Logic
**Current Implementation:** Only SM and Landsdel split C classes
**To Verify:** Confirm other championship types (Kretsmästerskap) use combined C

---

## Performance Considerations

**Expected Performance:**
- Small competition (8 shooters): <1ms
- Regional competition (30 shooters): <5ms
- Large competition (100+ shooters): <50ms

**Performance Tests:**
- Add `[Trait("Category", "Performance")]` to slow tests
- Measure execution time for large datasets
- Verify O(n log n) complexity (due to sorting)

---

## Documentation Updates Required

After test implementation:
1. Update `CLAUDE.md` with test project information
2. Update `README.md` with test running instructions
3. Create `Documentation/TESTING_GUIDE.md` with detailed testing procedures

---

## Success Criteria

**Tests Complete When:**
- ✅ All 78 tests implemented
- ✅ All tests passing (green)
- ✅ Code coverage > 90% for StandardMedalCalculationService
- ✅ Test data builders fully functional
- ✅ Integration tests cover realistic scenarios
- ✅ Documentation updated

---

## Related Documentation

- [Standard Medal Award (Precision Shooting).md](Standard%20Medal%20Award%20(Precision%20Shooting).md) - SSF rules
- [COMPETITION_RESULTS_WORKFLOW.md](COMPETITION_RESULTS_WORKFLOW.md) - Results calculation workflow
- [CLAUDE.md](../CLAUDE.md) - Project architecture and patterns

---

**Next Steps When Ready:**
1. ✅ User performs manual testing of Standard Medal system
2. ✅ User fixes any bugs found during manual testing
3. ✅ User resolves score table discrepancy
4. 🔄 Implement test project following this plan
5. 🔄 Run tests and verify all pass
6. 🔄 Update documentation

**Last Updated:** 2025-11-14
**Status:** READY TO IMPLEMENT (pending manual testing + bug fixes)
