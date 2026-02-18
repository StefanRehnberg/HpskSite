# Series Calculation System

## Overview

The Series Calculation System aggregates results across multiple competitions within a series to produce overall standings. This is distinct from per-competition result calculation (handled by `IResultsService` implementations like `PrecisionResultsService`).

**Key distinction:**
- **Per-competition results** (`IResultsService`): Type-specific, calculates scores for a single competition (e.g., Precision, Milsnabb)
- **Series calculation** (`ISeriesCalculationStrategy`): Cross-type, aggregates pre-computed competition totals across multiple competitions in a series

## Architecture

### Namespace

`HpskSite.CompetitionTypes.Common.SeriesCalculation`

This lives under `Common` because it's a cross-type feature. It consumes per-competition results (via the database) but doesn't depend on any type-specific service.

### Strategy Pattern

```
SeriesCalculation/
  ISeriesCalculationStrategy.cs        # Strategy interface
  SeriesCalculationRegistry.cs         # Static registry (like CompetitionTypes.All)
  SeriesResultTieBreaker.cs            # Shared tiebreak utility
  Models/
    SeriesCalculationContext.cs        # Input data for strategies
    SeriesResultData.cs               # Output: standings + breakdown
    StrategyParameter.cs              # Config parameter definition for UI
  Strategies/
    IndividualSumAllStrategy.cs       # Sum all competitions per shooter
    IndividualBestOfStrategy.cs       # Best N of M per shooter
    IndividualWinsCountStrategy.cs    # Count 1st-place finishes
    IndividualFixedPointsStrategy.cs  # Fixed points table by placement
    IndividualDynamicPointsStrategy.cs # Dynamic points based on participant count
    ClubTeamBestOfStrategy.cs         # Club team standings with individual section
```

### Key Design Decisions

1. **Strategies are stateless pure calculators** - No DB access, no DI dependencies. They receive a `SeriesCalculationContext` with all pre-fetched data and return a `SeriesResultData`.

2. **`SeriesCalculationService`** (DI-registered scoped service) handles data fetching from DB, member/club resolution, caching, then delegates to the strategy.

3. **Multiple result sections** - A strategy can produce multiple `SeriesResultSection` entries (e.g., both Individual and Club standings). Each section has its own type, title, and class standings.

4. **Grouped by shooting class** - Results are grouped by shooting class, matching the existing competition results pattern.

5. **Null for missing** - If a shooter didn't participate in a competition, the score is `null` (displayed as "-"), not 0.

6. **In-memory caching** - Results are cached using `IMemoryCache` with a 5-minute TTL. Cache is automatically invalidated when competition results are saved or deleted.

## Configuration

Two Umbraco properties on the `competitionSeries` document type:

- **`seriesCalculationStrategy`** (Textstring) - The strategy ID (e.g., "IndividualSumAll")
- **`seriesCalculationConfig`** (Textarea) - JSON config string with strategy-specific parameters

These are persisted in CreateSeries, UpdateSeries, and CopySeriesWithCompetitions.

## Available Strategies

### Individual Strategies

| Strategy ID | Name | Description | Parameters |
|---|---|---|---|
| `IndividualSumAll` | Individuellt totalsumma | Sums all competition scores per shooter | None |
| `IndividualBestOf` | Individuellt bästa N | Takes best N competition scores per shooter | `bestOf` (int) |
| `IndividualWinsCount` | Individuellt antal segrar | Counts 1st-place finishes per shooter | None |
| `IndividualFixedPoints` | Individuellt fasta poäng | Awards fixed points by placement (configurable table) | `pointsTable` (JSON array, e.g. `[25,20,16,13,11,10,9,8,7,6,5,4,3,2,1]`) |
| `IndividualDynamicPoints` | Individuellt dynamiska poäng | Points = participant count in class. 1st gets N, 2nd gets N-1, etc. | None |

### Club Strategy

| Strategy ID | Name | Description | Parameters |
|---|---|---|---|
| `ClubTeamBestOf` | Klubblag bästa X | Club team competition with individual + club sections | `bestOf` (int, 0=all), `maxShootersPerClub` (int), `groupByClass` (bool), `clubSeriesScoring` (string: "sum" or "placement") |

#### ClubTeamBestOf Details

- **Per competition**: Picks up to N best shooters per club, sums their scores = club competition score
- **groupByClass**: If true, clubs are ranked per class separately. If false, all classes combined into "Kombinerat" (a shooter only counts once, using their best score across classes)
- **bestOf**: If > 0, only the top N competition scores count (non-counting dimmed)
- **clubSeriesScoring**:
  - `"sum"` — Club's series score = sum of club competition scores (across best X competitions if bestOf > 0)
  - `"placement"` — Each competition awards placement points (1st club gets points = number of competing clubs, 2nd gets one less, etc.). Series score = sum of placement points. Ties share the average.
- **Produces two sections**: Individual (all shooters, sum-all) + Club (club standings)

## How to Add a New Strategy

1. Create a new class implementing `ISeriesCalculationStrategy` in `Strategies/`
2. Register it in `SeriesCalculationRegistry` (static constructor)
3. The strategy will automatically appear in the admin UI dropdown

Example:

```csharp
public class MyNewStrategy : ISeriesCalculationStrategy
{
    public string Id => "MyNew";
    public string Name => "Min nya strategi";
    public string Description => "Beskrivning av strategin.";

    public List<StrategyParameter> GetParameters() => new()
    {
        new StrategyParameter { Key = "myParam", Label = "Min parameter", Type = "int", DefaultValue = 5 }
    };

    public SeriesResultData Calculate(SeriesCalculationContext context)
    {
        // Access context.Parameters["myParam"] for config
        // Access context.CompetitionResults for per-competition scores
        // Return SeriesResultData with sections
    }
}
```

Then in `SeriesCalculationRegistry`:
```csharp
static SeriesCalculationRegistry()
{
    Register(new IndividualSumAllStrategy());
    Register(new IndividualBestOfStrategy());
    Register(new IndividualWinsCountStrategy());
    Register(new IndividualFixedPointsStrategy());
    Register(new IndividualDynamicPointsStrategy());
    Register(new ClubTeamBestOfStrategy());
    Register(new MyNewStrategy()); // Add here
}
```

## Data Flow

1. User configures strategy in series edit modal (admin)
2. Strategy ID and config JSON saved to Umbraco content properties
3. `CompetitionSeries.cshtml` checks if strategy is configured
4. If yes, calls `GetSeriesResults` API endpoint
5. `SeriesCalculationService.CalculateSeriesResults()`:
   - Checks in-memory cache first (5-minute TTL)
   - If cache miss: reads strategy from registry
   - Fetches child competitions
   - Batch-fetches all `PrecisionResultEntry` rows
   - Builds shooter lookup (name, club)
   - Aggregates scores per (competition, member, class)
   - Passes `SeriesCalculationContext` to strategy
   - Caches the result
6. Strategy returns `SeriesResultData` with sections
7. JavaScript renders sections with appropriate table layout

## Caching

- **Technology**: `IMemoryCache` (ASP.NET Core built-in)
- **TTL**: 5 minutes
- **Cache key**: `SeriesResults_{seriesContentId}`
- **Invalidation**: Automatic when competition results are saved or deleted via `CompetitionResultsController`. The controller calls `SeriesCalculationService.InvalidateCacheForCompetition(competitionId)` which finds the parent series and evicts its cache entry.
- **Manual invalidation**: `SeriesCalculationService.InvalidateCacheForSeries(seriesId)` can be called directly.

## Result Model

```
SeriesResultData
  StrategyId, StrategyName, CalculatedAt
  Competitions[]           # Ordered list of competitions
  Sections[]               # One or more result sections
    SectionType            # "Individual" or "Club"
    Title                  # Display title
    ClassStandings[]       # One per shooting class
      ClassName
      Rows[]               # Ranked rows
        Rank, Name, Club, EntityId
        TotalSeriesScore, TotalXCount
        CompetitionScores[]
          CompetitionId, Score, XCount, Points, Counting
```

## Tiebreaking

All strategies use `SeriesResultTieBreaker.Compare()`:
1. Primary: Total series score (descending)
2. Secondary: Total X count (descending)
3. Tertiary: Best result in last competition, then backwards through competitions

## Tie Handling in Points-Based Strategies

For `IndividualFixedPoints`, `IndividualDynamicPoints`, and club placement scoring:
- Tied shooters/clubs share the average of the positions they span
- Example: Two shooters tied for 1st in a 10-person class with dynamic points. Positions 1 and 2 share (10+9)/2 = 9 points each.

## Club Section Rendering

The `renderClubSection()` function in `CompetitionSeries.cshtml` handles:
- **Sum scoring**: Displays raw club competition scores directly
- **Placement scoring**: Shows placement points with raw score available as tooltip
- **BestOf**: Non-counting cells shown dimmed with strikethrough (same as individual BestOf)
- **No "Klubb" column**: Club sections show club name in the Name column, no separate Club column
- **No X count column**: Club sections don't track X count

## Relationship to Other Systems

- **CompetitionTypes static registry** (`Models/CompetitionType.cs`): Same pattern, different domain
- **ShootingClasses registry** (`Models/ShootingClass.cs`): Same pattern
- **PrecisionResultEntry** (`CompetitionTypes/Precision/Models/`): Source data for series calculation
- **CompetitionResultsController**: Per-competition results display; series calculation is separate. Also triggers cache invalidation on result save/delete.
- **AdminServicesComposer**: Where `SeriesCalculationService` is registered as scoped
