using System.Globalization;
using System.Text;
using HpskSite.Models.Schedule;

namespace HpskSite.Services.Schedule
{
    /// <summary>
    /// Renders a member's itinerary as a one-shot iCalendar file they can open into their phone's
    /// calendar, with an alarm before each item.
    ///
    /// One-shot by design (not a subscribable feed): a downloaded file cannot go stale silently,
    /// because a moved start time is announced separately by the start-list republish notification.
    /// The consequence is that re-importing after a change is the member's job, so the DESCRIPTION of
    /// every event says when the file was generated — otherwise there'd be no way to tell an old import
    /// from a current one.
    ///
    /// Only items with an absolute <see cref="ScheduleItem.StartsAt"/> can be exported; a skjutlag on an
    /// undated multi-day list genuinely has no moment to write. Those are counted and reported rather
    /// than guessed at — see <see cref="Build"/>'s skipped count.
    /// </summary>
    public class ScheduleIcsBuilder
    {
        private const string ProdId = "-//pistol.nu//Mitt schema//SV";

        /// <summary>Minutes before an item that the calendar alarm fires.</summary>
        private const int AlarmMinutes = 30;

        /// <summary>Assumed length of an item with no end time, so the calendar shows a block not a dot.</summary>
        private static readonly Dictionary<string, int> DefaultDurationMinutes = new()
        {
            [ScheduleItemKind.Skytte] = 60,
            [ScheduleItemKind.Funktionar] = 120,
            [ScheduleItemKind.Praktiskt] = 30,
        };

        public (string ics, int exported, int skipped) Build(MySchedule schedule, string siteBaseUrl)
        {
            var sb = new StringBuilder();
            var stamp = DateTime.UtcNow;
            var exported = 0;
            var skipped = 0;

            sb.Append("BEGIN:VCALENDAR\r\n");
            sb.Append("VERSION:2.0\r\n");
            sb.Append($"PRODID:{ProdId}\r\n");
            sb.Append("CALSCALE:GREGORIAN\r\n");
            sb.Append("METHOD:PUBLISH\r\n");
            AppendLine(sb, "X-WR-CALNAME", $"{schedule.CompName} — mitt schema");

            foreach (var item in schedule.Days.SelectMany(d => d.Items))
            {
                if (item.StartsAt == null) { skipped++; continue; }

                var start = item.StartsAt.Value;
                var end = item.EndsAt ?? start.AddMinutes(
                    DefaultDurationMinutes.TryGetValue(item.Kind, out var m) ? m : 60);
                if (end <= start) end = start.AddMinutes(30);

                sb.Append("BEGIN:VEVENT\r\n");
                AppendLine(sb, "UID", $"{item.SourceKey}-{schedule.CompetitionId}@pistol.nu");
                AppendLine(sb, "DTSTAMP", Utc(stamp));
                // Local wall-clock without a TZID: the times we hold ARE wall-clock at the range, and
                // floating times are interpreted in the viewer's own zone — which is the right answer
                // for a Swedish competition read on a Swedish phone, and avoids shipping a VTIMEZONE.
                AppendLine(sb, "DTSTART", Local(start));
                AppendLine(sb, "DTEND", Local(end));
                AppendLine(sb, "SUMMARY", BuildSummary(item, schedule));
                AppendLine(sb, "LOCATION", item.Where);
                AppendLine(sb, "DESCRIPTION", BuildDescription(item, schedule, stamp));
                AppendLine(sb, "URL", $"{siteBaseUrl.TrimEnd('/')}/mitt-schema?c={schedule.CompetitionId}");
                AppendLine(sb, "CATEGORIES", item.Kind);

                sb.Append("BEGIN:VALARM\r\n");
                sb.Append("ACTION:DISPLAY\r\n");
                AppendLine(sb, "DESCRIPTION", item.Title);
                sb.Append($"TRIGGER:-PT{AlarmMinutes}M\r\n");
                sb.Append("END:VALARM\r\n");

                sb.Append("END:VEVENT\r\n");
                exported++;
            }

            sb.Append("END:VCALENDAR\r\n");
            return (sb.ToString(), exported, skipped);
        }

        private static string BuildSummary(ScheduleItem item, MySchedule schedule)
        {
            var prefix = item.Kind switch
            {
                ScheduleItemKind.Funktionar => "Funktionär: ",
                ScheduleItemKind.Praktiskt => "",
                _ => "",
            };
            return $"{prefix}{item.Title} — {schedule.CompName}";
        }

        private static string BuildDescription(ScheduleItem item, MySchedule schedule, DateTime stamp)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.Where)) parts.Add(item.Where!);
            if (!string.IsNullOrWhiteSpace(item.Detail)) parts.Add(item.Detail!);
            if (item.HasConflict) parts.Add("OBS krockar med: " + string.Join("; ", item.ConflictsWith));
            parts.Add($"Hämtat från pistol.nu {stamp.ToLocalTime():yyyy-MM-dd HH:mm}. Ändrar arrangören tiden behöver du hämta schemat igen.");
            return string.Join("\\n", parts);
        }

        private static string Utc(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        private static string Local(DateTime d) => d.ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);

        /// <summary>
        /// Writes one property, escaped per RFC 5545 and folded at 73 octets. Skips empty values so we
        /// never emit a bare "LOCATION:" that some clients render as a blank line.
        /// </summary>
        private static void AppendLine(StringBuilder sb, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var escaped = value
                .Replace("\\", "\\\\")
                .Replace(";", "\\;")
                .Replace(",", "\\,")
                .Replace("\r\n", "\\n")
                .Replace("\n", "\\n");

            var line = $"{name}:{escaped}";
            const int max = 73;
            if (line.Length <= max) { sb.Append(line).Append("\r\n"); return; }

            sb.Append(line.Substring(0, max)).Append("\r\n");
            var rest = line.Substring(max);
            while (rest.Length > 0)
            {
                var take = Math.Min(max - 1, rest.Length);
                sb.Append(' ').Append(rest.Substring(0, take)).Append("\r\n");
                rest = rest.Substring(take);
            }
        }
    }
}
