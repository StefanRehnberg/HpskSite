using System;
using HpskSite.Models.Planning;

namespace HpskSite.Services.Planning
{
    /// <summary>
    /// Vem som får vilken notis, som rena regler (prövas i <c>PlanningNotifyRulesTests</c>).
    ///
    /// <code>
    /// Läge                     | Nämnd                | Nya meddelanden            | Möten
    /// Ett mejl om dagen (förval)| i kvällens mejl      | i kvällens mejl            | mejl direkt + påminnelse
    /// Allt direkt              | push + mejl direkt   | push högst 1/h + kvällsmejl | mejl direkt + push
    /// Bara när någon nämner mig| push + mejl direkt   | –                          | mejl direkt
    /// Inga notiser             | –                    | –                          | –
    /// </code>
    ///
    /// <para><b>⚠️ Notisens text bär aldrig meddelandets innehåll</b> — den syns på en låst telefon.</para>
    /// </summary>
    public static class PlanningNotifyRules
    {
        /// <summary>Kvällsmejlet går tidigast den här timmen (svensk tid).</summary>
        public const int DigestHour = 18;

        public static bool MailOnMention(string? mode) =>
            PlanningNotifyMode.Normalize(mode) is PlanningNotifyMode.All or PlanningNotifyMode.Mentions;

        public static bool PushOnMention(string? mode) => MailOnMention(mode);

        public static bool GetsDigest(string? mode) =>
            PlanningNotifyMode.Normalize(mode) is PlanningNotifyMode.Daily or PlanningNotifyMode.All;

        public static bool GetsHourlyPush(string? mode) => PlanningNotifyMode.Normalize(mode) == PlanningNotifyMode.All;

        public static bool GetsMeetingMail(string? mode) => PlanningNotifyMode.Normalize(mode) != PlanningNotifyMode.Off;

        public static bool GetsMeetingPush(string? mode) => PlanningNotifyMode.Normalize(mode) != PlanningNotifyMode.Off;

        /// <summary>Är det dags för kvällsmejlet? Från 18 och resten av dygnet.</summary>
        public static bool DigestDue(DateTime now) => now.Hour >= DigestHour;

        /// <summary>Nyckeln för kvällsmejlet: ett per deltagare och dag.</summary>
        public static int DayKey(DateTime now) => now.Year * 10000 + now.Month * 100 + now.Day;

        public static bool HourlyPushDue(DateTime? lastPush, DateTime now) => lastPush == null || now - lastPush.Value >= TimeSpan.FromHours(1);

        /// <summary>
        /// Vilken mötespåminnelse som är aktuell nu: "d1" (dagen innan, från 24 timmar före) eller
        /// "h1" (en timme före). Null utanför fönstren och efter mötets start. Nyckeln bär mötets
        /// starttid, så ett flyttat möte påminns igen och "d1" stoppar aldrig "h1".
        /// </summary>
        public static string? MeetingReminderStep(DateTime startsAt, DateTime now)
        {
            if (now >= startsAt) return null;
            var left = startsAt - now;
            if (left <= TimeSpan.FromHours(1)) return "h1";
            if (left <= TimeSpan.FromHours(24)) return "d1";
            return null;
        }

        /// <summary>Påminnelse om att svara på tiderna: dagen före svarsfristen.</summary>
        public static bool VoteReminderDue(DateTime? deadline, DateTime now) =>
            deadline.HasValue && now < deadline.Value && deadline.Value - now <= TimeSpan.FromHours(24);
    }
}
