using System;
using System.Collections.Generic;
using System.Text;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Kalenderfilen för en planerings möten (RFC 5545), som en prenumeration: ett flyttat möte
    /// flyttas i kalendern och ett inställt stryks.
    ///
    /// <list type="bullet">
    /// <item><b>UID bär sorten</b> (<c>planning-meeting-{id}@pistol.nu</c>, id-lagen regel 1) och är
    /// stabil, så kalendern känner igen mötet vid nästa hämtning.</item>
    /// <item><b>SEQUENCE</b> höjs vid ombokning och inställt.</item>
    /// <item><b>Tiden är svensk väggtid med TZID=Europe/Stockholm</b> — ett onlinemöte hålls i en
    /// bestämd tid, och en mottagare i en annan tidszon ska se rätt klockslag.</item>
    /// <item><b>METHOD:PUBLISH</b>, aldrig REQUEST — annars går kalenderns svar till en obevakad adress.</item>
    /// </list>
    /// </summary>
    public static class PlanningIcsBuilder
    {
        private const string ProdId = "-//pistol.nu//Tävlingsplanering//SV";
        private const string Tz = "Europe/Stockholm";

        public static string Build(string calendarName, IEnumerable<PlanningMeetingRow> meetings, string planningUrl, DateTime stamp)
        {
            var sb = new StringBuilder();
            Line(sb, "BEGIN:VCALENDAR");
            Line(sb, "VERSION:2.0");
            Line(sb, "PRODID:" + ProdId);
            Line(sb, "CALSCALE:GREGORIAN");
            Line(sb, "METHOD:PUBLISH");
            Line(sb, "X-WR-CALNAME:" + Escape(calendarName));
            Line(sb, "X-WR-TIMEZONE:" + Tz);
            // Kort VTIMEZONE för Sverige (CET/CEST), så att även kalendrar utan inbyggd tidszonsdatabas räknar rätt.
            Line(sb, "BEGIN:VTIMEZONE");
            Line(sb, "TZID:" + Tz);
            Line(sb, "BEGIN:DAYLIGHT");
            Line(sb, "TZOFFSETFROM:+0100");
            Line(sb, "TZOFFSETTO:+0200");
            Line(sb, "TZNAME:CEST");
            Line(sb, "DTSTART:19700329T020000");
            Line(sb, "RRULE:FREQ=YEARLY;BYMONTH=3;BYDAY=-1SU");
            Line(sb, "END:DAYLIGHT");
            Line(sb, "BEGIN:STANDARD");
            Line(sb, "TZOFFSETFROM:+0200");
            Line(sb, "TZOFFSETTO:+0100");
            Line(sb, "TZNAME:CET");
            Line(sb, "DTSTART:19701025T030000");
            Line(sb, "RRULE:FREQ=YEARLY;BYMONTH=10;BYDAY=-1SU");
            Line(sb, "END:STANDARD");
            Line(sb, "END:VTIMEZONE");

            foreach (var m in meetings)
            {
                if (m.StartsAt == null || m.DeletedAt != null) continue;
                var end = m.EndsAt ?? m.StartsAt.Value.AddHours(1);
                var cancelled = m.Status == PlanningMeetingStatus.Cancelled;
                Line(sb, "BEGIN:VEVENT");
                Line(sb, $"UID:planning-meeting-{m.Id}@pistol.nu");
                Line(sb, "SEQUENCE:" + m.Sequence);
                Line(sb, "DTSTAMP:" + stamp.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"));
                Line(sb, $"DTSTART;TZID={Tz}:" + m.StartsAt.Value.ToString("yyyyMMdd'T'HHmmss"));
                Line(sb, $"DTEND;TZID={Tz}:" + end.ToString("yyyyMMdd'T'HHmmss"));
                Line(sb, "SUMMARY:" + Escape((cancelled ? "INSTÄLLT: " : "") + m.Title));
                var desc = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(m.OnlineUrl)) desc.Append("Länk till mötet: ").Append(m.OnlineUrl).Append('\n');
                desc.Append("Planeringen: ").Append(planningUrl);
                Line(sb, "DESCRIPTION:" + Escape(desc.ToString()));
                if (!string.IsNullOrWhiteSpace(m.Place)) Line(sb, "LOCATION:" + Escape(m.Place!));
                else if (!string.IsNullOrWhiteSpace(m.OnlineUrl)) Line(sb, "LOCATION:" + Escape("Onlinemöte"));
                // URL-egenskapen escapas INTE (en Teams-länk bär ; och ,) — och bara https når hit (IsSafeUrl).
                if (!string.IsNullOrWhiteSpace(m.OnlineUrl) && PlanningService.IsSafeUrl(m.OnlineUrl!)) Line(sb, "URL:" + m.OnlineUrl);
                Line(sb, "STATUS:" + (cancelled ? "CANCELLED" : "CONFIRMED"));
                if (!cancelled)
                {
                    Line(sb, "BEGIN:VALARM");
                    Line(sb, "ACTION:DISPLAY");
                    Line(sb, "DESCRIPTION:" + Escape(m.Title));
                    Line(sb, "TRIGGER:-PT1H");
                    Line(sb, "END:VALARM");
                }
                Line(sb, "END:VEVENT");
            }
            Line(sb, "END:VCALENDAR");
            return sb.ToString();
        }

        public static string Escape(string s) =>
            (s ?? "").Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "");

        /// <summary>En rad, vikt vid 75 oktetter (RFC 5545 3.1) utan att dela ett UTF-8-tecken.</summary>
        private static void Line(StringBuilder sb, string line)
        {
            var bytes = 0;
            var cur = new StringBuilder();
            foreach (var ch in line)
            {
                var n = Encoding.UTF8.GetByteCount(ch.ToString());
                if (bytes + n > 74)
                {
                    sb.Append(cur).Append("\r\n ");
                    cur.Clear();
                    bytes = 1;
                }
                cur.Append(ch);
                bytes += n;
            }
            sb.Append(cur).Append("\r\n");
        }
    }
}
