# Finals Competition System

## Overview

The competition results system supports two types of competitions:

1. **Regular Competitions**: All series are treated equally (e.g., 6-12 series)
2. **Competitions with Finals**: Qualification round followed by a finals round (e.g., 7 qualification series + 3 finals series)

This document describes how competitions with finals are configured, displayed, and ranked.

## Competition Configuration

### Umbraco Properties

Two properties on the Competition document type control the finals system:

| Property | Alias | Type | Description |
|----------|-------|------|-------------|
| **Number Of Series Or Stations** | `numberOfSeriesOrStations` | Integer | Total number of series in the competition (qualification + finals) |
| **Number Of Final Series** | `numberOfFinalSeries` | Integer | Number of series that are finals (0 = no finals) |

### Example Configurations

**Regular Competition (No Finals):**
- `numberOfSeriesOrStations`: 6
- `numberOfFinalSeries`: 0
- Result: 6 regular series

**Competition with Finals:**
- `numberOfSeriesOrStations`: 10
- `numberOfFinalSeries`: 3
- Result: 7 qualification series + 3 finals series

## Results Display Format

### Regular Competition Display

| Plats | Namn | Förening | Vapengrupp | 1 | 2 | 3 | 4 | 5 | 6 | Tot | X |
|-------|------|----------|------------|---|---|---|---|---|---|-----|---|
| 1 | Anna | Club A | A3 | 48 | 47 | 48 | 46 | 48 | 48 | 285 | 15 |

### Finals Competition Display

| Plats | Namn | Förening | Vapengrupp | 1 | 2 | 3 | 4 | 5 | 6 | 7 | Tot | F1 | F2 | F3 | Tot | X |
|-------|------|----------|------------|---|---|---|---|---|---|---|-----|----|----|----|----|---|
| 1 | Ivan | Club B | A3 | 48 | 49 | 47 | 47 | 47 | 48 | 48 | 334 | 46 | 48 | 48 | 476 | 8 |

**Column Explanation:**
- **1-7**: Qualification series scores
- **Tot (first)**: Qualification total
- **F1-F3**: Finals series scores  
- **Tot (final)**: Grand total (qualification + finals)
- **X**: Total number of X shots (across all series)

## Tie-Breaking Rules with Finals

When shooters have identical scores in a competition with finals, the tie-breaking order is:

### 1. Total Score (Primary)
- Grand total (qualification + finals combined)
- Highest score wins

### 2. Number of X's (First Tie-Breaker)
- Total X count across all series
- Most X's wins

### 3. Count-Back Through Finals (Second Tie-Breaker)
- Start with the **last finals series** (F3)
- If tied, check second-to-last finals series (F2)
- Continue backwards through all finals series (F3 → F2 → F1)

### 4. Count-Back Through Qualification (Third Tie-Breaker)
- If still tied after finals count-back, use qualification series
- Start with **last qualification series** (series 7 in example)
- Continue backwards (7 → 6 → 5 → 4 → 3 → 2 → 1)

### Example: Finals Tie-Breaking

**Scenario:** Two shooters with identical totals and X counts

| Shooter | Q-Total | F1 | F2 | F3 | Total | X's | Result |
|---------|---------|----|----|----|----|-----|--------|
| Ivan    | 334     | 46 | 48 | **48** | 476 | 8  | **1st** |
| Johan   | 334     | 45 | 46 | **48** | 472 | 9  | **2nd** |

**Actually, they're not tied - Ivan has 476 vs Johan's 472.**

**Better Example - True Tie Situation:**

| Shooter | Q-Total | F1 | F2 | F3 | Total | X's | Result |
|---------|---------|----|----|----|----|-----|--------|
| Ivan    | 334     | 46 | **48** | 48 | 476 | 8  | **1st** |
| Johan   | 335     | 45 | **46** | 48 | 476 | 8  | **2nd** |

**Reasoning:**
1. Total score: Both 476 (tied)
2. X count: Both 8 (tied)
3. Last finals series (F3): Both 48 (tied)
4. Second-to-last finals (F2): Ivan=48, Johan=46 → **Ivan wins!**

## Technical Implementation

### Files Modified

1. **`Views/CompetitionResult.cshtml`**
   - Reads `numberOfSeriesOrStations` and `numberOfFinalSeries` from competition
   - Calculates qualification vs finals series counts
   - Dynamically generates table columns based on configuration
   - Separates qualification and finals series with intermediate total

2. **`Controllers/CompetitionResultsController.cs`**
   - `GetResultsList`: Returns finals configuration to frontend
   - `CalculateFinalResults`: Retrieves finals config and passes to comparer
   - `SeriesCountBackComparer`: Updated to handle finals-specific tie-breaking

### Key Code Sections

**View Configuration (Razor):**
```csharp
var numberOfSeriesOrStations = competition.Value<int>("numberOfSeriesOrStations");
var numberOfFinalSeries = competition.Value<int>("numberOfFinalSeries");

var hasFinalsRound = numberOfFinalSeries > 0;
var qualificationSeriesCount = hasFinalsRound 
    ? (numberOfSeriesOrStations - numberOfFinalSeries) 
    : numberOfSeriesOrStations;
```

**JavaScript Column Generation:**
```javascript
if (hasFinalsRound && seriesCount >= totalSeriesCount) {
    // Qualification series (1-7)
    for (let i = 1; i <= qualificationSeriesCount; i++) {
        html += `<th>${i}</th>`;
    }
    html += `<th>Tot</th>`; // Qualification total
    
    // Finals series (F1-F3)
    for (let i = 1; i <= numberOfFinalSeries; i++) {
        html += `<th>F${i}</th>`;
    }
}
```

**Tie-Breaking Comparer:**
```csharp
public class SeriesCountBackComparer : IComparer<ShooterResult>
{
    private readonly bool _hasFinalsRound;
    private readonly int _qualificationSeriesCount;
    private readonly int _numberOfFinalSeries;

    public SeriesCountBackComparer(bool hasFinalsRound = false, 
                                  int qualificationSeriesCount = 0, 
                                  int numberOfFinalSeries = 0)
    {
        _hasFinalsRound = hasFinalsRound;
        _qualificationSeriesCount = qualificationSeriesCount;
        _numberOfFinalSeries = numberOfFinalSeries;
    }

    // Count-back logic prioritizes finals series, then qualification
    public int Compare(ShooterResult? x, ShooterResult? y) { ... }
}
```

## Setting Up a Finals Competition

### Step-by-Step Process

1. **Create Competition in Umbraco**
   - Navigate to the Competitions section
   - Create a new Competition node

2. **Configure Series Settings**
   - Set **Number Of Series Or Stations**: `10` (total)
   - Set **Number Of Final Series**: `3` (finals only)
   - This creates: 7 qualification + 3 finals

3. **Result Entry**
   - Enter all 10 series for each shooter
   - Series 1-7: Qualification round
   - Series 8-10: Finals round
   - System automatically handles the split

4. **Results Display**
   - System detects `numberOfFinalSeries > 0`
   - Automatically displays separate qualification and finals columns
   - Applies finals-specific tie-breaking rules

## Display Examples

### Regular Competition (6 Series)

```
Configuration:
- numberOfSeriesOrStations: 6
- numberOfFinalSeries: 0

Display:
Plats | Namn  | 1 | 2 | 3 | 4 | 5 | 6 | Tot | X
------+-------+---+---+---+---+---+---+-----+---
1     | Anna  |48 |47 |48 |46 |48 |48 | 285 |15
```

### Finals Competition (7+3 Series)

```
Configuration:
- numberOfSeriesOrStations: 10
- numberOfFinalSeries: 3

Display:
Plats | Namn  | 1 | 2 | 3 | 4 | 5 | 6 | 7 |Tot | F1| F2| F3|Tot | X
------+-------+---+---+---+---+---+---+---+----+---+---+---+----+---
1     | Ivan  |48 |49 |47 |47 |47 |48 |48 |334 |46 |48 |48 |476 | 8
```

## Important Notes

### Qualification Total
- The first "Tot" column shows **qualification round total only**
- This is useful for determining who qualified for finals
- Not used for final ranking

### Final Total
- The last "Tot" column shows **grand total** (qualification + finals)
- This is the primary ranking criterion
- Includes all shots from both rounds

### X Count
- X count includes X shots from **all series** (qualification + finals)
- Used as the first tie-breaker
- Displayed in the final "X" column

### Backwards Compatibility
- Regular competitions (without finals) work exactly as before
- No changes needed to existing competition data
- System automatically detects finals configuration

## API Response Format

The `GetResultsList` API now includes finals configuration:

```json
{
  "Success": true,
  "Exists": true,
  "IsOfficial": false,
  "LastUpdated": "2025-10-03T12:00:00",
  "Results": { ... },
  "ResultPageUrl": "/competitions/2026/example/resultat",
  "NumberOfSeries": 10,
  "NumberOfFinalSeries": 3
}
```

Frontend JavaScript uses these values to format the results table correctly.

## Testing

### Test Scenarios

1. **Regular Competition**
   - Create competition with `numberOfFinalSeries = 0`
   - Verify single "Tot" column
   - Verify standard count-back (last series → first)

2. **Finals Competition**
   - Create competition with `numberOfFinalSeries = 3`
   - Verify dual "Tot" columns (qualification + final)
   - Verify "F1", "F2", "F3" columns appear
   - Test tie-breaking prioritizes finals

3. **Tie-Breaking with Finals**
   - Create two shooters with identical totals and X counts
   - Vary finals series scores
   - Verify correct ranking based on finals count-back

4. **Mixed Configurations**
   - Test various combinations (5+2, 7+3, 8+4, etc.)
   - Verify columns display correctly
   - Verify calculations are accurate

## Future Enhancements

Potential improvements:
- Display qualification rank separately from final rank
- Highlight which shooters qualified for finals
- Support for multiple finals rounds (semi-finals + finals)
- Configurable number of finalists
- Finals qualification criteria (top 8, top 12, etc.)

## Change History

- **2025-10-03**: Initial implementation of finals system
  - Added configuration properties support
  - Updated results display to show qualification and finals separately
  - Enhanced tie-breaking to prioritize finals series
  - Created comprehensive documentation

---

**Note:** This system follows international shooting competition standards where finals performance is prioritized over qualification performance in tie-breaking scenarios.





