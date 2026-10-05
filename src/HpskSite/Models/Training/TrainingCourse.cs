using NPoco;
using Umbraco.Cms.Infrastructure.Persistence.DatabaseAnnotations;

namespace HpskSite.Models.Training
{
    /// <summary>
    /// Fas D: en träning kopplad till en träningsgrupp (kursen). Många-till-många — en träning kan
    /// höra till flera kurser, och är fortfarande en vanlig klubbträning för alla andra.
    /// </summary>
    [TableName("TrainingGroupTraining")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingGroupTraining
    {
        public int Id { get; set; }
        public int TrainingGroupId { get; set; }
        public int TrainingId { get; set; }
        /// <summary>null = som gruppen; annars <see cref="TrainingCourseRules.Optional"/>/<see cref="TrainingCourseRules.Mandatory"/>.</summary>
        public string? Attendance { get; set; }
        /// <summary>null = som gruppen; annars <see cref="TrainingCourseRules.NotRequired"/>/<see cref="TrainingCourseRules.Required"/>.</summary>
        public string? Registration { get; set; }
        public string? Note { get; set; }
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>En serie som en funktionär registrerat på ett kurstillfälle (fas D).</summary>
    [TableName("TrainingCourseSeries")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingCourseSeries
    {
        public int Id { get; set; }
        public int TrainingGroupId { get; set; }
        public int TrainingId { get; set; }
        public int MemberId { get; set; }
        public int TrainingScoreId { get; set; }
        public int SeriesNumber { get; set; }
        public int Total { get; set; }
        public int XCount { get; set; }
        public bool ShotByShot { get; set; }
        public string? Valor { get; set; }
        public int? MarkenSeriesId { get; set; }
        public int RecordedByMemberId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>Anteckning per deltagare och tillfälle — överlämningen till nästa instruktör.</summary>
    [TableName("TrainingCourseNote")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class TrainingCourseNote
    {
        public int Id { get; set; }
        public int TrainingGroupId { get; set; }
        public int TrainingId { get; set; }
        public int MemberId { get; set; }
        public string Note { get; set; } = "";
        public int AuthorMemberId { get; set; }
        public DateTime CreatedDate { get; set; } = DateTime.Now;
        public DateTime UpdatedDate { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// Fas D: vad gäller för en kursdeltagare på ett kopplat tillfälle? Rena funktioner.
    ///
    /// <para><b>⚠️ Kraven HÄRLEDS, de lagras aldrig, och de skrivs aldrig till träningens egna fält</b>
    /// (Stefans beslut 2026-10-02). Tillfället är fortfarande en vanlig klubbträning: kravet att anmäla
    /// sig eller att närvara gäller bara gruppens deltagare. Ordningen: kopplingens avvikelse → gruppens
    /// standard → valfri / krävs inte.</para>
    ///
    /// <para>En missad obligatorisk dag VISAS, men spärrar ingenting.</para>
    /// </summary>
    public static class TrainingCourseRules
    {
        public const string Optional = "Optional";
        public const string Mandatory = "Mandatory";
        public const string NotRequired = "NotRequired";
        public const string Required = "Required";

        /// <summary>Ett okänt värde läses som null ("som gruppen") — aldrig som ett krav.</summary>
        public static string? NormaliseAttendance(string? v) =>
            string.Equals(v, Mandatory, StringComparison.OrdinalIgnoreCase) ? Mandatory
            : string.Equals(v, Optional, StringComparison.OrdinalIgnoreCase) ? Optional : null;

        public static string? NormaliseRegistration(string? v) =>
            string.Equals(v, Required, StringComparison.OrdinalIgnoreCase) ? Required
            : string.Equals(v, NotRequired, StringComparison.OrdinalIgnoreCase) ? NotRequired : null;

        public static bool IsMandatory(string? groupDefault, string? linkOverride) =>
            (NormaliseAttendance(linkOverride) ?? NormaliseAttendance(groupDefault)) == Mandatory;

        public static bool RegistrationRequired(string? groupDefault, string? linkOverride) =>
            (NormaliseRegistration(linkOverride) ?? NormaliseRegistration(groupDefault)) == Required;

        /// <summary>
        /// Tolkar en serie: antingen fem skott ("X" eller 0–10, X räknas som 10 och som innertia) eller
        /// bara en total (0–50). Returnerar summa, antal X och ett fel i klartext.
        ///
        /// <para>⚠️ En serie med skott måste ha EXAKT fem — en serie med fyra skott är inte en
        /// guldserie, och att räkna den som en hade gett en valör på ett ofullständigt underlag.</para>
        /// </summary>
        public static (int Total, int XCount, List<string>? Shots, string? Error) ParseSeries(IEnumerable<string>? shots, int? total)
        {
            var list = shots?.Select(s => (s ?? "").Trim().ToUpperInvariant()).Where(s => s.Length > 0).ToList();
            if (list is { Count: > 0 })
            {
                if (list.Count != 5) return (0, 0, null, "En serie har fem skott.");
                int sum = 0, x = 0;
                foreach (var s in list)
                {
                    if (s == "X") { sum += 10; x++; continue; }
                    if (!int.TryParse(s, out var v) || v < 0 || v > 10) return (0, 0, null, $"Ogiltigt skott: {s}.");
                    sum += v;
                }
                return (sum, x, list, null);
            }
            if (total is null) return (0, 0, null, "Ange skotten eller seriens total.");
            if (total < 0 || total > 50) return (0, 0, null, "Totalen ska vara mellan 0 och 50.");
            return (total.Value, 0, null, null);
        }

        /// <summary>
        /// Räknas en QR-incheckning på banan som närvaro på tillfället? Inom en timme före start till
        /// en timme efter slut (eller start + 3 h när sluttid saknas). Ett tillfälle utan klockslag
        /// matchar hela dagen. Härlett — skrivs aldrig som en närvarorad.
        /// </summary>
        public static bool CheckInCounts(DateTime checkIn, DateTime date, string? startTime, string? endTime)
        {
            if (checkIn.Date != date.Date) return false;
            if (!TimeSpan.TryParse(startTime ?? "", out var start)) return true;
            var end = TimeSpan.TryParse(endTime ?? "", out var e) && e > start ? e : start.Add(TimeSpan.FromHours(3));
            var t = checkIn.TimeOfDay;
            return t >= start.Subtract(TimeSpan.FromHours(1)) && t <= end.Add(TimeSpan.FromHours(1));
        }
    }
}
