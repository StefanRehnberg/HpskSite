using System.Globalization;
using System.Text;
using HpskSite.Models.Schedule;
using HpskSite.Models.Staffing;
using HpskSite.Services.Planning;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Steg 4 (B9): <b>ett påminnelsemejl per person och dag</b> över en planeringsfamiljs vecka. Den här klassen är den rena delen —
    /// när, vem som ska ha ett mejl och hur det ser ut — så att den kan testas utan databas och utan SMTP.
    /// Förut fanns bara en push 30 minuter före varje enskilt pass i en tävling; en sekretariatsfunktionär i det gemensamma lagret
    /// (utan tävling) fick ingenting, och den som har fyra pass fyra olika dagar fick fyra pushar utan sammanhang.
    /// </summary>
    public static class StaffWeekDigest
    {
        /// <summary>Mejlet går kvällen före, mellan dessa klockslag (hela timmar) — då hinner man planera morgonen.</summary>
        public const int FirstHour = 17;
        public const int LastHour = 21;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        public static bool InSendWindow(DateTime now) => now.Hour >= FirstHour && now.Hour <= LastHour;

        /// <summary>Dagen mejlet gäller: i morgon.</summary>
        public static DateTime DayFor(DateTime now) => now.Date.AddDays(1);

        /// <summary>Statusar som ska ha en påminnelse. Ett obesvarat uppdrag (Invited) och ett avböjt (Declined) ska inte.</summary>
        public static bool Remindable(string? status) =>
            string.IsNullOrEmpty(status)
            || string.Equals(status, StaffAssignmentStatus.Planned, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, StaffAssignmentStatus.Accepted, StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, StaffAssignmentStatus.Confirmed, StringComparison.OrdinalIgnoreCase);

        public static string Key(int rootId, DateTime day) => $"week-{rootId}-{day:yyyyMMdd}";

        public static string Subject(string planningName, DateTime day)
        {
            var d = day.ToString("dddd d MMMM", Sv);
            return $"I morgon ({d}): din dag i {planningName}";
        }

        /// <summary>Mejlets innehåll för en dag: raderna i tidsordning, krockar och varningar utskrivna, och en knapp till hela veckan.</summary>
        public static string Inner(MySchedule week, DateTime day, string weekUrl)
        {
            var sb = new StringBuilder();
            var items = week.Days.Where(d => d.Date?.Date == day.Date).SelectMany(d => d.Items).ToList();
            sb.Append($"<p>Det här har du <b>{PlanningNotifier.H(day.ToString("dddd d MMMM", Sv))}</b>:</p>");
            sb.Append("<table style=\"border-collapse:collapse;width:100%;max-width:560px\">");
            foreach (var it in items)
            {
                sb.Append("<tr style=\"border-top:1px solid #ddd\">");
                sb.Append($"<td style=\"padding:8px 12px 8px 0;white-space:nowrap;vertical-align:top\"><b>{PlanningNotifier.H(it.TimeLabel)}</b></td>");
                sb.Append("<td style=\"padding:8px 0\">");
                sb.Append($"<b>{PlanningNotifier.H(it.Title)}</b>");
                if (!string.IsNullOrWhiteSpace(it.Where)) sb.Append($"<br><span style=\"color:#666\">{PlanningNotifier.H(it.Where)}</span>");
                if (it.HasConflict)
                    sb.Append($"<br><span style=\"color:#b45309\"><b>Krockar med:</b> {PlanningNotifier.H(it.ConflictText)}</span>");
                if (!string.IsNullOrWhiteSpace(it.WarningText))
                    sb.Append($"<br><span style=\"color:#b45309\">Obs: {PlanningNotifier.H(it.WarningText)}</span>");
                sb.Append("</td></tr>");
            }
            sb.Append("</table>");
            sb.Append($"<p>{PlanningNotifier.Button("Se hela veckan", weekUrl)}</p>");
            return sb.ToString();
        }

        public static string Footer() =>
            "Du får det här mejlet kvällen före en dag där du har ett uppdrag, ett mejl per dag. "
            + "Svara inte på det — frågor om uppdraget går till den som bemannar.";
    }
}
