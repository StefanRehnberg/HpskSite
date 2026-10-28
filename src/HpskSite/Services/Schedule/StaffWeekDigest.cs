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

        /// <summary>
        /// Så många gånger ett mejl försöks per person och dag innan vi ger upp (steg 4–6 K3). Förut släpptes spärren vid varje misslyckande
        /// och svepet (var 5:e minut) försökte om i upp till fem timmar — en adress som studsar hårt fick mejl varje kväll via den delade
        /// avsändaren. Efter taket får personen inget mejl den dagen; Mitt schema gäller alltid.
        /// </summary>
        public const int MaxAttempts = 3;

        /// <summary>Så många misslyckade sändningar i rad avbryter ett svep: SMTP är då nere och det är meningslöst att pröva alla andra.</summary>
        public const int MaxConsecutiveFailures = 3;

        private static readonly CultureInfo Sv = CultureInfo.GetCultureInfo("sv-SE");

        public static bool InSendWindow(DateTime now) => now.Hour >= FirstHour && now.Hour <= LastHour;

        /// <summary>Dagen mejlet gäller: i morgon.</summary>
        public static DateTime DayFor(DateTime now) => now.Date.AddDays(1);

        /// <summary>
        /// Den som ska ha en påminnelse: den som <b>fått besked</b> om sin placering (<c>NotifiedAt</c>), har anmält sig själv (Accepted) eller är
        /// bekräftad. En placering som arrangören bara lagt in (ingen har meddelat personen) är ett utkast och mejlas inte — då skulle personen
        /// få sitt första besked som en påminnelse kvällen före.
        /// </summary>
        public static bool Remindable(string? status, DateTime? notifiedAt) => StaffAssignmentStatus.IsTold(status, notifiedAt);

        /// <summary>Spärren: <b>en per person och dag</b> (B9) — oavsett hur många familjer personen har uppdrag i.</summary>
        public static string Key(DateTime day) => $"week-{day:yyyyMMdd}";

        /// <summary>Raden som räknar misslyckade försök (<see cref="MaxAttempts"/>). Prefixet matchas med LIKE.</summary>
        public static string FailPrefix(DateTime day) => $"weekfail-{day:yyyyMMdd}-";

        public static string Subject(IReadOnlyList<string> planningNames, DateTime day)
        {
            var d = day.ToString("dddd d MMMM", Sv);
            return planningNames.Count == 1 ? $"I morgon ({d}): din dag i {planningNames[0]}" : $"I morgon ({d}): din dag";
        }

        /// <summary>
        /// Mejlets innehåll för en familj och en dag: raderna i tidsordning, krockar och varningar utskrivna, och en knapp till hela veckan.
        /// <paramref name="siteUrl"/> gör relativa länkar absoluta.
        /// </summary>
        public static string Inner(MySchedule week, DateTime day, string weekUrl, string siteUrl = "", bool heading = false)
        {
            var sb = new StringBuilder();
            var items = week.Days.Where(d => d.Date?.Date == day.Date).SelectMany(d => d.Items).ToList();
            if (heading) sb.Append($"<h3 style=\"margin:18px 0 4px\">{PlanningNotifier.H(week.CompName)}</h3>");
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

        /// <summary>
        /// Fotnoten: vem man vänder sig till (den som bemannar, med telefon när den finns) och att mejlet kan vara gammalt om något ändrats
        /// efter kl 17. Mejlet går att svara på — Reply-To är samma person.
        /// </summary>
        public static string Footer(string? contactName = null, string? contactPhone = null)
        {
            var contact = string.IsNullOrWhiteSpace(contactName) ? ""
                : $"Frågor om placeringen: <b>{PlanningNotifier.H(contactName)}</b>"
                  + (string.IsNullOrWhiteSpace(contactPhone) ? "" : $", <a href=\"tel:{PlanningNotifier.H(contactPhone)}\">{PlanningNotifier.H(contactPhone)}</a>")
                  + ". Du kan svara på det här mejlet. ";
            return contact
                + "Du får ett mejl per dag, kvällen före en dag där du har ett uppdrag. "
                + "Ändras något efter kl 17 kan mejlet vara inaktuellt — Mitt schema på pistol.nu gäller alltid.";
        }
    }
}
