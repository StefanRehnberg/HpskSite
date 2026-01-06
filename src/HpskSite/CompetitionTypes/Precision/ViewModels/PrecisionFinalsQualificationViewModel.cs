namespace HpskSite.CompetitionTypes.Precision.ViewModels
{
    /// <summary>
    /// View model for displaying finals qualification analysis
    /// </summary>
    public class PrecisionFinalsQualificationViewModel
    {
        public int CompetitionId { get; set; }
        public string CompetitionName { get; set; } = "";
        public int TotalShooters { get; set; }
        public int TotalQualifiers { get; set; }
        public List<ChampionshipClassQualification> ClassQualifications { get; set; } = new();
        public List<FinalsTeamPreview> ProposedTeams { get; set; } = new();
        public int MaxShootersPerTeam { get; set; } = 20;
    }

    public class ChampionshipClassQualification
    {
        public string ChampionshipClass { get; set; } = ""; // "A", "B", "C", "C Dam", etc.
        public List<string> SubClasses { get; set; } = new(); // ["A1", "A2", "A3"]
        public int TotalShooters { get; set; }
        public int Qualifiers { get; set; }
        public string QualificationRule { get; set; } = ""; // "Top 1/6", "Minimum 10", "All advance"
        public List<QualifiedShooter> QualifiedShooters { get; set; } = new();
    }

    public class QualifiedShooter
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = ""; // Original class (A1, A2, etc.)
        public string ChampionshipClass { get; set; } = ""; // Championship class (A, B, C, etc.)
        public int QualificationScore { get; set; }
        public int QualificationRank { get; set; } // Rank within championship class
        public int XCount { get; set; }
    }

    public class FinalsTeamPreview
    {
        public string TeamName { get; set; } = ""; // "Team F1", "Team F2", etc.
        public string ChampionshipClasses { get; set; } = ""; // "A", "B", "C + C Dam + C Jun", etc.
        public int ShooterCount { get; set; }
        public List<FinalsPosition> Positions { get; set; } = new();
    }

    public class FinalsPosition
    {
        public int Position { get; set; }
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public string ShootingClass { get; set; } = "";
        public string ChampionshipClass { get; set; } = "";
        public int QualificationScore { get; set; }
        public int Rank { get; set; }
    }
}
