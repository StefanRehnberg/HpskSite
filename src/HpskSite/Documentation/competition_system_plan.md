# Umbraco v16 Competition System - Complete Plan

## 1. Document Types & Compositions

### A. Extend Existing BasePage Composition
```csharp
// Already exists - your current BasePage with:
// - pageTitle, metaDescription, hideFromNavigation
```

### B. New Compositions (Reusable)

**DateManagement** (Composition)
```csharp
├── competitionDate (Date Picker with time) *Required*
├── registrationDeadline (Date Picker with time) *Required*
├── registrationStartDate (Date Picker with time)
```

**ContactInfo** (Composition) - *Extends your club system*
```csharp
├── contactPerson (Textstring)
├── contactEmail (Email Address)
├── contactPhone (Textstring)
├── venue (Textstring)
```

**CompetitionSettings** (Composition)
```csharp
├── isRegistrationOpen (True/False) *Required*
├── requiresApproval (True/False)
├── allowLateCancellation (True/False)
├── earlyBirdDeadline (Date Picker)
├── lateRegistrationFee (Decimal)
├── competitionFee (Decimal)
```

### C. Main Document Types

**CompetitionsRoot** - *Container*
```csharp
Compositions: BasePage
Icon: icon-target-two
Template: CompetitionsHome
├── introText (Rich Text Editor)
├── upcomingTitle (Textstring) - Default: "Kommande tävlingar"
├── pastTitle (Textstring) - Default: "Tidigare tävlingar"
├── featuredCompetitions (Multinode Treepicker → Competition)
```

**CompetitionType** - *Competition Categories*
```csharp
Compositions: BasePage
Icon: icon-medal
Template: CompetitionTypeDetail
├── typeName (Textstring) *Required* // "Precision", "Springskytte"
├── typeCode (Textstring) *Required* // "PREC", "SPRING"
├── description (Rich Text Editor)
├── scoringMethod (Dropdown) *Required*
│   ├── "Points Only"
│   ├── "Time Only"
│   ├── "Points + Time"
│   ├── "Placement Only"
├── availableClasses (Checkboxlist) *Required* // Classes available for this competition type
│   ├── "Klass 1"
│   ├── "Klass 2"
│   ├── "Klass 3"
│   ├── "Junior"
│   ├── "Veteran Yngre"
│   ├── "Veteran Äldre"
│   ├── "Dam 1"
│   ├── "Dam 2"
│   ├── "Dam 3"
├── targetInfo (Textstring) // "25m, precision target"
├── seriesCount (Numeric) *Required* // How many series per competition
├── shotsPerSeries (Numeric) *Required*
├── isActive (True/False)
├── sortOrder (Numeric)
```

**Competition** - *Individual Competitions*
```csharp
Compositions: BasePage, DateManagement, ContactInfo, CompetitionSettings
Icon: icon-calendar-alt
Template: CompetitionDetail
├── competitionType (Content Picker → CompetitionType) *Required*
├── description (Rich Text Editor)
├── additionalInfo (Rich Text Editor)
├── allowedClubs (Multinode Treepicker → Club content or "All")
├── competitionStatus (Dropdown) *Required*
│   ├── "Upcoming"
│   ├── "Registration Open"
│   ├── "Registration Closed" 
│   ├── "In Progress"
│   ├── "Completed"
│   ├── "Cancelled"
```

**CompetitionResults** - *Results Pages*
```csharp
Compositions: BasePage
Icon: icon-trophy
Template: CompetitionResults  
├── competition (Content Picker → Competition) *Required*
├── resultsDate (Date Picker) *Required*
├── officialResults (True/False)
├── resultsSummary (Rich Text Editor)
├── photoGallery (Multiple Media Picker)
├── nextCompetition (Content Picker → Competition)
```

## 2. Extend Existing Member Properties

**Add Competition-Related Properties to Member Type:**
Nothing to add
```

## 3. Custom Database Tables

**CompetitionRegistrations**
```sql
CREATE TABLE CompetitionRegistrations (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    CompetitionId INT NOT NULL, -- Umbraco content ID
    MemberId INT NOT NULL, -- Umbraco member ID  
    RegisteredDate DATETIME NOT NULL,
    ShootingClass NVARCHAR(50) NOT NULL, 
    Status NVARCHAR(50) NOT NULL, -- 'Registered', 'Confirmed', 'Cancelled', 'NoShow'
    PaymentStatus NVARCHAR(50), -- 'Pending', 'Paid', 'Refunded'
    SpecialRequests NVARCHAR(500),
    CheckedInDate DATETIME NULL,
    Notes NVARCHAR(1000)
);
```

**CompetitionResults**
```sql
CREATE TABLE CompetitionResults (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    CompetitionId INT NOT NULL,
    MemberId INT NOT NULL,
    ShootingClass NVARCHAR(50) NOT NULL, -- Same as registration
    SeriesNumber INT NOT NULL,
    Score DECIMAL(5,2) NOT NULL,
    TotalScore DECIMAL(6,2) NULL, -- Sum across all series
    TimeSeconds DECIMAL(6,2) NULL, -- For time-based competitions
    XCount INT DEFAULT 0, -- Number of X-ring hits
    Placement INT NULL, -- Calculated per class
    OverallPlacement INT NULL, -- Placement across all classes
    Details NVARCHAR(MAX), -- JSON for detailed shot-by-shot data
    RecordedDate DATETIME NOT NULL,
    RecordedByMemberId INT NULL,
    IsOfficial BIT DEFAULT 0,
    Notes NVARCHAR(500)
);
```

## 4. Models & ViewModels

**Extend Your Existing Models:**

**CompetitionType.cs**
```csharp
public class CompetitionType : BasePage
{
    public CompetitionType(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
        : base(content, publishedValueFallback) { }

    public string TypeName => this.Value<string>("typeName") ?? "";
    public string TypeCode => this.Value<string>("typeCode") ?? "";
    public string ScoringMethod => this.Value<string>("scoringMethod") ?? "Points Only";
    public IEnumerable<string> AvailableClasses => this.Value<IEnumerable<string>>("availableClasses") ?? Enumerable.Empty<string>();
    public int SeriesCount => this.Value<int>("seriesCount");
    public int ShotsPerSeries => this.Value<int>("shotsPerSeries");
    public bool IsActive => this.Value<bool>("isActive");
}
```

**Competition.cs**
```csharp
public class Competition : BasePage
{
    public Competition(IPublishedContent content, IPublishedValueFallback publishedValueFallback)
        : base(content, publishedValueFallback) { }

    public DateTime CompetitionDate => this.Value<DateTime>("competitionDate");
    public DateTime RegistrationDeadline => this.Value<DateTime>("registrationDeadline");
    public CompetitionType CompetitionType => this.Value<IPublishedContent>("competitionType")?.As<CompetitionType>();
    public string Venue => this.Value<string>("venue") ?? "";
    public bool IsRegistrationOpen => this.Value<bool>("isRegistrationOpen");
    public string CompetitionStatus => this.Value<string>("competitionStatus") ?? "Upcoming";
    public decimal CompetitionFee => this.Value<decimal>("competitionFee");
    
    // Computed properties
    public bool CanRegister => IsRegistrationOpen && DateTime.Now <= RegistrationDeadline;
    public bool IsUpcoming => CompetitionDate > DateTime.Now;
    public bool IsPast => CompetitionDate < DateTime.Now;
}
```

**New ViewModels:**

**CompetitionRegistrationViewModel.cs**
```csharp
public class CompetitionRegistrationViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; }
    public DateTime CompetitionDate { get; set; }
    public int MemberId { get; set; }
    public string MemberName { get; set; }
    public string ShootingClass { get; set; } = ""; // Selected by member during registration
    public List<string> AvailableClasses { get; set; } = new(); // From CompetitionType
    public string SpecialRequests { get; set; }
    public bool AcceptTerms { get; set; }
    public decimal Fee { get; set; }
    public string PaymentMethod { get; set; }
}
```

**CompetitionResultEntryViewModel.cs**
```csharp
public class CompetitionResultEntryViewModel
{
    public int CompetitionId { get; set; }
    public List<ParticipantResult> Participants { get; set; } = new();
}

public class ParticipantResult
{
    public int MemberId { get; set; }
    public string MemberName { get; set; }
    public string ShootingClass { get; set; }
    public List<SeriesResult> Series { get; set; } = new();
    public decimal TotalScore { get; set; }
    public int TotalXCount { get; set; }
    public decimal? TotalTime { get; set; }
}

public class SeriesResult
{
    public int SeriesNumber { get; set; }
    public decimal Score { get; set; }
    public int XCount { get; set; }
    public decimal? TimeSeconds { get; set; }
    public List<int> ShotScores { get; set; } = new(); // Individual shot scores
}
```

## 5. Controllers & API Endpoints

**CompetitionsController.cs** - *Surface Controller*
```csharp
[Route("api/competitions")]
public class CompetitionsController : SurfaceController
{
    [HttpGet("upcoming")]
    public IActionResult GetUpcoming() { }
    
    [HttpGet("{id}/details")]
    public IActionResult GetDetails(int id) { }
    
    [HttpPost("{id}/register")]
    [Authorize] // Uses your existing member auth
    public IActionResult Register(int id, CompetitionRegistrationViewModel model) { }
    
    [HttpPost("{id}/cancel")]
    [Authorize]
    public IActionResult CancelRegistration(int id) { }
    
    [HttpGet("my-competitions")]
    [Authorize]
    public IActionResult GetMyCompetitions() { }
}
```

**CompetitionResultsController.cs** - *Admin API*
```csharp
[Route("api/competition-results")]
[Authorize(Roles = "CompetitionAdmin,ClubAdmin")]
public class CompetitionResultsController : SurfaceController
{
    [HttpPost("{competitionId}/enter-results")]
    public IActionResult EnterResults(int competitionId, CompetitionResultEntryViewModel model) { }
    
    [HttpGet("{competitionId}/participants")]
    public IActionResult GetParticipants(int competitionId) { }
    
    [HttpGet("{competitionId}/participants-by-class")]
    public IActionResult GetParticipantsByClass(int competitionId, string shootingClass = null) { }
    
    [HttpPost("{competitionId}/calculate-placements")]
    public IActionResult CalculatePlacements(int competitionId) { } // Calculates both class and overall placements
    
    [HttpPost("{competitionId}/check-training-requirements")]
    public IActionResult CheckTrainingRequirements(int competitionId) { }
    
    [HttpGet("{competitionId}/class-results")]
    public IActionResult GetResultsByClass(int competitionId, string shootingClass) { }
}
```

## 6. Content Structure

**Proposed Site Tree:**
```
Home/
├── Competitions/
│   ├── Competition Types/
│   │   ├── Precision Shooting
│   │   ├── Springskytte
│   │   └── [Future Types]
│   ├── Upcoming Competitions/
│   │   ├── [Individual Competitions]
│   ├── Past Competitions/
│   │   ├── 2024/
│   │   │   ├── [Competition Results]
│   │   └── 2025/
│   │       ├── [Competition Results]
│   └── How To Compete/ (ContentPage)
├── Training/ (Your existing training system)
└── [Your existing content]
```

## 7. Admin Dashboard Integration

**Custom Umbraco Dashboard Sections:**

**Competition Management Dashboard**
- Competition calendar view
- Registration management
- Quick result entry
- Member competition history
- Training requirement tracking

**Reports Dashboard**
- Competition participation statistics
- Training progression linked to competitions
- Club performance comparisons
- Trend analysis

## 8. Templates & Views

**Key Templates:**
- `CompetitionsHome.cshtml` - Main competitions landing
- `CompetitionDetail.cshtml` - Individual competition with class-based registration
- `CompetitionResults.cshtml` - Results display with class groupings and overall standings
- `MyCompetitions.cshtml` - Member's competition history showing classes competed in
- `CompetitionTypeDetail.cshtml` - Competition type information with class descriptions

## 9. Integration with Training System

**Link Competitions to Training:**
- Automatic tracking when competition requirements are met
- Badge/achievement notifications
- Progress visualization
- Training-specific competition recommendations

**Training Competition Requirements:**
- Mark competitions that fulfill training step requirements
- Automatic verification of results against training criteria
- Cross-reference with your `TrainingDefinitions.cs`

## 10. Security & Permissions

**Member Groups:**
- `CompetitionAdmin` - Full competition management
- `ClubAdmin` - Manage club members' registrations
- `ResultsRecorder` - Enter competition results
- `Members` - Register for competitions

**Content Permissions:**
- Competition creation/editing restricted to admins
- Results entry with proper audit trail
- Member data protection compliance

## 11. Data Flow & User Journeys

**Member Registration Flow:**
1. Browse upcoming competitions
2. Check eligibility (training level, club membership)
3. **Select shooting class** from available options for competition type
4. Register with payment
5. Receive confirmation
6. Check-in at event (confirm class if needed)
7. View results post-competition (both class placement and overall)

**Admin Competition Management:**
1. Create competition type (if new) with available shooting classes
2. Create competition instance
3. Monitor registrations by class
4. Check-in participants and confirm shooting classes
5. Enter results during/after event
6. **Calculate placements separately for each class**
7. Publish official results with class-based and overall rankings
8. Update training progress for qualifying results

## 12. Future Enhancements

**Phase 2 Features:**
- Payment integration (Stripe/PayPal)
- Email notifications
- Mobile app support
- Live scoring during competitions
- Advanced statistics and analytics by shooting class
- Class progression tracking and recommendations
- Integration with Svenska Pistolskytteförbundet systems
- Automatic class suggestions based on member performance history


# Precision Competition Scoring System

## Database Schema Updates

### CompetitionSeries Table
```sql
CREATE TABLE CompetitionSeries (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    CompetitionId INT NOT NULL, -- Umbraco content ID
    MemberId INT NOT NULL, -- Umbraco member ID
    ShootingClass NVARCHAR(50) NOT NULL,
    SeriesNumber INT NOT NULL, -- 1-6 for standard, 1-10 for finale format
    IsFinale BIT DEFAULT 0, -- TRUE for finale series (series 8,9,10)
    Shot1 NVARCHAR(2) NOT NULL, -- '1'-'10' or 'X'
    Shot2 NVARCHAR(2) NOT NULL,
    Shot3 NVARCHAR(2) NOT NULL, 
    Shot4 NVARCHAR(2) NOT NULL,
    Shot5 NVARCHAR(2) NOT NULL,
    SeriesScore INT NOT NULL, -- Calculated: sum of shots (X counts as 10)
    XCount INT NOT NULL, -- Number of X's in this series
    RecordedDate DATETIME NOT NULL,
    RecordedByMemberId INT NULL,
    Notes NVARCHAR(500),
    CONSTRAINT FK_CompetitionSeries_Competition FOREIGN KEY (CompetitionId) REFERENCES Competitions(Id),
    CONSTRAINT FK_CompetitionSeries_Member FOREIGN KEY (MemberId) REFERENCES Members(Id)
);
```

### CompetitionTotals Table (Calculated from Series)
```sql
CREATE TABLE CompetitionTotals (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    CompetitionId INT NOT NULL,
    MemberId INT NOT NULL,
    ShootingClass NVARCHAR(50) NOT NULL,
    SeriesCount INT NOT NULL, -- 6 or 10 depending on format
    TotalScore INT NOT NULL, -- Sum of all series
    TotalXCount INT NOT NULL, -- Sum of all X's
    AveragePerSeries DECIMAL(4,2) NOT NULL, -- TotalScore / SeriesCount
    ClassPlacement INT NULL,
    OverallPlacement INT NULL,
    IsOfficial BIT DEFAULT 0,
    CalculatedDate DATETIME NOT NULL,
    UNIQUE(CompetitionId, MemberId) -- One total per member per competition
);
```

## Models for Precision Scoring

### PrecisionSeries.cs
```csharp
public class PrecisionSeries
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public int MemberId { get; set; }
    public string ShootingClass { get; set; } = "";
    public int SeriesNumber { get; set; }
    public bool IsFinale { get; set; }
    
    // Individual shots - stored as strings to handle 'X'
    public string Shot1 { get; set; } = "";
    public string Shot2 { get; set; } = "";
    public string Shot3 { get; set; } = "";
    public string Shot4 { get; set; } = "";
    public string Shot5 { get; set; } = "";
    
    // Calculated values
    public int SeriesScore { get; set; }
    public int XCount { get; set; }
    public DateTime RecordedDate { get; set; }
    public int? RecordedByMemberId { get; set; }
    public string Notes { get; set; } = "";
    
    // Helper methods
    public List<string> GetShots() => new() { Shot1, Shot2, Shot3, Shot4, Shot5 };
    
    public void SetShots(List<string> shots)
    {
        if (shots.Count != 5) throw new ArgumentException("Must have exactly 5 shots");
        Shot1 = shots[0];
        Shot2 = shots[1]; 
        Shot3 = shots[2];
        Shot4 = shots[3];
        Shot5 = shots[4];
        CalculateScore();
    }
    
    private void CalculateScore()
    {
        var shots = GetShots();
        SeriesScore = 0;
        XCount = 0;
        
        foreach (var shot in shots)
        {
            if (shot.ToUpper() == "X")
            {
                SeriesScore += 10;
                XCount++;
            }
            else if (int.TryParse(shot, out int value) && value >= 1 && value <= 10)
            {
                SeriesScore += value;
            }
        }
    }
}
```

### PrecisionTotal.cs
```csharp
public class PrecisionTotal
{
    public int Id { get; set; }
    public int CompetitionId { get; set; }
    public int MemberId { get; set; }
    public string MemberName { get; set; } = "";
    public string ShootingClass { get; set; } = "";
    public int SeriesCount { get; set; }
    public int TotalScore { get; set; }
    public int TotalXCount { get; set; }
    public decimal AveragePerSeries { get; set; }
    public int? ClassPlacement { get; set; }
    public int? OverallPlacement { get; set; }
    public bool IsOfficial { get; set; }
    public DateTime CalculatedDate { get; set; }
    
    // UI Helper properties
    public string FormattedScore => $"{TotalScore}/{SeriesCount * 50}"; // e.g., "287/300"
    public string FormattedAverage => AveragePerSeries.ToString("F1"); // e.g., "47.8"
    public string XCountDisplay => $"{TotalXCount}X";
}
```

## ViewModels for Data Entry

### PrecisionSeriesEntryViewModel.cs
```csharp
public class PrecisionSeriesEntryViewModel
{
    public int CompetitionId { get; set; }
    public string CompetitionName { get; set; } = "";
    public List<PrecisionShooterEntry> Shooters { get; set; } = new();
    public int TotalSeries { get; set; } // 6 or 10
    public bool HasFinale { get; set; } // True if 10 series format
}

public class PrecisionShooterEntry
{
    public int MemberId { get; set; }
    public string MemberName { get; set; } = "";
    public string ShootingClass { get; set; } = "";
    public List<PrecisionSeriesEntry> Series { get; set; } = new();
    
    // Calculated from series
    public int TotalScore => Series.Sum(s => s.SeriesScore);
    public int TotalXCount => Series.Sum(s => s.XCount);
    public decimal Average => Series.Count > 0 ? (decimal)TotalScore / Series.Count : 0;
}

public class PrecisionSeriesEntry
{
    public int SeriesNumber { get; set; }
    public bool IsFinale { get; set; }
    public List<string> Shots { get; set; } = new() { "", "", "", "", "" };
    
    // Calculated properties
    public int SeriesScore
    {
        get
        {
            int score = 0;
            foreach (var shot in Shots)
            {
                if (shot?.ToUpper() == "X")
                    score += 10;
                else if (int.TryParse(shot, out int value) && value >= 1 && value <= 10)
                    score += value;
            }
            return score;
        }
    }
    
    public int XCount => Shots.Count(s => s?.ToUpper() == "X");
    
    public bool IsComplete => Shots.All(s => !string.IsNullOrEmpty(s) && IsValidShot(s));
    
    private bool IsValidShot(string shot)
    {
        if (shot?.ToUpper() == "X") return true;
        return int.TryParse(shot, out int value) && value >= 1 && value <= 10;
    }
}
```

## API Controller for Precision Scoring

### PrecisionScoringController.cs
```csharp
[Route("api/precision-scoring")]
[Authorize(Roles = "CompetitionAdmin,ResultsRecorder")]
public class PrecisionScoringController : ControllerBase
{
    [HttpGet("{competitionId}/participants")]
    public IActionResult GetParticipants(int competitionId)
    {
        // Return list of registered participants grouped by class
    }
    
    [HttpGet("{competitionId}/entry-form")]
    public IActionResult GetEntryForm(int competitionId)
    {
        // Return PrecisionSeriesEntryViewModel with participants and empty series
    }
    
    [HttpPost("{competitionId}/save-series")]
    public IActionResult SaveSeries(int competitionId, [FromBody] SaveSeriesRequest request)
    {
        // Save individual series for a shooter
        // Validate shot values (1-10 or X)
        // Calculate and save series score and X count
    }
    
    [HttpPost("{competitionId}/calculate-totals")]
    public IActionResult CalculateTotals(int competitionId)
    {
        // Calculate total scores for all participants
        // Determine placements within each class
        // Determine overall placements
    }
    
    [HttpGet("{competitionId}/results")]
    public IActionResult GetResults(int competitionId, string? shootingClass = null)
    {
        // Return results, optionally filtered by class
    }
    
    [HttpGet("{competitionId}/detailed-results/{memberId}")]
    public IActionResult GetDetailedResults(int competitionId, int memberId)
    {
        // Return series-by-series breakdown for a specific shooter
    }
}

public class SaveSeriesRequest
{
    public int MemberId { get; set; }
    public int SeriesNumber { get; set; }
    public List<string> Shots { get; set; } = new();
    public bool IsFinale { get; set; }
    public string Notes { get; set; } = "";
}
```

## Competition Format Configuration

### Update CompetitionType for Precision
```csharp
// Add to CompetitionType document type:
├── competitionFormat (Dropdown) // For Precision competitions
│   ├── "6 Series Standard"
│   ├── "10 Series with Finale"
├── finaleParticipants (Numeric) // How many advance to finale (e.g., top 8)
```

### CompetitionType.cs Updates
```csharp
public class CompetitionType : BasePage
{
    // ... existing properties ...
    
    public string CompetitionFormat => this.Value<string>("competitionFormat") ?? "6 Series Standard";
    public int FinaleParticipants => this.Value<int>("finaleParticipants");
    
    // Helper properties for Precision competitions
    public bool IsPrecision => TypeCode == "PREC";
    public bool HasFinale => CompetitionFormat == "10 Series with Finale";
    public int TotalSeries => HasFinale ? 10 : 6;
    public int QualificationSeries => HasFinale ? 7 : 6;
}
```

## Results Display Templates

### Precision Results Display
```html
<!-- Example for CompetitionResults.cshtml -->
<div class="precision-results">
    <h2>@Model.Competition.Name - Results</h2>
    
    @foreach(var shootingClass in Model.ShootingClasses)
    {
        <div class="class-results">
            <h3>@shootingClass</h3>
            <table class="results-table">
                <thead>
                    <tr>
                        <th>Placering</th>
                        <th>Namn</th>
                        <th>Klubb</th>
                        @for(int i = 1; i <= Model.TotalSeries; i++)
                        {
                            <th>S@i</th>
                        }
                        <th>Totalt</th>
                        <th>X-antal</th>
                        <th>Snitt</th>
                    </tr>
                </thead>
                <tbody>
                    @foreach(var result in Model.GetResultsForClass(shootingClass))
                    {
                        <tr>
                            <td>@result.ClassPlacement</td>
                            <td>@result.MemberName</td>
                            <td>@result.ClubName</td>
                            @foreach(var series in result.Series)
                            {
                                <td class="@(series.IsFinale ? "finale-series" : "")">
                                    @series.SeriesScore
                                    @if(series.XCount > 0)
                                    {
                                        <span class="x-count">(@series.XCount X)</span>
                                    }
                                </td>
                            }
                            <td class="total-score">@result.FormattedScore</td>
                            <td>@result.XCountDisplay</td>
                            <td>@result.FormattedAverage</td>
                        </tr>
                    }
                </tbody>
            </table>
        </div>
    }
</div>
```

This system provides:
- **Detailed shot tracking** (1-10 and X values)
- **Series-by-series entry** for admins
- **Automatic score calculation** with X counting as 10
- **Separate X count tracking** for tiebreakers
- **Support for both 6 and 10 series formats**
- **Finale series identification** for advanced competitions
- **Class-based and overall rankings**