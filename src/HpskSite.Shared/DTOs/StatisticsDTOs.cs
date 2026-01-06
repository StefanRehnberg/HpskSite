namespace HpskSite.Shared.DTOs;

/// <summary>
/// Dashboard statistics response
/// </summary>
public class DashboardStatistics
{
    public int Year { get; set; }
    public int TotalSessions { get; set; }
    public int TrainingSessions { get; set; }
    public int CompetitionSessions { get; set; }
    public double ThirtyDayAverage { get; set; }
    public double OverallAverage { get; set; }
    public int BestSeriesScore { get; set; }
    public int BestMatchScore { get; set; }
    public List<WeaponClassStat> WeaponClassStats { get; set; } = new List<WeaponClassStat>();
    public List<ActivityEntry> RecentActivity { get; set; } = new List<ActivityEntry>();
}

/// <summary>
/// Statistics per weapon class
/// </summary>
public class WeaponClassStat
{
    public string WeaponClass { get; set; } = string.Empty;
    public int TotalSessions { get; set; }
    public double Average { get; set; }
    public int BestScore { get; set; }
}

/// <summary>
/// Recent activity entry
/// </summary>
public class ActivityEntry
{
    public DateTime Date { get; set; }
    public string WeaponClass { get; set; } = string.Empty;
    public int Score { get; set; }
    public int XCount { get; set; }
    public int SeriesCount { get; set; }
    public bool IsCompetition { get; set; }
    /// <summary>
    /// Source type: "Training" (self-entered), "TrainingMatch" (app match), "Competition" (official)
    /// </summary>
    public string SourceType { get; set; } = "Training";
}

/// <summary>
/// Progress chart data
/// </summary>
public class ProgressChartData
{
    public int Year { get; set; }
    public List<ChartDataPoint> DataPoints { get; set; } = new List<ChartDataPoint>();
}

/// <summary>
/// Chart data point
/// </summary>
public class ChartDataPoint
{
    public DateTime Date { get; set; }
    public double Score { get; set; }
    public int XCount { get; set; }
    public string? Label { get; set; }
}

/// <summary>
/// Handicap information for a weapon class
/// </summary>
public class WeaponClassHandicap
{
    public string WeaponClass { get; set; } = string.Empty;
    public string WeaponClassName { get; set; } = string.Empty;
    public decimal HandicapPerSeries { get; set; }
    public bool IsProvisional { get; set; }
    public int CompletedMatches { get; set; }
    public int RequiredMatches { get; set; }
    public decimal EffectiveAverage { get; set; }
    public decimal ActualAverage { get; set; }
    public decimal ReferenceScore { get; set; }
}

/// <summary>
/// Handicap profile containing all weapon class handicaps
/// </summary>
public class HandicapProfile
{
    public string? ShooterClass { get; set; }
    public List<WeaponClassHandicap> WeaponClasses { get; set; } = new List<WeaponClassHandicap>();
}
