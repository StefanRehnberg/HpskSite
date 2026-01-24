# Training Match Team System

This document describes the team-based competition support for training matches.

## Overview

The team system allows shooters to compete in teams during training matches. Teams have names, optional club affiliations, and combined scores calculated from individual adjusted scores. This adds a collaborative element to training while maintaining individual scoring.

## Key Features

- **Team Matches**: Toggle to enable team-based competition
- **Open vs Closed Teams**: Open matches allow dynamic team creation; closed matches have pre-defined teams
- **Team Scoring**: Combined team scores based on participant adjusted scores
- **Real-time Updates**: SignalR broadcasts team score changes
- **Max Shooters Per Team**: Configurable limit on team size
- **Club Affiliations**: Optional club association for teams

---

## Database Schema

### New Table: `TrainingMatchTeams`

```sql
CREATE TABLE TrainingMatchTeams (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    TrainingMatchId INT NOT NULL,
    TeamNumber INT NOT NULL,           -- 1, 2, 3, 4
    TeamName NVARCHAR(100) NOT NULL,   -- e.g., "HPSK", "Gästlaget"
    ClubId INT NULL,                   -- Optional club affiliation
    DisplayOrder INT NOT NULL DEFAULT 0,

    FOREIGN KEY (TrainingMatchId) REFERENCES TrainingMatches(Id) ON DELETE CASCADE,
    UNIQUE (TrainingMatchId, TeamNumber)
);
```

### Modified Tables

**TrainingMatches** (new columns):
```sql
IsTeamMatch BIT NOT NULL DEFAULT 0
MaxShootersPerTeam INT NULL  -- Required when IsTeamMatch=1
```

**TrainingMatchParticipants** (new columns):
```sql
TeamId INT NULL FOREIGN KEY REFERENCES TrainingMatchTeams(Id)
```

---

## Data Models

### TrainingMatchTeam

Location: `src/HpskSite.Shared/Models/TrainingMatchTeam.cs`

```csharp
public class TrainingMatchTeam
{
    public int Id { get; set; }
    public int TrainingMatchId { get; set; }
    public int TeamNumber { get; set; }          // 1, 2, 3, 4
    public string TeamName { get; set; }          // Team display name
    public int? ClubId { get; set; }              // Optional club ID
    public string? ClubName { get; set; }         // Populated via ClubService
    public int DisplayOrder { get; set; }

    // Calculated properties (populated by controller)
    public int ParticipantCount { get; set; }
    public int TeamScore { get; set; }            // Sum of raw scores
    public int AdjustedTeamScore { get; set; }    // Sum of adjusted scores
    public int TotalXCount { get; set; }          // Sum of X counts
    public int Rank { get; set; }                 // Team ranking
}
```

### TrainingMatch (updated)

```csharp
public class TrainingMatch
{
    // ... existing properties ...

    public bool IsTeamMatch { get; set; }
    public int? MaxShootersPerTeam { get; set; }
    public List<TrainingMatchTeam>? Teams { get; set; }
}
```

### TrainingMatchParticipant (updated)

```csharp
public class TrainingMatchParticipant
{
    // ... existing properties ...

    public int? TeamId { get; set; }
    public string? TeamName { get; set; }  // Denormalized for display
}
```

---

## Team Score Calculation

Team scores are calculated as the sum of all team members' adjusted scores:

```csharp
TeamScore = participants
    .Where(p => p.TeamId == teamId)
    .Sum(p => p.AdjustedTotalScore);
```

**Notes:**
- Uses adjusted scores (with handicap applied per-series)
- Team ranking based on AdjustedTeamScore
- X-count used as tiebreaker

---

## API Endpoints

### Web Controllers (`TrainingMatchController.cs`)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/umbraco/surface/TrainingMatch/CreateMatch` | POST | Create match with optional team setup |
| `/umbraco/surface/TrainingMatch/JoinMatch` | POST | Join match with optional team selection |
| `/umbraco/surface/TrainingMatch/CreateTeam` | POST | Create new team (open matches) |
| `/umbraco/surface/TrainingMatch/ChangeTeam` | POST | Switch to different team |
| `/umbraco/surface/TrainingMatch/GetTeams` | GET | Get teams for a match |

### Mobile API (`MatchApiController.cs`)

| Endpoint | Method | Description |
|----------|--------|-------------|
| `POST /api/match` | POST | Create match with team config |
| `POST /api/match/{code}/join` | POST | Join with optional teamId |
| `POST /api/match/{code}/team` | POST | Create new team |
| `GET /api/match/{code}` | GET | Get match with teams |

### Request/Response Examples

**Create Team Match (Closed)**:
```json
POST /api/match
{
    "matchName": "Klubbmatch",
    "weaponClass": "C",
    "isTeamMatch": true,
    "maxShootersPerTeam": 4,
    "isOpen": false,
    "teams": [
        { "teamNumber": 1, "teamName": "HPSK", "clubId": 1098 },
        { "teamNumber": 2, "teamName": "Gästlaget" }
    ]
}
```

**Join Team Match**:
```json
POST /api/match/ABC123/join
{
    "teamId": 5
}
```

**Create New Team (Open Match)**:
```json
POST /api/match/ABC123/team
{
    "teamName": "Nya laget",
    "clubId": null
}
```

---

## SignalR Events

### TeamScoreUpdated

Broadcast to all match viewers when team scores change.

**Server (TrainingMatchHub.cs)**:
```csharp
await hubContext.Clients.Group($"match_{matchCode}")
    .SendAsync("TeamScoreUpdated", teamScores);
```

**Client (Mobile)**:
```csharp
_hubConnection.On<JsonElement>("TeamScoreUpdated", jsonElement =>
{
    TeamScoreUpdated?.Invoke(this, jsonElement);
});
```

---

## UI Implementation

### Web UI

**Create Match (`TrainingMatchCreate.cshtml`)**:
- Team match toggle checkbox
- Max shooters per team input (required for team matches)
- Team count dropdown (2-4, for closed matches)
- Team name inputs with optional club picker
- Info text explaining open vs closed behavior

**Join Match (`TrainingMatchJoin.cshtml`)**:
- Team selection modal for team matches
- Shows team names, club affiliations, participant counts
- Auto-suggests team based on user's primaryClubId
- "Create new team" option for open matches

**Scoreboard (`TrainingMatchScoreboard.cshtml`)**:
- Team scores section with ranking table
- View toggle: Individual vs Team view
- Team badge in match header
- Participants grouped by team with visual separation

### Mobile App

**CreateMatchPage.xaml**:
- Team match toggle (`IsTeamMatch`)
- Max shooters per team picker
- Team count picker (conditional on closed match)
- Team name entries (Team 1-4)

**JoinMatchPage.xaml**:
- Team selection card (appears after match code entry)
- CollectionView of available teams
- "Create new team" button and name entry (open matches)
- Cancel/Join buttons

**ActiveMatchPage** (ViewModel):
- `IsTeamMatch`, `Teams`, `TeamRankings` properties
- `ShowTeamScores` toggle
- `ToggleTeamScoresView` command
- Real-time team score updates via SignalR

---

## Match Types

### Open Team Match

- Anyone can join
- Joining shooters can:
  - Select an existing team
  - Create a new team dynamically
- Teams are created as needed

### Closed Team Match

- Join requests require approval
- Teams are pre-defined by match creator
- Participants must choose from existing teams
- No team creation during match

---

## Validation Rules

| Rule | Description |
|------|-------------|
| MaxShootersPerTeam | Required when IsTeamMatch=true |
| Team capacity | Cannot join team at max capacity |
| Unique team names | Team names must be unique within match |
| Team required | Must select/create team when joining team match |
| Pre-defined teams | Closed matches require team definitions at creation |

---

## Files Modified

### Backend

| File | Changes |
|------|---------|
| `Migrations/AddTeamSupportToTrainingMatches.cs` | New migration file |
| `Models/TrainingMatchTeam.cs` | New model (Shared) |
| `Models/TrainingMatch.cs` | Added team properties (Shared) |
| `Models/TrainingMatchParticipant.cs` | Added TeamId/TeamName (Shared) |
| `Controllers/TrainingMatchController.cs` | Team handling in all match operations |
| `Controllers/Api/MatchApiController.cs` | Team handling for mobile API |
| `Hubs/TrainingMatchHub.cs` | TeamScoreUpdated event |

### Web UI

| File | Changes |
|------|---------|
| `Views/Partials/TrainingMatchCreate.cshtml` | Team creation UI |
| `Views/Partials/TrainingMatchJoin.cshtml` | Team selection modal |
| `Views/Partials/TrainingMatchScoreboard.cshtml` | Team scores display |
| `Views/TrainingMatch.cshtml` | Team-aware functions |

### Mobile App

| File | Changes |
|------|---------|
| `ViewModels/CreateMatchViewModel.cs` | Team properties and creation |
| `Views/CreateMatchPage.xaml` | Team configuration UI |
| `ViewModels/JoinMatchViewModel.cs` | Team selection handling |
| `Views/JoinMatchPage.xaml` | Team selection UI |
| `ViewModels/ActiveMatchViewModel.cs` | Team display and rankings |
| `Services/MatchService.cs` | CreateTeamAsync method |
| `Services/SignalRService.cs` | TeamScoreUpdated event |

---

## Testing Checklist

### Create Team Match
- [ ] Create closed team match with 2-4 pre-defined teams
- [ ] Create open team match (no pre-defined teams)
- [ ] Verify MaxShootersPerTeam is required
- [ ] Verify team names are saved correctly

### Join Team Match
- [ ] Join team match - team selection modal appears
- [ ] Select existing team and join
- [ ] Create new team (open match) and join
- [ ] Verify team assignment shows in scoreboard
- [ ] Cannot join team at max capacity

### Scoreboard Display
- [ ] Team scores section visible for team matches
- [ ] Team rankings update in real-time
- [ ] Toggle between individual and team views
- [ ] Participants grouped by team

### Real-time Updates
- [ ] TeamScoreUpdated broadcasts when scores change
- [ ] All viewers receive team score updates
- [ ] Team rankings update correctly

### Edge Cases
- [ ] Single shooter in team
- [ ] Empty teams display correctly
- [ ] Team at max capacity prevention
- [ ] Non-team matches unchanged (backward compatibility)

---

## History

- **2026-01-24**: Initial implementation of team support for training matches

---

**See Also:**
- `TRAINING_MATCH_HANDICAP_SYSTEM.md` - Handicap calculation rules
- `TRAINING_SCORING_SYSTEM.md` - Personal training scoring (different system)
