# Training Match Handicap System

This document describes the handicap calculation system used in training matches.

## Overview

The handicap system allows shooters of different skill levels to compete fairly by applying a per-series handicap bonus (or penalty for elite shooters). The key principle is that handicaps are applied **per series** with a cap of 50 points per series, and rounding is deferred to the **final total** so that fractional handicaps accumulate correctly.

## Calculation Rules

### Per-Series Handicap Application

For each series in a match (as decimal, no rounding yet):

```
AdjustedSeriesScore = clamp(RawScore + HandicapPerSeries, 0, 50)
```

Then sum all adjusted series scores and round once:

```
FinalScore = Round(Sum of all AdjustedSeriesScores)
```

### Key Principles

1. **Raw scores are capped at 50** before handicap is applied (handles invalid data)
2. **Handicap is applied per series**, not to the total
3. **Each adjusted series is clamped between 0 and 50** (as decimal)
4. **Rounding happens only on the final total**, not per series - this ensures fractional handicaps (e.g. 1.25) accumulate correctly across series
5. **Rounding uses "Away from Zero"** (standard rounding, matches JavaScript `Math.round()`)

### Why Rounding at the End?

Rounding per-series causes fractional handicaps to lose precision on every series. For example, with a 1.25 handicap, the `.25` would be rounded away on each series, losing `0.25 × seriesCount` points total.

**Example with per-series rounding (OLD - WRONG):**
- Scores: 46, 48, 46, 47, 48, 45 (6 series)
- Handicap: 1.25 per series
- Per-series: 47.25→47, 49.25→49, 47.25→47, 48.25→48, 49.25→49, 46.25→46
- Total = **286** (lost 0.25 × 6 = 1.5 points to rounding)

**Example with final rounding (CORRECT):**
- Same scores and handicap
- Per-series (decimal): 47.25, 49.25, 47.25, 48.25, 49.25, 46.25
- Sum = 287.5 → Round = **288** (full handicap benefit preserved)

### Why Per-Series Capping?

The per-series capping ensures fairness. Without it, a high-scoring shooter could receive full handicap benefit even when their series scores are near-perfect.

**Example without per-series capping (OLD - WRONG):**
- Scores: 49, 46, 44, 46, 42, 48 (raw total: 275)
- Handicap: 3.0 per series (6 series)
- Calculation: 275 + (3 × 6) = 275 + 18 = **293**

**Example with per-series capping (CORRECT):**
- Same scores and handicap
- Per-series: 49+3=52→50, 46+3=49, 44+3=47, 46+3=49, 42+3=45, 48+3=51→50
- Sum = 290.0 → Round = **290**

The shooter "loses" 3 points because two series hit the 50 cap.

## Handicap Types

### Positive Handicap (Most Common)
Applied to shooters below average skill level to help them compete.

```
Score: 45, Handicap: +3.0
Adjusted: 45 + 3 = 48.0
```

### Zero Handicap
Applied to average shooters. No adjustment made.

```
Score: 45, Handicap: 0
Adjusted: 45
```

### Negative Handicap (Elite Shooters)
Applied to elite shooters who need to "give" points to others.

```
Scores: 49, 48, 50 (3 series), Handicap: -2.5
Per-series (decimal): 46.5, 45.5, 47.5
Sum = 139.5 → Round(139.5, AwayFromZero) = 140
```

With extreme negative handicap, series can clamp at 0:
```
Score: 5, Handicap: -10.0
Adjusted: 5 - 10 = -5 → 0 (clamped)
```

## Effective Handicap

The **effective handicap** is the actual points added/subtracted after clamping. This is calculated as `FinalScore - RawTotal`. It may differ from the **theoretical handicap** when series hit the 0 or 50 limits.

**Example:**
- Scores: 49, 46, 48 (3 series)
- Handicap: +3.0 per series
- Theoretical: 3 × 3 = 9 points
- Per-series (decimal): 49+3=52→50, 46+3=49, 48+3=51→50
- Sum = 149.0, Raw = 143
- Effective: 149 - 143 = **6 points** (3 points "lost" to cap)

## Code Implementation

### C# (Server-side) - ResultCalculator.cs

Location: `src/HpskSite.Shared/Services/ResultCalculator.cs`

```csharp
// Main calculation method
public static int CalculateAdjustedTotal<T>(
    IEnumerable<T> seriesScores,
    decimal handicapPerSeries,
    int? equalizedCount = null)
    where T : ISeriesScore
{
    var scores = GetEffectiveScores(seriesScores, equalizedCount).ToList();

    // Short-circuit for zero handicap
    if (handicapPerSeries == 0)
    {
        return scores.Sum(s => Math.Min(s.Total, MaxScorePerSeries));
    }

    // Apply handicap per series and clamp each between 0 and 50 (as decimal).
    // Accumulate the decimal sum across all series, then round only the final total.
    decimal total = 0;
    foreach (var s in scores)
    {
        var rawCapped = Math.Min(s.Total, MaxScorePerSeries);
        var adjusted = rawCapped + handicapPerSeries;
        var clamped = Math.Clamp(adjusted, 0, (decimal)MaxScorePerSeries);
        total += clamped;
    }
    return (int)Math.Round(total, StandardRounding);
}

// Calculate effective handicap applied
public static decimal CalculateEffectiveHandicap<T>(
    IEnumerable<T> seriesScores,
    decimal handicapPerSeries,
    int? equalizedCount = null)
    where T : ISeriesScore
{
    // Effective handicap = adjusted total - raw total
    // This ensures consistency with CalculateAdjustedTotal.
    var scoresList = seriesScores.ToList();
    int adjusted = CalculateAdjustedTotal(scoresList, handicapPerSeries, equalizedCount);
    int raw = CalculateRawTotal(scoresList, equalizedCount);
    return adjusted - raw;
}
```

### JavaScript (Client-side) - TrainingMatchScoreboard.cshtml

Location: `src/HpskSite/Views/Partials/TrainingMatchScoreboard.cshtml`

```javascript
function calculateAdjustedTotalWithCap(scores, handicapPerSeries) {
    // Short-circuit for zero handicap
    if (handicapPerSeries === 0) {
        const rawTotal = scores.reduce((sum, s) => sum + Math.min(s.total, 50), 0);
        return { total: rawTotal, effectiveHandicap: 0 };
    }

    let rawTotal = 0;
    let decimalTotal = 0;
    for (const s of scores) {
        const rawCapped = Math.min(s.total, 50);
        rawTotal += rawCapped;
        const adjusted = rawCapped + handicapPerSeries;
        // Clamp between 0 and 50 as decimal (no per-series rounding)
        const clamped = Math.max(0, Math.min(adjusted, 50));
        decimalTotal += clamped;
    }
    // Round only the final total
    const roundedTotal = Math.round(decimalTotal);
    return { total: roundedTotal, effectiveHandicap: roundedTotal - rawTotal };
}
```

### Controller (API) - TrainingMatchController.cs

Location: `src/HpskSite/Controllers/TrainingMatchController.cs`

The leaderboard calculation in `GetMatchHistory` uses the same per-series clamping logic with final-total rounding.

## Single Source of Truth Architecture

**CRITICAL PRINCIPLE:** All handicap calculations MUST use `ResultCalculator` as the single source of truth. This ensures consistency across all platforms and prevents calculation discrepancies.

### Why This Matters

The handicap calculation involves several nuances:
- Per-series capping (0-50 range as decimal)
- Final-total rounding (`MidpointRounding.AwayFromZero`)
- Order of operations (cap raw score → add handicap → clamp as decimal → sum → round final total)

Having multiple implementations leads to subtle bugs that are hard to debug.

### Server-Side: Always Use ResultCalculator

All server-side calculations MUST call `ResultCalculator` methods:

```csharp
using HpskSite.Shared.Services;

// Correct: Use ResultCalculator directly
int adjustedTotal = ResultCalculator.CalculateAdjustedTotal(seriesList, handicapPerSeries);
```

### Client-Side: Use Server-Calculated Values

For historical/completed matches displayed in JavaScript, **always use the server-calculated values** rather than recalculating on the client.

**Correct Pattern (use server values):**
```javascript
function getFinalScore(participant) {
    // Server provides finalScore calculated by ResultCalculator.CalculateAdjustedTotal()
    if (match.hasHandicap && participant.finalScore !== null && participant.finalScore !== undefined) {
        return participant.finalScore;
    }
    return participant.totalScore ?? 0;
}
```

**Wrong Pattern (client recalculation):**
```javascript
// DON'T recalculate on client for historical matches
function calculateFinalScore(participant) {
    // This could diverge from server calculation!
    return participant.totalScore + (handicapPerSeries * seriesCount);
}
```

### Exception: Live Scoreboard

The **live scoreboard** (`TrainingMatchScoreboard.cshtml`) is the only place where client-side calculation is acceptable. This is because:

1. Real-time updates require immediate recalculation
2. Server hasn't saved/calculated the values yet
3. The JavaScript implementation **exactly mirrors** `ResultCalculator`

The live scoreboard's `calculateAdjustedTotalWithCap()` function must remain synchronized with `ResultCalculator.CalculateAdjustedTotal()`. Any changes to the calculation algorithm must be updated in **both** locations.

### API Response Contract

API endpoints that return match data MUST include:
- `totalScore` - Raw total (sum of series without handicap)
- `finalScore` - Adjusted total (calculated by `ResultCalculator.CalculateAdjustedTotal()`)
- `handicapTotal` - Effective handicap applied (`finalScore - totalScore`)

Controllers that return this data:
- `TrainingMatchController.GetLeaderboard()`
- `TrainingMatchController.ViewMatchAsSpectator()`
- `MatchApiController.EnrichMatchHistoryItems()`

### Deprecated Methods

The following method is deprecated and should NOT be used:

```csharp
// DEPRECATED - assumes average distribution across series
[Obsolete("Use CalculateAdjustedTotal<T> for accurate per-series capping")]
int CalculateAdjustedMatchTotal(int rawTotal, decimal handicapPerSeries, int seriesCount);
```

Always use the generic `ResultCalculator.CalculateAdjustedTotal<T>()` that accepts `IEnumerable<ISeriesScore>`.

## Display Format

### Scoreboard Total Row

For matches with handicap enabled, the total row shows:
- **Final Score** (adjusted, in yellow/gold)
- **Breakdown**: Raw score + effective handicap (e.g., "275 +15")
- **Series count** and X-count

Example display:
```
290          <- Final adjusted score (yellow)
275 +15      <- Raw score + effective handicap applied
6 serier | 12x
```

### Participant Header

Shows handicap per series:
```
Stefan
+3.00        <- Handicap per series (green badge)
(P)          <- Provisional badge if fewer than 8 matches
```

## Edge Cases

| Scenario | Behavior |
|----------|----------|
| Raw score > 50 | Capped at 50 before handicap applied |
| Handicap = 0 | Returns raw total (optimized path) |
| Positive handicap exceeds 50 | Capped at 50 per series (as decimal) |
| Negative handicap below 0 | Clamped at 0 per series (as decimal) |
| Fractional handicap (e.g. 1.25) | Accumulates across series, rounded only on final total |
| Empty scores | Returns 0 |
| Invalid input (null) | Returns 0 |

## Testing

Unit tests are in `src/HpskSite.Tests/ResultCalculatorTests.cs`.

Key test cases:
- `PerSeriesCapping_Example1_HighScoringShooterWith3Handicap`
- `PerSeriesCapping_Example2_HighScoringShooterWith175Handicap`
- `PerSeriesCapping_AllPerfectScores_ZeroEffectiveHandicap`
- `EdgeCase_ZeroHandicap_ReturnsRawTotal`
- `EdgeCase_NegativeHandicap_PartialClampingAtZero`
- `EdgeCase_RawScoresOver50_AreCappedBeforeHandicap`

Run tests:
```bash
dotnet test --filter "FullyQualifiedName~ResultCalculatorTests"
```

## Cross-Platform Consistency

The calculation is implemented identically in:
1. **C# (Server)** - `ResultCalculator.cs`
2. **JavaScript (Web)** - `TrainingMatchScoreboard.cshtml`
3. **C# (Mobile via Shared)** - Uses `ResultCalculator.cs`

All implementations use:
- Per-series clamping between 0 and 50 (as decimal)
- Final-total rounding only (away from zero)
- Same order of operations: cap raw → add handicap → clamp decimal → sum → round total

## History

- **2026-02-15**: Fixed fractional handicap rounding - rounding now deferred to final total
  - Previously rounded per-series, causing fractional handicaps (e.g. 1.25) to lose precision
  - Fixed in `ResultCalculator.CalculateAdjustedTotal()`, `CalculateEffectiveHandicap()`, `CalculateAdjustedMatchTotal()`
  - Fixed inline calculation in `TrainingMatchController.GetMatchHistory()`
  - Fixed `MatchApiController.EnrichMatchHistoryItems()` to use `ResultCalculator.RoundToQuarter()`
  - Fixed JavaScript `calculateAdjustedTotalWithCap()` in `TrainingMatchScoreboard.cshtml`
  - Updated all unit tests with correct expected values
- **2026-02-05**: Established single-source-of-truth architecture - all calculations must use `ResultCalculator`
  - Fixed `TrainingMatchController.GetLeaderboard()` and `ViewMatchAsSpectator()` to use `ResultCalculator.CalculateAdjustedTotal()`
  - Fixed `MatchApiController.EnrichMatchHistoryItems()` to use `ResultCalculator`
  - Updated `TrainingMatch.cshtml` to use server-calculated values instead of client-side recalculation
  - Added new `IHandicapCalculator.GetMatchFinalScore(IEnumerable<ISeriesScore>, decimal)` method
  - Marked old `GetMatchFinalScore(decimal, decimal, int)` as obsolete
- **2026-01-24**: Implemented per-series handicap capping (replaced old total-based calculation)
- **Previous**: Used `FinalScore = RawTotal + (HandicapPerSeries × SeriesCount)` with total cap only

---

**See Also:**
- `TRAINING_SCORING_SYSTEM.md` - Training scoring system (different from training matches)
- `COMPETITION_RESULTS_WORKFLOW.md` - Competition results workflow
