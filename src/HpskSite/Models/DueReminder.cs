using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// Loggen över mejlade påminnelser 90 dagar före (2026-10-21, plan del C). En rad per (källa, nyckel, ägare,
    /// FÖRFALLODAG) — det unika indexet är spärren mot dubbla mejl, inte koden.
    ///
    /// <para><b>⚠️ <see cref="DueDate"/> i nyckeln är bärande.</b> Flyttas datumet ska påminnelsen kunna gå ut
    /// igen — samma lärdom som <c>FirearmReminder.ExpiresOnAtSend</c>.</para>
    ///
    /// <para><b>Claim-then-send:</b> raden skrivs FÖRE utskicket, och <see cref="Succeeded"/> sätts först när
    /// minst ett mejl faktiskt gick iväg. En krasch emellan kostar en missad påminnelse; motsatt ordning kostar
    /// spam.</para>
    /// </summary>
    [TableName("DueReminder")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class DueReminder
    {
        public int Id { get; set; }
        /// <summary>Se <see cref="DueReminderSource"/>.</summary>
        public string Source { get; set; } = "";
        /// <summary>Källans id i källans sort, t.ex. <c>12:2030</c> (regel:år) eller <c>45</c> (besiktning).</summary>
        public string SourceKey { get; set; } = "";
        /// <summary>0 = klubb, 1 = krets, 2 = skjutbana (<see cref="DueReminderSource.RangeOwner"/>).</summary>
        public int OwnerType { get; set; }
        public int OwnerId { get; set; }
        public DateTime DueDate { get; set; }
        public DateTime SentAt { get; set; }
        public bool Succeeded { get; set; }
        public int RecipientCount { get; set; }
    }

    public static class DueReminderSource
    {
        public const string WheelRule = "wheel-rule";
        public const string WheelItem = "wheel-item";
        public const string RangeInspection = "range-inspection";
        public const string RangePermit = "range-permit";
        public const string RangeDocument = "range-document";

        /// <summary>Ägartypen i loggen för en banas påminnelse (klubb 0 och krets 1 är <c>DocumentOwnerType</c>).</summary>
        public const int RangeOwner = 2;

        public static string ForRangeKind(string kind) => kind switch
        {
            RangeRequirement.Inspection => RangeInspection,
            RangeRequirement.Permit => RangePermit,
            _ => RangeDocument
        };
    }

    /// <summary>Påminnelsernas regler som rena funktioner (<c>DueReminderRulesTests</c>).</summary>
    public static class DueReminderRules
    {
        public const int DaysBefore = 90;

        /// <summary>
        /// Passerade datum äldre än så här påminns det aldrig om — annars mejlar första körningen ut all gammal
        /// historik. Ett nyss passerat datum (läggs in för sent) påminns det om en gång.
        /// </summary>
        public const int MaxDaysOverdue = 30;

        public static bool InWindow(DateTime due, DateTime today)
        {
            var days = (due.Date - today.Date).Days;
            return days <= DaysBefore && days >= -MaxDaysOverdue;
        }

        public static string Subject(string title, DateTime due, DateTime today) =>
            (due.Date < today.Date ? "Försenat: " : "Påminnelse: ") + title;

        /// <summary>Mejlets stycken. <paramref name="title"/> och <paramref name="from"/> är redan HTML-kodade av
        /// anroparen; <paramref name="from"/> är t.ex. "årshjulet för Haaplinge GoAss" eller "skjutbanan Änganäs".</summary>
        public static List<string> Paragraphs(string title, DateTime due, DateTime today, string from)
        {
            var days = (due.Date - today.Date).Days;
            var when = days < 0
                ? $"skulle ha varit klart {due:yyyy-MM-dd} — för {-days} {(days == -1 ? "dag" : "dagar")} sedan"
                : days == 0 ? $"ska vara klart i dag, {due:yyyy-MM-dd}"
                : $"ska vara klart senast {due:yyyy-MM-dd} — om {days} {(days == 1 ? "dag" : "dagar")}";
            return new List<string>
            {
                $"<strong>{title}</strong> {when}.",
                $"Påminnelsen kommer från {from} på pistol.nu och skickas en gång, {DaysBefore} dagar före."
            };
        }
    }
}
