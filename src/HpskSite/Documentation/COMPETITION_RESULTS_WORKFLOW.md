# Competition Results Management System

## Overview

This document describes the complete workflow for managing competition results in real-time, from preliminary entry to official publication.

## System Architecture

### Key Components

1. **Results Entry Interface** (`Views/Partials/CompetitionResultsManagement.cshtml`)
   - Collapsible "Registrera Resultat" section for entering shot results
   - Collapsible "Resultatlista" section for viewing and managing the generated results list
   - Keypad interface for quick result entry
   - Support for multiple concurrent range officers

2. **Backend Controller** (`Controllers/CompetitionResultsController.cs`)
   - `SaveResult`: Saves individual series results to the database
   - `CreateResultsList`: Generates/updates the complete results list
   - `GetResultsList`: Retrieves the current results list and its status
   - `ToggleResultsOfficial`: Changes the official status of results
   - `UpdateLiveLeaderboard`: Automatically updates results after each entry

3. **Results Display** (`Views/CompetitionResult.cshtml`)
   - Formatted display of results grouped by shooting class
   - Auto-refresh for preliminary results (every 15 seconds)
   - Print and export functionality
   - Visual distinction between preliminary and official results

4. **Results Hub** (`Views/CompetitionResultsHub.cshtml`)
   - Central hub showing all result pages
   - Links to both preliminary and final results

## Workflow

### Phase 1: Competition Ongoing (Preliminary Results)

1. **Range Officers Enter Results**
   - Multiple range officers can work simultaneously
   - Each RO enters results series by series
   - Results are saved to `dbo.PrecisionResultEntry` table
   - Each save automatically triggers an update to the live leaderboard

2. **Generate Preliminary Results List**
   - Navigate to Competition → Results tab
   - In the "Registrera Resultat" section, click "Skapa Resultatlista"
   - System creates/updates the "Slutresultat" page with:
     - `isOfficial = false` (preliminary status)
     - All current results grouped by shooting class
     - Automatically sorted by total score and X count

3. **View Live Results**
   - Navigate to Competition → Resultat Hub → Slutresultat
   - Page displays with:
     - **Yellow warning badge** showing "PRELIMINÄR"
     - Auto-refresh every 15 seconds
     - Pause/resume button for auto-refresh
     - Full results table with series breakdowns
   - Results update automatically as new entries are saved

4. **Concurrent Operation**
   - Multiple range officers can enter results simultaneously
   - No conflicts - database handles concurrent writes
   - Each save updates the results list immediately
   - All viewers see updates within 15 seconds (auto-refresh interval)

### Phase 2: Competition Complete (Official Results)

1. **Final Review**
   - All results are entered and verified
   - View the "Slutresultat" page to inspect the complete list
   - Check for any errors or missing entries

2. **Mark Results as Official**
   - On the Competition → Results tab, expand "Resultatlista"
   - Click "Markera som Officiell"
   - Confirm the action
   - System updates `isOfficial = true`

3. **Official Results Display**
   - "Slutresultat" page now shows:
     - **Green success badge** showing "OFFICIELL"
     - Auto-refresh is disabled
     - Results are final and published
     - Can still be printed or exported

4. **Revert if Needed**
   - If corrections are needed after marking official:
     - Click "Återställ till Preliminär"
     - Make corrections
     - Re-mark as official when complete

## Database Structure

### PrecisionResultEntry Table

```sql
CREATE TABLE [dbo].[PrecisionResultEntry] (
    [Id] INT IDENTITY(1,1) PRIMARY KEY,
    [CompetitionId] INT NOT NULL,
    [SeriesNumber] INT NOT NULL,
    [TeamNumber] INT NOT NULL,
    [Position] INT NOT NULL,
    [MemberId] INT NOT NULL,
    [ShootingClass] NVARCHAR(50) NOT NULL,
    [Shots] NVARCHAR(MAX) NOT NULL,  -- JSON array: ["X","10","9","8","7"]
    [EnteredBy] INT NOT NULL,         -- Range officer MemberId
    [EnteredAt] DATETIME NOT NULL,
    [LastModified] DATETIME NOT NULL
)
```

### Umbraco Content Structure

```
Competition (2171)
└── Resultat (CompetitionResultsHub)
    └── Slutresultat (CompetitionResult)
        ├── resultType: "Final Results"
        ├── isOfficial: false/true
        ├── resultData: JSON with complete results
        └── lastUpdated: timestamp
```

## API Endpoints

### For Results Entry

- **POST** `/umbraco/surface/CompetitionResults/SaveResult`
  - Saves a single series result
  - Auto-updates live leaderboard
  - Handles concurrent writes

- **POST** `/umbraco/surface/CompetitionResults/DeleteResult`
  - Removes a series result
  - Auto-updates live leaderboard

### For Results List Management

- **POST** `/umbraco/surface/CompetitionResults/CreateResultsList`
  - Generates/updates the complete results list
  - Preserves existing `isOfficial` status
  - Request: `{ competitionId: 2171 }`
  - Response: `{ success: true, resultsCount: 20, classGroupsCount: 5, isOfficial: false }`

- **GET** `/umbraco/surface/CompetitionResults/GetResultsList?competitionId=2171`
  - Retrieves current results list and status
  - Response includes `isOfficial`, `lastUpdated`, and complete results data

- **POST** `/umbraco/surface/CompetitionResults/ToggleResultsOfficial`
  - Changes official status of results
  - Request: `{ competitionId: 2171, isOfficial: true }`
  - Response: `{ success: true, message: "...", isOfficial: true }`

## Features

### Real-Time Updates

- **During Competition**: Results update automatically every 15 seconds for all viewers
- **After Each Save**: Live leaderboard is immediately recalculated
- **Auto-Pause**: Refresh pauses when page is hidden (tab switched)
- **Manual Control**: Users can pause/resume auto-refresh

### Concurrent Operation

- **Multiple Range Officers**: Can work simultaneously on different shooters
- **No Conflicts**: Database handles concurrent writes safely
- **Session Tracking**: Each RO's session is tracked separately
- **Collision Detection**: Warns if multiple ROs try to edit same shooter

### Status Management

- **Preliminary**: Yellow badge, auto-refresh enabled, can be edited
- **Official**: Green badge, auto-refresh disabled, results are final
- **Reversible**: Can revert from official to preliminary if corrections needed

### Display Features

- **Grouped by Class**: Results organized by shooting class (C1, C2, B1, etc.)
- **Series Breakdown**: Shows individual series scores and totals
- **X Count Display**: Highlights X shots in red
- **Print Support**: Formatted printing with proper page breaks
- **Export Ready**: Placeholder for CSV/Excel export

## Best Practices

### During Competition

1. Have range officers start entering results as soon as first series completes
2. Generate preliminary results list early to catch any errors
3. Keep the results page open on a display for shooters to view
4. Regenerate the list periodically to ensure it's up-to-date

### Before Making Official

1. Verify all shooters have completed all series
2. Check for any obviously incorrect scores
3. Confirm with range officers that all entries are complete
4. Review the printed version if needed

### After Making Official

1. Export results for archival purposes
2. Share URL to official results page
3. If corrections needed:
   - Revert to preliminary
   - Make corrections
   - Verify changes
   - Re-mark as official

## Troubleshooting

### Results Not Updating

1. Check that "Skapa Resultatlista" has been clicked at least once
2. Verify auto-refresh is not paused (look for pause/resume button)
3. Check browser console for any errors
4. Try manually refreshing the page

### Concurrent Entry Issues

1. Ensure each range officer is working on different shooters
2. If collision occurs, the system will warn the second RO
3. Wait for first RO to finish, then retry

### Missing Results

1. Verify results were saved successfully (check for success message)
2. Check that "Skapa Resultatlista" was clicked after entering new results
3. Verify the correct competition ID is being used
4. Check database directly if needed:
   ```sql
   SELECT * FROM PrecisionResultEntry WHERE CompetitionId = 2171
   ```

## Future Enhancements

### Planned Features

- SignalR integration for true real-time updates (no page refresh needed)
- Export to Excel/CSV functionality
- Email notifications when results are made official
- Mobile-optimized display for shooters
- QR code generation for easy access to results

### Performance Optimization

- Implement caching for frequently accessed results
- Add pagination for competitions with many shooters
- Optimize database queries for large datasets
- Implement compression for large result datasets

## Security Considerations

- Only authenticated members can enter results
- Anti-forgery tokens protect all POST requests
- Range officer ID is tracked for audit purposes
- Official status changes should be restricted to administrators (future enhancement)

## Summary

This system provides a complete solution for managing competition results in real-time:

✅ **Real-time entry** - Multiple range officers can work concurrently  
✅ **Live updates** - Viewers see results refresh automatically every 15 seconds  
✅ **Preliminary/Official workflow** - Clear distinction between draft and final results  
✅ **Professional display** - Formatted tables grouped by shooting class  
✅ **User-friendly** - Collapsible sections, clear status indicators, print support  
✅ **Reliable** - Handles concurrent writes, auto-updates, reversible status changes  

The system is now fully operational and ready for use in live competitions.





