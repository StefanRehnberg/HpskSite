# Training Match Handicap System

This document describes the handicap calculation system used in training matches.

## Overview

The handicap system allows shooters of different skill levels to compete fairly by applying a per-series handicap bonus (or penalty for elite shooters). The key principle is that handicaps are applied **per series** with a cap of 50 points per series.

## Calculation Rules

### Per-Series Handicap Application

For each series in a match:

```
AdjustedSeriesScore = clamp(RawScore + HandicapPerSeries, 0, 50)
```

Then sum all adjusted series scores:

```
FinalScore = Sum of all AdjustedSeriesScores
```

### Key Principles

1. **Raw scores are capped at 50** before handicap is applied (handles invalid data)
2. **Handicap is applied per series**, not to the total
3. **Each adjusted series is clamped between 0 and 50**
4. **Rounding uses "Away from Zero"** (standard rounding, matches JavaScript `Math.round()`)

### Why Per-Series Capping?

The per-series capping ensures fairness. Without it, a high-scoring shooter could receive full handicap benefit even when their series scores are near-perfect.

**Example without per-series capping (OLD - WRONG):**
- Scores: 49, 46, 44, 46, 42, 48 (raw total: 275)
- Handicap: 3.0 per series (6 series)
- Calculation: 275 + (3 × 6) = 275 + 18 = **293**

**Example with per-series capping (NEW - CORRECT):**
- Same scores and handicap
- Per-series: 49+3=52→50, 46+3=49, 44+3=47, 46+3=49, 42+3=45, 48+3=51→50
- Final: 50 + 49 + 47 + 49 + 45 + 50 = **290**

The shooter "loses" 3 points because two series hit the 50 cap.

## Handicap Types

### Positive Handicap (Most Common)
Applied to shooters below average skill level to help them compete.

```
Score: 45, Handicap: +3.0
Adjusted: 45 + 3 = 48
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
Score: 49, Handicap: -2.5
Adjusted: 49 - 2.5 = 46.5 → 47 (rounded)
```

With extreme negative handicap, series can clamp at 0:
```
Score: 5, Handicap: -10.0
Adjusted: 5 - 10 = -5 → 0 (clamped)
```

## Effective Handicap

The **effective handicap** is the actual points added/subtracted after clamping. This may differ from the **theoretical handicap** when series hit the 0 or 50 limits.

**Example:**
- Scores: 49, 46, 48 (3 series)
- Handicap: +3.0 per series
- Theoretical: 3 × 3 = 9 points
- Per-series: 49+3=52→50 (+1 effective), 46+3=49 (+3 effective), 48+3=51→50 (+2 effective)
- Effective: 1 + 3 + 2 = **6 points** (3 points "lost" to cap)

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

    // Apply handicap per series and clamp each between 0 and 50
    int total = 0;
    foreach (var s in scores)
    {
        var rawCapped = Math.Min(s.Total, MaxScorePerSeries);
        var adjusted = rawCapped + handicapPerSeries;
        var rounded = (int)Math.Round(adjusted, StandardRounding);
        var clamped = Math.Clamp(rounded, 0, MaxScorePerSeries);
        total += clamped;
    }
    return total;
}

// Calculate effective handicap applied
public static decimal CalculateEffectiveHandicap<T>(
    IEnumerable<T> seriesScores,
    decimal handicapPerSeries,
    int? equalizedCount = null)
    where T : ISeriesScore
{
    // Returns actual handicap applied after clamping
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

    let total = 0;
    let effectiveHandicap = 0;
    for (const s of scores) {
        const rawCapped = Math.min(s.total, 50);
        const adjusted = rawCapped + handicapPerSeries;
        const rounded = Math.round(adjusted);
        // Clamp between 0 and 50
        const clamped = Math.max(0, Math.min(rounded, 50));
        total += clamped;
        effectiveHandicap += (clamped - rawCapped);
    }
    return { total, effectiveHandicap };
}
```

### Controller (API) - TrainingMatchController.cs

Location: `src/HpskSite/Controllers/TrainingMatchController.cs`

The leaderboard calculation in `GetMatchHistory` uses the same per-series clamping logic.

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
| Positive handicap exceeds 50 | Capped at 50 per series |
| Negative handicap below 0 | Clamped at 0 per series |
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
- Standard rounding (away from zero)
- Clamping between 0 and 50
- Same order of operations

## History

- **2026-01-24**: Implemented per-series handicap capping (replaced old total-based calculation)
- **Previous**: Used `FinalScore = RawTotal + (HandicapPerSeries × SeriesCount)` with total cap only

---

**See Also:**
- `TRAINING_SCORING_SYSTEM.md` - Training scoring system (different from training matches)
- `COMPETITION_RESULTS_WORKFLOW.md` - Competition results workflow
