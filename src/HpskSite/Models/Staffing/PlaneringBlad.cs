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

        /// <summary>
        /// Which sheet to print. Every print button used to hit the same URL and get the same thing —
        /// a role-grouped list of names with one person repeated on row after row and no day axis at all.
        /// Bemanning / Förberedelser / Dagsprogram are different documents and print as such.
        /// </summary>
        public string Vy { get; set; } = PlaneringBladVy.Allt;

        /// <summary>The Bemanning sheet IS the grid — same builder as the screen, so paper and screen
        /// cannot drift apart.</summary>
        public GridResponse? Grid { get; set; }

        public List<HpskSite.Models.Schedule.CompetitionAgendaItem> Agenda { get; set; } = new();

        public bool ShowBemanning => Vy is PlaneringBladVy.Allt or PlaneringBladVy.Bemanning;
        public bool ShowForberedelser => Vy is PlaneringBladVy.Allt or PlaneringBladVy.Forberedelser;
        public bool ShowDagsprogram => Vy is PlaneringBladVy.Allt or PlaneringBladVy.Dagsprogram;

        public string Heading => Vy switch
        {
            PlaneringBladVy.Bemanning => "Bemanning",
            PlaneringBladVy.Forberedelser => "Förberedelser",
            PlaneringBladVy.Dagsprogram => "Dagsprogram",
            _ => "Planering",
        };
    }

    public static class PlaneringBladVy
    {
        public const string Allt = "allt";
        public const string Bemanning = "bemanning";
        public const string Forberedelser = "forberedelser";
        public const string Dagsprogram = "dagsprogram";

        public static string Normalise(string? v) => v?.Trim().ToLowerInvariant() switch
        {
            Bemanning => Bemanning,
            Forberedelser => Forberedelser,
            Dagsprogram => Dagsprogram,
            _ => Allt,
        };
    }
}
