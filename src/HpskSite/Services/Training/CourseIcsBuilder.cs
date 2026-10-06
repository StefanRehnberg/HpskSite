using System.Text;
using HpskSite.Services.Schedule;

namespace HpskSite.Services.Training
{
    /// <summary>
    /// Kursens tillfällen som en kalenderfil att öppna i telefonen (UX-omgången 2026-10-06). Samma
    /// form och escaping som <see cref="ScheduleIcsBuilder"/>: en engångsfil, flytande lokal tid,
    /// och en rad i beskrivningen om när den hämtades — ändras schemat får medlemmen hämta igen.
    ///
    /// <para>Påminnelsen i filen ligger DAGEN INNAN (för obligatoriska tillfällen också två timmar
    /// före), eftersom den som går en kurs behöver planera kvällen, inte bara hinna fram.</para>
    ///
    /// <para>Inställda tillfällen och tillfällen utan starttid skrivs inte — en träning utan klockslag
    /// har inget ögonblick att lägga i kalendern, och en gissad tid är värre än ingen.</para>
    /// </summary>
    public static class CourseIcsBuilder
    {
        private const string ProdId = "-//pistol.nu//Min kurs//SV";

        public static (string Ics, int Exported) Build(string courseName, int groupId,
            IEnumerable<TrainingCourseService.Occasion> occasions, string siteBaseUrl)
        {
            var sb = new StringBuilder();
            var stamp = DateTime.UtcNow;
            var exported = 0;
            sb.Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n");
            sb.Append($"PRODID:{ProdId}\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n");
            ScheduleIcsBuilder.AppendLine(sb, "X-WR-CALNAME", courseName);

            foreach (var o in occasions)
            {
                if (o.IsCancelled || o.IsPast) continue;
                if (!DateTime.TryParse(o.Date, out var day)) continue;
                if (!TimeSpan.TryParseExact(o.StartTime ?? "", @"hh\:mm", null, out var st)) continue;
                var start = day.Date.Add(st);
                var end = TimeSpan.TryParseExact(o.EndTime ?? "", @"hh\:mm", null, out var et) && et > st
                    ? day.Date.Add(et) : start.AddHours(2);
                var title = string.IsNullOrWhiteSpace(o.Note) ? o.Name : o.Note!;

                sb.Append("BEGIN:VEVENT\r\n");
                ScheduleIcsBuilder.AppendLine(sb, "UID", $"course-{groupId}-training-{o.TrainingId}@pistol.nu");
                ScheduleIcsBuilder.AppendLine(sb, "DTSTAMP", ScheduleIcsBuilder.Utc(stamp));
                ScheduleIcsBuilder.AppendLine(sb, "DTSTART", ScheduleIcsBuilder.Local(start));
                ScheduleIcsBuilder.AppendLine(sb, "DTEND", ScheduleIcsBuilder.Local(end));
                ScheduleIcsBuilder.AppendLine(sb, "SUMMARY", $"{title} — {courseName}");
                ScheduleIcsBuilder.AppendLine(sb, "LOCATION", o.Venue);
                var desc = new List<string>();
                if (o.MandatoryForCourse) desc.Add("Obligatoriskt för kursen.");
                if (o.RegistrationRequired) desc.Add("Anmälan krävs — anmäl dig på pistol.nu.");
                desc.Add($"Hämtat från pistol.nu {stamp.ToLocalTime():yyyy-MM-dd HH:mm}. Ändras schemat behöver du hämta filen igen.");
                ScheduleIcsBuilder.AppendLine(sb, "DESCRIPTION", string.Join("\n", desc));
                ScheduleIcsBuilder.AppendLine(sb, "URL", $"{siteBaseUrl.TrimEnd('/')}/min-kurs?g={groupId}");

                sb.Append("BEGIN:VALARM\r\nACTION:DISPLAY\r\n");
                ScheduleIcsBuilder.AppendLine(sb, "DESCRIPTION", title);
                sb.Append("TRIGGER:-P1D\r\nEND:VALARM\r\n");
                if (o.MandatoryForCourse)
                {
                    sb.Append("BEGIN:VALARM\r\nACTION:DISPLAY\r\n");
                    ScheduleIcsBuilder.AppendLine(sb, "DESCRIPTION", title);
                    sb.Append("TRIGGER:-PT2H\r\nEND:VALARM\r\n");
                }
                sb.Append("END:VEVENT\r\n");
                exported++;
            }
            sb.Append("END:VCALENDAR\r\n");
            return (sb.ToString(), exported);
        }
    }
}
