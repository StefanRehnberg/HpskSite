namespace HpskSite.CompetitionTypes.Common.SeriesCalculation.Models
{
    public class SeriesCalculationContext
    {
        public int SeriesId { get; set; }
        public string SeriesName { get; set; } = "";
        public List<SeriesCompetitionInfo> Competitions { get; set; } = new();
        public Dictionary<string, object> Parameters { get; set; } = new();
        public Dictionary<int, List<ShooterCompetitionScore>> CompetitionResults { get; set; } = new();
    }

    public class SeriesCompetitionInfo
    {
        public int CompetitionId { get; set; }
        public string Name { get; set; } = "";
        public DateTime Date { get; set; }
    }

    public class ShooterCompetitionScore
    {
        public int MemberId { get; set; }
        public string Name { get; set; } = "";
        public string Club { get; set; } = "";
        public int ClubId { get; set; }
        public string ShootingClass { get; set; } = "";
        public int TotalScore { get; set; }
        public int XCount { get; set; }
    }
}
