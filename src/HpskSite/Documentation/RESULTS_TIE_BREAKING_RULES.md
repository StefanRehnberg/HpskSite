# Competition Results Tie-Breaking Rules

## Overview

This document describes how competition results are sorted and ranked when shooters have identical scores. The system implements standard shooting competition tie-breaking rules (count-back method).

## Sorting Priority

When ranking shooters within a shooting class, the following criteria are applied in order:

### 1. Total Score (Primary)
- The shooter with the **highest total score** across all series ranks first
- Total score is calculated by summing all shots from all series
- 'X' shots count as 10 points

### 2. Number of X's (First Tie-Breaker)
- If shooters have the same total score, the shooter with the **most X shots** ranks first
- X shots are the innermost 10s and indicate higher precision

### 3. Count-Back Through Series (Second Tie-Breaker)
- If shooters have the same total score AND same number of X's, the system performs a "count-back"
- **Count-back method:**
  1. Compare the **last series** score (highest wins)
  2. If still tied, compare the **second-to-last series** score
  3. Continue backwards through all series until the tie is broken
  4. If all series scores are identical, shooters are considered truly tied

## Example

### Scenario: Two shooters with identical total scores and X counts

| Shooter | Series 1 | Series 2 | Series 3 | Series 4 | Series 5 | Series 6 | Total | X's | Final Rank |
|---------|----------|----------|----------|----------|----------|----------|-------|-----|------------|
| Anna    | 48       | 47       | 48       | 46       | 48       | **48**   | 285   | 15  | **1st**    |
| Björn   | 49       | 47       | 47       | 46       | 49       | **47**   | 285   | 15  | **2nd**    |

**Reasoning:**
- Both have 285 points (tied)
- Both have 15 X's (tied)
- Count-back: Anna's last series (48) > Björn's last series (47)
- **Result: Anna ranks 1st**

### Another Example: Tie resolved in earlier series

| Shooter | Series 1 | Series 2 | Series 3 | Series 4 | Series 5 | Series 6 | Total | X's | Final Rank |
|---------|----------|----------|----------|----------|----------|----------|-------|-----|------------|
| Carl    | 47       | **49**   | 46       | 48       | 47       | 48       | 285   | 15  | **1st**    |
| Diana   | 48       | **47**   | 46       | 48       | 48       | 48       | 285   | 15  | **2nd**    |

**Count-back process:**
1. Last series (6): Both have 48 - tied
2. Series 5: Carl=47, Diana=48 - Diana wins series 5 - tied
3. Series 4: Both have 48 - tied
4. Series 3: Both have 46 - tied
5. Series 2: Carl=49, Diana=47 - **Carl wins!**

## Technical Implementation

### Location
- **File:** `Controllers/CompetitionResultsController.cs`
- **Method:** `CalculateFinalResults()`
- **Comparer Class:** `SeriesCountBackComparer`

### Code Structure

```csharp
// In CalculateFinalResults method:
Shooters = classGroup
    .OrderByDescending(s => s.TotalScore)                        // 1. Total score
    .ThenByDescending(s => s.TotalXCount)                        // 2. X count
    .ThenByDescending(s => s, new SeriesCountBackComparer())     // 3. Count-back
    .ToList()
```

### SeriesCountBackComparer Class

The `SeriesCountBackComparer` implements `IComparer<ShooterResult>` and:

1. **Extracts series scores** from both shooters
2. **Orders series by number** (descending - last series first)
3. **Iterates through series** from last to first
4. **Compares each series score** until a difference is found
5. **Returns comparison result** based on first non-equal series

### Key Features
- Handles shooters with different numbers of series (missing series = 0 points)
- Safely parses JSON shot data
- Returns 0 if all series are equal (true tie)
- Treats 'X' shots as 10 points in series score calculation

## Standards Compliance

This implementation follows standard shooting competition rules as used in:
- ISSF (International Shooting Sport Federation)
- DSB (Deutscher Schützenbund)
- Svenska Skyttesportförbundet

The count-back method is the internationally recognized standard for breaking ties in precision shooting competitions.

## Testing

To verify the tie-breaking logic works correctly:

1. Create test data with multiple shooters having identical total scores
2. Vary the X counts to test first tie-breaker
3. Create scenarios where X counts are equal but series scores differ
4. Verify that shooters are ranked according to last series, second-to-last, etc.

## Future Enhancements

Potential improvements:
- Display tie-breaking information in results UI (e.g., "Won on count-back")
- Add tie-breaking statistics to result exports
- Support for alternative tie-breaking methods (if required by specific competitions)

## Finals Competitions

For competitions with a finals round (e.g., 7 qualification series + 3 finals series), the tie-breaking rules are enhanced:

### Enhanced Tie-Breaking Order

1. **Total Score** (qualification + finals combined)
2. **Number of X's** (across all series)
3. **Count-back through finals series** (last finals series first)
4. **Count-back through qualification series** (if still tied after finals)

**Key Point:** Finals series take priority in count-back scenarios, reflecting their higher importance in determining final rankings.

See **FINALS_COMPETITION_SYSTEM.md** for complete details on competitions with finals.

## Change History

- **2025-10-03**: Enhanced tie-breaking system to support finals competitions
  - Added finals-aware count-back logic
  - Finals series now prioritized in tie-breaking
  - Updated `SeriesCountBackComparer` class with finals support
- **2025-10-03**: Initial implementation of count-back tie-breaking system
  - Added `SeriesCountBackComparer` class
  - Integrated into `CalculateFinalResults` sorting logic
  - Documented rules and examples

---

**Note:** This tie-breaking system ensures fair and consistent ranking of competition results according to international shooting sport standards, including special handling for competitions with finals rounds.

