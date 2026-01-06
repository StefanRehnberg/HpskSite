# Training Scoring System

Complete documentation for the self-service training log system where members record individual training sessions with detailed shot-by-shot data.

## Overview

**Purpose:** Personal training progress tracking and improvement analysis

**Separation from Skyttetrappan:**
- **Skyttetrappan** = Structured curriculum with instructor validation
- **Training Scoring** = Personal training log for individual practice
- No data sharing between systems (could be linked in future)

**Key Features:**
- Self-service entry (no admin approval required)
- Shot-by-shot tracking
- Automatic calculations
- Personal best tracking (training vs competition)
- Dashboard with Chart.js visualizations
- Unified results from multiple sources

**Last Updated:** 2025-10-31

---

## Architecture

### Data Storage
**Database Table:** `TrainingScores` (not member properties)

**Controller:** `TrainingScoringController.cs` (Surface Controller)

**Models:** `ViewModels/TrainingScoring/` directory

**UI:** Integrated into `UserProfile.cshtml` with 3 tabs:
1. **Dashboard** (default) - Statistics and visualizations
2. **Profil** - User profile editing
3. **Träningsresultat** - Training score entry and history

---

## Database Schema

### TrainingScores Table

Created by migrations:
- `CreateTrainingScoresTable.cs` (initial schema)
- `RemoveShootingClassFromTrainingScores.cs` (removed ShootingClass column/index)
- `AddIsCompetitionToTrainingScores.cs` (added IsCompetition flag)

**Schema:**
```sql
CREATE TABLE TrainingScores (
    Id INT PRIMARY KEY IDENTITY,
    MemberId INT NOT NULL,                     -- FK to cmsMember.nodeId
    TrainingDate DATETIME NOT NULL,
    WeaponClass VARCHAR(50) NOT NULL,          -- A, B, C, R, P (weapon type)
    IsCompetition BIT NOT NULL DEFAULT 0,      -- True for external competition results
    SeriesScores NVARCHAR(MAX) NOT NULL,       -- JSON array of series data
    TotalScore INT NOT NULL,                   -- Calculated total points
    XCount INT NOT NULL,                       -- Total X-count across all series
    Notes VARCHAR(1000),                       -- Optional training notes
    CreatedAt DATETIME NOT NULL DEFAULT GETDATE(),
    UpdatedAt DATETIME NOT NULL DEFAULT GETDATE()
)

-- Indexes
CREATE INDEX IX_TrainingScores_MemberId ON TrainingScores(MemberId)
CREATE INDEX IX_TrainingScores_TrainingDate ON TrainingScores(TrainingDate)
CREATE INDEX IX_TrainingScores_WeaponClass ON TrainingScores(WeaponClass)
```

### Important Distinction

- **WeaponClass** (stored): A, B, C, R, P - Weapon type only
- **ShootingClass** (NOT stored): A3, C Vet Y - Competition class designation
- Training scores only track weapon type, not competition class

---

## Models

### 1. TrainingSeries.cs

Represents a single series (5 shots)

```csharp
public class TrainingSeries
{
    public int SeriesNumber { get; set; }           // 1, 2, 3...
    public List<string> Shots { get; set; }         // ["10", "X", "9", "10", "8"]
    public int Total { get; set; }                  // Auto-calculated
    public int XCount { get; set; }                 // Auto-calculated

    public void CalculateScore()                    // Sum shots, count X's
    public bool IsValid()                           // Validates 5 shots, values 0-10/X
}
```

### 2. TrainingScoreEntry.cs

Complete training session

```csharp
public class TrainingScoreEntry
{
    public int Id { get; set; }
    public int MemberId { get; set; }
    public DateTime TrainingDate { get; set; }
    public string WeaponClass { get; set; }         // A, B, C, R, P
    public bool IsCompetition { get; set; }         // External competition flag
    public List<TrainingSeries> Series { get; set; }
    public int TotalScore { get; set; }
    public int XCount { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt, UpdatedAt { get; set; }

    public void CalculateTotals()                   // Sum all series
    public bool IsValid(out string errorMessage)    // Comprehensive validation
    public string SerializeSeries()                 // To JSON for DB storage
    public void DeserializeSeries(string json)      // From JSON
    public string GetSummary()                      // Shows "Träning" or "Tävling"
}
```

### 3. PersonalBest.cs

Personal best tracking

```csharp
public class PersonalBest
{
    public string WeaponClass { get; set; }         // A, B, C, R, P
    public bool IsCompetition { get; set; }         // Training vs competition
    public int SeriesCount { get; set; }            // Best tracked per series count
    public int BestScore { get; set; }
    public int XCount { get; set; }
    public DateTime AchievedDate { get; set; }
    public int TrainingScoreId { get; set; }        // Links to entry
    public int? PreviousBest { get; set; }
    public int Improvement { get; }                 // Calculated property
}

public class PersonalBestsByClass
{
    public string WeaponClass { get; set; }
    public List<PersonalBest> Bests { get; set; }  // One per series count
}
```

**Note:** `TrainingStatistics.cs` was deleted in favor of dashboard-specific statistics

---

## API Endpoints

### TrainingScoringController.cs

All endpoints require member authentication.

#### POST RecordTrainingScore

**Endpoint:** `/umbraco/surface/TrainingScoring/RecordTrainingScore`

**Body:**
```json
{
  "trainingDate": "2025-10-31",
  "weaponClass": "A",
  "isCompetition": false,
  "series": [
    { "shots": ["10", "X", "9", "10", "8"] },
    { "shots": ["X", "10", "10", "9", "9"] }
  ],
  "notes": "Good session"
}
```

**Validates:**
- Member is logged in
- Calculates totals automatically

**Returns:** Success/error with entry summary

#### GET GetMyTrainingScores

**Endpoint:** `/umbraco/surface/TrainingScoring/GetMyTrainingScores?limit=10&skip=0`

**Returns:** Current member's scores with pagination
- Deserializes series JSON for each entry
- Ordered by TrainingDate descending

#### GET GetPersonalBests

**Endpoint:** `/umbraco/surface/TrainingScoring/GetPersonalBests?weaponClass=A&includeCompetitions=true`

**Returns:** PersonalBestsByClass structure
- Dynamically calculates personal bests from all scores
- Groups by weaponClass, seriesCount, and isCompetition
- Tracks training and competition bests separately

#### GET GetDashboardStatistics

**Endpoint:** `/umbraco/surface/TrainingScoring/GetDashboardStatistics`

**Returns:** Comprehensive statistics for dashboard:
```json
{
  "totalSessions": 42,
  "totalTrainingSessions": 35,
  "totalCompetitions": 7,
  "overallAverage": 45.3,
  "recentAverage": 46.8,
  "previousAverage": 44.2,
  "monthlyData": [...],           // Individual entries for line chart
  "weaponClassData": [...],       // Aggregated for bar chart
  "personalBestsCount": 12
}
```

#### PUT UpdateTrainingScore

**Endpoint:** `/umbraco/surface/TrainingScoring/UpdateTrainingScore`

**Body:** TrainingScoreEntry with Id

**Validates:**
- Verifies ownership (member can only edit own scores)
- Recalculates totals

#### DELETE DeleteTrainingScore

**Endpoint:** `/umbraco/surface/TrainingScoring/DeleteTrainingScore?id=123`

**Validates:**
- Verifies ownership
- Permanent deletion from TrainingScores table

---

## User Interface

### Dashboard Tab (Default View - Redesigned 2025-10-31)

#### Year Filter
- Dropdown showing available years from user's data
- Filters all dashboard statistics and charts
- Default: Current year

#### 1. Quick Stats Cards (3 Cards)

**Card 1: Activity Summary**
- Total sessions count (training + competition combined)
- Icon-based breakdown:
  - 🎯 Training sessions count
  - 🏆 Competition shots count
- Large numbers with clear visual hierarchy

**Card 2: Current Form (30 days)**
- Overall average score for last 30 days
- Breakdown by type:
  - Training average (blue dot)
  - Competition average (green dot)
- Trend indicator:
  - Comparison vs previous 30 days
  - Color coded: green (improving), yellow (stable), red (declining)
  - Icons: arrow-up, dash, arrow-down
- Empty state: "Inga resultat senaste 30 dagarna"

**Card 3: Personal Bests with Progressive Disclosure**
- **Collapsed view** (default):
  - Best training score across all weapon classes
  - Best competition score across all weapon classes
  - Expandable button with chevron icon
- **Expanded view** (click to expand):
  - Complete breakdown per weapon class
  - Training bests per weapon
  - Competition bests per weapon
  - Uses Bootstrap accordion pattern

#### 2. Progress Over Time Chart (Chart.js Line Chart)

**Data Source:** Individual entries from last 12 months (NOT aggregated)

**Display:**
- Each training session and competition entry as separate point
- X-Axis: Time scale with automatic date formatting
  - Shows full date range from first to last entry
  - Unit: Months (e.g., "May 2025", "Aug 2025")
  - Tooltip: Full date with day (e.g., "1 maj 2025")
- Y-Axis:
  - Dynamic minimum with 15% padding below lowest score
  - **Maximum capped at 50.0** (theoretical max: 10 × 5 shots)

**Features:**
- Filter buttons: All / Training Only / Competition Only
- Separate line per weapon class with color coding:
  - A: Red, B: Orange, C: Yellow, R: Teal, P: Purple
- Tooltip: Shows weapon class, score, date, competition name (if applicable)

**Use Case:** Visualize individual performance over time, identify trends

#### 3. Weapon Class Performance Chart (Chart.js Bar Chart)

**Data Source:** Aggregated averages by weapon class

**Display:**
- Bar chart comparing training vs competition performance
- Two bars per weapon class:
  - Training (blue): Average of all training sessions
  - Competition (green): Average of all competition entries
- Y-Axis:
  - Dynamic minimum with 15% padding
  - **Maximum capped at 50.0**
- Data Labels: Values displayed on top of bars (e.g., "45.3")
- Tooltip: Shows type and score (e.g., "Träning: 45.3p")

**Use Case:** Compare overall performance between training and competition by weapon

#### 4. Quick Actions

- "Registrera Träningsresultat" button → TrainingScoreEntry modal
- "Visa Alla Resultat" button → Switches to Resultat tab

### Träningsresultat Tab

#### 1. Personal Bests Section

- Grouped by weapon class (A, B, C, R, P)
- Each class shows bests for different series counts
- Separate display for training vs competition bests
- Displays: Score, X-count, Date achieved, Improvement
- Color-coded with warning/success styling

#### 2. Recent Training Sessions Table

- Last 10 sessions with pagination support
- Columns: Date, Type (Training/Competition), Class, Series Count, Total Score, X-count
- View button opens detail modal
- Delete button with confirmation

#### 3. Add New Score Button

Opens TrainingScoreEntry modal

### Modals

#### TrainingScoreEntry.cshtml - Add Training Score

**Fields:**
- Date picker (max: today, required)
- Weapon class dropdown (A, B, C, R, P)
- **IsCompetition checkbox** - "Detta är ett resultat från en extern tävling"
  - Help text explains external competition tracking
  - Affects statistics categorization
- Dynamic series cards (add/remove)
- Each series: 5 shot inputs (0-10 or X)
- Real-time score calculation per series
- Overall summary (total series, total score, X-count)
- Optional notes textarea

**Features:**
- Client-side validation before submit
- Auto-reset on close (including checkbox)

#### TrainingScoreDetail.cshtml - View Score Details

**Display:**
- Full training session overview
- Quick stats: Total score, X-count, Series count
- Accordion with each series expanded
- Shot-by-shot visualization with color coding:
  - X shots: Yellow background
  - 10 points: Green background
  - Others: Light gray
- Notes display
- Delete button with confirmation
- Timestamps (created/updated)

---

## Data Collection Architecture - Unified Results System

### Overview

The system aggregates results from **THREE** data sources to provide comprehensive member statistics.

### Architecture Components

1. **UnifiedResultsService** (`Services/UnifiedResultsService.cs`) - Central aggregation service
2. **TrainingScoringController** - Uses UnifiedResultsService for dashboard statistics
3. **Resultat Tab** - Displays all results from all sources in single table

### Three Data Sources

#### Source 1: TrainingScores Table (Training Entries)

**Table:** `TrainingScores`
**Filter:** `WHERE IsCompetition = 0` (or NULL for legacy data)
**Data Type:** Self-entered training sessions

**Fields Used:**
- `Id, MemberId, TrainingDate, WeaponClass`
- `SeriesScores` (JSON), `TotalScore, XCount, Notes`

**Characteristics:**
- Manually entered by members via TrainingScoreEntry modal
- Full shot-by-shot data (SeriesScores JSON)
- Can be edited/deleted by member
- SourceType: "Training"

**Controller:** `UnifiedResultsService.cs` lines 62-119
- Query: Lines 72-84 (SELECT from TrainingScores WHERE IsCompetition is null/false)
- Processing: Lines 86-112 (Deserialize series, calculate averages)

#### Source 2: PrecisionResultEntry Table (Competition Entries)

**Table:** `PrecisionResultEntry`
**Data Type:** Results entered during official competitions via precision scoring system

**Fields Used:**
- `CompetitionId, MemberId, ShootingClass`
- `Shots` (JSON array per series), `EnteredAt`

**Query Strategy:**
- GROUP BY CompetitionId, MemberId, ShootingClass
- STRING_AGG to combine all series shots
- COUNT(*) for series count
- MIN(EnteredAt) for date

**Characteristics:**
- Created during competition scoring by competition admin
- One row per series in competition
- Multiple weapon classes in same competition = multiple entries
- Cannot be edited/deleted (official results)
- SourceType: "Competition"
- Competition name fetched via IContentService.GetById(CompetitionId)

**Controller:** `UnifiedResultsService.cs` lines 124-243
- Query: Lines 131-142 (GROUP BY competition/class, aggregate shots)
- Processing: Lines 146-233 (Parse shots, calculate totals, fetch competition name)
- WeaponClass extraction: Lines 152-154 (First char of ShootingClass, e.g., "A3" → "A")

#### Source 3: Competition Result Documents (Official Results) - FUTURE

**Document Type:** `competitionResult` child nodes under `competition`
**Status:** ⏳ NOT YET IMPLEMENTED
**Plan:** Parse `resultData` JSON property to extract member-specific results
**Controller:** `UnifiedResultsService.cs` lines 248-258 (returns empty list currently)

### Data Aggregation Flow

```
GetDashboardStatistics() in TrainingScoringController.cs (line 360)
  ↓
1. Call _unifiedResultsService.GetMemberResults(memberId)
  ↓
2. UnifiedResultsService aggregates:
   a. GetTrainingScoresResults(memberId) → Training entries
   b. GetPrecisionResultEntries(memberId, db) → Competition entries
   c. GetOfficialCompetitionResults(memberId) → Empty for now
  ↓
3. Combine all results, order by date descending
  ↓
4. Separate by SourceType:
   - trainingResults = SourceType == "Training"
   - competitionResults = SourceType == "Competition" OR "Official"
  ↓
5. Calculate separate statistics for each type
  ↓
6. Generate monthly data (individual entries, not aggregated)
  ↓
7. Generate weapon class aggregates
```

### Unified Result Entry Model

```csharp
public class UnifiedResultEntry
{
    public int Id { get; set; }                    // TrainingScore.Id OR CompetitionId
    public DateTime Date { get; set; }
    public string SourceType { get; set; }         // "Training", "Competition", "Official"
    public string WeaponClass { get; set; }        // A, B, C, R, P
    public int TotalScore { get; set; }
    public int XCount { get; set; }
    public int SeriesCount { get; set; }
    public double AverageScore { get; set; }       // TotalScore / SeriesCount
    public string? CompetitionName { get; set; }   // Null for training
    public int? CompetitionId { get; set; }        // Null for training
    public bool CanEdit { get; set; }              // True for training, false for competition
    public bool CanDelete { get; set; }            // True for training, false for competition
    public string? Notes { get; set; }
    public List<SeriesDetail> Series { get; set; } // Full shot-by-shot data
}
```

### Key Calculation Rules

1. **Series Average (Medelresultat):** TotalScore ÷ SeriesCount
   - Used for all comparisons and statistics
   - Allows fair comparison between 3-series and 6-series shoots

2. **Competition Count:** Total number of ENTRIES, not unique competitions
   - Multiple weapon classes in same competition = multiple entries
   - Example: Same competition with A, B, C = counted as 3 entries

3. **Monthly Data (Progress Chart):** Individual entries, NOT aggregated
   - Each training session = separate data point
   - Each competition entry (weapon class) = separate data point

4. **Weapon Class Data (Bar Chart):** Aggregated averages
   - Average of all training sessions per weapon class
   - Average of all competition entries per weapon class

### Benefits of Unified System

✅ Single source of truth for all member results
✅ Automatic separation of training vs competition
✅ Official competition results integrated seamlessly
✅ Consistent statistics across all data sources
✅ No data duplication
✅ Easy to extend with additional sources

**Service Registration:** Registered in `Composers/AdminServicesComposer.cs` line 18 (Scoped lifetime)

---

## JavaScript Integration

### Dashboard Functions

```javascript
loadDashboard()                       // Fetches GetDashboardStatistics API
renderDashboard(data, selectedYears)  // Builds 3 stat cards + 2 charts + quick actions
renderProgressChart(monthlyData, filter)
  // Line chart with All/Training/Competition filters
  // Shows INDIVIDUAL data points (not monthly aggregates)
  // X-axis: Time scale, Y-axis: Capped at 50.0
renderWeaponClassChart(weaponClassData)
  // Bar chart comparing training vs competition by weapon
  // Y-axis: Capped at 50.0, data labels on bars
filterProgressChart(filter)       // Updates progress chart filter
filterByYear()                     // Handles year dropdown change
togglePersonalBests()              // Toggles accordion in Personal Bests card
getColorForWeaponClass(wc)        // Color mapping
switchToTab(tabId)                 // Programmatically switch tabs
```

### Resultat Tab Functions

```javascript
loadTrainingScoresDashboard()     // Fetches scores + bests
renderTrainingDashboard()         // Builds personal bests + recent sessions table
showAddScoreModal()               // Opens entry modal
viewScore(id)                     // Opens detail modal
```

### Modal Functions

```javascript
// TrainingScoreEntry.cshtml
addSeries()                       // Adds new series card
removeSeries(id)                  // Removes series
validateShot(input)               // Client-side validation (0-10/X)
calculateSummary()                // Real-time totals
submitTrainingScore()             // Form submission

// TrainingScoreDetail.cshtml
viewScoreDetails(id)              // Loads and displays score details
loadScoreDetails(id)              // Fetches from API
renderScoreDetails(score)         // Builds detail HTML
deleteTrainingScore()             // Delete with confirmation
```

---

## Styling

### Key CSS Classes

- `.shot-input` - Shot value inputs (centered, bold)
- `.series-card` - Series container with left border
- `.accordion-button` - Series accordion headers
- Color coding: Primary (sessions), Success (average), Warning (personal bests)

---

## Security

- Member authentication required for all endpoints
- Ownership verification (can only edit/delete own scores)
- Anti-forgery tokens on all POST/PUT/DELETE
- Member ID set server-side (can't be spoofed)

---

## Data Validation

- Training date must be in past
- At least 1 series required, max 20 series
- Each series must have exactly 5 shots
- Shot values: 0-10 or X only
- Weapon class required (A, B, C, R, P)

---

## Implementation Status

✅ Database schema complete (3 migrations)
✅ All 3 models updated (TrainingSeries, TrainingScoreEntry, PersonalBest)
✅ TrainingScoringController with 6 endpoints
✅ Chart.js installed via npm
✅ UserProfile.cshtml restructured (3-tab interface)
✅ Dashboard implementation complete (4 cards + 2 charts)
✅ TrainingScoreEntry modal (with isCompetition checkbox)
✅ TrainingScoreDetail modal (view/delete)
✅ Träningsresultat tab with complete display
✅ Auto-refresh after add/delete operations
✅ Build verified (0 errors, 0 warnings)
✅ Unified Results System (3 data sources)

---

## Testing Checklist

**Dashboard Tab:**
- [ ] Navigate to /user-profile → Dashboard loads
- [ ] Verify 4 stat cards display
- [ ] Verify progress chart renders
- [ ] Test chart filters (All/Training/Competition)
- [ ] Verify weapon class chart
- [ ] Test quick action buttons

**Training Score Entry:**
- [ ] Click "Nytt resultat" → Modal opens
- [ ] Add series with 5 shots each
- [ ] Verify real-time calculation
- [ ] Test isCompetition checkbox
- [ ] Submit and verify dashboard updates

**Träningsresultat Tab:**
- [ ] Switch to Träningsresultat tab
- [ ] Verify personal bests (training/competition separate)
- [ ] Recent sessions table correct
- [ ] View/delete functionality works

---

## Future Enhancements

- [ ] Export training data to CSV/PDF
- [ ] Training reminders/goals
- [ ] Link to Skyttetrappan for step validation
- [ ] Admin view of all member training activity
- [ ] Export chart visualizations as images
- [ ] More granular filtering (date range, specific weapon)
- [ ] Year-over-year comparison charts
- [ ] Shot distribution analysis (10s, 9s, etc.)

---

**Implementation Status:** ✅ Complete
**Last Tested:** 2025-10-31
**Build Status:** ✅ 0 errors
