namespace HpskSite.Models.Staffing
{
    /// <summary>En start på bladet: vem, dag, klockslag och vad ("klass C2, patrull 18"). Planera dagen: pappret ska visa när de som jobbar själva skjuter.</summary>
    public sealed record PlaneringBladStart(int MemberId, string Date, string Time, string Text);

    /// <summary>Det partialen <c>_BladGridTable</c> ritar: några rader i rutnätet under en rubrik.</summary>
    public sealed record PlaneringBladGridVm(PlaneringBladModel Blad, IReadOnlyList<GridColumn> Cols, IReadOnlyList<GridRow> Rows, string FirstColumn);

    /// <summary>Model for the printable "vem gör vad"-blad (/tavlingsplanering/blad?c=): the day-of roster
    /// (Bemanning) + the preparation work-breakdown (Förberedelser) on one printable sheet.</summary>
    public class PlaneringBladModel
    {
        public int CompetitionId { get; set; }
        /// <summary>Planeringen bladet skrivs ut ur (0 = tävlingens gamla väg). Styr "Tillbaka".</summary>
        public int PlanningId { get; set; }
        /// <summary>Dagsprogrammet hör till tävlingen — en planering utan tävling har inget.</summary>
        public bool AgendaAvailable { get; set; } = true;
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

        /// <summary>
        /// Planera dagen: skyttarnas egna starter i tävlingen (bara planerarens blad — det avslöjar när en funktionär skjuter) och krockarna
        /// mellan ett uppdrag och en egen start. Tomma när planeringen inte har en tävling med startlista.
        /// </summary>
        public List<PlaneringBladStart> Starts { get; set; } = new();
        public List<GridClash> StartClashes { get; set; } = new();

        public IEnumerable<PlaneringBladStart> StartsFor(int? memberId, string dateKey) =>
            memberId is > 0 ? Starts.Where(s => s.MemberId == memberId && s.Date == dateKey) : Enumerable.Empty<PlaneringBladStart>();

        /// <summary>Krockar uppdraget i <paramref name="row"/> och <paramref name="e"/> den dagen med personens egen start? Samma matchning som rutnätet på skärmen.</summary>
        public bool HasStartClash(GridEntry e, GridRow row, string dateKey)
        {
            var key = e.MemberId is > 0 ? "m:" + e.MemberId : null;
            if (key == null) return false;
            foreach (var c in StartClashes.Where(c => c.Kind == "start" && c.PersonKey == key && c.DateKey == dateKey))
            {
                var mine = (c.A ?? "").StartsWith("egen start", StringComparison.Ordinal) ? c.B : c.A;
                if (string.IsNullOrEmpty(mine) || !mine.StartsWith(row.RoleName, StringComparison.Ordinal)) continue;
                if (string.IsNullOrEmpty(e.TimeLabel) ? !mine.Contains("hela dagen", StringComparison.Ordinal) : !mine.Contains(e.TimeLabel, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        public List<HpskSite.Models.Schedule.CompetitionAgendaItem> Agenda { get; set; } = new();

        public bool ShowBemanning => Vy is PlaneringBladVy.Allt or PlaneringBladVy.Bemanning;
        public bool ShowForberedelser => Vy is PlaneringBladVy.Allt or PlaneringBladVy.Forberedelser;
        public bool ShowDagsprogram => AgendaAvailable && Vy is PlaneringBladVy.Allt or PlaneringBladVy.Dagsprogram;

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
