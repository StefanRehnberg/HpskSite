namespace HpskSite.Models.ViewModels.Competition
{
    public class StartListGenerationRequest
    {
        public int CompetitionId { get; set; }
        public string ClassStartOrder { get; set; } = "A,B,C,R,M,L"; // All 6 weapon classes
        public string TeamFormat { get; set; } = "";
        public int MaxShootersPerTeam { get; set; } = 30;
        public string FirstStartTime { get; set; } = "09:00";
        public int StartInterval { get; set; } = 120; // minutes
        public string MemberSortOrder { get; set; } = "FirstName";
        public string GeneratedBy { get; set; } = "";
        public string Notes { get; set; } = "";
    }

    public class StartListGenerationResponse
    {
        public bool Success { get; set; }
        public string Message { get; set; } = "";
        public int? StartListId { get; set; }
        public string? StartListUrl { get; set; }
        public StartListSummary? Summary { get; set; }
    }

    public class StartListSummary
    {
        public int TeamCount { get; set; }
        public int TotalShooters { get; set; }
        public string TeamFormat { get; set; } = "";
        public string FirstStartTime { get; set; } = "";
        public string LastEndTime { get; set; } = "";
        public List<StartListTeamSummary> Teams { get; set; } = new List<StartListTeamSummary>();
    }

    public class StartListTeamSummary
    {
        public int TeamNumber { get; set; }
        public string StartTime { get; set; } = "";
        public string EndTime { get; set; } = "";
        public int ShooterCount { get; set; }
        public List<string> WeaponClasses { get; set; } = new List<string>();
    }
}