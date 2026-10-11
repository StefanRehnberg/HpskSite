using System.Globalization;
using NPoco;

namespace HpskSite.Models
{
    /// <summary>
    /// En händelses skjutbana (banans kalender, 2026-10-11).
    ///
    /// <para><b>⚠️ EGEN TABELL, inte en doctype-egenskap.</b> Händelserna är Umbraco-noder på main men
    /// SQL-rader på branchen <c>sajtstruktur</c>, och händelsens id BEVARAS vid flytten. En tabell
    /// nycklad på id:t fungerar därför oförändrad i båda lagringarna — och banans kalender kan fråga
    /// "vilka händelser är på bana X" med ett index i stället för att gå igenom trädet.</para>
    /// </summary>
    [TableName("ClubEventRange")]
    [PrimaryKey("EventId", AutoIncrement = false)]
    public class ClubEventRange
    {
        public int EventId { get; set; }
        public int RangeId { get; set; }
        /// <summary>Bana/vall på skjutbanan. NULL = hela banan.</summary>
        public int? RangeSectionId { get; set; }
        public int? UpdatedByMemberId { get; set; }
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>
    /// En bokning på banan för någon som inte använder pistol.nu — lagd av banans förvaltare. Utan den
    /// är banans kalender bara sann för de klubbar som råkar vara här.
    /// </summary>
    [TableName("RangeExternalBooking")]
    [PrimaryKey("Id", AutoIncrement = true)]
    public class RangeExternalBooking
    {
        public int Id { get; set; }
        public int RangeId { get; set; }
        public int? RangeSectionId { get; set; }
        public DateTime Date { get; set; }
        /// <summary>"HH:mm". NULL = hela dagen.</summary>
        public string? StartTime { get; set; }
        public string? EndTime { get; set; }
        public string Title { get; set; } = "";
        public string? Note { get; set; }
        public bool IsActive { get; set; } = true;
        public int CreatedByMemberId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    /// <summary>Sorterna i banans kalender. Strängar som går ut till klienten oförändrade.</summary>
    public static class RangeCalendarKind
    {
        public const string Competition = "competition";
        public const string Training = "training";
        public const string Event = "event";
        /// <summary>En klubbs fasta tid på banan (ClubRangeAllocation), utlagd per dag.</summary>
        public const string Allocation = "allocation";
        public const string External = "external";
    }

    /// <summary>En post i banans kalender — samma form oavsett källa.</summary>
    public class RangeCalendarItem
    {
        public string Kind { get; set; } = "";
        /// <summary>Källans id (tävlingsnod, träning, händelsenod, fast tid, extern bokning).</summary>
        public int SourceId { get; set; }
        public string Title { get; set; } = "";
        /// <summary>Klubben som står för posten. 0 = kretsen, förvaltaren eller okänd.</summary>
        public int ClubId { get; set; }
        /// <summary>Kortnamnet på den som står för posten (klubb, krets eller "Extern").</summary>
        public string OwnerName { get; set; } = "";
        /// <summary>"yyyy-MM-dd".</summary>
        public string Date { get; set; } = "";
        /// <summary>"HH:mm" eller null (hela dagen / okänd tid).</summary>
        public string? Start { get; set; }
        public string? End { get; set; }
        public int? SectionId { get; set; }
        public string? SectionLabel { get; set; }
        public string? Url { get; set; }
        public bool IsCancelled { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>
    /// Banans regler som rena funktioner: tid, vall och krock. Ingen databas, så de går att pröva.
    ///
    /// <para><b>Krock är en VARNING, aldrig en spärr.</b> pistol.nu ser bara det som läggs in här —
    /// en förening som inte använder sajten syns inte — så en spärr hade gett ett falskt "ledigt"
    /// lika ofta som ett riktigt "upptaget".</para>
    /// </summary>
    public static class RangeCalendarRules
    {
        /// <summary>
        /// Delar två poster samma del av banan? <c>null</c> = hela banan, och hela banan delar allt.
        /// Två olika vallar krockar aldrig — det är just det som gör att två klubbar kan skjuta samtidigt.
        /// </summary>
        public static bool SectionsCollide(int? a, int? b)
            => a is not > 0 || b is not > 0 || a == b;

        /// <summary>Ett klockslag ("18:00", men också "18" eller "1830", se <see cref="ClockTime"/>) →
        /// minuter från midnatt. null för tomt eller oläsligt.</summary>
        public static int? Minutes(string? hhmm)
            => ClockTime.ToTimeSpan(hhmm) is TimeSpan t ? (int)t.TotalMinutes : null;

        /// <summary>
        /// Hur två poster SAMMA DAG förhåller sig i tid.
        /// <list type="bullet">
        /// <item><c>Overlap</c> — tiderna går bevisligen omlott (eller börjar samtidigt).</item>
        /// <item><c>SameDay</c> — samma dag men tiden räcker inte för att avgöra (minst en saknar
        /// klockslag). Visas som information, aldrig som krock.</item>
        /// <item><c>None</c> — tiderna går bevisligen inte omlott.</item>
        /// </list>
        /// ⚠️ <b>Ingen påhittad längd.</b> En post med starttid men utan sluttid tar inte "en timme" —
        /// den krockar bara om den börjar inne i den andra postens tid (samma regel som Mitt schema).
        /// Kant i kant (den ena slutar 18:00, den andra börjar 18:00) är INTE en krock.
        /// </summary>
        public static TimeRelation Relate(string? aStart, string? aEnd, string? bStart, string? bEnd)
        {
            var aS = Minutes(aStart); var aE = Minutes(aEnd);
            var bS = Minutes(bStart); var bE = Minutes(bEnd);
            if (aS == null || bS == null) return TimeRelation.SameDay;
            if (aS == bS) return TimeRelation.Overlap;
            if (aE != null && bE != null) return aS < bE && bS < aE ? TimeRelation.Overlap : TimeRelation.None;
            // Bara den ena har ett slut: krock om den andras START ligger inne i intervallet.
            if (aE != null) return bS > aS && bS < aE ? TimeRelation.Overlap : TimeRelation.None;
            if (bE != null) return aS > bS && aS < bE ? TimeRelation.Overlap : TimeRelation.None;
            // Två öppna poster med olika start: vi vet inte.
            return TimeRelation.SameDay;
        }

        /// <summary>
        /// En klubbs fasta tider utlagda på datumen i [from, to]. ISO-veckodag (1 = måndag). Giltighet
        /// (ValidFrom/ValidTo) respekteras; datumen räknas med.
        /// </summary>
        public static IEnumerable<(DateTime Date, ClubRangeAllocation Slot)> Expand(
            IEnumerable<ClubRangeAllocation> slots, DateTime from, DateTime to)
        {
            var list = slots.ToList();
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                int iso = ((int)d.DayOfWeek + 6) % 7 + 1;
                foreach (var s in list)
                {
                    if (s.DayOfWeek != iso) continue;
                    if (s.ValidFrom is { } vf && d < vf.Date) continue;
                    if (s.ValidTo is { } vt && d > vt.Date) continue;
                    yield return (d, s);
                }
            }
        }

        public static string Hhmm(TimeSpan t) => t.ToString(@"hh\:mm", CultureInfo.InvariantCulture);

        /// <summary>
        /// Vad på banan en incheckning just nu kan ge närvaro på (Michael, Luleå 2026-10-11: "banans QR
        /// aktiverar närvaro på aktiviteten"). Bara träningar och händelser — en tävling har sin egen
        /// incheckning, en fast tid och en extern bokning har ingen närvaro. Inställt räknas inte.
        /// Samma tidsfönster som kursens upprop (<see cref="Training.TrainingCourseRules.CheckInCounts"/>):
        /// en timme före start till en timme efter slut, så "incheckad" och "närvarande" aldrig säger emot
        /// varandra. Vem som FÅR registreras avgörs av anroparen.
        /// </summary>
        public static List<RangeCalendarItem> OngoingForCheckIn(IEnumerable<RangeCalendarItem> items, DateTime now)
        {
            var today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return items.Where(i => (i.Kind == RangeCalendarKind.Training || i.Kind == RangeCalendarKind.Event)
                                    && !i.IsCancelled && i.Date == today
                                    && Training.TrainingCourseRules.CheckInCounts(now, now.Date, i.Start, i.End))
                        .ToList();
        }
    }

    public enum TimeRelation { None, SameDay, Overlap }
}
