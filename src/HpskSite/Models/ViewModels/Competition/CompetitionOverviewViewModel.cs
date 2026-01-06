namespace HpskSite.Models.ViewModels.Competition
{
    public class CompetitionOverviewViewModel
    {
        // Current user context
        public int? CurrentMemberId { get; set; }
        public string CurrentMemberName { get; set; } = "";
        public bool IsAdmin { get; set; }

        // Active competitions
        public List<CompetitionSummary> ActiveCompetitions { get; set; } = new List<CompetitionSummary>();
        public List<CompetitionSummary> UpcomingCompetitions { get; set; } = new List<CompetitionSummary>();
        public List<CompetitionSummary> CompletedCompetitions { get; set; } = new List<CompetitionSummary>();

        // User's current registrations
        public List<MyCompetitionRegistration> MyRegistrations { get; set; } = new List<MyCompetitionRegistration>();

        // Overall statistics
        public CompetitionStatistics Statistics { get; set; } = new CompetitionStatistics();

        // Recent activity
        public List<RecentActivity> RecentActivities { get; set; } = new List<RecentActivity>();

        // Get user's active registrations
        public List<MyCompetitionRegistration> GetActiveRegistrations()
        {
            return MyRegistrations.Where(r => r.IsActive && !r.IsCompleted).ToList();
        }

        // Get user's completed registrations
        public List<MyCompetitionRegistration> GetCompletedRegistrations()
        {
            return MyRegistrations.Where(r => r.IsCompleted).ToList();
        }
    }

    public class CompetitionSummary
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string CompetitionType { get; set; } = "";
        public DateTime CompetitionDate { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; }

        // Registration info
        public int TotalRegistrations { get; set; }
        public int MaxParticipants { get; set; }
        public bool CanRegister { get; set; }
        public DateTime? RegistrationDeadline { get; set; }

        // Status
        public string Status { get; set; } = "";
        public bool IsRegistrationOpen => CanRegister && IsActive;
        public bool IsFull => TotalRegistrations >= MaxParticipants;

        // User's involvement
        public bool IsUserRegistered { get; set; }
        public int? UserRegistrationId { get; set; }

        public string GetStatusDisplay()
        {
            if (CompetitionDate < DateTime.Now.Date) return "Avslutad";
            if (CompetitionDate == DateTime.Now.Date) return "Pågår idag";
            if (!IsActive) return "Inaktiv";
            if (IsFull) return "Fullbokad";
            if (IsRegistrationOpen) return "Öppen för anmälan";
            return "Kommande";
        }

        public string GetStatusColor()
        {
            return GetStatusDisplay() switch
            {
                "Pågår idag" => "#FF5722",      // Red-orange
                "Öppen för anmälan" => "#4CAF50", // Green
                "Kommande" => "#2196F3",        // Blue
                "Fullbokad" => "#FF9800",       // Orange
                "Avslutad" => "#9E9E9E",        // Gray
                "Inaktiv" => "#757575",         // Dark gray
                _ => "#9E9E9E"
            };
        }
    }

    public class MyCompetitionRegistration
    {
        public int RegistrationId { get; set; }
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public DateTime CompetitionDate { get; set; }
        public string MemberClass { get; set; } = "";
        public int? StartNumber { get; set; }

        // Progress
        public bool IsActive { get; set; }
        public bool IsCompleted { get; set; }
        public int CompletedSeries { get; set; }
        public int TotalSeries { get; set; }

        // Current scores
        public decimal CurrentTotal { get; set; }
        public decimal BestSeriesScore { get; set; }
        public int TotalInnerTens { get; set; }
        public int TotalTens { get; set; }

        // Next action
        public string NextAction { get; set; } = "";
        public bool CanContinue { get; set; }

        public string GetProgressDisplay()
        {
            if (!IsActive) return "Inaktiv";
            if (IsCompleted) return $"Avslutad - {CurrentTotal:F1}p";
            if (CompletedSeries == 0) return "Inte påbörjad";
            return $"Pågående ({CompletedSeries}/{TotalSeries} serier)";
        }

        public decimal GetProgressPercentage()
        {
            return TotalSeries > 0 ? (decimal)CompletedSeries / TotalSeries * 100 : 0;
        }
    }

    public class CompetitionStatistics
    {
        public int TotalCompetitions { get; set; }
        public int ActiveCompetitions { get; set; }
        public int TotalParticipants { get; set; }
        public int UniqueParticipants { get; set; }

        // Performance stats
        public decimal AverageScore { get; set; }
        public decimal HighestScore { get; set; }
        public string? HighestScoreHolder { get; set; }
        public int TotalInnerTens { get; set; }
        public int TotalTens { get; set; }

        // Popular classes
        public Dictionary<string, int> ClassDistribution { get; set; } = new Dictionary<string, int>();

        // Recent trends
        public List<MonthlyStats> MonthlyTrends { get; set; } = new List<MonthlyStats>();
    }

    public class MonthlyStats
    {
        public string Month { get; set; } = "";
        public int Competitions { get; set; }
        public int Participants { get; set; }
        public decimal AverageScore { get; set; }
    }

    public class RecentActivity
    {
        public string ActivityType { get; set; } = ""; // "registration", "series_completed", "competition_finished"
        public string Description { get; set; } = "";
        public DateTime ActivityDate { get; set; }
        public string? MemberName { get; set; }
        public string? CompetitionName { get; set; }
        public decimal? Score { get; set; }

        public string GetActivityIcon()
        {
            return ActivityType switch
            {
                "registration" => "bi-person-plus",
                "series_completed" => "bi-check-circle",
                "competition_finished" => "bi-trophy",
                "new_competition" => "bi-calendar-plus",
                _ => "bi-info-circle"
            };
        }

        public string GetActivityColor()
        {
            return ActivityType switch
            {
                "registration" => "#2196F3",      // Blue
                "series_completed" => "#4CAF50",  // Green
                "competition_finished" => "#FF9800", // Orange
                "new_competition" => "#9C27B0",   // Purple
                _ => "#757575"                    // Gray
            };
        }

        public string GetRelativeTime()
        {
            var timeSpan = DateTime.Now - ActivityDate;

            return timeSpan.TotalDays switch
            {
                < 1 when timeSpan.TotalHours < 1 => $"{(int)timeSpan.TotalMinutes} min sedan",
                < 1 => $"{(int)timeSpan.TotalHours} tim sedan",
                < 7 => $"{(int)timeSpan.TotalDays} dag{((int)timeSpan.TotalDays == 1 ? "" : "ar")} sedan",
                < 30 => $"{(int)(timeSpan.TotalDays / 7)} vecka{((int)(timeSpan.TotalDays / 7) == 1 ? "" : "r")} sedan",
                _ => ActivityDate.ToString("yyyy-MM-dd")
            };
        }
    }
}