using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HpskSite.Models.Staffing
{
    public enum PersonWeekKind
    {
        /// <summary>Ett uppdrag (StaffAssignment) i en planering.</summary>
        Assignment = 0,
        /// <summary>Personens egen start (startlista/patrull).</summary>
        Start = 1,
        /// <summary>"Jag tänker tävla den dagen" — saknar tid, bara en dag.</summary>
        Intent = 2,
    }

    /// <summary>
    /// En rad i personens vecka. <see cref="SourceKey"/> är radens stabila identitet (uppdragets id, startens nyckel) — den
    /// som hindrar att samma rad räknas två gånger. <see cref="PlanningId"/> och <see cref="CompetitionId"/> är bägge
    /// valfria: det gemensamma ligger i en planering utan tävling, en egen start har en tävling men ingen planering.
    /// </summary>
    public sealed record PersonWeekEntry(
        string SourceKey,
        PersonWeekKind Kind,
        DateTime Date,
        StaffInterval? Interval,
        string? Place,
        string Label,
        int? PlanningId = null,
        int? CompetitionId = null,
        string? Title = null,
        string? Part = null,
        string? Status = null);

    /// <param name="GapMinutes">Vid <see cref="ClashKind.TightTravel"/>: minuterna mellan passen — så att "kort tid mellan platserna" kan säga hur kort.</param>
    public sealed record PersonWeekClash(string WithSourceKey, string WithLabel, ClashKind Kind, int? GapMinutes = null)
    {
        /// <summary>Bara en överlappning är en krock; trång förflyttning och avsikt är varningar.</summary>
        public bool IsHard => Kind == ClashKind.Overlap;
    }

    public sealed record PersonWeekItem(PersonWeekEntry Entry, IReadOnlyList<PersonWeekClash> Clashes)
    {
        public bool HasClash => Clashes.Any(c => c.IsHard);
        public bool HasWarning => Clashes.Count > 0;
        /// <summary>Passet slutar efter midnatt — raden står på startdagen och visas som "fortsätter" nästa dag.</summary>
        public bool EndsNextDay => Entry.Interval is { } i && i.CrossesMidnight;
    }

    public sealed record PersonWeekDay(DateTime Date, IReadOnlyList<PersonWeekItem> Items, IReadOnlyList<PersonWeekEntry> CarriedIn);

    /// <summary>
    /// Expertgruppen 1c (K5): <b>en läsmodell för medlemmens vecka</b>, per datum över alla planeringar och tävlingar i
    /// familjen. Personvyn (steg 3), Mitt schema, hubben, kalenderfilen och påminnelserna (ett mejl per person och dag, B9)
    /// ska alla läsa den här formen — ingen av dem bygger en egen. Ren funktion; ingen databas, inget klockslag att hämta.
    /// <list type="bullet">
    /// <item><b>Varje rad exakt en gång</b>: en rad står på sin startdag och bara där (en rad med samma
    /// <see cref="PersonWeekEntry.SourceKey"/> räknas en gång). Ett pass över midnatt visas nästa dag i
    /// <see cref="PersonWeekDay.CarriedIn"/>, aldrig som en andra rad.</item>
    /// <item><b>Krockar</b> kommer ur <see cref="StaffInterval.Between"/>: överlappning, trång förflyttning mellan platser.
    /// Ett okänt intervall krockar aldrig. Två rader i samma planering räknas också — rutnätet visar dem bara inte åt personen.</item>
    /// <item><b>Avsikt</b> (jag tänker tävla) varnar mot uppdrag samma dag — men inte i en tävling där personen redan har
    /// en start; då är det starten som gäller (samma regel som <see cref="FamilyClashRules"/>).</item>
    /// </list>
    /// </summary>
    public static class PersonWeek
    {
        private static readonly StringComparer Sv = StringComparer.Create(CultureInfo.GetCultureInfo("sv-SE"), true);

        /// <summary>Veckans dagar från <paramref name="from"/> till och med <paramref name="to"/> — dagar utan rader utelämnas.</summary>
        public static List<PersonWeekDay> Build(IEnumerable<PersonWeekEntry> entries, DateTime from, DateTime to, TimeSpan? travelMargin = null)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var all = new List<PersonWeekEntry>();
            foreach (var e in entries)
            {
                // Samma rad två gånger (t.ex. läst via både tävlingen och planeringen) är fortfarande en rad.
                if (!string.IsNullOrEmpty(e.SourceKey) && !seen.Add(e.SourceKey)) continue;
                all.Add(e with { Date = e.Date.Date });
            }

            var inRange = all.Where(e => e.Date >= from.Date && e.Date <= to.Date).ToList();
            var starts = all.Where(e => e.Kind == PersonWeekKind.Start).ToList();

            var clashes = inRange.Select(_ => new List<PersonWeekClash>()).ToArray();

            for (var i = 0; i < inRange.Count; i++)
                for (var j = i + 1; j < inRange.Count; j++)
                {
                    var a = inRange[i];
                    var b = inRange[j];
                    var kind = Compare(a, b, starts, travelMargin);
                    if (kind == ClashKind.None) continue;
                    int? gap = kind == ClashKind.TightTravel && a.Interval is { } x && b.Interval is { } y ? (int)Math.Round(x.GapTo(y).TotalMinutes) : null;
                    clashes[i].Add(new PersonWeekClash(b.SourceKey, b.Label, kind, gap));
                    clashes[j].Add(new PersonWeekClash(a.SourceKey, a.Label, kind, gap));
                }

            // Index, inte radens värde: två rader utan nyckel kan vara lika och ska ändå vara två.
            return inRange
                .Select((e, idx) => (Entry: e, Clashes: clashes[idx]))
                .GroupBy(x => x.Entry.Date)
                .OrderBy(g => g.Key)
                .Select(g => new PersonWeekDay(
                    g.Key,
                    g.OrderBy(x => x.Entry.Interval?.Start ?? DateTime.MaxValue)
                        .ThenBy(x => x.Entry.Label, Sv)
                        .ThenBy(x => x.Entry.SourceKey, StringComparer.Ordinal)
                        .Select(x => new PersonWeekItem(x.Entry, x.Clashes))
                        .ToList(),
                    // Passet som började kvällen före och pågår efter midnatt.
                    all.Where(e => e.Interval is { } iv && iv.CrossesMidnight && e.Date == g.Key.AddDays(-1))
                        .OrderBy(e => e.Interval!.Value.Start).ToList()))
                .ToList();
        }

        private static bool SameRole(PersonWeekEntry a, PersonWeekEntry b) =>
            !string.IsNullOrWhiteSpace(a.Title) && string.Equals(a.Title.Trim(), b.Title?.Trim(), StringComparison.OrdinalIgnoreCase);

        private static ClashKind Compare(PersonWeekEntry a, PersonWeekEntry b, List<PersonWeekEntry> starts, TimeSpan? margin)
        {
            var aIntent = a.Kind == PersonWeekKind.Intent;
            var bIntent = b.Kind == PersonWeekKind.Intent;

            if (aIntent || bIntent)
            {
                if (aIntent && bIntent) return ClashKind.None;
                var intent = aIntent ? a : b;
                var other = aIntent ? b : a;
                if (other.Kind != PersonWeekKind.Assignment || other.Date != intent.Date) return ClashKind.None;
                // Har personen en start i tävlingen gäller starten, inte avsikten.
                if (intent.CompetitionId is > 0 && starts.Any(s => s.CompetitionId == intent.CompetitionId)) return ClashKind.None;
                return ClashKind.Intent;
            }

            if (a.Interval is not { } ia || b.Interval is not { } ib) return ClashKind.None;
            // Bara intilliggande dagar kan mötas (midnatt); längre isär är det aldrig en krock.
            if (Math.Abs((a.Date - b.Date).TotalDays) > 1) return ClashKind.None;
            // Steg 4–6 (planerare, UX): sekretariat, kassa och tävlingsledning finns både i Gemensamt och per del (B8). Heldag i det
            // gemensamma mot SAMMA roll med tider i en del är samma jobb skrivet två gånger — inte en dubbelbokning. Räknades det som krock
            // fick varje funktionär en röd ruta från dag ett, och efter tre dagar läste ingen den. Olika roller krockar som förut.
            if (a.Kind == PersonWeekKind.Assignment && b.Kind == PersonWeekKind.Assignment && ia.AllDay != ib.AllDay && SameRole(a, b))
                return ClashKind.None;
            return StaffInterval.Between(ia, a.Place, ib, b.Place, margin);
        }
    }
}
