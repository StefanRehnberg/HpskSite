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

        /// <summary>
        /// Ämnesraden för ETT mejl per förening (eller bana) och svep (Stefans beslut 2026-10-21 — första svepet gav
        /// fyra mejl samtidigt till samma personer). <paramref name="where"/> är föreningens eller banans namn, ren text.
        /// </summary>
        public static string Subject(IReadOnlyList<(string Title, DateTime Due)> items, DateTime today, string where)
        {
            var overdue = items.Any(i => i.Due.Date < today.Date);
            if (items.Count == 1) return (overdue ? "Försenat: " : "Påminnelse: ") + items[0].Title;
            return (overdue ? "Försenat och kommande: " : "Påminnelse: ") + $"{items.Count} uppgifter för {where}";
        }

        /// <summary>"senast 2026-12-31 (om 71 dagar)", "i dag 2026-10-21" eller "skulle ha varit klart 2026-10-15 (för 6 dagar sedan)".</summary>
        public static string WhenText(DateTime due, DateTime today)
        {
            var days = (due.Date - today.Date).Days;
            if (days < 0) return $"skulle ha varit klart {due:yyyy-MM-dd} (för {-days} {(days == -1 ? "dag" : "dagar")} sedan)";
            if (days == 0) return $"i dag, {due:yyyy-MM-dd}";
            return $"senast {due:yyyy-MM-dd} (om {days} {(days == 1 ? "dag" : "dagar")})";
        }

        /// <summary>
        /// Mejlets stycken. Titlarna och <paramref name="fromHtml"/> är redan HTML-kodade av anroparen;
        /// <paramref name="fromHtml"/> är t.ex. "årshjulet för Haaplinge GoAss" eller "skjutbanan Änganäs".
        ///
        /// <para>⚠️ Säger vad man GÖR när det är klart. Första svepet visade att ingen förstått att en betald punkt
        /// skulle bockas av — den stod kvar som försenad.</para>
        /// </summary>
        public static List<string> Paragraphs(IReadOnlyList<(string Title, DateTime Due)> items, DateTime today, string fromHtml, bool fromRange)
        {
            var sorted = items.OrderBy(i => i.Due).ToList();
            var list = new List<string>();
            if (sorted.Count == 1)
                list.Add($"<strong>{sorted[0].Title}</strong> ska vara klart {WhenText(sorted[0].Due, today)}.");
            else
                list.Add("Det här ska vara klart:<br>" + string.Join("<br>",
                    sorted.Select(i => $"• <strong>{i.Title}</strong> — {WhenText(i.Due, today)}")));
            list.Add(fromRange
                ? "När besiktningen är gjord eller ett nytt tillstånd har kommit: registrera det på banans sida, så flyttas nästa datum och påminnelsen."
                : (sorted.Count == 1 ? "Är det redan gjort? " : "Är något redan gjort? ")
                  + "Bocka av det i årshjulet — då står det inte kvar som försenat, och resten av styrelsen ser att det är klart.");
            list.Add($"Påminnelsen kommer från {fromHtml} på pistol.nu. Varje uppgift påminns det om en gång, {DaysBefore} dagar före.");
            return list;
        }
    }
}
