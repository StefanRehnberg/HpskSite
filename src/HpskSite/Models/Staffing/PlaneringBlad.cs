namespace HpskSite.Models.Staffing
{
    /// <summary>Model for the printable "vem gör vad"-blad (/tavlingsplanering/blad?c=): the day-of roster
    /// (Bemanning) + the preparation work-breakdown (Förberedelser) on one printable sheet.</summary>
    public class PlaneringBladModel
    {
        public int CompetitionId { get; set; }
        public string CompName { get; set; } = "";
        public string Discipline { get; set; } = "";
        public string? CompDate { get; set; }
        public bool Exists { get; set; }
        public bool CanAccess { get; set; }
        public StaffRosterResponse Roster { get; set; } = new();
        public WorkBreakdownResponse Work { get; set; } = new();
    }
}
